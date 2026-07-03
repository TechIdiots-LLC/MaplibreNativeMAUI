using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapLibreNative.Maui.Handlers.Properties;

public class SymbolLayerProperties : ILayerProperties
{
    // ── Layout ────────────────────────────────────────────────────────────────

    [JsonPropertyName("symbol-placement")]
    public string? SymbolPlacement { get; set; }

    [JsonPropertyName("symbol-spacing")]
    public double? SymbolSpacing { get; set; }

    [JsonPropertyName("icon-image")]
    public string? IconImage { get; set; }

    [JsonPropertyName("icon-size")]
    public double? IconSize { get; set; }

    [JsonPropertyName("icon-anchor")]
    public string? IconAnchor { get; set; }

    [JsonPropertyName("icon-allow-overlap")]
    public bool? IconAllowOverlap { get; set; }

    [JsonPropertyName("icon-ignore-placement")]
    public bool? IconIgnorePlacement { get; set; }

    [JsonPropertyName("icon-optional")]
    public bool? IconOptional { get; set; }

    [JsonPropertyName("icon-offset")]
    public double[]? IconOffset { get; set; }

    [JsonPropertyName("icon-rotate")]
    public double? IconRotate { get; set; }

    [JsonPropertyName("text-field")]
    public string? TextField { get; set; }

    [JsonPropertyName("text-font")]
    public string[]? TextFont { get; set; }

    [JsonPropertyName("text-size")]
    public double? TextSize { get; set; }

    [JsonPropertyName("text-anchor")]
    public string? TextAnchor { get; set; }

    [JsonPropertyName("text-offset")]
    public double[]? TextOffset { get; set; }

    [JsonPropertyName("text-allow-overlap")]
    public bool? TextAllowOverlap { get; set; }

    [JsonPropertyName("text-max-width")]
    public double? TextMaxWidth { get; set; }

    [JsonPropertyName("text-transform")]
    public string? TextTransform { get; set; }

    [JsonPropertyName("text-rotate")]
    public double? TextRotate { get; set; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }

    // ── Paint ─────────────────────────────────────────────────────────────────

    [JsonPropertyName("icon-opacity")]
    public double? IconOpacity { get; set; }

    [JsonPropertyName("icon-color")]
    public string? IconColor { get; set; }

    [JsonPropertyName("icon-halo-color")]
    public string? IconHaloColor { get; set; }

    [JsonPropertyName("icon-halo-width")]
    public double? IconHaloWidth { get; set; }

    [JsonPropertyName("icon-halo-blur")]
    public double? IconHaloBlur { get; set; }

    [JsonPropertyName("text-opacity")]
    public double? TextOpacity { get; set; }

    [JsonPropertyName("text-color")]
    public string? TextColor { get; set; }

    [JsonPropertyName("text-halo-color")]
    public string? TextHaloColor { get; set; }

    [JsonPropertyName("text-halo-width")]
    public double? TextHaloWidth { get; set; }

    [JsonPropertyName("text-halo-blur")]
    public double? TextHaloBlur { get; set; }

    [JsonPropertyName("text-translate")]
    public double[]? TextTranslate { get; set; }

    // ── ILayerProperties ──────────────────────────────────────────────────────

    public void FromJson(string json)
    {
        var options = JsonSerializer.Deserialize<SymbolLayerProperties>(json);
        if (options == null) return;
        SymbolPlacement    = options.SymbolPlacement    ?? SymbolPlacement;
        SymbolSpacing      = options.SymbolSpacing      ?? SymbolSpacing;
        IconImage          = options.IconImage          ?? IconImage;
        IconSize           = options.IconSize           ?? IconSize;
        IconAnchor         = options.IconAnchor         ?? IconAnchor;
        IconAllowOverlap   = options.IconAllowOverlap   ?? IconAllowOverlap;
        IconIgnorePlacement= options.IconIgnorePlacement?? IconIgnorePlacement;
        IconOptional       = options.IconOptional       ?? IconOptional;
        IconOffset         = options.IconOffset         ?? IconOffset;
        IconRotate         = options.IconRotate         ?? IconRotate;
        TextField          = options.TextField          ?? TextField;
        TextFont           = options.TextFont           ?? TextFont;
        TextSize           = options.TextSize           ?? TextSize;
        TextAnchor         = options.TextAnchor         ?? TextAnchor;
        TextOffset         = options.TextOffset         ?? TextOffset;
        TextAllowOverlap   = options.TextAllowOverlap   ?? TextAllowOverlap;
        TextMaxWidth       = options.TextMaxWidth       ?? TextMaxWidth;
        TextTransform      = options.TextTransform      ?? TextTransform;
        TextRotate         = options.TextRotate         ?? TextRotate;
        Visibility         = options.Visibility         ?? Visibility;
        IconOpacity        = options.IconOpacity        ?? IconOpacity;
        IconColor          = options.IconColor          ?? IconColor;
        IconHaloColor      = options.IconHaloColor      ?? IconHaloColor;
        IconHaloWidth      = options.IconHaloWidth      ?? IconHaloWidth;
        IconHaloBlur       = options.IconHaloBlur       ?? IconHaloBlur;
        TextOpacity        = options.TextOpacity        ?? TextOpacity;
        TextColor          = options.TextColor          ?? TextColor;
        TextHaloColor      = options.TextHaloColor      ?? TextHaloColor;
        TextHaloWidth      = options.TextHaloWidth      ?? TextHaloWidth;
        TextHaloBlur       = options.TextHaloBlur       ?? TextHaloBlur;
        TextTranslate      = options.TextTranslate      ?? TextTranslate;
    }

    public IDictionary<string, object?> ToDictionary()
    {
        return new Dictionary<string, object?>
        {
            { "symbol-placement",     SymbolPlacement },
            { "symbol-spacing",       SymbolSpacing },
            { "icon-image",           IconImage },
            { "icon-size",            IconSize },
            { "icon-anchor",          IconAnchor },
            { "icon-allow-overlap",   IconAllowOverlap },
            { "icon-ignore-placement",IconIgnorePlacement },
            { "icon-optional",        IconOptional },
            { "icon-offset",          IconOffset },
            { "icon-rotate",          IconRotate },
            { "text-field",           TextField },
            { "text-font",            TextFont },
            { "text-size",            TextSize },
            { "text-anchor",          TextAnchor },
            { "text-offset",          TextOffset },
            { "text-allow-overlap",   TextAllowOverlap },
            { "text-max-width",       TextMaxWidth },
            { "text-transform",       TextTransform },
            { "text-rotate",          TextRotate },
            { "visibility",           Visibility },
            { "icon-opacity",         IconOpacity },
            { "icon-color",           IconColor },
            { "icon-halo-color",      IconHaloColor },
            { "icon-halo-width",      IconHaloWidth },
            { "icon-halo-blur",       IconHaloBlur },
            { "text-opacity",         TextOpacity },
            { "text-color",           TextColor },
            { "text-halo-color",      TextHaloColor },
            { "text-halo-width",      TextHaloWidth },
            { "text-halo-blur",       TextHaloBlur },
            { "text-translate",       TextTranslate },
        };
    }
}
