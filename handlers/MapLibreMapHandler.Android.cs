#nullable enable

using Android.Views;
using Microsoft.Maui.Handlers;

namespace MapLibreNative.Maui.Handlers;

public partial class MapLibreMapHandler : ViewHandler<MapLibreMap, SurfaceView>
{
    private MapLibreMapController _controller = null!;

    public IMapLibreMapController Controller => _controller;

    public MapLibreMapHandler() : base(PropertyMapper) { }

    protected override SurfaceView CreatePlatformView()
    {
        var dpi = (float)(Platform.CurrentActivity?.Resources?.DisplayMetrics?.Density ?? 1f);
        _controller = MapLibreMapFactory.Create(dpi, VirtualView?.StyleUrl);

        _controller.OnMapReadyReceived              += VirtualView.OnMapReady;
        _controller.OnStyleLoadedReceived           += VirtualView.OnStyleLoaded;
        _controller.OnDidBecomeIdleReceived         += VirtualView.OnDidBecomeIdle;
        _controller.OnCameraMoveStartedReceived     += VirtualView.OnCameraMoveStarted;
        _controller.OnCameraMoveReceived            += VirtualView.OnCameraMove;
        _controller.OnCameraIdleReceived            += VirtualView.OnCameraIdle;
        _controller.OnCameraTrackingChangedReceived += VirtualView.OnCameraTrackingChanged;
        _controller.OnMapClickReceived              += VirtualView.OnMapClick;
        _controller.OnMapLongClickReceived          += VirtualView.OnMapLongClick;
        _controller.OnUserLocationUpdateReceived    += VirtualView.OnUserLocationUpdate;

        return _controller.View;
    }

    public void UpdateStyleUrl(string styleUrl)        => _controller.SetStyleString(styleUrl);
    public void UpdateMinMaxZoomPreference(double? min, double? max) => _controller.SetMinMaxZoomPreference(min, max);
    public void UpdateRotateGestureEnabled(bool v)     => _controller.SetRotateGesturesEnabled(v);
    public void UpdateScrollGesturesEnabled(bool v)    => _controller.SetScrollGesturesEnabled(v);
    public void UpdateTiltGesturesEnabled(bool v)      => _controller.SetTiltGesturesEnabled(v);
    public void UpdateTrackCameraPosition(bool v)      => _controller.SetTrackCameraPosition(v);
    public void UpdateZoomGesturesEnabled(bool v)      => _controller.SetZoomGesturesEnabled(v);
    public void UpdateMyLocationEnabled(bool v)        => _controller.SetMyLocationEnabled(v);
    public void UpdateMyLocationTrackingMode(int v)    => _controller.SetMyLocationTrackingMode(v);
    public void UpdateMyLocationRenderMode(int v)      => _controller.SetMyLocationRenderMode(v);
    public void UpdateCompassGravity(int v)            => _controller.SetCompassGravity(v);
    public void UpdateAttributionButtonGravity(int v)  => _controller.SetAttributionButtonGravity(v);
    public void UpdateLogoViewMargins(int?[]? margin)
    {
        if (margin?.Length >= 2 && margin[0] != null && margin[1] != null)
            _controller.SetLogoViewMargins(margin[0]!.Value, margin[1]!.Value);
    }
    public void UpdateCompassViewMargins(int?[]? margin)
    {
        if (margin?.Length >= 2 && margin[0] != null && margin[1] != null)
            _controller.SetCompassViewMargins(margin[0]!.Value, margin[1]!.Value);
    }
    public void UpdateAttributionButtonMargins(int?[]? margin)
    {
        if (margin?.Length >= 2 && margin[0] != null && margin[1] != null)
            _controller.SetAttributionButtonMargins(margin[0]!.Value, margin[1]!.Value);
    }
    public void UpdateShowNavigationControls(bool show) => _controller.SetShowNavigationControls(show);
    public void UpdateShowAttributionControl(bool show, string? customAttribution) => _controller.SetShowAttributionControl(show, customAttribution);
}
