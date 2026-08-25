namespace MapLibreNative.Maui.Handlers.Sources;

public class RasterDemSource : RasterSource
{
    public static readonly BindableProperty EncodingProperty = BindableProperty.Create(nameof(Encoding), typeof(string), typeof(RasterDemSource), null);

    /// <summary>
    /// DEM encoding — <c>"terrarium"</c> (AWS/Mapzen terrain-tiles) or <c>"mapbox"</c>
    /// (the default when unset). Only honoured with <see cref="TileSource.TileUrlTemplates"/>;
    /// a TileJSON source declares its own encoding.
    /// </summary>
    public string? Encoding
    {
        get => (string?)GetValue(EncodingProperty);
        set => SetValue(EncodingProperty, value);
    }

    protected override void AddLayerToParentMap()
    {
        var parentMap = FindParentMapLibreMap(this);
        if (parentMap == null) return;
        if (string.IsNullOrEmpty(SourceName)) return;
        parentMap.AddRasterDemSource(SourceName, TileUrl, TileUrlTemplates, TileSize, MinZoom, MaxZoom,
                                     Encoding, Attribution);
    }
}
