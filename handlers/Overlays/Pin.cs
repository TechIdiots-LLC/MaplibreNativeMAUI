using GeoJSON.Text.Geometry;
using MapLibreNative.Maui.Handlers.Properties;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;

namespace MapLibreNative.Maui.Handlers.Overlays;

/// <summary>The kind of a <see cref="Pin"/>, mirroring <c>Microsoft.Maui.Controls.Maps.PinType</c>.</summary>
public enum PinType
{
    Generic,
    Place,
    SavedPin,
    SearchResult
}

/// <summary>
/// A marker at a geographic location, mirroring <c>Microsoft.Maui.Controls.Maps.Pin</c>.
/// </summary>
/// <remarks>
/// Rendered as a SymbolLayer icon (SDF circle sprite registered as <c>"mln_marker"</c>) with an
/// optional text label below the pin. <see cref="TintColor"/> colorises the icon via
/// <c>icon-color</c>; <see cref="StrokeColor"/> adds a halo. <see cref="Label"/> is stored both
/// in the GeoJSON feature properties (for feature-query read-back) and as the layer's static
/// <c>text-field</c> value.
/// </remarks>
public class Pin : MapOverlayElement
{
    // ── Sprite ────────────────────────────────────────────────────────────────

    private const string SpriteId   = "mln_marker";
    private const int    SpriteSize = 24;

    /// <summary>24×24 premultiplied RGBA white circle (SDF shape mask).</summary>
    private static readonly byte[] SpritePixels = GenerateCircleSprite(SpriteSize, 9.5f);

    private static byte[] GenerateCircleSprite(int size, float radius)
    {
        var pixels = new byte[size * size * 4];
        float cx = (size - 1) * 0.5f;
        float cy = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                // Antialias: 1 inside, 0 outside, smooth at the edge.
                float a = Math.Clamp(radius - dist + 0.5f, 0f, 1f);
                byte b = (byte)(a * 255f);
                int i = (y * size + x) * 4;
                // Premultiplied white: RGB = alpha (white * alpha = alpha).
                pixels[i]     = b; // R
                pixels[i + 1] = b; // G
                pixels[i + 2] = b; // B
                pixels[i + 3] = b; // A
            }
        }
        return pixels;
    }

    // ── Bindable properties ───────────────────────────────────────────────────

    public static readonly BindableProperty LocationProperty = BindableProperty.Create(
        nameof(Location), typeof(Location), typeof(Pin), default(Location), propertyChanged: OnVisualChanged);

    /// <summary>The geographic location of the marker.</summary>
    public Location Location
    {
        get => (Location)GetValue(LocationProperty);
        set => SetValue(LocationProperty, value);
    }

    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label), typeof(string), typeof(Pin), null, propertyChanged: OnVisualChanged);

    /// <summary>A short label shown below the marker and stored in the feature properties.</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly BindableProperty AddressProperty = BindableProperty.Create(
        nameof(Address), typeof(string), typeof(Pin), null, propertyChanged: OnVisualChanged);

    /// <summary>A secondary description (stored in the feature properties for query read-back).</summary>
    public string? Address
    {
        get => (string?)GetValue(AddressProperty);
        set => SetValue(AddressProperty, value);
    }

    public static readonly BindableProperty TypeProperty = BindableProperty.Create(
        nameof(Type), typeof(PinType), typeof(Pin), PinType.Generic);

    /// <summary>The kind of marker. Provided for parity; does not affect rendering.</summary>
    public PinType Type
    {
        get => (PinType)GetValue(TypeProperty);
        set => SetValue(TypeProperty, value);
    }

    public static readonly BindableProperty TintColorProperty = BindableProperty.Create(
        nameof(TintColor), typeof(Color), typeof(Pin), null, propertyChanged: OnVisualChanged);

    /// <summary>The fill colour of the marker icon. Defaults to red.</summary>
    public Color? TintColor
    {
        get => (Color?)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when <see cref="SendMarkerClicked"/> is invoked for this marker.</summary>
    public event EventHandler? MarkerClicked;

    /// <summary>Invokes <see cref="MarkerClicked"/>. Hook this from the map's click/feature-query pipeline.</summary>
    public void SendMarkerClicked() => MarkerClicked?.Invoke(this, System.EventArgs.Empty);

    // ── Overlay lifecycle ─────────────────────────────────────────────────────

    private string SymbolLayerId => ElementId + "_symbol";

    protected override void BuildOverlay(MapLibreMap map)
    {
        if (Location is null) return;

        // Register the default marker sprite (SDF circle) so icon-image can reference it.
        // Safe to call on every rebuild — identical data overwrites the same key idempotently.
        map.AddSpriteImage(SpriteId, SpriteSize, SpriteSize, SpritePixels, pixelRatio: 1f, sdf: true);

        // GeoJSON source: single point with label/address as queryable feature properties.
        var featureProps = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(Label))   featureProps["label"]   = Label!;
        if (!string.IsNullOrEmpty(Address)) featureProps["address"] = Address!;

        var point = new GeoJSON.Text.Geometry.Point(new Position(Location.Latitude, Location.Longitude));
        map.AddGeoJsonSource(SourceId, ToFeatureCollection(point, featureProps.Count > 0 ? featureProps : null));

        // Symbol layer: SDF icon + optional text label.
        var sym = new SymbolLayerProperties
        {
            // Icon
            IconImage        = SpriteId,
            IconSize         = 1.2,
            IconAnchor       = "bottom",    // pin bottom sits on the geographic point
            IconAllowOverlap = true,
            // Icon paint — SDF enables runtime colorisation via icon-color
            IconColor        = ToMlnColor(TintColor, Color.FromRgb(220, 40, 40)),
            IconHaloColor    = ToMlnColor(StrokeColor, Colors.White),
            IconHaloWidth    = StrokeWidth > 0 ? StrokeWidth : 1.5,
        };

        if (!string.IsNullOrEmpty(Label))
        {
            // Static label: the layer is rebuilt per-pin so a literal value is correct.
            sym.TextField        = Label;
            sym.TextFont         = new[] { "Open Sans Regular", "Arial Unicode MS Regular" };
            sym.TextSize         = 12.0;
            sym.TextAnchor       = "top";
            sym.TextOffset       = new[] { 0.0, 0.5 };   // half-em below the icon anchor
            sym.TextColor        = "#333333";
            sym.TextHaloColor    = "white";
            sym.TextHaloWidth    = 1.0;
            sym.TextAllowOverlap = false;
        }

        map.AddSymbolLayer(SymbolLayerId, SourceId, null, null, sym, enableInteraction: true);
    }

    protected override void RemoveOverlay(MapLibreMap map)
    {
        map.RemoveLayer(SymbolLayerId);
        map.RemoveSource(SourceId);
        // Note: the sprite "mln_marker" is shared across all Pin instances and is not removed
        // here — it persists for the lifetime of the style.
    }
}

