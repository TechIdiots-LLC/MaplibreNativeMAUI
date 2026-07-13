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

    private static int _instanceCounter;
    private readonly int _instanceId = System.Threading.Interlocked.Increment(ref _instanceCounter);
    private static readonly string _hdiagPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maplibre_maui_diag.log");
    private void HDiag(string msg)
    {
        try { System.IO.File.AppendAllText(_hdiagPath, $"{DateTime.Now:HH:mm:ss.fff} [hnd#{_instanceId}] {msg}\r\n"); }
        catch { /* ignore */ }
    }

    private static bool _globalHooksInstalled;
    private static readonly object _hookLock = new();

    // One-time global exception logging so a crash (which the packaged CI build does NOT record in
    // the Windows event log) writes its full stack to the diag log. Logs only; does not swallow.
    private void InstallGlobalExceptionHooks()
    {
        if (_globalHooksInstalled) return;
        lock (_hookLock)
        {
            if (_globalHooksInstalled) return;
            _globalHooksInstalled = true;

            void Write(string tag, object? detail)
            {
                try { System.IO.File.AppendAllText(_hdiagPath,
                    $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {detail}\r\n"); }
                catch { /* ignore */ }
            }

            try
            {
                var app = Microsoft.UI.Xaml.Application.Current;
                if (app != null)
                    app.UnhandledException += (_, e) => Write("XAML-UNHANDLED", $"{e.Message}\r\n{e.Exception}");
            }
            catch { /* ignore */ }

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Write("FATAL-APPDOMAIN", e.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Write("UNOBSERVED-TASK", e.Exception);
                e.SetObserved();
            };
        }
    }

    public IMapLibreMapController Controller => _controller;

    public MapLibreMapHandler() : base(PropertyMapper) { }

    protected override Microsoft.UI.Xaml.Controls.Grid CreatePlatformView()
    {
        InstallGlobalExceptionHooks();
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
        _controller.UiScale = (float)(VirtualView?.UiScale ?? 1.0);   // read before Init creates the view

        _controller.OnMapReadyReceived               += VirtualView.OnMapReady;
        _controller.OnStyleLoadedReceived            += VirtualView.OnStyleLoaded;
        _controller.OnDidBecomeIdleReceived          += VirtualView.OnDidBecomeIdle;
        _controller.OnCameraMoveStartedReceived      += VirtualView.OnCameraMoveStarted;
        _controller.OnCameraMoveReceived             += VirtualView.OnCameraMove;
        _controller.OnCameraIdleReceived             += VirtualView.OnCameraIdle;
        _controller.OnCameraTrackingChangedReceived  += VirtualView.OnCameraTrackingChanged;
        _controller.OnCameraTrackingDismissedReceived += VirtualView.OnCameraTrackingDismissed;
        _controller.OnMapClickReceived               += (ll, sx, sy) => VirtualView.OnMapClick(ll, sx, sy);
        _controller.OnMapLongClickReceived           += (ll, sx, sy) => VirtualView.OnMapLongClick(ll, sx, sy);
        _controller.OnUserLocationUpdateReceived     += VirtualView.OnUserLocationUpdate;

        _controller.Init();

        // On window maximize/restore MAUI does not re-arrange the page on its own,
        // so the map View keeps its old (too-short) height and the nav panel stays
        // hidden until a tab switch. The host Window.SizeChanged DOES fire — use it
        // to force a re-layout so View.SizeChanged runs the real resize.
        _hostWindow = window;
        if (_hostWindow != null)
            _hostWindow.SizeChanged += OnHostWindowSizeChanged;

        // Pointer input is handled entirely by MapImageView on the map surface itself
        // (with an OriginalSource guard so overlay buttons don't drive the map).
        // Do NOT wire pointer events on the outer Grid here: the map surface marks its
        // events Handled, so handler-level events would fire only for presses on the
        // nav/GPS/attribution overlays — panning the map "through" those buttons.
        var view = _controller.View;
        HDiag("CreatePlatformView (handler connected)");
        return view;
    }

    private void OnHostWindowSizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Window w) return;

        HDiag($"HostSizeChanged {e.Size.Width}x{e.Size.Height}");

        // The window is already at its new size here, but the framework has not yet
        // arranged window.Content to fill it (root still reports the old size), so a
        // synchronous re-layout is a no-op. Defer to Low priority so it runs AFTER
        // the window's own layout pass — by then the root has the new size and
        // re-measuring propagates it down to the map Grid, firing View.SizeChanged
        // which performs the real GL/overlay resize and re-shows the nav panel.
        w.DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (w.Content is Microsoft.UI.Xaml.FrameworkElement root)
                {
                    root.InvalidateMeasure();
                    root.UpdateLayout();
                    HDiag($"PostLayout rootActual={root.ActualWidth}x{root.ActualHeight}");
                    // Re-position GL child and overlays now that XAML coordinates are
                    // stable. Needed after window restore from fullscreen where
                    // OnViewSizeChanged fires before the layout pass settles.
                    _controller?.RefreshPosition();
                }
            });
    }

    // ── PropertyMapper update methods ─────────────────────────────────────────

    public void UpdateStyleUrl(string styleUrl)
    {
        _styleUrl = styleUrl;
        _controller.SetStyleString(styleUrl);
    }

    public void UpdateMinMaxZoomPreference(double? minZoom, double? maxZoom)
        => _controller.SetMinMaxZoomPreference(minZoom, maxZoom);

    public void UpdateRotateGesturesEnabled(bool v)   => _controller.SetRotateGesturesEnabled(v);
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
    public void UpdateShowGpsControl(bool show)         => _controller.SetShowGpsControl(show);
    public void UpdateNavigationControlPosition(MapControlCorner corner)  => _controller.SetNavigationControlPosition(corner);
    public void UpdateGpsControlPosition(MapControlCorner corner)         => _controller.SetGpsControlPosition(corner);
    public void UpdateAttributionControlPosition(MapControlCorner corner) => _controller.SetAttributionControlPosition(corner);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void DisconnectHandler(Microsoft.UI.Xaml.Controls.Grid platformView)
    {
        HDiag("DisconnectHandler (handler disconnected - e.g. tab switch away)");
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

        base.DisconnectHandler(platformView);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────


    private static float GetDpiForWindow(Microsoft.UI.Xaml.Window window)
        => (float)(window.Content?.XamlRoot?.RasterizationScale ?? 1.0);
}
#endif

