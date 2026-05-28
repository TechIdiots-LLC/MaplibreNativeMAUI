using CommunityToolkit.Mvvm.ComponentModel;
using Maui.MapLibre.Handlers.Maps;
using Map = Maui.MapLibre.Handlers.Maps.Map;
using Style = Maui.MapLibre.Handlers.Maps.Style;

namespace MauiSample;

public partial class BasicMapViewModel : ObservableObject
{
    // ── Style switcher ─────────────────────────────────────────────────────────

    public static readonly Dictionary<string, string> Styles = new()
    {
        ["MapLibre Demo"]    = "https://demotiles.maplibre.org/style.json",
        ["OpenFreeMap Lib."] = "https://tiles.openfreemap.org/styles/liberty",
        ["OpenFreeMap Pos."] = "https://tiles.openfreemap.org/styles/positron",
        ["OpenFreeMap Brt."] = "https://tiles.openfreemap.org/styles/bright",
    };

    public List<string> StyleNames { get; } = [.. Styles.Keys];

    [ObservableProperty]
    private string _selectedStyleName = "MapLibre Demo";

    [ObservableProperty]
    private string _styleUrl = Styles["MapLibre Demo"];

    [ObservableProperty]
    private string _status = "Loading...";

    partial void OnSelectedStyleNameChanged(string value)
    {
        if (Styles.TryGetValue(value, out var url))
            StyleUrl = url;
    }

    public void OnMapReady(Map _)      => Status = "Map ready — pick a city or switch styles";
    public void OnStyleLoaded(Style _) => Status = $"Style: {SelectedStyleName}";

    /// <summary>Known city destinations: (lat, lon, zoom).</summary>
    public static readonly Dictionary<string, (double Lat, double Lon, double Zoom)> Cities = new()
    {
        ["London"]   = (51.505,  -0.090,  10),
        ["New York"] = (40.712, -74.006,  10),
        ["Tokyo"]    = (35.676, 139.650,  10),
        ["Sydney"]   = (-33.869, 151.209, 10),
    };
}