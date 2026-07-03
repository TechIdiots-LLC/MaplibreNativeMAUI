/**
 * MbglMapHost.cs — WPF HwndHost that embeds a MapLibre Native OpenGL map
 * using the same mln-cabi.dll C ABI as the MAUI path.
 *
 * Drop-in replacement for VistumblerCS's MaplibreMapHost (C++/CLI), but backed by
 * pure P/Invoke via MapLibreNative.Maui — no C++/CLI or MAUI dependency.
 *
 * Usage in XAML:
 *   xmlns:mlwpf="clr-namespace:MapLibreNative.Maui.WPF;assembly=MapLibreNative.Maui.WPF"
 *   <mlwpf:MbglMapHost StyleUrl="https://demotiles.maplibre.org/style.json" />
 */
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MapLibreNative.Maui;

namespace MapLibreNative.Maui.WPF;

/// <summary>
/// WPF HwndHost that embeds a MapLibre Native OpenGL map rendered by mln-cabi.dll.
/// Handles its own OpenGL context, RunLoop, pan/zoom/double-tap input and optional
/// navigation + attribution overlay popups.
/// </summary>
public class MlnMapHost : HwndHost
{
    private static readonly string LogPath =
        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mbgl_wpf_log.txt");

    private static void Log(string s)
    {
        try { System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n"); } catch { }
    }

    // ── Public dependency properties ──────────────────────────────────────────

    public string StyleUrl
    {
        get => (string)GetValue(StyleUrlProperty);
        set => SetValue(StyleUrlProperty, value);
    }
    public static readonly DependencyProperty StyleUrlProperty =
        DependencyProperty.Register(nameof(StyleUrl), typeof(string), typeof(MlnMapHost),
            new PropertyMetadata("https://demotiles.maplibre.org/style.json", OnStyleUrlChanged));

    private static void OnStyleUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MlnMapHost h && h._map != null && e.NewValue is string url)
        {
            if (url.TrimStart().StartsWith('{'))
                h._map.SetStyleJson(url);
            else
                h._map.SetStyleUrl(url);
        }
    }

    public bool ShowNavigationControls
    {
        get => (bool)GetValue(ShowNavigationControlsProperty);
        set => SetValue(ShowNavigationControlsProperty, value);
    }
    public static readonly DependencyProperty ShowNavigationControlsProperty =
        DependencyProperty.Register(nameof(ShowNavigationControls), typeof(bool), typeof(MlnMapHost),
            new PropertyMetadata(true, OnShowNavChanged));

    private static void OnShowNavChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MlnMapHost h) return;
        h.UpdateNavPopupOpen();
        // Stacked controls below the nav panel shift up/down as it is hidden/shown.
        h.PositionGpsPopup();
        h.PositionAttributionPopup();
    }

    public bool ShowGpsControl
    {
        get => (bool)GetValue(ShowGpsControlProperty);
        set => SetValue(ShowGpsControlProperty, value);
    }
    public static readonly DependencyProperty ShowGpsControlProperty =
        DependencyProperty.Register(nameof(ShowGpsControl), typeof(bool), typeof(MlnMapHost),
            new PropertyMetadata(true, (d, _) => { if (d is MlnMapHost h) { h.UpdateGpsPopupOpen(); h.PositionAttributionPopup(); } }));

    /// <summary>
    /// Corner the navigation control is anchored to. When multiple controls share
    /// a corner they stack (navigation, then GPS, then attribution). Default TopRight.
    /// </summary>
    public MapControlCorner NavigationControlPosition
    {
        get => (MapControlCorner)GetValue(NavigationControlPositionProperty);
        set => SetValue(NavigationControlPositionProperty, value);
    }
    public static readonly DependencyProperty NavigationControlPositionProperty =
        DependencyProperty.Register(nameof(NavigationControlPosition), typeof(MapControlCorner), typeof(MlnMapHost),
            new PropertyMetadata(MapControlCorner.TopRight, OnControlPositionChanged));

    /// <summary>
    /// Corner the GPS control is anchored to. When multiple controls share a
    /// corner they stack (navigation, then GPS, then attribution). Default TopRight.
    /// </summary>
    public MapControlCorner GpsControlPosition
    {
        get => (MapControlCorner)GetValue(GpsControlPositionProperty);
        set => SetValue(GpsControlPositionProperty, value);
    }
    public static readonly DependencyProperty GpsControlPositionProperty =
        DependencyProperty.Register(nameof(GpsControlPosition), typeof(MapControlCorner), typeof(MlnMapHost),
            new PropertyMetadata(MapControlCorner.TopRight, OnControlPositionChanged));

    /// <summary>
    /// Corner the attribution control is anchored to. When multiple controls share
    /// a corner they stack (navigation, then GPS, then attribution). Default BottomLeft.
    /// </summary>
    public MapControlCorner AttributionControlPosition
    {
        get => (MapControlCorner)GetValue(AttributionControlPositionProperty);
        set => SetValue(AttributionControlPositionProperty, value);
    }
    public static readonly DependencyProperty AttributionControlPositionProperty =
        DependencyProperty.Register(nameof(AttributionControlPosition), typeof(MapControlCorner), typeof(MlnMapHost),
            new PropertyMetadata(MapControlCorner.BottomLeft, OnControlPositionChanged));

    private static void OnControlPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MlnMapHost h) return;
        h.PositionNavPopup();
        h.PositionGpsPopup();
        h.PositionAttributionPopup();
    }

    // ── Public non-DP properties ──────────────────────────────────────────────

    /// <summary>When true, each GPS fix also re-centres the map (preserves zoom after the first fix).</summary>
    public bool FollowLocation { get; set; } = true;

    /// <summary>When false the bearing arrow is suppressed — indicator always points north.</summary>
    public bool ShowBearing { get; set; } = true;

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler? MapReady;
    public event EventHandler? StyleLoaded;
    public event EventHandler? DidBecomeIdle;
    public event EventHandler? CameraIdle;
    /// <summary>Fired when the user taps/clicks the map without panning.</summary>
    public event EventHandler<MlnMapClickEventArgs>? MapClicked;

    // ── Public camera helpers ─────────────────────────────────────────────────

    public void CenterOn(double latitude, double longitude, double zoom = 14.0)
    {
        if (_map == null) return;
        _map.JumpTo(latitude, longitude, zoom);
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

    public void ResetNorth()
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        double currentBearing = _map.Bearing;
        double newPitch = Math.Abs(currentBearing) < 0.5 ? 0 : _map.Pitch;
        _map.EaseTo(lat, lon, _map.Zoom, bearing: 0, pitch: newPitch, durationMs: 300);
        _renderNeedsUpdate = true;
    }

    /// <summary>Rotate the map by <paramref name="deltaDeg"/> (positive = clockwise).</summary>
    public void RotateBy(double deltaDeg)
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom, _map.Bearing + deltaDeg, _map.Pitch, durationMs: 200);
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

    private void PanTo(double lat, double lon)
    {
        if (_map == null) return;
        _map.EaseTo(lat, lon, _map.Zoom, _map.Bearing, _map.Pitch, durationMs: 200);
        _renderNeedsUpdate = true;
    }

    // ── GeoJSON source API ────────────────────────────────────────────────────

    public void AddGeoJsonSource(string sourceId, string geojson)
    {
        if (_style == null) return;
        MbglSource src;
        if (!_style.HasSource(sourceId))
            src = _style.AddGeoJsonSource(sourceId);
        else
            src = _style.GetSource(sourceId)!;
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
        if (!_style.HasSource(sourceId))
            _style.AddGeoJsonSourceUrl(sourceId, url);
        _renderNeedsUpdate = true;
    }

    /// <summary>
    /// Add a vector tile source backed by a TileJSON URL (type:"vector").
    /// MapLibre will fetch the TileJSON descriptor and request individual
    /// MVT tiles as the viewport changes.  No-op if the source already exists.
    /// </summary>
    public void AddVectorSourceUrl(string sourceId, string tileJsonUrl)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId))
            _style.AddVectorSource(sourceId, tileJsonUrl);
        _renderNeedsUpdate = true;
    }

    /// <summary>
    /// Add a raster tile source from a TileJSON or tile-template URL.
    /// <paramref name="tileSize"/> defaults to 512.
    /// No-op if the source already exists.
    /// </summary>
    public void AddRasterSource(string sourceId, string url, int tileSize = 512)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId))
            _style.AddRasterSource(sourceId, url, tileSize);
        _renderNeedsUpdate = true;
    }

    /// <summary>
    /// Add a raster-DEM tile source (for hillshade/terrain) from a TileJSON or tile-template URL.
    /// <paramref name="tileSize"/> defaults to 512.
    /// No-op if the source already exists.
    /// </summary>
    public void AddRasterDemSource(string sourceId, string url, int tileSize = 512)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId))
            _style.AddRasterDemSource(sourceId, url, tileSize);
        _renderNeedsUpdate = true;
    }

    /// <summary>
    /// Add an image source pinned to the map by its four corner coordinates.
    /// <paramref name="coordinates"/> order: top-right, top-left, bottom-right, bottom-left.
    /// When <paramref name="coordinates"/> is <c>null</c> the source is added as a plain raster source.
    /// No-op if the source already exists.
    /// </summary>
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

    /// <summary>
    /// Add a source from a raw MapLibre source-spec JSON object.
    /// Accepts any source type — geojson, vector, raster, raster-dem, image, etc.
    /// The JSON must include a <c>"type"</c> field, e.g.:
    /// <c>{"type":"vector","url":"https://example.com/tilejson.json"}</c>
    /// No-op if a source with <paramref name="sourceId"/> already exists.
    /// </summary>
    public void AddSourceJson(string sourceId, string sourceJson)
    {
        if (_style == null) return;
        if (!_style.HasSource(sourceId))
            _style.AddSourceJson(sourceId, sourceJson);
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

    // ── Feature queries ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns GeoJSON string of features in a box around (<paramref name="cx"/>, <paramref name="cy"/>)
    /// within <paramref name="thresholdPx"/> physical pixels on each side, optionally filtered to
    /// the given <paramref name="layerIds"/>. Returns <c>null</c> when the map is not ready.
    /// </summary>
    public string? QueryRenderedFeaturesInBox(double cx, double cy, double thresholdPx = 5,
        string[]? layerIds = null)
    {
        if (_map == null) return null;
        string? filter = layerIds is { Length: > 0 } ? string.Join(",", layerIds) : null;
        return _map.QueryRenderedFeaturesInBox(
            cx - thresholdPx, cy - thresholdPx,
            cx + thresholdPx, cy + thresholdPx,
            filter);
    }

    // ── Location indicator ("blue dot") ──────────────────────────────────────

    private const string LocIndLayerId = "mbgl_wpf_location";
    private MbglLayer? _locIndLayer;
    private record struct LocIndParams(double Lat, double Lon, float Bearing, float AccuracyM);
    private LocIndParams? _pendingLocInd;

    /// <summary>
    /// Show (or update) the user-location blue dot at the given position.
    /// Safe to call before the style is loaded; the position is stored and applied
    /// once StyleLoaded fires.
    /// </summary>
    public void UpdateLocationIndicator(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        bool isFirstFix = !_pendingLocInd.HasValue;
        _pendingLocInd = new LocIndParams(lat, lon, bearing, Math.Max(5f, accuracyMeters));

        if (FollowLocation)
        {
            if (isFirstFix) CenterOn(lat, lon);
            else            PanTo(lat, lon);
        }

        if (!_styleReady || _style == null) return;
        ApplyPendingLocationIndicator();
    }

    public void ClearLocationIndicator()
    {
        _pendingLocInd = null;
        _locIndLayer   = null;
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
            if (_style.HasLayer(LocIndLayerId))
                _style.RemoveLayer(LocIndLayerId);
            _locIndLayer = _style.AddLocationIndicatorLayer(LocIndLayerId);
            // Fixed appearance: semi-transparent blue fill, solid border
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

    // ── Win32 / WGL P/Invoke ──────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int   ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool  ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    // Monitor work area — keeps popups above the taskbar
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref WpfMonitorInfo mi);
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)]
    private struct WpfMonitorInfo
    {
        public uint  cbSize;
        public WpfRect rcMonitor;
        public WpfRect rcWork;
        public uint  dwFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WpfRect { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Returns the work area of the monitor containing the given DEVICE-pixel point,
    /// expressed in WPF logical (device-independent) pixels.
    /// Each popup passes its own intended center position so the correct monitor is
    /// queried even when the map window spans two screens (multi-monitor).
    /// Falls back to <see cref="SystemParameters.WorkArea"/> on failure.
    /// </summary>
    private Rect GetWorkAreaLogicalAt(Point devicePt)
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget == null) return SystemParameters.WorkArea;
        var mi   = new WpfMonitorInfo { cbSize = (uint)Marshal.SizeOf<WpfMonitorInfo>() };
        var pt   = new POINT { X = (int)devicePt.X, Y = (int)devicePt.Y };
        var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero || !GetMonitorInfoW(hMon, ref mi)) return SystemParameters.WorkArea;
        var toLogical = src.CompositionTarget.TransformFromDevice;
        var tl = toLogical.Transform(new Point(mi.rcWork.Left,  mi.rcWork.Top));
        var br = toLogical.Transform(new Point(mi.rcWork.Right, mi.rcWork.Bottom));
        return new Rect(tl, br);
    }

    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hDC);
    [DllImport("opengl32.dll")] private static extern bool   wglDeleteContext(IntPtr hGLRC);
    [DllImport("opengl32.dll")] private static extern bool   wglMakeCurrent(IntPtr hDC, IntPtr hGLRC);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string procName);
    [DllImport("opengl32.dll")] private static extern void   glViewport(int x, int y, int width, int height);
    [DllImport("opengl32.dll")] private static extern void   glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll")] private static extern void   glClear(uint mask);

    private const uint GL_COLOR_BUFFER_BIT   = 0x00004000;
    private const uint GL_DEPTH_BUFFER_BIT   = 0x00000100;
    private const uint GL_STENCIL_BUFFER_BIT = 0x00000400;
    private const uint GL_FRAMEBUFFER        = 0x8D40;

    private delegate void glBindFramebufferDelegate(uint target, uint framebuffer);
    private glBindFramebufferDelegate? _glBindFramebuffer;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WglCreateContextAttribsARBDelegate(IntPtr hDC, IntPtr hShareContext, int[] attribList);

    [DllImport("gdi32.dll")] private static extern int  ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int iPixelFormat, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct WNDCLASSEXA
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPStr)] public string  lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern ushort RegisterClassExA(ref WNDCLASSEXA wc);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcA(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint   dwFlags;
        public byte   iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte   cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte   cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint   dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    private const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    private const uint PFD_SUPPORT_OPENGL = 0x00000020;
    private const uint PFD_DOUBLEBUFFER   = 0x00000001;
    private const uint WS_CHILD           = 0x40000000;
    private const uint WS_VISIBLE         = 0x10000000;
    private const uint WS_CLIPCHILDREN    = 0x02000000;
    private const uint WS_CLIPSIBLINGS    = 0x04000000;
    private const int  WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    private const int  WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const uint CS_OWNDC  = 0x0020;
    private const uint CS_DBLCLKS = 0x0008;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    private const string GlWindowClass = "MbglWpfGLChild";
    private static WndProcDelegate? _wndProcKeepAlive;
    private static bool             _classRegistered;

    private static void EnsureWindowClassRegistered()
    {
        if (_classRegistered) return;
        _wndProcKeepAlive = (hWnd, msg, w, l) => DefWindowProcA(hWnd, msg, w, l);
        var wc = new WNDCLASSEXA
        {
            cbSize        = (uint)Marshal.SizeOf<WNDCLASSEXA>(),
            style         = CS_OWNDC | CS_DBLCLKS,
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance     = GetModuleHandleW(IntPtr.Zero),
            lpszClassName = GlWindowClass,
        };
        RegisterClassExA(ref wc);
        _classRegistered = true;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private IntPtr _childHwnd = IntPtr.Zero;
    private IntPtr _hDC       = IntPtr.Zero;
    private IntPtr _hGLRC     = IntPtr.Zero;

    private MbglRunLoop?  _runLoop;
    private MbglFrontend? _frontend;
    private MbglMap?      _map;
    private MbglStyle?    _style;

    private DispatcherTimer? _renderTimer;
    private bool  _initialized;
    private bool  _styleReady;
    private bool  _renderNeedsUpdate = true;
    private float _dpi = 1.0f;
    private int   _renderTickCount;

    // ── Input state ───────────────────────────────────────────────────────────

    private bool  _isDragging;
    private int   _lastMouseX;
    private int   _lastMouseY;
    private int   _mouseDownX;
    private int   _mouseDownY;
    private const int ClickThresholdPx = 5;

    private const int WM_LBUTTONDOWN   = 0x0201;
    private const int WM_LBUTTONUP     = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_MOUSEMOVE     = 0x0200;
    private const int WM_MOUSEWHEEL    = 0x020A;
    private const int WM_SETCURSOR     = 0x0020;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    private const int IDC_ARROW   = 32512;
    private const int IDC_HAND    = 32649;
    private const int IDC_SIZEALL = 32646;

    // ── Navigation popup ──────────────────────────────────────────────────────

    private Popup?           _navPopup;
    private RotateTransform? _compassRotate;
    private Point?           _navDesired;

    // ── GPS control popup ─────────────────────────────────────────────────────

    private Popup?  _gpsPopup;
    private Border? _gpsBtnTracking;   // top button — GPS tracking state
    private Border? _gpsBtnBearing;    // bottom button — bearing reset
    private TextBlock? _gpsTrackingIcon;

    private enum GpsTrackingMode { Off, Show, Follow, FollowBearing }
    private GpsTrackingMode _gpsTrackingMode = GpsTrackingMode.Off;
    private double _lastGpsLat, _lastGpsLon;
    private float  _lastGpsBearing, _lastGpsAccuracy;
    private bool   _hasGpsFix;

    // ── Attribution popup ─────────────────────────────────────────────────────

    private Popup?           _attributionPopup;
    private TextBlock?       _attributionText;
    private Border?          _attributionBorder;
    private Point?           _attributionDesired;
    private Popup?           _attrButtonPopup;    // collapsed ⓘ button
    private Border?          _attrButtonBorder;   // root Border of the ⓘ button (for height measurement)
    private DispatcherTimer? _attrCollapseTimer;
    private bool             _attrLoaded;         // true once attribution content has been fetched

    // ── HwndHost overrides ────────────────────────────────────────────────────

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClassRegistered();
        int initW = Math.Max(1, (int)ActualWidth);
        int initH = Math.Max(1, (int)ActualHeight);

        _childHwnd = CreateWindowEx(
            0, GlWindowClass, "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, initW, initH,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += (_, _) =>
        {
            PositionNavPopup();
            PositionGpsPopup();
            UpdateNavPopupOpen();
            UpdateGpsPopupOpen();
            Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)PositionAttributionPopup);
        };

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)TryInitialize);
        return new HandleRef(this, _childHwnd);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool visible = (bool)e.NewValue;
        if (visible)
        {
            _renderNeedsUpdate = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)TryInitialize);
        }
        else
            _renderTimer?.Stop();

        UpdateNavPopupOpen();
        UpdateAttributionPopupOpen();
        UpdateGpsPopupOpen();
    }

    private void TryInitialize()
    {
        if (_initialized) { _renderTimer?.Start(); return; }
        if (!IsVisible || ActualWidth < 2 || ActualHeight < 2 || _childHwnd == IntPtr.Zero) return;

        _dpi  = GetDpiScale();
        int physW = Math.Max(1, (int)(ActualWidth  * _dpi));
        int physH = Math.Max(1, (int)(ActualHeight * _dpi));
        SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0, physW, physH, 0x0040);

        _initialized = true;
        try { InitOpenGl(physW, physH);    Log("InitOpenGl OK"); }
        catch (Exception ex) { Log($"InitOpenGl EX: {ex}"); throw; }
        try { InitMaplibre(physW, physH);  Log("InitMaplibre OK"); }
        catch (Exception ex) { Log($"InitMaplibre EX: {ex}"); throw; }
        try { InitNavPopup();              }
        catch (Exception ex) { Log($"InitNavPopup EX: {ex}"); }
        try { InitAttributionPopup();      }
        catch (Exception ex) { Log($"InitAttributionPopup EX: {ex}"); }
        try { InitGpsPopup();              }
        catch (Exception ex) { Log($"InitGpsPopup EX: {ex}"); }

        // Hide popups when the window loses focus so they don't float over other apps.
        var parentWin = Window.GetWindow(this);
        if (parentWin != null)
        {
            parentWin.Deactivated += (_, _) =>
            {
                if (_navPopup         != null) _navPopup.IsOpen         = false;
                if (_attributionPopup != null) _attributionPopup.IsOpen = false;
                if (_attrButtonPopup  != null) _attrButtonPopup.IsOpen  = false;
                if (_gpsPopup         != null) _gpsPopup.IsOpen         = false;
                _attrCollapseTimer?.Stop();
            };
            parentWin.Activated += (_, _) =>
            {
                if (parentWin.WindowState == WindowState.Minimized) return;
                UpdateNavPopupOpen();
                UpdateAttributionPopupOpen();
                UpdateGpsPopupOpen();
                if (_attrLoaded) CollapseAttribution();
            };
            parentWin.StateChanged += (_, _) =>
            {
                if (parentWin.WindowState == WindowState.Minimized)
                {
                    _attrCollapseTimer?.Stop();
                    if (_navPopup         != null) _navPopup.IsOpen         = false;
                    if (_attributionPopup != null) _attributionPopup.IsOpen = false;
                    if (_attrButtonPopup  != null) _attrButtonPopup.IsOpen  = false;
                    if (_gpsPopup         != null) _gpsPopup.IsOpen         = false;
                }
                else
                {
                    _renderNeedsUpdate = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        if (parentWin.WindowState == WindowState.Minimized) return;
                        UpdateNavPopupOpen();
                        UpdateGpsPopupOpen();
                        if (_attrLoaded) CollapseAttribution();
                    });
                }
            };
            parentWin.LocationChanged += (_, _) => 
            { 
                PositionNavPopup();
                PositionGpsPopup();
                PositionAttributionPopup();
                // Re-open popups to force WPF to recalculate their screen positions
                if (_navPopup != null && ShowNavigationControls && IsVisible)
                {
                    var wasOpen = _navPopup.IsOpen;
                    _navPopup.IsOpen = false;
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        if (parentWin.WindowState != WindowState.Minimized)
                            _navPopup.IsOpen = wasOpen;
                    });
                }
                if (_gpsPopup != null && ShowGpsControl && IsVisible)
                {
                    var gpsWasOpen = _gpsPopup.IsOpen;
                    _gpsPopup.IsOpen = false;
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        if (parentWin.WindowState != WindowState.Minimized)
                            _gpsPopup.IsOpen = gpsWasOpen;
                    });
                }
                if (_attrLoaded && _initialized && IsVisible)
                {
                    bool attrWasOpen = _attributionPopup?.IsOpen == true;
                    bool btnWasOpen  = _attrButtonPopup?.IsOpen  == true;
                    if (_attributionPopup != null) _attributionPopup.IsOpen = false;
                    if (_attrButtonPopup  != null) _attrButtonPopup.IsOpen  = false;
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        if (parentWin.WindowState == WindowState.Minimized) return;
                        if (_attributionPopup != null) _attributionPopup.IsOpen = attrWasOpen;
                        if (_attrButtonPopup  != null) _attrButtonPopup.IsOpen  = btnWasOpen;
                    });
                }
            };
        }

        _renderTimer?.Start();
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        IsVisibleChanged -= OnIsVisibleChanged;
        _renderTimer?.Stop();
        _renderTimer = null;

        if (_navPopup         != null) { _navPopup.IsOpen         = false; _navPopup         = null; }
        if (_attributionPopup != null) { _attributionPopup.IsOpen = false; _attributionPopup = null; }
        if (_attrButtonPopup  != null) { _attrButtonPopup.IsOpen  = false; _attrButtonPopup  = null; }
        if (_gpsPopup         != null) { _gpsPopup.IsOpen         = false; _gpsPopup         = null; }
        _attrCollapseTimer?.Stop(); _attrCollapseTimer = null;
        _attrLoaded        = false;
        _attributionText   = null;
        _attributionBorder = null;
        _gpsTrackingIcon   = null;
        _gpsBtnTracking    = null;
        _gpsBtnBearing     = null;

        _styleReady      = false;
        _locIndLayer     = null;
        _pendingLocInd   = null;

        _map?.Dispose();      _map      = null;
        _frontend?.Dispose(); _frontend = null;
        _runLoop?.Dispose();  _runLoop  = null;

        if (_hGLRC != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(_hGLRC); _hGLRC = IntPtr.Zero; }
        if (_hDC   != IntPtr.Zero) { ReleaseDC(_childHwnd, _hDC); _hDC = IntPtr.Zero; }
        if (_childHwnd != IntPtr.Zero) { DestroyWindow(_childHwnd); _childHwnd = IntPtr.Zero; }
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        PositionNavPopup();
        PositionGpsPopup();
        PositionGpsPopup();
        PositionAttributionPopup();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (info.NewSize.Width < 1 || info.NewSize.Height < 1) return;

        // Refresh _dpi here so moving the window to a monitor with different DPI
        // (e.g., from a 100% laptop screen to a 200% 4K external display) keeps
        // the physical pixel dimensions in sync.
        float dpi = GetDpiScale();
        _dpi = dpi;
        int wP = Math.Max(1, (int)(info.NewSize.Width  * dpi));
        int hP = Math.Max(1, (int)(info.NewSize.Height * dpi));

        if (_childHwnd != IntPtr.Zero)
            SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0, wP, hP, 0x0056);

        if (_frontend != null && _map != null)
        {
            _frontend.SetSize(wP, hP);
            _map.SetSize(wP, hP);
            for (int i = 0; i < 4; i++) _runLoop?.RunOnce();
        }
        _renderNeedsUpdate = true;

        PositionNavPopup();
        PositionGpsPopup();
        UpdateNavPopupOpen();
        UpdateGpsPopupOpen();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)PositionAttributionPopup);

        if (!_initialized && IsVisible)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)TryInitialize);
    }

    // ── OpenGL + MapLibre init ────────────────────────────────────────────────

    private void InitOpenGl(int physW, int physH)
    {
        _hDC = GetDC(_childHwnd);
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize      = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion   = 1,
            dwFlags    = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8,
        };
        int fmt = ChoosePixelFormat(_hDC, ref pfd);
        SetPixelFormat(_hDC, fmt, ref pfd);

        var tmpCtx = wglCreateContext(_hDC);
        wglMakeCurrent(_hDC, tmpCtx);

        var fn = wglGetProcAddress("wglCreateContextAttribsARB");
        if (fn != IntPtr.Zero)
        {
            var createFn = Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsARBDelegate>(fn);
            var attribs = new[] { WGL_CONTEXT_MAJOR_VERSION_ARB, 3, WGL_CONTEXT_MINOR_VERSION_ARB, 2, 0 };
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(tmpCtx);
            _hGLRC = createFn(_hDC, IntPtr.Zero, attribs);
        }
        else
        {
            _hGLRC = tmpCtx;
        }
        wglMakeCurrent(_hDC, _hGLRC);

        var pfbPtr = wglGetProcAddress("glBindFramebuffer");
        if (pfbPtr != IntPtr.Zero)
            _glBindFramebuffer = Marshal.GetDelegateForFunctionPointer<glBindFramebufferDelegate>(pfbPtr);
    }

    private void InitMaplibre(int physW, int physH)
    {
        _runLoop  = new MbglRunLoop();
        _frontend = new MbglFrontend(_hDC, _hGLRC, physW, physH, _dpi,
            () => _renderNeedsUpdate = true);

        _map = new MbglMap(_frontend, _runLoop, pixelRatio: _dpi,
            observer: OnMapObserverEvent);
        _map.SetSize(physW, physH);

        // Start render timer
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += OnRenderTick;

        var url = StyleUrl;
        if (!string.IsNullOrEmpty(url))
        {
            if (url.TrimStart().StartsWith('{'))
                _map.SetStyleJson(url);
            else
                _map.SetStyleUrl(url);
        }

        MapReady?.Invoke(this, EventArgs.Empty);
    }

    private void OnMapObserverEvent(string eventName, string? detail)
    {
        switch (eventName)
        {
            case "onDidFinishLoadingStyle":
                Dispatcher.BeginInvoke(() =>
                {
                    _styleReady = true;
                    _locIndLayer = null;  // invalidated by style reload
                    _style = _map?.GetStyle();
                    _renderNeedsUpdate = true;
                    _attrLoaded = false;  // new style — attribution sources may differ
                    if (_pendingLocInd.HasValue) ApplyPendingLocationIndicator();
                    RefreshAttribution();
                    StyleLoaded?.Invoke(this, EventArgs.Empty);
                });
                break;
            case "onDidBecomeIdle":
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_attrLoaded) RefreshAttribution();
                    DidBecomeIdle?.Invoke(this, EventArgs.Empty);
                });
                break;
            case "onCameraIsChanging":
                Dispatcher.BeginInvoke(CollapseAttribution);
                break;
            case "onCameraDidChange":
                Dispatcher.BeginInvoke(() =>
                {
                    CameraIdle?.Invoke(this, EventArgs.Empty);
                    if (_compassRotate != null && _map != null)
                        _compassRotate.Angle = -_map.Bearing;
                    // Keep GPS bearing button colour in sync with map bearing
                    RefreshGpsBearingButton();
                });
                break;
            case "onDidFailLoadingMap":
                Log($"onDidFailLoadingMap: {detail}");
                break;
            case "onDidFinishRenderingFrameNeedsRepaint":
            case "onDidFinishRenderingFrameNeedsRepaintPlacementChanged":
                // mbgl will call update() again on its own; OnRender() will set
                // _renderNeedsUpdate when params are ready.
                break;
            case "onDidFinishRenderingFramePlacementChanged":
                // needsRepaint is false — queue via TriggerRepaint() so update()
                // is called with fresh params before we render.
                _map?.TriggerRepaint();
                break;
        }
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        _runLoop?.RunOnce();

        if (_renderNeedsUpdate && _hGLRC != IntPtr.Zero && _hDC != IntPtr.Zero && _frontend != null)
        {
            _renderNeedsUpdate = false;
            wglMakeCurrent(_hDC, _hGLRC);

            int physW = Math.Max(1, (int)(ActualWidth  * _dpi));
            int physH = Math.Max(1, (int)(ActualHeight * _dpi));
            _glBindFramebuffer?.Invoke(GL_FRAMEBUFFER, 0);
            glViewport(0, 0, physW, physH);
            glClearColor(0.85f, 0.90f, 0.97f, 1f);
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT | GL_STENCIL_BUFFER_BIT);

            try { _frontend.Render(); }
            catch (Exception ex) { Log($"Render threw: {ex.Message}"); }
            SwapBuffers(_hDC);

            if (++_renderTickCount <= 5 || _renderTickCount % 120 == 0)
                System.Diagnostics.Debug.WriteLine(
                    $"[MbglMapHost] tick#{_renderTickCount} rendered {physW}x{physH}");
        }
    }

    // ── Mouse input → MapLibre ────────────────────────────────────────────────

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (hwnd == _childHwnd && _map != null)
        {
            long lp  = lParam.ToInt64();
            int  cx  = (short)(lp & 0xFFFF);
            int  cy  = (short)((lp >> 16) & 0xFFFF);

            switch (msg)
            {
                case WM_SETCURSOR:
                {
                    if (hwnd == _childHwnd && (lParam.ToInt64() & 0xFFFF) == 1 /* HTCLIENT */)
                    {
                        int idc = _isDragging ? IDC_SIZEALL : IDC_HAND;
                        SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)idc));
                        handled = true;
                        return new IntPtr(1);
                    }
                    break;
                }

                case WM_LBUTTONDOWN:
                    _isDragging = true;
                    _lastMouseX = cx; _lastMouseY = cy;
                    _mouseDownX = cx; _mouseDownY = cy;
                    SetCapture(hwnd);
                    _map.OnPanStart(cx, cy);
                    handled = true;
                    break;

                case WM_MOUSEMOVE:
                    if (_isDragging)
                    {
                        int dx = cx - _lastMouseX;
                        int dy = cy - _lastMouseY;
                        _lastMouseX = cx; _lastMouseY = cy;
                        _map.OnPanMove(dx, dy);
                        _renderNeedsUpdate = true;
                        handled = true;
                    }
                    break;

                case WM_LBUTTONUP:
                    if (_isDragging)
                    {
                        _isDragging = false;
                        ReleaseCapture();
                        _map.OnPanEnd();
                        _renderNeedsUpdate = true;
                        _map.TriggerRepaint();
                        // Fire MapClicked if the mouse barely moved (tap/click, not a pan).
                        int dx = cx - _mouseDownX;
                        int dy = cy - _mouseDownY;
                        if (Math.Abs(dx) <= ClickThresholdPx && Math.Abs(dy) <= ClickThresholdPx)
                        {
                            var ll = _map.LatLngForPixel(cx, cy);
                            MapClicked?.Invoke(this, new MlnMapClickEventArgs(cx, cy, ll.Lat, ll.Lon));
                        }
                        handled = true;
                    }
                    break;

                case WM_LBUTTONDBLCLK:
                    _isDragging = false;
                    ReleaseCapture();
                    _map.OnDoubleTap(cx, cy);
                    _renderNeedsUpdate = true;
                    handled = true;
                    break;

                case WM_MOUSEWHEEL:
                {
                    int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    int screenX = (short)(lp & 0xFFFF);
                    int screenY = (short)((lp >> 16) & 0xFFFF);
                    var pt = new POINT { X = screenX, Y = screenY };
                    ScreenToClient(hwnd, ref pt);
                    _map.OnScroll((double)delta / 120, pt.X, pt.Y);
                    _renderNeedsUpdate = true;
                    handled = true;
                    break;
                }
            }
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    // ── Navigation popup ──────────────────────────────────────────────────────

    private void InitNavPopup()
    {
        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            Effect = new DropShadowEffect
            {
                BlurRadius = 6, ShadowDepth = 2, Opacity = 0.25, Color = Colors.Black, Direction = 270,
            },
        };

        var panel = new StackPanel { Width = NavPanelW };
        outerBorder.Child = panel;

        // ── Round rotate/pitch d-pad (top) ───────────────────────────────────
        var dpad = BuildDpad();
        panel.Children.Add(dpad);

        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(218, 218, 218)) });

        var zoomInBtn = MakeNavButton("+", ZoomIn);
        SetButtonCorners(zoomInBtn, 0, 0, 0, 0);
        panel.Children.Add(zoomInBtn);

        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(218, 218, 218)) });

        var zoomOutBtn = MakeNavButton("\u2212", ZoomOut);
        SetButtonCorners(zoomOutBtn, 0, 0, 4, 4);
        panel.Children.Add(zoomOutBtn);

        _navPopup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen          = true,
            IsHitTestVisible   = true,
            PlacementTarget    = this,
            // PlacementMode.Relative positions the popup relative to the PlacementTarget
            // in logical (device-independent) pixels, so no manual DPI conversion is needed.
            // AbsolutePoint + PointToScreen would double-scale offsets at DPI != 100%.
            Placement          = PlacementMode.Relative,
            Child              = outerBorder,
        };
        HookPopupOpen(_navPopup);
        PositionNavPopup();
    }

    private static Border MakeNavButton(string? text, Action onClick)
    {
        var btn = new Border
        {
            Width      = NavPanelW,
            Height     = 29,
            Background = Brushes.White,
            Cursor     = System.Windows.Input.Cursors.Hand,
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

    /// <summary>
    /// Build the round rotate/pitch d-pad: up/down tilt the map (±10°, clamped 0–60°),
    /// left/right rotate it (±15°), the centre resets north, and a hollow ring around
    /// the arrows acts as a compass whose blue tick tracks the current bearing.
    /// </summary>
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
            Background = Brushes.Transparent,   // transparent but hit-testable
            Cursor     = System.Windows.Input.Cursors.Hand,
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

    // ── Control corner + stacking helpers ─────────────────────────────────────

    private const double CtrlMargin   = 10;
    private const double CtrlStackGap = 10;
    private const double NavPanelW    = 29;          // matches zoom/GPS button width so controls align when stacked
    private const double NavDpadH     = 29;          // round d-pad height (square with the panel width)
    private const double NavPanelH    = NavDpadH + 29 * 2 + 2;  // d-pad + 2 zoom buttons + 2 separators
    private const double GpsPanelH    = 29 * 2 + 1;  // 2 buttons + 1 separator

    private static bool CornerIsLeft(MapControlCorner c) => c is MapControlCorner.TopLeft or MapControlCorner.BottomLeft;
    private static bool CornerIsTop(MapControlCorner c)  => c is MapControlCorner.TopLeft or MapControlCorner.TopRight;

    /// <summary>
    /// Vertical offset contributed by controls that precede the given one in the
    /// stack order (navigation → GPS → attribution) and share the same corner.
    /// The first control sits at the corner; later ones stack inward.
    /// </summary>
    private double ControlStackOffset(MapControlCorner corner, int stackIndex)
    {
        double off = 0;
        if (stackIndex > 0 && ShowNavigationControls && NavigationControlPosition == corner)
            off += NavPanelH + CtrlStackGap;
        if (stackIndex > 1 && ShowGpsControl && GpsControlPosition == corner)
            off += GpsPanelH + CtrlStackGap;
        return off;
    }

    /// <summary>
    /// Whether the current map area is large enough to fully contain a control of
    /// the given size sitting at the given vertical stacking offset, with a margin
    /// on each side. Controls that do not fit are hidden so they never overflow the
    /// map edge or float over adjacent UI when the host is very small.
    /// </summary>
    private bool ControlFitsMap(double panelW, double panelH, double stackOffset)
        => ActualWidth  >= panelW + 2 * CtrlMargin
        && ActualHeight >= panelH + stackOffset + 2 * CtrlMargin;

    private void PositionNavPopup()
    {
        if (_navPopup == null || !_initialized) return;
        const int margin = (int)CtrlMargin;
        const int panelW = (int)NavPanelW;
        const int panelH = (int)NavPanelH;  // d-pad + 2 zoom buttons + 2 separator px
        var corner = NavigationControlPosition;
        double off  = ControlStackOffset(corner, 0);  // nav is first → 0
        double hOff = CornerIsLeft(corner) ? margin : ActualWidth  - panelW - margin;
        double vOff = CornerIsTop(corner)  ? margin + off : ActualHeight - panelH - margin - off;
        // Clamp to the work area of the monitor this popup will land on.
        // Computing centerDevice BEFORE calling GetWorkAreaLogicalAt ensures the
        // correct monitor is selected even when the map spans two screens.
        try
        {
            var centerDevice = PointToScreen(new Point(hOff + panelW / 2.0, vOff + panelH / 2.0));
            var wa     = GetWorkAreaLogicalAt(centerDevice);
            var origin = PointToScreen(new Point(0, 0)); // device px (element origin)
            var src    = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                var toLogical     = src.CompositionTarget.TransformFromDevice;
                var originLogical = toLogical.Transform(origin);
                double maxH = wa.Right  - originLogical.X - panelW;
                double maxV = wa.Bottom - originLogical.Y - panelH;
                double minH = wa.Left   - originLogical.X;
                double minV = wa.Top    - originLogical.Y;
                hOff = Math.Max(minH, Math.Min(hOff, maxH));
                vOff = Math.Max(minV, Math.Min(vOff, maxV));
            }
        }
        catch { /* non-critical; fall back to unclamped position */ }
        _navPopup.HorizontalOffset = hOff;
        _navPopup.VerticalOffset   = vOff;
    }

    private void UpdateNavPopupOpen()
    {
        if (_navPopup == null) return;
        bool fits = ControlFitsMap(NavPanelW, NavPanelH, ControlStackOffset(NavigationControlPosition, 0));
        _navPopup.IsOpen = _initialized && IsVisible && ShowNavigationControls && fits;
    }

    private void HookPopupOpen(Popup popup)
    {
        popup.Opened += (_, _) => { };
        UpdateNavPopupOpen();
    }

    // ── GPS control popup ─────────────────────────────────────────────────────

    private void InitGpsPopup()
    {
        // Top button: GPS tracking state (Off / Show / Follow)
        _gpsTrackingIcon = new TextBlock
        {
            Text                = "\u25CB",   // ○ = Off state
            FontSize            = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            IsHitTestVisible    = false,
            Foreground          = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
        };
        _gpsBtnTracking = MakeNavButton(null, CycleGpsMode);
        SetButtonCorners(_gpsBtnTracking, 4, 4, 0, 0);
        _gpsBtnTracking.Child = _gpsTrackingIcon;

        // Bottom button: reset bearing to north
        var bearingIcon = new TextBlock
        {
            Text                = "\u21BA",  // ↺ reset bearing (distinct from the compass drag button)
            FontSize            = 16,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            IsHitTestVisible    = false,
            Foreground          = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            Tag                 = "bearing",
        };
        _gpsBtnBearing = MakeNavButton(null, GpsBearingButtonPressed);
        SetButtonCorners(_gpsBtnBearing, 0, 0, 4, 4);
        _gpsBtnBearing.Child = bearingIcon;

        var panel = new StackPanel { Width = 29 };
        panel.Children.Add(_gpsBtnTracking);
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(218, 218, 218)) });
        panel.Children.Add(_gpsBtnBearing);

        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            Effect = new DropShadowEffect
            {
                BlurRadius = 6, ShadowDepth = 2, Opacity = 0.25, Color = Colors.Black, Direction = 270,
            },
            Child = panel,
        };

        _gpsPopup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen          = true,
            IsHitTestVisible   = true,
            PlacementTarget    = this,
            Placement          = PlacementMode.Relative,
            Child              = outerBorder,
        };

        PositionGpsPopup();
        _gpsPopup.IsOpen = _initialized && IsVisible && ShowGpsControl;
    }

    private void CycleGpsMode()
    {
        _gpsTrackingMode = _gpsTrackingMode switch
        {
            GpsTrackingMode.Off           => GpsTrackingMode.Show,
            GpsTrackingMode.Show          => GpsTrackingMode.Follow,
            GpsTrackingMode.Follow        => GpsTrackingMode.FollowBearing,
            GpsTrackingMode.FollowBearing => GpsTrackingMode.Off,
            _                             => GpsTrackingMode.Off,
        };

        ApplyGpsMode();
        RefreshGpsTrackingButton();
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
            if (_gpsTrackingMode is GpsTrackingMode.Follow or GpsTrackingMode.FollowBearing)
            {
                double zoom          = _map?.Zoom ?? 14;
                double cameraZoom    = zoom < 8 ? 14 : zoom;
                double cameraBearing = _gpsTrackingMode == GpsTrackingMode.FollowBearing ? _lastGpsBearing : (_map?.Bearing ?? 0);
                _map?.EaseTo(_lastGpsLat, _lastGpsLon, cameraZoom, cameraBearing, _map.Pitch, durationMs: 300);
            }
            if (_styleReady && _style != null)
                ApplyPendingLocationIndicator();
            _renderNeedsUpdate = true;
        }
    }

    /// <summary>
    /// Feed a GPS location fix to the host.  The host respects the current
    /// GPS tracking mode — Off: ignored; Show: blue dot only; Follow: dot + camera.
    /// </summary>
    public void UpdateGpsLocation(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        _lastGpsLat      = lat;
        _lastGpsLon      = lon;
        _lastGpsBearing  = bearing;
        _lastGpsAccuracy = accuracyMeters;
        bool isFirstFix  = !_hasGpsFix;
        _hasGpsFix       = true;

        if (_gpsTrackingMode == GpsTrackingMode.Off) return;

        _pendingLocInd = new LocIndParams(lat, lon, bearing, Math.Max(5f, accuracyMeters));

        bool follow     = _gpsTrackingMode is GpsTrackingMode.Follow or GpsTrackingMode.FollowBearing;
        bool useBearing = _gpsTrackingMode == GpsTrackingMode.FollowBearing;

        if (follow && _map != null)
        {
            double cameraZoom    = _map.Zoom < 8 ? 14 : _map.Zoom;
            double cameraBearing = useBearing ? bearing : _map.Bearing;
            if (isFirstFix) _map.JumpTo(lat, lon, cameraZoom, cameraBearing, _map.Pitch);
            else            _map.EaseTo(lat, lon, _map.Zoom, cameraBearing, _map.Pitch, durationMs: 200);
        }

        if (_styleReady && _style != null)
            ApplyPendingLocationIndicator();
        _renderNeedsUpdate = true;

        if (isFirstFix) RefreshGpsTrackingButton();
    }

    private void RefreshGpsTrackingButton()
    {
        if (_gpsTrackingIcon == null) return;
        switch (_gpsTrackingMode)
        {
            case GpsTrackingMode.Show:
                _gpsTrackingIcon.Text       = "\u2299";   // ⊙ circled dot
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));
                _gpsBtnTracking!.Background = Brushes.White;
                break;
            case GpsTrackingMode.Follow:
                _gpsTrackingIcon.Text       = "\u25CE";   // ◎ bullseye
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x70, 0xC5));
                _gpsBtnTracking!.Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFF));
                break;
            case GpsTrackingMode.FollowBearing:
                _gpsTrackingIcon.Text       = "\u25B2";   // ▲ navigation triangle / heading-up
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x7C, 0x00));
                _gpsBtnTracking!.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0));
                break;
            default:  // Off
                _gpsTrackingIcon.Text       = "\u25CB";   // ○ empty circle
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                _gpsBtnTracking!.Background = Brushes.White;
                break;
        }
    }

    /// <summary>
    /// GPS bearing button: in FollowBearing, drops to Follow (stops heading-up rotation) then
    /// resets bearing to north; in all other states just resets bearing to north.
    /// </summary>
    private void GpsBearingButtonPressed()
    {
        if (_gpsTrackingMode == GpsTrackingMode.FollowBearing)
        {
            _gpsTrackingMode = GpsTrackingMode.Follow;
            RefreshGpsTrackingButton();
        }
        ResetNorth();
    }

    private void RefreshGpsBearingButton()
    {
        if (_gpsBtnBearing?.Child is not TextBlock tb) return;
        double bearing = _map?.Bearing ?? 0;
        tb.Foreground = Math.Abs(bearing) > 0.5
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5))
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    }

    private void PositionGpsPopup()
    {
        if (_gpsPopup == null || !_initialized) return;
        const int margin = 10;
        const int panelW = 29;
        const int panelH = (int)GpsPanelH;  // 2 buttons + 1 px separator
        // Anchor to the configured corner, stacking inward when sharing with others.
        var corner  = GpsControlPosition;
        double off  = ControlStackOffset(corner, 1);  // gps is second (after nav)
        double hOff = CornerIsLeft(corner) ? margin : ActualWidth  - panelW - margin;
        double vOff = CornerIsTop(corner)  ? margin + off : ActualHeight - panelH - margin - off;
        // Clamp to the work area of the monitor this popup will land on.
        try
        {
            var centerDevice = PointToScreen(new Point(hOff + panelW / 2.0, vOff + panelH / 2.0));
            var wa     = GetWorkAreaLogicalAt(centerDevice);
            var origin = PointToScreen(new Point(0, 0));
            var src    = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                var toLogical     = src.CompositionTarget.TransformFromDevice;
                var originLogical = toLogical.Transform(origin);
                double maxH = wa.Right  - originLogical.X - panelW;
                double maxV = wa.Bottom - originLogical.Y - panelH;
                double minH = wa.Left   - originLogical.X;
                double minV = wa.Top    - originLogical.Y;
                hOff = Math.Max(minH, Math.Min(hOff, maxH));
                vOff = Math.Max(minV, Math.Min(vOff, maxV));
            }
        }
        catch { /* non-critical; fall back to unclamped position */ }
        _gpsPopup.HorizontalOffset = hOff;
        _gpsPopup.VerticalOffset   = vOff;
    }

    private void UpdateGpsPopupOpen()
    {
        if (_gpsPopup == null) return;
        bool fits = ControlFitsMap(29, GpsPanelH, ControlStackOffset(GpsControlPosition, 1));
        _gpsPopup.IsOpen = _initialized && IsVisible && ShowGpsControl && fits;
    }

    // ── Attribution popup ─────────────────────────────────────────────────────

    private void InitAttributionPopup()
    {
        _attributionText = new TextBlock
        {
            TextWrapping  = TextWrapping.Wrap,
            FontSize      = 10,
            Foreground    = Brushes.Black,
            MaxWidth      = 320,  // Will be dynamically adjusted in PositionAttributionPopup
        };

        _attributionBorder = new Border
        {
            Background    = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            CornerRadius  = new CornerRadius(2),
            Padding       = new Thickness(6, 2, 6, 2),
            Child         = _attributionText,
        };

        // AllowsTransparency=false so the popup uses a normal (non-layered) HWND.
        // Layered windows have a known WPF rendering defect where Hyperlink/Run inlines
        // inside TextBlock fail to paint, leaving the popup visually blank.
        _attributionBorder.Background = Brushes.White;
        _attributionPopup = new Popup
        {
            AllowsTransparency = false,
            StaysOpen          = true,
            IsHitTestVisible   = true,
            PlacementTarget    = this,
            Placement          = PlacementMode.Relative,
            Child              = _attributionBorder,
        };
        _attributionBorder.SizeChanged += (_, _) => PositionAttributionPopup();

        // ── Collapsed ⓘ button ───────────────────────────────────────────────
        var btnText   = new TextBlock { Text = "\u24d8", FontSize = 12, Foreground = Brushes.Black };
        _attrButtonBorder = new Border
        {
            Background   = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            CornerRadius = new CornerRadius(2),
            Padding      = new Thickness(6, 2, 6, 2),
            Cursor       = Cursors.Hand,
            Child        = btnText,
        };
        _attrButtonBorder.MouseLeftButtonUp += (_, _) => ExpandAttribution();
        _attrButtonBorder.SizeChanged       += (_, _) => PositionAttributionPopup();
        _attrButtonPopup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen          = true,
            IsHitTestVisible   = true,
            PlacementTarget    = this,
            Placement          = PlacementMode.Relative,
            Child              = _attrButtonBorder,
        };

        _attrCollapseTimer       = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _attrCollapseTimer.Tick += (_, _) => { _attrCollapseTimer.Stop(); CollapseAttribution(); };

        UpdateAttributionPopupOpen();
    }

    private void RefreshAttribution()
    {
        if (_style == null) return;
        var parts = MbglStyle.EnsureMapLibreAttribution(_style.GetSourceAttributions());

        var allInlines = new List<Inline>();
        var first = true;
        foreach (var part in parts)
        {
            var inlines = ParseHtmlToInlines(part);
            if (inlines.Count == 0) continue;
            if (!first) allInlines.Add(new Run(" | "));
            allInlines.AddRange(inlines);
            first = false;
        }

        if (_attributionText != null)
        {
            _attributionText.Inlines.Clear();
            _attributionText.Inlines.AddRange(allInlines);
        }

        if (allInlines.Count > 0)
        {
            _attrLoaded = true;
            PositionAttributionPopup();
            ExpandAttribution();
        }
        else
        {
            UpdateAttributionPopupOpen();
        }
    }

    private void ExpandAttribution()
    {
        _attrCollapseTimer?.Stop();
        if (_attributionPopup == null || !_attrLoaded) return;
        var parentWin = Window.GetWindow(this);
        // Don't open the popup (or start the auto-collapse timer) when the window
        // isn't visible to the user — it would float above other applications.
        if (parentWin?.WindowState == WindowState.Minimized || parentWin?.IsActive == false) return;
        if (_initialized && IsVisible)
        {
            _attributionPopup.IsOpen = true;
            if (_attrButtonPopup != null) _attrButtonPopup.IsOpen = false;
        }
        _attrCollapseTimer?.Start();
    }

    private void CollapseAttribution()
    {
        _attrCollapseTimer?.Stop();
        if (_attributionPopup != null) _attributionPopup.IsOpen = false;
        var parentWin = Window.GetWindow(this);
        if (_attrButtonPopup != null && _attrLoaded && _initialized && IsVisible
            && parentWin?.WindowState != WindowState.Minimized
            && parentWin?.IsActive == true)
            _attrButtonPopup.IsOpen = true;
    }

    /// <summary>
    /// Parses an HTML attribution string into WPF inline elements.
    /// Anchor elements become clickable <see cref="Hyperlink"/> inlines;
    /// plain text between anchors becomes <see cref="Run"/> inlines.
    /// Handles both quoted and unquoted href attributes.
    /// </summary>
    private static List<Inline> ParseHtmlToInlines(string html)
    {
        var inlines = new List<Inline>();
        if (string.IsNullOrWhiteSpace(html)) return inlines;

        var pos = 0;
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(html,
                @"<a\b([^>]*)>(.*?)</a>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            // Plain text before this anchor
            if (m.Index > pos)
            {
                var plain = DecodeHtmlEntities(html[pos..m.Index]);
                if (!string.IsNullOrEmpty(plain))
                    inlines.Add(new Run(plain));
            }

            // Inner text (strip nested tags, then decode entities)
            var innerText = DecodeHtmlEntities(
                System.Text.RegularExpressions.Regex.Replace(
                    m.Groups[2].Value, @"<[^>]+>", string.Empty)).Trim();

            if (!string.IsNullOrWhiteSpace(innerText))
            {
                // Extract href — handles both quoted and unquoted attribute values
                var hrefMatch = System.Text.RegularExpressions.Regex.Match(
                    m.Groups[1].Value,
                    @"href=[""']?([^""'\s>]+)[""']?",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (hrefMatch.Success &&
                    Uri.TryCreate(hrefMatch.Groups[1].Value, UriKind.Absolute, out var uri))
                {
                    var capturedUri = uri;
                    var link = new Hyperlink(new Run(innerText));
                    link.Click += (_, _) =>
                    {
                        try { System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(capturedUri.AbsoluteUri)
                            { UseShellExecute = true }); }
                        catch { /* ignore if browser launch fails */ }
                    };
                    inlines.Add(link);
                }
                else
                {
                    inlines.Add(new Run(innerText));
                }
            }

            pos = m.Index + m.Length;
        }

        // Trailing plain text after the last anchor
        if (pos < html.Length)
        {
            var plain = DecodeHtmlEntities(html[pos..]);
            if (!string.IsNullOrEmpty(plain))
                inlines.Add(new Run(plain));
        }

        return inlines;
    }

    private static string DecodeHtmlEntities(string text) =>
        text
            .Replace("&amp;",  "&")
            .Replace("&lt;",   "<")
            .Replace("&gt;",   ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;",  "'")
            .Replace("&nbsp;", " ")
            .Replace("&copy;", "©")
            .Replace("&reg;",  "®")
            .Replace("&trade;","™");

    private void PositionAttributionPopup()
    {
        if (_attributionPopup == null || !_initialized) return;

        const double margin = 4;
        var    corner = AttributionControlPosition;
        bool   isLeft = CornerIsLeft(corner);
        bool   isTop  = CornerIsTop(corner);
        double off    = ControlStackOffset(corner, 2);  // attribution is last in stack order

        // Constrain attribution width to map width minus margins
        if (_attributionText != null)
            _attributionText.MaxWidth = Math.Max(100, ActualWidth - 8);

        double attrW = _attributionBorder?.ActualWidth > 0
            ? _attributionBorder.ActualWidth
            : (_attributionBorder?.DesiredSize.Width ?? 0);
        double attrH = _attributionBorder?.ActualHeight > 0
            ? _attributionBorder.ActualHeight
            : (_attributionBorder?.DesiredSize.Height > 0 ? _attributionBorder.DesiredSize.Height : 22);
        double btnW = _attrButtonBorder?.ActualWidth > 0
            ? _attrButtonBorder.ActualWidth
            : (_attrButtonBorder?.DesiredSize.Width ?? 0);
        double btnH = _attrButtonBorder?.ActualHeight > 0
            ? _attrButtonBorder.ActualHeight
            : (_attrButtonBorder?.DesiredSize.Height > 0 ? _attrButtonBorder.DesiredSize.Height : 22);

        // Horizontal offset: anchor to the left or right edge. For left anchoring keep
        // the existing overflow-shift so wide attribution never runs off the right edge.
        double AttrHOff(double w)
        {
            if (isLeft)
            {
                double x = margin;
                if (x + w > ActualWidth) x = Math.Max(margin, ActualWidth - w - margin);
                return x;
            }
            return Math.Max(margin, ActualWidth - w - margin);
        }
        double attrHOff = AttrHOff(attrW);
        double btnHOff  = AttrHOff(btnW);

        // Vertical offset: PlacementMode.Relative sets the popup's TOP. For bottom
        // anchoring we subtract the popup height so its bottom lands margin px above
        // the map edge (minus any stacking offset). For top anchoring we add the offset.
        double attrVOff = isTop ? margin + off : ActualHeight - attrH - margin - off;
        double btnVOff  = isTop ? margin + off : ActualHeight - btnH  - margin - off;

        // Clamp to the work area of the monitor the attribution popup will land on.
        try
        {
            // Use the attribution popup's center to look up the right monitor.
            var centerDevice = PointToScreen(new Point(attrHOff + attrW / 2.0, attrVOff + attrH / 2.0));
            var wa     = GetWorkAreaLogicalAt(centerDevice);
            var origin = PointToScreen(new Point(0, 0));
            var src    = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                var toLogical     = src.CompositionTarget.TransformFromDevice;
                var originLogical = toLogical.Transform(origin);
                double attrMaxV = wa.Bottom - originLogical.Y - attrH;
                double btnMaxV  = wa.Bottom - originLogical.Y - btnH;
                double minV     = wa.Top    - originLogical.Y;
                attrVOff = Math.Max(minV, Math.Min(attrVOff, attrMaxV));
                btnVOff  = Math.Max(minV, Math.Min(btnVOff,  btnMaxV));
            }
        }
        catch { /* non-critical */ }
        _attributionPopup.HorizontalOffset = attrHOff;
        _attributionPopup.VerticalOffset   = attrVOff;

        if (_attrButtonPopup != null)
        {
            _attrButtonPopup.HorizontalOffset = btnHOff;
            _attrButtonPopup.VerticalOffset   = btnVOff;
        }
    }

    private void UpdateAttributionPopupOpen()
    {
        bool active = _initialized && IsVisible && _attrLoaded;
        if (!active)
        {
            if (_attributionPopup != null) _attributionPopup.IsOpen = false;
            if (_attrButtonPopup  != null) _attrButtonPopup.IsOpen  = false;
            _attrCollapseTimer?.Stop();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Point GetAbsolutePosition(double logicalX, double logicalY)
    {
        var pt = PointToScreen(new Point(logicalX, logicalY));
        return pt;
    }

    private float GetDpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        return src != null
            ? (float)src.CompositionTarget.TransformToDevice.M11
            : 1.0f;
    }

    private static readonly HashSet<string> LayoutPropertyNames = new(StringComparer.Ordinal)
    {
        "visibility", "symbol-placement", "symbol-spacing", "icon-image", "icon-size",
        "text-field", "text-font", "text-size", "line-cap", "line-join", "fill-sort-key",
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
                string s  => $"\"{s}\"",
                bool   b  => b ? "true" : "false",
                double d  => d.ToString(ic),
                float  f  => f.ToString(ic),
                int    i  => i.ToString(),
                long   l  => l.ToString(),
                _         => System.Text.Json.JsonSerializer.Serialize(val),
            };
            if (LayoutPropertyNames.Contains(name))
                layer.SetLayoutProperty(name, json);
            else
                layer.SetPaintProperty(name, json);
        }
    }

    /// <summary>
    /// Wraps a pre-serialised JSON string so that <see cref="ApplyLayerProperties"/> forwards
    /// it verbatim to SetPaintProperty/SetLayoutProperty rather than quoting it as a string.
    /// Use this for MapLibre expressions, e.g. ["interpolate", ...] for circle-radius.
    /// </summary>
    public record RawJson(string Json);
}

/// <summary>Event args for <see cref="MlnMapHost.MapClicked"/>.</summary>
public sealed class MlnMapClickEventArgs : EventArgs
{
    /// <summary>Physical pixel X within the map viewport at the time of the click.</summary>
    public double ScreenX { get; }
    /// <summary>Physical pixel Y within the map viewport at the time of the click.</summary>
    public double ScreenY { get; }
    /// <summary>Geographic latitude corresponding to the click position.</summary>
    public double Latitude { get; }
    /// <summary>Geographic longitude corresponding to the click position.</summary>
    public double Longitude { get; }

    internal MlnMapClickEventArgs(double screenX, double screenY, double latitude, double longitude)
    {
        ScreenX   = screenX;
        ScreenY   = screenY;
        Latitude  = latitude;
        Longitude = longitude;
    }
}
