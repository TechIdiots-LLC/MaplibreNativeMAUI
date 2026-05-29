using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapLibreNative.Maui.Handlers.Properties;

public class LineLayerProperties(
    int? lineOpacity = null,
    string? lineColor = null,
    int[]? lineTranslate = null,
    string? lineTranslateAnchor = null,
    double? lineWidth = null,
    double? lineGapWidth = null,
    double? lineOffset = null,
    double? lineBlur = null,
    int[]? lineDasharray = null,
    string? linePattern = null,
    string? lineGradient = null,
    string? lineCap = null,
    string? lineJoin = null,
    int? lineMiterLimit = null,
    int? lineRoundLimit = null,
    int? lineSortKey = null,
    string? visibility = null)
    : ILayerProperties
{
    [JsonPropertyName("line-opacity")]
    public int? LineOpacity { get; set; } = lineOpacity;

    [JsonPropertyName("line-color")]
    public string? LineColor { get; set; } = lineColor;

    [JsonPropertyName("line-translate")]
    public int[]? LineTranslate { get; set; } = lineTranslate;

    [JsonPropertyName("line-translate-anchor")]
    public string? LineTranslateAnchor { get; set; } = lineTranslateAnchor;

    [JsonPropertyName("line-width")]
    public double? LineWidth { get; set; } = lineWidth;

    [JsonPropertyName("line-gap-width")]
    public double? LineGapWidth { get; set; } = lineGapWidth;

    [JsonPropertyName("line-offset")]
    public double? LineOffset { get; set; } = lineOffset;

    [JsonPropertyName("line-blur")]
    public double? LineBlur { get; set; } = lineBlur;

    [JsonPropertyName("line-dasharray")]
    public int[]? LineDasharray { get; set; } = lineDasharray;

    [JsonPropertyName("line-pattern")]
    public string? LinePattern { get; set; } = linePattern;

    [JsonPropertyName("line-gradient")]
    public string? LineGradient { get; set; } = lineGradient;

    [JsonPropertyName("line-cap")]
    public string? LineCap { get; set; } = lineCap;

    [JsonPropertyName("line-join")]
    public string? LineJoin { get; set; } = lineJoin;

    [JsonPropertyName("line-miter-limit")]
    public int? LineMiterLimit { get; set; } = lineMiterLimit;

    [JsonPropertyName("line-round-limit")]
    public int? LineRoundLimit { get; set; } = lineRoundLimit;

    [JsonPropertyName("line-sort-key")]
    public int? LineSortKey { get; set; } = lineSortKey;

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; } = visibility;

    public void FromJson(string json)
    {
        var options = JsonSerializer.Deserialize<LineLayerProperties>(json);
        LineOpacity = options?.LineOpacity ?? LineOpacity;
        LineColor = options?.LineColor ?? LineColor;
        LineTranslate = options?.LineTranslate ?? LineTranslate;
        LineTranslateAnchor = options?.LineTranslateAnchor ?? LineTranslateAnchor;
        LineWidth = options?.LineWidth ?? LineWidth;
        LineGapWidth = options?.LineGapWidth ?? LineGapWidth;
        LineOffset = options?.LineOffset ?? LineOffset;
        LineBlur = options?.LineBlur ?? LineBlur;
        LineDasharray = options?.LineDasharray ?? LineDasharray;
        LinePattern = options?.LinePattern ?? LinePattern;
        LineGradient = options?.LineGradient ?? LineGradient;
        LineCap = options?.LineCap ?? LineCap;
        LineJoin = options?.LineJoin ?? LineJoin;
        LineMiterLimit = options?.LineMiterLimit ?? LineMiterLimit;
        LineRoundLimit = options?.LineRoundLimit ?? LineRoundLimit;
        LineSortKey = options?.LineSortKey ?? LineSortKey;
        Visibility = options?.Visibility ?? Visibility;
    }

    public IDictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            { "line-opacity", LineOpacity },
            { "line-color", LineColor },
            { "line-translate", LineTranslate },
            { "line-translate-anchor", LineTranslateAnchor },
            { "line-width", LineWidth },
            { "line-gap-width", LineGapWidth },
            { "line-offset", LineOffset },
            { "line-blur", LineBlur },
            { "line-dasharray", LineDasharray },
            { "line-pattern", LinePattern },
            { "line-gradient", LineGradient },
            { "line-cap", LineCap },
            { "line-join", LineJoin },
            { "line-miter-limit", LineMiterLimit },
            { "line-round-limit", LineRoundLimit },
            { "line-sort-key", LineSortKey },
            { "visibility", Visibility }
        };
    }
}