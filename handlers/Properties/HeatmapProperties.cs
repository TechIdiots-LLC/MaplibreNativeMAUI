using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapLibreNative.Maui.Handlers.Properties;

public class HeatmapProperties : ILayerProperties
{
    // ── Layout ────────────────────────────────────────────────────────────────

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }

    // ── Paint ─────────────────────────────────────────────────────────────────

    [JsonPropertyName("heatmap-radius")]
    public double? HeatmapRadius { get; set; }

    [JsonPropertyName("heatmap-weight")]
    public double? HeatmapWeight { get; set; }

    [JsonPropertyName("heatmap-intensity")]
    public double? HeatmapIntensity { get; set; }

    /// <summary>
    /// Colour ramp. Usually a MapLibre expression (e.g. an <c>["interpolate", …]</c>
    /// array) but a plain colour string is also accepted. Passed through verbatim
    /// as JSON to the native paint property.
    /// </summary>
    [JsonPropertyName("heatmap-color")]
    public object? HeatmapColor { get; set; }

    [JsonPropertyName("heatmap-opacity")]
    public double? HeatmapOpacity { get; set; }

    // ── ILayerProperties ──────────────────────────────────────────────────────

    public void FromJson(string json)
    {
        var options = JsonSerializer.Deserialize<HeatmapProperties>(json);
        if (options == null) return;
        Visibility       = options.Visibility       ?? Visibility;
        HeatmapRadius    = options.HeatmapRadius     ?? HeatmapRadius;
        HeatmapWeight    = options.HeatmapWeight     ?? HeatmapWeight;
        HeatmapIntensity = options.HeatmapIntensity  ?? HeatmapIntensity;
        HeatmapColor     = options.HeatmapColor      ?? HeatmapColor;
        HeatmapOpacity   = options.HeatmapOpacity    ?? HeatmapOpacity;
    }

    public IDictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            { "visibility",        Visibility },
            { "heatmap-radius",    HeatmapRadius },
            { "heatmap-weight",    HeatmapWeight },
            { "heatmap-intensity", HeatmapIntensity },
            { "heatmap-color",     HeatmapColor },
            { "heatmap-opacity",   HeatmapOpacity },
        };
    }
}