/**
 * MainWindow.xaml.cs — WPF example using MapLibreNative.Maui.WPF.MlnMapImage.
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
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MapLibreNative.Maui.WPF;

namespace WpfExample;

public partial class MainWindow : Window
{
    private bool   _markerVisible;
    private bool _firstStyleLoad = true;
    private double _currentZoom = 9;

    // ── GPS simulation ─────────────────────────────────────────────────────────
    // A short loop around Seattle. The GPS overlay button on the map (bottom-right)
    // cycles: ○ Off → ⊙ Show → ◎ Follow → ○ Off.  Click it before or after
    // starting the simulation to see how each mode behaves.
    private static readonly (double Lat, double Lon, float Bearing, string Label)[] GpsWaypoints =
    [
        (47.6062, -122.3321,   0f, "Pike Place Market"),
        (47.6089, -122.3380, 310f, "Seattle Centre approach"),
        (47.6116, -122.3493, 270f, "Space Needle"),
        (47.6145, -122.3450,  45f, "Queen Anne"),
        (47.6120, -122.3330,  90f, "South Lake Union"),
        (47.6090, -122.3290, 135f, "Capitol Hill approach"),
        (47.6062, -122.3321, 200f, "Pike Place Market (loop)"),
    ];
    private DispatcherTimer? _gpsTimer;
    private int _gpsWaypointIndex;

    // ── Data-driven circle-color investigation ──────────────────────────────────
    // See https://github.com/TechIdiots-LLC/MaplibreNativeMAUI investigate/runtime-data-driven-circle-color.
    // Reproduces (minimally, without a basemap or vector tiles) a consumer-reported
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

    // Headless terrain smoke test (--terraintest): drives SetTerrain over a style on
    // the offscreen WGL backend and snapshots the frame before/after so we can tell
    // whether draping actually changes the output (or the process dies) on Windows
    // GL, where terrain has never been exercised. Logs to the same _autoTestLogPath.
    private bool _terrainTestRequested;

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

    // ── Preset terrain (raster-dem) sources ────────────────────────────────────
    // The terrain picker is editable, so a custom tilejson/tiles URL can be typed
    // too. This mirrors how a consuming app (e.g. Vistumbler) might offer preset or
    // custom terrain sources in its settings, and lets terrain be toggled on top of
    // whatever style is loaded rather than a dedicated terrain style.
    private static readonly Dictionary<string, string> TerrainSources = new()
    {
        ["Matterhorn (Mapterhorn)"] = "https://tiles.mapterhorn.com/tilejson.json",
    };

    // Internal source ID the toggle adds the picked raster-dem under.
    const string TerrainSourceId = "__terrain-dem";

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
        TerrainPicker.ItemsSource   = TerrainSources.Keys;
        TerrainPicker.SelectedIndex = 0;

        // Data-bound markers: bind an ObservableCollection<MlnMapMarker> to ItemsSource once; the
        // Add City Pins / Clear Pins buttons mutate it and the map updates live.
        MapHost.ItemsSource = _pins;

        var cmdArgs = Environment.GetCommandLineArgs();
        _autoTestRequested = cmdArgs.Contains("--autotest");
        _terrainTestRequested = cmdArgs.Contains("--terraintest");
        if (_autoTestRequested || _terrainTestRequested)
        {
            try { File.Delete(_autoTestLogPath); } catch { /* fine if it didn't exist */ }
            DdLog($"=== {(_terrainTestRequested ? "terraintest" : "autotest")} run started {DateTime.Now:O} ===");
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

        // A reloaded style drops runtime sources/layers, so re-add the DEM + hillshade
        // that the on-map ⛰ terrain control (ShowTerrainControl) toggles.
        _terrainDemAdded = false;
        EnsureTerrainDemAndHillshade();

        if (_terrainTestRequested)
        {
            _terrainTestRequested = false; // run once
            await RunTerrainSmokeTestAsync();
            DdLog("=== terraintest run complete — exiting ===");
            await Task.Delay(500);
            Application.Current.Shutdown();
            return;
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

    /// <summary>
    /// Headless terrain smoke test for the Windows GL (WGL) backend. Enables 3D
    /// terrain over the loaded style with a raster-dem source, tilts the camera, and
    /// snapshots the rendered frame before and after so we can tell whether draping
    /// changed the output (or the process crashed). Terrain has only ever been
    /// exercised on Android GL, so this reproduces the Windows behaviour headlessly.
    /// Snapshots go next to the log as terrain_*.png; stats/GL state go to the log.
    /// </summary>
    private async Task RunTerrainSmokeTestAsync()
    {
        DdLog("--- RunTerrainSmokeTestAsync start ---");
        string dir = Path.GetDirectoryName(_autoTestLogPath)!;

        // Matterhorn — the Mapterhorn DEM's showcase area; strong relief to drape over.
        const double lat = 45.976, lon = 7.658;
        const string demUrl = "https://tiles.mapterhorn.com/tilejson.json";

        try
        {
            // Tilt hard so draping (which displaces geometry by terrain height) is
            // visually distinct from the flat map. 60° is the pitch cap.
            MapHost.JumpTo(lat, lon, zoom: 12, bearing: 0, pitch: 60);
            DdLog($"camera → Matterhorn ({lat},{lon}) z12 pitch60");
            await Task.Delay(3500); // let base tiles load + a few frames render

            SnapshotTo(Path.Combine(dir, "terrain_before.png"), "before (flat, tilted)");

            MapHost.AddRasterDemSource(TerrainSourceId, demUrl);
            DdLog($"AddRasterDemSource({TerrainSourceId}, {demUrl})");
            // Hillshade from the same DEM so the relief is actually visible — draping
            // alone displaces geometry but reads as almost nothing over flat fills.
            MapHost.AddHillshadeLayer("__terrain-hillshade", TerrainSourceId);
            DdLog("AddHillshadeLayer(__terrain-hillshade) from DEM source");
            await Task.Delay(2000); // let DEM tilejson + first DEM tiles fetch

            SnapshotTo(Path.Combine(dir, "terrain_hillshade.png"), "hillshade only (flat, tilted)");

            DdLog($"pre-setTerrain IsTerrainEnabled={MapHost.IsTerrainEnabled}");
            MapHost.ToggleTerrain(TerrainSourceId, 1.5f);
            DdLog($"post-setTerrain IsTerrainEnabled={MapHost.IsTerrainEnabled} (survived the call)");
            await Task.Delay(4000); // let DEM tiles load + drape render

            SnapshotTo(Path.Combine(dir, "terrain_after.png"), "after (hillshade + terrain drape, tilted)");

            // Toggle back off to confirm remove-terrain also survives.
            MapHost.ToggleTerrain(TerrainSourceId, 1.0f);
            DdLog($"after remove IsTerrainEnabled={MapHost.IsTerrainEnabled}");
            await Task.Delay(1500);
        }
        catch (Exception ex)
        {
            DdLog($"terrain smoke test THREW (managed): {ex}");
        }
        DdLog("--- RunTerrainSmokeTestAsync end ---");
    }

    /// <summary>
    /// Snapshots the current rendered frame to <paramref name="path"/> as PNG and logs
    /// a cheap pixel fingerprint (non-background pixel count + average RGB) so two
    /// frames can be compared for "did the render actually change" without opening the
    /// images. Background here is the demo/style clear colour, which we don't know, so
    /// we just report the average and a count of non-uniform pixels.
    /// </summary>
    private void SnapshotTo(string path, string label)
    {
        var bmp = MapHost.SnapshotBitmap();
        if (bmp == null) { DdLog($"snapshot {label}: (null — no frame yet)"); return; }

        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);

        long rs = 0, gs = 0, bs = 0;
        long n = (long)w * h;
        // Count distinct-ish pixels vs the top-left pixel as a rough "content" proxy.
        byte b0 = px[0], g0 = px[1], r0 = px[2];
        long nonBg = 0;
        for (long i = 0; i < n; i++)
        {
            long o = i * 4;
            byte b = px[o], g = px[o + 1], r = px[o + 2];
            bs += b; gs += g; rs += r;
            if (Math.Abs(b - b0) + Math.Abs(g - g0) + Math.Abs(r - r0) > 24) nonBg++;
        }
        DdLog($"snapshot {label}: {w}x{h} avgRGB=({rs / n},{gs / n},{bs / n}) " +
              $"nonUniformPx={nonBg} ({100.0 * nonBg / n:0.0}%) topLeft=({r0},{g0},{b0}) → {Path.GetFileName(path)}");

        try
        {
            using var fs = File.Create(path);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(fs);
        }
        catch (Exception ex) { DdLog($"snapshot save failed: {ex.Message}"); }
    }

    private void MapHost_CameraIdle(object sender, EventArgs e)
    {
        var region = MapHost.VisibleRegion;
        StatusText.Text = region is null
            ? "Camera idle."
            : $"Camera idle \u2014 visible center ({region.Center.Latitude:F3}, {region.Center.Longitude:F3}), " +
              $"span \u00B1{region.LatitudeDegrees / 2:F3}\u00B0, \u00B1{region.LongitudeDegrees / 2:F3}\u00B0";
    }

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

    // Hillshade layer id added alongside terrain so the relief is visible — draping
    // displaces geometry by DEM height but reads as almost nothing over flat fills, so
    // a hillshade from the same DEM makes 3D terrain legible on any style.
    const string TerrainHillshadeLayerId = "__terrain-hillshade";
    private bool _terrainDemAdded;

    // Adds the picked raster-dem source (+ a hillshade layer so the relief is visible)
    // to the current style if not already there. Both the on-map ⛰ terrain control
    // (ShowTerrainControl) and the toolbar button below toggle terrain on this source.
    private void EnsureTerrainDemAndHillshade()
    {
        if (_terrainDemAdded) return;
        var url = SelectedTerrainUrl();
        if (string.IsNullOrWhiteSpace(url)) return;
        MapHost.AddRasterDemSource(TerrainSourceId, url);
        MapHost.AddHillshadeLayer(TerrainHillshadeLayerId, TerrainSourceId);
        _terrainDemAdded = true;
    }

    // The toolbar button is the programmatic equivalent of the on-map ⛰ terrain control:
    // both toggle terrain on the same pre-added raster-dem source (hillshade stays on).
    private void BtnToggleTerrain_Click(object sender, RoutedEventArgs e)
    {
        EnsureTerrainDemAndHillshade();
        if (!_terrainDemAdded)
        {
            StatusText.Text = "Pick or type a terrain source URL first.";
            return;
        }
        MapHost.ToggleTerrain(TerrainSourceId, 1.0f);
        StatusText.Text = MapHost.IsTerrainEnabled
            ? "3D terrain on (with hillshade) — navigate to the source's coverage (e.g. the Alps) and tilt to see relief."
            : "3D terrain off.";
    }

    // The selected terrain URL: a preset name maps to its URL; otherwise the typed
    // text is treated as a custom tilejson/tiles URL.
    private string SelectedTerrainUrl()
    {
        var text = TerrainPicker.Text?.Trim() ?? "";
        return TerrainSources.TryGetValue(text, out var url) ? url : text;
    }

    // ── Data-bound pins (ItemsSource of MlnMapMarker) ──────────────────────

    private readonly ObservableCollection<MlnMapMarker> _pins = new();

    private void BtnAddPins_Click(object sender, RoutedEventArgs e)
    {
        _pins.Clear();
        _pins.Add(new MlnMapMarker(47.6062, -122.3321, "Seattle",  Colors.DodgerBlue));
        _pins.Add(new MlnMapMarker(51.5074,   -0.1278, "London",   Colors.Crimson));
        _pins.Add(new MlnMapMarker(40.7128,  -74.0060, "New York", Colors.SeaGreen));
        _pins.Add(new MlnMapMarker(35.6762,  139.6503, "Tokyo",    Colors.DarkOrange));
        _pins.Add(new MlnMapMarker(-33.8688, 151.2093, "Sydney",   Colors.MediumPurple));
        StatusText.Text = $"Added {_pins.Count} data-bound pins (ItemsSource).";
    }

    private void BtnClearPins_Click(object sender, RoutedEventArgs e)
    {
        _pins.Clear();
        StatusText.Text = "Cleared pins.";
    }

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

        // ── Vector-tile source-layer test ───────────────────────────────────────
        // The GeoJSON cases above never set a source-layer; the real-world failure
        // mode is a circle layer added at runtime against a *vector* source with a
        // source-layer (the AddCircleLayer + SetSourceLayer pattern). Uses the public
        // MapLibre demotiles basemap vector source ("maplibre"), source-layer
        // "centroids" (one point per country) — no external/private tile server.
        //
        // Before the upstream setSourceLayer relayout fix (maplibre-native #4372)
        // this rendered zero features (the source-layer change after addLayer never
        // triggered a tile relayout); after the fix both the literal control and the
        // data-driven (match on the string field NAME) layers render one circle per
        // country centroid.
        try
        {
            MapHost.AddCircleLayer(
                layerName:    "ddtest-vt-match",
                sourceName:   "maplibre",
                belowLayerId: null,
                sourceLayer:  "centroids",
                properties: new Dictionary<string, object?>
                {
                    ["circle-radius"]  = 6.0,
                    ["circle-color"]   = new object[]
                    {
                        "match", new object[] { "get", "NAME" },
                        "Canada", "#00ff00",
                        "#ff0000", // default
                    },
                    ["circle-opacity"] = 1.0,
                });
            DdLog("AddCircleLayer(ddtest-vt-match) sourceLayer=centroids circle-color=match(NAME)");

            MapHost.AddCircleLayer(
                layerName:    "ddtest-vt-literal",
                sourceName:   "maplibre",
                belowLayerId: null,
                sourceLayer:  "centroids",
                properties: new Dictionary<string, object?>
                {
                    ["circle-radius"]  = 6.0,
                    ["circle-color"]   = "#ff0000",
                    ["circle-opacity"] = 1.0,
                });
            DdLog("AddCircleLayer(ddtest-vt-literal) sourceLayer=centroids circle-color=literal (control)");
        }
        catch (Exception ex)
        {
            DdLog($"vector-tile test setup THREW: {ex}");
        }

        // World view so country centroids are on-screen; demotiles maxzoom is 6.
        MapHost.CenterOn(20.0, 0.0, zoom: 2);
        await Task.Delay(4000);

        double cx = MapHost.ActualWidth / 2;
        double cy = MapHost.ActualHeight / 2;
        double threshold = Math.Max(MapHost.ActualWidth, MapHost.ActualHeight); // cover the whole window

        foreach (var layerId in new[] { "ddtest-vt-literal", "ddtest-vt-match" })
        {
            string? json = null;
            string? error = null;
            try { json = MapHost.QueryRenderedFeaturesInBox(cx, cy, threshold, new[] { layerId }); }
            catch (Exception ex) { error = ex.ToString(); }

            int count = CountFeatures(json);
            DdLog($"{layerId}: featureCount={count} error={error ?? "(none)"} json={Truncate(json, 600)}");
        }

        DdLog("--- RunDataDrivenCircleTestAsync end ---");
        await RunClusterTestAsync();
        await RunOfflineTestAsync();
        StatusText.Text = $"Data-driven circle test complete — see {_autoTestLogPath}";
    }

    /// <summary>
    /// Exercises the offline-region pipeline added in cabi 2.2.0: create a small
    /// tile-pyramid region for the demotiles style, download it to completion,
    /// query status, round-trip metadata, delete it, and clear the ambient cache.
    /// Uses its own temp cache database, independent of the map's.
    /// </summary>
    private async Task RunOfflineTestAsync()
    {
        DdLog("--- RunOfflineTestAsync start ---");
        string cachePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maplibre_offline_test_cache.db");
        try { File.Delete(cachePath); } catch { /* fresh DB preferred, stale is fine */ }
        try
        {
            using var mgr = new MapLibreNative.Maui.MbglOfflineManager(cachePath);

            int progressEvents = 0;
            // The observer's Complete flag is the authoritative completion signal —
            // GetRegionStatusAsync can report Complete=false forever when a required
            // resource permanently 404s (demotiles has no 65280-65535 glyph range).
            var completeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            mgr.RegionProgress += p =>
            {
                System.Threading.Interlocked.Increment(ref progressEvents);
                if (p.Complete) completeTcs.TrySetResult();
            };
            mgr.RegionError    += e => DdLog($"offline error: region={e.RegionId} reason={e.Reason} {e.Message}");

            var meta = System.Text.Encoding.UTF8.GetBytes("{\"name\":\"autotest-region\"}");
            var region = await mgr.CreateRegionAsync(
                "https://demotiles.maplibre.org/style.json",
                latSw: 51.4, lonSw: -0.3, latNe: 51.6, lonNe: 0.0,
                minZoom: 0, maxZoom: 2, includeIdeographs: false, metadata: meta);
            DdLog($"created region id={region.Id} type={region.Type} " +
                  $"bounds=[{string.Join(",", region.Bounds ?? Array.Empty<double>())}] " +
                  $"zoom={region.MinZoom}-{region.MaxZoom}");

            mgr.ObserveRegion(region.Id);
            mgr.SetDownloadState(region.Id, true);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.WhenAny(completeTcs.Task, Task.Delay(TimeSpan.FromSeconds(90)));
            var status = await mgr.GetRegionStatusAsync(region.Id);
            DdLog($"download: observerComplete={completeTcs.Task.IsCompleted} statusComplete={status.Complete} " +
                  $"resources={status.CompletedResourceCount}/{status.RequiredResourceCount} " +
                  $"bytes={status.CompletedResourceSize} tiles={status.CompletedTileCount} " +
                  $"precise={status.RequiredResourceCountIsPrecise} progressEvents={progressEvents} " +
                  $"elapsed={sw.Elapsed.TotalSeconds:0.#}s");

            var regions = await mgr.ListRegionsAsync();
            DdLog($"list regions: count={regions.Length} ids=[{string.Join(",", regions.Select(r => r.Id))}]");

            var metaBack = mgr.GetRegionMetadata(region.Id);
            DdLog($"metadata roundtrip: {(metaBack != null ? System.Text.Encoding.UTF8.GetString(metaBack) : "(null)")}");

            await mgr.DeleteRegionAsync(region.Id);
            var after = await mgr.ListRegionsAsync();
            DdLog($"after delete: count={after.Length}");

            await mgr.ClearAmbientCacheAsync();
            await mgr.PackDatabaseAsync();
            DdLog("ambient clear + pack ok");
        }
        catch (Exception ex)
        {
            DdLog($"offline test THREW: {ex}");
        }
        DdLog("--- RunOfflineTestAsync end ---");
    }

    /// <summary>
    /// Exercises the clustered-GeoJSON pipeline added in cabi 2.1.0:
    /// AddGeoJsonSource with options (cluster=true), QuerySourceFeatures,
    /// and the supercluster extension queries (expansion-zoom / leaves).
    /// </summary>
    private async Task RunClusterTestAsync()
    {
        DdLog("--- RunClusterTestAsync start ---");
        try
        {
            // Six points in two tight groups ~0.02° apart; at zoom 3 each group
            // collapses into one cluster.
            const string clusterGeoJson = """
            { "type": "FeatureCollection", "features": [
              { "type": "Feature", "properties": {}, "geometry": { "type": "Point", "coordinates": [-10.00, 10.00] } },
              { "type": "Feature", "properties": {}, "geometry": { "type": "Point", "coordinates": [-10.01, 10.01] } },
              { "type": "Feature", "properties": {}, "geometry": { "type": "Point", "coordinates": [-10.02, 10.02] } },
              { "type": "Feature", "properties": {}, "geometry": { "type": "Point", "coordinates": [ 10.00, 10.00] } },
              { "type": "Feature", "properties": {}, "geometry": { "type": "Point", "coordinates": [ 10.01, 10.01] } },
              { "type": "Feature", "properties": {}, "geometry": { "type": "Point", "coordinates": [ 10.02, 10.02] } }
            ] }
            """;

            MapHost.AddGeoJsonSource("cluster-src", clusterGeoJson,
                """{"cluster":true,"clusterRadius":50,"clusterMaxZoom":14}""");
            DdLog("AddGeoJsonSource(cluster-src, cluster=true, radius=50, maxZoom=14)");

            MapHost.AddCircleLayer(
                layerName:    "cluster-circles",
                sourceName:   "cluster-src",
                belowLayerId: null,
                sourceLayer:  null,
                properties: new Dictionary<string, object?>
                {
                    ["circle-radius"]  = 12.0,
                    ["circle-color"]   = "#ff8800",
                    ["circle-opacity"] = 0.8,
                });

            MapHost.CenterOn(10.0, 0.0, zoom: 3);
            await Task.Delay(2000); // let cluster tiles build and render

            // Rendered query should see 2 clusters (one per group) with cluster
            // properties (cluster=true, point_count=3, cluster_id).
            double cx = MapHost.ActualWidth / 2, cy = MapHost.ActualHeight / 2;
            double threshold = Math.Max(MapHost.ActualWidth, MapHost.ActualHeight);
            string? rendered = MapHost.QueryRenderedFeaturesInBox(cx, cy, threshold, new[] { "cluster-circles" });
            DdLog($"cluster rendered query: featureCount={CountFeatures(rendered)} json={Truncate(rendered, 600)}");

            // QuerySourceFeatures on a clustered source returns the cluster tiles too.
            string? src = MapHost.QuerySourceFeatures("cluster-src");
            DdLog($"QuerySourceFeatures(cluster-src): featureCount={CountFeatures(src)}");

            // Take the first cluster feature and drill in.
            string? clusterFeature = FirstClusterFeature(rendered);
            if (clusterFeature is null)
            {
                DdLog("cluster test: NO cluster feature found in rendered query (FAIL)");
            }
            else
            {
                double? expZoom = MapHost.GetClusterExpansionZoom("cluster-src", clusterFeature);
                string? leaves  = MapHost.GetClusterLeaves("cluster-src", clusterFeature, limit: 10);
                string? kids    = MapHost.GetClusterChildren("cluster-src", clusterFeature);
                DdLog($"GetClusterExpansionZoom={expZoom?.ToString() ?? "(null)"} " +
                      $"leavesCount={CountFeatures(leaves)} childrenCount={CountFeatures(kids)}");
            }
        }
        catch (Exception ex)
        {
            DdLog($"cluster test THREW: {ex}");
        }
        DdLog("--- RunClusterTestAsync end ---");
    }

    /// <summary>Returns the first feature with a truthy "cluster" property from a
    /// FeatureCollection JSON string, serialized back to JSON, or null.</summary>
    private static string? FirstClusterFeature(string? featureCollectionJson)
    {
        if (string.IsNullOrEmpty(featureCollectionJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(featureCollectionJson);
            if (!doc.RootElement.TryGetProperty("features", out var features)) return null;
            foreach (var f in features.EnumerateArray())
            {
                if (f.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("cluster", out var cluster) &&
                    cluster.ValueKind == System.Text.Json.JsonValueKind.True)
                    return f.GetRawText();
            }
        }
        catch (System.Text.Json.JsonException) { }
        return null;
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

    // ── GPS simulation ─────────────────────────────────────────────────────────

    private void BtnGpsStart_Click(object sender, RoutedEventArgs e)
    {
        if (_gpsTimer != null)
            return; // already running

        _gpsWaypointIndex = 0;
        _gpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _gpsTimer.Tick += GpsTimer_Tick;
        _gpsTimer.Start();

        BtnGpsStart.IsEnabled = false;
        BtnGpsStop.IsEnabled  = true;
        StatusText.Text = "GPS simulation running — tap ○/⊙/◎ on the map to cycle tracking mode.";
    }

    private void BtnGpsStop_Click(object sender, RoutedEventArgs e)
    {
        _gpsTimer?.Stop();
        _gpsTimer = null;

        BtnGpsStart.IsEnabled = true;
        BtnGpsStop.IsEnabled  = false;
        StatusText.Text = "GPS simulation stopped.";
    }

    private void GpsTimer_Tick(object? sender, EventArgs e)
    {
        var wp = GpsWaypoints[_gpsWaypointIndex % GpsWaypoints.Length];
        MapHost.UpdateGpsLocation(wp.Lat, wp.Lon, wp.Bearing, accuracyMeters: 8f);
        StatusText.Text = $"GPS fix #{_gpsWaypointIndex + 1}: {wp.Label}  ({wp.Lat:F4}, {wp.Lon:F4})  bearing={wp.Bearing:F0}°";
        _gpsWaypointIndex++;
    }
}
