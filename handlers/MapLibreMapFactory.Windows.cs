#if WINDOWS
namespace MapLibreNative.Maui.Handlers;

public partial class MapLibreMapFactory
{
    public static MapLibreMapController Create(
        nint parentHwnd,
        float pixelRatio,
        Dictionary<string, object> args)
    {
        var builder = new MapLibreMapBuilder();

        if (args.TryGetValue("styleString", out var styleString))
            builder.SetStyleString((string)styleString);

        if (args.TryGetValue("minZoom", out var minZoom))
            builder.SetMinMaxZoomPreference((double)minZoom, null);

        if (args.TryGetValue("maxZoom", out var maxZoom))
            builder.SetMinMaxZoomPreference(null, (double)maxZoom);

        return builder.Build(parentHwnd, pixelRatio);
    }
}
#endif
