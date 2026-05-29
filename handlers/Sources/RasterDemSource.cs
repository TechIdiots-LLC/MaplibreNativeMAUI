namespace MapLibreNative.Maui.Handlers.Sources;

public class RasterDemSource : RasterSource
{
    protected override void AddLayerToParentMap()
    {
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == null) return;
        if (string.IsNullOrEmpty(SourceName)) return;
        parentMap.AddRasterDemSource(SourceName, TileUrl, TileUrlTemplates, TileSize, MinZoom, MaxZoom);
    }
}