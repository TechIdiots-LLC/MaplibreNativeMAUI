namespace MapLibreNative.Maui.Handlers;

public partial class MapLibreMapFactory
{
    public static MapLibreMapController Create(float pixelRatio, string? styleString)
        => new MapLibreMapController(pixelRatio, styleString);
}