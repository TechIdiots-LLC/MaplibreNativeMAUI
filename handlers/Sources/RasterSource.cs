using MapLibreNative.Maui.Handlers.Annotation;

namespace MapLibreNative.Maui.Handlers.Sources;

public class RasterSource : TileSource
{
    public static readonly BindableProperty TileSizeProperty = BindableProperty.Create(nameof(TileSize), typeof(int), typeof(RasterDemSource), 256);

    public int TileSize
    {
        get => (int)GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }
    
    protected override void AddLayerToParentMap()
    {
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == null) return;
        if (string.IsNullOrEmpty(SourceName)) return;
        parentMap.AddRasterSource(SourceName, TileUrl, TileUrlTemplates, TileSize, MinZoom, MaxZoom);
    }
}