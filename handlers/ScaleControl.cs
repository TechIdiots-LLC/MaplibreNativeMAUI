namespace MapLibreNative.Maui.Handlers;

/// <summary>The units a <see cref="ScaleControl"/> measures in.</summary>
public enum ScaleUnit
{
    /// <summary>Metres, then kilometres.</summary>
    Metric,
    /// <summary>Feet, then miles.</summary>
    Imperial,
    /// <summary>Nautical miles.</summary>
    Nautical,
}

/// <summary>
/// A scale bar, as maplibre-gl-js's <c>ScaleControl</c>: a bar of a round distance (1, 2 or 5 times a power of ten) no
/// wider than <see cref="MaxBarWidth"/>, measured at the map's centre. Add it to a map as a control:
/// <c>Map.AddControl(new ScaleControl(), MapControlCorner.BottomLeft)</c>.
/// </summary>
public class ScaleControl : ContentView
{
    public static readonly BindableProperty UnitProperty = BindableProperty.Create(
        nameof(Unit), typeof(ScaleUnit), typeof(ScaleControl), ScaleUnit.Metric, propertyChanged: (b, _, _) => ((ScaleControl)b).Refresh());

    public static readonly BindableProperty MaxBarWidthProperty = BindableProperty.Create(
        nameof(MaxBarWidth), typeof(double), typeof(ScaleControl), 100.0, propertyChanged: (b, _, _) => ((ScaleControl)b).Refresh());

    private const double MetresPerFoot = 0.3048, MetresPerMile = 1609.344, MetresPerNauticalMile = 1852;
    private const double EarthCircumference = 40075016.686;   // metres at the equator, WGS84
    private const double TileSize = 512;                      // world size in map pixels at zoom 0

    private readonly Grid _bar;
    private readonly Label _label;
    private MapLibreMap? _map;

    public ScaleControl()
    {
        _label = new Label
        {
            FontSize = 10,
            TextColor = Color.FromArgb("#333333"),
            Padding = new Thickness(5, 0),
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.NoWrap,
        };
        // gl-js's look: a translucent white bar with a dark edge on three sides, open at the top
        var edge = Color.FromArgb("#333333");
        _bar = new Grid
        {
            BackgroundColor = Color.FromRgba(255, 255, 255, 191),
            HeightRequest = 18,
            HorizontalOptions = LayoutOptions.Start,
            Children =
            {
                _label,
                new BoxView { Color = edge, WidthRequest = 2, HorizontalOptions = LayoutOptions.Start },
                new BoxView { Color = edge, WidthRequest = 2, HorizontalOptions = LayoutOptions.End },
                new BoxView { Color = edge, HeightRequest = 2, VerticalOptions = LayoutOptions.End },
            },
        };
        Content = _bar;
        IsVisible = false;   // until there's a map to measure
    }

    /// <summary>The units to measure in (default metric).</summary>
    public ScaleUnit Unit
    {
        get => (ScaleUnit)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>The widest the bar may be, in device-independent units (default 100, as gl-js).</summary>
    public double MaxBarWidth
    {
        get => (double)GetValue(MaxBarWidthProperty);
        set => SetValue(MaxBarWidthProperty, value);
    }

    /// <summary>The distance the bar shows, e.g. "500 m", or empty before the map is ready.</summary>
    public string Text => _label.Text ?? "";

    protected override void OnParentSet()
    {
        base.OnParentSet();
        if (_map != null)
        {
            _map.CameraMove -= OnCamera;
            _map.CameraIdle -= OnCamera;
            _map.MapReady -= OnCamera;
            _map.StyleLoaded -= OnCamera;
            _map.HandlerChanged -= OnCamera;
        }
        _map = Parent as MapLibreMap;
        if (_map != null)
        {
            _map.CameraMove += OnCamera;
            _map.CameraIdle += OnCamera;
            _map.MapReady += OnCamera;
            _map.StyleLoaded += OnCamera;
            _map.HandlerChanged += OnCamera;
        }
        Refresh();
    }

    private void OnCamera(object? sender, System.EventArgs e) => Refresh();

    /// <summary>Measures the map again. The control does this as the camera moves; call it after changes it can't see.</summary>
    public void Refresh()
    {
        if (_map?.Handler is not MapLibreMapHandler handler || !_map.IsStyleLoaded || MaxBarWidth <= 0) { IsVisible = false; return; }
        double zoom, lat;
        try
        {
            zoom = handler.Controller.GetZoom();
            lat = handler.Controller.GetCenter().Latitude;
        }
        catch (Exception) { return; }   // map torn down or not built yet
        if (double.IsNaN(zoom) || double.IsNaN(lat) || Math.Abs(lat) >= 90) { IsVisible = false; return; }

        // Web Mercator: a map pixel covers this much ground at the centre's latitude; UiScale makes it wider than a DIP
        double metresPerMapPixel = EarthCircumference * Math.Cos(lat * Math.PI / 180) / (TileSize * Math.Pow(2, zoom));
        double metresPerDip = metresPerMapPixel / Math.Max(0.01, _map.UiScale);
        double maxMetres = metresPerDip * MaxBarWidth;

        var (unitMetres, unitName) = Unit switch
        {
            ScaleUnit.Imperial when maxMetres >= MetresPerMile => (MetresPerMile, "mi"),
            ScaleUnit.Imperial => (MetresPerFoot, "ft"),
            ScaleUnit.Nautical => (MetresPerNauticalMile, "nm"),
            _ when maxMetres >= 1000 => (1000.0, "km"),
            _ => (1.0, "m"),
        };
        double amount = RoundDown(maxMetres / unitMetres);
        if (amount <= 0) { IsVisible = false; return; }

        double width = Math.Round(amount * unitMetres / metresPerDip);
        string text = $"{amount.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)} {unitName}";
        if (_label.Text != text) _label.Text = text;
        if (_bar.WidthRequest != width) _bar.WidthRequest = width;
        IsVisible = true;
    }

    /// <summary>The largest 1, 2 or 5 times a power of ten that is no more than <paramref name="value"/>.</summary>
    internal static double RoundDown(double value)
    {
        if (!(value > 0) || double.IsInfinity(value)) return 0;
        double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double lead = value / power;
        double step = lead >= 5 ? 5 : lead >= 2 ? 2 : 1;
        return step * power;
    }
}
