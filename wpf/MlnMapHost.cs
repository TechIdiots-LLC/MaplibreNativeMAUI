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
        if (d is MlnMapHost h) h.UpdateNavPopupOpen();
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

        // Hide popups when the window loses focus so they don't float over other apps.
        var parentWin = Window.GetWindow(this);
        if (parentWin != null)
        {
            parentWin.Deactivated += (_, _) =>
            {
                if (_navPopup         != null) _navPopup.IsOpen         = false;
                if (_attributionPopup != null) _attributionPopup.IsOpen = false;
                if (_attrButtonPopup  != null) _attrButtonPopup.IsOpen  = false;
                _attrCollapseTimer?.Stop();
            };
            parentWin.Activated += (_, _) =>
            {
                // Don't reopen popups while the window is still minimized
                // (WM_ACTIVATE can fire even on a minimized window).
                if (parentWin.WindowState == WindowState.Minimized) return;
                UpdateNavPopupOpen();
                UpdateAttributionPopupOpen();
                if (_attrLoaded) CollapseAttribution();  // show ⓘ button after re-focus
            };
            parentWin.StateChanged += (_, _) =>
            {
                if (parentWin.WindowState == WindowState.Minimized)
                {
                    // Close all popups and stop the collapse timer so it can't
                    // reopen the attribution button while the window is minimized.
                    _attrCollapseTimer?.Stop();
                    if (_navPopup         != null) _navPopup.IsOpen         = false;
                    if (_attributionPopup != null) _attributionPopup.IsOpen = false;
                    if (_attrButtonPopup  != null) _attrButtonPopup.IsOpen  = false;
                }
                else
                {
                    // Window restored from minimize: WPF SizeChanged doesn't fire when the
                    // restored size is unchanged, so the GL surface won't repaint on its own.
                    _renderNeedsUpdate = true;
                    // Defer popup reopen — IsVisible on the HwndHost is still false at the
                    // point StateChanged fires for restore; BeginInvoke(Render) lets WPF
                    // finish making the visual tree visible before we set IsOpen = true.
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        if (parentWin.WindowState == WindowState.Minimized) return;
                        UpdateNavPopupOpen();
                        if (_attrLoaded) CollapseAttribution();  // reshow ⓘ button
                    });
                }
            };
            parentWin.LocationChanged += (_, _) => 
            { 
                // Force immediate reposition when window moves
                PositionNavPopup(); 
                PositionAttributionPopup(); 
                // Re-open popups to force WPF to recalculate their screen positions
                if (_navPopup != null && ShowNavigationControls && IsVisible)
                {
                    var wasOpen = _navPopup.IsOpen;
                    _navPopup.IsOpen = false;
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        // Don't reopen if the window was minimized after LocationChanged fired.
                        if (parentWin.WindowState != WindowState.Minimized)
                            _navPopup.IsOpen = wasOpen;
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
        _attrCollapseTimer?.Stop(); _attrCollapseTimer = null;
        _attrLoaded        = false;
        _attributionText   = null;
        _attributionBorder = null;

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
                    // Keep compass bearing arrow in sync
                    if (_compassRotate != null && _map != null)
                        _compassRotate.Angle = -_map.Bearing;
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

        var panel = new StackPanel { Width = 29 };
        outerBorder.Child = panel;

        var zoomInBtn = MakeNavButton("+", ZoomIn);
        SetButtonCorners(zoomInBtn, 4, 4, 0, 0);
        panel.Children.Add(zoomInBtn);

        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(218, 218, 218)) });

        var zoomOutBtn = MakeNavButton("\u2212", ZoomOut);
        SetButtonCorners(zoomOutBtn, 0, 0, 0, 0);
        panel.Children.Add(zoomOutBtn);

        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(218, 218, 218)) });

        _compassRotate = new RotateTransform { CenterX = 0.5, CenterY = 0.5 };
        var compassIcon = new TextBlock
        {
            Text                = "\u2191",
            FontSize            = 16,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            IsHitTestVisible    = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform     = _compassRotate,
        };
        var compassBtn = MakeNavButton(null, ResetNorth);
        SetButtonCorners(compassBtn, 0, 0, 4, 4);
        compassBtn.Child = compassIcon;
        panel.Children.Add(compassBtn);

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
            Width      = 29,
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

    private void PositionNavPopup()
    {
        if (_navPopup == null || !_initialized) return;
        const int margin = 10;
        // With PlacementMode.Relative, offsets are in logical pixels relative to the
        // PlacementTarget (this HwndHost). No PointToScreen conversion needed.
        _navPopup.HorizontalOffset = ActualWidth - 29 - margin;
        _navPopup.VerticalOffset   = margin;
    }

    private void UpdateNavPopupOpen()
    {
        if (_navPopup == null) return;
        _navPopup.IsOpen = _initialized && IsVisible && ShowNavigationControls;
    }

    private void HookPopupOpen(Popup popup)
    {
        popup.Opened += (_, _) => { };
        // Open it now if we're already ready
        UpdateNavPopupOpen();
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
        var parts = _style.GetSourceAttributions();

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
            && parentWin?.WindowState != WindowState.Minimized)
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

        // Constrain attribution width to map width minus margins
        if (_attributionText != null)
            _attributionText.MaxWidth = Math.Max(100, ActualWidth - 8);

        // Determine left offset, shifting left if the popup would overflow the right edge.
        double leftMargin = margin;
        if (_attributionBorder != null)
        {
            double popupWidth = _attributionBorder.ActualWidth > 0
                ? _attributionBorder.ActualWidth
                : _attributionBorder.DesiredSize.Width;
            if (leftMargin + popupWidth > ActualWidth)
                leftMargin = Math.Max(margin, ActualWidth - popupWidth - margin);
        }

        // Position each popup so its BOTTOM is margin px above the map's bottom edge.
        // VerticalOffset (PlacementMode.Relative) sets the popup's TOP — so we subtract
        // the popup's own height to make the bottom land at ActualHeight - margin.
        double attrH = _attributionBorder?.ActualHeight > 0
            ? _attributionBorder.ActualHeight
            : (_attributionBorder?.DesiredSize.Height > 0 ? _attributionBorder.DesiredSize.Height : 22);
        _attributionPopup.HorizontalOffset = leftMargin;
        _attributionPopup.VerticalOffset   = ActualHeight - attrH - margin;

        if (_attrButtonPopup != null)
        {
            double btnH = _attrButtonBorder?.ActualHeight > 0
                ? _attrButtonBorder.ActualHeight
                : (_attrButtonBorder?.DesiredSize.Height > 0 ? _attrButtonBorder.DesiredSize.Height : 22);
            _attrButtonPopup.HorizontalOffset = leftMargin;
            _attrButtonPopup.VerticalOffset   = ActualHeight - btnH - margin;
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
}
