namespace MapLibreNative.Maui.Handlers.Geometry;

public class LatLngBounds(LatLng ne, LatLng sw)
{
    public LatLng NorthEast { get; set; } = ne;
    public LatLng SouthWest { get; set; } = sw;
}