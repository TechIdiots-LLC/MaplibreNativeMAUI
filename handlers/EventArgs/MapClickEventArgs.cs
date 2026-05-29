using MapLibreNative.Maui.Handlers.Geometry;

namespace MapLibreNative.Maui.Handlers.EventArgs;

public class MapClickEventArgs : System.EventArgs
{
    public LatLng LatLng { get; set; }
}