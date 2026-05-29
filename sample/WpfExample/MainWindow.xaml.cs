/**
 * MainWindow.xaml.cs — WPF example using MapLibreNative.Maui.WPF.MlnMapHost.
 *
 * Demonstrates:
 *  • Loading a MapLibre style via the StyleUrl dependency property
 *  • Flying to named locations with CenterOn()
 *  • Zoom / north-reset helpers
 *  • Adding and removing a GeoJSON circle-layer marker
 *  • Listening to MapReady / StyleLoaded / CameraIdle events
 */
using System.Windows;
using System.Windows.Input;

namespace WpfExample;

public partial class MainWindow : Window
{
    private bool   _markerVisible;
    private bool _firstStyleLoad = true;
    private double _currentZoom = 9;

    // ── Preset styles (same set as the MAUI sample) ────────────────────────────
    private static readonly Dictionary<string, string> Styles = new()
    {
        ["MapLibre Demo"]    = "https://demotiles.maplibre.org/style.json",
        ["OpenFreeMap Lib."] = "https://tiles.openfreemap.org/styles/liberty",
        ["OpenFreeMap Pos."] = "https://tiles.openfreemap.org/styles/positron",
        ["OpenFreeMap Brt."] = "https://tiles.openfreemap.org/styles/bright",
    };

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

    public MainWindow()
    {
        InitializeComponent();
        StylePicker.ItemsSource   = [.. Styles.Keys];
        StylePicker.SelectedIndex = 0;
    }

    // ── Map lifecycle events ───────────────────────────────────────────────────

    private void MapHost_MapReady(object sender, EventArgs e)
        => StatusText.Text = "Map ready — loading style…";

    private void MapHost_StyleLoaded(object sender, EventArgs e)
    {
        var name = StylePicker.SelectedItem as string ?? "custom";
        StatusText.Text = $"Style loaded: {name}.";
        // Centre on Seattle only for the very first load
        if (_firstStyleLoad) { _firstStyleLoad = false; MapHost.CenterOn(47.6062, -122.3321, zoom: 9); }
    }

    private void MapHost_CameraIdle(object sender, EventArgs e)
        => StatusText.Text = $"Camera idle.";

    // ── Style switcher ────────────────────────────────────────────────────────────────────────

    private void StylePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (StylePicker.SelectedItem is not string name) return;
        if (!Styles.TryGetValue(name, out var url)) return;
        UrlEntry.Text    = url;
        MapHost.StyleUrl = url;
        StatusText.Text  = $"Loading style: {name}…";
    }

    private void BtnApplyUrl_Click(object sender, RoutedEventArgs e) => ApplyCustomUrl();

    private void UrlEntry_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) ApplyCustomUrl();
    }

    private void ApplyCustomUrl()
    {
        var url = UrlEntry.Text?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        StylePicker.SelectedIndex = -1;  // clear preset selection
        MapHost.StyleUrl = url;
        StatusText.Text  = "Loading custom style…";
    }
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
