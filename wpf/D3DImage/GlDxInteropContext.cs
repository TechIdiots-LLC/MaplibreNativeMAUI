/**
 * GlDxInteropContext.cs — OpenGL ⇄ Direct3D9 shared-surface bridge for the
 * airspace-free WPF map renderer (MlnMapImage).
 *
 * MapLibre renders (via mln-cabi's GL frontend) into an FBO whose colour
 * attachment is a Direct3D9Ex render-target texture shared with the GL context
 * through the WGL_NV_DX_interop2 extension. That same D3D surface is handed to a
 * WPF D3DImage, so the map becomes an ordinary WPF visual — no child HWND, no
 * airspace, and controls can be real WPF children.
 *
 * Recipe follows OpenTK's GLWpfControl. Requires a GPU/driver that exports
 * WGL_NV_DX_interop2 (all modern NVIDIA/AMD/Intel desktop drivers).
 */
using System.Runtime.InteropServices;
using Vortice.Direct3D9;

namespace MapLibreNative.Maui.WPF.D3DImageRenderer;

/// <summary>
/// Owns the hidden WGL context, the shared D3D9Ex render-target surface, and the GL FBO
/// that MapLibre renders into. Present the result through WPF by handing
/// <see cref="D3DSurfacePointer"/> to <c>D3DImage.SetBackBuffer</c>.
/// </summary>
internal sealed class GlDxInteropContext : IDisposable
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
    private const uint WS_OVERLAPPED = 0x00000000;

    // GL enums / bindable functions (all core in GL 3.0+, resolved via wglGetProcAddress)
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
    private delegate bool DxSetShareHandle(IntPtr dxObject, IntPtr shareHandle);
    private delegate IntPtr DxRegisterObject(IntPtr hDevice, IntPtr dxObject, uint name, uint type, uint access);
    private delegate bool DxUnregisterObject(IntPtr hDevice, IntPtr hObject);
    private delegate bool DxLock(IntPtr hDevice, int count, IntPtr[] hObjects);
    private delegate bool DxUnlock(IntPtr hDevice, int count, IntPtr[] hObjects);

    private DxOpenDevice _dxOpenDevice = null!;
    private DxCloseDevice _dxCloseDevice = null!;
    private DxSetShareHandle _dxSetShareHandle = null!;
    private DxRegisterObject _dxRegisterObject = null!;
    private DxUnregisterObject _dxUnregisterObject = null!;
    private DxLock _dxLock = null!;
    private DxUnlock _dxUnlock = null!;

    private const uint WGL_ACCESS_READ_WRITE_NV = 0x0000;

    // ── State ─────────────────────────────────────────────────────────────────

    private static bool _classRegistered;
    private static WndProcDelegate? _wndProcKeepAlive;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    private const string WndClass = "MlnGlDxHidden";

    private IntPtr _hwnd, _hdc, _glrc;

    private IDirect3D9Ex _d3d = null!;
    private IDirect3DDevice9Ex _device = null!;
    private IDirect3DTexture9? _texture;
    private IDirect3DSurface9? _surface;

    private IntPtr _glDxDevice;      // wglDXOpenDeviceNV handle
    private IntPtr _dxRegistered;    // wglDXRegisterObjectNV handle for the shared texture
    private uint _glColorTex, _fbo, _depthRbo;

    /// <summary>Width in physical pixels of the current shared surface.</summary>
    public int Width { get; private set; }
    /// <summary>Height in physical pixels of the current shared surface.</summary>
    public int Height { get; private set; }

    /// <summary>The GL framebuffer object MapLibre must render into (bind before <c>Render</c>).</summary>
    public uint Framebuffer => _fbo;

    /// <summary>Native <c>IDirect3DSurface9*</c> to hand to <c>D3DImage.SetBackBuffer</c>. Zero until sized.</summary>
    public IntPtr D3DSurfacePointer => _surface?.NativePointer ?? IntPtr.Zero;

    /// <summary>The hidden window's device context (pass as the mbgl frontend surface handle).</summary>
    public IntPtr Hdc => _hdc;
    /// <summary>The WGL render context (pass as the mbgl frontend GL context).</summary>
    public IntPtr GlContext => _glrc;

    // ── Init ──────────────────────────────────────────────────────────────────

    public void Initialize()
    {
        CreateHiddenGlContext();
        LoadGlFunctions();
        LoadNvDxInterop();
        CreateD3DDevice();
        _glDxDevice = _dxOpenDevice(_device.NativePointer);
        if (_glDxDevice == IntPtr.Zero)
            throw new InvalidOperationException("wglDXOpenDeviceNV failed — WGL_NV_DX_interop unavailable on this GPU/driver.");
    }

    public void MakeCurrent() => wglMakeCurrent(_hdc, _glrc);

    private void CreateHiddenGlContext()
    {
        EnsureClass();
        _hwnd = CreateWindowEx(0, WndClass, "", WS_OVERLAPPED, 0, 0, 1, 1,
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
        _dxSetShareHandle = Load<DxSetShareHandle>("wglDXSetResourceShareHandleNV");
        _dxRegisterObject = Load<DxRegisterObject>("wglDXRegisterObjectNV");
        _dxUnregisterObject = Load<DxUnregisterObject>("wglDXUnregisterObjectNV");
        _dxLock = Load<DxLock>("wglDXLockObjectsNV");
        _dxUnlock = Load<DxUnlock>("wglDXUnlockObjectsNV");
    }

    private void CreateD3DDevice()
    {
        D3D9.Direct3DCreate9Ex(out IDirect3D9Ex d3d).CheckError();
        _d3d = d3d;

        var pp = new PresentParameters
        {
            Windowed = true,
            SwapEffect = SwapEffect.Discard,
            DeviceWindowHandle = _hwnd,
            PresentationInterval = PresentInterval.Immediate,
            BackBufferFormat = Format.Unknown,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
        };
        _device = _d3d.CreateDeviceEx(0, DeviceType.Hardware, _hwnd,
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
            pp);
    }

    // ── Sizing / shared surface ───────────────────────────────────────────────

    /// <summary>(Re)creates the shared surface and FBO at the given physical pixel size.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height && _surface != null) return;

        MakeCurrent();
        ReleaseSurface();

        Width = width;
        Height = height;

        // 1. Shared D3D9 render-target texture (+ share handle for NV interop).
        IntPtr shareHandle = IntPtr.Zero;
        _texture = _device.CreateTexture((uint)width, (uint)height, 1, Usage.RenderTarget, Format.A8R8G8B8,
            Pool.Default, ref shareHandle);
        _surface = _texture.GetSurfaceLevel(0);
        _dxSetShareHandle(_texture.NativePointer, shareHandle);

        // 2. GL colour texture registered against the D3D texture.
        var tex = new uint[1];
        glGenTextures(1, tex);
        _glColorTex = tex[0];
        _dxRegistered = _dxRegisterObject(_glDxDevice, _texture.NativePointer, _glColorTex,
            GL_TEXTURE_2D, WGL_ACCESS_READ_WRITE_NV);
        if (_dxRegistered == IntPtr.Zero)
            throw new InvalidOperationException("wglDXRegisterObjectNV failed for the shared texture.");

        // 3. FBO: shared colour texture + depth/stencil renderbuffer.
        var fb = new uint[1]; _glGenFramebuffers(1, fb); _fbo = fb[0];
        var rb = new uint[1]; _glGenRenderbuffers(1, rb); _depthRbo = rb[0];

        Lock();
        _glBindFramebuffer(GL_FRAMEBUFFER, _fbo);
        _glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _glColorTex, 0);
        _glBindRenderbuffer(GL_RENDERBUFFER, _depthRbo);
        _glRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH24_STENCIL8, width, height);
        _glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GL_RENDERBUFFER, _depthRbo);
        uint status = _glCheckFramebufferStatus(GL_FRAMEBUFFER);
        Unlock();
        if (status != GL_FRAMEBUFFER_COMPLETE)
            throw new InvalidOperationException($"Interop framebuffer incomplete (status 0x{status:X}).");
    }

    /// <summary>Binds the interop FBO as the current draw target. Call between Lock and Render.</summary>
    public void BindFramebuffer() => _glBindFramebuffer(GL_FRAMEBUFFER, _fbo);

    /// <summary>Locks the shared surface for GL rendering. Bracket <c>Render</c> with Lock/Unlock.</summary>
    public void Lock()
    {
        if (_dxRegistered != IntPtr.Zero)
            _dxLock(_glDxDevice, 1, new[] { _dxRegistered });
    }

    /// <summary>Unlocks the shared surface so WPF/D3D can read it.</summary>
    public void Unlock()
    {
        if (_dxRegistered != IntPtr.Zero)
            _dxUnlock(_glDxDevice, 1, new[] { _dxRegistered });
    }

    private void ReleaseSurface()
    {
        if (_dxRegistered != IntPtr.Zero)
        {
            _dxUnregisterObject(_glDxDevice, _dxRegistered);
            _dxRegistered = IntPtr.Zero;
        }
        if (_fbo != 0) { _glDeleteFramebuffers(1, new[] { _fbo }); _fbo = 0; }
        if (_depthRbo != 0) { _glDeleteRenderbuffers(1, new[] { _depthRbo }); _depthRbo = 0; }
        if (_glColorTex != 0) { glDeleteTextures(1, new[] { _glColorTex }); _glColorTex = 0; }
        _surface?.Dispose(); _surface = null;
        _texture?.Dispose(); _texture = null;
    }

    public void Dispose()
    {
        MakeCurrent();
        ReleaseSurface();
        if (_glDxDevice != IntPtr.Zero) { _dxCloseDevice(_glDxDevice); _glDxDevice = IntPtr.Zero; }
        _device?.Dispose();
        _d3d?.Dispose();
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        if (_glrc != IntPtr.Zero) { wglDeleteContext(_glrc); _glrc = IntPtr.Zero; }
        if (_hdc != IntPtr.Zero) { ReleaseDC(_hwnd, _hdc); _hdc = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }
}
