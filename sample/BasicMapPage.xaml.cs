using Maui.MapLibre.Handlers;

namespace MauiSample;

public partial class BasicMapPage : ContentPage
{
    private readonly BasicMapViewModel _vm;

    public BasicMapPage()
    {
        InitializeComponent();
        _vm = new BasicMapViewModel();
        BindingContext = _vm;

        Map.MapReady    += (_, e) => _vm.OnMapReady(e.Map);
        Map.StyleLoaded += (_, e) => _vm.OnStyleLoaded(e.Style);
    }

    private void OnCityClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn) return;
        var city = btn.CommandParameter as string;
        if (city is null || !BasicMapViewModel.Cities.TryGetValue(city, out var pos)) return;

        var controller = (Map.Handler as MapLibreMapHandler)?.Controller;
        controller?.JumpTo(pos.Lat, pos.Lon, pos.Zoom);

        _vm.Status = $"Jumped to {city} ({pos.Lat:F2}, {pos.Lon:F2})";
    }

    private void OnDebugToggled(object sender, ToggledEventArgs e)
    {
        var controller = (Map.Handler as MapLibreMapHandler)?.Controller;
        // TileBorders (0x02) | Collision (0x10)
        controller?.SetDebugOptions(e.Value ? 0x12 : 0);
        _vm.Status = e.Value ? "Debug overlays ON" : "Debug overlays OFF";
    }
}