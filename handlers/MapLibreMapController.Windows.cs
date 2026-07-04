#if WINDOWS
using System.Runtime.InteropServices;
using System.Text.Json;
using MapLibreNative.Maui;
using MapLibreNative.Maui.Handlers.Geometry;
using Map = MapLibreNative.Maui.Handlers.Maps.Map;
using Style = MapLibreNative.Maui.Handlers.Maps.Style;
using Location = Microsoft.Maui.Devices.Sensors.Location;
using WUX = Microsoft.UI.Xaml;
using WUXC = Microsoft.UI.Xaml.Controls;
using WUXM = Microsoft.UI.Xaml.Media;

namespace MapLibreNative.Maui.Handlers;

/// <summary>
/// Windows-specific IMapLibreMapController implementation backed by the C ABI
/// mln-cabi.dll via MbglMap / MbglFrontend / MbglRunLoop P/Invoke bindings.
/// </summary>
public class MapLibreMapController : IMapLibreMapController
{
    // ── Known layout property names ───────────────────────────────────────────
    // All others are treated as paint properties.

    private static readonly HashSet<string> LayoutPropertyNames = new(StringComparer.Ordinal)
    {
        "visibility",
        // symbol layout
        "symbol-placement","symbol-spacing","symbol-avoid-edges","symbol-sort-key","symbol-z-order",
        "icon-allow-overlap","icon-ignore-placement","icon-optional","icon-rotation-alignment",
        "icon-size","icon-text-fit","icon-text-fit-padding","icon-image","icon-rotate",
        "icon-padding","icon-keep-upright","icon-offset","icon-anchor","icon-pitch-alignment",
        "text-pitch-alignment","text-rotation-alignment","text-field","text-font","text-size",
        "text-max-width","text-line-height","text-letter-spacing","text-justify",
        "text-radial-offset","text-variable-anchor","text-anchor","text-max-angle",
        "text-writing-mode","text-rotate","text-padding","text-keep-upright","text-transform",
        "text-offset","text-allow-overlap","text-ignore-placement","text-optional",
        // line layout
        "line-cap","line-join","line-miter-limit","line-round-limit","line-sort-key",
        // fill layout
        "fill-sort-key",
        // circle layout
        "circle-sort-key",
    };

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly IntPtr _parentHwnd;
    private string? _styleString;
    private float            _pixelRatio;

    private MbglMap?      _map;
    private MbglStyle?    _style;

    // ── On-map control state ──────────────────────────────────────────────────
    private bool   _showNavControls = true;
    private bool   _showAttrControl = true;
    private string? _customAttribution;
    private string  _attrText = string.Empty;  // cached, rebuilt on StyleLoaded
    private bool   _showGpsControl = true;
    /// <summary>GPS tracking mode — matches Android TrackingMode enum.</summary>
    private enum GpsTrackingMode { Off, Show, Follow, FollowBearing }
    private GpsTrackingMode _gpsMode = GpsTrackingMode.Off;
    // Last received GPS fix — cached so SHOW mode can display it immediately when enabled
    private double _lastGpsLat, _lastGpsLon;
    private float  _lastGpsBearing, _lastGpsAccuracy;
    private bool   _hasGpsFix;
    // Hit-testing constants (logical pixels, scaled by _pixelRatio internally)
    private const int NavButtonSize   = 29;   // px (zoom button height)
    private const int NavDpadSize     = NavButtonSize;   // px — round d-pad; matches button width so nav/zoom/GPS align when stacked
    private const int NavPanelMargin  = 10;   // from map edge
    private const int AttrPadH        = 6;    // horizontal text padding
    private const int AttrPadV        = 3;    // vertical text padding
    // Below this map height (logical px) the nav panel is hidden so its buttons
    // don't spill past the map edge or stack over the attribution on short maps.
    // Nav panel = 3×29 px buttons + 2×1 px dividers + ~10 px top margin ≈ 99 px.
    // Cached overlay positions — skip SetWindowPos when nothing changed (prevents repaint flicker)
    private (int x, int y, int w, int h) _lastNavRect;
    private (int x, int y, int w, int h) _lastAttrRect;
    // Cached attr text measurement — only re-measure via GDI when text or available width changes
    private (int cx, int cy) _cachedAttrMeasure;
    // Collapse state
    private bool _attrCollapsed;
    private System.Threading.Timer? _attrCollapseTimer;

    // Pumps the libuv run loop on the UI thread. Without this, async HTTP responses
    // for style/tile downloads are never delivered and StyleLoaded never fires.
    private bool _renderNeedsUpdate;

    private bool _initialized;
    private bool _styleReady;

    // Owns the in-tree map surface + mbgl objects (the airspace-free renderer; the only path).
    private WinUI.SwapChainMapView? _swapView;

    // XAML overlay controls for the SwapChainPanel renderer (real in-tree children of View).
    private WUXC.StackPanel? _swapNavPanel;
    private WUXM.RotateTransform? _swapCompassRotate;   // north tick on the nav d-pad
    private WUXC.StackPanel? _swapGpsPanel;
    private WUXC.Border?     _swapGpsTopBtn;
    private WUXC.TextBlock?  _swapGpsTopIcon;
    private WUXC.TextBlock?  _swapGpsBottomIcon;
    private WUXC.Border?     _swapAttrBorder;
    private WUXC.TextBlock?  _swapAttrText;

    /// <summary>The WinUI placeholder element the handler uses as the platform view.</summary>
    public Microsoft.UI.Xaml.Controls.Grid View { get; } = new();

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<Map>?                            OnMapReadyReceived;
    public event Action?                                 OnDidBecomeIdleReceived;
    public event Action<int>?                            OnCameraMoveStartedReceived;
    public event Action?                                 OnCameraMoveReceived;
    public event Action?                                 OnCameraIdleReceived;
    public event Action<int>?                            OnCameraTrackingChangedReceived;
    public event Action?                                 OnCameraTrackingDismissedReceived;
    public event Func<LatLng, double, double, bool>?                     OnMapClickReceived;
    public event Func<LatLng, double, double, bool>?                     OnMapLongClickReceived;
    public event Action<Maps.Style>?                     OnStyleLoadedReceived;
    public event Action<Location>?                       OnUserLocationUpdateReceived;
    public event Action<string>?                         OnDidFailLoadingMapReceived;
    public event Action<string>?                         OnStyleImageMissingReceived;
    public event Action<string>?                         OnRenderErrorReceived;

    public MapLibreMapController(IntPtr parentHwnd, float pixelRatio, string? styleString)
    {
        _parentHwnd  = parentHwnd;
        _pixelRatio  = pixelRatio;
        _styleString = styleString;
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    public void Init() => InitSwapChain();

    // ── SwapChainPanel renderer (airspace-free, in-tree) ───────────────────────

    private void InitSwapChain()
    {
        _swapView = new WinUI.SwapChainMapView
        {
            StyleUrl = string.IsNullOrEmpty(_styleString)
                ? "https://demotiles.maplibre.org/style.json" : _styleString!,
        };

        // The self-contained view owns the mbgl objects; mirror them onto the controller's
        // fields so all existing camera/source/layer operations work unchanged.
        _swapView.MapReady += (_, _) =>
        {
            _map = _swapView.Map;
            _initialized = true;
            OnMapReadyReceived?.Invoke(new Map(null));
        };
        _swapView.StyleLoaded += (_, _) =>
        {
            _style = _swapView.Style;
            _styleReady = true;
            RefreshAttributionText();   // build attribution from the new style's sources
            OnStyleLoadedReceived?.Invoke(new Style(null));
        };
        _swapView.DidBecomeIdle += (_, _) =>
        {
            if (_attrText.Length == 0) RefreshAttributionText();
            OnDidBecomeIdleReceived?.Invoke();
        };
        _swapView.CameraIdle    += (_, _) => { RefreshSwapChainGps(); OnCameraIdleReceived?.Invoke(); };
        _swapView.MapClicked    += (_, e) => OnMapClickReceived?.Invoke(new LatLng(e.Lat, e.Lon), e.X, e.Y);

        View.Children.Add(_swapView.View);
        CreateSwapChainOverlays();
        View.Unloaded += (_, _) => DisposeNative();
    }

    // ── SwapChainPanel XAML overlays (nav / GPS / attribution) ─────────────────

    private void CreateSwapChainOverlays()
    {
        // Navigation — top-right corner: rotate/pitch/compass d-pad, then zoom in/out.
        _swapNavPanel = new WUXC.StackPanel
        {
            HorizontalAlignment = WUX.HorizontalAlignment.Right,
            VerticalAlignment = WUX.VerticalAlignment.Top,
            Margin = new WUX.Thickness(0, 10, 10, 0),
            Width = 30,
        };
        _swapNavPanel.Children.Add(BuildSwapDpad());
        _swapNavPanel.Children.Add(MakeSwapDivider());
        _swapNavPanel.Children.Add(MakeSwapNavButton("+", () => _swapView?.ZoomIn(), new WUX.CornerRadius(0)));
        _swapNavPanel.Children.Add(MakeSwapDivider());
        _swapNavPanel.Children.Add(MakeSwapNavButton("−", () => _swapView?.ZoomOut(), new WUX.CornerRadius(0, 0, 4, 4)));
        View.Children.Add(_swapNavPanel);

        // GPS control — top-right, stacked below the nav panel (d-pad 30 + 2 zoom 30 + 2 dividers ≈ 92).
        _swapGpsPanel = new WUXC.StackPanel
        {
            HorizontalAlignment = WUX.HorizontalAlignment.Right,
            VerticalAlignment = WUX.VerticalAlignment.Top,
            Margin = new WUX.Thickness(0, 112, 10, 0),
            Width = 30,
            Visibility = _showGpsControl ? WUX.Visibility.Visible : WUX.Visibility.Collapsed,
        };
        _swapGpsTopBtn = MakeSwapButton("○", CycleGpsMode, true);
        _swapGpsTopIcon = (WUXC.TextBlock)_swapGpsTopBtn.Child;
        var gpsBottom = MakeSwapButton("↺", GpsBearingButtonPressed, false);
        _swapGpsBottomIcon = (WUXC.TextBlock)gpsBottom.Child;
        _swapGpsPanel.Children.Add(_swapGpsTopBtn);
        _swapGpsPanel.Children.Add(gpsBottom);
        View.Children.Add(_swapGpsPanel);

        // Attribution — bottom-left, collapsed ⓘ that expands on tap.
        _swapAttrText = new WUXC.TextBlock
        {
            Text = "ⓘ",
            FontSize = 11,
            Foreground = new WUXM.SolidColorBrush(Windows.UI.Color.FromArgb(255, 85, 85, 85)),
            TextWrapping = WUX.TextWrapping.Wrap,
            MaxWidth = 320,
        };
        _swapAttrBorder = new WUXC.Border
        {
            Background = new WUXM.SolidColorBrush(Windows.UI.Color.FromArgb(235, 248, 248, 248)),
            BorderBrush = new WUXM.SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 208, 208)),
            BorderThickness = new WUX.Thickness(1),
            CornerRadius = new WUX.CornerRadius(4),
            Padding = new WUX.Thickness(6, 3, 6, 3),
            HorizontalAlignment = WUX.HorizontalAlignment.Left,
            VerticalAlignment = WUX.VerticalAlignment.Bottom,
            Margin = new WUX.Thickness(10, 0, 0, 10),
            Child = _swapAttrText,
            Visibility = _showAttrControl ? WUX.Visibility.Visible : WUX.Visibility.Collapsed,
        };
        _swapAttrBorder.Tapped += (_, e) =>
        {
            if (_attrCollapsed) ExpandAttribution(); else CollapseAttribution();
            e.Handled = true;
        };
        View.Children.Add(_swapAttrBorder);

        RefreshSwapChainGps();
    }

    private static WUXC.Border MakeSwapButton(string glyph, Action onClick, bool top)
    {
        var b = new WUXC.Border
        {
            Height = 30,
            Background = new WUXM.SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = new WUXM.SolidColorBrush(Windows.UI.Color.FromArgb(255, 218, 218, 218)),
            BorderThickness = new WUX.Thickness(1, top ? 1 : 0, 1, 1),
            CornerRadius = top ? new WUX.CornerRadius(4, 4, 0, 0) : new WUX.CornerRadius(0, 0, 4, 4),
            Child = new WUXC.TextBlock
            {
                Text = glyph,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = WUX.HorizontalAlignment.Center,
                VerticalAlignment = WUX.VerticalAlignment.Center,
            },
        };
        b.Tapped += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }

    private static WUXC.Border MakeSwapDivider()
        => new() { Height = 1, Background = new WUXM.SolidColorBrush(Rgb(0xDA, 0xDA, 0xDA)) };

    private static WUXC.Border MakeSwapNavButton(string glyph, Action onClick, WUX.CornerRadius corners)
    {
        var b = new WUXC.Border
        {
            Height = 29,
            Background = new WUXM.SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = corners,
            Child = new WUXC.TextBlock
            {
                Text = glyph,
                FontSize = 18,
                // Explicit dark foreground — the WinUI default can be white (theme-dependent),
                // which would make +/- invisible on the white button.
                Foreground = new WUXM.SolidColorBrush(Rgb(0x33, 0x33, 0x33)),
                HorizontalAlignment = WUX.HorizontalAlignment.Center,
                VerticalAlignment = WUX.VerticalAlignment.Center,
                IsHitTestVisible = false,
            },
        };
        b.PointerEntered += (_, _) => b.Background = new WUXM.SolidColorBrush(Rgb(0xF0, 0xF0, 0xF0));
        b.PointerExited  += (_, _) => b.Background = new WUXM.SolidColorBrush(Microsoft.UI.Colors.White);
        b.Tapped += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }

    // Rotate/pitch/compass d-pad — WinUI equivalent of the WPF MlnMapImage d-pad.
    private WUXC.Border BuildSwapDpad()
    {
        const double size = 30;
        var root = new WUXC.Grid { Width = size, Height = size, Background = new WUXM.SolidColorBrush(Microsoft.UI.Colors.White) };

        var grid = new WUXC.Grid();
        for (int i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new WUXC.ColumnDefinition { Width = new WUX.GridLength(1, WUX.GridUnitType.Star) });
            grid.RowDefinitions.Add(new WUXC.RowDefinition { Height = new WUX.GridLength(1, WUX.GridUnitType.Star) });
        }
        void Add(WUX.FrameworkElement el, int row, int col) { WUXC.Grid.SetRow(el, row); WUXC.Grid.SetColumn(el, col); grid.Children.Add(el); }
        Add(MakeSwapDpadArrow("▲", () => PitchBy(10)),  0, 1);  // ▲ up    → more tilt
        Add(MakeSwapDpadArrow("◀", () => RotateBy(-15)), 1, 0);  // ◀ left  → rotate ccw
        Add(MakeSwapDpadArrow(null,      ResetNorth),         1, 1);  // centre  → reset north
        Add(MakeSwapDpadArrow("▶", () => RotateBy(15)),  1, 2);  // ▶ right → rotate cw
        Add(MakeSwapDpadArrow("▼", () => PitchBy(-10)), 2, 1);  // ▼ down  → less tilt
        root.Children.Add(grid);

        double ringSize = size * 0.80;
        root.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = ringSize,
            Height = ringSize,
            Stroke = new WUXM.SolidColorBrush(Rgb(0xCC, 0xCC, 0xCC)),
            StrokeThickness = 1,
            IsHitTestVisible = false,
            HorizontalAlignment = WUX.HorizontalAlignment.Center,
            VerticalAlignment = WUX.VerticalAlignment.Center,
        });

        _swapCompassRotate = new WUXM.RotateTransform { Angle = 0 };
        var northTick = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 2,
            Height = size * 0.14,
            Fill = new WUXM.SolidColorBrush(Rgb(0x0A, 0x66, 0xCC)),
            IsHitTestVisible = false,
            HorizontalAlignment = WUX.HorizontalAlignment.Center,
            VerticalAlignment = WUX.VerticalAlignment.Top,
            Margin = new WUX.Thickness(0, (size - ringSize) / 2, 0, 0),
        };
        var tickHost = new WUXC.Grid
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = _swapCompassRotate,
        };
        tickHost.Children.Add(northTick);
        root.Children.Add(tickHost);

        return new WUXC.Border { CornerRadius = new WUX.CornerRadius(4, 4, 0, 0), Child = root };
    }

    private static WUXC.Border MakeSwapDpadArrow(string? glyph, Action onClick)
    {
        var btn = new WUXC.Border { Background = new WUXM.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        if (glyph != null)
        {
            btn.Child = new WUXC.TextBlock
            {
                Text = glyph,
                FontSize = 8,
                Foreground = new WUXM.SolidColorBrush(Rgb(0x33, 0x33, 0x33)),
                HorizontalAlignment = WUX.HorizontalAlignment.Center,
                VerticalAlignment = WUX.VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
        }
        btn.PointerEntered += (_, _) => btn.Background = new WUXM.SolidColorBrush(Windows.UI.Color.FromArgb(30, 0, 0, 0));
        btn.PointerExited  += (_, _) => btn.Background = new WUXM.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        btn.Tapped += (_, e) => { onClick(); e.Handled = true; };
        return btn;
    }

    /// <summary>Updates the GPS button glyphs/colours from the current tracking mode + bearing.</summary>
    private void RefreshSwapChainGps()
    {
        if (_swapCompassRotate != null) _swapCompassRotate.Angle = -GetBearing();
        if (_swapGpsTopIcon == null || _swapGpsTopBtn == null || _swapGpsBottomIcon == null) return;

        (string glyph, Windows.UI.Color fg, Windows.UI.Color bg) = _gpsMode switch
        {
            GpsTrackingMode.Show          => ("⊙", Rgb(0x1E, 0x88, 0xE5), Rgb(255, 255, 255)),
            GpsTrackingMode.Follow        => ("◎", Rgb(0x00, 0x70, 0xC5), Rgb(0xE0, 0xF3, 0xFF)),
            GpsTrackingMode.FollowBearing => ("▲", Rgb(0xF5, 0x7C, 0x00), Rgb(0xFF, 0xF3, 0xE0)),
            _                             => ("○", Rgb(0x99, 0x99, 0x99), Rgb(255, 255, 255)),
        };
        _swapGpsTopIcon.Text = glyph;
        _swapGpsTopIcon.Foreground = new WUXM.SolidColorBrush(fg);
        _swapGpsTopBtn.Background = new WUXM.SolidColorBrush(bg);

        bool rotated = Math.Abs(GetBearing()) > 0.5;
        _swapGpsBottomIcon.Foreground = new WUXM.SolidColorBrush(rotated ? Rgb(0x1E, 0x88, 0xE5) : Rgb(0x55, 0x55, 0x55));
    }

    /// <summary>Updates the attribution control text from <c>_attrText</c> / collapsed state (UI thread safe).</summary>
    private void RefreshSwapChainAttribution()
    {
        if (_swapAttrText == null) return;
        _swapAttrText.DispatcherQueue.TryEnqueue(() =>
        {
            if (_swapAttrText == null || _swapAttrBorder == null) return;
            _swapAttrText.Text = _attrCollapsed || _attrText.Length == 0 ? "ⓘ" : _attrText;
        });
    }

    private static Windows.UI.Color Rgb(int r, int g, int b) => Windows.UI.Color.FromArgb(255, (byte)r, (byte)g, (byte)b);



    /// <summary>
    /// Called by the handler after a layout pass settles (e.g. window restore). The in-tree
    /// SwapChainPanel surface repositions itself with XAML layout, so this is a no-op.
    /// </summary>
    internal void RefreshPosition() { }


    // ── IMapLibreMapOptionsSink ───────────────────────────────────────────────

    public void SetCameraTargetBounds(LatLngBounds bounds,
        double minZoom = double.NaN, double maxZoom = double.NaN,
        double minPitch = double.NaN, double maxPitch = double.NaN)
    {
        _map?.SetBounds(bounds.SouthWest.Latitude, bounds.SouthWest.Longitude,
                        bounds.NorthEast.Latitude, bounds.NorthEast.Longitude,
                        minZoom, maxZoom, minPitch, maxPitch);
    }
    public void SetCompassEnabled(bool compassEnabled)     { /* no-op — overlay not yet implemented */ }
    public void SetRotateGesturesEnabled(bool v)          { /* TODO: mbgl gesture flags */ }
    public void SetScrollGesturesEnabled(bool v)          { }
    public void SetTiltGesturesEnabled(bool v)            { }
    public void SetTrackCameraPosition(bool v)            { }
    public void SetZoomGesturesEnabled(bool v)            { }
    public void SetMyLocationEnabled(bool v)              { }
    public void SetMyLocationTrackingMode(int v)          { }
    public void SetMyLocationRenderMode(int v)            { }
    public void SetLogoViewMargins(int x, int y)          { }
    public void SetCompassGravity(int gravity)            { }
    public void SetCompassViewMargins(int x, int y)       { }
    public void SetAttributionButtonGravity(int gravity)  { }
    public void SetAttributionButtonMargins(int x, int y) { }

    public void SetStyleString(string styleString)
    {
        _styleString = styleString;
        if (_map == null) return;
        if (styleString.StartsWith('{'))
            _map.SetStyleJson(styleString);
        else
            _map.SetStyleUrl(styleString);
    }

    public void SetMinMaxZoomPreference(double? min, double? max)
    {
        if (min.HasValue) _map?.SetMinZoom(min.Value);
        if (max.HasValue) _map?.SetMaxZoom(max.Value);
    }

    // ── Sources ───────────────────────────────────────────────────────────────

    public void AddGeoJsonSource(string sourceName, string source)
    {
        if (!_styleReady || _style == null) return;
        // Reuse the existing source if present so a re-add updates it in place
        // instead of no-op'ing (which left overlay geometry stale).
        var s = _style.HasSource(sourceName) ? _style.GetSource(sourceName)! : _style.AddGeoJsonSource(sourceName);
        s.SetGeoJson(source);
    }

    public void SetGeoJsonSource(string sourceName, string source)
    {
        if (!_styleReady || _style == null) return;
        // Update the existing source's data in place (no layer churn — removing/re-adding
        // layers while the in-tree renderer is drawing can crash the map natively).
        _style.GetSource(sourceName)?.SetGeoJson(source);
    }

    public void SetGeoJsonFeature(string sourceName, string geojsonFeature)
    {
        // Partial update: not directly supported in C ABI — replace whole source for now
        if (!_styleReady || _style == null) return;
        if (_style.HasSource(sourceName))
            _style.RemoveSource(sourceName);
        AddGeoJsonSource(sourceName, geojsonFeature);
    }

    public void AddRasterSource(string sourceName, string? tileUrl, string[]? tileUrlTemplates,
        int tileSize, int minZoom, int maxZoom)
    {
        if (!_styleReady || _style == null) return;
        var url = tileUrl ?? tileUrlTemplates?.FirstOrDefault();
        if (url == null) return;
        _style.AddRasterSource(sourceName, url, tileSize);
    }

    public void AddRasterDemSource(string sourceName, string? tileUrl, string[]? tileUrlTemplates,
        int tileSize, int minZoom, int maxZoom)
    {
        if (!_styleReady || _style == null) return;
        var url = tileUrl ?? tileUrlTemplates?.FirstOrDefault();
        if (url == null) return;
        _style.AddRasterDemSource(sourceName, url, tileSize);
    }

    public void AddVectorSource(string sourceName, string? tileUrl, string[]? tileUrlTemplates,
        int minZoom, int maxZoom)
    {
        if (!_styleReady || _style == null) return;
        var url = tileUrl ?? tileUrlTemplates?.FirstOrDefault();
        if (url == null) return;
        _style.AddVectorSource(sourceName, url);
    }

    public void AddImageSource(string sourceName, string url, LatLngQuad? coordinates)
    {
        if (!_styleReady || _style == null) return;
        if (coordinates != null)
        {
            _style.AddImageSource(sourceName, url,
                coordinates.TopRight.Latitude,    coordinates.TopRight.Longitude,
                coordinates.TopLeft.Latitude,     coordinates.TopLeft.Longitude,
                coordinates.BottomRight.Latitude, coordinates.BottomRight.Longitude,
                coordinates.BottomLeft.Latitude,  coordinates.BottomLeft.Longitude);
        }
        else
        {
            // No coordinates supplied — fall back to a plain raster source.
            _style.AddRasterSource(sourceName, url);
        }
    }

    public void RemoveSource(string sourceId)
    {
        if (!_styleReady || _style == null) return;
        _style.RemoveSource(sourceId);
    }

    // ── Layers ────────────────────────────────────────────────────────────────

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

    public void AddHeatmapLayer(
        string layerName,
        string sourceName,
        IDictionary<string, object?> properties,
        float minZoom = 0,
        float maxZoom = 0,
        string? belowLayerId = null)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddHeatmapLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, null, minZoom, maxZoom);
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

    public void AddFillExtrusionLayer(string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0, bool enableInteraction = false)
    {
        if (!_styleReady || _style == null) return;
        var layer = _style.AddFillExtrusionLayer(layerName, sourceName, belowLayerId);
        ApplyLayerMeta(layer, sourceLayer, minZoom, maxZoom);
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ApplyLayerMeta(MbglLayer layer, string? sourceLayer, float minZoom, float maxZoom)
    {
        if (sourceLayer != null) layer.SetSourceLayer(sourceLayer);
        if (minZoom > 0) layer.SetMinZoom(minZoom);
        if (maxZoom > 0) layer.SetMaxZoom(maxZoom);
    }

    private static void ApplyProperties(MbglLayer layer, IDictionary<string, object?> props)
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

    /// <summary>Cycle GPS tracking mode: Off → Show → Follow → FollowBearing → Off.</summary>
    private void CycleGpsMode()
    {
        _gpsMode = _gpsMode switch
        {
            GpsTrackingMode.Off           => GpsTrackingMode.Show,
            GpsTrackingMode.Show          => GpsTrackingMode.Follow,
            GpsTrackingMode.Follow        => GpsTrackingMode.FollowBearing,
            GpsTrackingMode.FollowBearing => GpsTrackingMode.Off,
            _                             => GpsTrackingMode.Off,
        };

        ApplyGpsMode();
        RefreshSwapChainGps();
    }

    /// <summary>Apply the current GPS mode to the location indicator and camera.</summary>
    private void ApplyGpsMode()
    {
        if (_gpsMode == GpsTrackingMode.Off)
        {
            ClearLocationIndicator();
        }
        else if (_hasGpsFix)
        {
            // (Re-)apply the last known GPS fix to the location indicator
            _pendingLocInd = new LocIndParams(_lastGpsLat, _lastGpsLon, _lastGpsBearing, Math.Max(5f, _lastGpsAccuracy));
            if (_gpsMode is GpsTrackingMode.Follow or GpsTrackingMode.FollowBearing)
            {
                double cameraZoom    = GetZoom() < 8 ? 14 : GetZoom();
                double cameraBearing = _gpsMode == GpsTrackingMode.FollowBearing ? _lastGpsBearing : GetBearing();
                EaseTo(_lastGpsLat, _lastGpsLon, cameraZoom, cameraBearing, GetPitch(), durationMs: 300);
            }
            if (_styleReady && _style != null)
                ApplyPendingLocationIndicator();
            _renderNeedsUpdate = true;
        }
    }

    /// <summary>
    /// Feed a GPS location update to the controller.
    /// The controller stores the fix and applies it according to the current
    /// <see cref="GpsTrackingMode"/> (Off = ignored; Show = update dot only;
    /// Follow = update dot + re-centre camera).
    /// </summary>
    public void UpdateGpsLocation(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        _lastGpsLat      = lat;
        _lastGpsLon      = lon;
        _lastGpsBearing  = bearing;
        _lastGpsAccuracy = accuracyMeters;
        bool isFirstFix  = !_hasGpsFix;
        _hasGpsFix       = true;

        if (_gpsMode == GpsTrackingMode.Off) return;

        bool follow        = _gpsMode is GpsTrackingMode.Follow or GpsTrackingMode.FollowBearing;
        bool useBearing    = _gpsMode == GpsTrackingMode.FollowBearing;
        _pendingLocInd     = new LocIndParams(lat, lon, bearing, Math.Max(5f, accuracyMeters));

        if (follow)
        {
            double cameraZoom    = GetZoom() < 8 ? 14 : GetZoom();
            double cameraBearing = useBearing ? bearing : GetBearing();
            if (isFirstFix) JumpTo(lat, lon, cameraZoom, cameraBearing, GetPitch());
            else            EaseTo(lat, lon, GetZoom(), cameraBearing, GetPitch(), durationMs: 200);
        }

        if (_styleReady && _style != null)
            ApplyPendingLocationIndicator();
        _renderNeedsUpdate = true;
        _map?.TriggerRepaint();

        // Refresh the GPS button (state indicator icon may change on first fix)
        if (isFirstFix) RefreshSwapChainGps();
    }

    /// <summary>
    /// Bearing button pressed: if in FollowBearing mode, drop to Follow (stop rotating) and
    /// reset bearing to north; otherwise just reset bearing to north.
    /// </summary>
    private void GpsBearingButtonPressed()
    {
        if (_gpsMode == GpsTrackingMode.FollowBearing)
        {
            _gpsMode = GpsTrackingMode.Follow;
            RefreshSwapChainGps();
        }
        ResetNorth();
    }

    public void SetShowGpsControl(bool show)
    {
        _showGpsControl = show;
        if (_swapGpsPanel != null)
            _swapGpsPanel.Visibility = show ? WUX.Visibility.Visible : WUX.Visibility.Collapsed;
    }


    // ── Attribution overlay WndProc ────────────────────────────────────────────



    // ── Nav/attribution public API ─────────────────────────────────────────────

    private void ZoomIn()
    {
        if (_map == null) return;
        var center = GetCenter();
        EaseTo(center.Latitude, center.Longitude, GetZoom() + 1, GetBearing(), GetPitch(), durationMs: 250);
        _renderNeedsUpdate = true;
    }

    private void ZoomOut()
    {
        if (_map == null) return;
        var center = GetCenter();
        EaseTo(center.Latitude, center.Longitude, GetZoom() - 1, GetBearing(), GetPitch(), durationMs: 250);
        _renderNeedsUpdate = true;
    }

    private void ResetNorth()
    {
        if (_map == null) return;
        var center = GetCenter();
        // Match maplibre-gl-js: first click resets bearing to 0 (keeping pitch);
        // if bearing is already ~0, also reset pitch to 0.
        double currentBearing = GetBearing();
        double newPitch = Math.Abs(currentBearing) < 0.5 ? 0 : GetPitch();
        EaseTo(center.Latitude, center.Longitude, GetZoom(), bearing: 0, pitch: newPitch, durationMs: 300);
        _renderNeedsUpdate = true;
    }

    /// <summary>Rotate the map by <paramref name="deltaDeg"/> (positive = clockwise).</summary>
    private void RotateBy(double deltaDeg)
    {
        if (_map == null || deltaDeg == 0) return;
        // Snap the target onto the increment grid (multiples of |deltaDeg|) so a full
        // rotation returns exactly to the start. Incrementing the live bearing instead
        // accumulates float error each ease and never quite closes the loop.
        double step = Math.Abs(deltaDeg);
        double target = Math.Round(GetBearing() / step) * step + deltaDeg;
        var center = GetCenter();
        EaseTo(center.Latitude, center.Longitude, GetZoom(), target, GetPitch(), durationMs: 200);
        _renderNeedsUpdate = true;
    }

    /// <summary>Tilt the map by <paramref name="deltaDeg"/>, clamped to 0–60°.</summary>
    private void PitchBy(double deltaDeg)
    {
        if (_map == null) return;
        var center = GetCenter();
        double newPitch = Math.Max(0, Math.Min(60, GetPitch() + deltaDeg));
        EaseTo(center.Latitude, center.Longitude, GetZoom(), GetBearing(), newPitch, durationMs: 200);
        _renderNeedsUpdate = true;
    }

    /// <summary>
    /// Rebuilds the attribution text from all loaded TileJSON sources and repositions
    /// the attribution overlay. Called after <c>StyleLoaded</c>.
    /// </summary>
    private void RefreshAttributionText()
    {
        if (_style == null) { _attrText = string.Empty; return; }
        var parts = new System.Collections.Generic.List<string>(_style.GetSourceAttributions());
        if (!string.IsNullOrWhiteSpace(_customAttribution))
            parts.Add(_customAttribution!);
        var attributions = MbglStyle.EnsureMapLibreAttribution(parts);
        // Strip HTML tags to plain text (attribution strings from OSM are like
        // "© <a href='...'>OpenStreetMap</a> contributors" — we strip the links for now).
        var sb = new System.Text.StringBuilder();
        foreach (var part in attributions)
        {
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(StripHtmlTags(part));
        }
        _attrText = sb.ToString();
        if (_attrText.Length > 0)
            ExpandAttribution();
        else
            RefreshSwapChainAttribution();
    }

    private static string StripHtmlTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var sb = new System.Text.StringBuilder(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if      (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag)   sb.Append(c);
        }
        return DecodeHtmlEntities(sb.ToString().Trim());
    }

    private static string DecodeHtmlEntities(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('&')) return text;
        return text
            .Replace("&amp;",   "&")
            .Replace("&copy;",  "\u00A9")
            .Replace("&reg;",   "\u00AE")
            .Replace("&trade;", "\u2122")
            .Replace("&mdash;", "\u2014")
            .Replace("&ndash;", "\u2013")
            .Replace("&nbsp;",  "\u00A0")
            .Replace("&lt;",    "<")
            .Replace("&gt;",    ">");
    }

    public void SetShowNavigationControls(bool show)
    {
        _showNavControls = show;
        if (_swapNavPanel != null)
            _swapNavPanel.Visibility = show ? WUX.Visibility.Visible : WUX.Visibility.Collapsed;
    }

    // The in-tree XAML overlays use fixed corners (nav/GPS top-right, attribution bottom-left);
    // per-control corner placement is not applied yet, so these setters are accepted no-ops.
    public void SetNavigationControlPosition(MapControlCorner corner) { }
    public void SetGpsControlPosition(MapControlCorner corner) { }
    public void SetAttributionControlPosition(MapControlCorner corner) { }

    public void SetShowAttributionControl(bool show, string? customAttribution)
    {
        _showAttrControl   = show;
        _customAttribution = customAttribution;
        _attrCollapsed     = false;  // reset collapse when attribution settings change
        if (_swapAttrBorder != null)
            _swapAttrBorder.Visibility = show ? WUX.Visibility.Visible : WUX.Visibility.Collapsed;
        RefreshAttributionText();
    }

    private void ExpandAttribution()
    {
        _attrCollapsed = false;
        RefreshSwapChainAttribution();
        ScheduleAutoCollapse();
    }

    private void CollapseAttribution()
    {
        _attrCollapseTimer?.Dispose();
        _attrCollapseTimer = null;
        if (_attrCollapsed) return;
        _attrCollapsed = true;
        RefreshSwapChainAttribution();
    }

    private void ScheduleAutoCollapse()
    {
        _attrCollapseTimer?.Dispose();
        _attrCollapseTimer = new System.Threading.Timer(
            _ => CollapseAttribution(), null,
            TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    // ── Popup WndProc (mouse input) ────────────────────────────────────────────



    // ── Cleanup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Public entry point called by <see cref="MapLibreMapHandler"/> from its
    /// <c>DisconnectHandler</c> override. Idempotent — safe to call more than once.
    /// </summary>
    public void Shutdown() => DisposeNative();

    private void DisposeNative()
    {
        _swapView?.Dispose();
        _swapView = null;
        _map = null;
        _style = null;
        _initialized = false;
    }

    // ── Camera ────────────────────────────────────────────────────────────────

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


    public void SetGestureInProgress(bool inProgress) => _map?.SetGestureInProgress(inProgress);
    public void MoveBy(double dx, double dy, long durationMs = 0) => _map?.MoveBy(dx, dy, durationMs);
    public void RotateBy(double x0, double y0, double x1, double y1) => _map?.RotateBy(x0, y0, x1, y1);
    public void PitchBy(double deltaDegrees, long durationMs = 0) => _map?.PitchBy(deltaDegrees, durationMs);

    // ── Tier 1 – map option setters ───────────────────────────────────────────
    public void SetNorthOrientation(int orientation) => _map?.SetNorthOrientation(orientation);
    public void SetConstrainMode(int mode) => _map?.SetConstrainMode(mode);
    public void SetViewportMode(int mode) => _map?.SetViewportMode(mode);

    // ── Tier 1 – bounds read-back ─────────────────────────────────────────────
    public BoundOptions GetBounds() => _map?.GetBounds() ?? default;

    // ── Tier 2 – tile LOD / prefetch ─────────────────────────────────────────
    public void SetPrefetchZoomDelta(int delta) => _map?.SetPrefetchZoomDelta(delta);
    public int  GetPrefetchZoomDelta() => _map?.GetPrefetchZoomDelta() ?? 4;
    public void SetTileLodMinRadius(double radius) => _map?.SetTileLodMinRadius(radius);
    public void SetTileLodScale(double scale) => _map?.SetTileLodScale(scale);
    public void SetTileLodPitchThreshold(double thresholdRadians) => _map?.SetTileLodPitchThreshold(thresholdRadians);
    public void SetTileLodZoomShift(double shift) => _map?.SetTileLodZoomShift(shift);
    public void SetTileLodMode(int mode) => _map?.SetTileLodMode(mode);

    // ── Tier 2 – camera / batch projection ───────────────────────────────────
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

    // ── Location indicator ("blue dot") ──────────────────────────────────────

    public bool FollowLocation { get; set; } = true;
    public bool ShowBearing    { get; set; } = true;

    private const string LocIndLayerId = "mbgl_maui_location";
    private MbglLayer?   _locIndLayer;
    private record struct LocIndParams(double Lat, double Lon, float Bearing, float AccuracyM);
    private LocIndParams? _pendingLocInd;

    public void UpdateLocationIndicator(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        bool isFirstFix = !_pendingLocInd.HasValue;
        _pendingLocInd = new LocIndParams(lat, lon, bearing, Math.Max(5f, accuracyMeters));

        if (FollowLocation)
        {
            if (isFirstFix) JumpTo(lat, lon, GetZoom() < 8 ? 14 : GetZoom());
            else            EaseTo(lat, lon, GetZoom(), GetBearing(), GetPitch(), durationMs: 200);
        }

        if (!_styleReady || _style == null) return;
        ApplyPendingLocationIndicator();
    }

    public void ClearLocationIndicator()
    {
        _pendingLocInd = null;
        _locIndLayer   = null;
        if (_styleReady && _style?.HasLayer(LocIndLayerId) == true)
            _style.RemoveLayer(LocIndLayerId);
        _renderNeedsUpdate = true;
    }

    private void ApplyPendingLocationIndicator()
    {
        if (_pendingLocInd == null || _style == null) return;
        var p  = _pendingLocInd.Value;
        var ic = System.Globalization.CultureInfo.InvariantCulture;

        if (_locIndLayer == null)
        {
            if (_style.HasLayer(LocIndLayerId)) _style.RemoveLayer(LocIndLayerId);
            _locIndLayer = _style.AddLocationIndicatorLayer(LocIndLayerId);
            _locIndLayer.SetPaintProperty("accuracy-radius-color", "\"rgba(30,136,229,0.3)\"");
            _locIndLayer.SetPaintProperty("accuracy-radius-border-color", "\"rgba(30,136,229,0.85)\"");
        }

        _locIndLayer.SetPaintProperty("location",
            $"[{p.Lat.ToString(ic)},{p.Lon.ToString(ic)},0]");
        _locIndLayer.SetPaintProperty("bearing",
            (ShowBearing ? p.Bearing : 0f).ToString(ic));
        _locIndLayer.SetPaintProperty("accuracy-radius", p.AccuracyM.ToString(ic));

        _renderNeedsUpdate = true;
    }

    public void OnPointerWheelChanged(double delta, double cx, double cy)
        => _map?.OnScroll(delta, cx, cy);

    public void OnPointerPressed(double x, double y)
        => _map?.OnPanStart(x, y);

    public void OnPointerMoved(double dx, double dy)
        => _map?.OnPanMove(dx, dy);

    public void OnPointerReleased()
        => _map?.OnPanEnd();

    public void OnDoubleTapped(double x, double y)
        => _map?.OnDoubleTap(x, y);

    public void OnPinch(double scale, double cx, double cy)
        => _map?.OnPinch(scale, cx, cy);
}
#endif
