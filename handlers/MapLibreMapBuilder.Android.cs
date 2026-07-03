namespace MapLibreNative.Maui.Handlers;

public partial class MapLibreMapBuilder : IMapLibreMapOptionsSink
{
    private string _styleString = "";

    public MapLibreMapController Build(float pixelRatio)
        => new MapLibreMapController(pixelRatio, _styleString);

    public void SetStyleString(string styleString)         { _styleString = styleString; }
    public void SetCompassEnabled(bool v)                  { }
    public void SetMinMaxZoomPreference(double? min, double? max) { }
    public void SetRotateGesturesEnabled(bool v)           { }
    public void SetScrollGesturesEnabled(bool v)           { }
    public void SetTiltGesturesEnabled(bool v)             { }
    public void SetTrackCameraPosition(bool v)             { }
    public void SetZoomGesturesEnabled(bool v)             { }
    public void SetMyLocationEnabled(bool v)               { }
    public void SetMyLocationTrackingMode(int v)           { }
    public void SetMyLocationRenderMode(int v)             { }
    public void SetLogoViewMargins(int x, int y)           { }
    public void SetCompassGravity(int gravity)             { }
    public void SetCompassViewMargins(int x, int y)        { }
    public void SetAttributionButtonGravity(int gravity)   { }
    public void SetAttributionButtonMargins(int x, int y)  { }
    public void SetShowNavigationControls(bool show)        { }
    public void SetShowAttributionControl(bool show, string? customAttribution) { }
    public void SetShowGpsControl(bool show)                { }
    public void SetNavigationControlPosition(MapControlCorner corner)  { }
    public void SetGpsControlPosition(MapControlCorner corner)         { }
    public void SetAttributionControlPosition(MapControlCorner corner) { }
}
