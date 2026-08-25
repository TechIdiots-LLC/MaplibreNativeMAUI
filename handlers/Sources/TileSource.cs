namespace MapLibreNative.Maui.Handlers.Sources;

public class TileSource : SourceView
{
    public static readonly BindableProperty TileUrlProperty = BindableProperty.Create(nameof(TileUrl), typeof(string), typeof(RasterDemSource), null);
    public static readonly BindableProperty TileUrlTemplatesProperty = BindableProperty.Create(nameof(TileUrlTemplates), typeof(string[]), typeof(RasterDemSource), null);
    public static readonly BindableProperty AttributionProperty = BindableProperty.Create(nameof(Attribution), typeof(string), typeof(TileSource), null);

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

    /// <summary>
    /// Attribution HTML for the source, shown by the attribution control. Only honoured with
    /// <see cref="TileUrlTemplates"/>; a TileJSON source carries its own attribution.
    /// </summary>
    public string? Attribution
    {
        get => (string?)GetValue(AttributionProperty);
        set => SetValue(AttributionProperty, value);
    }
}