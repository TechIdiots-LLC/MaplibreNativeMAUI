using GeoJSON.Text.Geometry;
using MapLibreNative.Maui.Geometry;
using MapLibreNative.Maui.Handlers.Properties;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;

namespace MapLibreNative.Maui.Handlers.Overlays;

/// <summary>
/// A circle of a given geographic <see cref="Radius"/> drawn on the map, mirroring
/// <c>Microsoft.Maui.Controls.Maps.Circle</c>. Rendered as a filled polygon (approximated via
/// <see cref="GeographyUtils.ToCircumferencePositions"/>) with an optional stroke.
/// </summary>
public class Circle : MapOverlayElement
{
    public static readonly BindableProperty CenterProperty = BindableProperty.Create(
        nameof(Center), typeof(Location), typeof(Circle), default(Location), propertyChanged: OnVisualChanged);

    /// <summary>The centre of the circle.</summary>
    public Location Center
    {
        get => (Location)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    public static readonly BindableProperty RadiusProperty = BindableProperty.Create(
        nameof(Radius), typeof(Distance), typeof(Circle), default(Distance), propertyChanged: OnVisualChanged);

    /// <summary>The radius of the circle.</summary>
    public Distance Radius
    {
        get => (Distance)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public static readonly BindableProperty FillColorProperty = BindableProperty.Create(
        nameof(FillColor), typeof(Color), typeof(Circle), null, propertyChanged: OnVisualChanged);

    /// <summary>The fill colour of the circle.</summary>
    public Color? FillColor
    {
        get => (Color?)GetValue(FillColorProperty);
        set => SetValue(FillColorProperty, value);
    }

    string FillLayerId => ElementId + "_fill";
    string LineLayerId => ElementId + "_line";

    protected override void BuildOverlay(MapLibreMap map)
    {
        if (Center is null || Radius.Meters <= 0) return;

        var ring = GeographyUtils.ToCircumferencePositions(
            new MapCoordinate(Center.Latitude, Center.Longitude), Radius);

        var positions = new List<IPosition>(ring.Count);
        foreach (var p in ring)
            positions.Add(new Position(p.Latitude, p.Longitude));

        var polygon = new GeoJSON.Text.Geometry.Polygon(new List<LineString> { new(positions) });
        map.AddGeoJsonSource(SourceId, ToFeatureCollection(polygon));

        var fill = new FillLayerProperties(
            null, true, null, ToMlnColor(FillColor, Color.FromRgba(0, 122, 255, 0.25f)), null, null, null, null);
        map.AddFillLayer(FillLayerId, SourceId, null, null, fill);

        var line = new LineLayerProperties(
            lineColor: ToMlnColor(StrokeColor, Color.FromRgb(0, 122, 255)),
            lineWidth: StrokeWidth,
            lineCap: "round",
            lineJoin: "round");
        map.AddLineLayer(LineLayerId, SourceId, null, null, line);
    }

    protected override void RemoveOverlay(MapLibreMap map)
    {
        map.RemoveLayer(LineLayerId);
        map.RemoveLayer(FillLayerId);
        map.RemoveSource(SourceId);
    }
}
