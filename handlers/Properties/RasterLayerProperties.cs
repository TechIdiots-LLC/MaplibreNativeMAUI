using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapLibreNative.Maui.Handlers.Properties;

public class RasterLayerProperties : ILayerProperties
{
    // ── Layout ────────────────────────────────────────────────────────────────

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }

    // ── Paint ─────────────────────────────────────────────────────────────────

    [JsonPropertyName("raster-opacity")]
    public double? RasterOpacity { get; set; }

    [JsonPropertyName("raster-hue-rotate")]
    public double? RasterHueRotate { get; set; }

    [JsonPropertyName("raster-brightness-min")]
    public double? RasterBrightnessMin { get; set; }

    [JsonPropertyName("raster-brightness-max")]
    public double? RasterBrightnessMax { get; set; }

    [JsonPropertyName("raster-saturation")]
    public double? RasterSaturation { get; set; }

    [JsonPropertyName("raster-contrast")]
    public double? RasterContrast { get; set; }

    [JsonPropertyName("raster-resampling")]
    public string? RasterResampling { get; set; }

    [JsonPropertyName("raster-fade-duration")]
    public double? RasterFadeDuration { get; set; }

    // ── ILayerProperties ──────────────────────────────────────────────────────

    public void FromJson(string json)
    {
        var options = JsonSerializer.Deserialize<RasterLayerProperties>(json);
        if (options == null) return;
        Visibility          = options.Visibility          ?? Visibility;
        RasterOpacity       = options.RasterOpacity       ?? RasterOpacity;
        RasterHueRotate     = options.RasterHueRotate     ?? RasterHueRotate;
        RasterBrightnessMin = options.RasterBrightnessMin ?? RasterBrightnessMin;
        RasterBrightnessMax = options.RasterBrightnessMax ?? RasterBrightnessMax;
        RasterSaturation    = options.RasterSaturation    ?? RasterSaturation;
        RasterContrast      = options.RasterContrast      ?? RasterContrast;
        RasterResampling    = options.RasterResampling    ?? RasterResampling;
        RasterFadeDuration  = options.RasterFadeDuration  ?? RasterFadeDuration;
    }

    public IDictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            { "visibility",            Visibility },
            { "raster-opacity",        RasterOpacity },
            { "raster-hue-rotate",     RasterHueRotate },
            { "raster-brightness-min", RasterBrightnessMin },
            { "raster-brightness-max", RasterBrightnessMax },
            { "raster-saturation",     RasterSaturation },
            { "raster-contrast",       RasterContrast },
            { "raster-resampling",     RasterResampling },
            { "raster-fade-duration",  RasterFadeDuration },
        };
    }
}