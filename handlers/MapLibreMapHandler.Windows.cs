#if WINDOWS
using Microsoft.Maui.Handlers;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using Windows.System;

namespace MapLibreNative.Maui.Handlers;

public partial class MapLibreMapHandler : ViewHandler<MapLibreMap, Microsoft.UI.Xaml.Controls.Grid>
{
    private MapLibreMapController _controller = null!;
    private string _styleUrl = string.Empty;
    private Microsoft.UI.Xaml.Window? _hostWindow;

    // Input tracking
    private bool   _isDragging;
    private double _lastPointerX;
    private double _lastPointerY;

    public IMapLibreMapController Controller => _controller;

    public MapLibreMapHandler() : base(PropertyMapper) { }

    protected override Microsoft.UI.Xaml.Controls.Grid CreatePlatformView()
    {
        var window = MauiContext?.Services?.GetService<Microsoft.UI.Xaml.Window>();

        // GetDpiForWindow returns RasterizationScale (e.g. 1.0, 1.25, 1.5, 2.0).
        // Fallback must be 1.0f (100% scale), NOT 96.0f — that is the raw DPI number
        // and would set _pixelRatio=96, making every physical dimension 96× too large.
        float dpi  = window != null ? GetDpiForWindow(window) : 1.0f;
        var   hwnd = WindowNative.GetWindowHandle(window);

        _controller = MapLibreMapFactory.Create(hwnd, dpi, new Dictionary<string, object>
        {
            ["styleString"] = _styleUrl
        });

        _controller.OnMapReadyReceived               += VirtualView.OnMapReady;
        _controller.OnStyleLoadedReceived            += VirtualView.OnStyleLoaded;
        _controller.OnDidBecomeIdleReceived          += VirtualView.OnDidBecomeIdle;
        _controller.OnCameraMoveStartedReceived      += VirtualView.OnCameraMoveStarted;
        _controller.OnCameraMoveReceived             += VirtualView.OnCameraMove;
        _controller.OnCameraIdleReceived             += VirtualView.OnCameraIdle;
        _controller.OnCameraTrackingChangedReceived  += VirtualView.OnCameraTrackingChanged;
        _controller.OnCameraTrackingDismissedReceived += VirtualView.OnCameraTrackingDismissed;
        _controller.OnMapClickReceived               += VirtualView.OnMapClick;
        _controller.OnMapLongClickReceived           += VirtualView.OnMapLongClick;
        _controller.OnUserLocationUpdateReceived     += VirtualView.OnUserLocationUpdate;

        _controller.Init();

        // On window maximize/restore MAUI does not re-arrange the page on its own,
        // so the map View keeps its old (too-short) height and the nav panel stays
        // hidden until a tab switch. The host Window.SizeChanged DOES fire — use it
        // to force a re-layout so View.SizeChanged runs the real resize.
        _hostWindow = window;
        if (_hostWindow != null)
            _hostWindow.SizeChanged += OnHostWindowSizeChanged;
        DiagLog($"CreatePlatformView window={(window != null)} dpi={dpi}");

        var view = _controller.View;
        AttachInputEvents(view);
        return view;
    }
    private void OnHostWindowSizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs e)
    {
        var view = _controller?.View;
        double before = view?.ActualHeight ?? -1;
        if (sender is Microsoft.UI.Xaml.Window w &&
            w.Content is Microsoft.UI.Xaml.FrameworkElement root)
        {
            root.InvalidateMeasure();
            root.UpdateLayout();
            DiagLog($"HostSizeChanged size={e.Size.Width}x{e.Size.Height} " +
                    $"rootActual={root.ActualWidth}x{root.ActualHeight} " +
                    $"viewBefore={before} viewAfter={view?.ActualHeight ?? -1}");
        }
        else
        {
            DiagLog($"HostSizeChanged size={e.Size.Width}x{e.Size.Height} (no root) viewBefore={before}");
        }
    }

    private static readonly string _diagPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maplibre_maui_diag.log");
    private static void DiagLog(string msg)
    {
        try { System.IO.File.AppendAllText(_diagPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n"); }
        catch { /* ignore */ }
        System.Diagnostics.Debug.WriteLine($"[MapLibre.Diag] {msg}");
    }

    // ── Input events ──────────────────────────────────────────────────────────

    private void AttachInputEvents(Microsoft.UI.Xaml.Controls.Grid view)
    {
        view.PointerWheelChanged += OnPointerWheelChanged;
        view.PointerPressed      += OnPointerPressed;
        view.PointerMoved        += OnPointerMoved;
        view.PointerReleased     += OnPointerReleased;
        view.PointerCanceled     += OnPointerCanceled;
        view.DoubleTapped        += OnDoubleTapped;
        // Pinch-to-zoom is handled at the Win32 level via WM_GESTURE in the
        // popup HWND's WndProc.  Do NOT use XAML ManipulationDelta — even
        // Scale-only mode triggers an arithmetic overflow in WinUI 3's internal
        // manipulation tracker on precision touchpads
        // (microsoft/microsoft-ui-xaml#8084).
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pt    = e.GetCurrentPoint((UIElement)sender);
        double delta = pt.Properties.MouseWheelDelta / 120.0; // positive = zoom in
        double cx = pt.Position.X;
        double cy = pt.Position.Y;
        _controller.OnPointerWheelChanged(delta, cx, cy);
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var el = (UIElement)sender;
        el.CapturePointer(e.Pointer);
        var pt = e.GetCurrentPoint(el);
        _lastPointerX = pt.Position.X;
        _lastPointerY = pt.Position.Y;
        _isDragging   = true;
        _controller.OnPointerPressed(pt.Position.X, pt.Position.Y);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        var pt = e.GetCurrentPoint((UIElement)sender);
        double dx = pt.Position.X - _lastPointerX;
        double dy = pt.Position.Y - _lastPointerY;
        _lastPointerX = pt.Position.X;
        _lastPointerY = pt.Position.Y;
        _controller.OnPointerMoved(dx, dy);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _isDragging = false;
        _controller.OnPointerReleased();
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        _controller.OnPointerReleased();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var pos = e.GetPosition((UIElement)sender);
        _controller.OnDoubleTapped(pos.X, pos.Y);
        e.Handled = true;
    }

    // ── PropertyMapper update methods ─────────────────────────────────────────

    public void UpdateStyleUrl(string styleUrl)
    {
        _styleUrl = styleUrl;
        _controller.SetStyleString(styleUrl);
    }

    public void UpdateMinMaxZoomPreference(double? minZoom, double? maxZoom)
        => _controller.SetMinMaxZoomPreference(minZoom, maxZoom);

    public void UpdateRotateGestureEnabled(bool v)   => _controller.SetRotateGesturesEnabled(v);
    public void UpdateScrollGesturesEnabled(bool v)  => _controller.SetScrollGesturesEnabled(v);
    public void UpdateTiltGesturesEnabled(bool v)    => _controller.SetTiltGesturesEnabled(v);
    public void UpdateTrackCameraPosition(bool v)    => _controller.SetTrackCameraPosition(v);
    public void UpdateZoomGesturesEnabled(bool v)    => _controller.SetZoomGesturesEnabled(v);
    public void UpdateMyLocationEnabled(bool v)      => _controller.SetMyLocationEnabled(v);
    public void UpdateMyLocationTrackingMode(int v)  => _controller.SetMyLocationTrackingMode(v);
    public void UpdateMyLocationRenderMode(int v)    => _controller.SetMyLocationRenderMode(v);
    public void UpdateLogoViewMargins(int?[]? margin) { if (margin?.Length >= 2 && margin[0] != null && margin[1] != null) _controller.SetLogoViewMargins(margin[0]!.Value, margin[1]!.Value); }
    public void UpdateCompassGravity(int gravity)    => _controller.SetCompassGravity(gravity);
    public void UpdateCompassViewMargins(int?[]? margin) { if (margin?.Length >= 2 && margin[0] != null && margin[1] != null) _controller.SetCompassViewMargins(margin[0]!.Value, margin[1]!.Value); }
    public void UpdateAttributionButtonGravity(int gravity) => _controller.SetAttributionButtonGravity(gravity);
    public void UpdateAttributionButtonMargins(int?[]? margin) { if (margin?.Length >= 2 && margin[0] != null && margin[1] != null) _controller.SetAttributionButtonMargins(margin[0]!.Value, margin[1]!.Value); }
    public void UpdateShowNavigationControls(bool show) => _controller.SetShowNavigationControls(show);
    public void UpdateShowAttributionControl(bool show, string? customAttribution) => _controller.SetShowAttributionControl(show, customAttribution);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void DisconnectHandler(Microsoft.UI.Xaml.Controls.Grid platformView)
    {
        // Shutdown the GL popup and native mbgl resources BEFORE base removes the
        // platform view from the visual tree. This guarantees the dispatcher timer
        // is stopped and the HWND is destroyed even in navigation patterns where
        // the XAML Unloaded event fires asynchronously or is skipped entirely
        // (e.g. Shell tab switches on WinUI 3 with some MAUI versions).
        _controller.Shutdown();

        // Unhook the host-window size handler.
        if (_hostWindow != null)
        {
            _hostWindow.SizeChanged -= OnHostWindowSizeChanged;
            _hostWindow = null;
        }

        // Unhook input events so they can't fire after the controller is gone.
        platformView.PointerWheelChanged -= OnPointerWheelChanged;
        platformView.PointerPressed      -= OnPointerPressed;
        platformView.PointerMoved        -= OnPointerMoved;
        platformView.PointerReleased     -= OnPointerReleased;
        platformView.PointerCanceled     -= OnPointerCanceled;
        platformView.DoubleTapped        -= OnDoubleTapped;

        base.DisconnectHandler(platformView);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────


    private static float GetDpiForWindow(Microsoft.UI.Xaml.Window window)
        => (float)(window.Content?.XamlRoot?.RasterizationScale ?? 1.0);
}
#endif

