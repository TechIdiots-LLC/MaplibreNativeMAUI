#if WINDOWS

namespace MapLibreNative.Maui.Handlers;

public partial class MapLibreMapBuilder : IMapLibreMapOptionsSink
{
    private string? _styleString;
    private double? _minZoom;
    private double? _maxZoom;

    public MapLibreMapController Build(nint parentHwnd, float pixelRatio)
    {
        var controller = new MapLibreMapController(parentHwnd, pixelRatio, _styleString);
        if (_minZoom.HasValue || _maxZoom.HasValue)
            controller.SetMinMaxZoomPreference(_minZoom, _maxZoom);
        return controller;
    }

    public void SetStyleString(string styleString) => _styleString = styleString;

    public void SetMinMaxZoomPreference(double? min, double? max)
    {
        if (min.HasValue) _minZoom = min;
        if (max.HasValue) _maxZoom = max;
    }

    // ── IMapLibreMapOptionsSink stubs ─────────────────────────────────────────

    public void SetCompassEnabled(bool compassEnabled)         { }
    public void SetRotateGesturesEnabled(bool v)               { }
    public void SetScrollGesturesEnabled(bool v)               { }
    public void SetTiltGesturesEnabled(bool v)                 { }
    public void SetTrackCameraPosition(bool v)                 { }
    public void SetZoomGesturesEnabled(bool v)                 { }
    public void SetMyLocationEnabled(bool v)                   { }
    public void SetMyLocationTrackingMode(int v)               { }
    public void SetMyLocationRenderMode(int v)                 { }
    public void SetLogoViewMargins(int x, int y)               { }
    public void SetCompassGravity(int gravity)                 { }
    public void SetCompassViewMargins(int x, int y)            { }
    public void SetAttributionButtonGravity(int gravity)       { }
    public void SetAttributionButtonMargins(int x, int y)      { }
    public void SetShowNavigationControls(bool show)            { }
    public void SetShowAttributionControl(bool show, string? customAttribution) { }
}
#endif
