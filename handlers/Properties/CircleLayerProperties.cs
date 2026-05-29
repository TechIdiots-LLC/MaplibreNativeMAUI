using System.Text.Json;

namespace MapLibreNative.Maui.Handlers.Properties;

public class CircleLayerProperties(
    int? circleSortKey,
    int? circleRadius,
    string? circleColor,
    int? circleBlur,
    int? circleOpacity,
    int[]? circleTranslate,
    string? circleTranslateAnchor,
    string? circlePitchScale,
    string? circlePitchAlignment,
    int? circleStrokeWidth,
    string? circleStrokeColor,
    int? circleStrokeOpacity,
    string? visibility)
    : ILayerProperties
{
    public int? CircleSortKey { get; set; } = circleSortKey;
    public int? CircleRadius { get; set; } = circleRadius;
    public string? CircleColor { get; set; } = circleColor;
    public int? CircleBlur { get; set; } = circleBlur;
    public int? CircleOpacity { get; set; } = circleOpacity;
    public int[]? CircleTranslate { get; set; } = circleTranslate;
    public string? CircleTranslateAnchor { get; set; } = circleTranslateAnchor;
    public string? CirclePitchScale { get; set; } = circlePitchScale;
    public string? CirclePitchAlignment { get; set; } = circlePitchAlignment;
    public int? CircleStrokeWidth { get; set; } = circleStrokeWidth;
    public string? CircleStrokeColor { get; set; } = circleStrokeColor;
    public int? CircleStrokeOpacity { get; set; } = circleStrokeOpacity;
    public string? Visibility { get; set; } = visibility;

    public void FromJson(string json)
    {
        var options = JsonSerializer.Deserialize<CircleLayerProperties>(json);
        CircleSortKey = options?.CircleSortKey;
        CircleRadius = options?.CircleRadius;
        CircleColor = options?.CircleColor;
        CircleBlur = options?.CircleBlur;
        CircleOpacity = options?.CircleOpacity;
        CircleTranslate = options?.CircleTranslate;
        CircleTranslateAnchor = options?.CircleTranslateAnchor;
        CirclePitchScale = options?.CirclePitchScale;
        CirclePitchAlignment = options?.CirclePitchAlignment;
        CircleStrokeColor = options?.CircleStrokeColor;
        CircleStrokeWidth = options?.CircleStrokeWidth;
        CircleStrokeOpacity = options?.CircleStrokeOpacity;
        Visibility = options?.Visibility;
    }

    public IDictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            { "circle-sort-key", CircleSortKey },
            { "circle-radius", CircleRadius },
            { "circle-color", CircleColor },
            { "circle-blur", CircleBlur },
            { "circle-opacity", CircleOpacity },
            { "circle-translate", CircleTranslate },
            { "circle-translate-anchor", CircleTranslateAnchor },
            { "circle-pitch-scale", CirclePitchScale },
            { "circle-pitch-alignment", CirclePitchAlignment },
            { "visibility", Visibility }
        };
    }
}