namespace MapLibreNative.Maui.Handlers.Geometry;

public class LatLngQuad(LatLng topRight, LatLng topLeft, LatLng bottomRight, LatLng bottomLeft)
{
    public LatLng TopRight { get; set; } = topRight;
    public LatLng TopLeft { get; set; } = topLeft;
    public LatLng BottomRight { get; set; } = bottomRight;
    public LatLng BottomLeft { get; set; } = bottomLeft;
}