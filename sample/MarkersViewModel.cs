using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;

namespace MauiSample;

/// <summary>A landmark bound into the map via <c>ItemsSource</c> + <c>ItemTemplate</c>.</summary>
public sealed class LandmarkItem
{
    public LandmarkItem(string name, double latitude, double longitude, Color color)
    {
        Name = name;
        Location = new Location(latitude, longitude);
        Color = color;
    }

    public string Name { get; }
    public Location Location { get; }
    public Color Color { get; }
}

public partial class MarkersViewModel : ObservableObject
{
    // The original five landmarks, restored by Reset.
    private static readonly (string Name, double Lat, double Lon)[] Seed =
    {
        ("Big Ben",             51.5007,  -0.1246),
        ("Eiffel Tower",        48.8584,   2.2945),
        ("Colosseum",           41.8902,  12.4922),
        ("Christ the Redeemer", -22.9519, -43.2104),
        ("Tokyo Tower",         35.6836, 139.7673),
    };

    private static readonly Color PinColor = Color.FromRgb(0xE5, 0x5E, 0x5E);

    private readonly Random _rng = new();
    private int _added;

    [ObservableProperty]
    private string _styleUrl = "https://demotiles.maplibre.org/style.json";

    [ObservableProperty]
    private string _status = "Ready";

    [ObservableProperty]
    private string _regionText = "Visible region: —";

    /// <summary>
    /// Bound to <c>MapLibreMap.ItemsSource</c>; each item is templated into a <c>Pin</c>. Because this
    /// is an <see cref="ObservableCollection{T}"/>, Add/Remove/Reset update the map live.
    /// </summary>
    public ObservableCollection<LandmarkItem> Landmarks { get; } = new();

    public MarkersViewModel() => Reset();

    [RelayCommand]
    private void AddMarker()
    {
        // Drop a random pin somewhere over the globe to show live collection sync.
        double lat = _rng.NextDouble() * 140 - 70;   // -70..70
        double lon = _rng.NextDouble() * 360 - 180;  // -180..180
        Landmarks.Add(new LandmarkItem($"Pin {++_added}", lat, lon, Colors.DodgerBlue));
        Status = $"Added Pin {_added} — {Landmarks.Count} markers";
    }

    [RelayCommand]
    private void RemoveLast()
    {
        if (Landmarks.Count == 0) return;
        var last = Landmarks[^1];
        Landmarks.RemoveAt(Landmarks.Count - 1);
        Status = $"Removed {last.Name} — {Landmarks.Count} markers";
    }

    [RelayCommand]
    private void Reset()
    {
        Landmarks.Clear();
        foreach (var (name, lat, lon) in Seed)
            Landmarks.Add(new LandmarkItem(name, lat, lon, PinColor));
        _added = 0;
        Status = "Tap a marker to identify it";
    }
}
