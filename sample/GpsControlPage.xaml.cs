using MapLibreNative.Maui.Handlers;

namespace MauiSample;

public partial class GpsControlPage : ContentPage
{
    private readonly GpsControlViewModel _vm;

    public GpsControlPage()
    {
        InitializeComponent();
        _vm = new GpsControlViewModel();
        BindingContext = _vm;

        // Wire GPS updates from the ViewModel into the map controller.
        // UpdateGpsLocation is on IMapLibreMapController, so no platform guard needed.
        _vm.SendGpsUpdate = (lat, lon, bearing, accuracy) =>
        {
            var controller = (Map.Handler as MapLibreMapHandler)?.Controller;
            controller?.UpdateGpsLocation(lat, lon, bearing, accuracy);
        };

        // Centre on Seattle when the style loads so the simulated route is visible.
        Map.StyleLoaded += (_, _) =>
        {
            var controller = (Map.Handler as MapLibreMapHandler)?.Controller;
            controller?.JumpTo(47.6062, -122.3321, zoom: 13);
        };
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        Map.SizeToViewport(height);
    }
}
