#if WINDOWS
/**
 * MapImageView.Windows.cs — in-tree map view for the MAUI Windows renderer.
 *
 * MapLibre (mln-cabi GL) renders into FBO 0 of a hidden off-screen window
 * (HiddenWglContext); after each frame glReadPixels captures the result and writes it
 * directly into a WinUI WriteableBitmap.  The bitmap is displayed in an ordinary XAML
 * Image element so the map is real in-tree content: correct z-order, clipping, pointer
 * input — no floating window, no airspace.
 */
using System.Runtime.InteropServices;
using MapLibreNative.Maui;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;
using WUX  = Microsoft.UI.Xaml;
using WUXC = Microsoft.UI.Xaml.Controls;
using WUXI = Microsoft.UI.Xaml.Input;
using WUXM = Microsoft.UI.Xaml.Media;

namespace MapLibreNative.Maui.Handlers.WinUI;

/// <summary>
/// Hosts a <see cref="WUXC.Image"/> backed by a <see cref="WriteableBitmap"/> that is updated
/// each frame via <c>glReadPixels</c> — the in-tree map surface used by the MAUI Windows handler.
/// </summary>
public sealed class MapImageView : IDisposable
{
    // Access WinRT IBufferByteAccess to write directly into the WriteableBitmap pixel buffer.
    [ComImport, Guid("905a0fef-bc53-11df-8c49-001e4fc686da"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBufferByteAccess { [PreserveSig] int Buffer(out IntPtr value); }

    [DllImport("opengl32.dll")] private static extern void glViewport(int x, int y, int w, int h);

    /// <summary>The XAML element to add to the map's visual tree (map surface + on-map controls).</summary>
    public WUXC.Grid View { get; } = new();

    /// <summary>Style URL or inline style JSON. Applied when the map is created.</summary>
    public string StyleUrl { get; set; } = "https://demotiles.maplibre.org/style.json";

    /// <summary>The underlying map, once created (on panel load). Drive camera/sources/layers through this.</summary>
    public MbglMap? Map => _map;

    /// <summary>The current style, once loaded.</summary>
    public MbglStyle? Style => _style;

    public event EventHandler? MapReady;
    public event EventHandler? StyleLoaded;
    public event EventHandler? DidBecomeIdle;
    public event EventHandler? CameraIdle;
    /// <summary>Raised on a tap without pan: (latitude, longitude, physicalX, physicalY).</summary>
    public event EventHandler<(double Lat, double Lon, double X, double Y)>? MapClicked;

    // The map surface — a plain XAML Image displaying the WriteableBitmap.
    private readonly WUXC.Image _mapImage = new()
    {
        HorizontalAlignment = WUX.HorizontalAlignment.Stretch,
        VerticalAlignment   = WUX.VerticalAlignment.Stretch,
        Stretch             = WUXM.Stretch.Fill,
        // GL renders bottom-left origin; WinUI is top-left — flip vertically.
        RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        RenderTransform = new WUXM.ScaleTransform { ScaleX = 1, ScaleY = -1 },
    };
    private WriteableBitmap? _bitmap;

    private HiddenWglContext? _interop;

    // mbgl allows exactly one RunLoop per thread. Every MapImageView on the UI thread shares a
    // single app-lifetime RunLoop; constructing a second one on the same thread aborts natively.
    // This is what crashed the app when navigating back to a map tab — MAUI builds the new map
    // (and its RunLoop) before the previous handler/map is torn down, so two RunLoops briefly
    // co-existed on the UI thread. Sharing one avoids that entirely.
    [ThreadStatic] private static MbglRunLoop? _sharedRunLoop;
    private MbglRunLoop?   _runLoop;
    private MbglFrontend?  _frontend;
    private MbglMap?       _map;
    private MbglStyle?     _style;

    private int   _width = 1, _height = 1;
    private float _dpi = 1f;
    private bool  _renderNeedsUpdate = true, _rendering, _isDragging, _disposed;
    private Windows.Foundation.Point _lastPos;

    private static int _diagCounter;
    private readonly int _diagId = System.Threading.Interlocked.Increment(ref _diagCounter);
    private static readonly string _diagPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maplibre_maui_diag.log");
    private void MDiag(string msg)
    {
        try { System.IO.File.AppendAllText(_diagPath,
            $"{DateTime.Now:HH:mm:ss.fff} [miv#{_diagId}] {msg}\r\n"); }
        catch { /* ignore */ }
    }

    public MapImageView()
    {
        View.Children.Add(_mapImage);
        // Nav / GPS / attribution controls are added by MapLibreMapController.Windows.

        View.Loaded        += (_, _) => Start();
        View.Unloaded      += (_, _) => Stop();
        View.SizeChanged   += (_, e) => Resize((int)e.NewSize.Width, (int)e.NewSize.Height);

        View.PointerPressed       += OnPointerPressed;
        View.PointerMoved         += OnPointerMoved;
        View.PointerReleased      += OnPointerReleased;
        View.PointerCanceled      += OnPointerCanceled;
        View.PointerWheelChanged  += OnPointerWheel;
        View.Tapped               += OnTapped;
        View.DoubleTapped         += OnDoubleTapped;
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    private void Start()
    {
        // Once disposed (controller teardown on tab switch), a stale View.Loaded must NOT
        // resurrect this instance: the owning controller has already nulled its _mapView, so
        // re-firing MapReady would dereference null. The new tab visit builds a fresh MapImageView.
        if (_disposed || _interop != null) return;

        _dpi    = (float)View.XamlRoot.RasterizationScale;
        _width  = Math.Max(1, (int)(View.ActualWidth  * _dpi));
        _height = Math.Max(1, (int)(View.ActualHeight * _dpi));
        MDiag($"Start dpi={_dpi} size={_width}x{_height} actual={View.ActualWidth}x{View.ActualHeight} style={StyleUrl}");

        _interop = new HiddenWglContext();
        _interop.Initialize();
        _interop.Resize(_width, _height);
        CreateBitmap(_width, _height);

        _runLoop  = _sharedRunLoop ??= new MbglRunLoop();
        _frontend = new MbglFrontend(_interop.Hdc, _interop.GlContext, _width, _height, _dpi,
            () => _renderNeedsUpdate = true);
        // Persistent tile/resource cache (mbgl's default is :memory:). Shares
        // MbglCache.DefaultPath with MbglOfflineManager so offline regions
        // downloaded by the manager are served to the map.
        _map = new MbglMap(_frontend, _runLoop, cachePath: MbglCache.DefaultPath,
                           pixelRatio: _dpi, observer: OnMapObserverEvent);
        _map.SetSize(_width, _height);

        var url = StyleUrl;
        if (!string.IsNullOrEmpty(url))
        {
            if (url.TrimStart().StartsWith('{')) _map.SetStyleJson(url);
            else _map.SetStyleUrl(url);
        }

        WUXM.CompositionTarget.Rendering += OnRendering;
        _rendering = true;
        MapReady?.Invoke(this, System.EventArgs.Empty);
    }

    private void CreateBitmap(int w, int h)
    {
        _bitmap = new WriteableBitmap(w, h);
        _mapImage.Source = _bitmap;
    }

    private void Resize(int dipWidth, int dipHeight)
    {
        if (_interop == null || _frontend == null || _map == null) return;
        float scale = (float)(View.XamlRoot?.RasterizationScale ?? _dpi);
        int w = Math.Max(1, (int)(dipWidth  * scale));
        int h = Math.Max(1, (int)(dipHeight * scale));
        if (w == _width && h == _height) return;
        _width = w; _height = h; _dpi = scale;

        _interop.Resize(w, h);
        CreateBitmap(w, h);
        _frontend.SetSize(w, h);
        _map.SetSize(w, h);
        _renderNeedsUpdate = true;
    }

    private void OnRendering(object? sender, object e)
    {
        _runLoop?.RunOnce();
        if (!_renderNeedsUpdate || _interop == null || _frontend == null || _bitmap == null)
            return;
        _renderNeedsUpdate = false;

        _interop.MakeCurrent();
        glViewport(0, 0, _width, _height);
        try { _frontend.Render(); } catch { return; }

        // Write pixels directly into the WriteableBitmap's backing store via IBufferByteAccess.
        // NOTE: a plain (IBufferByteAccess)(object) cast throws InvalidCastException under CsWinRT
        // (WinUI 3) — the projected IBuffer must be QueryInterface'd via WinRT's .As<T>().
        var ibb = _bitmap.PixelBuffer.As<IBufferByteAccess>();
        ibb.Buffer(out IntPtr ptr);
        _interop.ReadPixels(ptr);
        _bitmap.Invalidate();
    }

    // ── Camera helpers ─────────────────────────────────────────────────────────

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

    // ── Input (XAML routed events; DIP -> physical px) ──────────────────────────

    private (double X, double Y) Phys(Windows.Foundation.Point dip)
        => (dip.X * _dpi, dip.Y * _dpi);

    // Only the map image should drive pan/zoom/click. The nav / GPS / attribution overlays are
    // sibling children on top; a press on one of them must reach that control instead of being
    // captured here for a map pan.
    private bool IsOnMapSurface(object? src)
        => ReferenceEquals(src, _mapImage) || ReferenceEquals(src, View);

    private void OnPointerPressed(object sender, WUXI.PointerRoutedEventArgs e)
    {
        if (_map == null || !IsOnMapSurface(e.OriginalSource)) return;
        View.CapturePointer(e.Pointer);
        _lastPos = e.GetCurrentPoint(View).Position;
        _isDragging = true;
        var p = Phys(_lastPos);
        _map.OnPanStart(p.X, p.Y);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, WUXI.PointerRoutedEventArgs e)
    {
        if (_map == null || !_isDragging) return;
        var pos = e.GetCurrentPoint(View).Position;
        var d = Phys(new Windows.Foundation.Point(pos.X - _lastPos.X, pos.Y - _lastPos.Y));
        _lastPos = pos;
        _map.OnPanMove(d.X, d.Y);
        _renderNeedsUpdate = true;
    }

    private void OnPointerReleased(object sender, WUXI.PointerRoutedEventArgs e)
    {
        if (_map == null || !_isDragging) return;
        _isDragging = false;
        View.ReleasePointerCapture(e.Pointer);
        _map.OnPanEnd();
        _map.TriggerRepaint();
        _renderNeedsUpdate = true;
    }

    private void OnPointerCanceled(object sender, WUXI.PointerRoutedEventArgs e)
    {
        if (_map == null || !_isDragging) return;
        _isDragging = false;
        _map.OnPanEnd();
    }

    private void OnPointerWheel(object sender, WUXI.PointerRoutedEventArgs e)
    {
        if (_map == null || !IsOnMapSurface(e.OriginalSource)) return;
        var pt = e.GetCurrentPoint(View);
        var p = Phys(pt.Position);
        _map.OnScroll(pt.Properties.MouseWheelDelta / 120.0, p.X, p.Y);
        _renderNeedsUpdate = true;
        e.Handled = true;
    }

    private void OnTapped(object sender, WUXI.TappedRoutedEventArgs e)
    {
        if (_map == null || !IsOnMapSurface(e.OriginalSource)) return;
        var p = Phys(e.GetPosition(View));
        var ll = _map.LatLngForPixel(p.X, p.Y);
        MapClicked?.Invoke(this, (ll.Lat, ll.Lon, p.X, p.Y));
    }

    private void OnDoubleTapped(object sender, WUXI.DoubleTappedRoutedEventArgs e)
    {
        if (_map == null || !IsOnMapSurface(e.OriginalSource)) return;
        var p = Phys(e.GetPosition(View));
        _map.OnDoubleTap(p.X, p.Y);
        _renderNeedsUpdate = true;
        e.Handled = true;
    }

    // ── mbgl observer ─────────────────────────────────────────────────────────

    private void OnMapObserverEvent(string eventName, string? detail)
    {
        switch (eventName)
        {
            case "onDidFinishLoadingStyle":
                _style = _map?.GetStyle();
                _renderNeedsUpdate = true;
                MDiag("onDidFinishLoadingStyle");
                StyleLoaded?.Invoke(this, System.EventArgs.Empty);
                break;
            case "onDidBecomeIdle":
                MDiag("onDidBecomeIdle");
                DidBecomeIdle?.Invoke(this, System.EventArgs.Empty);
                break;
            case "onCameraDidChange":
                CameraIdle?.Invoke(this, System.EventArgs.Empty);
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
            WUXM.CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    public void Dispose()
    {
        MDiag("Dispose");
        _disposed = true;
        Stop();
        _map?.Dispose();      _map      = null;
        _frontend?.Dispose(); _frontend = null;
        // _runLoop is the shared per-thread RunLoop (app-lifetime) — never dispose it here.
        _runLoop  = null;
        _interop?.Dispose();  _interop  = null;
        _style = null;
    }
}
#endif
