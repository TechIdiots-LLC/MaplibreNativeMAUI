using Style = MapLibreNative.Maui.Handlers.Maps.Style;

namespace MapLibreNative.Maui.Handlers.EventArgs;

public class StyleLoadedEventArgs : System.EventArgs
{
    public Style Style { get; set; }
}