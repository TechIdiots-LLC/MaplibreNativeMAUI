using System.Collections.ObjectModel;
using System.Collections.Specialized;
using GeoJSON.Text.Geometry;
using MapLibreNative.Maui.Handlers.Properties;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;

namespace MapLibreNative.Maui.Handlers.Overlays;

/// <summary>
/// A line connecting a sequence of geographic locations, mirroring
/// <c>Microsoft.Maui.Controls.Maps.Polyline</c>. Rendered as a GeoJSON <c>LineString</c> + line layer.
/// </summary>
public class Polyline : MapOverlayElement
{
    /// <summary>The ordered locations that form the line. Mutating the collection rebuilds the line.</summary>
    public IList<Location> Geopath { get; }

    public Polyline()
    {
        var observable = new ObservableCollection<Location>();
        observable.CollectionChanged += OnGeopathChanged;
        Geopath = observable;
    }

    void OnGeopathChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    string LineLayerId => ElementId + "_line";

    protected override void BuildOverlay(MapLibreMap map)
    {
        if (Geopath.Count < 2) return;

        var line = new LineString(ToPositions(Geopath));
        map.AddGeoJsonSource(SourceId, ToFeatureCollection(line));

        var props = new LineLayerProperties(
            lineColor: ToMlnColor(StrokeColor, Color.FromRgb(0, 122, 255)),
            lineWidth: StrokeWidth,
            lineCap: "round",
            lineJoin: "round");
        map.AddLineLayer(LineLayerId, SourceId, null, null, props);
    }

    protected override void RemoveOverlay(MapLibreMap map)
    {
        map.RemoveLayer(LineLayerId);
        map.RemoveSource(SourceId);
    }
}
