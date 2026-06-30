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
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace WpfExample;

public partial class MainWindow : Window
{
    private bool   _markerVisible;
    private bool _firstStyleLoad = true;
    private double _currentZoom = 9;

    // ── Data-driven circle-color investigation ──────────────────────────────────
    // See https://github.com/acalcutt/maplibre-maui investigate/runtime-data-driven-circle-color.
    // Reproduces (minimally, without a basemap or vector tiles) the VistumblerCS
    // symptom: a circle-color value that depends on a per-feature property renders
    // zero features when added at RUNTIME (AddGeoJsonSource + AddCircleLayer after
    // the style is already loaded), even though the identical property+stops/case/
    // match JSON is proven to render correctly by dependencies/maplibre-native's
    // own render-test suite (metrics/integration/render-tests/circle-color/*),
    // which constructs the whole style — sources and layers together — as one
    // document at map-creation time rather than mutating it afterward.
    private readonly string _autoTestLogPath =
        Path.Combine(Path.GetTempPath(), "maplibre_datadriven_test.log");
    private bool _autoTestRequested;

    private const string DdTestSourceId = "ddtest-src";
    private const string DdTestGeoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "properties": { "category": 1 },
              "geometry": { "type": "Point", "coordinates": [-20, 0] } },
            { "type": "Feature", "properties": { "category": 2 },
              "geometry": { "type": "Point", "coordinates": [0, 0] } },
            { "type": "Feature", "properties": { "category": 3 },
              "geometry": { "type": "Point", "coordinates": [20, 0] } }
          ]
        }
        """;

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
        StylePicker.ItemsSource   = Styles.Keys;
        StylePicker.SelectedIndex = 0;

        _autoTestRequested = Environment.GetCommandLineArgs().Contains("--autotest");
        if (_autoTestRequested)
        {
            try { File.Delete(_autoTestLogPath); } catch { /* fine if it didn't exist */ }
            DdLog($"=== autotest run started {DateTime.Now:O} ===");
        }
    }

    // ── Map lifecycle events ───────────────────────────────────────────────────

    private void MapHost_MapReady(object sender, EventArgs e)
        => StatusText.Text = "Map ready — loading style…";

    private async void MapHost_StyleLoaded(object sender, EventArgs e)
    {
        var name = StylePicker.SelectedItem as string ?? "custom";
        StatusText.Text = $"Style loaded: {name}.";
        // Centre on Seattle only for the very first load
        if (_firstStyleLoad)
        {
            _firstStyleLoad = false;
            MapHost.CenterOn(47.6062, -122.3321, zoom: 9);
        }

        if (_autoTestRequested)
        {
            _autoTestRequested = false; // run once
            await RunDataDrivenCircleTestAsync();
            DdLog("=== autotest run complete — exiting ===");
            await Task.Delay(500);
            Application.Current.Shutdown();
        }
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

    // ── Data-driven circle-color investigation ──────────────────────────────────

    private async void BtnDataDrivenTest_Click(object sender, RoutedEventArgs e)
    {
        BtnDataDrivenTest.IsEnabled = false;
        try { await RunDataDrivenCircleTestAsync(); }
        finally { BtnDataDrivenTest.IsEnabled = true; }
    }

    /// <summary>
    /// Adds one shared GeoJSON source (3 points, "category": 1/2/3) at runtime,
    /// then four circle layers reading from it — a literal-color control, and
    /// three feature-dependent circle-color forms (property+stops, case, match) —
    /// and reports how many features QueryRenderedFeaturesInBox finds for each.
    /// Results go to StatusText, Debug.WriteLine, and %TEMP%\maplibre_datadriven_test.log.
    /// </summary>
    private async Task RunDataDrivenCircleTestAsync()
    {
        DdLog("--- RunDataDrivenCircleTestAsync start ---");
        StatusText.Text = "Running data-driven circle-color test…";

        // World view centred on the three test points (-20,0)/(0,0)/(20,0).
        MapHost.CenterOn(0, 0, zoom: 3);
        await Task.Delay(500); // let the camera move land before adding layers

        MapHost.AddGeoJsonSource(DdTestSourceId, DdTestGeoJson);
        DdLog($"AddGeoJsonSource({DdTestSourceId}) — {DdTestGeoJson.Replace("\n", " ").Replace("  ", "")}");

        AddDdLayer("ddtest-literal", "#ff0000"); // control: ignores category entirely

        AddDdLayer("ddtest-stops", new Dictionary<string, object?>
        {
            ["property"] = "category",
            ["stops"] = new object[]
            {
                new object[] { 1, "#ff0000" },
                new object[] { 2, "#00ff00" },
                new object[] { 3, "#0000ff" },
            },
        });

        AddDdLayer("ddtest-case", new object[]
        {
            "case",
            new object[] { "==", new object[] { "get", "category" }, 1 }, "#ff0000",
            new object[] { "==", new object[] { "get", "category" }, 2 }, "#00ff00",
            "#0000ff",
        });

        AddDdLayer("ddtest-match", new object[]
        {
            "match", new object[] { "get", "category" },
            1, "#ff0000",
            2, "#00ff00",
            "#0000ff",
        });

        // Let tiles/buckets build and a frame render before querying — still on the
        // world view, so the GeoJSON test points are on-screen.
        await Task.Delay(2000);
        double cxGj = MapHost.ActualWidth / 2;
        double cyGj = MapHost.ActualHeight / 2;
        double thresholdGj = Math.Max(MapHost.ActualWidth, MapHost.ActualHeight);
        foreach (var layerId in new[] { "ddtest-literal", "ddtest-stops", "ddtest-case", "ddtest-match" })
        {
            string? json = null;
            string? error = null;
            try { json = MapHost.QueryRenderedFeaturesInBox(cxGj, cyGj, thresholdGj, new[] { layerId }); }
            catch (Exception ex) { error = ex.ToString(); }
            DdLog($"{layerId}: featureCount={CountFeatures(json)} error={error ?? "(none)"} json={Truncate(json, 600)}");
        }

        // ── Real-world comparison: the actual WifiDB vector-tile source VistumblerCS
        // uses, instead of a local GeoJSON source. Same property+stops circle-color
        // on "sectype" (1/2/3), same runtime AddVectorSourceUrl + AddCircleLayer
        // pattern. Network-dependent — wrapped so a fetch failure doesn't abort the
        // GeoJSON results above.
        try
        {
            MapHost.AddVectorSourceUrl("ddtest-vt-src", "https://wifidb.net/api/tilejson.php?bucket=daily");
            DdLog("AddVectorSourceUrl(ddtest-vt-src) -> https://wifidb.net/api/tilejson.php?bucket=daily");
            MapHost.AddCircleLayer(
                layerName:    "ddtest-vt-stops",
                sourceName:   "ddtest-vt-src",
                belowLayerId: null,
                sourceLayer:  "daily",
                properties: new Dictionary<string, object?>
                {
                    ["circle-radius"] = 30.0,
                    ["circle-color"] = new Dictionary<string, object?>
                    {
                        ["property"] = "sectype",
                        ["stops"] = new object[]
                        {
                            new object[] { 1, "#ff0000" },
                            new object[] { 2, "#00ff00" },
                            new object[] { 3, "#0000ff" },
                        },
                    },
                    ["circle-opacity"] = 1.0,
                });
            DdLog("AddCircleLayer(ddtest-vt-stops) sourceLayer=daily circle-color=property+stops(sectype)");

            MapHost.AddCircleLayer(
                layerName:    "ddtest-vt-literal",
                sourceName:   "ddtest-vt-src",
                belowLayerId: null,
                sourceLayer:  "daily",
                properties: new Dictionary<string, object?>
                {
                    ["circle-radius"]  = 30.0,
                    ["circle-color"]   = "#ff0000",
                    ["circle-opacity"] = 1.0,
                });
            DdLog("AddCircleLayer(ddtest-vt-literal) sourceLayer=daily circle-color=literal (control)");
        }
        catch (Exception ex)
        {
            DdLog($"vector-tile test setup THREW: {ex}");
        }

        // Centre on a region likely to actually have WifiDB daily data, and give the
        // network fetch + tile parse + render noticeably longer than the local-geojson case.
        MapHost.CenterOn(42.3601, -71.0589, zoom: 8); // Boston, MA
        await Task.Delay(6000);

        double cx = MapHost.ActualWidth / 2;
        double cy = MapHost.ActualHeight / 2;
        double threshold = Math.Max(MapHost.ActualWidth, MapHost.ActualHeight); // cover the whole window

        foreach (var layerId in new[] { "ddtest-vt-literal", "ddtest-vt-stops" })
        {
            string? json = null;
            string? error = null;
            try { json = MapHost.QueryRenderedFeaturesInBox(cx, cy, threshold, new[] { layerId }); }
            catch (Exception ex) { error = ex.ToString(); }

            int count = CountFeatures(json);
            DdLog($"{layerId}: featureCount={count} error={error ?? "(none)"} json={Truncate(json, 600)}");
        }

        DdLog("--- RunDataDrivenCircleTestAsync end ---");
        StatusText.Text = $"Data-driven circle test complete — see {_autoTestLogPath}";
    }

    private void AddDdLayer(string layerId, object? circleColor)
    {
        try
        {
            MapHost.AddCircleLayer(
                layerName:    layerId,
                sourceName:   DdTestSourceId,
                belowLayerId: null,
                sourceLayer:  null,
                properties: new Dictionary<string, object?>
                {
                    ["circle-radius"]  = 30.0, // large + literal: keep radius out of the equation
                    ["circle-color"]   = circleColor,
                    ["circle-opacity"] = 1.0,
                });
            DdLog($"AddCircleLayer({layerId}) circle-color={JsonSerializer.Serialize(circleColor)}");
        }
        catch (Exception ex)
        {
            DdLog($"AddCircleLayer({layerId}) THREW: {ex}");
        }
    }

    private static int CountFeatures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) return root.GetArrayLength();
            if (root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
                return features.GetArrayLength();
            return 0;
        }
        catch { return -1; } // malformed JSON — distinguishable from a genuine zero
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "(null)" : s.Length <= max ? s : s[..max] + "…";

    private void DdLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        System.Diagnostics.Debug.WriteLine($"[ddtest] {line}");
        try { File.AppendAllText(_autoTestLogPath, line + Environment.NewLine); } catch { /* best-effort */ }
    }
}
