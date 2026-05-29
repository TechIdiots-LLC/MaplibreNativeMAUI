namespace MapLibreNative.Maui.Handlers.Sources;

public class VectorSource : TileSource
{
    protected override void AddLayerToParentMap()
    {
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == null) return;
        if (string.IsNullOrEmpty(SourceName)) return;
        if (string.IsNullOrEmpty(TileUrl) && TileUrlTemplates is not { Length: > 0 }) return;
        parentMap.AddVectorSource(SourceName, TileUrl, TileUrlTemplates, MinZoom, MaxZoom);
    }
}