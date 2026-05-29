using MapLibreNative.Maui.Handlers.Properties;

namespace MapLibreNative.Maui.Handlers.Layers;

public class FillLayer : LayerView<FillLayerProperties>
{
    protected override void AddLayerToParentMap()
    {
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == null) return;
        if(string.IsNullOrEmpty(LayerName)) return;
        if (string.IsNullOrEmpty(SourceName)) return;
        if (Properties == null) return;
        parentMap.AddFillLayer(LayerName, SourceName, BelowLayerId, SourceLayer, Properties, MinZoom, MaxZoom, EnableInteraction);
    }
}