using MapLibreNative.Maui.Handlers.Annotation;
using MapLibreNative.Maui.Handlers.Properties;

namespace MapLibreNative.Maui.Handlers.Layers;

public class LineLayer : LayerView<LineLayerProperties>
{
    protected override void AddLayerToParentMap()
    {
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == null) return;
        if(string.IsNullOrEmpty(LayerName)) return;
        if (string.IsNullOrEmpty(SourceName)) return;
        if (Properties == null) return;
        parentMap.AddLineLayer(LayerName, SourceName, BelowLayerId, SourceLayer, Properties, MinZoom, MaxZoom, EnableInteraction);
    }
}