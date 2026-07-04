using System.Text.Json;
using MapLibreNative.Maui.Handlers.EventArgs;
using MapLibreNative.Maui.Handlers.Overlays;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;

namespace MauiSample;

public partial class MarkersPage : ContentPage
{
    private readonly MarkersViewModel _vm = new();

    private static readonly (string Name, double Lat, double Lon)[] Landmarks =
    {
        ("Big Ben",             51.5007,  -0.1246),
        ("Eiffel Tower",        48.8584,   2.2945),
        ("Colosseum",           41.8902,  12.4922),
        ("Christ the Redeemer", -22.9519, -43.2104),
        ("Tokyo Tower",         35.6836, 139.7673),
    };

    public MarkersPage()
    {
        InitializeComponent();
        BindingContext = _vm;

        // Declarative Pin elements: each renders as an SDF marker sprite + text label and stores
        // its label in the feature properties (used by the tap query below).
        foreach (var (name, lat, lon) in Landmarks)
        {
            Map.Add(new Pin
            {
                Location = new Location(lat, lon),
                Label = name,
                TintColor = Color.FromRgb(0xE5, 0x5E, 0x5E),
            });
        }
    }

    private void OnMapReady(object? sender, MapReadyEventArgs e)
    {
        _vm.Status = "Tap a marker to identify it";
    }

    private void OnMapClick(object? sender, MapClickEventArgs e)
    {
        // Query rendered features at the tap; Pins carry their label in the feature properties.
        var json = Map.QueryRenderedFeaturesInBox(e.ScreenX, e.ScreenY, thresholdPx: 12);
        var label = ExtractLabel(json);
        _vm.Status = label != null ? $"Tapped marker: {label}" : "No marker here — tap a red pin";
    }

    private static string? ExtractLabel(string? geojson)
    {
        if (string.IsNullOrEmpty(geojson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(geojson);
            if (!doc.RootElement.TryGetProperty("features", out var features)) return null;
            foreach (var f in features.EnumerateArray())
                if (f.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("label", out var label))
                    return label.GetString();
        }
        catch { /* not a feature collection / no label */ }
        return null;
    }
}
