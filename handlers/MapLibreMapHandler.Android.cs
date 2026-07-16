#nullable enable

using Android.Views;
using Android.Widget;
using Microsoft.Maui.Handlers;

namespace MapLibreNative.Maui.Handlers;

public partial class MapLibreMapHandler : ViewHandler<MapLibreMap, FrameLayout>
{
    private MapLibreMapController _controller = null!;

    public IMapLibreMapController Controller => _controller;

    public MapLibreMapHandler() : base(PropertyMapper) { }

    protected override FrameLayout CreatePlatformView()
    {
        var dpi = (float)(Platform.CurrentActivity?.Resources?.DisplayMetrics?.Density ?? 1f);
        dpi *= (float)(VirtualView?.UiScale ?? 1.0);   // app-requested extra scaling (e.g. OS font scale)
        _controller = MapLibreMapFactory.Create(dpi, VirtualView?.StyleUrl);

        _controller.OnMapReadyReceived              += VirtualView.OnMapReady;
        _controller.OnStyleLoadedReceived           += VirtualView.OnStyleLoaded;
        _controller.OnDidBecomeIdleReceived         += VirtualView.OnDidBecomeIdle;
        _controller.OnCameraMoveStartedReceived     += VirtualView.OnCameraMoveStarted;
        _controller.OnCameraMoveReceived            += VirtualView.OnCameraMove;
        _controller.OnCameraIdleReceived            += VirtualView.OnCameraIdle;
        _controller.OnCameraTrackingChangedReceived += VirtualView.OnCameraTrackingChanged;
        _controller.OnMapClickReceived              += (ll, sx, sy) => VirtualView.OnMapClick(ll, sx, sy);
        _controller.OnMapLongClickReceived          += (ll, sx, sy) => VirtualView.OnMapLongClick(ll, sx, sy);
        _controller.OnUserLocationUpdateReceived    += VirtualView.OnUserLocationUpdate;

        return _controller.View;
    }

    public void UpdateStyleUrl(string styleUrl)        => _controller.SetStyleString(styleUrl);
    public void UpdateMinMaxZoomPreference(double? min, double? max) => _controller.SetMinMaxZoomPreference(min, max);
    public void UpdateRotateGesturesEnabled(bool v)     => _controller.SetRotateGesturesEnabled(v);
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
    public void UpdateShowGpsControl(bool show)         => _controller.SetShowGpsControl(show);
    public void UpdateGpsFollowZoom(GpsFollowZoomMode mode, double zoom) => _controller.SetGpsFollowZoom(mode, zoom);
    public void UpdateNavigationControlPosition(MapControlCorner corner)  => _controller.SetNavigationControlPosition(corner);
    public void UpdateGpsControlPosition(MapControlCorner corner)         => _controller.SetGpsControlPosition(corner);
    public void UpdateAttributionControlPosition(MapControlCorner corner) => _controller.SetAttributionControlPosition(corner);
    public void UpdateShowTerrainControl(bool show, string sourceId, float exaggeration) => _controller.SetShowTerrainControl(show, sourceId, exaggeration);
    public void UpdateTerrainControlPosition(MapControlCorner corner)     => _controller.SetTerrainControlPosition(corner);
}
