/**
 * ConsoleExample — static map render to PNG using MapLibreNative.Maui directly.
 *
 * Creates a hidden Win32 window as an OpenGL context host, renders one frame of
 * a world-view map in static mode, reads back the pixels and saves them as a PNG.
 *
 * UseWPF=true in the .csproj gives access to WriteableBitmap / PngBitmapEncoder for
 * PNG encoding; no WPF window is ever shown.
 *
 * Usage:  dotnet run
 * Output: map_output.png written to the executable directory.
 */
using MapLibreNative.Maui;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConsoleExample;

class Program
{
    // ── Win32 ──────────────────────────────────────────────────────────────────
    [DllImport("kernel32")] static extern IntPtr GetModuleHandle(IntPtr _);
    [DllImport("user32", CharSet = CharSet.Ansi)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32", CharSet = CharSet.Ansi)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title,
        uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] static extern bool  DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32")] static extern int   ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32", CharSet = CharSet.Ansi)]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    // ── GDI / OpenGL ────────────────────────────────────────────────────────────
    [DllImport("gdi32")]    static extern int  ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32")]    static extern bool SetPixelFormat(IntPtr hdc, int fmt, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("opengl32")] static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32")] static extern bool   wglDeleteContext(IntPtr ctx);
    [DllImport("opengl32")] static extern bool   wglMakeCurrent(IntPtr hdc, IntPtr ctx);
    [DllImport("opengl32")] static extern IntPtr wglGetProcAddress(string proc);
    [DllImport("opengl32")] static extern void   glViewport(int x, int y, int w, int h);
    [DllImport("opengl32")] static extern void   glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32")] static extern void   glClear(uint mask);
    [DllImport("opengl32")] static extern void   glReadPixels(
        int x, int y, int w, int h, uint fmt, uint type, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr WglCreateContextAttribsARB(IntPtr hdc, IntPtr share, int[] attribs);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void GlBindFramebuffer(uint target, uint fbo);

    // ── Native structs ──────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct WNDCLASSEX
    {
        public uint    cbSize, style;
        public IntPtr  lpfnWndProc;
        public int     cbClsExtra, cbWndExtra;
        public IntPtr  hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string  lpszClassName;
        public IntPtr  hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint   dwFlags;
        public byte   iPixelType, cColorBits,
                      cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
                      cAlphaBits, cAlphaShift, cAccumBits,
                      cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
                      cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    // ── OpenGL constants ────────────────────────────────────────────────────────
    const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    const uint PFD_SUPPORT_OPENGL = 0x00000020;
    const uint PFD_DOUBLEBUFFER   = 0x00000001;
    const uint CS_OWNDC           = 0x0020;
    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    const uint GL_COLOR_BUFFER_BIT = 0x00004000;
    const uint GL_FRAMEBUFFER     = 0x8D40;
    const uint GL_RGBA            = 0x1908;
    const uint GL_UNSIGNED_BYTE   = 0x1401;
    // WGL_CONTEXT_MAJOR/MINOR_VERSION_ARB
    const int WGL_CTX_MAJOR = 0x2091;
    const int WGL_CTX_MINOR = 0x2092;

    const int Width  = 1024;
    const int Height = 768;

    [STAThread]
    static void Main()
    {
        Console.WriteLine("MapLibreNative.Maui — console static render example");
        Console.WriteLine($"Rendering {Width}×{Height} map centred on Seattle...");

        // ── Create a hidden window as an OpenGL context host ──────────────────
        var hInst = GetModuleHandle(IntPtr.Zero);
        WndProcDelegate wndProc = DefWindowProc; // keep delegate alive
        var wc = new WNDCLASSEX
        {
            cbSize        = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style         = CS_OWNDC,
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance     = hInst,
            lpszClassName = "mbgl_console_ctx",
        };
        RegisterClassEx(ref wc);

        var hwnd = CreateWindowEx(0, "mbgl_console_ctx", "offscreen",
            WS_OVERLAPPEDWINDOW, 0, 0, Width, Height,
            IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.Error.WriteLine("CreateWindowEx failed."); return; }

        var hDC = GetDC(hwnd);

        // ── Set pixel format ──────────────────────────────────────────────────
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize        = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion     = 1,
            dwFlags      = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            cColorBits   = 32,
            cDepthBits   = 24,
            cStencilBits = 8,
        };
        var fmt = ChoosePixelFormat(hDC, ref pfd);
        SetPixelFormat(hDC, fmt, ref pfd);

        // ── Create OpenGL 3.2 core context ────────────────────────────────────
        var hGLRC = wglCreateContext(hDC);
        wglMakeCurrent(hDC, hGLRC);

        var fnCreate = wglGetProcAddress("wglCreateContextAttribsARB");
        if (fnCreate != IntPtr.Zero)
        {
            var createFn = Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsARB>(fnCreate);
            int[] attribs = { WGL_CTX_MAJOR, 3, WGL_CTX_MINOR, 2, 0 };
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(hGLRC);
            hGLRC = createFn(hDC, IntPtr.Zero, attribs);
            wglMakeCurrent(hDC, hGLRC);
        }

        GlBindFramebuffer? glBindFramebuffer = null;
        var fnBind = wglGetProcAddress("glBindFramebuffer");
        if (fnBind != IntPtr.Zero)
            glBindFramebuffer = Marshal.GetDelegateForFunctionPointer<GlBindFramebuffer>(fnBind);

        // ── MapLibre setup ────────────────────────────────────────────────────
        bool renderNeeded = false;
        bool mapIdle      = false;
        string? failMsg   = null;

        using var runLoop  = new MbglRunLoop();
        using var frontend = new MbglFrontend(hDC, hGLRC, Width, Height, 1.0f,
            onRender: () => renderNeeded = true);
        using var map = new MbglMap(frontend, runLoop,
            observer: (evt, detail) =>
            {
                switch (evt)
                {
                    case "onDidFinishLoadingStyle":
                        Console.WriteLine("  Style loaded.");
                        break;
                    case "onDidBecomeIdle":
                        Console.WriteLine("  Map idle — all tiles ready.");
                        mapIdle = true;
                        break;
                    case "onDidFailLoadingMap":
                        failMsg = detail ?? "unknown error";
                        mapIdle = true; // unblock
                        break;
                }
            });

        map.SetSize(Width, Height);
        map.JumpTo(lat: 47.6062, lon: -122.3321, zoom: 9); // Seattle
        map.SetStyleUrl("https://demotiles.maplibre.org/style.json");

        // ── Pump run loop until the map reaches idle ──────────────────────────
        Console.WriteLine("Pumping run loop (max 30 s)...");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!mapIdle && DateTime.UtcNow < deadline)
        {
            runLoop.RunOnce();
            if (renderNeeded)
            {
                renderNeeded = false;
                wglMakeCurrent(hDC, hGLRC);
                glBindFramebuffer?.Invoke(GL_FRAMEBUFFER, 0);
                glViewport(0, 0, Width, Height);
                glClearColor(0.85f, 0.90f, 0.97f, 1f);
                glClear(GL_COLOR_BUFFER_BIT);
                frontend.Render();
            }
            Thread.Sleep(8);
        }

        if (failMsg != null)    { Console.Error.WriteLine($"Map load failed: {failMsg}"); }
        else if (!mapIdle)      { Console.Error.WriteLine("Timed out waiting for map idle."); }

        // ── Final render pass (flush any last-minute tiles) ───────────────────
        Console.WriteLine("Rendering final frame...");
        for (int pass = 0; pass < 5; pass++)
        {
            runLoop.RunOnce();
            if (renderNeeded) renderNeeded = false;
            Thread.Sleep(16);
        }

        wglMakeCurrent(hDC, hGLRC);
        glBindFramebuffer?.Invoke(GL_FRAMEBUFFER, 0);
        glViewport(0, 0, Width, Height);
        glClearColor(0.85f, 0.90f, 0.97f, 1f);
        glClear(GL_COLOR_BUFFER_BIT);
        frontend.Render();

        // ── Read pixels (RGBA, OpenGL y=0 is bottom-left) ────────────────────
        var pixels = new byte[Width * Height * 4];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            glReadPixels(0, 0, Width, Height, GL_RGBA, GL_UNSIGNED_BYTE,
                pin.AddrOfPinnedObject());
        }
        finally { pin.Free(); }

        // ── Flip vertically and convert RGBA → BGRA for WriteableBitmap ───────
        int stride  = Width * 4;
        var flipped = new byte[pixels.Length];
        for (int row = 0; row < Height; row++)
        {
            int srcRow = Height - 1 - row;
            for (int col = 0; col < Width; col++)
            {
                int src = srcRow * stride + col * 4;
                int dst = row    * stride + col * 4;
                flipped[dst + 0] = pixels[src + 2]; // B ← R
                flipped[dst + 1] = pixels[src + 1]; // G
                flipped[dst + 2] = pixels[src + 0]; // R ← B
                flipped[dst + 3] = pixels[src + 3]; // A
            }
        }

        // ── Encode and save as PNG ─────────────────────────────────────────────
        string outPath = Path.Combine(AppContext.BaseDirectory, "map_output.png");
        var bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), flipped, stride, 0);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(outPath);
        encoder.Save(fs);

        Console.WriteLine($"Saved: {outPath}");

        // ── Cleanup ────────────────────────────────────────────────────────────
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        wglDeleteContext(hGLRC);
        ReleaseDC(hwnd, hDC);
        DestroyWindow(hwnd);
        GC.KeepAlive(wndProc); // prevent delegate collection before DestroyWindow
    }
}
