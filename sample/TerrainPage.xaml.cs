namespace MauiSample;

/// <summary>
/// Demonstrates the runtime 3D terrain toggle (MapLibreMap.ToggleTerrain). The
/// style carries an OSM raster basemap and a hillshade layer over a Mapterhorn
/// raster-dem source but no terrain, so the map starts flat; the button drapes
/// it over 3D terrain using that same DEM source. Mirrors the maplibre-gl-js
/// TerrainControl (a button that flips setTerrain on/off).
/// </summary>
public partial class TerrainPage : ContentPage
{
    // The raster-dem source ID the toggle enables terrain from. It is already in
    // the style below and shared with the hillshade layer.
    private const string TerrainSourceId = "mapterhorn";
    private const float Exaggeration = 1.0f;

    public TerrainPage()
    {
        InitializeComponent();
        Map.StyleUrl = StyleJson; // StyleUrl accepts a JSON string (routed to SetStyleJson)
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        Map.SizeToViewport(height);
    }

    private void OnToggleTerrain(object? sender, EventArgs e)
    {
        Map.ToggleTerrain(TerrainSourceId, Exaggeration);

        bool on = Map.IsTerrainEnabled;
        ToggleButton.Text = on ? "Disable 3D Terrain" : "Enable 3D Terrain";
        StatusLabel.Text = on ? $"Terrain: on (exaggeration {Exaggeration})" : "Terrain: off";
    }

    // OSM raster + hillshade over the Mapterhorn DEM, pitched over Innsbruck. No
    // "terrain" root property: the map starts flat and the button toggles it.
    private const string StyleJson = """
        {
          "version": 8,
          "center": [11.39085, 47.27574],
          "zoom": 12,
          "pitch": 60,
          "sources": {
            "osm": {
              "type": "raster",
              "tiles": ["https://tile.openstreetmap.org/{z}/{x}/{y}.png"],
              "tileSize": 256,
              "attribution": "© OpenStreetMap Contributors",
              "maxzoom": 19
            },
            "mapterhorn": {
              "type": "raster-dem",
              "url": "https://tiles.mapterhorn.com/tilejson.json",
              "encoding": "terrarium"
            }
          },
          "layers": [
            { "id": "osm", "type": "raster", "source": "osm" },
            {
              "id": "hills",
              "type": "hillshade",
              "source": "mapterhorn",
              "paint": { "hillshade-shadow-color": "#473B24" }
            }
          ]
        }
        """;
}
