using MapLibreNative.Maui.Geometry;
using MapLibreNative.Maui.Handlers;
using Microsoft.Maui.Devices.Sensors;

namespace MauiSample;

public partial class ShapesPage : ContentPage
{
    private readonly ShapesViewModel _vm = new();

    // Circle centred on central London; grow/shrink buttons re-set Radius, which rebuilds the layer.
    private static readonly Location CircleCenter = new(51.5074, -0.1278);
    private double _radiusMeters = 3000;

    public ShapesPage()
    {
        InitializeComponent();
        BindingContext = _vm;

        // A route (Polyline) across central London.
        foreach (var (lat, lon) in new[]
                 {
                     (51.4900, -0.1500), (51.5000, -0.1400),
                     (51.5050, -0.1200), (51.5100, -0.1000),
                 })
            Route.Geopath.Add(new Location(lat, lon));

        // A region (Polygon) north-east of centre.
        foreach (var (lat, lon) in new[]
                 {
                     (51.5200, -0.1000), (51.5300, -0.0800), (51.5150, -0.0600),
                 })
            Region.Geopath.Add(new Location(lat, lon));

        // A radius circle around the centre.
        RadiusCircle.Center = CircleCenter;
        RadiusCircle.Radius = Distance.FromMeters(_radiusMeters);
    }

    private void OnMapReady(object? sender, EventArgs e)
    {
        _vm.Status = "Map ready — polyline, polygon and a 3 km circle";
        var ctrl = (Map.Handler as MapLibreMapHandler)?.Controller;
        ctrl?.JumpTo(51.5074, -0.1278, 11);
    }

    private void OnGrow(object? sender, EventArgs e)   => SetRadius(_radiusMeters + 1000);
    private void OnShrink(object? sender, EventArgs e) => SetRadius(_radiusMeters - 1000);

    private void SetRadius(double meters)
    {
        _radiusMeters = Math.Clamp(meters, 500, 10000);
        RadiusCircle.Radius = Distance.FromMeters(_radiusMeters);
        _vm.Status = $"Circle radius: {_radiusMeters / 1000.0:0.#} km";
    }
}
