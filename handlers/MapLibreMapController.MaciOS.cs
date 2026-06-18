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

    public MapContainerView View { get; }

    // -- Events ----------------------------------------------------------------

    public event Action<Map>?                OnMapReadyReceived;
    public event Action?                     OnDidBecomeIdleReceived;
    public event Action<int>?                OnCameraMoveStartedReceived;
    public event Action?                     OnCameraMoveReceived;
    public event Action?                     OnCameraIdleReceived;
    public event Action<int>?                OnCameraTrackingChangedReceived;
    public event Action?                     OnCameraTrackingDismissedReceived;
    public event Func<LatLng, bool>?         OnMapClickReceived;
    public event Func<LatLng, bool>?         OnMapLongClickReceived;
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
        NSLayoutConstraint.ActivateConstraints([
            _attrView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -8),
            _attrView.BottomAnchor.ConstraintEqualTo(View.BottomAnchor, -8),
            _attrView.WidthAnchor.ConstraintLessThanOrEqualTo(View.WidthAnchor, 0.9f),
        ]);

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
        NSLayoutConstraint.ActivateConstraints([
            _attrButton.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -8),
            _attrButton.BottomAnchor.ConstraintEqualTo(View.BottomAnchor, -8),
        ]);
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
                    OnCameraMoveReceived?.Invoke();
                    break;
                case "onCameraDidChange":
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
    public void SetShowNavigationControls(bool show)          { }
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

        if (parts.Count == 0 || !_showAttrControl)
        {
            _attrView.Hidden   = true;
            _attrButton.Hidden = true;
            return;
        }

        _attrLoaded = true;
        _attrView.AttributedText = BuildAttributionAttributedString(parts);
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
        System.Collections.Generic.List<string> parts)
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
    public void ClearLocationIndicator() { }

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
