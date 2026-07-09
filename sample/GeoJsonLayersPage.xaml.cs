using MapLibreNative.Maui.Handlers;

namespace MauiSample;

public partial class GeoJsonLayersPage : ContentPage
{
    private readonly GeoJsonLayersViewModel _vm;
    private bool _polygonAdded;
    private bool _routeVisible;

    // Hyde Park, London — simplified polygon
    private const string ParkPolygonGeoJson = """
        {
          "type": "Feature",
          "geometry": {
            "type": "Polygon",
            "coordinates": [[
              [-0.1794, 51.5073],
              [-0.1543, 51.5073],
              [-0.1543, 51.5136],
              [-0.1794, 51.5136],
              [-0.1794, 51.5073]
            ]]
          }
        }
        """;

    // Serpentine walk route through Hyde Park
    private const string RouteGeoJson = """
        {
          "type": "Feature",
          "geometry": {
            "type": "LineString",
            "coordinates": [
              [-0.1794, 51.5073],
              [-0.1720, 51.5090],
              [-0.1668, 51.5105],
              [-0.1620, 51.5110],
              [-0.1580, 51.5100],
              [-0.1543, 51.5073]
            ]
          }
        }
        """;

    public GeoJsonLayersPage()
    {
        InitializeComponent();
        _vm = new GeoJsonLayersViewModel();
        BindingContext = _vm;

        Map.StyleLoaded += (_, _) =>
        {
            _vm.Status = "Style loaded — tap buttons to add layers";
            // Jump to Hyde Park
            var controller = (Map.Handler as MapLibreMapHandler)?.Controller;
            controller?.JumpTo(51.5073, -0.1668, 13);
        };
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        Map.SizeToViewport(height);
    }

    private void OnAddPolygon(object sender, EventArgs e)
    {
        if (_polygonAdded) { _vm.Status = "Polygon already added"; return; }

        var ctrl = (Map.Handler as MapLibreMapHandler)?.Controller;
        if (ctrl is null) { _vm.Status = "Map not ready"; return; }

        ctrl.AddGeoJsonSource("park-source", ParkPolygonGeoJson);
        ctrl.AddFillLayer("park-fill", "park-source", null, null,
            new Dictionary<string, object?> { ["fill-color"] = "#228B22", ["fill-opacity"] = 0.4 });
        ctrl.AddLineLayer("park-outline", "park-source", null, null,
            new Dictionary<string, object?> { ["line-color"] = "#006400", ["line-width"] = 2.0 });

        _polygonAdded = true;
        _vm.Status = "Hyde Park polygon added";
    }

    private void OnRemovePolygon(object sender, EventArgs e)
    {
        if (!_polygonAdded) { _vm.Status = "No polygon to remove"; return; }

        var ctrl = (Map.Handler as MapLibreMapHandler)?.Controller;
        if (ctrl is null) return;

        ctrl.RemoveLayer("park-fill");
        ctrl.RemoveLayer("park-outline");
        ctrl.RemoveSource("park-source");
        _polygonAdded = false;
        _vm.Status = "Polygon removed";
    }

    private void OnToggleRoute(object sender, EventArgs e)
    {
        var ctrl = (Map.Handler as MapLibreMapHandler)?.Controller;
        if (ctrl is null) { _vm.Status = "Map not ready"; return; }

        if (_routeVisible)
        {
            ctrl.RemoveLayer("route-line");
            ctrl.RemoveSource("route-source");
            _routeVisible = false;
            _vm.Status = "Route removed";
        }
        else
        {
            ctrl.AddGeoJsonSource("route-source", RouteGeoJson);
            ctrl.AddLineLayer("route-line", "route-source", null, null,
                new Dictionary<string, object?>
                {
                    ["line-color"] = "#FF4500",
                    ["line-width"] = 4.0,
                    ["line-dasharray"] = new[] { 2, 1 },
                });
            _routeVisible = true;
            _vm.Status = "Walking route added";
        }
    }
}
