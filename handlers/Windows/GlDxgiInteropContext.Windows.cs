#if WINDOWS
/**
 * GlDxgiInteropContext.Windows.cs — OpenGL ⇄ Direct3D11 bridge for the MAUI Windows
 * SwapChainPanel renderer.
 *
 * MapLibre (mln-cabi's GL frontend) renders into an FBO whose colour attachment is a
 * D3D11 render-target texture shared with the GL context via WGL_NV_DX_interop2. That
 * offscreen texture is then copied into the composition swap chain's back buffer by
 * SwapChainMapView (see that file). A stable offscreen texture is used rather than the
 * back buffer directly because composition swap chains require the flip model, whose
 * back buffer rotates on every Present.
 *
 * This is the D3D11 sibling of the WPF GlDxInteropContext (which targets D3D9/D3DImage).
 * Requires WGL_NV_DX_interop2 (all modern desktop GPUs/drivers).
 */
using System.Runtime.InteropServices;

namespace MapLibreNative.Maui.Handlers.WinUI;

/// <summary>
/// Owns the hidden WGL context and the GL FBO whose colour attachment is a D3D11 texture
/// shared through WGL_NV_DX_interop2. The D3D11 device and the shared offscreen texture are
/// owned by <see cref="SwapChainMapView"/> and passed in.
/// </summary>
internal sealed class GlDxgiInteropContext : IDisposable
{
    // ── WGL / GL P/Invoke ─────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int fmt, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hDC);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr hGLRC);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr hDC, IntPtr hGLRC);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] private static extern void glGenTextures(int n, uint[] textures);
    [DllImport("opengl32.dll")] private static extern void glDeleteTextures(int n, uint[] textures);

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
    private const uint PFD_DOUBLEBUFFER = 0x00000001;

    private const uint GL_FRAMEBUFFER = 0x8D40;
    private const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    private const uint GL_DEPTH_STENCIL_ATTACHMENT = 0x821A;
    private const uint GL_RENDERBUFFER = 0x8D41;
    private const uint GL_DEPTH24_STENCIL8 = 0x88F0;
    private const uint GL_TEXTURE_2D = 0x0DE1;
    private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;

    private delegate void GenT(int n, uint[] o);
    private delegate void BindFbo(uint target, uint fb);
    private delegate void FboTex2D(uint target, uint attach, uint textarget, uint tex, int level);
    private delegate void BindRbo(uint target, uint rb);
    private delegate void RboStorage(uint target, uint fmt, int w, int h);
    private delegate void FboRbo(uint target, uint attach, uint rbtarget, uint rb);
    private delegate uint CheckFbo(uint target);
    private delegate void DelObjs(int n, uint[] o);

    private GenT _glGenFramebuffers = null!, _glGenRenderbuffers = null!;
    private BindFbo _glBindFramebuffer = null!;
    private FboTex2D _glFramebufferTexture2D = null!;
    private BindRbo _glBindRenderbuffer = null!;
    private RboStorage _glRenderbufferStorage = null!;
    private FboRbo _glFramebufferRenderbuffer = null!;
    private CheckFbo _glCheckFramebufferStatus = null!;
    private DelObjs _glDeleteFramebuffers = null!, _glDeleteRenderbuffers = null!;

    // ── WGL_NV_DX_interop ─────────────────────────────────────────────────────

    private delegate IntPtr DxOpenDevice(IntPtr dxDevice);
    private delegate bool DxCloseDevice(IntPtr hDevice);
    private delegate IntPtr DxRegisterObject(IntPtr hDevice, IntPtr dxObject, uint name, uint type, uint access);
    private delegate bool DxUnregisterObject(IntPtr hDevice, IntPtr hObject);
    private delegate bool DxLock(IntPtr hDevice, int count, IntPtr[] hObjects);
    private delegate bool DxUnlock(IntPtr hDevice, int count, IntPtr[] hObjects);

    private DxOpenDevice _dxOpenDevice = null!;
    private DxCloseDevice _dxCloseDevice = null!;
    private DxRegisterObject _dxRegisterObject = null!;
    private DxUnregisterObject _dxUnregisterObject = null!;
    private DxLock _dxLock = null!;
    private DxUnlock _dxUnlock = null!;

    // Access flag: we fully overwrite the texture each frame (render target).
    private const uint WGL_ACCESS_WRITE_DISCARD_NV = 0x0002;

    // ── State ─────────────────────────────────────────────────────────────────

    private static bool _classRegistered;
    private static WndProcDelegate? _wndProcKeepAlive;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    private const string WndClass = "MlnGlDxgiHidden";

    private IntPtr _hwnd, _hdc, _glrc;
    private IntPtr _glDxDevice;    // wglDXOpenDeviceNV handle
    private IntPtr _dxRegistered;  // registered offscreen texture
    private uint _glColorTex, _fbo, _depthRbo;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint Framebuffer => _fbo;
    public IntPtr Hdc => _hdc;
    public IntPtr GlContext => _glrc;

    // ── Init ──────────────────────────────────────────────────────────────────

    /// <summary>Creates the hidden GL context and opens the interop device for <paramref name="d3d11Device"/>.</summary>
    public void Initialize(IntPtr d3d11Device)
    {
        CreateHiddenGlContext();
        LoadGlFunctions();
        LoadNvDxInterop();
        _glDxDevice = _dxOpenDevice(d3d11Device);
        if (_glDxDevice == IntPtr.Zero)
            throw new InvalidOperationException("wglDXOpenDeviceNV failed — WGL_NV_DX_interop2 unavailable on this GPU/driver.");
    }

    public void MakeCurrent() => wglMakeCurrent(_hdc, _glrc);

    private void CreateHiddenGlContext()
    {
        EnsureClass();
        _hwnd = CreateWindowEx(0, WndClass, "", 0, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create hidden GL window.");
        _hdc = GetDC(_hwnd);

        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
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
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXA>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = GetModuleHandleW(IntPtr.Zero),
            lpszClassName = WndClass,
        };
        RegisterClassExA(ref wc);
        _classRegistered = true;
    }

    private T Load<T>(string name) where T : Delegate
    {
        var p = wglGetProcAddress(name);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException($"Required GL/WGL entry point '{name}' not found.");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private void LoadGlFunctions()
    {
        _glGenFramebuffers = Load<GenT>("glGenFramebuffers");
        _glGenRenderbuffers = Load<GenT>("glGenRenderbuffers");
        _glBindFramebuffer = Load<BindFbo>("glBindFramebuffer");
        _glFramebufferTexture2D = Load<FboTex2D>("glFramebufferTexture2D");
        _glBindRenderbuffer = Load<BindRbo>("glBindRenderbuffer");
        _glRenderbufferStorage = Load<RboStorage>("glRenderbufferStorage");
        _glFramebufferRenderbuffer = Load<FboRbo>("glFramebufferRenderbuffer");
        _glCheckFramebufferStatus = Load<CheckFbo>("glCheckFramebufferStatus");
        _glDeleteFramebuffers = Load<DelObjs>("glDeleteFramebuffers");
        _glDeleteRenderbuffers = Load<DelObjs>("glDeleteRenderbuffers");
    }

    private void LoadNvDxInterop()
    {
        _dxOpenDevice = Load<DxOpenDevice>("wglDXOpenDeviceNV");
        _dxCloseDevice = Load<DxCloseDevice>("wglDXCloseDeviceNV");
        _dxRegisterObject = Load<DxRegisterObject>("wglDXRegisterObjectNV");
        _dxUnregisterObject = Load<DxUnregisterObject>("wglDXUnregisterObjectNV");
        _dxLock = Load<DxLock>("wglDXLockObjectsNV");
        _dxUnlock = Load<DxUnlock>("wglDXUnlockObjectsNV");
    }

    // ── Shared texture / FBO ──────────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="d3d11Texture"/> (a render-target texture owned by the caller) as the
    /// FBO colour attachment at the given size. Call whenever the offscreen texture is (re)created.
    /// </summary>
    public void SetSharedTexture(IntPtr d3d11Texture, int width, int height)
    {
        MakeCurrent();
        ReleaseShared();

        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        var tex = new uint[1];
        glGenTextures(1, tex);
        _glColorTex = tex[0];
        _dxRegistered = _dxRegisterObject(_glDxDevice, d3d11Texture, _glColorTex,
            GL_TEXTURE_2D, WGL_ACCESS_WRITE_DISCARD_NV);
        if (_dxRegistered == IntPtr.Zero)
            throw new InvalidOperationException("wglDXRegisterObjectNV failed for the offscreen texture.");

        var fb = new uint[1]; _glGenFramebuffers(1, fb); _fbo = fb[0];
        var rb = new uint[1]; _glGenRenderbuffers(1, rb); _depthRbo = rb[0];

        Lock();
        _glBindFramebuffer(GL_FRAMEBUFFER, _fbo);
        _glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _glColorTex, 0);
        _glBindRenderbuffer(GL_RENDERBUFFER, _depthRbo);
        _glRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH24_STENCIL8, Width, Height);
        _glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GL_RENDERBUFFER, _depthRbo);
        uint status = _glCheckFramebufferStatus(GL_FRAMEBUFFER);
        Unlock();
        if (status != GL_FRAMEBUFFER_COMPLETE)
            throw new InvalidOperationException($"Interop framebuffer incomplete (status 0x{status:X}).");
    }

    /// <summary>Binds the interop FBO. Call between <see cref="Lock"/> and mbgl's render.</summary>
    public void BindFramebuffer() => _glBindFramebuffer(GL_FRAMEBUFFER, _fbo);

    /// <summary>Hands the shared texture to GL for rendering.</summary>
    public void Lock()
    {
        if (_dxRegistered != IntPtr.Zero)
            _dxLock(_glDxDevice, 1, new[] { _dxRegistered });
    }

    /// <summary>Returns the shared texture to D3D so it can be copied to the back buffer.</summary>
    public void Unlock()
    {
        if (_dxRegistered != IntPtr.Zero)
            _dxUnlock(_glDxDevice, 1, new[] { _dxRegistered });
    }

    private void ReleaseShared()
    {
        if (_dxRegistered != IntPtr.Zero) { _dxUnregisterObject(_glDxDevice, _dxRegistered); _dxRegistered = IntPtr.Zero; }
        if (_fbo != 0) { _glDeleteFramebuffers(1, new[] { _fbo }); _fbo = 0; }
        if (_depthRbo != 0) { _glDeleteRenderbuffers(1, new[] { _depthRbo }); _depthRbo = 0; }
        if (_glColorTex != 0) { glDeleteTextures(1, new[] { _glColorTex }); _glColorTex = 0; }
    }

    public void Dispose()
    {
        MakeCurrent();
        ReleaseShared();
        if (_glDxDevice != IntPtr.Zero) { _dxCloseDevice(_glDxDevice); _glDxDevice = IntPtr.Zero; }
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        if (_glrc != IntPtr.Zero) { wglDeleteContext(_glrc); _glrc = IntPtr.Zero; }
        if (_hdc != IntPtr.Zero) { ReleaseDC(_hwnd, _hdc); _hdc = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }
}
#endif
