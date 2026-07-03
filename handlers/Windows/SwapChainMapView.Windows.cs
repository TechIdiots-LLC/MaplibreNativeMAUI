#if WINDOWS
/**
 * SwapChainMapView.Windows.cs — in-tree DXGI surface for the MAUI Windows map.
 *
 * The airspace-free replacement for the current WS_POPUP GL window
 * (MapLibreMapController.Windows). A SwapChainPanel is a first-class XAML element
 * that owns a DXGI composition swap chain, so the map becomes real in-tree content
 * and on-map controls can be ordinary XAML children (no owned windows, no
 * per-tick realignment, pointer input flows through XAML natively).
 *
 * Rendering path:
 *   MapLibre (mln-cabi GL) → FBO backed by a shared D3D11 offscreen texture
 *     (via GlDxgiInteropContext / WGL_NV_DX_interop2)
 *   → CopyResource into the composition swap chain's back buffer → Present.
 * A stable offscreen texture is used because composition swap chains require the
 * flip model, whose back buffer rotates each Present. GL renders bottom-up, so the
 * SwapChainPanel is vertically flipped at the element level (as in the WPF path).
 *
 * STATUS — GL→DXGI bridge implemented; needs on-GPU validation of the interop path
 * and of the element-level flip. Not yet wired into MapLibreMapController.Windows —
 * the WS_POPUP path remains the default. Next: swap this in behind a renderer flag
 * and port the nav/GPS/attribution panels to XAML children of View.
 */
using System.Runtime.InteropServices;
using MapLibreNative.Maui;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace MapLibreNative.Maui.Handlers.WinUI;

/// <summary>
/// Hosts a <see cref="SwapChainPanel"/> backed by a D3D11 composition swap chain into which
/// MapLibre is rendered through GL↔D3D11 interop — the in-tree surface the MAUI Windows map
/// should use instead of a floating WS_POPUP window.
/// </summary>
public sealed class SwapChainMapView : IDisposable
{
    [ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig] int SetSwapChain(IntPtr swapChain);
    }

    [DllImport("opengl32.dll")] private static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll")] private static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll")] private static extern void glClear(uint mask);
    private const uint GL_COLOR_BUFFER_BIT = 0x00004000;
    private const uint GL_DEPTH_BUFFER_BIT = 0x00000100;
    private const uint GL_STENCIL_BUFFER_BIT = 0x00000400;

    /// <summary>The XAML element to add to the map's visual tree. Add on-map controls as its children.</summary>
    public Microsoft.UI.Xaml.Controls.Grid View { get; } = new();

    /// <summary>Style URL or inline style JSON. Applied when the map is created, or live if already running.</summary>
    public string StyleUrl { get; set; } = "https://demotiles.maplibre.org/style.json";

    private readonly Microsoft.UI.Xaml.Controls.SwapChainPanel _panel = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _offscreen;

    private GlDxgiInteropContext? _interop;
    private MbglRunLoop? _runLoop;
    private MbglFrontend? _frontend;
    private MbglMap? _map;
    private MbglStyle? _style;

    private int _width = 1, _height = 1;
    private float _dpi = 1f;
    private bool _renderNeedsUpdate = true, _rendering;

    public SwapChainMapView()
    {
        // GL renders bottom-left origin; flip the panel so the map is upright. Input (when
        // wired) is handled on the un-flipped parent View, so map coordinates stay correct.
        _panel.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _panel.RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = -1 };
        View.Children.Add(_panel);

        _panel.Loaded += (_, _) => Start();
        _panel.Unloaded += (_, _) => Stop();
        _panel.SizeChanged += (_, e) => Resize((int)e.NewSize.Width, (int)e.NewSize.Height);
    }

    private void Start()
    {
        if (_device != null) return;

        _dpi = (float)_panel.CompositionScaleX;
        _width = Math.Max(1, (int)(_panel.ActualWidth * _panel.CompositionScaleX));
        _height = Math.Max(1, (int)(_panel.ActualHeight * _panel.CompositionScaleY));

        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            null, out _device, out _context).CheckError();

        using (var dxgiDevice = _device!.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
            };
            _swapChain = factory.CreateSwapChainForComposition(_device, desc);
        }

        var native = _panel.As<ISwapChainPanelNative>();
        Marshal.ThrowExceptionForHR(native.SetSwapChain(_swapChain!.NativePointer));

        _interop = new GlDxgiInteropContext();
        _interop.Initialize(_device.NativePointer);
        CreateOffscreen(_width, _height);

        _runLoop = new MbglRunLoop();
        _frontend = new MbglFrontend(_interop.Hdc, _interop.GlContext, _width, _height, _dpi,
            () => _renderNeedsUpdate = true);
        _map = new MbglMap(_frontend, _runLoop, pixelRatio: _dpi, observer: OnMapObserverEvent);
        _map.SetSize(_width, _height);

        var url = StyleUrl;
        if (!string.IsNullOrEmpty(url))
        {
            if (url.TrimStart().StartsWith('{')) _map.SetStyleJson(url);
            else _map.SetStyleUrl(url);
        }

        CompositionTarget.Rendering += OnRendering;
        _rendering = true;
    }

    private void CreateOffscreen(int width, int height)
    {
        _offscreen?.Dispose();
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _offscreen = _device!.CreateTexture2D(desc);
        _interop!.SetSharedTexture(_offscreen.NativePointer, width, height);
    }

    private void Resize(int dipWidth, int dipHeight)
    {
        if (_swapChain == null || _device == null || _interop == null) return;
        int w = Math.Max(1, (int)(dipWidth * _panel.CompositionScaleX));
        int h = Math.Max(1, (int)(dipHeight * _panel.CompositionScaleY));
        if (w == _width && h == _height) return;
        _width = w; _height = h;
        _dpi = (float)_panel.CompositionScaleX;

        _swapChain.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        CreateOffscreen(w, h);
        _frontend?.SetSize(w, h);
        _map?.SetSize(w, h);
        _renderNeedsUpdate = true;
    }

    private void OnRendering(object? sender, object e)
    {
        _runLoop?.RunOnce();
        if (!_renderNeedsUpdate || _interop == null || _frontend == null || _swapChain == null || _context == null || _offscreen == null)
            return;
        _renderNeedsUpdate = false;

        _interop.MakeCurrent();
        _interop.Lock();
        _interop.BindFramebuffer();
        glViewport(0, 0, _width, _height);
        glClearColor(0.85f, 0.90f, 0.97f, 1f);
        glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT | GL_STENCIL_BUFFER_BIT);
        try { _frontend.Render(); } catch { /* swallow per-frame render faults */ }
        _interop.Unlock();

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _context.CopyResource(backBuffer, _offscreen);
        _swapChain.Present(1, PresentFlags.None);
    }

    private void OnMapObserverEvent(string eventName, string? detail)
    {
        switch (eventName)
        {
            case "onDidFinishLoadingStyle":
                _style = _map?.GetStyle();
                _renderNeedsUpdate = true;
                break;
            case "onDidFinishRenderingFramePlacementChanged":
                _map?.TriggerRepaint();
                break;
        }
    }

    private void Stop()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    public void Dispose()
    {
        Stop();
        _map?.Dispose(); _map = null;
        _frontend?.Dispose(); _frontend = null;
        _runLoop?.Dispose(); _runLoop = null;
        _interop?.Dispose(); _interop = null;
        _offscreen?.Dispose(); _offscreen = null;
        _swapChain?.Dispose(); _swapChain = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }
}
#endif
