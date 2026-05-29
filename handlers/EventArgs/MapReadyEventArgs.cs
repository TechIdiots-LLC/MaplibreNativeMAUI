using Map = MapLibreNative.Maui.Handlers.Maps.Map;

namespace MapLibreNative.Maui.Handlers.EventArgs;

public class MapReadyEventArgs : System.EventArgs
{
    public Map Map { get; set; }
}