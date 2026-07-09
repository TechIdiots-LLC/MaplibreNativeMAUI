using System.Text.Json;
using MapLibreNative.Maui.Handlers.EventArgs;

namespace MauiSample;

public partial class MarkersPage : ContentPage
{
    private readonly MarkersViewModel _vm = new();

    public MarkersPage()
    {
        InitializeComponent();
        BindingContext = _vm;
        // Pins are now created from the bound ObservableCollection via ItemsSource + ItemTemplate.
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        Map.SizeToViewport(height);
    }

    private void OnMapReady(object? sender, MapReadyEventArgs e)
    {
        _vm.Status = "Tap a marker to identify it";
    }

    private void OnCameraIdle(object? sender, EventArgs e)
    {
        var region = Map.VisibleRegion;
        _vm.RegionText = region is null
            ? "Visible region: —"
            : $"Visible region: center ({region.Center.Latitude:F3}, {region.Center.Longitude:F3}), " +
              $"span ±{region.LatitudeDegrees / 2:F3}°, ±{region.LongitudeDegrees / 2:F3}°";
    }

    private void OnMapClick(object? sender, MapClickEventArgs e)
    {
        // Query rendered features at the tap; Pins carry their label in the feature properties.
        var json = Map.QueryRenderedFeaturesInBox(e.ScreenX, e.ScreenY, thresholdPx: 12);
        var label = ExtractLabel(json);
        _vm.Status = label != null ? $"Tapped marker: {label}" : "No marker here — tap a pin";
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
