/**
 * MainWindow.xaml.cs — WPF example using Maui.MapLibre.WPF.MlnMapHost.
 *
 * Demonstrates:
 *  • Loading a MapLibre style via the StyleUrl dependency property
 *  • Flying to named locations with CenterOn()
 *  • Zoom / north-reset helpers
 *  • Adding and removing a GeoJSON circle-layer marker
 *  • Listening to MapReady / StyleLoaded / CameraIdle events
 */
using System.Windows;

namespace WpfExample;

public partial class MainWindow : Window
{
    private bool   _markerVisible;
    private double _currentZoom = 9;

    const string MarkerSourceId = "example-marker";
    const string MarkerLayerId  = "example-marker-layer";

    // Seattle GeoJSON point — matches the default fly-to location
    const string MarkerGeoJson = """
        {
          "type": "FeatureCollection",
          "features": [{
            "type": "Feature",
            "geometry": {
              "type": "Point",
              "coordinates": [-122.3321, 47.6062]
            },
            "properties": {}
          }]
        }
        """;

    public MainWindow() => InitializeComponent();

    // ── Map lifecycle events ───────────────────────────────────────────────────

    private void MapHost_MapReady(object sender, EventArgs e)
        => StatusText.Text = "Map ready — loading style…";

    private void MapHost_StyleLoaded(object sender, EventArgs e)
    {
        StatusText.Text = "Style loaded.";
        // Start centred on Seattle
        MapHost.CenterOn(47.6062, -122.3321, zoom: 9);
    }

    private void MapHost_CameraIdle(object sender, EventArgs e)
        => StatusText.Text = $"Camera idle.";

    // ── Fly-to buttons ─────────────────────────────────────────────────────────

    private void BtnSeattle_Click(object sender, RoutedEventArgs e)
        => MapHost.CenterOn(47.6062, -122.3321, zoom: 10);

    private void BtnLondon_Click(object sender, RoutedEventArgs e)
        => MapHost.CenterOn(51.5074, -0.1278, zoom: 10);

    private void BtnNewYork_Click(object sender, RoutedEventArgs e)
        => MapHost.CenterOn(40.7128, -74.0060, zoom: 10);

    // ── Camera helpers ─────────────────────────────────────────────────────────

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)  => MapHost.ZoomIn();
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => MapHost.ZoomOut();
    private void BtnNorth_Click(object sender, RoutedEventArgs e)   => MapHost.ResetNorth();

    // ── GeoJSON marker (toggle) ────────────────────────────────────────────────

    private void BtnMarker_Click(object sender, RoutedEventArgs e)
    {
        if (!_markerVisible)
            AddMarker();
        else
            RemoveMarker();
    }

    private void AddMarker()
    {
        MapHost.AddGeoJsonSource(MarkerSourceId, MarkerGeoJson);
        MapHost.AddCircleLayer(
            layerName:    MarkerLayerId,
            sourceName:   MarkerSourceId,
            belowLayerId: null,
            sourceLayer:  null,
            properties: new Dictionary<string, object?>
            {
                ["circle-radius"]       = 12.0,
                ["circle-color"]        = "#E74C3C",
                ["circle-opacity"]      = 0.9,
                ["circle-stroke-width"] = 2.0,
                ["circle-stroke-color"] = "#FFFFFF",
            });

        _markerVisible      = true;
        BtnMarker.Content   = "Remove Marker";
        StatusText.Text     = "Marker added at Seattle.";
    }

    private void RemoveMarker()
    {
        MapHost.RemoveLayer(MarkerLayerId);
        MapHost.RemoveSource(MarkerSourceId);

        _markerVisible      = false;
        BtnMarker.Content   = "Add Marker";
        StatusText.Text     = "Marker removed.";
    }
}
