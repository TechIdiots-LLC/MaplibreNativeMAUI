using MapLibreNative.Maui.Handlers.Properties;

namespace MapLibreNative.Maui.Handlers.Layers;

public class HeatmapLayer : LayerView<HeatmapProperties>
{
    protected override void AddLayerToParentMap()
    {
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == null) return;
        if(string.IsNullOrEmpty(LayerName)) return;
        if (string.IsNullOrEmpty(SourceName)) return;
        if (Properties == null) return;
        parentMap.AddHeatmapLayer(LayerName, SourceName, BelowLayerId, Properties, MinZoom, MaxZoom);
    }
}