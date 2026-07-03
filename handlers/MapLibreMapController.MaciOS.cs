#if IOS || MACCATALYST
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using UIKit;
using System.Text.Json;
using System.Text.RegularExpressions;
using MapLibreNative.Maui;
using MapLibreNative.Maui.Handlers.Geometry;
using Map    = MapLibreNative.Maui.Handlers.Maps.Map;
using Style  = MapLibreNative.Maui.Handlers.Maps.Style;
using Location = Microsoft.Maui.Devices.Sensors.Location;

namespace MapLibreNative.Maui.Handlers;

// -- Container UIView --------------------------------------------------------

/// <summary>Simple container view; the MTKView rendered by the C++ backend is
/// inserted as a subview once the frontend is initialised.</summary>
[Register("MapContainerView")]
public sealed class MapContainerView : UIView
{
    public Action<int, int>? OnResized;

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();

        // Keep the Metal view (frame-based) filling the container;
        // skip views that use Auto Layout (TranslatesAutoresizingMaskIntoConstraints = false).
        foreach (var sv in Subviews)
            if (sv.TranslatesAutoresizingMaskIntoConstraints)
                sv.Frame = Bounds;

        var scale = UIScreen.MainScreen.Scale;
        int w = Math.Max(1, (int)(Bounds.Width  * scale));
        int h = Math.Max(1, (int)(Bounds.Height * scale));
        OnResized?.Invoke(w, h);
    }
}

// -- Controller ----------------------------------------------------------------

/// <summary>
/// iOS / Mac Catalyst IMapLibreMapController backed by mln-cabi (Metal frontend).
/// Platform view is a plain container UIView; the C++ Metal backend owns an MTKView
/// which is retrieved via GetNativeView() and added as a subview on first layout.
/// </summary>
public class MapLibreMapController : IMapLibreMapController
{
    // -- Layout property names (same set as Windows/Android) ------------------

    private static readonly HashSet<string> LayoutPropertyNames = new(StringComparer.Ordinal)
    {
        "visibility",
        "symbol-placement","symbol-spacing","symbol-avoid-edges","symbol-sort-key","symbol-z-order",
        "icon-allow-overlap","icon-ignore-placement","icon-optional","icon-rotation-alignment",
        "icon-size","icon-text-fit","icon-text-fit-padding","icon-image","icon-rotate",
        "icon-padding","icon-keep-upright","icon-offset","icon-anchor","icon-pitch-alignment",
        "text-pitch-alignment","text-rotation-alignment","text-field","text-font","text-size",
        "text-max-width","text-line-height","text-letter-spacing","text-justify",
        "text-radial-offset","text-variable-anchor","text-anchor","text-max-angle",
        "text-writing-mode","text-rotate","text-padding","text-keep-upright","text-transform",
        "text-offset","text-allow-overlap","text-ignore-placement","text-optional",
        "line-cap","line-join","line-miter-limit","line-round-limit","line-sort-key",
        "fill-sort-key","circle-sort-key",
    };

    // -- State -----------------------------------------------------------------

    private readonly string? _styleString;
    private readonly float   _pixelRatio;

    private MbglRunLoop?  _runLoop;
    private MbglFrontend? _frontend;
    private MbglMap?      _map;
    private MbglStyle?    _style;
    private bool          _styleReady;
    private UITextView    _attrView    = null!;  // expanded full text
    private UIButton      _attrButton  = null!;  // collapsed ⓘ button
    private bool          _showAttrControl  = true;
    private string?       _customAttribution;
    private int           _attrCollapseGen;       // generation counter for auto-collapse timer
    private bool          _attrLoaded;            // true once attribution content has been fetched

    // -- Navigation + GPS overlay controls -------------------------------------

    private const float OverlayMargin = 8f;
    private const float OverlayGap    = 8f;
    private const float OverlayBtn    = 44f;

    private UIStackView _navPanel     = null!;   // d-pad / zoom-in / zoom-out
    private UIStackView _gpsPanel     = null!;   // tracking / bearing-reset
    private UIView      _navNorthTick = null!;   // rotates with map bearing
    private UIButton    _gpsTracking  = null!;   // reflects tracking mode
    private UIButton    _gpsBearing   = null!;

    private readonly List<NSLayoutConstraint> _overlayConstraints = new();

    private bool _showNavControls = true;
    private bool _showGpsControl  = true;

    private MapControlCorner _navCorner  = MapControlCorner.TopRight;
    private MapControlCorner _gpsCorner  = MapControlCorner.TopRight;
    private MapControlCorner _attrCorner = MapControlCorner.BottomLeft;

    // GPS tracking state (fixes are fed externally via UpdateGpsLocation)
    private enum GpsTrackingMode { Off, Show, Follow, FollowBearing }
    private GpsTrackingMode _gpsMode = GpsTrackingMode.Off;
    private double _lastGpsLat, _lastGpsLon;
    private float  _lastGpsBearing;
    private float  _lastGpsAccuracy = 10f;
    private bool   _hasGpsFix;

    // Location indicator puck (portable style layer, shared with Windows impl)
    private const string LocIndLayerId = "__mln_location_indicator";
    private MbglLayer? _locIndLayer;
    private readonly record struct LocIndParams(double Lat, double Lon, float Bearing, float AccuracyM);
    private LocIndParams? _pendingLocInd;

    public MapContainerView View { get; }

    // -- Events ----------------------------------------------------------------

    public event Action<Map>?                OnMapReadyReceived;
    public event Action?                     OnDidBecomeIdleReceived;
    public event Action<int>?                OnCameraMoveStartedReceived;
    public event Action?                     OnCameraMoveReceived;
    public event Action?                     OnCameraIdleReceived;
    public event Action<int>?                OnCameraTrackingChangedReceived;
    public event Action?                     OnCameraTrackingDismissedReceived;
    public event Func<LatLng, double, double, bool>?         OnMapClickReceived;
    public event Func<LatLng, double, double, bool>?         OnMapLongClickReceived;
    public event Action<Style>?              OnStyleLoadedReceived;
    public event Action<Location>?           OnUserLocationUpdateReceived;
    public event Action<string>?             OnDidFailLoadingMapReceived;
    public event Action<string>?             OnStyleImageMissingReceived;
    public event Action<string>?             OnRenderErrorReceived;

    // -- Construction ----------------------------------------------------------

    public MapLibreMapController(float pixelRatio, string? styleString)
    {
        _pixelRatio  = pixelRatio;
        _styleString = styleString;

        View = new MapContainerView { OnResized = OnViewResized };

        // Attribution overlay — bottom-right corner, OSM licence compliance.
        _attrView = new UITextView
        {
            BackgroundColor  = UIColor.FromRGBA(255, 255, 255, 180),
            Editable         = false,
            ScrollEnabled    = false,
            Selectable       = true,
            Hidden           = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _attrView.TextContainerInset = new UIEdgeInsets(3, 6, 3, 6);
        _attrView.Font = UIFont.SystemFontOfSize(11f);
        _attrView.Layer.CornerRadius = 4f;
        View.AddSubview(_attrView);
        // Width cap is corner-independent; positional anchors are set in RepositionOverlays.
        _attrView.WidthAnchor.ConstraintLessThanOrEqualTo(View.WidthAnchor, 0.9f).Active = true;

        // Collapsed ⓘ button — same corner, shown when full text is hidden
        _attrButton = new UIButton(UIButtonType.System);
        _attrButton.SetTitle("ⓘ", UIControlState.Normal);
        _attrButton.BackgroundColor = UIColor.FromRGBA(255, 255, 255, 180);
        _attrButton.SetTitleColor(UIColor.FromRGBA(50, 50, 50, 220), UIControlState.Normal);
        _attrButton.TitleLabel!.Font = UIFont.SystemFontOfSize(13f);
        _attrButton.Layer.CornerRadius = 4f;
        _attrButton.Hidden = true;
        _attrButton.TranslatesAutoresizingMaskIntoConstraints = false;
        _attrButton.TouchUpInside += (_, _) => ExpandAttribution();
        View.AddSubview(_attrButton);

        // Navigation + GPS overlay panels (custom native views — mln-cabi does
        // not expose the platform SDK's built-in controls).
        BuildNavigationPanel();
        BuildGpsPanel();

        RepositionOverlays();
    }

    // -- Navigation + GPS panel construction -----------------------------------

    private void BuildNavigationPanel()
    {
        _navPanel = MakeOverlayPanel();

        var dpad = BuildDpad();

        var zoomIn = MakeOverlayButton("\uFF0B");   // ＋
        zoomIn.TouchUpInside += (_, _) => ZoomBy(1);
        var zoomOut = MakeOverlayButton("\uFF0D");  // －
        zoomOut.TouchUpInside += (_, _) => ZoomBy(-1);

        _navPanel.AddArrangedSubview(dpad);
        _navPanel.AddArrangedSubview(zoomIn);
        _navPanel.AddArrangedSubview(zoomOut);
        _navPanel.Hidden = !_showNavControls;
        View.AddSubview(_navPanel);
    }

    /// <summary>
    /// Builds the round rotate/pitch d-pad: up/down tilt the map (±10°, clamped
    /// 0–60°), left/right rotate it (±15°), the centre resets north, and a hollow
    /// ring around the arrows carries a blue north tick that tracks the bearing.
    /// </summary>
    private UIView BuildDpad()
    {
        float s    = OverlayBtn;
        float cell = s / 3f;

        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.WidthAnchor.ConstraintEqualTo(s).Active  = true;
        host.HeightAnchor.ConstraintEqualTo(s).Active = true;

        UIButton Arrow(string? title, Action onTap, float cx, float cy)
        {
            var b = new UIButton(UIButtonType.System)
            {
                Frame = new CoreGraphics.CGRect(cx - cell / 2, cy - cell / 2, cell, cell),
            };
            if (title != null)
            {
                b.SetTitle(title, UIControlState.Normal);
                b.SetTitleColor(UIColor.FromRGBA((byte)40, (byte)40, (byte)40, (byte)230), UIControlState.Normal);
                b.TitleLabel!.Font = UIFont.SystemFontOfSize(9f);
            }
            b.TouchUpInside += (_, _) => onTap();
            return b;
        }

        host.AddSubview(Arrow("\u25B2", () => PitchBy(10),  s / 2,        cell / 2));    // ▲ up
        host.AddSubview(Arrow("\u25BC", () => PitchBy(-10), s / 2,        s - cell / 2)); // ▼ down
        host.AddSubview(Arrow("\u25C0", () => RotateBy(-15), cell / 2,    s / 2));       // ◀ left
        host.AddSubview(Arrow("\u25B6", () => RotateBy(15), s - cell / 2, s / 2));       // ▶ right
        host.AddSubview(Arrow(null,      ResetNorth,        s / 2,        s / 2));       // centre reset

        // Hollow compass ring (non-interactive).
        float ring = s * 0.80f;
        var ringView = new UIView(new CoreGraphics.CGRect((s - ring) / 2, (s - ring) / 2, ring, ring))
        {
            UserInteractionEnabled = false,
        };
        ringView.Layer.BorderWidth  = 1f;
        ringView.Layer.BorderColor  = UIColor.FromRGBA((byte)204, (byte)204, (byte)204, (byte)255).CGColor;
        ringView.Layer.CornerRadius = ring / 2f;
        host.AddSubview(ringView);

        // North tick — a short blue mark at the top of the ring that rotates with bearing.
        _navNorthTick = new UIView(new CoreGraphics.CGRect(0, 0, s, s)) { UserInteractionEnabled = false };
        var tick = new UIView(new CoreGraphics.CGRect(s / 2f - 1, (s - ring) / 2, 2, s * 0.14f))
        {
            BackgroundColor = UIColor.FromRGBA((byte)10, (byte)102, (byte)204, (byte)255),
        };
        _navNorthTick.AddSubview(tick);
        host.AddSubview(_navNorthTick);

        return host;
    }

    private void BuildGpsPanel()
    {
        _gpsPanel = MakeOverlayPanel();

        _gpsTracking = MakeOverlayButton("\u25CB");  // ○
        _gpsTracking.TouchUpInside += (_, _) => CycleGpsMode();
        _gpsBearing = MakeOverlayButton("\u21BA");   // ↺
        _gpsBearing.TouchUpInside += (_, _) => ResetNorth();

        _gpsPanel.AddArrangedSubview(_gpsTracking);
        _gpsPanel.AddArrangedSubview(_gpsBearing);
        _gpsPanel.Hidden = !_showGpsControl;
        View.AddSubview(_gpsPanel);

        RefreshGpsIcons();
    }

    private static UIStackView MakeOverlayPanel()
    {
        var panel = new UIStackView
        {
            Axis         = UILayoutConstraintAxis.Vertical,
            Distribution = UIStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        panel.BackgroundColor    = UIColor.FromRGBA(255, 255, 255, 230);
        panel.Layer.CornerRadius = 4f;
        panel.ClipsToBounds      = true;
        return panel;
    }

    private static UIButton MakeOverlayButton(string title)
    {
        var b = new UIButton(UIButtonType.System);
        b.SetTitle(title, UIControlState.Normal);
        b.SetTitleColor(UIColor.FromRGBA(40, 40, 40, 230), UIControlState.Normal);
        b.TitleLabel!.Font = UIFont.SystemFontOfSize(20f);
        b.TranslatesAutoresizingMaskIntoConstraints = false;
        b.WidthAnchor.ConstraintEqualTo(OverlayBtn).Active  = true;
        b.HeightAnchor.ConstraintEqualTo(OverlayBtn).Active = true;
        return b;
    }

    /// <summary>Anchors nav / GPS / attribution overlays to their corners and
    /// applies vertical stacking when several share a corner.</summary>
    private void RepositionOverlays()
    {
        if (_overlayConstraints.Count > 0)
        {
            NSLayoutConstraint.DeactivateConstraints(_overlayConstraints.ToArray());
            _overlayConstraints.Clear();
        }

        float navH = OverlayBtn * 3;
        float gpsH = OverlayBtn * 2;
        bool navVis = _showNavControls;
        bool gpsVis = _showGpsControl;

        float StackOffset(MapControlCorner c, int idx)
        {
            float off = 0;
            if (idx > 0 && navVis && _navCorner == c) off += navH + OverlayGap;
            if (idx > 1 && gpsVis && _gpsCorner == c) off += gpsH + OverlayGap;
            return off;
        }

        void Anchor(UIView v, MapControlCorner c, float off)
        {
            bool left = c is MapControlCorner.TopLeft or MapControlCorner.BottomLeft;
            bool top  = c is MapControlCorner.TopLeft or MapControlCorner.TopRight;

            var hz = left
                ? v.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor, OverlayMargin)
                : v.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -OverlayMargin);
            var vt = top
                ? v.TopAnchor.ConstraintEqualTo(View.TopAnchor, OverlayMargin + off)
                : v.BottomAnchor.ConstraintEqualTo(View.BottomAnchor, -(OverlayMargin + off));

            hz.Active = true;
            vt.Active = true;
            _overlayConstraints.Add(hz);
            _overlayConstraints.Add(vt);
        }

        Anchor(_navPanel,   _navCorner,  StackOffset(_navCorner, 0));
        Anchor(_gpsPanel,   _gpsCorner,  StackOffset(_gpsCorner, 1));
        Anchor(_attrView,   _attrCorner, StackOffset(_attrCorner, 2));
        Anchor(_attrButton, _attrCorner, StackOffset(_attrCorner, 2));
    }

    // -- View size -------------------------------------------------------------

    private void OnViewResized(int w, int h)
    {
        if (_frontend == null)
        {
            TryInitialize(w, h);
            return;
        }
        _frontend.SetSize(w, h);
        _map?.SetSize(w, h);
        _map?.TriggerRepaint();
    }

    private void TryInitialize(int w, int h)
    {
        if (_frontend != null || w < 1 || h < 1) return;

        _runLoop  = new MbglRunLoop();
        // surface_handle unused on Apple (Metal backend creates its own MTKView).
        _frontend = new MbglFrontend(
            IntPtr.Zero,
            IntPtr.Zero,
            w, h, _pixelRatio, OnRender);

        // Wire the MTKView (created by the C++ backend) into the container view.
        var nativeViewPtr = _frontend.GetNativeView();
        if (nativeViewPtr != IntPtr.Zero)
        {
            var metalView = ObjCRuntime.Runtime.GetNSObject<UIView>(nativeViewPtr)!;
            metalView.Frame = View.Bounds;
            View.InsertSubview(metalView, 0);
        }
        // Keep the attribution overlays on top of the metal view.
        View.BringSubviewToFront(_attrView);
        View.BringSubviewToFront(_attrButton);
        View.BringSubviewToFront(_navPanel);
        View.BringSubviewToFront(_gpsPanel);

        _map = new MbglMap(_frontend, _runLoop,
                           pixelRatio: _pixelRatio,
                           observer: OnMapObserverEvent);
        _map.SetSize(w, h);

        if (!string.IsNullOrEmpty(_styleString))
        {
            if (_styleString!.StartsWith('{')) _map.SetStyleJson(_styleString);
            else                               _map.SetStyleUrl(_styleString);
        }

        OnMapReadyReceived?.Invoke(new Map(null));
    }

    private void OnRender()
    {
        // Dispatch to main thread � Metal command buffers must be committed there.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _frontend?.Render();
            _runLoop?.RunOnce();
        });
    }

    private void OnMapObserverEvent(string eventName, string? detail)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (eventName)
            {
                case "onDidFinishLoadingStyle":
                    _styleReady = true;
                    _style = _map?.GetStyle();
                    _attrLoaded = false;  // new style — sources may have different attribution
                    RefreshAttribution();
                    _locIndLayer = null;               // layer belongs to the old style
                    if (_pendingLocInd.HasValue) ApplyPendingLocationIndicator();
                    OnStyleLoadedReceived?.Invoke(new Style(null));
                    break;
                case "onDidBecomeIdle":
                    // TileJSON sources may finish loading after onDidFinishLoadingStyle;
                    // only retry while we still have no content.
                    if (!_attrLoaded) RefreshAttribution();
                    OnDidBecomeIdleReceived?.Invoke();
                    break;
                case "onCameraIsChanging":
                    CollapseAttribution();
                    UpdateCompassRotation();
                    OnCameraMoveReceived?.Invoke();
                    break;
                case "onCameraDidChange":
                    UpdateCompassRotation();
                    OnCameraIdleReceived?.Invoke();
                    break;
                case "onDidFailLoadingMap":
                    OnDidFailLoadingMapReceived?.Invoke(detail ?? string.Empty);
                    break;
                case "onStyleImageMissing":
                    OnStyleImageMissingReceived?.Invoke(detail ?? string.Empty);
                    break;
                case "onRenderError":
                    System.Diagnostics.Debug.WriteLine($"[MapLibre.iOS] render error: {detail}");
                    OnRenderErrorReceived?.Invoke(detail ?? string.Empty);
                    break;
            }
        });
    }

    // -- IMapLibreMapOptionsSink -----------------------------------------------

    public void SetStyleString(string styleString)
    {
        if (_map == null) return;
        if (styleString.StartsWith('{')) _map.SetStyleJson(styleString);
        else                             _map.SetStyleUrl(styleString);
    }

    public void SetMinMaxZoomPreference(double? min, double? max)
    {
        if (min.HasValue) _map?.SetMinZoom(min.Value);
        if (max.HasValue) _map?.SetMaxZoom(max.Value);
    }

    public void SetCameraTargetBounds(LatLngBounds bounds,
        double minZoom = double.NaN, double maxZoom = double.NaN,
        double minPitch = double.NaN, double maxPitch = double.NaN)
    {
        _map?.SetBounds(bounds.SouthWest.Latitude, bounds.SouthWest.Longitude,
                        bounds.NorthEast.Latitude, bounds.NorthEast.Longitude,
                        minZoom, maxZoom, minPitch, maxPitch);
    }
    public void SetCompassEnabled(bool v)                     { }
    public void SetRotateGesturesEnabled(bool v)              { }
    public void SetScrollGesturesEnabled(bool v)              { }
    public void SetTiltGesturesEnabled(bool v)                { }
    public void SetTrackCameraPosition(bool v)                { }
    public void SetZoomGesturesEnabled(bool v)                { }
    public void SetMyLocationEnabled(bool v)                  { }
    public void SetMyLocationTrackingMode(int v)              { }
    public void SetMyLocationRenderMode(int v)                { }
    public void SetLogoViewMargins(int x, int y)              { }
    public void SetCompassGravity(int gravity)                { }
    public void SetCompassViewMargins(int x, int y)           { }
    public void SetAttributionButtonGravity(int v)            { }
    public void SetAttributionButtonMargins(int x, int y)     { }
    public void SetShowNavigationControls(bool show)
    {
        _showNavControls = show;
        if (_navPanel != null) _navPanel.Hidden = !show;
        RepositionOverlays();
    }

    public void SetShowGpsControl(bool show)
    {
        _showGpsControl = show;
        if (_gpsPanel != null) _gpsPanel.Hidden = !show;
        RepositionOverlays();
    }

    public void SetNavigationControlPosition(MapControlCorner corner)
    {
        if (_navCorner == corner) return;
        _navCorner = corner;
        RepositionOverlays();
    }

    public void SetGpsControlPosition(MapControlCorner corner)
    {
        if (_gpsCorner == corner) return;
        _gpsCorner = corner;
        RepositionOverlays();
    }

    public void SetAttributionControlPosition(MapControlCorner corner)
    {
        if (_attrCorner == corner) return;
        _attrCorner = corner;
        RepositionOverlays();
    }

    public void UpdateGpsLocation(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        _lastGpsLat      = lat;
        _lastGpsLon      = lon;
        _lastGpsBearing  = bearing;
        _lastGpsAccuracy = Math.Max(5f, accuracyMeters);
        bool isFirstFix  = !_hasGpsFix;
        _hasGpsFix       = true;

        _pendingLocInd = new LocIndParams(lat, lon, bearing, _lastGpsAccuracy);

        if (_gpsMode is GpsTrackingMode.Follow or GpsTrackingMode.FollowBearing && _map != null)
        {
            bool useBearing   = _gpsMode == GpsTrackingMode.FollowBearing;
            double zoom       = _map.Zoom < 8 ? 14 : _map.Zoom;
            double camBearing = useBearing ? bearing : _map.Bearing;
            if (isFirstFix) _map.JumpTo(lat, lon, zoom, camBearing, _map.Pitch);
            else            _map.EaseTo(lat, lon, _map.Zoom, camBearing, _map.Pitch, durationMs: 200);
        }

        if (_styleReady && _style != null)
            ApplyPendingLocationIndicator();
        _map?.TriggerRepaint();
    }

    // -- Navigation + GPS behaviour --------------------------------------------

    private void ZoomBy(double delta)
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom + delta, _map.Bearing, _map.Pitch, durationMs: 200);
    }

    private void ResetNorth()
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom, 0, _map.Pitch, durationMs: 200);
    }

    /// <summary>Rotate the map by <paramref name="deltaDeg"/> (positive = clockwise).</summary>
    private void RotateBy(double deltaDeg)
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom, _map.Bearing + deltaDeg, _map.Pitch, durationMs: 200);
    }

    /// <summary>Tilt the map by <paramref name="deltaDeg"/>, clamped to 0–60°.</summary>
    private void PitchBy(double deltaDeg)
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        double newPitch = Math.Max(0, Math.Min(60, _map.Pitch + deltaDeg));
        _map.EaseTo(lat, lon, _map.Zoom, _map.Bearing, newPitch, durationMs: 200);
    }

    private void CycleGpsMode()
    {
        _gpsMode = _gpsMode switch
        {
            GpsTrackingMode.Off    => GpsTrackingMode.Show,
            GpsTrackingMode.Show   => GpsTrackingMode.Follow,
            GpsTrackingMode.Follow => GpsTrackingMode.FollowBearing,
            _                      => GpsTrackingMode.Off,
        };
        RefreshGpsIcons();

        if (_gpsMode == GpsTrackingMode.Off)
        {
            ClearLocationIndicator();
            return;
        }
        if (_hasGpsFix)
            UpdateGpsLocation(_lastGpsLat, _lastGpsLon, _lastGpsBearing, _lastGpsAccuracy);
    }

    private void RefreshGpsIcons()
    {
        if (_gpsTracking == null) return;
        (string icon, byte r, byte g, byte b) = _gpsMode switch
        {
            GpsTrackingMode.Show          => ("\u2299", (byte)30,  (byte)136, (byte)229), // ⊙ blue
            GpsTrackingMode.Follow        => ("\u25CE", (byte)21,  (byte)101, (byte)192), // ◎ deep blue
            GpsTrackingMode.FollowBearing => ("\u27A4", (byte)245, (byte)124, (byte)0),   // ➤ orange
            _                             => ("\u25CB", (byte)120, (byte)120, (byte)120), // ○ gray
        };
        _gpsTracking.SetTitle(icon, UIControlState.Normal);
        _gpsTracking.SetTitleColor(UIColor.FromRGBA(r, g, b, (byte)255), UIControlState.Normal);
    }

    /// <summary>Rotates the compass north tick to reflect the current map bearing.</summary>
    private void UpdateCompassRotation()
    {
        if (_navNorthTick == null || _map == null) return;
        _navNorthTick.Transform = CGAffineTransform.MakeRotation(-(float)(_map.Bearing * Math.PI / 180.0));
    }

    private void ApplyPendingLocationIndicator()
    {
        if (_pendingLocInd == null || _style == null || _gpsMode == GpsTrackingMode.Off) return;
        var p  = _pendingLocInd.Value;
        var ic = System.Globalization.CultureInfo.InvariantCulture;

        if (_locIndLayer == null)
        {
            if (_style.HasLayer(LocIndLayerId)) _style.RemoveLayer(LocIndLayerId);
            _locIndLayer = _style.AddLocationIndicatorLayer(LocIndLayerId);
            _locIndLayer.SetPaintProperty("accuracy-radius-color", "\"rgba(30,136,229,0.3)\"");
            _locIndLayer.SetPaintProperty("accuracy-radius-border-color", "\"rgba(30,136,229,0.85)\"");
        }

        bool showBearing = _gpsMode == GpsTrackingMode.FollowBearing;
        _locIndLayer.SetPaintProperty("location",
            $"[{p.Lat.ToString(ic)},{p.Lon.ToString(ic)},0]");
        _locIndLayer.SetPaintProperty("bearing",
            (showBearing ? p.Bearing : 0f).ToString(ic));
        _locIndLayer.SetPaintProperty("accuracy-radius", p.AccuracyM.ToString(ic));
    }

    private void ClearLocationIndicatorInternal()
    {
        _pendingLocInd = null;
        _locIndLayer   = null;
        if (_styleReady && _style?.HasLayer(LocIndLayerId) == true)
            _style.RemoveLayer(LocIndLayerId);
        _map?.TriggerRepaint();
    }

    public void SetShowAttributionControl(bool show, string? customAttribution)
    {
        _showAttrControl   = show;
        _customAttribution = customAttribution;
        RefreshAttribution();
    }

    // -- Attribution -----------------------------------------------------------

    private void RefreshAttribution()
    {
        if (_style == null)
        {
            _attrView.Hidden   = true;
            _attrButton.Hidden = true;
            return;
        }

        var parts = new System.Collections.Generic.List<string>(_style.GetSourceAttributions());
        if (!string.IsNullOrWhiteSpace(_customAttribution))
            parts.Add(_customAttribution!);
        var attributions = MbglStyle.EnsureMapLibreAttribution(parts);

        if (attributions.Count == 0 || !_showAttrControl)
        {
            _attrView.Hidden   = true;
            _attrButton.Hidden = true;
            return;
        }

        _attrLoaded = true;
        _attrView.AttributedText = BuildAttributionAttributedString(attributions);
        ExpandAttribution();
    }

    private void ExpandAttribution()
    {
        if (!_showAttrControl || !_attrLoaded) return;
        _attrView.Hidden   = false;
        _attrButton.Hidden = true;
        ScheduleAutoCollapse();
    }

    private void CollapseAttribution()
    {
        // If neither view is showing, there is nothing to collapse.
        if (_attrView.Hidden && _attrButton.Hidden) return;
        ++_attrCollapseGen;  // cancel any pending auto-collapse
        _attrView.Hidden   = true;
        _attrButton.Hidden = !(_attrLoaded && _showAttrControl);
    }

    private void ScheduleAutoCollapse()
    {
        int gen = ++_attrCollapseGen;
        // Fire on the main thread after 5 s; generation counter prevents stale
        // callbacks from firing after ExpandAttribution was called again.
        Task.Delay(5000).ContinueWith(_ =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_attrCollapseGen == gen) CollapseAttribution();
            }));
    }

    private static NSAttributedString BuildAttributionAttributedString(
        System.Collections.Generic.IReadOnlyList<string> parts)
    {
        var result = new NSMutableAttributedString();
        var hrefRe = new Regex(
            @"<a\b[^>]*?href=[""']?([^""'\s>]+)[""']?[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var baseAttrs = new UIStringAttributes
        {
            ForegroundColor = UIColor.FromRGBA(50, 50, 50, 220),
        };
        var linkAttrs = new UIStringAttributes
        {
            ForegroundColor = UIColor.SystemBlue,
        };

        bool first = true;
        foreach (var part in parts)
        {
            if (!first) result.Append(new NSAttributedString(" | ", baseAttrs));
            first = false;

            int pos = 0;
            foreach (Match m in hrefRe.Matches(part))
            {
                if (m.Index > pos)
                    result.Append(new NSAttributedString(
                        DecodeHtmlEntities(StripHtmlTags(part[pos..m.Index])), baseAttrs));

                string href     = m.Groups[1].Value;
                string linkText = DecodeHtmlEntities(StripHtmlTags(m.Groups[2].Value));
                if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
                {
                    var la = new UIStringAttributes(linkAttrs.Dictionary.MutableCopy() as Foundation.NSMutableDictionary);
                    la.Link = new NSUrl(uri.AbsoluteUri);
                    result.Append(new NSAttributedString(linkText, la));
                }
                else
                {
                    result.Append(new NSAttributedString(linkText, baseAttrs));
                }
                pos = m.Index + m.Length;
            }
            if (pos < part.Length)
                result.Append(new NSAttributedString(
                    DecodeHtmlEntities(StripHtmlTags(part[pos..])), baseAttrs));
        }
        return result;
    }

    private static string StripHtmlTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var sb    = new System.Text.StringBuilder(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if      (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag)   sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string DecodeHtmlEntities(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('&')) return text;
        return text
            .Replace("&amp;",   "&")
            .Replace("&lt;",    "<")
            .Replace("&gt;",    ">")
            .Replace("&quot;",  "\"")
            .Replace("&#39;",   "'")
            .Replace("&nbsp;",  "\u00A0")
            .Replace("&copy;",  "\u00A9")
            .Replace("&reg;",   "\u00AE")
            .Replace("&trade;", "\u2122");
    }

    // -- Sources ---------------------------------------------------------------

    public void AddGeoJsonSource(string sourceName, string source)
    {
        if (!_styleReady || _style == null) return;
        if (_style.HasSource(sourceName)) return;
        var s = _style.AddGeoJsonSource(sourceName);
        s.SetGeoJson(source);
    }

    public void SetGeoJsonSource(string sourceName, string source)
        => AddGeoJsonSource(sourceName, source);

    public void SetGeoJsonFeature(string sourceName, string geojsonFeature)
    {
        if (!_styleReady || _style == null) return;
        if (_style.HasSource(sourceName)) _style.RemoveSource(sourceName);
        AddGeoJsonSource(sourceName, geojsonFeature);
    }

    public void AddRasterSource(string sourceName, string? tileUrl,
        string[]? tileUrlTemplates, int tileSize, int minZoom, int maxZoom)
    {
        if (!_styleReady || _style == null) return;
        var url = tileUrl ?? tileUrlTemplates?.FirstOrDefault();
        if (url != null) _style.AddRasterSource(sourceName, url, tileSize);
    }

    public void AddRasterDemSource(string sourceName, string? tileUrl,
        string[]? tileUrlTemplates, int tileSize, int minZoom, int maxZoom)
    {
        if (!_styleReady || _style == null) return;
        var url = tileUrl ?? tileUrlTemplates?.FirstOrDefault();
        if (url != null) _style.AddRasterDemSource(sourceName, url, tileSize);
    }

    public void AddVectorSource(string sourceName, string? tileUrl,
        string[]? tileUrlTemplates, int minZoom, int maxZoom)
    {
        if (!_styleReady || _style == null) return;
        var url = tileUrl ?? tileUrlTemplates?.FirstOrDefault();
        if (url != null) _style.AddVectorSource(sourceName, url);
    }

    public void AddImageSource(string sourceName, string url, LatLngQuad? coordinates)
    {
        if (!_styleReady || _style == null) return;
        _style.AddRasterSource(sourceName, url);
    }

    public void RemoveSource(string sourceId)
    {
        if (!_styleReady || _style == null) return;
        _style.RemoveSource(sourceId);
    }

    // -- Layers ----------------------------------------------------------------

    public void AddFillLayer(string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddFillLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddLineLayer(string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddLineLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddCircleLayer(string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddCircleLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddSymbolLayer(string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddSymbolLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddRasterLayer(string layerName, string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, string? belowLayerId = null)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddRasterLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, null, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddHillshadeLayer(string layerName, string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, string? belowLayerId = null)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddHillshadeLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, null, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddFillExtrusionLayer(string layerName, string sourceName,
        string? belowLayerId, string? sourceLayer,
        IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddFillExtrusionLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void AddHeatmapLayer(string layerName, string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, string? belowLayerId = null)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddHeatmapLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, null, minZoom, maxZoom);
        ApplyProperties(layer, properties);
    }

    public void RemoveLayer(string layerId)
    {
        if (!_styleReady || _style == null) return;
        _style.RemoveLayer(layerId);
    }

    public void AddSpriteImage(string imageId, int width, int height, byte[] rgba, float pixelRatio = 1f, bool sdf = false)
    {
        if (!_styleReady || _style == null) return;
        _style.AddImage(imageId, width, height, pixelRatio, sdf, rgba);
    }

    public void RemoveSpriteImage(string imageId)
    {
        if (!_styleReady || _style == null) return;
        _style.RemoveImage(imageId);
    }

    // -- Helpers ---------------------------------------------------------------

    private static void ApplyLayerMeta(MbglLayer layer, string? sourceLayer,
        float minZoom, float maxZoom)
    {
        if (sourceLayer != null) layer.SetSourceLayer(sourceLayer);
        if (minZoom > 0) layer.SetMinZoom(minZoom);
        if (maxZoom > 0) layer.SetMaxZoom(maxZoom);
    }

    private void ApplyProperties(MbglLayer layer, IDictionary<string, object?> props)
    {
        foreach (var (k, v) in props)
        {
            var json = JsonSerializer.Serialize(v);
            if (LayoutPropertyNames.Contains(k))
                layer.SetLayoutProperty(k, json);
            else
                layer.SetPaintProperty(k, json);
        }
    }

    // -- Camera ----------------------------------------------------------------

    public void JumpTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0)
        => _map?.JumpTo(latitude, longitude, zoom, bearing, pitch);

    public void EaseTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0, long durationMs = 300)
        => _map?.EaseTo(latitude, longitude, zoom, bearing, pitch, durationMs);

    public void FlyTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0, long durationMs = 500)
        => _map?.FlyTo(latitude, longitude, zoom, bearing, pitch, durationMs);

    public void CancelTransitions() => _map?.CancelTransitions();

    public double GetZoom()    => _map?.Zoom    ?? 0;
    public double GetBearing() => _map?.Bearing ?? 0;
    public double GetPitch()   => _map?.Pitch   ?? 0;
    public LatLng GetCenter()
    {
        if (_map == null) return new LatLng(0, 0);
        var (lat, lon) = _map.Center;
        return new LatLng(lat, lon);
    }

    public (double X, double Y) LatLngToScreenPoint(double latitude, double longitude)
        => _map?.PixelForLatLng(latitude, longitude) ?? (0, 0);

    public LatLng ScreenPointToLatLng(double x, double y)
    {
        if (_map == null) return new LatLng(0, 0);
        var (lat, lon) = _map.LatLngForPixel(x, y);
        return new LatLng(lat, lon);
    }

    public string? QueryRenderedFeaturesAtPoint(double x, double y, string? layerIds = null)
        => _map?.QueryRenderedFeaturesAtPoint(x, y, layerIds);

    public string? QueryRenderedFeaturesInBox(double x1, double y1, double x2, double y2,
        string? layerIds = null)
        => _map?.QueryRenderedFeaturesInBox(x1, y1, x2, y2, layerIds);

    // ── Viewport bounds ────────────────────────────────────────────────────────
    public (double LatSW, double LonSW, double LatNE, double LonNE) GetVisibleBounds()
        => _map?.LatLngBoundsForCamera() ?? default;

    // ── Memory / debug ─────────────────────────────────────────────────────────
    public void ReduceMemoryUse() => _map?.ReduceMemoryUse();
    public void DumpDebugLogs()   => _map?.DumpDebugLogs();

    // ── Feature state ──────────────────────────────────────────────────────────
    public void SetFeatureState(string sourceId, string featureId, string stateJson,
        string? sourceLayerId = null)
        => _map?.SetFeatureState(sourceId, featureId, stateJson, sourceLayerId);

    public string? GetFeatureState(string sourceId, string featureId,
        string? sourceLayerId = null)
        => _map?.GetFeatureState(sourceId, featureId, sourceLayerId);

    public void RemoveFeatureState(string sourceId, string? featureId = null,
        string? stateKey = null, string? sourceLayerId = null)
        => _map?.RemoveFeatureState(sourceId, featureId, stateKey, sourceLayerId);

    // ── Style – generic JSON add ───────────────────────────────────────────────
    public void AddSourceJson(string sourceId, string sourceJson)
        => _style?.AddSourceJson(sourceId, sourceJson);

    public MbglLayer? AddLayerJson(string layerJson, string? beforeLayerId = null)
        => _style?.AddLayerJson(layerJson, beforeLayerId);

    // -- Tier 1 – gesture / interactive movement ───────────────────────────────
    public void SetGestureInProgress(bool inProgress) => _map?.SetGestureInProgress(inProgress);
    public void MoveBy(double dx, double dy, long durationMs = 0) => _map?.MoveBy(dx, dy, durationMs);
    public void RotateBy(double x0, double y0, double x1, double y1) => _map?.RotateBy(x0, y0, x1, y1);
    public void PitchBy(double deltaDegrees, long durationMs = 0) => _map?.PitchBy(deltaDegrees, durationMs);

    // -- Tier 1 – map option setters ───────────────────────────────────────────
    public void SetNorthOrientation(int orientation) => _map?.SetNorthOrientation(orientation);
    public void SetConstrainMode(int mode) => _map?.SetConstrainMode(mode);
    public void SetViewportMode(int mode) => _map?.SetViewportMode(mode);

    // -- Tier 1 – bounds read-back ─────────────────────────────────────────────
    public BoundOptions GetBounds() => _map?.GetBounds() ?? default;

    // -- Tier 2 – tile LOD / prefetch ─────────────────────────────────────────
    public void SetPrefetchZoomDelta(int delta) => _map?.SetPrefetchZoomDelta(delta);
    public int  GetPrefetchZoomDelta() => _map?.GetPrefetchZoomDelta() ?? 4;
    public void SetTileLodMinRadius(double radius) => _map?.SetTileLodMinRadius(radius);
    public void SetTileLodScale(double scale) => _map?.SetTileLodScale(scale);
    public void SetTileLodPitchThreshold(double thresholdRadians) => _map?.SetTileLodPitchThreshold(thresholdRadians);
    public void SetTileLodZoomShift(double shift) => _map?.SetTileLodZoomShift(shift);
    public void SetTileLodMode(int mode) => _map?.SetTileLodMode(mode);

    // -- Tier 2 – camera / batch projection ───────────────────────────────────
    public CameraResult CameraForLatLngs(
        IReadOnlyList<(double Lat, double Lon)> points,
        double padTop = 0, double padLeft = 0, double padBottom = 0, double padRight = 0)
        => _map?.CameraForLatLngs(points, padTop, padLeft, padBottom, padRight) ?? default;

    public (double X, double Y)[] PixelsForLatLngs(IReadOnlyList<(double Lat, double Lon)> points)
        => _map?.PixelsForLatLngs(points) ?? [];

    public (double Lat, double Lon)[] LatLngsForPixels(IReadOnlyList<(double X, double Y)> pixels)
        => _map?.LatLngsForPixels(pixels) ?? [];

    // ── Debug overlays ────────────────────────────────────────────────────────────

    public int  GetDebugOptions() => _map?.GetDebugOptions() ?? 0;
    public void SetDebugOptions(int options) => _map?.SetDebugOptions(options);

    // ── Style inspection ───────────────────────────────────────────────────

    public string   GetStyleUrl()       => _style?.GetUrl()       ?? string.Empty;
    public string[] GetStyleSourceIds() => _style?.GetSourceIds() ?? [];
    public string[] GetStyleLayerIds()  => _style?.GetLayerIds()  ?? [];

    // ── Layer read-back + visibility ──────────────────────────────────────────

    public string? GetLayerPaintProperty(string layerId, string name)
        => _style?.GetLayer(layerId)?.GetPaintProperty(name);

    public string? GetLayerLayoutProperty(string layerId, string name)
        => _style?.GetLayer(layerId)?.GetLayoutProperty(name);

    public bool GetLayerVisibility(string layerId)
        => _style?.GetLayer(layerId)?.GetVisibility() ?? false;

    public void SetLayerVisibility(string layerId, bool visible)
        => _style?.GetLayer(layerId)?.SetVisible(visible);

    // ── Location indicator (no-op on iOS/macCatalyst — platform uses its own blue-dot) ──
    public bool FollowLocation { get; set; } = true;
    public bool ShowBearing    { get; set; } = true;
    public void UpdateLocationIndicator(double lat, double lon, float bearing = 0, float accuracyMeters = 10) { }
    public void ClearLocationIndicator() => ClearLocationIndicatorInternal();

    // -- Cleanup ---------------------------------------------------------------

    private void DisposeNative()
    {
        _style    = null;
        _map?.Dispose();      _map      = null;
        // Drain pending libuv tasks scheduled by Map destruction.
        for (int i = 0; i < 8 && _runLoop != null; i++) _runLoop.RunOnce();
        // mbgl_map_create transfers ownership of the frontend pointer to the
        // native CabiMap; mbgl_map_destroy already destroyed it. Do not call
        // Dispose() on _frontend — it is a no-op after TransferOwnership() but
        // we null it here explicitly to avoid confusion.
        _frontend = null;
        _runLoop?.Dispose();  _runLoop  = null;
        _styleReady = false;
    }
}
#endif
