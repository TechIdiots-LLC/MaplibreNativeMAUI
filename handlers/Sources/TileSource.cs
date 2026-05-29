namespace MapLibreNative.Maui.Handlers.Sources;

public class TileSource : SourceView
{
    public static readonly BindableProperty TileUrlProperty = BindableProperty.Create(nameof(TileUrl), typeof(string), typeof(RasterDemSource), null);
    public static readonly BindableProperty TileUrlTemplatesProperty = BindableProperty.Create(nameof(TileUrlTemplates), typeof(string[]), typeof(RasterDemSource), null);
    public string? TileUrl
    {
        get => (string?)GetValue(TileUrlProperty);
        set => SetValue(TileUrlProperty, value);
    }

    public string[]? TileUrlTemplates
    {
        get => (string[])GetValue(TileUrlTemplatesProperty);
        set => SetValue(TileUrlTemplatesProperty, value);
    }
}