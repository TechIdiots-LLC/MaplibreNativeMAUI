namespace MauiSample;

/// <summary>
/// Demonstrates toggling 3D terrain on any style with a configurable raster-dem
/// source. Pick a base style and a terrain source (preset or a custom URL), then
/// the button adds that raster-dem source to the current style and toggles terrain
/// (MapLibreMap.ToggleTerrain). This is the pattern an app would use to offer a
/// terrain source setting; terrain works over whatever style is loaded.
/// </summary>
public partial class TerrainPage : ContentPage
{
    // Internal source ID the picked raster-dem is added under.
    private const string TerrainSourceId = "__terrain-dem";
    // Hillshade layer added alongside terrain so the relief is visible — draping
    // displaces geometry by DEM height but reads as almost nothing over flat fills.
    private const string TerrainHillshadeLayerId = "__terrain-hillshade";
    private const float Exaggeration = 1.0f;

    private static readonly Dictionary<string, string> Styles = new()
    {
        ["MapLibre Demo"]    = "https://demotiles.maplibre.org/style.json",
        ["OpenFreeMap Liberty"]  = "https://tiles.openfreemap.org/styles/liberty",
        ["OpenFreeMap Positron"] = "https://tiles.openfreemap.org/styles/positron",
        ["OpenFreeMap Bright"]   = "https://tiles.openfreemap.org/styles/bright",
    };

    // Preset raster-dem (terrain) sources. The custom-URL entry overrides these,
    // mirroring how an app might offer preset or custom terrain sources in settings.
    private static readonly Dictionary<string, string> TerrainSources = new()
    {
        ["Matterhorn (Mapterhorn)"] = "https://tiles.mapterhorn.com/tilejson.json",
    };

    // Whether the picked DEM source has been added to the currently loaded style.
    // Reset on every style load, since reloading the style drops runtime sources.
    private bool _demAdded;

    public TerrainPage()
    {
        InitializeComponent();

        StylePicker.ItemsSource = Styles.Keys.ToList();
        TerrainPicker.ItemsSource = TerrainSources.Keys.ToList();
        TerrainPicker.SelectedIndex = 0;
        TerrainPicker.SelectedIndexChanged += OnTerrainSourceChanged;

        Map.StyleLoaded += (_, _) =>
        {
            // A reloaded style drops runtime sources/layers, so re-add the DEM + hillshade
            // that the on-map ⛰ terrain control toggles.
            _demAdded = false;
            EnsureDemAndHillshade();
            UpdateStatus();
        };

        StylePicker.SelectedIndex = 0; // triggers OnStyleChanged -> loads the first style
    }

    // Adds the picked raster-dem source (+ a hillshade layer so the relief is visible)
    // to the current style if not already there. The built-in terrain control and the
    // button below both toggle terrain on this source.
    private void EnsureDemAndHillshade()
    {
        if (_demAdded) return;
        var url = SelectedTerrainUrl();
        if (string.IsNullOrWhiteSpace(url)) return;
        Map.AddRasterDemSource(TerrainSourceId, url, tileUrlTemplates: null,
                               tileSize: 256, minZoom: 0, maxZoom: 15);
        Map.AddHillshadeLayer(TerrainHillshadeLayerId, TerrainSourceId);
        _demAdded = true;
    }

    // Re-add the DEM under the newly picked source URL so the terrain control uses it.
    private void OnTerrainSourceChanged(object? sender, EventArgs e)
    {
        if (Map.IsTerrainEnabled) Map.RemoveTerrain();
        Map.RemoveLayer(TerrainHillshadeLayerId);
        Map.RemoveSource(TerrainSourceId);
        _demAdded = false;
        EnsureDemAndHillshade();
        UpdateStatus();
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        Map.SizeToViewport(height);
    }

    private void OnStyleChanged(object? sender, EventArgs e)
    {
        if (StylePicker.SelectedItem is string name && Styles.TryGetValue(name, out var url))
            Map.StyleUrl = url;
    }

    // The button below is the programmatic equivalent of the on-map ⛰ terrain control:
    // both toggle terrain on the same pre-added raster-dem source (hillshade stays on).
    private void OnToggleTerrain(object? sender, EventArgs e)
    {
        EnsureDemAndHillshade();
        if (!_demAdded)
        {
            StatusLabel.Text = "Pick or type a terrain source URL first.";
            return;
        }
        Map.ToggleTerrain(TerrainSourceId, Exaggeration);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        bool on = Map.IsTerrainEnabled;
        ToggleButton.Text = on ? "Disable 3D Terrain" : "Enable 3D Terrain";
        StatusLabel.Text = on
            ? $"Terrain: on with hillshade (exaggeration {Exaggeration}) — navigate to the source's coverage and tilt to see it."
            : "Terrain: off";
    }

    // The selected terrain URL: the custom entry wins if non-empty, otherwise the
    // preset picked in TerrainPicker.
    private string? SelectedTerrainUrl()
    {
        var custom = CustomTerrainEntry.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        return TerrainPicker.SelectedItem is string name && TerrainSources.TryGetValue(name, out var url)
            ? url : null;
    }
}
