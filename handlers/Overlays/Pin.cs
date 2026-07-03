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
/// Rendered as a circle marker (the typed symbol/text layer is not yet supported by the underlying
/// <c>SymbolLayerProperties</c>). <see cref="Label"/> and <see cref="Address"/> are carried in the
/// feature's properties so they can be retrieved from a rendered-feature query.
/// </remarks>
public class Pin : MapOverlayElement
{
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

    /// <summary>A short label for the marker (carried in the feature properties).</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly BindableProperty AddressProperty = BindableProperty.Create(
        nameof(Address), typeof(string), typeof(Pin), null, propertyChanged: OnVisualChanged);

    /// <summary>A secondary description for the marker (carried in the feature properties).</summary>
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

    /// <summary>The fill colour of the marker. Defaults to red.</summary>
    public Color? TintColor
    {
        get => (Color?)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    /// <summary>Raised when <see cref="SendMarkerClicked"/> is invoked for this marker.</summary>
    public event EventHandler? MarkerClicked;

    /// <summary>Invokes <see cref="MarkerClicked"/>. Hook this from the map's click/feature-query pipeline.</summary>
    public void SendMarkerClicked() => MarkerClicked?.Invoke(this, System.EventArgs.Empty);

    string CircleLayerId => ElementId + "_circle";

    protected override void BuildOverlay(MapLibreMap map)
    {
        if (Location is null) return;

        var props = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(Label)) props["label"] = Label!;
        if (!string.IsNullOrEmpty(Address)) props["address"] = Address!;

        var point = new GeoJSON.Text.Geometry.Point(new Position(Location.Latitude, Location.Longitude));
        map.AddGeoJsonSource(SourceId, ToFeatureCollection(point, props.Count > 0 ? props : null));

        var circle = new CircleLayerProperties(
            circleSortKey: null,
            circleRadius: 8,
            circleColor: ToMlnColor(TintColor, Color.FromRgb(220, 40, 40)),
            circleBlur: null,
            circleOpacity: null,
            circleTranslate: null,
            circleTranslateAnchor: null,
            circlePitchScale: null,
            circlePitchAlignment: null,
            circleStrokeWidth: (int)Math.Round(StrokeWidth),
            circleStrokeColor: ToMlnColor(StrokeColor, Colors.White),
            circleStrokeOpacity: null,
            visibility: null);
        map.AddCircleLayer(CircleLayerId, SourceId, null, null, circle, enableInteraction: true);
    }

    protected override void RemoveOverlay(MapLibreMap map)
    {
        map.RemoveLayer(CircleLayerId);
        map.RemoveSource(SourceId);
    }
}
