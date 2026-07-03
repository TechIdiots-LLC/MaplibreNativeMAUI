/**
 * MlnMapImage.cs — airspace-free WPF MapLibre control.
 *
 * Unlike MlnMapHost (an HwndHost whose controls must be floating Popups), this control
 * renders MapLibre into a shared D3D surface and presents it through a WPF D3DImage. The
 * map is therefore an ordinary WPF visual, so on-map controls are real WPF children with
 * correct z-order, clipping and hit-testing — no popups, no airspace.
 *
 * Status: functional architecture; needs on-GPU validation of the WGL_NV_DX_interop path.
 * Kept alongside MlnMapHost (the default) so consumers opt in by choosing this control.
 *
 * Usage in XAML:
 *   xmlns:mlwpf="clr-namespace:MapLibreNative.Maui.WPF;assembly=MapLibreNative.Maui.WPF"
 *   <mlwpf:MlnMapImage StyleUrl="https://demotiles.maplibre.org/style.json" />
 */
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MapLibreNative.Maui;
using MapLibreNative.Maui.WPF.D3DImageRenderer;

namespace MapLibreNative.Maui.WPF;

/// <summary>
/// A WPF MapLibre map control backed by a <see cref="System.Windows.Interop.D3DImage"/>, so the map
/// is a real element in the visual tree and its controls are ordinary WPF children.
/// </summary>
public class MlnMapImage : Grid
{
    [DllImport("opengl32.dll")] private static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll")] private static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll")] private static extern void glClear(uint mask);
    private const uint GL_COLOR_BUFFER_BIT = 0x00004000;
    private const uint GL_DEPTH_BUFFER_BIT = 0x00000100;
    private const uint GL_STENCIL_BUFFER_BIT = 0x00000400;

    // ── Dependency properties ─────────────────────────────────────────────────

    public string StyleUrl
    {
        get => (string)GetValue(StyleUrlProperty);
        set => SetValue(StyleUrlProperty, value);
    }
    public static readonly DependencyProperty StyleUrlProperty =
        DependencyProperty.Register(nameof(StyleUrl), typeof(string), typeof(MlnMapImage),
            new PropertyMetadata("https://demotiles.maplibre.org/style.json", OnStyleUrlChanged));

    private static void OnStyleUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MlnMapImage m && m._map != null && e.NewValue is string url)
        {
            if (url.TrimStart().StartsWith('{')) m._map.SetStyleJson(url);
            else m._map.SetStyleUrl(url);
            m._renderNeedsUpdate = true;
        }
    }

    public bool ShowGpsControl
    {
        get => (bool)GetValue(ShowGpsControlProperty);
        set => SetValue(ShowGpsControlProperty, value);
    }
    public static readonly DependencyProperty ShowGpsControlProperty =
        DependencyProperty.Register(nameof(ShowGpsControl), typeof(bool), typeof(MlnMapImage),
            new PropertyMetadata(true, (d, e) =>
            {
                if (d is MlnMapImage m && m._gpsPanel != null)
                    m._gpsPanel.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            }));

    public bool ShowAttributionControl
    {
        get => (bool)GetValue(ShowAttributionControlProperty);
        set => SetValue(ShowAttributionControlProperty, value);
    }
    public static readonly DependencyProperty ShowAttributionControlProperty =
        DependencyProperty.Register(nameof(ShowAttributionControl), typeof(bool), typeof(MlnMapImage),
            new PropertyMetadata(true, (d, e) =>
            {
                if (d is MlnMapImage m && m._attrBorder != null)
                    m._attrBorder.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            }));

    /// <summary>When true, each GPS fix re-centres the map. Controlled by the GPS tracking mode.</summary>
    public bool FollowLocation { get; set; } = true;

    /// <summary>When false the location indicator always points north (bearing suppressed).</summary>
    public bool ShowBearing { get; set; } = true;

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler? MapReady;
    public event EventHandler? StyleLoaded;
    public event EventHandler<MlnMapClickEventArgs>? MapClicked;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly D3DImage _d3dImage = new();
    private GlDxInteropContext? _interop;
    private MbglRunLoop? _runLoop;
    private MbglFrontend? _frontend;
    private MbglMap? _map;
    private MbglStyle? _style;
    private DispatcherTimer? _renderTimer;

    private bool _initialized, _renderNeedsUpdate = true, _surfaceDirty = true, _styleReady;
    private float _dpi = 1f;
    private int _physW = 1, _physH = 1;

    private bool _isDragging;
    private Point _lastPos, _downPos;
    private const double ClickThresholdPx = 5;

    public MlnMapImage()
    {
        Background = Brushes.Transparent; // ensure hit-testing over the whole map
        // OpenGL renders bottom-left origin; D3DImage samples top-left → flip vertically.
        _image.RenderTransformOrigin = new Point(0.5, 0.5);
        _image.RenderTransform = new ScaleTransform(1, -1);
        _image.Source = _d3dImage;
        Children.Add(_image);

        BuildNavOverlay();
        BuildGpsOverlay();
        BuildAttributionOverlay();

        Loaded += (_, _) => TryInitialize();
        Unloaded += (_, _) => Teardown();
        SizeChanged += (_, _) => UpdateSize();
    }

    private double GetDpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private void TryInitialize()
    {
        if (_initialized || ActualWidth < 2 || ActualHeight < 2) return;
        _dpi = (float)GetDpiScale();
        _physW = Math.Max(1, (int)Math.Round(ActualWidth * _dpi));
        _physH = Math.Max(1, (int)Math.Round(ActualHeight * _dpi));

        _interop = new GlDxInteropContext();
        _interop.Initialize();
        _interop.Resize(_physW, _physH);
        _surfaceDirty = true;

        _runLoop = new MbglRunLoop();
        _frontend = new MbglFrontend(_interop.Hdc, _interop.GlContext, _physW, _physH, _dpi,
            () => _renderNeedsUpdate = true);
        _map = new MbglMap(_frontend, _runLoop, pixelRatio: _dpi, observer: OnMapObserverEvent);
        _map.SetSize(_physW, _physH);

        var url = StyleUrl;
        if (!string.IsNullOrEmpty(url))
        {
            if (url.TrimStart().StartsWith('{')) _map.SetStyleJson(url);
            else _map.SetStyleUrl(url);
        }

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();

        _initialized = true;
        MapReady?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSize()
    {
        if (!_initialized || _interop == null || _map == null || _frontend == null) return;
        if (ActualWidth < 1 || ActualHeight < 1) return;
        _dpi = (float)GetDpiScale();
        int w = Math.Max(1, (int)Math.Round(ActualWidth * _dpi));
        int h = Math.Max(1, (int)Math.Round(ActualHeight * _dpi));
        if (w == _physW && h == _physH) return;
        _physW = w; _physH = h;
        _interop.MakeCurrent();
        _interop.Resize(w, h);
        _surfaceDirty = true;
        _frontend.SetSize(w, h);
        _map.SetSize(w, h);
        _renderNeedsUpdate = true;
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        _runLoop?.RunOnce();
        if (!_renderNeedsUpdate || _interop == null || _frontend == null) return;
        _renderNeedsUpdate = false;

        _interop.MakeCurrent();
        _interop.Lock();
        _interop.BindFramebuffer();
        glViewport(0, 0, _interop.Width, _interop.Height);
        glClearColor(0.85f, 0.90f, 0.97f, 1f);
        glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT | GL_STENCIL_BUFFER_BIT);
        try { _frontend.Render(); } catch { /* swallow per-frame render faults */ }
        _interop.Unlock();

        if (!_d3dImage.IsFrontBufferAvailable) return;
        _d3dImage.Lock();
        if (_surfaceDirty)
        {
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _interop.D3DSurfacePointer, true);
            _surfaceDirty = false;
        }
        _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _interop.Width, _interop.Height));
        _d3dImage.Unlock();
    }

    // ── Camera API ────────────────────────────────────────────────────────────

    public void CenterOn(double latitude, double longitude, double zoom = 14.0)
    {
        if (_map == null) return;
        _map.JumpTo(latitude, longitude, zoom);
        _renderNeedsUpdate = true;
    }

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

    // ── Input (real WPF routed events — no WndProc) ────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_map == null) return;
        var pos = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            var dp = ToPhysical(pos);
            _map.OnDoubleTap(dp.X, dp.Y);
            _renderNeedsUpdate = true;
            e.Handled = true;
            return;
        }
        _isDragging = true;
        _downPos = _lastPos = pos;
        CaptureMouse();
        var p = ToPhysical(_lastPos);
        _map.OnPanStart(p.X, p.Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_map == null || !_isDragging) return;
        var pos = e.GetPosition(this);
        var d = ToPhysical(new Point(pos.X - _lastPos.X, pos.Y - _lastPos.Y));
        _lastPos = pos;
        _map.OnPanMove(d.X, d.Y);
        _renderNeedsUpdate = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_map == null || !_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();
        _map.OnPanEnd();
        _map.TriggerRepaint();
        _renderNeedsUpdate = true;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _downPos.X) <= ClickThresholdPx && Math.Abs(pos.Y - _downPos.Y) <= ClickThresholdPx)
        {
            var p = ToPhysical(pos);
            var ll = _map.LatLngForPixel(p.X, p.Y);
            MapClicked?.Invoke(this, new MlnMapClickEventArgs((int)p.X, (int)p.Y, ll.Lat, ll.Lon));
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_map == null) return;
        var p = ToPhysical(e.GetPosition(this));
        _map.OnScroll((double)e.Delta / 120, p.X, p.Y);
        _renderNeedsUpdate = true;
        e.Handled = true;
    }

    private (double X, double Y) ToPhysical(Point dip) => (dip.X * _dpi, dip.Y * _dpi);

    // ── Nav overlay (real WPF children — the whole point) ──────────────────────

    private void BuildNavOverlay()
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0),
            Width = 30,
        };
        panel.Children.Add(MakeButton("+", ZoomIn, true));
        panel.Children.Add(MakeButton("−", ZoomOut, false));
        Children.Add(panel);
    }

    private static Border MakeButton(string glyph, Action onClick, bool top)
    {
        var b = new Border
        {
            Height = 30,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 218)),
            BorderThickness = new Thickness(1, top ? 1 : 0, 1, 1),
            CornerRadius = top ? new CornerRadius(4, 4, 0, 0) : new CornerRadius(0, 0, 4, 4),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        b.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }

    // ── mbgl observer ─────────────────────────────────────────────────────────

    private void OnMapObserverEvent(string eventName, string? detail)
    {
        switch (eventName)
        {
            case "onDidFinishLoadingStyle":
                Dispatcher.BeginInvoke(() =>
                {
                    _styleReady = true;
                    _locIndLayer = null; // invalidated by style reload
                    _style = _map?.GetStyle();
                    _renderNeedsUpdate = true;
                    if (_pendingLocInd.HasValue) ApplyPendingLocationIndicator();
                    RefreshAttribution();
                    StyleLoaded?.Invoke(this, EventArgs.Empty);
                });
                break;
            case "onCameraDidChange":
                Dispatcher.BeginInvoke(RefreshGpsBearingButton);
                break;
            case "onDidFinishRenderingFramePlacementChanged":
                _map?.TriggerRepaint();
                break;
        }
    }

    // ── Extra camera helpers ───────────────────────────────────────────────────

    /// <summary>Rotate the map back to north (bearing 0).</summary>
    public void ResetNorth()
    {
        if (_map == null) return;
        var (lat, lon) = _map.Center;
        _map.EaseTo(lat, lon, _map.Zoom, bearing: 0, _map.Pitch, durationMs: 300);
        _renderNeedsUpdate = true;
    }

    private void PanTo(double lat, double lon)
    {
        if (_map == null) return;
        _map.EaseTo(lat, lon, _map.Zoom, _map.Bearing, _map.Pitch, durationMs: 200);
        _renderNeedsUpdate = true;
    }

    // ── Location indicator ("blue dot") ────────────────────────────────────────

    private const string LocIndLayerId = "mln_image_location";
    private MbglLayer? _locIndLayer;
    private record struct LocIndParams(double Lat, double Lon, float Bearing, float AccuracyM);
    private LocIndParams? _pendingLocInd;

    /// <summary>Show (or update) the user-location blue dot. Safe to call before the style loads.</summary>
    public void UpdateLocationIndicator(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        bool isFirstFix = !_pendingLocInd.HasValue;
        _pendingLocInd = new LocIndParams(lat, lon, bearing, Math.Max(5f, accuracyMeters));
        if (FollowLocation)
        {
            if (isFirstFix) CenterOn(lat, lon);
            else PanTo(lat, lon);
        }
        if (_styleReady && _style != null) ApplyPendingLocationIndicator();
    }

    public void ClearLocationIndicator()
    {
        _pendingLocInd = null;
        _locIndLayer = null;
        if (_style != null && _styleReady && _style.HasLayer(LocIndLayerId))
            _style.RemoveLayer(LocIndLayerId);
        _renderNeedsUpdate = true;
    }

    private void ApplyPendingLocationIndicator()
    {
        if (_pendingLocInd == null || _style == null) return;
        var p = _pendingLocInd.Value;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (_locIndLayer == null)
        {
            if (_style.HasLayer(LocIndLayerId)) _style.RemoveLayer(LocIndLayerId);
            _locIndLayer = _style.AddLocationIndicatorLayer(LocIndLayerId);
            _locIndLayer.SetPaintProperty("accuracy-radius-color", "\"rgba(30,136,229,0.3)\"");
            _locIndLayer.SetPaintProperty("accuracy-radius-border-color", "\"rgba(30,136,229,0.85)\"");
        }
        _locIndLayer.SetPaintProperty("location", $"[{p.Lat.ToString(ic)},{p.Lon.ToString(ic)},0]");
        _locIndLayer.SetPaintProperty("bearing", (ShowBearing ? p.Bearing : 0f).ToString(ic));
        _locIndLayer.SetPaintProperty("accuracy-radius", p.AccuracyM.ToString(ic));
        _renderNeedsUpdate = true;
    }

    // ── GPS control ────────────────────────────────────────────────────────────

    private enum GpsTrackingMode { Off, Show, Follow, FollowBearing }
    private GpsTrackingMode _gpsTrackingMode = GpsTrackingMode.Off;
    private double _lastGpsLat, _lastGpsLon;
    private float _lastGpsBearing, _lastGpsAccuracy;
    private bool _hasGpsFix;

    private StackPanel? _gpsPanel;
    private Border? _gpsBtnTracking;
    private TextBlock? _gpsTrackingIcon;
    private Border? _gpsBtnBearing;
    private TextBlock? _gpsBearingIcon;

    /// <summary>Feed a GPS fix; honoured per the current tracking mode (Off / Show / Follow / FollowBearing).</summary>
    public void UpdateGpsLocation(double lat, double lon, float bearing = 0, float accuracyMeters = 10)
    {
        _lastGpsLat = lat; _lastGpsLon = lon; _lastGpsBearing = bearing; _lastGpsAccuracy = accuracyMeters;
        bool isFirstFix = !_hasGpsFix;
        _hasGpsFix = true;
        if (_gpsTrackingMode == GpsTrackingMode.Off) return;

        _pendingLocInd = new LocIndParams(lat, lon, bearing, Math.Max(5f, accuracyMeters));
        bool follow = _gpsTrackingMode is GpsTrackingMode.Follow or GpsTrackingMode.FollowBearing;
        bool useBearing = _gpsTrackingMode == GpsTrackingMode.FollowBearing;
        if (follow && _map != null)
        {
            double cameraZoom = _map.Zoom < 8 ? 14 : _map.Zoom;
            double cameraBearing = useBearing ? bearing : _map.Bearing;
            if (isFirstFix) _map.JumpTo(lat, lon, cameraZoom, cameraBearing, _map.Pitch);
            else _map.EaseTo(lat, lon, _map.Zoom, cameraBearing, _map.Pitch, durationMs: 200);
        }
        if (_styleReady && _style != null) ApplyPendingLocationIndicator();
        _renderNeedsUpdate = true;
        if (isFirstFix) RefreshGpsTrackingButton();
    }

    private void CycleGpsMode()
    {
        _gpsTrackingMode = _gpsTrackingMode switch
        {
            GpsTrackingMode.Off => GpsTrackingMode.Show,
            GpsTrackingMode.Show => GpsTrackingMode.Follow,
            GpsTrackingMode.Follow => GpsTrackingMode.FollowBearing,
            GpsTrackingMode.FollowBearing => GpsTrackingMode.Off,
            _ => GpsTrackingMode.Off,
        };
        ApplyGpsMode();
        RefreshGpsTrackingButton();
    }

    private void ApplyGpsMode()
    {
        if (_gpsTrackingMode == GpsTrackingMode.Off)
        {
            ClearLocationIndicator();
        }
        else if (_hasGpsFix)
        {
            _pendingLocInd = new LocIndParams(_lastGpsLat, _lastGpsLon, _lastGpsBearing, Math.Max(5f, _lastGpsAccuracy));
            if (_gpsTrackingMode is GpsTrackingMode.Follow or GpsTrackingMode.FollowBearing && _map != null)
            {
                double zoom = _map.Zoom < 8 ? 14 : _map.Zoom;
                double cameraBearing = _gpsTrackingMode == GpsTrackingMode.FollowBearing ? _lastGpsBearing : _map.Bearing;
                _map.EaseTo(_lastGpsLat, _lastGpsLon, zoom, cameraBearing, _map.Pitch, durationMs: 300);
            }
            if (_styleReady && _style != null) ApplyPendingLocationIndicator();
            _renderNeedsUpdate = true;
        }
    }

    private void GpsBearingButtonPressed()
    {
        if (_gpsTrackingMode == GpsTrackingMode.FollowBearing)
        {
            _gpsTrackingMode = GpsTrackingMode.Follow;
            RefreshGpsTrackingButton();
        }
        ResetNorth();
    }

    private void RefreshGpsTrackingButton()
    {
        if (_gpsTrackingIcon == null || _gpsBtnTracking == null) return;
        switch (_gpsTrackingMode)
        {
            case GpsTrackingMode.Show:
                _gpsTrackingIcon.Text = "⊙";
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));
                _gpsBtnTracking.Background = Brushes.White;
                break;
            case GpsTrackingMode.Follow:
                _gpsTrackingIcon.Text = "◎";
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x70, 0xC5));
                _gpsBtnTracking.Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFF));
                break;
            case GpsTrackingMode.FollowBearing:
                _gpsTrackingIcon.Text = "▲";
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x7C, 0x00));
                _gpsBtnTracking.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0));
                break;
            default:
                _gpsTrackingIcon.Text = "○";
                _gpsTrackingIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                _gpsBtnTracking.Background = Brushes.White;
                break;
        }
    }

    private void RefreshGpsBearingButton()
    {
        if (_gpsBearingIcon == null) return;
        double bearing = _map?.Bearing ?? 0;
        _gpsBearingIcon.Foreground = Math.Abs(bearing) > 0.5
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5))
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    }

    private void BuildGpsOverlay()
    {
        _gpsPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 80, 10, 0),
            Width = 30,
        };
        _gpsTrackingIcon = new TextBlock
        {
            Text = "○",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _gpsBtnTracking = MakeIconButton(_gpsTrackingIcon, CycleGpsMode, true);
        _gpsBearingIcon = new TextBlock
        {
            Text = "↺",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _gpsBtnBearing = MakeIconButton(_gpsBearingIcon, GpsBearingButtonPressed, false);
        _gpsPanel.Children.Add(_gpsBtnTracking);
        _gpsPanel.Children.Add(_gpsBtnBearing);
        Children.Add(_gpsPanel);
    }

    private static Border MakeIconButton(TextBlock icon, Action onClick, bool top)
    {
        var b = new Border
        {
            Height = 30,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 218, 218)),
            BorderThickness = new Thickness(1, top ? 1 : 0, 1, 1),
            CornerRadius = top ? new CornerRadius(4, 4, 0, 0) : new CornerRadius(0, 0, 4, 4),
            Cursor = Cursors.Hand,
            Child = icon,
        };
        b.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }

    // ── Attribution ────────────────────────────────────────────────────────────

    private Border? _attrBorder;
    private TextBlock? _attrTextBlock;
    private string _attrText = string.Empty;
    private bool _attrCollapsed = true;
    private DispatcherTimer? _attrCollapseTimer;

    private void BuildAttributionOverlay()
    {
        _attrTextBlock = new TextBlock
        {
            Text = "ⓘ",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        };
        _attrBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 0xF8, 0xF8, 0xF8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 0, 10),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Child = _attrTextBlock,
        };
        _attrBorder.MouseLeftButtonUp += (_, e) =>
        {
            if (_attrCollapsed) ExpandAttribution(); else CollapseAttribution();
            e.Handled = true;
        };
        Children.Add(_attrBorder);
    }

    private void RefreshAttribution()
    {
        if (_style == null || _attrTextBlock == null || _attrBorder == null) return;
        var parts = MbglStyle.EnsureMapLibreAttribution(_style.GetSourceAttributions());
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            var text = StripHtml(part);
            if (text.Length == 0) continue;
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(text);
        }
        _attrText = sb.ToString();
        if (_attrText.Length > 0 && ShowAttributionControl)
        {
            _attrBorder.Visibility = Visibility.Visible;
            ExpandAttribution();
        }
        else
        {
            _attrBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void ExpandAttribution()
    {
        if (_attrTextBlock == null || _attrText.Length == 0) return;
        _attrCollapsed = false;
        _attrTextBlock.Text = _attrText;
        _attrCollapseTimer?.Stop();
        _attrCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _attrCollapseTimer.Tick += (_, _) => CollapseAttribution();
        _attrCollapseTimer.Start();
    }

    private void CollapseAttribution()
    {
        _attrCollapseTimer?.Stop();
        _attrCollapseTimer = null;
        if (_attrTextBlock == null) return;
        _attrCollapsed = true;
        _attrTextBlock.Text = "ⓘ"; // ⓘ
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var sb = new System.Text.StringBuilder(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private void Teardown()
    {
        _renderTimer?.Stop();
        _renderTimer = null;
        _attrCollapseTimer?.Stop();
        _attrCollapseTimer = null;
        _map?.Dispose(); _map = null;
        _frontend?.Dispose(); _frontend = null;
        _runLoop?.Dispose(); _runLoop = null;
        _interop?.Dispose(); _interop = null;
        _initialized = false;
        _styleReady = false;
        _locIndLayer = null;
    }
}
