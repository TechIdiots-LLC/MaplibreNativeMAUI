using System.Collections.ObjectModel;
using System.Collections.Specialized;
using GeoJSON.Text.Geometry;
using MapLibreNative.Maui.Handlers.Properties;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;

namespace MapLibreNative.Maui.Handlers.Overlays;

/// <summary>
/// A filled area bounded by a sequence of geographic locations, mirroring
/// <c>Microsoft.Maui.Controls.Maps.Polygon</c>. Rendered as a GeoJSON <c>Polygon</c> with a fill layer
/// and a stroke line layer. The ring is closed automatically.
/// </summary>
public class Polygon : MapOverlayElement
{
    /// <summary>The vertices of the polygon. Mutating the collection rebuilds the shape.</summary>
    public IList<Location> Geopath { get; }

    public Polygon()
    {
        var observable = new ObservableCollection<Location>();
        observable.CollectionChanged += OnGeopathChanged;
        Geopath = observable;
    }

    void OnGeopathChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    public static readonly BindableProperty FillColorProperty = BindableProperty.Create(
        nameof(FillColor), typeof(Color), typeof(Polygon), null, propertyChanged: OnVisualChanged);

    /// <summary>The fill colour of the polygon.</summary>
    public Color? FillColor
    {
        get => (Color?)GetValue(FillColorProperty);
        set => SetValue(FillColorProperty, value);
    }

    string FillLayerId => ElementId + "_fill";
    string LineLayerId => ElementId + "_line";

    protected override void BuildOverlay(MapLibreMap map)
    {
        if (Geopath.Count < 3) return;

        var positions = ToPositions(Geopath);
        // GeoJSON polygon rings must be closed (first == last).
        if (!positions[0].Equals(positions[^1]))
            positions.Add(positions[0]);

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
