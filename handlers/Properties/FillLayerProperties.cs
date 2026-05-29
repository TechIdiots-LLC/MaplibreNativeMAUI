using System.Text.Json;

namespace MapLibreNative.Maui.Handlers.Properties;

public class FillLayerProperties(
    int? fillSortKey,
    bool? fillAntialias,
    int? fillOpacity,
    string? fillColor,
    string? fillOutlineColor,
    int[]? fillTranslate,
    string? fillTranslateAnchor,
    string? fillPattern) : ILayerProperties
{
    public int? FillSortKey { get; set; } = fillSortKey;
    public bool? FillAntialias { get; set; } = fillAntialias;
    public int? FillOpacity { get; set; } = fillOpacity;
    public string? FillColor { get; set; } = fillColor;
    public string? FillOutlineColor { get; set; } = fillOutlineColor;
    public int[]? FillTranslate { get; set; } = fillTranslate;
    public string? FillTranslateAnchor { get; set; } = fillTranslateAnchor;
    public string? FillPattern { get; set; } = fillPattern;
    
    public void FromJson(string json)
    {
        var options = JsonSerializer.Deserialize<FillLayerProperties>(json);
        FillSortKey = options?.FillSortKey;
        FillAntialias = options?.FillAntialias;
        FillOpacity = options?.FillOpacity;
        FillColor = options?.FillColor;
        FillOutlineColor = options?.FillOutlineColor;
        FillTranslate = options?.FillTranslate;
        FillTranslateAnchor = options?.FillTranslateAnchor;
        FillPattern = options?.FillPattern;
    }

    public IDictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            { "fill-sort-key", FillSortKey },
            { "fill-antialias", FillAntialias },
            { "fill-opacity", FillOpacity },
            { "fill-color", FillColor },
            { "fill-outline-color", FillOutlineColor },
            { "fill-translate", FillTranslate },
            { "fill-translate-anchor", FillTranslateAnchor },
            { "fill-pattern", FillPattern }
        };
    }
}