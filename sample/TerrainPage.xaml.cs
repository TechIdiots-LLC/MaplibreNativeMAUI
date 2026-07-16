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

        Map.StyleLoaded += (_, _) =>
        {
            _demAdded = false; // the reloaded style has no runtime DEM source or terrain
            UpdateStatus();
        };

        StylePicker.SelectedIndex = 0; // triggers OnStyleChanged -> loads the first style
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

    private void OnToggleTerrain(object? sender, EventArgs e)
    {
        // Turning terrain ON: add the picked raster-dem source to the current style
        // first (once per style load), so terrain works on whatever style is loaded,
        // plus a hillshade layer from that DEM so the relief is actually visible.
        if (!Map.IsTerrainEnabled)
        {
            var url = SelectedTerrainUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                StatusLabel.Text = "Pick or type a terrain source URL first.";
                return;
            }
            if (!_demAdded)
            {
                Map.AddRasterDemSource(TerrainSourceId, url, tileUrlTemplates: null,
                                       tileSize: 256, minZoom: 0, maxZoom: 15);
                _demAdded = true;
            }
            Map.AddHillshadeLayer(TerrainHillshadeLayerId, TerrainSourceId);
        }
        else
        {
            Map.RemoveLayer(TerrainHillshadeLayerId);
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
