/**
 * MlnMapImage.cs — airspace-free WPF MapLibre control.
 *
 * MapLibre renders into the framebuffer of a hidden WGL window (HiddenWglContext); each
 * frame the pixels are read back with glReadPixels into a WriteableBitmap shown by an
 * ordinary WPF Image. The map is therefore a normal WPF visual, so on-map controls are
 * real WPF children with correct z-order, clipping and hit-testing — no popups, no airspace.
 *
 * Usage in XAML:
 *   xmlns:mlwpf="clr-namespace:MapLibreNative.Maui.WPF;assembly=MapLibreNative.Maui.WPF"
 *   <mlwpf:MlnMapImage StyleUrl="https://demotiles.maplibre.org/style.json" />
 */
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MapLibreNative.Maui;

namespace MapLibreNative.Maui.WPF;

/// <summary>
/// A WPF MapLibre map control backed by a <see cref="System.Windows.Media.Imaging.WriteableBitmap"/>:
/// MapLibre renders into an off-screen GL framebuffer; each frame the pixels are read back with
/// <c>glReadPixels</c> and pushed into the bitmap.  The bitmap is displayed by an ordinary WPF
/// <see cref="Image"/> element, so on-map controls are real WPF children with correct z-order and
/// clipping — no HwndHost, no airspace, no Popup windows.
/// </summary>
public partial class MlnMapImage : Grid
{
    [DllImport("opengl32.dll")] private static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int w, int h, uint fmt, uint type, IntPtr data);
    private const uint GL_BGRA        = 0x80E1;  // GL 1.2 — all modern drivers
    private const uint GL_UNSIGNED_BYTE = 0x1401;

    // ── Dependency properties ─────────────────────────────────────────────────

    public string StyleUrl
    {
        get => (string)GetValue(StyleUrlProperty);
        set => SetValue(StyleUrlProperty, value);
    }
    public static readonly DependencyProperty StyleUrlProperty =
        DependencyProperty.Register(nameof(StyleUrl), typeof(string), typeof(MlnMapImage),
            new PropertyMetadata("https://demotiles.maplibre.org/style.json", OnStyleUrlChanged));

    private static void OnStyleUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MlnMapImage m && m._map != null && e.NewValue is string url)
        {
            if (url.TrimStart().StartsWith('{')) m._map.SetStyleJson(url);
            else m._map.SetStyleUrl(url);
            m._renderNeedsUpdate = true;
        }
    }

    /// <summary>
    /// Extra multiplier applied to the style-unit pixel ratio (text/icon/circle/line
    /// sizes) when the native map is created — surface dimensions stay in real physical
    /// pixels. Default 1.0. Lets apps honour the OS text-scale / accessibility setting,
    /// which MapLibre otherwise ignores. Set before the control initialises; read once.
    /// </summary>
    public double UiScale
    {
        get => (double)GetValue(UiScaleProperty);
        set => SetValue(UiScaleProperty, value);
    }
    public static readonly DependencyProperty UiScaleProperty =
        DependencyProperty.Register(nameof(UiScale), typeof(double), typeof(MlnMapImage),
            new PropertyMetadata(1.0));

    public bool ShowGpsControl
    {
        get => (bool)GetValue(ShowGpsControlProperty);
        set => SetValue(ShowGpsControlProperty, value);
    }
    public static readonly DependencyProperty ShowGpsControlProperty =
        DependencyProperty.Register(nameof(ShowGpsControl), typeof(bool), typeof(MlnMapImage),
            new PropertyMetadata(true, (d, e) =>
            {
                if (d is MlnMapImage m && m._gpsPanel != null)
                    m._gpsPanel.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            }));

    public bool ShowAttributionControl
    {
        get => (bool)GetValue(ShowAttributionControlProperty);
        set => SetValue(ShowAttributionControlProperty, value);
    }
    public static readonly DependencyProperty ShowAttributionControlProperty =
        DependencyProperty.Register(nameof(ShowAttributionControl), typeof(bool), typeof(MlnMapImage),
            new PropertyMetadata(true, (d, e) =>
            {
                if (d is MlnMapImage m && m._attrBorder != null)
                    m._attrBorder.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            }));

    /// <summary>
    /// Show an on-map 3D-terrain toggle button (like the navigation and GPS controls).
    /// Clicking it toggles terrain on <see cref="TerrainControlSourceId"/>: enable if off,
    /// disable if on — mirroring maplibre-gl-js's TerrainControl. The raster-dem source must
    /// already exist in the style; the control does not add sources or hillshade. Default <c>false</c>.
    /// </summary>
    public bool ShowTerrainControl
    {
        get => (bool)GetValue(ShowTerrainControlProperty);
        set => SetValue(ShowTerrainControlProperty, value);
    }
    public static readonly DependencyProperty ShowTerrainControlProperty =
        DependencyProperty.Register(nameof(ShowTerrainControl), typeof(bool), typeof(MlnMapImage),
            new PropertyMetadata(false, (d, e) =>
            {
                if (d is MlnMapImage m && m._terrainPanel != null)
                {
                    m._terrainPanel.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
                    m.RepositionControls();
                }
            }));

    /// <summary>
    /// ID of the raster-dem source the terrain control toggles. Must already be in the style.
    /// Defaults to app-specific <c>"mln-terrain-dem"</c> (not a generic name like "terrain")
    /// so it does not collide with a real style source; set it to your dem source id.
    /// </summary>
    public string TerrainControlSourceId
    {
        get => (string)GetValue(TerrainControlSourceIdProperty);
        set => SetValue(TerrainControlSourceIdProperty, value);
    }
    public static readonly DependencyProperty TerrainControlSourceIdProperty =
        DependencyProperty.Register(nameof(TerrainControlSourceId), typeof(string), typeof(MlnMapImage),
            new PropertyMetadata("mln-terrain-dem"));

    /// <summary>Vertical exaggeration the terrain control applies when enabling terrain. Default <c>1.0</c>.</summary>
    public float TerrainControlExaggeration
    {
        get => (float)GetValue(TerrainControlExaggerationProperty);
        set => SetValue(TerrainControlExaggerationProperty, value);
    }
    public static readonly DependencyProperty TerrainControlExaggerationProperty =
        DependencyProperty.Register(nameof(TerrainControlExaggeration), typeof(float), typeof(MlnMapImage),
            new PropertyMetadata(1.0f));

    public bool ShowNavigationControls
    {
        get => (bool)GetValue(ShowNavigationControlsProperty);
        set => SetValue(ShowNavigationControlsProperty, value);
    }
    public static readonly DependencyProperty ShowNavigationControlsProperty =
        DependencyProperty.Register(nameof(ShowNavigationControls), typeof(bool), typeof(MlnMapImage),
            new PropertyMetadata(true, (d, e) =>
            {
                if (d is MlnMapImage m && m._navPanel != null)
                {
                    m._navPanel.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
                    m.RepositionControls();
                }
            }));

    public MapControlCorner NavigationControlPosition
    {
        get => (MapControlCorner)GetValue(NavigationControlPositionProperty);
        set => SetValue(NavigationControlPositionProperty, value);
    }
    public static readonly DependencyProperty NavigationControlPositionProperty =
        DependencyProperty.Register(nameof(NavigationControlPosition), typeof(MapControlCorner), typeof(MlnMapImage),
            new PropertyMetadata(MapControlCorner.TopRight, OnControlPositionChanged));

    public MapControlCorner GpsControlPosition
    {
        get => (MapControlCorner)GetValue(GpsControlPositionProperty);
        set => SetValue(GpsControlPositionProperty, value);
    }
    public static readonly DependencyProperty GpsControlPositionProperty =
        DependencyProperty.Register(nameof(GpsControlPosition), typeof(MapControlCorner), typeof(MlnMapImage),
            new PropertyMetadata(MapControlCorner.TopRight, OnControlPositionChanged));

    public MapControlCorner AttributionControlPosition
    {
        get => (MapControlCorner)GetValue(AttributionControlPositionProperty);
        set => SetValue(AttributionControlPositionProperty, value);
    }
    public static readonly DependencyProperty AttributionControlPositionProperty =
        DependencyProperty.Register(nameof(AttributionControlPosition), typeof(MapControlCorner), typeof(MlnMapImage),
            new PropertyMetadata(MapControlCorner.BottomLeft, OnControlPositionChanged));

    /// <summary>Corner the terrain control is anchored to. Default <see cref="MapControlCorner.TopRight"/>.</summary>
    public MapControlCorner TerrainControlPosition
    {
        get => (MapControlCorner)GetValue(TerrainControlPositionProperty);
        set => SetValue(TerrainControlPositionProperty, value);
    }
    public static readonly DependencyProperty TerrainControlPositionProperty =
        DependencyProperty.Register(nameof(TerrainControlPosition), typeof(MapControlCorner), typeof(MlnMapImage),
            new PropertyMetadata(MapControlCorner.TopRight, OnControlPositionChanged));

    private static void OnControlPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MlnMapImage m) m.RepositionControls();
    }

    /// <summary>
    /// How the GPS control picks the camera zoom when Follow mode engages (the on-map
    /// GPS button, or the first fix while following). KeepCurrent preserves the
    /// historical behaviour; Accuracy zooms so the fix's accuracy circle is comfortably
    /// visible; Fixed always uses <see cref="GpsFollowZoom"/>. Later fixes never change
    /// the zoom, so a manual scroll zoom sticks.
    /// </summary>
    public GpsFollowZoomMode GpsFollowZoomMode
    {
        get => (GpsFollowZoomMode)GetValue(GpsFollowZoomModeProperty);
        set => SetValue(GpsFollowZoomModeProperty, value);
    }
    public static readonly DependencyProperty GpsFollowZoomModeProperty =
        DependencyProperty.Register(nameof(GpsFollowZoomMode), typeof(GpsFollowZoomMode), typeof(MlnMapImage),
            new PropertyMetadata(GpsFollowZoomMode.KeepCurrent));

    /// <summary>Zoom level applied when <see cref="GpsFollowZoomMode"/> is <see cref="MapLibreNative.Maui.GpsFollowZoomMode.Fixed"/>. Default <c>16</c>.</summary>
    public double GpsFollowZoom
    {
        get => (double)GetValue(GpsFollowZoomProperty);
        set => SetValue(GpsFollowZoomProperty, value);
    }
    public static readonly DependencyProperty GpsFollowZoomProperty =
        DependencyProperty.Register(nameof(GpsFollowZoom), typeof(double), typeof(MlnMapImage),
            new PropertyMetadata(16.0));

    // ── Visible region (read-only, bindable) ──────────────────────────────────

    private static readonly DependencyPropertyKey VisibleRegionPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(VisibleRegion),
            typeof(MapLibreNative.Maui.Geometry.MapSpan), typeof(MlnMapImage),
            new PropertyMetadata(null));

    /// <summary>
    /// The map region currently visible on screen, as a
    /// <see cref="MapLibreNative.Maui.Geometry.MapSpan"/>. Refreshed whenever the camera becomes
    /// idle and <see langword="null"/> until the map has rendered its first frame.
    /// </summary>
    public static readonly DependencyProperty VisibleRegionProperty =
        VisibleRegionPropertyKey.DependencyProperty;

    /// <inheritdoc cref="VisibleRegionProperty"/>
    public MapLibreNative.Maui.Geometry.MapSpan? VisibleRegion
    {
        get => (MapLibreNative.Maui.Geometry.MapSpan?)GetValue(VisibleRegionProperty);
        private set => SetValue(VisibleRegionPropertyKey, value);
    }

    /// <summary>
    /// Reads the map's currently visible region on demand. Returns <see langword="null"/> when the
    /// map is not yet ready.
    /// </summary>
    public MapLibreNative.Maui.Geometry.MapSpan? GetVisibleRegion()
    {
        if (_map == null) return null;
        var (latSW, lonSW, latNE, lonNE) = _map.LatLngBoundsForCamera();
        if (double.IsNaN(latSW) || double.IsNaN(lonSW) || double.IsNaN(latNE) || double.IsNaN(lonNE))
            return null;
        double centerLat = (latSW + latNE) / 2.0;
        double centerLon = (lonSW + lonNE) / 2.0;
        double latDegrees = Math.Abs(latNE - latSW);
        double lonDegrees = Math.Abs(lonNE - lonSW);
        return new MapLibreNative.Maui.Geometry.MapSpan(
            new MapLibreNative.Maui.Geometry.MapCoordinate(centerLat, centerLon), latDegrees, lonDegrees);
    }

    /// <summary>When true, each GPS fix re-centres the map. Controlled by the GPS tracking mode.</summary>
    public bool FollowLocation { get; set; } = true;

    /// <summary>When false the location indicator always points north (bearing suppressed).</summary>
    public bool ShowBearing { get; set; } = true;

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler? MapReady;
    public event EventHandler? StyleLoaded;
    public event EventHandler? CameraIdle;
    public event EventHandler<MlnMapClickEventArgs>? MapClicked;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private WriteableBitmap? _bitmap;
    private HiddenWglContext? _interop;
    private MbglRunLoop? _runLoop;
    private MbglFrontend? _frontend;
    private MbglMap? _map;
    private MbglStyle? _style;
    private DispatcherTimer? _renderTimer;

    private bool _initialized, _renderNeedsUpdate = true, _styleReady;
    // Vulkan builds render off-screen (headless) and read pixels back through the
    // frontend; OpenGL builds render into a WGL FBO and read back via glReadPixels.
    private static readonly bool _vulkan = MbglFrontend.RenderBackend == MbglRenderBackend.Vulkan;
    private float _dpi = 1f;
    private int _physW = 1, _physH = 1;

    private bool _isDragging;
    private Point _lastPos, _downPos;
    private const double ClickThresholdPx = 5;

    public MlnMapImage()
    {
        Background = Brushes.Transparent; // ensure hit-testing over the whole map

        // Shield the control's own UI (nav d-pad, GPS, attribution) from host-app
        // implicit styles. An app-level <Style TargetType="TextBlock"> with e.g.
        // Margin="5,2" otherwise leaks into these elements — the d-pad arrows live
        // in ~10px grid cells, so an inherited 5px side margin collapses them to
        // zero width and they disappear. An empty implicit style registered here
        // is resolved first for every TextBlock in this subtree.
        Resources.Add(typeof(TextBlock), new Style(typeof(TextBlock)));

        // GL renders bottom-left origin so it flips vertically; the Vulkan headless
        // read-back is already top-down, so no flip there.
        _image.RenderTransformOrigin = new Point(0.5, 0.5);
        _image.RenderTransform = new ScaleTransform(1, _vulkan ? 1 : -1);
        Children.Add(_image);

        BuildTerrainOverlay();
        BuildNavOverlay();
        BuildGpsOverlay();
        BuildAttributionOverlay();
        RepositionControls();

        Loaded += (_, _) => TryInitialize();
        Unloaded += (_, _) => Teardown();
        SizeChanged += (_, _) => UpdateSize();
    }

    private double GetDpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private void TryInitialize()
    {
        if (_initialized || ActualWidth < 2 || ActualHeight < 2) return;
        _dpi = (float)GetDpiScale();
        _physW = Math.Max(1, (int)Math.Round(ActualWidth * _dpi));
        _physH = Math.Max(1, (int)Math.Round(ActualHeight * _dpi));

        if (!_vulkan)
        {
            // OpenGL: off-screen WGL context we glReadPixels from each frame.
            _interop = new HiddenWglContext();
            _interop.Initialize();
            _interop.Resize(_physW, _physH);
        }
        CreateBitmap(_physW, _physH);

        // UiScale multiplies only the style-unit pixel ratio (text/icon/circle/line
        // sizes) — the surface dimensions above stay in real physical pixels.
        float pixelRatio = _dpi * (float)UiScale;

        _runLoop = new MbglRunLoop();
        // Vulkan renders headless (no surface handle); OpenGL needs the WGL HDC + context.
        // pixelRatio (= _dpi * UiScale) scales style-unit sizes; the surface dims above stay physical.
        _frontend = _vulkan
            ? new MbglFrontend(IntPtr.Zero, IntPtr.Zero, _physW, _physH, pixelRatio,
                () => _renderNeedsUpdate = true)
            : new MbglFrontend(_interop!.Hdc, _interop.GlContext, _physW, _physH, pixelRatio,
                () => _renderNeedsUpdate = true);
        // Persistent tile/resource cache (mbgl's default is :memory:), shared
        // with MbglOfflineManager via MbglCache.DefaultPath.
        _map = new MbglMap(_frontend, _runLoop, cachePath: MbglCache.DefaultPath,
                           pixelRatio: pixelRatio, observer: OnMapObserverEvent);
        _map.SetSize(_physW, _physH);

        var url = StyleUrl;
        if (!string.IsNullOrEmpty(url))
        {
            if (url.TrimStart().StartsWith('{')) _map.SetStyleJson(url);
            else _map.SetStyleUrl(url);
        }

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();

        _initialized = true;
        MapReady?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSize()
    {
        if (!_initialized || _map == null || _frontend == null) return;
        if (ActualWidth < 1 || ActualHeight < 1) return;
        _dpi = (float)GetDpiScale();
        int w = Math.Max(1, (int)Math.Round(ActualWidth * _dpi));
        int h = Math.Max(1, (int)Math.Round(ActualHeight * _dpi));
        if (w == _physW && h == _physH) return;
        _physW = w; _physH = h;
        if (_interop != null) { _interop.MakeCurrent(); _interop.Resize(w, h); }  // OpenGL only
        CreateBitmap(w, h);
        _frontend.SetSize(w, h);
        _map.SetSize(w, h);
        _renderNeedsUpdate = true;
    }

    private void CreateBitmap(int w, int h)
    {
        // DPI = 96 * _dpi → logical size = w / _dpi = ActualWidth, so the image fills the grid exactly.
        _bitmap = new WriteableBitmap(w, h, 96 * _dpi, 96 * _dpi, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        _runLoop?.RunOnce();
        if (!_renderNeedsUpdate || _frontend == null || _bitmap == null) return;
        _renderNeedsUpdate = false;

        if (_vulkan)
        {
            // Headless Vulkan: render off-screen, then copy the frame into the bitmap.
            try { _frontend.Render(); } catch { return; /* swallow per-frame render faults */ }
            _bitmap.Lock();
            _frontend.ReadPixels(_bitmap.BackBuffer, (nuint)((long)_physW * _physH * 4));
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _physW, _physH));
            _bitmap.Unlock();
        }
        else
        {
            if (_interop == null) return;
            _interop.MakeCurrent();
            glViewport(0, 0, _interop.Width, _interop.Height);
            try { _frontend.Render(); } catch { return; /* swallow per-frame render faults */ }

            // Read pixels (GL bottom-left → WPF top-left; _image has ScaleTransform(1,-1) to compensate).
            _bitmap.Lock();
            _interop.ReadPixels(_bitmap.BackBuffer);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _physW, _physH));
            _bitmap.Unlock();
        }
    }

    /// <summary>
    /// Returns a frozen copy of the current rendered frame (BGRA32), or null if no
    /// frame has been produced yet. Test/diagnostic hook — lets a headless harness
    /// inspect what the map actually drew (e.g. to verify terrain draping changes
    /// the output). The GL framebuffer is bottom-left origin, so the returned image
    /// is vertically flipped relative to on-screen (the live view applies a
    /// ScaleTransform(1,-1) to compensate); pixel content/statistics are unaffected.
    /// </summary>
    public BitmapSource? SnapshotBitmap()
    {
        if (_bitmap == null) return null;
        var clone = _bitmap.Clone();
        clone.Freeze();
        return clone;
    }

    // ── Camera API ────────────────────────────────────────────────────────────

    public void CenterOn(double latitude, double longitude, double zoom = 14.0)
    {
        if (_map == null) return;
        _map.JumpTo(latitude, longitude, zoom);
        _renderNeedsUpdate = true;
    }

    public void JumpTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0)
    {
        if (_map == null) return;
        _map.JumpTo(latitude, longitude, zoom, bearing, pitch);
        _renderNeedsUpdate = true;
    }

    public void EaseTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0, long durationMs = 300)
    {
        if (_map == null) return;
        _map.EaseTo(latitude, longitude, zoom, bearing, pitch, durationMs);
        _renderNeedsUpdate = true;
    }

    public void FlyTo(double latitude, double longitude, double zoom,
        double bearing = 0, double pitch = 0, long durationMs = 500)
    {
        if (_map == null) return;
        _map.FlyTo(latitude, longitude, zoom, bearing, pitch, durationMs);
        _renderNeedsUpdate = true;
    }

    // Camera – edge padding overloads. Padding (screen px, top/left/bottom/right)
    // centres the target in the unobscured part of the viewport — use when a
    // panel or overlay covers part of the map. Pass double.NaN for zoom /
    // bearing / pitch to keep the current value.

    public void JumpTo(double latitude, double longitude, double zoom,
        double bearing, double pitch,
        double padTop, double padLeft, double padBottom, double padRight)
    {
        if (_map == null) return;
        _map.JumpTo(latitude, longitude, zoom, bearing, pitch,
                    padTop, padLeft, padBottom, padRight);
        _renderNeedsUpdate = true;
    }

    public void EaseTo(double latitude, double longitude, double zoom,
        double bearing, double pitch,
        double padTop, double padLeft, double padBottom, double padRight,
        long durationMs = 300)
    {
        if (_map == null) return;
        _map.EaseTo(latitude, longitude, zoom, bearing, pitch,
                    padTop, padLeft, padBottom, padRight, durationMs);
        _renderNeedsUpdate = true;
    }

    public void FlyTo(double latitude, double longitude, double zoom,
        double bearing, double pitch,
        double padTop, double padLeft, double padBottom, double padRight,
        long durationMs = 500)
    {
        if (_map == null) return;
        _map.FlyTo(latitude, longitude, zoom, bearing, pitch,
                   padTop, padLeft, padBottom, padRight, durationMs);
        _renderNeedsUpdate = true;
    }

    /// <summary>Multiply the map scale by <paramref name="scale"/> (2.0 = one zoom
    /// level in), optionally about a screen anchor point (NaN = viewport centre).</summary>
    public void ScaleBy(double scale, double anchorX = double.NaN, double anchorY = double.NaN,
        long durationMs = 0)
    {
        if (_map == null) return;
        _map.ScaleBy(scale, anchorX, anchorY, durationMs);
        _renderNeedsUpdate = true;
    }

    public void ZoomIn()
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom + 1, _map.Bearing, _map.Pitch, durationMs: 250);
        _renderNeedsUpdate = true;
    }

    public void ZoomOut()
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom - 1, _map.Bearing, _map.Pitch, durationMs: 250);
        _renderNeedsUpdate = true;
    }

    /// <summary>Rotate the map by <paramref name="deltaDeg"/> (positive = clockwise).</summary>
    public void RotateBy(double deltaDeg)
    {
        if (_map == null || deltaDeg == 0) return;
        OnUserRotatedMap();
        // Snap the target onto the increment grid (multiples of |deltaDeg|) so a full
        // rotation returns exactly to the start, instead of accumulating float error
        // by incrementing the live (possibly mid-animation) bearing each ease.
        double step = Math.Abs(deltaDeg);
        double target = Math.Round(_map.Bearing / step) * step + deltaDeg;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom, target, _map.Pitch, durationMs: 200);
        _renderNeedsUpdate = true;
    }

    /// <summary>Tilt the map by <paramref name="deltaDeg"/>, clamped to 0–60°.</summary>
    public void PitchBy(double deltaDeg)
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        double newPitch = Math.Max(0, Math.Min(60, _map.Pitch + deltaDeg));
        _map.EaseTo(lat, lon, _map.Zoom, _map.Bearing, newPitch, durationMs: 200);
        _renderNeedsUpdate = true;
    }

    // ── Sources / layers / queries ─────────────────────────────────────────────

    public void AddGeoJsonSource(string sourceId, string geojson)
    {
        if (_style == null) return;
        MbglSource src = _style.HasSource(sourceId) ? _style.GetSource(sourceId)! : _style.AddGeoJsonSource(sourceId);
        src.SetGeoJson(geojson);
        _renderNeedsUpdate = true;
    }

    /// <summary>
    /// Add a GeoJSON source with style-spec options (clustering etc.).
    /// <paramref name="optionsJson"/> is a JSON object of GeoJSON source options,
    /// e.g. <c>{"cluster":true,"clusterRadius":50,"clusterMaxZoom":14}</c>.
    /// Options only apply at creation; an existing source keeps its original
    /// options and just gets new data.
    /// </summary>
    public void AddGeoJsonSource(string sourceId, string geojson, string? optionsJson)
    {
        if (_style == null) return;
        MbglSource src = _style.HasSource(sourceId)
            ? _style.GetSource(sourceId)!
            : _style.AddGeoJsonSourceOptions(sourceId, optionsJson);
        src.SetGeoJson(geojson);
        _renderNeedsUpdate = true;
    }

    public void SetGeoJsonSource(string sourceId, string geojson)
    {
        if (_style == null) return;
        _style.GetSource(sourceId)?.SetGeoJson(geojson);
        _renderNeedsUpdate = true;
    }

    public void AddGeoJsonSourceUrl(string sourceId, string url)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId)) _style.AddGeoJsonSourceUrl(sourceId, url);
        _renderNeedsUpdate = true;
    }

    public void AddVectorSourceUrl(string sourceId, string tileJsonUrl)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId)) _style.AddVectorSource(sourceId, tileJsonUrl);
        _renderNeedsUpdate = true;
    }

    // ── 3D terrain ────────────────────────────────────────────────────────────

    /// <summary>Enables 3D terrain from an existing raster-dem source in the style.</summary>
    public void SetTerrain(string sourceId, float exaggeration = 1.0f)
    {
        if (_style == null) return;
        _style.SetTerrain(sourceId, exaggeration);
        _renderNeedsUpdate = true;
    }

    /// <summary>Disables 3D terrain; the map renders flat again.</summary>
    public void RemoveTerrain()
    {
        if (_style == null) return;
        _style.RemoveTerrain();
        _renderNeedsUpdate = true;
    }

    /// <summary>Turns 3D terrain on if it is off, or off if it is on.</summary>
    public void ToggleTerrain(string sourceId, float exaggeration = 1.0f)
    {
        if (_style == null) return;
        if (_style.IsTerrainEnabled) _style.RemoveTerrain();
        else _style.SetTerrain(sourceId, exaggeration);
        _renderNeedsUpdate = true;
    }

    /// <summary>Whether 3D terrain is currently enabled.</summary>
    public bool IsTerrainEnabled => _style != null && _style.IsTerrainEnabled;

    public void AddRasterSource(string sourceId, string url, int tileSize = 512)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId)) _style.AddRasterSource(sourceId, url, tileSize);
        _renderNeedsUpdate = true;
    }

    public void AddRasterDemSource(string sourceId, string url, int tileSize = 512)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId)) _style.AddRasterDemSource(sourceId, url, tileSize);
        _renderNeedsUpdate = true;
    }

    public void AddImageSource(string sourceId, string url,
        double lat0 = 0, double lon0 = 0, double lat1 = 0, double lon1 = 0,
        double lat2 = 0, double lon2 = 0, double lat3 = 0, double lon3 = 0,
        bool hasCoordinates = false)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId))
        {
            if (hasCoordinates)
                _style.AddImageSource(sourceId, url, lat0, lon0, lat1, lon1, lat2, lon2, lat3, lon3);
            else
                _style.AddRasterSource(sourceId, url);
        }
        _renderNeedsUpdate = true;
    }

    public void AddSourceJson(string sourceId, string sourceJson)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId)) _style.AddSourceJson(sourceId, sourceJson);
        _renderNeedsUpdate = true;
    }

    public void AddCircleLayer(
        string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0)
    {
        if (_style == null) return;
        if (_style.HasLayer(layerName)) return;
        var layer = _style.AddCircleLayer(layerName, sourceName, belowLayerId);
        ApplyLayerProperties(layer, properties);
        if (minZoom > 0) layer.SetMinZoom(minZoom);
        if (maxZoom > 0) layer.SetMaxZoom(maxZoom);
        if (sourceLayer != null) layer.SetSourceLayer(sourceLayer);
        _renderNeedsUpdate = true;
    }

    /// <summary>Adds a line layer for the given source (e.g. paths / measure lines).
    /// <paramref name="properties"/> are style-spec line paint/layout keys such as
    /// <c>line-color</c>, <c>line-width</c>, <c>line-dasharray</c>.</summary>
    public void AddLineLayer(
        string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0)
    {
        if (_style == null) return;
        if (_style.HasLayer(layerName)) return;
        var layer = _style.AddLineLayer(layerName, sourceName, belowLayerId);
        ApplyLayerProperties(layer, properties);
        if (minZoom > 0) layer.SetMinZoom(minZoom);
        if (maxZoom > 0) layer.SetMaxZoom(maxZoom);
        if (sourceLayer != null) layer.SetSourceLayer(sourceLayer);
        _renderNeedsUpdate = true;
    }

    /// <summary>Adds a fill layer for the given source (e.g. polygons).
    /// <paramref name="properties"/> are style-spec fill paint/layout keys such as
    /// <c>fill-color</c>, <c>fill-opacity</c>, <c>fill-outline-color</c>.</summary>
    public void AddFillLayer(
        string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0)
    {
        if (_style == null) return;
        if (_style.HasLayer(layerName)) return;
        var layer = _style.AddFillLayer(layerName, sourceName, belowLayerId);
        ApplyLayerProperties(layer, properties);
        if (minZoom > 0) layer.SetMinZoom(minZoom);
        if (maxZoom > 0) layer.SetMaxZoom(maxZoom);
        if (sourceLayer != null) layer.SetSourceLayer(sourceLayer);
        _renderNeedsUpdate = true;
    }

    /// <summary>Adds a raster layer for the given raster/image source (e.g. image
    /// overlays or tiled raster basemaps). <paramref name="properties"/> are
    /// style-spec raster keys such as <c>raster-opacity</c>, <c>raster-resampling</c>.
    /// Raster sources have no sub-layers, so <paramref name="sourceLayer"/> is
    /// normally <c>null</c>.</summary>
    public void AddRasterLayer(
        string layerName, string sourceName, string? belowLayerId,
        string? sourceLayer, IDictionary<string, object?> properties,
        float minZoom = 0, float maxZoom = 0)
    {
        if (_style == null) return;
        if (_style.HasLayer(layerName)) return;
        var layer = _style.AddRasterLayer(layerName, sourceName, belowLayerId);
        ApplyLayerProperties(layer, properties);
        if (minZoom > 0) layer.SetMinZoom(minZoom);
        if (maxZoom > 0) layer.SetMaxZoom(maxZoom);
        if (sourceLayer != null) layer.SetSourceLayer(sourceLayer);
        _renderNeedsUpdate = true;
    }

    /// <summary>
    /// Adds a hillshade layer over a raster-dem source. Terrain draping displaces
    /// map geometry by DEM height but that is nearly invisible over flat-coloured
    /// fills; a hillshade layer from the same DEM shades the relief so 3D terrain
    /// reads clearly. No-op if the layer already exists or there is no style.
    /// </summary>
    public void AddHillshadeLayer(string layerName, string sourceName, string? belowLayerId = null)
    {
        if (_style == null) return;
        if (_style.HasLayer(layerName)) return;
        _style.AddHillshadeLayer(layerName, sourceName, belowLayerId);
        _renderNeedsUpdate = true;
    }

    public void RemoveLayer(string layerId)
    {
        if (_style == null) return;
        if (_style.HasLayer(layerId)) _style.RemoveLayer(layerId);
        _renderNeedsUpdate = true;
    }

    public void RemoveSource(string sourceId)
    {
        if (_style == null) return;
        if (_style.HasSource(sourceId)) _style.RemoveSource(sourceId);
        _renderNeedsUpdate = true;
    }

    /// <summary>
    /// Returns GeoJSON of rendered features in a box around (<paramref name="cx"/>, <paramref name="cy"/>)
    /// within <paramref name="thresholdPx"/> physical pixels, optionally filtered to <paramref name="layerIds"/>.
    /// </summary>
    public string? QueryRenderedFeaturesInBox(double cx, double cy, double thresholdPx = 5,
        string[]? layerIds = null)
    {
        if (_map == null) return null;
        string? filter = layerIds is { Length: > 0 } ? string.Join(",", layerIds) : null;
        return _map.QueryRenderedFeaturesInBox(
            cx - thresholdPx, cy - thresholdPx, cx + thresholdPx, cy + thresholdPx, filter);
    }

    /// <summary>
    /// Query all features in a source's data, regardless of visibility.
    /// Returns a GeoJSON FeatureCollection string, or null if the renderer is not ready.
    /// </summary>
    /// <param name="sourceLayerIds">Comma-separated source-layer names — required for
    /// vector sources, ignored for GeoJSON sources.</param>
    /// <param name="filterJson">Optional style-spec filter expression JSON.</param>
    public string? QuerySourceFeatures(string sourceId, string? sourceLayerIds = null,
        string? filterJson = null)
        => _map?.QuerySourceFeatures(sourceId, sourceLayerIds, filterJson);

    /// <summary>Zoom level at which the given cluster (a Feature from a rendered-features
    /// query on a clustered GeoJSON source) expands into children, or null.</summary>
    public double? GetClusterExpansionZoom(string sourceId, string clusterFeatureJson)
        => _map?.GetClusterExpansionZoom(sourceId, clusterFeatureJson);

    /// <summary>Direct children of a cluster as a GeoJSON FeatureCollection string, or null.</summary>
    public string? GetClusterChildren(string sourceId, string clusterFeatureJson)
        => _map?.GetClusterChildren(sourceId, clusterFeatureJson);

    /// <summary>Up to <paramref name="limit"/> leaf features of a cluster (from
    /// <paramref name="offset"/>) as a GeoJSON FeatureCollection string, or null.</summary>
    public string? GetClusterLeaves(string sourceId, string clusterFeatureJson,
        uint limit = 10, uint offset = 0)
        => _map?.GetClusterLeaves(sourceId, clusterFeatureJson, limit, offset);

    /// <summary>Wraps a pre-serialised JSON string so layer properties forward it verbatim (e.g. expressions).</summary>
    public record RawJson(string Json);

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

    private static void ApplyLayerProperties(MbglLayer layer, IDictionary<string, object?> props)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var (name, val) in props)
        {
            if (val == null) continue;
            string json = val switch
            {
                RawJson r => r.Json,
                string s => $"\"{s}\"",
                bool b => b ? "true" : "false",
                double d => d.ToString(ic),
                float f => f.ToString(ic),
                int i => i.ToString(),
                long l => l.ToString(),
                _ => System.Text.Json.JsonSerializer.Serialize(val),
            };
            if (LayoutPropertyNames.Contains(name))
                layer.SetLayoutProperty(name, json);
            else
                layer.SetPaintProperty(name, json);
        }
    }

    // ── Input (real WPF routed events — no WndProc) ────────────────────────────

    // The map surface is the only element that should drive pan/zoom/click. Overlay controls
    // (nav / GPS / attribution) are siblings on top; a press on one of them must reach that
    // control's own handler instead of being captured here for a map pan.
    private bool IsOnMapSurface(RoutedEventArgs e)
        => ReferenceEquals(e.OriginalSource, _image) || ReferenceEquals(e.OriginalSource, this);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_map == null || !IsOnMapSurface(e)) return;
        var pos = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            var dp = ToPhysical(pos);
            _map.OnDoubleTap(dp.X, dp.Y);
            _renderNeedsUpdate = true;
            e.Handled = true;
            return;
        }
        _isDragging = true;
        _downPos = _lastPos = pos;
        CaptureMouse();
        var p = ToPhysical(_lastPos);
        _map.OnPanStart(p.X, p.Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_map == null || !_isDragging) return;
        var pos = e.GetPosition(this);
        var d = ToPhysical(new Point(pos.X - _lastPos.X, pos.Y - _lastPos.Y));
        _lastPos = pos;
        if (d.X == 0 && d.Y == 0) return;   // a plain click can raise MouseMove with no travel
        _map.OnPanMove(d.X, d.Y);
        _renderNeedsUpdate = true;
        OnUserPannedMap();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_map == null || !_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();
        _map.OnPanEnd();
        _map.TriggerRepaint();
        _renderNeedsUpdate = true;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _downPos.X) <= ClickThresholdPx && Math.Abs(pos.Y - _downPos.Y) <= ClickThresholdPx)
        {
            var p = ToPhysical(pos);
            var ll = _map.LatLngForPixel(p.X, p.Y);
            MapClicked?.Invoke(this, new MlnMapClickEventArgs((int)p.X, (int)p.Y, ll.Lat, ll.Lon));
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_map == null || !IsOnMapSurface(e)) return;
        var p = ToPhysical(e.GetPosition(this));
        _map.OnScroll((double)e.Delta / 120, p.X, p.Y);
        _renderNeedsUpdate = true;
        e.Handled = true;
    }

    private (double X, double Y) ToPhysical(Point dip) => (dip.X * _dpi, dip.Y * _dpi);

    // ── Nav overlay (real WPF children — the whole point) ──────────────────────

    private StackPanel? _navPanel;
    private RotateTransform? _compassRotate;

    private void BuildNavOverlay()
    {
        _navPanel = new StackPanel { Width = NavPanelW };

        _navPanel.Children.Add(BuildDpad());
        _navPanel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(218, 218, 218)) });

        var zoomInBtn = MakeNavButton("+", ZoomIn);
        SetButtonCorners(zoomInBtn, 0, 0, 0, 0);
        _navPanel.Children.Add(zoomInBtn);

        _navPanel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(218, 218, 218)) });

        var zoomOutBtn = MakeNavButton("\u2212", ZoomOut);
        SetButtonCorners(zoomOutBtn, 0, 0, 4, 4);
        _navPanel.Children.Add(zoomOutBtn);

        Children.Add(_navPanel);
    }

    private Border BuildDpad()
    {
        var root = new Grid { Width = NavPanelW, Height = NavDpadH, Background = Brushes.White };

        // 3×3 hit grid: edges = direction buttons, centre = reset, corners empty.
        var grid = new Grid();
        for (int i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        void Add(UIElement el, int row, int col) { Grid.SetRow(el, row); Grid.SetColumn(el, col); grid.Children.Add(el); }

        Add(MakeDpadArrow("\u25B2", () => PitchBy(10)),  0, 1);  // ▲ up    → more tilt
        Add(MakeDpadArrow("\u25C0", () => RotateBy(-15)), 1, 0);  // ◀ left  → rotate ccw
        Add(MakeDpadArrow(null,      ResetNorth),         1, 1);  // centre  → reset north
        Add(MakeDpadArrow("\u25B6", () => RotateBy(15)),  1, 2);  // ▶ right → rotate cw
        Add(MakeDpadArrow("\u25BC", () => PitchBy(-10)), 2, 1);  // ▼ down  → less tilt
        root.Children.Add(grid);

        // Hollow compass ring around the arrows (non-interactive).
        double ringSize = NavDpadH * 0.80;
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width               = ringSize,
            Height              = ringSize,
            Stroke              = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
            StrokeThickness     = 1,
            Fill                = Brushes.Transparent,
            IsHitTestVisible    = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        root.Children.Add(ring);

        // North tick — a short blue mark at the top of the ring that rotates with bearing.
        _compassRotate = new RotateTransform { Angle = 0 };
        var northTick = new System.Windows.Shapes.Rectangle
        {
            Width               = 2,
            Height              = NavDpadH * 0.14,
            Fill                = new SolidColorBrush(Color.FromRgb(10, 102, 204)),
            IsHitTestVisible    = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, (NavDpadH - ringSize) / 2, 0, 0),
        };
        var tickHost = new Grid
        {
            Width                 = NavDpadH,
            Height                = NavDpadH,
            IsHitTestVisible      = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform       = _compassRotate,
        };
        tickHost.Children.Add(northTick);
        root.Children.Add(tickHost);

        return new Border
        {
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            ClipToBounds = true,
            Child        = root,
        };
    }

    private static Border MakeDpadArrow(string? text, Action onClick)
    {
        var btn = new Border
        {
            Background = Brushes.Transparent,
            Cursor     = Cursors.Hand,
        };
        if (text != null)
        {
            btn.Child = new TextBlock
            {
                Text                = text,
                FontSize            = 8,
                Foreground          = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                IsHitTestVisible    = false,
            };
        }
        btn.MouseEnter += (_, _) => btn.Background = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
        btn.MouseLeave += (_, _) => btn.Background = Brushes.Transparent;
        btn.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return btn;
    }

    private static Border MakeNavButton(string? text, Action onClick)
    {
        var btn = new Border
        {
            Width      = NavPanelW,
            Height     = 29,
            Background = Brushes.White,
            Cursor     = Cursors.Hand,
        };
        if (text != null)
        {
            btn.Child = new TextBlock
            {
                Text                = text,
                FontSize            = 18,
                FontWeight          = FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                IsHitTestVisible    = false,
            };
        }
        btn.MouseEnter  += (_, _) => btn.Background = new SolidColorBrush(Color.FromRgb(240, 240, 240));
        btn.MouseLeave  += (_, _) => btn.Background = Brushes.White;
        btn.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return btn;
    }

    private static void SetButtonCorners(Border b, double topLeft, double topRight, double bottomRight, double bottomLeft)
        => b.CornerRadius = new CornerRadius(topLeft, topRight, bottomRight, bottomLeft);

    // ── Control positioning (corner anchoring + stacking) ──────────────────────

    private const double ControlEdgeMargin = 10;
    private const double ControlStackGap    = 10;
    private const double NavPanelW           = 29;          // matches GPS button width so controls align when stacked
    private const double NavDpadH            = 29;          // round d-pad height (square with panel width)
    private const double NavPanelH           = NavDpadH + 29 * 2 + 2;  // d-pad + 2 zoom buttons + 2 separator px
    private const double GpsPanelH           = 29 * 2 + 1;  // 2 buttons + 1 separator
    private const double TerrainPanelH       = 30;          // single toggle button

    private void RepositionControls()
    {
        // Terrain sits at the top of its corner column (above navigation), so it anchors
        // at offset 0; nav and GPS shift inward by the visible controls stacked above them.
        bool terrainVis = ShowTerrainControl && _terrainPanel?.Visibility == Visibility.Visible;

        if (_terrainPanel != null)
            ApplyCorner(_terrainPanel, TerrainControlPosition, 0);

        if (_navPanel != null)
        {
            double off = terrainVis && NavigationControlPosition == TerrainControlPosition
                ? TerrainPanelH + ControlStackGap : 0;
            ApplyCorner(_navPanel, NavigationControlPosition, off);
        }

        if (_gpsPanel != null)
        {
            // Stack the GPS panel inward from the terrain + nav panels when they share a corner.
            double off = 0;
            if (terrainVis && GpsControlPosition == TerrainControlPosition)
                off += TerrainPanelH + ControlStackGap;
            if (ShowNavigationControls && _navPanel?.Visibility == Visibility.Visible
                && GpsControlPosition == NavigationControlPosition)
                off += NavPanelH + ControlStackGap;
            ApplyCorner(_gpsPanel, GpsControlPosition, off);
        }

        if (_attrBorder != null)
            ApplyCorner(_attrBorder, AttributionControlPosition, 0);
    }

    private static void ApplyCorner(FrameworkElement el, MapControlCorner corner, double stackOffset)
    {
        bool left = corner is MapControlCorner.TopLeft or MapControlCorner.BottomLeft;
        bool top = corner is MapControlCorner.TopLeft or MapControlCorner.TopRight;
        el.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        el.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        double edge = ControlEdgeMargin + stackOffset;
        el.Margin = new Thickness(
            left ? ControlEdgeMargin : 0,
            top ? edge : 0,
            left ? 0 : ControlEdgeMargin,
            top ? 0 : edge);
    }

    // ── mbgl observer ─────────────────────────────────────────────────────────

    private void OnMapObserverEvent(string eventName, string? detail)
    {
        switch (eventName)
        {
            case "onDidFinishLoadingStyle":
                Dispatcher.BeginInvoke(() =>
                {
                    _styleReady = true;
                    _locIndLayer = null; // invalidated by style reload
                    _style = _map?.GetStyle();
                    _attrLoaded = false;        // new style — sources may have different attribution
                    _appliedAttribution = null; // …and the banner should show once for it
                    _renderNeedsUpdate = true;
                    if (_pendingLocInd.HasValue) ApplyPendingLocationIndicator();
                    RefreshAttribution();
                    RebuildItemsLayer();
                    RefreshTerrainButton();   // terrain state resets with the new style
                    StyleLoaded?.Invoke(this, EventArgs.Empty);
                });
                break;
            case "onCameraIsChanging":
                Dispatcher.BeginInvoke(RefreshCompassRotation);
                break;
            case "onCameraDidChange":
                Dispatcher.BeginInvoke(() => { VisibleRegion = GetVisibleRegion(); RefreshGpsBearingButton(); RefreshCompassRotation(); CameraIdle?.Invoke(this, EventArgs.Empty); });
                break;
            case "onSourceChanged":
                // Always refresh: fires when a source's TileJSON metadata loads, including
                // sources added dynamically after the style is already loaded.
                Dispatcher.BeginInvoke(RefreshAttribution);
                break;
            case "onDidBecomeIdle":
                // Fallback: retry attribution if onSourceChanged fired before the string was ready.
                if (!_attrLoaded) Dispatcher.BeginInvoke(RefreshAttribution);
                break;
            case "onDidFinishRenderingFramePlacementChanged":
                _map?.TriggerRepaint();
                break;
        }
    }

    // ── Extra camera helpers ───────────────────────────────────────────────────

    /// <summary>Rotate the map back to north (bearing 0).</summary>
    public void ResetNorth()
    {
        if (_map == null) return;
        // A GPS-driven bearing would immediately rotate away from north again — release it.
        if (_gpsBearingMode == GpsBearingMode.GpsBearing) OnUserRotatedMap();
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom, bearing: 0, _map.Pitch, durationMs: 300);
        _renderNeedsUpdate = true;
    }

    private void PanTo(double lat, double lon)
    {
        if (_map == null) return;
        _map.EaseTo(lat, lon, _map.Zoom, _map.Bearing, _map.Pitch, durationMs: 200);
        _renderNeedsUpdate = true;
    }

    // ── Location indicator ("blue dot") ────────────────────────────────────────

    private const string LocIndLayerId = "mln_image_location";
    private MbglLayer? _locIndLayer;
    private record struct LocIndParams(double Lat, double Lon, float Bearing, float AccuracyM);
    private LocIndParams? _pendingLocInd;

    /// <summary>Show (or update) the user-location blue dot. Safe to call before the style loads.</summary>
    public void UpdateLocationIndicator(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        bool isFirstFix = !_pendingLocInd.HasValue;
        _pendingLocInd = new LocIndParams(lat, lon, bearing, Math.Max(5f, accuracyMeters));
        if (FollowLocation)
        {
            if (isFirstFix) CenterOn(lat, lon);
            else PanTo(lat, lon);
        }
        if (_styleReady && _style != null) ApplyPendingLocationIndicator();
    }

    public void ClearLocationIndicator()
    {
        _pendingLocInd = null;
        _locIndLayer = null;
        if (_style != null && _styleReady && _style.HasLayer(LocIndLayerId))
            _style.RemoveLayer(LocIndLayerId);
        _renderNeedsUpdate = true;
    }

    private void ApplyPendingLocationIndicator()
    {
        if (_pendingLocInd == null || _style == null) return;
        var p = _pendingLocInd.Value;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (_locIndLayer == null)
        {
            if (_style.HasLayer(LocIndLayerId)) _style.RemoveLayer(LocIndLayerId);
            _locIndLayer = _style.AddLocationIndicatorLayer(LocIndLayerId);
            _locIndLayer.SetPaintProperty("accuracy-radius-color", "\"rgba(30,136,229,0.3)\"");
            _locIndLayer.SetPaintProperty("accuracy-radius-border-color", "\"rgba(30,136,229,0.85)\"");
        }
        _locIndLayer.SetPaintProperty("location", $"[{p.Lat.ToString(ic)},{p.Lon.ToString(ic)},0]");
        _locIndLayer.SetPaintProperty("bearing", (ShowBearing ? p.Bearing : 0f).ToString(ic));
        _locIndLayer.SetPaintProperty("accuracy-radius", p.AccuracyM.ToString(ic));
        _renderNeedsUpdate = true;
    }

    // ── GPS control ────────────────────────────────────────────────────────────

    private enum GpsTrackingMode { Off, Show, Follow }
    private enum GpsBearingMode  { Free, NorthUp, GpsBearing }
    private GpsTrackingMode _gpsTrackingMode = GpsTrackingMode.Off;
    private GpsBearingMode  _gpsBearingMode = GpsBearingMode.Free;
    private double _lastGpsLat, _lastGpsLon;
    private float _lastGpsBearing, _lastGpsAccuracy;
    private bool _hasGpsFix;

    private StackPanel? _gpsPanel;
    private Border? _gpsBtnTracking;
    private TextBlock? _gpsTrackingIcon;
    private Border? _gpsBtnBearing;
    private TextBlock? _gpsBearingIcon;

    /// <summary>Feed a GPS fix; honoured per the current tracking mode (Off / Show / Follow)
    /// and bearing mode (Free / NorthUp / GpsBearing).</summary>
    public void UpdateGpsLocation(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        _lastGpsLat = lat; _lastGpsLon = lon; _lastGpsBearing = bearing; _lastGpsAccuracy = accuracyMeters;
        bool isFirstFix = !_hasGpsFix;
        _hasGpsFix = true;
        if (_gpsTrackingMode == GpsTrackingMode.Off) return;

        _pendingLocInd = new LocIndParams(lat, lon, bearing, Math.Max(5f, accuracyMeters));
        if (_gpsTrackingMode == GpsTrackingMode.Follow && _map != null)
        {
            double cameraBearing = CameraBearingForMode();
            if (isFirstFix) _map.JumpTo(lat, lon, FollowEntryZoom(), cameraBearing, _map.Pitch);
            else _map.EaseTo(lat, lon, _map.Zoom, cameraBearing, _map.Pitch, durationMs: 200);
        }
        else if (_gpsBearingMode == GpsBearingMode.GpsBearing && _map != null)
        {
            // Not following the position, but still tracking the GPS bearing.
            var (cLat, cLon) = _map.Center;
            _map.EaseTo(cLat, cLon, _map.Zoom, bearing, _map.Pitch, durationMs: 200);
        }
        if (_styleReady && _style != null) ApplyPendingLocationIndicator();
        _renderNeedsUpdate = true;
        if (isFirstFix) RefreshGpsTrackingButton();
    }

    private void CycleGpsMode()
    {
        _gpsTrackingMode = _gpsTrackingMode switch
        {
            GpsTrackingMode.Off  => GpsTrackingMode.Show,
            GpsTrackingMode.Show => GpsTrackingMode.Follow,
            _                    => GpsTrackingMode.Off,
        };
        ApplyGpsMode();
        RefreshGpsTrackingButton();
    }

    /// <summary>Cycle camera bearing mode: Free → NorthUp → GpsBearing → Free.</summary>
    private void CycleGpsBearingMode()
    {
        _gpsBearingMode = _gpsBearingMode switch
        {
            GpsBearingMode.Free    => GpsBearingMode.NorthUp,
            GpsBearingMode.NorthUp => GpsBearingMode.GpsBearing,
            _                      => GpsBearingMode.Free,
        };
        RefreshGpsBearingButton();

        // Rotate the camera to the newly selected reference immediately.
        if (_map != null && _gpsBearingMode != GpsBearingMode.Free
            && (_gpsBearingMode == GpsBearingMode.NorthUp || _hasGpsFix))
        {
            var (lat, lon) = _map.Center;
            double target = _gpsBearingMode == GpsBearingMode.NorthUp ? 0 : _lastGpsBearing;
            _map.EaseTo(lat, lon, _map.Zoom, target, _map.Pitch, durationMs: 300);
            _renderNeedsUpdate = true;
        }
    }

    /// <summary>Camera bearing to use for GPS-driven camera moves, per the bearing mode.</summary>
    private double CameraBearingForMode() => _gpsBearingMode switch
    {
        GpsBearingMode.NorthUp    => 0,
        GpsBearingMode.GpsBearing => _lastGpsBearing,
        _                         => _map?.Bearing ?? 0,
    };

    /// <summary>Zoom to use when Follow mode engages, per <see cref="GpsFollowZoomMode"/>.
    /// Later fixes keep the live zoom so a manual scroll zoom sticks.</summary>
    private double FollowEntryZoom() => GpsFollowZoomMode switch
    {
        Maui.GpsFollowZoomMode.Fixed    => Math.Clamp(GpsFollowZoom, 1, 22),
        Maui.GpsFollowZoomMode.Accuracy => AccuracyZoom(),
        _                               => (_map?.Zoom ?? 0) < 8 ? 14 : _map?.Zoom ?? 14,
    };

    /// <summary>Zoom at which the fix's accuracy circle spans ~⅓ of the shorter viewport
    /// side — a sharp fix lands at street level (clamped to 17), a coarse cell-grade fix
    /// stays zoomed out to cover its uncertainty.</summary>
    private double AccuracyZoom()
    {
        double acc    = Math.Max(5, _lastGpsAccuracy);
        double minDim = Math.Min(ActualWidth, ActualHeight);
        if (minDim < 1) minDim = 800;
        // metres per style pixel at zoom z (512px tiles): 78271.517 * cos(lat) / 2^z
        double targetMpp = (2 * acc) / (0.33 * minDim);
        double zoom = Math.Log2(78271.517 * Math.Cos(_lastGpsLat * Math.PI / 180.0) / targetMpp);
        return Math.Clamp(zoom, 10, 17);
    }

    private void ApplyGpsMode()
    {
        if (_gpsTrackingMode == GpsTrackingMode.Off)
        {
            ClearLocationIndicator();
        }
        else if (_hasGpsFix)
        {
            _pendingLocInd = new LocIndParams(_lastGpsLat, _lastGpsLon, _lastGpsBearing, Math.Max(5f, _lastGpsAccuracy));
            if (_gpsTrackingMode == GpsTrackingMode.Follow && _map != null)
            {
                _map.EaseTo(_lastGpsLat, _lastGpsLon, FollowEntryZoom(), CameraBearingForMode(), _map.Pitch, durationMs: 300);
            }
            if (_styleReady && _style != null) ApplyPendingLocationIndicator();
            _renderNeedsUpdate = true;
        }
    }

    /// <summary>A user pan gesture moved the map: drop Follow back to Show (the button
    /// re-enters Follow with one click), matching maplibre-gl-js GeolocateControl.</summary>
    private void OnUserPannedMap()
    {
        if (_gpsTrackingMode != GpsTrackingMode.Follow) return;
        _gpsTrackingMode = GpsTrackingMode.Show;
        RefreshGpsTrackingButton();
    }

    /// <summary>A manual rotation (d-pad or RotateBy) took over the bearing: drop back to Free.</summary>
    private void OnUserRotatedMap()
    {
        if (_gpsBearingMode == GpsBearingMode.Free) return;
        _gpsBearingMode = GpsBearingMode.Free;
        RefreshGpsBearingButton();
    }

    private void RefreshGpsTrackingButton()
    {
        if (_gpsTrackingIcon == null || _gpsBtnTracking == null) return;
        switch (_gpsTrackingMode)
        {
            case GpsTrackingMode.Show:
                _gpsTrackingIcon.Text = "⊙";
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));
                _gpsBtnTracking.Background = Brushes.White;
                break;
            case GpsTrackingMode.Follow:
                _gpsTrackingIcon.Text = "◎";
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x70, 0xC5));
                _gpsBtnTracking.Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFF));
                break;
            default:
                _gpsTrackingIcon.Text = "○";
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                _gpsBtnTracking.Background = Brushes.White;
                break;
        }
    }

    private void RefreshGpsBearingButton()
    {
        if (_gpsBearingIcon == null || _gpsBtnBearing == null) return;
        switch (_gpsBearingMode)
        {
            case GpsBearingMode.NorthUp:
                _gpsBearingIcon.Text = "N";
                _gpsBearingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x70, 0xC5));
                _gpsBtnBearing.Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFF));
                break;
            case GpsBearingMode.GpsBearing:
                _gpsBearingIcon.Text = "➤";
                _gpsBearingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x7C, 0x00));
                _gpsBtnBearing.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0));
                break;
            default:
                _gpsBearingIcon.Text = "↺";
                _gpsBearingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                _gpsBtnBearing.Background = Brushes.White;
                break;
        }
    }

    private void RefreshCompassRotation()
    {
        if (_compassRotate == null || _map == null) return;
        _compassRotate.Angle = -_map.Bearing;
    }

    private void BuildGpsOverlay()
    {
        _gpsPanel = new StackPanel { Width = NavPanelW };
        _gpsTrackingIcon = new TextBlock
        {
            Text = "○",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _gpsBtnTracking = MakeIconButton(_gpsTrackingIcon, CycleGpsMode, true);
        _gpsBearingIcon = new TextBlock
        {
            Text = "↺",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _gpsBtnBearing = MakeIconButton(_gpsBearingIcon, CycleGpsBearingMode, false);
        _gpsPanel.Children.Add(_gpsBtnTracking);
        _gpsPanel.Children.Add(_gpsBtnBearing);
        Children.Add(_gpsPanel);
    }

    private StackPanel? _terrainPanel;
    private TextBlock? _terrainIcon;
    private Border? _terrainBtn;

    private void BuildTerrainOverlay()
    {
        _terrainPanel = new StackPanel
        {
            Width = NavPanelW,
            Visibility = ShowTerrainControl ? Visibility.Visible : Visibility.Collapsed,
        };
        _terrainIcon = new TextBlock
        {
            Text = "⛰",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _terrainBtn = MakeSoloButton(_terrainIcon, ToggleTerrainControl);
        _terrainPanel.Children.Add(_terrainBtn);
        Children.Add(_terrainPanel);
        RefreshTerrainButton();
    }

    /// <summary>Toggles terrain on the configured source (enable if off, disable if on), then refreshes the button.</summary>
    private void ToggleTerrainControl()
    {
        ToggleTerrain(TerrainControlSourceId, TerrainControlExaggeration);
        RefreshTerrainButton();
    }

    /// <summary>Colours the terrain button to reflect whether terrain is currently enabled.</summary>
    private void RefreshTerrainButton()
    {
        if (_terrainIcon == null || _terrainBtn == null) return;
        bool on = IsTerrainEnabled;
        _terrainIcon.Foreground = new SolidColorBrush(on ? Color.FromRgb(0x00, 0x70, 0xC5) : Color.FromRgb(0x55, 0x55, 0x55));
        _terrainBtn.Background = on ? new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFF)) : Brushes.White;
        _terrainBtn.ToolTip = on ? "Disable 3D terrain" : "Enable 3D terrain";
    }

    // A standalone rounded icon button (all four corners), for single-button panels.
    private static Border MakeSoloButton(TextBlock icon, Action onClick)
    {
        var b = new Border
        {
            Height = 30,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 218)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            Child = icon,
        };
        b.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }

    private static Border MakeIconButton(TextBlock icon, Action onClick, bool top)
    {
        var b = new Border
        {
            Height = 30,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 218)),
            BorderThickness = new Thickness(1, top ? 1 : 0, 1, 1),
            CornerRadius = top ? new CornerRadius(4, 4, 0, 0) : new CornerRadius(0, 0, 4, 4),
            Cursor = Cursors.Hand,
            Child = icon,
        };
        b.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }

    // ── Attribution ────────────────────────────────────────────────────────────

    private Border? _attrBorder;
    private TextBlock? _attrTextBlock;
    private string _attrText = string.Empty;
    private bool _attrCollapsed = true;
    private bool _attrLoaded;           // true once real source attributions have been fetched
    private string? _appliedAttribution; // content currently shown — banner re-expands only when this changes
    private DispatcherTimer? _attrCollapseTimer;

    private void BuildAttributionOverlay()
    {
        _attrTextBlock = new TextBlock
        {
            Text = "ⓘ",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        };
        _attrBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 0xF8, 0xF8, 0xF8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 0, 10),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = _attrTextBlock,
        };
        _attrBorder.MouseLeftButtonUp += (_, e) =>
        {
            if (_attrCollapsed) ExpandAttribution(pinned: true); else CollapseAttribution();
            e.Handled = true;
        };
        Children.Add(_attrBorder);
    }

    private void RefreshAttribution()
    {
        if (_style == null || _attrTextBlock == null || _attrBorder == null) return;
        var parts = MbglStyle.EnsureMapLibreAttribution(_style.GetSourceAttributions());
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            var text = StripHtml(part);
            if (text.Length == 0) continue;
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(text);
        }
        _attrText = sb.ToString();
        if (_attrText.Length > 0 && ShowAttributionControl)
        {
            _attrLoaded = true;

            // onSourceChanged fires for every runtime source mutation (e.g. an app
            // refreshing a GeoJSON source on a timer). Only re-expand the banner when
            // the attribution content actually changed — otherwise a periodic source
            // update keeps popping it open.
            if (_attrText == _appliedAttribution) return;
            _appliedAttribution = _attrText;

            _attrBorder.Visibility = Visibility.Visible;
            ExpandAttribution();
        }
        else
        {
            _appliedAttribution = null;
            _attrBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void ExpandAttribution(bool pinned = false)
    {
        if (_attrTextBlock == null || _attrText.Length == 0) return;
        // A deliberate click on the collapsed ⓘ chip gets a longer read window than
        // the standard auto-shown flash — matching the Android/iOS pinned behaviour.
        _attrCollapsed = false;
        _attrTextBlock.Text = _attrText;
        _attrCollapseTimer?.Stop();
        _attrCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(pinned ? 10 : 5) };
        _attrCollapseTimer.Tick += (_, _) => CollapseAttribution();
        _attrCollapseTimer.Start();
    }

    private void CollapseAttribution()
    {
        _attrCollapseTimer?.Stop();
        _attrCollapseTimer = null;
        if (_attrTextBlock == null) return;
        _attrCollapsed = true;
        _attrTextBlock.Text = "ⓘ";
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var sb = new System.Text.StringBuilder(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private void Teardown()
    {
        _renderTimer?.Stop();
        _renderTimer = null;
        _attrCollapseTimer?.Stop();
        _attrCollapseTimer = null;
        _map?.Dispose(); _map = null;
        _frontend?.Dispose(); _frontend = null;
        _runLoop?.Dispose(); _runLoop = null;
        _interop?.Dispose(); _interop = null;
        _initialized = false;
        _styleReady = false;
        _locIndLayer = null;
    }
}
