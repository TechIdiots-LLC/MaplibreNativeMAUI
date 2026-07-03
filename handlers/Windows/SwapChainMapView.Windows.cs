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
 * STATUS — starting point:
 *   ✓ SwapChainPanel + D3D11 device + composition swap chain wired via
 *     ISwapChainPanelNative, with a CompositionTarget.Rendering present loop
 *     (clears the panel to prove the in-tree surface).
 *   ☐ TODO: bridge mbgl's GL output into the swap-chain back buffer. Mirror the
 *     WPF GlDxInteropContext: register the DXGI back-buffer D3D11 texture with the
 *     GL context via WGL_NV_DX_interop2 and have mln-cabi's frontend render into an
 *     FBO backed by it (or run mbgl's GL through ANGLE targeting this swap chain).
 *   ☐ TODO: replace MapLibreMapController.Windows' WS_POPUP surface + panels with
 *     this view behind a renderer flag; port nav/GPS/attribution to XAML children.
 *
 * Not yet referenced by the handler — the WS_POPUP path remains the default.
 */
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace MapLibreNative.Maui.Handlers.WinUI;

/// <summary>
/// Hosts a <see cref="SwapChainPanel"/> backed by a D3D11 composition swap chain — the in-tree
/// surface the MAUI Windows map should render into instead of a floating WS_POPUP window.
/// </summary>
public sealed class SwapChainMapView : IDisposable
{
    [ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig] int SetSwapChain(IntPtr swapChain);
    }

    /// <summary>The XAML element to add to the map's visual tree.</summary>
    public SwapChainPanel Panel { get; } = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private int _width = 1, _height = 1;
    private bool _rendering;

    public SwapChainMapView()
    {
        Panel.SizeChanged += (_, e) => Resize((int)e.NewSize.Width, (int)e.NewSize.Height);
        Panel.Loaded += (_, _) => Start();
        Panel.Unloaded += (_, _) => Stop();
    }

    private void Start()
    {
        if (_device != null) return;

        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            null, out _device, out _context).CheckError();

        _width = Math.Max(1, (int)(Panel.ActualWidth * Panel.CompositionScaleX));
        _height = Math.Max(1, (int)(Panel.ActualHeight * Panel.CompositionScaleY));

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

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

        // Bind the swap chain to the XAML panel.
        var native = Panel.As<ISwapChainPanelNative>();
        Marshal.ThrowExceptionForHR(native.SetSwapChain(_swapChain.NativePointer));

        CreateRenderTarget();

        CompositionTarget.Rendering += OnRendering;
        _rendering = true;
    }

    private void CreateRenderTarget()
    {
        _rtv?.Dispose();
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(backBuffer);
    }

    private void Resize(int width, int height)
    {
        if (_swapChain == null) return;
        width = Math.Max(1, (int)(width * Panel.CompositionScaleX));
        height = Math.Max(1, (int)(height * Panel.CompositionScaleY));
        if (width == _width && height == _height) return;
        _width = width; _height = height;

        _rtv?.Dispose(); _rtv = null;
        _swapChain.ResizeBuffers(2, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        CreateRenderTarget();
    }

    private void OnRendering(object? sender, object e)
    {
        if (_swapChain == null || _context == null || _rtv == null) return;

        // TODO: replace this clear with the mbgl GL→DXGI bridge (render MapLibre into
        // the back buffer via WGL_NV_DX_interop / ANGLE). For now, clear to prove the
        // in-tree surface presents correctly.
        _context.OMSetRenderTargets(_rtv);
        _context.ClearRenderTargetView(_rtv, new Vortice.Mathematics.Color4(0.85f, 0.90f, 0.97f, 1f));
        _swapChain.Present(1, PresentFlags.None);
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
        _rtv?.Dispose(); _rtv = null;
        _swapChain?.Dispose(); _swapChain = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }
}
#endif
