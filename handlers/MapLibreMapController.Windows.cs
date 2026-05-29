#if WINDOWS
using System.Runtime.InteropServices;
using System.Text.Json;
using MapLibreNative.Maui;
using MapLibreNative.Maui.Handlers.Geometry;
using Map = MapLibreNative.Maui.Handlers.Maps.Map;
using Style = MapLibreNative.Maui.Handlers.Maps.Style;
using Location = Microsoft.Maui.Devices.Sensors.Location;

namespace MapLibreNative.Maui.Handlers;

/// <summary>
/// Windows-specific IMapLibreMapController implementation backed by the C ABI
/// mln-cabi.dll via MbglMap / MbglFrontend / MbglRunLoop P/Invoke bindings.
/// </summary>
public class MapLibreMapController : IMapLibreMapController
{
    // ── Win32 interop ─────────────────────────────────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern ushort RegisterClassExA(ref WNDCLASSEXA wc);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcA(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(IntPtr zero);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateWindowExA(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref Windows.Foundation.Point pt);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW  = 0x0100;
    private const uint RDW_ALLCHILDREN = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    // ── GDI painting (overlay windows) ───────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int    fErase;
        public RECT   rcPaint;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint crColor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern bool Rectangle(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int count);

    private const uint DT_LEFT      = 0x00000000;
    private const uint DT_CENTER    = 0x00000001;
    private const uint DT_VCENTER   = 0x00000004;
    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_WORDBREAK = 0x00000010;
    private const uint DT_CALCRECT  = 0x00000400;
    private const uint DT_NOPREFIX  = 0x00000800;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint uFormat);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation,
        int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string pszFaceName);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint crColor);

    [DllImport("gdi32.dll")]
    private static extern uint SetBkMode(IntPtr hdc, int iBkMode);

    [DllImport("gdi32.dll")]
    private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [DllImport("gdi32.dll")]
    private static extern bool LineTo(IntPtr hdc, int x, int y);

    private const int  PS_SOLID    = 0;
    private const int  FW_NORMAL   = 400;
    private const int  FW_BOLD     = 700;
    private const int  TRANSPARENT = 1;
    private const uint DEFAULT_CHARSET  = 1;
    private const uint OUT_DEFAULT_PRECIS = 0;
    private const uint CLIP_DEFAULT_PRECIS = 0;
    private const uint CLEARTYPE_QUALITY  = 5;
    private const uint DEFAULT_PITCH = 0;

    // ── SetLayeredWindowAttributes (for per-pixel alpha on attribution) ───────
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    private const uint LWA_ALPHA     = 0x00000002;
    private const uint LWA_COLORKEY  = 0x00000001;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("gdi32.dll")]
    private static extern bool SetPixelFormat(IntPtr hdc, int iPixelFormat, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("gdi32.dll")]
    private static extern bool SwapBuffers(IntPtr hdc);

    [DllImport("opengl32.dll")]
    private static extern IntPtr wglCreateContext(IntPtr hDC);

    [DllImport("opengl32.dll")]
    private static extern bool wglMakeCurrent(IntPtr hDC, IntPtr hGLRC);

    [DllImport("opengl32.dll")]
    private static extern bool wglDeleteContext(IntPtr hGLRC);

    [DllImport("opengl32.dll")]
    private static extern IntPtr wglGetProcAddress(string name);

    [DllImport("opengl32.dll")]
    private static extern void glViewport(int x, int y, int width, int height);

    [DllImport("opengl32.dll")]
    private static extern void glClearColor(float r, float g, float b, float a);

    [DllImport("opengl32.dll")]
    private static extern void glClear(uint mask);

    private delegate void glBindFramebufferDelegate(uint target, uint framebuffer);
    private static glBindFramebufferDelegate? _glBindFramebuffer;

    private const uint GL_COLOR_BUFFER_BIT   = 0x00004000;
    private const uint GL_DEPTH_BUFFER_BIT   = 0x00000100;
    private const uint GL_STENCIL_BUFFER_BIT = 0x00000400;
    private const uint GL_FRAMEBUFFER        = 0x8D40;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WglCreateContextAttribsARBDelegate(IntPtr hDC, IntPtr hShareContext, int[] attribList);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrA")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcA")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    private const int GWLP_WNDPROC        = -4;
    private const uint WM_LBUTTONDOWN     = 0x0201;
    private const uint WM_LBUTTONUP       = 0x0202;
    private const uint WM_MOUSEMOVE       = 0x0200;
    private const uint WM_MOUSEWHEEL      = 0x020A;
    private const uint WM_LBUTTONDBLCLK   = 0x0203;
    private const uint WM_RBUTTONDOWN     = 0x0204;
    private const uint WM_RBUTTONUP       = 0x0205;
    private const uint WM_RBUTTONDBLCLK   = 0x0206;
    private const uint MK_LBUTTON         = 0x0001;
    private const int  WHEEL_DELTA        = 120;

    // WM_GESTURE constants (pinch-to-zoom via Win32 gesture API)
    private const uint WM_GESTURE  = 0x0119;
    private const uint GID_ZOOM    = 3;
    private const uint GF_BEGIN    = 0x0001;
    private const uint GF_END      = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct GESTUREINFO
    {
        public uint   cbSize;
        public uint   dwFlags;       // GF_BEGIN | GF_END | GF_INERTIA
        public uint   dwID;          // GID_ZOOM etc.
        public IntPtr hwndTarget;    // CRITICAL: was missing, misaligning everything below it!
        public short  ptsLocationX;  // gesture centroid, screen coords (POINTS)
        public short  ptsLocationY;
        public uint   dwInstanceID;
        public uint   dwSequenceID;
        public ulong  ullArguments;  // GID_ZOOM: lower 32 bits = pixel distance between points
        public uint   cbExtraArgs;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGestureInfo(IntPtr hGestureInfo, ref GESTUREINFO pGestureInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseGestureInfoHandle(IntPtr hGestureInfo);

    // Cursor helpers
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    private const int IDC_ARROW   = 32512;  // standard arrow
    private const int IDC_HAND    = 32649;  // pointing hand (hover)
    private const int IDC_SIZEALL = 32646;  // four-direction arrow (drag)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct WNDCLASSEXA
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    private const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    private const uint PFD_SUPPORT_OPENGL = 0x00000020;
    private const uint PFD_DOUBLEBUFFER   = 0x00000001;
    private const uint WS_POPUP        = 0x80000000;
    private const uint WS_CHILD           = 0x40000000;
    private const uint WS_EX_LAYERED   = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_VISIBLE         = 0x10000000;
    private const uint WS_CLIPCHILDREN    = 0x02000000;
    private const uint WS_CLIPSIBLINGS    = 0x04000000;
    private const uint CS_OWNDC           = 0x0020;
    private const uint CS_DBLCLKS         = 0x0008;
    private const int  WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    private const int  WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const uint SWP_NOZORDER       = 0x0004;
    private const uint SWP_NOACTIVATE      = 0x0010;
    private const uint SWP_NOMOVE          = 0x0002;
    private const uint SWP_NOSIZE          = 0x0001;
    private static readonly IntPtr HWND_TOP     = IntPtr.Zero;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    private const string GlWindowClass = "MapLibreCabiGL";
    private static WndProcDelegate? _wndProcKeepAlive;
    private static bool _classRegistered;

    // Per-instance WndProc subclass state
    private WndProcDelegate? _subclassProc;  // keep-alive reference
    private IntPtr _origWndProc = IntPtr.Zero;
    private bool   _dragging;
    private int    _lastMouseX;
    private int    _lastMouseY;
    private uint   _gesturePrevDist;  // previous per-frame distance for incremental WM_GESTURE zoom

    private static void EnsureGlWindowClassRegistered()
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
    private readonly float   _pixelRatio;

    private IntPtr       _effectiveParentHwnd = IntPtr.Zero;
    private IntPtr       _childHwnd = IntPtr.Zero;
    private IntPtr       _hDC       = IntPtr.Zero;
    private IntPtr       _hGLRC     = IntPtr.Zero;
    private MbglRunLoop? _runLoop;
    private MbglFrontend? _frontend;
    private MbglMap?      _map;
    private MbglStyle?    _style;

    // ── Overlay windows ───────────────────────────────────────────────────────
    // Nav panel (zoom-in, zoom-out, compass) — top-right corner, 29×90px logical
    private IntPtr _navHwnd   = IntPtr.Zero;
    private WndProcDelegate? _navWndProc;   // keep-alive
    private bool   _showNavControls = true;
    // Attribution — bottom-right corner, auto-width text band
    private IntPtr _attrHwnd  = IntPtr.Zero;
    private WndProcDelegate? _attrWndProc;  // keep-alive
    private bool   _showAttrControl = true;
    private string? _customAttribution;
    private string  _attrText = string.Empty;  // cached, rebuilt on StyleLoaded
    private IntPtr  _attrFont = IntPtr.Zero;
    private IntPtr  _navFont  = IntPtr.Zero;
    // Hit-testing constants (logical pixels, scaled by _pixelRatio internally)
    private const int NavButtonSize   = 29;   // px
    private const int NavPanelMargin  = 10;   // from map edge
    private const int AttrPadH        = 6;    // horizontal text padding
    private const int AttrPadV        = 3;    // vertical text padding
    private const int AttrFontSizePt  = 11;
    private const string OverlayClass = "MapLibreOverlay";
    // Cached overlay positions — skip SetWindowPos when nothing changed (prevents repaint flicker)
    private (int x, int y, int w, int h) _lastNavRect;
    private (int x, int y, int w, int h) _lastAttrRect;
    // Cached attr text measurement — only re-measure via GDI when text or available width changes
    private string _lastMeasuredAttrText = string.Empty;
    private int    _lastMeasuredAttrInnerW;
    private (int cx, int cy) _cachedAttrMeasure;
    // Collapse state
    private bool _attrCollapsed;
    private System.Threading.Timer? _attrCollapseTimer;

    // Pumps the libuv run loop on the UI thread. Without this, async HTTP responses
    // for style/tile downloads are never delivered and StyleLoaded never fires.
    private Microsoft.UI.Xaml.DispatcherTimer? _runLoopTimer;
    private bool _renderNeedsUpdate;

    private bool _initialized;
    private bool _styleReady;

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
    public event Func<LatLng, bool>?                     OnMapClickReceived;
    public event Func<LatLng, bool>?                     OnMapLongClickReceived;
    public event Action<Maps.Style>?                     OnStyleLoadedReceived;
    public event Action<Location>?                       OnUserLocationUpdateReceived;
    public event Action<string>?                         OnDidFailLoadingMapReceived;
    public event Action<string>?                         OnStyleImageMissingReceived;

    public MapLibreMapController(IntPtr parentHwnd, float pixelRatio, string? styleString)
    {
        _parentHwnd  = parentHwnd;
        _pixelRatio  = pixelRatio;
        _styleString = styleString;
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    public void Init()
    {
        View.Loaded       += (_, _) => TryInitialize();
        View.SizeChanged  += (_, e) => OnViewSizeChanged(new Microsoft.Maui.Graphics.Size(e.NewSize.Width, e.NewSize.Height));
        View.Unloaded     += (_, _) => DisposeNative();
    }

    private void TryInitialize()
    {
        if (_initialized || _parentHwnd == IntPtr.Zero) return;
        if (View.ActualWidth < 2 || View.ActualHeight < 2) return;

        // AIRSPACE WORKAROUND: Create the GL window as a borderless top-level popup
        // OWNED BY (not parented to) the main XAML window. This bypasses WinUI's
        // DirectComposition surface, which renders on top of any HWND child of the
        // bridge. We then track main-window movement to keep the popup aligned.
        IntPtr ownerHwnd = _parentHwnd;
        System.Diagnostics.Debug.WriteLine(
            $"[MapLibre.Win] TryInitialize (popup mode) owner=0x{ownerHwnd.ToInt64():X}");

        int physW = Math.Max(1, (int)(View.ActualWidth  * _pixelRatio));
        int physH = Math.Max(1, (int)(View.ActualHeight * _pixelRatio));

        EnsureGlWindowClassRegistered();

        _childHwnd = CreateWindowExA(
            WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            GlWindowClass, "",
            WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, physW, physH,
            ownerHwnd, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);

        if (_childHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create GL popup window.");

        _effectiveParentHwnd = ownerHwnd;

        // Subclass the popup WndProc so it handles mouse input directly.
        // The popup sits above the WinUI compositor, so XAML never sees pointer
        // events over the map area — we must intercept them at the Win32 level.
        _subclassProc  = PopupWndProc;
        _origWndProc   = SetWindowLongPtr(_childHwnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_subclassProc));

        InitOpenGl(physW, physH);
        InitMaplibre(physW, physH);
        UpdateChildWindowPosition();
        StartRunLoopPump();
        CreateOverlays();

        // Force at least one paint so we can visually verify the GL HWND is rendering
        // (vs. being covered by the WinUI compositor).
        _renderNeedsUpdate = true;

        _initialized = true;
        ShowOverlays();  // re-evaluate now that _initialized is true

        // Win32 timer fires WM_TIMER even during modal resize loops where the
        // DispatcherTimer is paused, ensuring overlays stay above the GL popup.
        SetTimer(_childHwnd, (IntPtr)OverlayZTimerId, 50, IntPtr.Zero);
        System.Diagnostics.Debug.WriteLine(
            $"[MapLibre.Win] TryInitialize done. childHwnd=0x{_childHwnd.ToInt64():X} " +
            $"size={physW}x{physH} pixelRatio={_pixelRatio} visible={IsWindowVisible(_childHwnd)}");
    }

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

        // Bootstrap context to get wglCreateContextAttribsARB
        var tmpCtx = wglCreateContext(_hDC);
        wglMakeCurrent(_hDC, tmpCtx);

        var fn = wglGetProcAddress("wglCreateContextAttribsARB");
        if (fn != IntPtr.Zero)
        {
            var createFn = Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsARBDelegate>(fn);
            var attribs = new[]
            {
                WGL_CONTEXT_MAJOR_VERSION_ARB, 3,
                WGL_CONTEXT_MINOR_VERSION_ARB, 2,
                0
            };
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(tmpCtx);
            _hGLRC = createFn(_hDC, IntPtr.Zero, attribs);
        }
        else
        {
            _hGLRC = tmpCtx;
        }
        wglMakeCurrent(_hDC, _hGLRC);

        // Resolve the GL3 entry point we need to bind the default framebuffer
        // before each frontend.Render() call. Without this, mbgl renders into its
        // own offscreen FBO and the visible back buffer stays solid black.
        if (_glBindFramebuffer == null)
        {
            var p = wglGetProcAddress("glBindFramebuffer");
            if (p != IntPtr.Zero)
                _glBindFramebuffer = Marshal.GetDelegateForFunctionPointer<glBindFramebufferDelegate>(p);
        }
    }

    private void InitMaplibre(int physW, int physH)
    {
        _runLoop = new MbglRunLoop();

        _frontend = new MbglFrontend(_hDC, _hGLRC, physW, physH, _pixelRatio, OnRender);

        _map = new MbglMap(
            _frontend, _runLoop,
            pixelRatio: _pixelRatio,
            observer: OnMapObserverEvent);

        _map.SetSize(physW, physH);

        if (!string.IsNullOrEmpty(_styleString))
        {
            if (_styleString!.StartsWith('{'))
                _map.SetStyleJson(_styleString);
            else
                _map.SetStyleUrl(_styleString);
        }

        OnMapReadyReceived?.Invoke(new Map(null));
    }

    private void OnRender()
    {
        // Native frontend signalled it has new content — request a redraw on next pump tick.
        _renderNeedsUpdate = true;
    }

    private void StartRunLoopPump()
    {
        if (_runLoopTimer != null) return;
        _runLoopTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _runLoopTimer.Tick += OnRunLoopTick;
        _runLoopTimer.Start();
    }

    private void OnRunLoopTick(object? sender, object e)
    {
        // Pump libuv so HTTP callbacks for style/tile downloads run on this thread.
        _runLoop?.RunOnce();

        // Keep popup aligned with the View on every tick (track window moves).
        UpdateChildWindowPosition();

        if (_renderNeedsUpdate && _hGLRC != IntPtr.Zero && _hDC != IntPtr.Zero && _frontend != null)
        {
            _renderNeedsUpdate = false;
            wglMakeCurrent(_hDC, _hGLRC);

            // Bind default framebuffer + clear so frontend.Render() composites into
            // the back buffer that SwapBuffers will present.
            int physW = Math.Max(1, (int)(View.ActualWidth  * _pixelRatio));
            int physH = Math.Max(1, (int)(View.ActualHeight * _pixelRatio));
            _glBindFramebuffer?.Invoke(GL_FRAMEBUFFER, 0);
            glViewport(0, 0, physW, physH);
            // Neutral light-gray clear; mbgl draws the style's background layer over this.
            // Do NOT use a "garish" diagnostic color in release — any pixel mbgl doesn't
            // touch (e.g. before first frame, between tiles) will show through.
            glClearColor(0.85f, 0.90f, 0.97f, 1f);
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT | GL_STENCIL_BUFFER_BIT);

            try { _frontend.Render(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MapLibre.Win] Render threw: {ex.Message}"); }
            SwapBuffers(_hDC);

            if (++_renderTickCounter <= 5 || _renderTickCounter % 60 == 0)
                System.Diagnostics.Debug.WriteLine(
                    $"[MapLibre.Win] tick#{_renderTickCounter} rendered+swapped {physW}x{physH}");
        }
    }

    private int _renderTickCounter;

    private void OnMapObserverEvent(string eventName, string? detail)
    {
        switch (eventName)
        {
            case "onDidFinishLoadingStyle":
                _styleReady = true;
                _locIndLayer = null;  // style reload invalidates all layer handles
                _style = _map?.GetStyle();
                _renderNeedsUpdate = true;
                _attrCollapsed = false;  // new style: show full text
                if (_pendingLocInd.HasValue) ApplyPendingLocationIndicator();
                RefreshAttributionText();  // rebuild attribution from TileJSON sources
                OnStyleLoadedReceived?.Invoke(new Maps.Style(null));
                break;
            case "onDidBecomeIdle":
                // Refresh attribution on idle: TileJSON sources may have finished
                // loading *after* onDidFinishLoadingStyle, so this is the earliest
                // reliable point at which Source::getAttribution() is populated.
                if (_attrText.Length == 0) RefreshAttributionText();
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
        }
    }

    private void OnViewSizeChanged(Microsoft.Maui.Graphics.Size newSize)
    {
        if (!_initialized) return;
        int physW = Math.Max(1, (int)(newSize.Width  * _pixelRatio));
        int physH = Math.Max(1, (int)(newSize.Height * _pixelRatio));

        _frontend?.SetSize(physW, physH);
        _map?.SetSize(physW, physH);
        SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0, physW, physH, SWP_NOZORDER);
        UpdateChildWindowPosition();
        _renderNeedsUpdate = true;
        _map?.TriggerRepaint();
    }

    private void UpdateChildWindowPosition()
    {
        if (_childHwnd == IntPtr.Zero || !View.IsLoaded) return;

        // Popup is top-level → coordinates are SCREEN coords.
        // Get the View's position in XAML root, then translate via the owner
        // window's client-to-screen mapping.
        var transform = View.TransformToVisual(null);
        var pt = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

        // Convert XAML-root client coords to screen coords using the owner HWND.
        // Owner is the top-level XAML window. ClientToScreen with (pt.X*ratio, pt.Y*ratio)
        // — but XAML root coords map to the *island bridge* client area, not the
        // top-level client. The bridge's screen origin we already saw was (34,26)
        // on the diagnostic earlier; computing precisely:
        IntPtr bridgeHwnd = TryGetXamlIslandHwnd();
        IntPtr coordOrigin = bridgeHwnd != IntPtr.Zero ? bridgeHwnd : _parentHwnd;
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(coordOrigin, ref origin);

        int x = (int)(origin.X + pt.X * _pixelRatio);
        int y = (int)(origin.Y + pt.Y * _pixelRatio);
        int w = Math.Max(1, (int)(View.ActualWidth  * _pixelRatio));
        int h = Math.Max(1, (int)(View.ActualHeight * _pixelRatio));

        SetWindowPos(_childHwnd, IntPtr.Zero, x, y, w, h, SWP_NOACTIVATE | SWP_NOZORDER);
        PositionOverlays();  // keep overlay windows tracking the map
        RaiseOverlays();     // re-assert overlays above GL popup (can be scrambled on resize/restore)

        if (_logPositionCount < 5)
        {
            _logPositionCount++;
            GetWindowRect(_childHwnd, out var sr);
            System.Diagnostics.Debug.WriteLine(
                $"[MapLibre.Win] UpdateChildWindowPosition #{_logPositionCount} " +
                $"set screen=({x},{y},{w}x{h}) actual=({sr.Left},{sr.Top})-({sr.Right},{sr.Bottom}) " +
                $"originHwnd=0x{coordOrigin.ToInt64():X} screenOrigin=({origin.X},{origin.Y})");
        }
    }
    private int _logPositionCount;

    private IntPtr TryGetXamlIslandHwnd()
    {
        // Strategy: WinUI 3 hosts XAML content inside a child HWND of the top-level
        // window. The class names that have appeared across SDK versions:
        //   "Microsoft.UI.Content.DesktopChildSiteBridge"
        //   "Microsoft.UI.Content.DesktopWindowContentBridge"
        //   "Windows.UI.Composition.DesktopWindowContentBridge"
        // We need to parent our GL HWND inside that child so it renders on top of
        // the XAML compositor surface (which uses DirectComposition; HWND children
        // of the bridge naturally render above DComp content).
        IntPtr found = IntPtr.Zero;
        try
        {
            EnumChildWindows(_parentHwnd, (h, _) =>
            {
                var sb = new System.Text.StringBuilder(256);
                GetClassNameW(h, sb, sb.Capacity);
                var cls = sb.ToString();
                if (cls.Contains("ContentBridge") || cls.Contains("DesktopChildSiteBridge"))
                {
                    found = h;
                    System.Diagnostics.Debug.WriteLine(
                        $"[MapLibre.Win] Found XAML bridge: 0x{h.ToInt64():X} class='{cls}'");
                    return false; // stop
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MapLibre.Win] TryGetXamlIslandHwnd failed: {ex.Message}");
        }
        return found;
    }

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
        if (_style.HasSource(sourceName)) return;
        var s = _style.AddGeoJsonSource(sourceName);
        s.SetGeoJson(source);
    }

    public void SetGeoJsonSource(string sourceName, string source)
    {
        if (!_styleReady || _style == null) return;
        // AddGeoJsonSource with overwrite not directly supported — update via URL trick
        AddGeoJsonSource(sourceName, source);
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
        // C ABI does not yet expose image sources with lat/lng quads; add as raster-tile workaround
        _style.AddRasterSource(sourceName, url);
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

    // ── Overlay window management ──────────────────────────────────────────────

    private static bool _overlayClassRegistered;
    private static WndProcDelegate? _overlayClassWndProc;  // keep-alive for class WndProc

    private static void EnsureOverlayClassRegistered()
    {
        if (_overlayClassRegistered) return;
        // The class WndProc is just a no-op placeholder; each window gets subclassed.
        _overlayClassWndProc = (h, m, w, l) => DefWindowProcA(h, m, w, l);
        var wc = new WNDCLASSEXA
        {
            cbSize        = (uint)Marshal.SizeOf<WNDCLASSEXA>(),
            style         = 0,
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_overlayClassWndProc),
            hInstance     = GetModuleHandleW(IntPtr.Zero),
            lpszClassName = OverlayClass,
        };
        RegisterClassExA(ref wc);
        _overlayClassRegistered = true;
    }

    /// <summary>
    /// Creates the nav and attribution overlay HWNDs and installs their WndProcs.
    /// Must be called after <see cref="TryInitialize"/> has set <c>_childHwnd</c>.
    /// </summary>
    private void CreateOverlays()
    {
        EnsureOverlayClassRegistered();

        // Nav panel: WS_POPUP | WS_VISIBLE, WS_EX_NOACTIVATE so it doesn't steal focus.
        // Sized to 29×90 (3 buttons) in physical pixels; positioned top-right.
        _navWndProc = NavOverlayWndProc;
        _navHwnd = CreateWindowExA(
            WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            OverlayClass, "",
            WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 1, 1,
            _effectiveParentHwnd, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        if (_navHwnd != IntPtr.Zero)
        {
            // Place nav overlay just above the GL child in z-order (not TOPMOST —
            // that would render above every other app window on the desktop).
            SetWindowPos(_navHwnd, _childHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowLongPtr(_navHwnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_navWndProc));
            // Create the GDI font for nav buttons once.
            int fontH = -(int)(_pixelRatio * 14);
            _navFont = CreateFontW(fontH, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, "Segoe UI Symbol");
        }

        // Attribution band: small always-visible text overlay, bottom-right.
        _attrWndProc = AttrOverlayWndProc;
        _attrHwnd = CreateWindowExA(
            WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
            OverlayClass, "",
            WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 1, 1,
            _effectiveParentHwnd, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        if (_attrHwnd != IntPtr.Zero)
        {
            // 92% opacity — matches maplibre-gl-js attribution style.
            SetLayeredWindowAttributes(_attrHwnd, 0, 235, LWA_ALPHA);
            // Place attr overlay just above the GL child (and nav) in z-order.
            SetWindowPos(_attrHwnd, _childHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowLongPtr(_attrHwnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_attrWndProc));
            int fontH = -(int)(_pixelRatio * AttrFontSizePt);
            _attrFont = CreateFontW(fontH, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, "Segoe UI");
        }

        PositionOverlays();
        ShowOverlays();
    }

    /// <summary>Show/hide overlays based on current enabled flags.</summary>
    private void ShowOverlays()
    {
        if (_navHwnd  != IntPtr.Zero)
        {
            uint showFlag = (_showNavControls && _initialized)
                ? WS_VISIBLE : 0;
            uint style = (uint)GetWindowLongA(_navHwnd, GWL_STYLE);
            SetWindowLongPtr(_navHwnd, GWL_STYLE,
                (IntPtr)((_showNavControls && _initialized)
                    ? (style | WS_VISIBLE)
                    : (style & ~WS_VISIBLE)));
            SetWindowPos(_navHwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW_OR_HIDE(
                    _showNavControls && _initialized));
        }
        if (_attrHwnd != IntPtr.Zero)
        {
            SetWindowPos(_attrHwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW_OR_HIDE(
                    _showAttrControl && _initialized && _attrText.Length > 0));
        }
    }

    private static uint SWP_SHOWWINDOW_OR_HIDE(bool show)
        => show ? SWP_SHOWWINDOW : SWP_HIDEWINDOW;

    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLongA(IntPtr hWnd, int nIndex);
    private const int GWL_STYLE = -16;

    /// <summary>Position overlays relative to the GL popup window.</summary>
    private void PositionOverlays()
    {
        if (_childHwnd == IntPtr.Zero) return;
        GetWindowRect(_childHwnd, out var wr);
        int mapW = wr.Right  - wr.Left;
        int mapH = wr.Bottom - wr.Top;

        int marginPx  = (int)(NavPanelMargin * _pixelRatio);
        int btnSizePx = (int)(NavButtonSize  * _pixelRatio);

        if (_navHwnd != IntPtr.Zero && _showNavControls)
        {
            int panelH = btnSizePx * 3 + 2;  // +2 for separator lines
            int navX   = wr.Left + mapW - btnSizePx - marginPx;
            int navY   = wr.Top  + marginPx;
            var nr = (navX, navY, btnSizePx, panelH);
            if (nr != _lastNavRect)
            {
                _lastNavRect = nr;
                SetWindowPos(_navHwnd, IntPtr.Zero, navX, navY, btnSizePx, panelH, SWP_NOACTIVATE | SWP_NOZORDER);
                InvalidateRect(_navHwnd, IntPtr.Zero, true);
            }
        }

        if (_attrHwnd != IntPtr.Zero && _showAttrControl && _attrText.Length > 0)
        {
            int padH = (int)(AttrPadH * _pixelRatio);
            int padV = (int)(AttrPadV * _pixelRatio);

            // Cap width so attribution never overflows the map edge.
            int maxAttrW = Math.Max(120, mapW - marginPx * 2);
            int innerW   = maxAttrW - padH * 2;

            // Use displayed text (full or collapsed ⓘ) for measurement.
            string measureText  = _attrCollapsed ? "ⓘ" : _attrText;
            uint   measureFlags = _attrCollapsed
                ? DT_CENTER | DT_SINGLELINE | DT_CALCRECT | DT_NOPREFIX
                : DT_LEFT   | DT_WORDBREAK  | DT_CALCRECT | DT_NOPREFIX;

            // Only re-measure via GDI when displayed text or available width changes.
            if (measureText != _lastMeasuredAttrText || innerW != _lastMeasuredAttrInnerW)
            {
                var hdc     = GetDC(IntPtr.Zero);
                var oldFont = SelectObject(hdc, _attrFont != IntPtr.Zero ? _attrFont : IntPtr.Zero);
                var calcRc  = new RECT { Left = 0, Top = 0, Right = innerW, Bottom = 0 };
                DrawTextW(hdc, measureText, measureText.Length, ref calcRc, measureFlags);
                SelectObject(hdc, oldFont);
                ReleaseDC(IntPtr.Zero, hdc);
                _cachedAttrMeasure      = (calcRc.Right, calcRc.Bottom);
                _lastMeasuredAttrText   = measureText;
                _lastMeasuredAttrInnerW = innerW;
            }

            int attrW = _cachedAttrMeasure.cx + padH * 2;
            int attrH = _cachedAttrMeasure.cy + padV * 2;
            int attrX = wr.Left + mapW - attrW - marginPx;
            int attrY = wr.Top  + mapH - attrH - marginPx;
            var ar = (attrX, attrY, attrW, attrH);
            if (ar != _lastAttrRect)
            {
                _lastAttrRect = ar;
                SetWindowPos(_attrHwnd, IntPtr.Zero, attrX, attrY, attrW, attrH, SWP_NOACTIVATE | SWP_NOZORDER);
                InvalidateRect(_attrHwnd, IntPtr.Zero, true);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out SIZE lpSize);

    private void DestroyOverlays()
    {
        if (_childHwnd != IntPtr.Zero) KillTimer(_childHwnd, (IntPtr)OverlayZTimerId);
        if (_navFont  != IntPtr.Zero) { DeleteObject(_navFont);  _navFont  = IntPtr.Zero; }
        if (_attrFont != IntPtr.Zero) { DeleteObject(_attrFont); _attrFont = IntPtr.Zero; }
        if (_navHwnd  != IntPtr.Zero) { DestroyWindow(_navHwnd);  _navHwnd  = IntPtr.Zero; }
        if (_attrHwnd != IntPtr.Zero) { DestroyWindow(_attrHwnd); _attrHwnd = IntPtr.Zero; }
        _navWndProc  = null;
        _attrWndProc = null;
        _lastNavRect  = default;
        _lastAttrRect = default;
        _attrCollapseTimer?.Dispose(); _attrCollapseTimer = null;
    }

    // ── Nav overlay WndProc ────────────────────────────────────────────────────

    private const uint WM_PAINT   = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_NCHITTEST  = 0x0084;
    private const uint WM_SETCURSOR  = 0x0020;
    private const uint WM_TIMER      = 0x0113;
    private const uint OverlayZTimerId = 42;
    private static readonly IntPtr HTCLIENT = new(1);

    private IntPtr NavOverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND: return new IntPtr(1);  // no erase flicker
            case WM_PAINT:
                PaintNavPanel(hWnd);
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
            {
                int btnSizePx = (int)(NavButtonSize * _pixelRatio);
                int y = (short)((unchecked((int)lParam.ToInt64()) >> 16) & 0xFFFF);
                int btn = y / (btnSizePx + 1);
                switch (btn)
                {
                    case 0: ZoomIn();    break;  // top button = zoom in (+)
                    case 1: ZoomOut();   break;  // middle     = zoom out (−)
                    case 2: ResetNorth();break;  // bottom     = compass/reset-north
                }
                return IntPtr.Zero;
            }
            case WM_NCHITTEST: return HTCLIENT;
        }
        return DefWindowProcA(hWnd, msg, wParam, lParam);
    }

    private void PaintNavPanel(IntPtr hWnd)
    {
        var ps = new PAINTSTRUCT
        {
            rgbReserved = new byte[32]
        };
        var hdc = BeginPaint(hWnd, out ps);
        try
        {
            GetClientRect(hWnd, out var rc);
            int btnSizePx = rc.Right;  // window width == button size

            // White rounded background
            var bgBrush  = CreateSolidBrush(0x00FFFFFF);
            var borderPen = CreatePen(PS_SOLID, 1, 0x00CCCCCC);
            var oldBrush = SelectObject(hdc, bgBrush);
            var oldPen   = SelectObject(hdc, borderPen);
            RoundRect(hdc, 0, 0, rc.Right, rc.Bottom, 6, 6);
            SelectObject(hdc, oldBrush);
            SelectObject(hdc, oldPen);
            DeleteObject(bgBrush);
            DeleteObject(borderPen);

            // Divider lines between buttons
            var divPen = CreatePen(PS_SOLID, 1, 0x00E0E0E0);
            SelectObject(hdc, divPen);
            MoveToEx(hdc, 4, btnSizePx, IntPtr.Zero);
            LineTo(hdc, btnSizePx - 4, btnSizePx);
            MoveToEx(hdc, 4, btnSizePx * 2 + 1, IntPtr.Zero);
            LineTo(hdc, btnSizePx - 4, btnSizePx * 2 + 1);
            SelectObject(hdc, oldPen);
            DeleteObject(divPen);

            // Button labels: +, −, ↑ (compass north arrow)
            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, 0x00333333);
            var oldFont = SelectObject(hdc, _navFont != IntPtr.Zero ? _navFont : IntPtr.Zero);

            // Measure each symbol to center it.
            PaintCenteredText(hdc, "+",    0,             btnSizePx, btnSizePx);
            PaintCenteredText(hdc, "−",    btnSizePx + 1, btnSizePx, btnSizePx);
            // ↑ compass arrow (Unicode north arrow ↑)
            PaintCenteredText(hdc, "↑",    btnSizePx * 2 + 2, btnSizePx, btnSizePx);

            SelectObject(hdc, oldFont);
        }
        finally { EndPaint(hWnd, ref ps); }
    }

    private static void PaintCenteredText(IntPtr hdc, string text, int rowY, int rowH, int rowW)
    {
        GetTextExtentPoint32W(hdc, text, text.Length, out var sz);
        int x = (rowW - sz.cx) / 2;
        int y = rowY + (rowH - sz.cy) / 2;
        TextOutW(hdc, x, y, text, text.Length);
    }

    // ── Attribution overlay WndProc ────────────────────────────────────────────

    private IntPtr AttrOverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND: return new IntPtr(1);
            case WM_PAINT:
                PaintAttribution(hWnd);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (_attrCollapsed) ExpandAttribution();
                else                CollapseAttribution();
                return IntPtr.Zero;
            case WM_NCHITTEST: return HTCLIENT;
        }
        return DefWindowProcA(hWnd, msg, wParam, lParam);
    }

    private void PaintAttribution(IntPtr hWnd)
    {
        var ps = new PAINTSTRUCT { rgbReserved = new byte[32] };
        var hdc = BeginPaint(hWnd, out ps);
        try
        {
            GetClientRect(hWnd, out var rc);
            int padH = (int)(AttrPadH * _pixelRatio);
            int padV = (int)(AttrPadV * _pixelRatio);

            // Slightly translucent white background (actual alpha set by WS_EX_LAYERED on the HWND).
            var bgBrush   = CreateSolidBrush(0x00F8F8F8);
            var borderPen = CreatePen(PS_SOLID, 1, 0x00D0D0D0);
            var oldBrush  = SelectObject(hdc, bgBrush);
            var oldPen    = SelectObject(hdc, borderPen);
            RoundRect(hdc, 0, 0, rc.Right, rc.Bottom, 4, 4);
            SelectObject(hdc, oldBrush);
            SelectObject(hdc, oldPen);
            DeleteObject(bgBrush);
            DeleteObject(borderPen);

            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, 0x00555555);
            var oldFont = SelectObject(hdc, _attrFont != IntPtr.Zero ? _attrFont : IntPtr.Zero);
            var textRc  = new RECT { Left = padH, Top = padV, Right = rc.Right - padH, Bottom = rc.Bottom - padV };
            string paintText = _attrCollapsed ? "ⓘ" : _attrText;
            uint   dtFlags   = _attrCollapsed
                ? DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX
                : DT_LEFT   | DT_WORDBREAK | DT_NOPREFIX;
            DrawTextW(hdc, paintText, paintText.Length, ref textRc, dtFlags);
            SelectObject(hdc, oldFont);
        }
        finally { EndPaint(hWnd, ref ps); }
    }

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
        // Strip HTML tags to plain text (attribution strings from OSM are like
        // "© <a href='...'>OpenStreetMap</a> contributors" — we strip the links for now).
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(StripHtmlTags(part));
        }
        _attrText = sb.ToString();
        if (_attrText.Length > 0)
        {
            ExpandAttribution();
        }
        else if (_attrHwnd != IntPtr.Zero)
        {
            ShowOverlays();
        }
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
        ShowOverlays();
    }

    public void SetShowAttributionControl(bool show, string? customAttribution)
    {
        _showAttrControl   = show;
        _customAttribution = customAttribution;
        _attrCollapsed     = false;  // reset collapse when attribution settings change
        RefreshAttributionText();
    }

    private void ExpandAttribution()
    {
        _attrCollapsed = false;
        if (_attrHwnd != IntPtr.Zero && _showAttrControl)
        {
            PositionOverlays();
            InvalidateRect(_attrHwnd, IntPtr.Zero, false);
            ShowOverlays();
        }
        ScheduleAutoCollapse();
    }

    private void CollapseAttribution()
    {
        _attrCollapseTimer?.Dispose();
        _attrCollapseTimer = null;
        if (_attrCollapsed) return;
        _attrCollapsed = true;
        if (_attrHwnd != IntPtr.Zero && _showAttrControl && _attrText.Length > 0)
        {
            PositionOverlays();
            InvalidateRect(_attrHwnd, IntPtr.Zero, false);
        }
    }

    private void ScheduleAutoCollapse()
    {
        _attrCollapseTimer?.Dispose();
        _attrCollapseTimer = new System.Threading.Timer(
            _ => CollapseAttribution(), null,
            TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    // ── Popup WndProc (mouse input) ────────────────────────────────────────────

    /// <summary>Re-asserts overlay HWNDs above the GL popup in z-order.</summary>
    private void RaiseOverlays()
    {
        if (_navHwnd  != IntPtr.Zero)
            SetWindowPos(_navHwnd,  _childHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (_attrHwnd != IntPtr.Zero)
            SetWindowPos(_attrHwnd, _childHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private IntPtr PopupWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TIMER when (uint)wParam.ToInt64() == OverlayZTimerId:
                RaiseOverlays();
                return IntPtr.Zero;

            case WM_SETCURSOR:
            {
                // Override the cursor for the map client area.
                // wParam is the HWND under the cursor; only set if over the map itself (not a child overlay).
                if (wParam == hWnd && (unchecked((int)lParam.ToInt64()) & 0xFFFF) == 1 /* HTCLIENT */)
                {
                    // Dragging: four-direction move arrow (communicates free pan)
                    // Hover:    pointing hand (communicates interactivity)
                    int idc = _dragging ? IDC_SIZEALL : IDC_HAND;
                    SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)idc));
                    return new IntPtr(1);  // non-zero suppresses DefWindowProc override
                }
                break;
            }

            case WM_LBUTTONDOWN:
            {
                SetCapture(hWnd);
                _dragging  = true;
                _lastMouseX = (short)(unchecked((int)lParam.ToInt64()) & 0xFFFF);
                _lastMouseY = (short)((unchecked((int)lParam.ToInt64()) >> 16) & 0xFFFF);
                // Convert physical px → logical for mbgl (it works in physical coords
                // relative to its own viewport, so pass physical directly).
                _map?.OnPanStart(_lastMouseX, _lastMouseY);
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                if (!_dragging) break;
                int x  = (short)(unchecked((int)lParam.ToInt64()) & 0xFFFF);
                int y  = (short)((unchecked((int)lParam.ToInt64()) >> 16) & 0xFFFF);
                int dx = x - _lastMouseX;
                int dy = y - _lastMouseY;
                _lastMouseX = x;
                _lastMouseY = y;
                _map?.OnPanMove(dx, dy);
                _renderNeedsUpdate = true;
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                if (_dragging)
                {
                    ReleaseCapture();
                    _dragging = false;
                    _map?.OnPanEnd();
                }
                return IntPtr.Zero;
            }
            case WM_LBUTTONDBLCLK:
            {
                int x = (short)(unchecked((int)lParam.ToInt64()) & 0xFFFF);
                int y = (short)((unchecked((int)lParam.ToInt64()) >> 16) & 0xFFFF);
                _map?.OnDoubleTap(x, y);
                _renderNeedsUpdate = true;
                return IntPtr.Zero;
            }
            case WM_MOUSEWHEEL:
            {
                // wParam hi-word is wheel delta; lo-word is key state.
                // Cursor position is in screen coords in lParam.
                int delta = (short)((unchecked((int)wParam.ToInt64()) >> 16) & 0xFFFF);
                // Convert screen cursor pos → popup client coords.
                int screenX = (short)(unchecked((int)lParam.ToInt64()) & 0xFFFF);
                int screenY = (short)((unchecked((int)lParam.ToInt64()) >> 16) & 0xFFFF);
                GetWindowRect(hWnd, out var wr);
                int cx = screenX - wr.Left;
                int cy = screenY - wr.Top;
                _map?.OnScroll((double)delta / WHEEL_DELTA, cx, cy);
                _renderNeedsUpdate = true;
                return IntPtr.Zero;
            }

            case WM_GESTURE:
            {
                // Handle pinch-to-zoom via the Win32 gesture API.  This fires for
                // both touch screens and precision touchpads without going through
                // WinUI 3's ManipulationDelta, which overflows on touchpad pinch
                // (microsoft/microsoft-ui-xaml#8084).
                var gi = new GESTUREINFO { cbSize = (uint)Marshal.SizeOf<GESTUREINFO>() };
                if (GetGestureInfo(wParam, ref gi) && gi.dwID == GID_ZOOM)
                {
                    // ptsLocation is in screen coords — convert to popup client coords.
                    GetWindowRect(hWnd, out var gwr);
                    int gcx = gi.ptsLocationX - gwr.Left;
                    int gcy = gi.ptsLocationY - gwr.Top;

                    uint dist = (uint)(gi.ullArguments & 0xFFFFFFFFUL);

                    if ((gi.dwFlags & GF_BEGIN) != 0)
                    {
                        // Record starting distance; no zoom change on the first frame.
                        _gesturePrevDist = dist;
                    }
                    else if (_gesturePrevDist > 0 && dist > 0)
                    {
                        // Incremental per-frame scale — consistent with what the
                        // native mbgl_map_on_pinch expects (log2 of a small ratio).
                        double scale = (double)dist / _gesturePrevDist;
                        _map?.OnPinch(scale, gcx, gcy);
                        _renderNeedsUpdate = true;
                        _gesturePrevDist = dist;
                    }

                    if ((gi.dwFlags & GF_END) != 0)
                        _gesturePrevDist = 0;
                }
                CloseGestureInfoHandle(wParam);
                return IntPtr.Zero;
            }
        }
        return _origWndProc != IntPtr.Zero
            ? CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam)
            : DefWindowProcA(hWnd, msg, wParam, lParam);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Public entry point called by <see cref="MapLibreMapHandler"/> from its
    /// <c>DisconnectHandler</c> override. Idempotent — safe to call more than once.
    /// </summary>
    public void Shutdown() => DisposeNative();

    private void DisposeNative()
    {
        // Stop the pump FIRST so no tick can run after native objects are freed
        // (avoids 0xc0000374 heap corruption on page tear-down).
        if (_runLoopTimer != null)
        {
            _runLoopTimer.Stop();
            _runLoopTimer.Tick -= OnRunLoopTick;
            _runLoopTimer = null;
        }

        // Make GL context current on this thread so frontend/map destructors can
        // free GL resources cleanly.
        if (_hGLRC != IntPtr.Zero && _hDC != IntPtr.Zero)
        {
            try { wglMakeCurrent(_hDC, _hGLRC); } catch { }
        }

        _style    = null;
        _map?.Dispose();      _map      = null;
        // Drain any callbacks queued by ~Map() onto the libuv loop.
        try { for (int i = 0; i < 8; i++) _runLoop?.RunOnce(); } catch { }
        // NOTE: do NOT call _frontend.Dispose() here. mbgl_map_create transfers
        // ownership of the frontend pointer into the native CabiMap struct, so
        // mbgl_map_destroy already destroyed the frontend C++ object. Calling
        // mbgl_frontend_destroy afterwards would be a double-free → 0xc0000374.
        // MbglMap.ctor calls frontend.TransferOwnership() which zeroes the handle,
        // so Dispose() is already a no-op — but we skip it here for clarity.
        _frontend = null;
        _runLoop?.Dispose();  _runLoop  = null;

        if (_hGLRC != IntPtr.Zero)
        {
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(_hGLRC);
            _hGLRC = IntPtr.Zero;
        }
        if (_hDC != IntPtr.Zero)
        {
            ReleaseDC(_childHwnd, _hDC);
            _hDC = IntPtr.Zero;
        }
        // Destroy overlay windows before the GL popup so GDI handles are clean.
        DestroyOverlays();
        if (_childHwnd != IntPtr.Zero)
        {
            // Restore the original WndProc before destroying the window so no
            // stale subclass pointer can be called during WM_DESTROY processing.
            if (_origWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_childHwnd, GWLP_WNDPROC, _origWndProc);
                _origWndProc = IntPtr.Zero;
            }
            _subclassProc = null;
            DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }
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

    // ── Tier 1 – gesture / interactive movement ───────────────────────────────
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
