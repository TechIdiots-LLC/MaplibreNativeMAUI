#if WINDOWS
/**
 * GlDxgiInteropContext.Windows.cs — off-screen WGL context for the MAUI Windows map renderer.
 *
 * Creates a hidden, full-size Win32 window with an OpenGL context.  MapLibre's WGL backend
 * renders into FBO 0 (the window's framebuffer) as it always does; after each frame the caller
 * reads the pixels with glReadPixels and hands them to a WinUI WriteableBitmap.
 *
 * Replaces the earlier WGL_NV_DX_interop2 + D3D11 design which failed because
 * WGLRenderableResource::bind() in platform_frontend_windows.cpp unconditionally calls
 * glBindFramebuffer(0), so MapLibre's output always lands in FBO 0 — not in any custom FBO.
 */
using System.Runtime.InteropServices;

namespace MapLibreNative.Maui.Handlers.WinUI;

/// <summary>
/// Owns a hidden Win32 window and its WGL rendering context.  MapLibre renders into FBO 0 of
/// that window; call <see cref="ReadPixels"/> after each <c>MbglFrontend.Render</c> to copy the
/// result into the caller-supplied pixel buffer (e.g. a WinUI <c>WriteableBitmap</c>'s backing store).
/// </summary>
internal sealed class GlDxgiInteropContext : IDisposable
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int fmt, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hDC);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr hGLRC);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr hDC, IntPtr hGLRC);
    [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int w, int h, uint fmt, uint type, IntPtr data);

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
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern ushort RegisterClassExA(ref WNDCLASSEXA wc);

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
    // Single-buffered (no PFD_DOUBLEBUFFER) — glReadPixels captures from the only buffer.
    private const uint CS_OWNDC       = 0x0020;
    private const uint WS_POPUP       = 0x80000000;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint GL_BGRA        = 0x80E1;   // GL 1.2 — supported by all modern drivers
    private const uint GL_UNSIGNED_BYTE = 0x1401;

    // ── State ─────────────────────────────────────────────────────────────────

    private static bool _classRegistered;
    private static WndProcDelegate? _wndProcKeepAlive;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    private const string WndClass = "MlnGlHiddenDxgi";

    private IntPtr _hwnd, _hdc, _glrc;

    /// <summary>Physical pixel width of the current surface.</summary>
    public int Width { get; private set; }
    /// <summary>Physical pixel height of the current surface.</summary>
    public int Height { get; private set; }

    /// <summary>The hidden window's device context — pass as <c>surfaceHandle</c> to <see cref="MbglFrontend"/>.</summary>
    public IntPtr Hdc => _hdc;
    /// <summary>The WGL render context — pass as <c>glContext</c> to <see cref="MbglFrontend"/>.</summary>
    public IntPtr GlContext => _glrc;

    // ── Init ──────────────────────────────────────────────────────────────────

    /// <summary>Creates the hidden Win32 window and its WGL context.</summary>
    public void Initialize() => CreateHiddenGlContext();

    public void MakeCurrent() => wglMakeCurrent(_hdc, _glrc);

    /// <summary>
    /// Resizes the hidden window so its framebuffer matches the map.
    /// Call before the first render and whenever the map view resizes.
    /// </summary>
    public void Resize(int width, int height)
    {
        width  = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        Width  = width;
        Height = height;
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        MakeCurrent(); // re-bind after surface reallocation
    }

    /// <summary>
    /// Reads the rendered pixels (bottom-left origin, BGRA) from FBO 0 into
    /// <paramref name="buffer"/>. Call after <see cref="MbglFrontend.Render"/>.
    /// </summary>
    public void ReadPixels(IntPtr buffer) =>
        glReadPixels(0, 0, Width, Height, GL_BGRA, GL_UNSIGNED_BYTE, buffer);

    private void CreateHiddenGlContext()
    {
        EnsureClass();
        _hwnd = CreateWindowEx(0, WndClass, "", WS_POPUP, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create hidden GL window.");
        _hdc = GetDC(_hwnd);

        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize     = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion  = 1,
            dwFlags   = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL,
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8,
        };
        int fmt = ChoosePixelFormat(_hdc, ref pfd);
        SetPixelFormat(_hdc, fmt, ref pfd);
        _glrc = wglCreateContext(_hdc);
        wglMakeCurrent(_hdc, _glrc);
    }

    private static void EnsureClass()
    {
        if (_classRegistered) return;
        _wndProcKeepAlive = (h, m, w, l) => DefWindowProcW(h, m, w, l);
        var wc = new WNDCLASSEXA
        {
            cbSize    = (uint)Marshal.SizeOf<WNDCLASSEXA>(),
            style     = CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = GetModuleHandleW(IntPtr.Zero),
            lpszClassName = WndClass,
        };
        RegisterClassExA(ref wc);
        _classRegistered = true;
    }

    public void Dispose()
    {
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        if (_glrc != IntPtr.Zero) { wglDeleteContext(_glrc); _glrc = IntPtr.Zero; }
        if (_hdc  != IntPtr.Zero) { ReleaseDC(_hwnd, _hdc); _hdc  = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd);   _hwnd = IntPtr.Zero; }
    }
}
#endif
