using MapLibreNative.Maui.Handlers;

namespace MauiSample;

public partial class MarkersPage : ContentPage
{
    private const string SourceId = "landmarks";
    private const string CircleLayerId = "landmarks-circle";
    private const string LabelLayerId = "landmarks-label";

    // Five world landmarks as a GeoJSON FeatureCollection of Points.
    private const string LandmarksGeoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-0.1246, 51.5007] },  "properties": { "name": "Big Ben" } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [2.2945, 48.8584] },   "properties": { "name": "Eiffel Tower" } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [12.4922, 41.8902] },  "properties": { "name": "Colosseum" } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [-43.2104, -22.9519] },"properties": { "name": "Christ the Redeemer" } },
            { "type": "Feature", "geometry": { "type": "Point", "coordinates": [139.7673, 35.6836] }, "properties": { "name": "Tokyo Tower" } }
          ]
        }
        """;

    private readonly MarkersViewModel _vm = new();
    private bool _markersAdded = false;

    public MarkersPage()
    {
        InitializeComponent();
        BindingContext = _vm;
    }

    private void OnMapReady(object? sender, EventArgs e)
    {
        _vm.Status = "Map ready";
    }

    private void OnAddMarkers(object? sender, EventArgs e)
    {
        var ctrl = (Map.Handler as MapLibreMapHandler)?.Controller;
        if (ctrl == null) { _vm.Status = "Map not ready"; return; }
        if (_markersAdded) { _vm.Status = "Landmarks already on map"; return; }

        ctrl.AddGeoJsonSource(SourceId, LandmarksGeoJson);

        ctrl.AddCircleLayer(CircleLayerId, SourceId, null, null, new Dictionary<string, object?>
        {
            ["circle-radius"] = 8.0,
            ["circle-color"] = "#E55E5E",
            ["circle-stroke-color"] = "#ffffff",
            ["circle-stroke-width"] = 2.0
        });

        ctrl.AddSymbolLayer(LabelLayerId, SourceId, null, null, new Dictionary<string, object?>
        {
            ["text-field"] = "{name}",
            ["text-size"] = 12.0,
            ["text-offset"] = new[] { 0.0, 1.5 },
            ["text-anchor"] = "top",
            ["text-color"] = "#333333",
            ["text-halo-color"] = "#ffffff",
            ["text-halo-width"] = 1.0
        });

        _markersAdded = true;
        _vm.Status = "5 landmarks added";
    }

    private void OnRemoveMarkers(object? sender, EventArgs e)
    {
        var ctrl = (Map.Handler as MapLibreMapHandler)?.Controller;
        if (ctrl == null) { _vm.Status = "Map not ready"; return; }
        if (!_markersAdded) { _vm.Status = "No landmarks to remove"; return; }

        ctrl.RemoveLayer(LabelLayerId);
        ctrl.RemoveLayer(CircleLayerId);
        ctrl.RemoveSource(SourceId);

        _markersAdded = false;
        _vm.Status = "Landmarks removed";
    }
}
