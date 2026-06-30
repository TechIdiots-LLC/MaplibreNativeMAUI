using System.Text;
using System.Text.Json;
using MapLibreNative.Maui.Handlers;

namespace MauiSample;

/// <summary>
/// Mirrors WpfExample's "Run Data-Driven Circle Test" harness on the MAUI side.
///
/// Reproduces the VistumblerCS-reported symptom: a circle layer added at RUNTIME
/// (AddGeoJsonSource/AddVectorSource + AddCircleLayer after the style is already
/// loaded) renders zero features. Two scenarios:
///   1. A local GeoJSON source with literal / property+stops / case / match
///      circle-color forms (no source-layer involved — these always worked).
///   2. A runtime vector-tile source (the public MapLibre demotiles "maplibre"
///      vector source, source-layer "centroids") with a layer added then given
///      its source-layer via SetSourceLayer — the pattern that exposed the real
///      bug: Layer::setSourceLayer() never notified the render orchestrator, so
///      already-loaded tiles never relaid out. Fixed in mln_cabi.cpp's
///      mbgl_layer_set_source_layer (see CHANGELOG 3.2.10) by also touching
///      visibility to force the missing notification.
///
/// Uses only the public demotiles tile server — no external/private tile server.
/// </summary>
public partial class DataDrivenCircleTestPage : ContentPage
{
    private readonly DataDrivenCircleTestViewModel _vm = new();
    private bool _running;

    private const string DdSourceId = "ddtest-src";
    private const string DdGeoJson = """
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

    public DataDrivenCircleTestPage()
    {
        InitializeComponent();
        BindingContext = _vm;
    }

    private void OnMapReady(object? sender, EventArgs e)
    {
        _vm.Status = "Map ready — tap \"Run Test\"";
    }

    private async void OnRunTest(object? sender, EventArgs e)
    {
        if (_running) return;
        _running = true;
        RunTestButton.IsEnabled = false;
        try
        {
            await RunDataDrivenCircleTestAsync();
        }
        finally
        {
            RunTestButton.IsEnabled = true;
            _running = false;
        }
    }

    private async Task RunDataDrivenCircleTestAsync()
    {
        var ctrl = (Map.Handler as MapLibreMapHandler)?.Controller;
        if (ctrl == null) { _vm.Status = "Map not ready"; return; }

        var log = new StringBuilder();
        void Log(string s) { log.AppendLine(s); _vm.Status = log.ToString(); System.Diagnostics.Debug.WriteLine($"[ddtest] {s}"); }

        Log("--- RunDataDrivenCircleTestAsync start ---");

        // World view centred on the three test points (-20,0)/(0,0)/(20,0).
        ctrl.JumpTo(0, 0, 3);
        await Task.Delay(500);

        ctrl.AddGeoJsonSource(DdSourceId, DdGeoJson);
        Log($"AddGeoJsonSource({DdSourceId})");

        AddDdLayer(ctrl, "ddtest-literal", "#ff0000", Log); // control: ignores category entirely

        AddDdLayer(ctrl, "ddtest-stops", new Dictionary<string, object?>
        {
            ["property"] = "category",
            ["stops"] = new object[]
            {
                new object[] { 1, "#ff0000" },
                new object[] { 2, "#00ff00" },
                new object[] { 3, "#0000ff" },
            },
        }, Log);

        AddDdLayer(ctrl, "ddtest-case", new object[]
        {
            "case",
            new object[] { "==", new object[] { "get", "category" }, 1 }, "#ff0000",
            new object[] { "==", new object[] { "get", "category" }, 2 }, "#00ff00",
            "#0000ff",
        }, Log);

        AddDdLayer(ctrl, "ddtest-match", new object[]
        {
            "match", new object[] { "get", "category" },
            1, "#ff0000",
            2, "#00ff00",
            "#0000ff",
        }, Log);

        await Task.Delay(2000);

        var (cxGj, cyGj, thresholdGj) = ScreenCenterAndThreshold();
        foreach (var layerId in new[] { "ddtest-literal", "ddtest-stops", "ddtest-case", "ddtest-match" })
        {
            string? json = TryQuery(Map, cxGj, cyGj, thresholdGj, layerId, out var error);
            Log($"{layerId}: featureCount={CountFeatures(json)} error={error ?? "(none)"}");
        }

        // ── Vector-tile source-layer test ───────────────────────────────────────
        // Same runtime AddVectorSource + AddCircleLayer(sourceLayer:) pattern as
        // VistumblerCS's WifiDB history layers, but against the public demotiles
        // vector source ("maplibre"), source-layer "centroids" (one point per
        // country). Before the mln_cabi setSourceLayer relayout fix this rendered
        // zero features for BOTH the literal control and the data-driven layer;
        // after the fix both render one circle per country centroid.
        try
        {
            ctrl.AddVectorSource("ddtest-vt-src", "https://demotiles.maplibre.org/tiles/tiles.json", null, 0, 0);
            Log("AddVectorSource(ddtest-vt-src) -> https://demotiles.maplibre.org/tiles/tiles.json");

            ctrl.AddCircleLayer("ddtest-vt-literal", "ddtest-vt-src", null, "centroids",
                new Dictionary<string, object?>
                {
                    ["circle-radius"] = 6.0,
                    ["circle-color"] = "#ff0000",
                    ["circle-opacity"] = 1.0,
                });
            Log("AddCircleLayer(ddtest-vt-literal) sourceLayer=centroids circle-color=literal (control)");

            ctrl.AddCircleLayer("ddtest-vt-match", "ddtest-vt-src", null, "centroids",
                new Dictionary<string, object?>
                {
                    ["circle-radius"] = 6.0,
                    ["circle-color"] = new object[]
                    {
                        "match", new object[] { "get", "NAME" },
                        "Canada", "#00ff00",
                        "#ff0000", // default
                    },
                    ["circle-opacity"] = 1.0,
                });
            Log("AddCircleLayer(ddtest-vt-match) sourceLayer=centroids circle-color=match(NAME)");
        }
        catch (Exception ex)
        {
            Log($"vector-tile test setup THREW: {ex}");
        }

        // World view so country centroids are on-screen; demotiles maxzoom is 6.
        ctrl.JumpTo(20.0, 0.0, 2);
        await Task.Delay(4000);

        var (cx, cy, threshold) = ScreenCenterAndThreshold();
        foreach (var layerId in new[] { "ddtest-vt-literal", "ddtest-vt-match" })
        {
            string? json = TryQuery(Map, cx, cy, threshold, layerId, out var error);
            Log($"{layerId}: featureCount={CountFeatures(json)} error={error ?? "(none)"}");
        }

        Log("--- RunDataDrivenCircleTestAsync end ---");
    }

    private (double cx, double cy, double threshold) ScreenCenterAndThreshold()
    {
        double w = Map.Width, h = Map.Height;
        if (w <= 0 || h <= 0) { w = 400; h = 800; } // fallback before first layout pass
        return (w / 2, h / 2, Math.Max(w, h));
    }

    private static string? TryQuery(MapLibreMap map, double cx, double cy, double threshold,
        string layerId, out string? error)
    {
        error = null;
        try { return map.QueryRenderedFeaturesInBox(cx, cy, threshold, layerId); }
        catch (Exception ex) { error = ex.ToString(); return null; }
    }

    private static void AddDdLayer(IMapLibreMapController ctrl, string layerId, object? circleColor, Action<string> log)
    {
        try
        {
            ctrl.AddCircleLayer(layerId, DdSourceId, null, null, new Dictionary<string, object?>
            {
                ["circle-radius"]  = 30.0, // large + literal: keep radius out of the equation
                ["circle-color"]   = circleColor,
                ["circle-opacity"] = 1.0,
            });
            log($"AddCircleLayer({layerId}) circle-color={JsonSerializer.Serialize(circleColor)}");
        }
        catch (Exception ex)
        {
            log($"AddCircleLayer({layerId}) THREW: {ex}");
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
}
