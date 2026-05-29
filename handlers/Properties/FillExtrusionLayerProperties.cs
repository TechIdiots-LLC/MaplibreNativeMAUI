using System.Text.Json;

namespace MapLibreNative.Maui.Handlers.Properties;

public class FillExtrusionLayerProperties(
    int? fillExtrusionOpacity, 
    string? fillExtrusionColor, 
    int[]? fillExtrusionTranslate, 
    string? fillExtrusionTranslateAnchor, 
    string? fillExtrusionPattern, 
    int? fillExtrusionHeight, 
    int? fillExtrusionBase, 
    bool? fillExtrusionVerticalGradient) : ILayerProperties
{
    public int? FillExtrusionOpacity { get; set; } = fillExtrusionOpacity;
    public string? FillExtrusionColor { get; set; } = fillExtrusionColor;
    public int[]? FillExtrusionTranslate { get; set; } = fillExtrusionTranslate;
    public string? FillExtrusionTranslateAnchor { get; set; } = fillExtrusionTranslateAnchor;
    public string? FillExtrusionPattern { get; set; } = fillExtrusionPattern;
    public int? FillExtrusionHeight { get; set; } = fillExtrusionHeight;
    public int? FillExtrusionBase { get; set; } = fillExtrusionBase;
    public bool? FillExtrusionVerticalGradient { get; set; } = fillExtrusionVerticalGradient;
    
    public void FromJson(string json)
    {
        var options = JsonSerializer.Deserialize<FillExtrusionLayerProperties>(json);
        FillExtrusionOpacity = options?.FillExtrusionOpacity;
        FillExtrusionColor = options?.FillExtrusionColor;
        FillExtrusionTranslate = options?.FillExtrusionTranslate;
        FillExtrusionTranslateAnchor = options?.FillExtrusionTranslateAnchor;
        FillExtrusionPattern = options?.FillExtrusionPattern;
        FillExtrusionHeight = options?.FillExtrusionHeight;
        FillExtrusionBase = options?.FillExtrusionBase;
        FillExtrusionVerticalGradient = options?.FillExtrusionVerticalGradient;
    }

    public IDictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            { "fill-extrusion-opacity", FillExtrusionOpacity },
            { "fill-extrusion-color", FillExtrusionColor },
            { "fill-extrusion-translate", FillExtrusionTranslate },
            { "fill-extrusion-translate-anchor", FillExtrusionTranslateAnchor },
            { "fill-extrusion-pattern", FillExtrusionPattern },
            { "fill-extrusion-height", FillExtrusionHeight },
            { "fill-extrusion-base", FillExtrusionBase },
            { "fill-extrusion-vertical-gradient", FillExtrusionVerticalGradient }
        };
    }
}