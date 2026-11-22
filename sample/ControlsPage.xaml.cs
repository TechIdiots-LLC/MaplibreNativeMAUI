using MapLibreNative.Maui;
using MapLibreNative.Maui.Handlers;

namespace MauiSample;

/// <summary>
/// Controls of the app's own on the map, stacked in the corners with the built-in ones as maplibre-gl-js's
/// addControl does (MapLibreMap.AddControl): a scale bar and a layers button top left, a star below GPS in the top
/// right, a note bottom right. "Units" cycles the scale bar's units, "Move GPS" walks the GPS control round the
/// corners, and "Built-ins" hides and shows the built-in controls; the rest of each stack closes up or makes room.
/// The label shows the insets and the GPS control's modes (MapLibreMap.GpsTrackingMode / GpsBearingMode).
/// </summary>
public partial class ControlsPage : ContentPage
{
    private static readonly MapControlCorner[] Corners =
        [MapControlCorner.TopRight, MapControlCorner.BottomRight, MapControlCorner.BottomLeft, MapControlCorner.TopLeft];
    private int _gpsCorner;

    public ControlsPage()
    {
        InitializeComponent();

        var scale = new ScaleControl();
        Map.AddControl(scale, MapControlCorner.TopLeft);
        Map.AddControl(SmallButton("★", (_, _) => Alert("Star", "A control of the app's own.")));
        Map.AddControl(SmallButton("Layers", (_, _) => Alert("Layers", "Top left, first in its corner.")),
            MapControlCorner.TopLeft);
        Map.AddControl(SmallButton("Units", (_, _) => scale.Unit = (ScaleUnit)(((int)scale.Unit + 1) % 3)),
            MapControlCorner.TopLeft);
        Map.AddControl(SmallButton("Move GPS", (_, _) =>
        {
            _gpsCorner = (_gpsCorner + 1) % Corners.Length;
            Map.GpsControlPosition = Corners[_gpsCorner];
        }), MapControlCorner.TopLeft);
        Map.AddControl(SmallButton("Built-ins", (_, _) =>
        {
            bool show = !Map.ShowNavigationControls;
            Map.ShowNavigationControls = Map.ShowGpsControl = Map.ShowTerrainControl = show;
        }), MapControlCorner.TopLeft);
        Map.AddControl(new Border
        {
            Padding = new Thickness(8, 4),
            BackgroundColor = Color.FromArgb("#E6FFFFFF"),
            StrokeThickness = 0,
            Content = new Label { Text = "A note, bottom right", FontSize = 12, TextColor = Colors.Black },
        }, MapControlCorner.BottomRight);

        Map.ControlInsetsChanged += (_, _) => ShowInsets();
        Map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Map.GpsTrackingMode) or nameof(Map.GpsBearingMode)) ShowInsets();
        };
        ShowInsets();
    }

    private void ShowInsets()
    {
        var i = Map.ControlInsets;
        static string S(Size s) => $"{s.Width:F0}×{s.Height:F0}";
        InsetsLabel.Text = $"Controls take: top left {S(i.TopLeft)}, top right {S(i.TopRight)}\n" +
                           $"bottom left {S(i.BottomLeft)}, bottom right {S(i.BottomRight)}\n" +
                           $"GPS: {Map.GpsTrackingMode}, {Map.GpsBearingMode}";
    }

    // DisplayAlertAsync is new in MAUI 10; the sample still builds for net9.0 too.
#if NET10_0_OR_GREATER
    private Task Alert(string title, string message) => DisplayAlertAsync(title, message, "OK");
#else
    private Task Alert(string title, string message) => DisplayAlert(title, message, "OK");
#endif

    private static Button SmallButton(string text, EventHandler clicked)
    {
        var b = new Button
        {
            Text = text, FontSize = 12, Padding = new Thickness(8, 2), MinimumHeightRequest = 30, HeightRequest = 30,
            BackgroundColor = Colors.White, TextColor = Colors.Black, CornerRadius = 4,
        };
        b.Clicked += clicked;
        return b;
    }
}
