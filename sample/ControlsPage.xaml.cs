using MapLibreNative.Maui;
using MapLibreNative.Maui.Handlers;

namespace MauiSample;

/// <summary>
/// Controls of the app's own on the map, stacked in the corners with the built-in ones as maplibre-gl-js's
/// addControl does (MapLibreMap.AddControl): a star below GPS in the top right, a layers button top left, a note
/// bottom right. "Move GPS" walks the GPS control round the corners, and "Built-ins" hides and shows the built-in
/// controls; the rest of each stack closes up or makes room.
/// </summary>
public partial class ControlsPage : ContentPage
{
    private static readonly MapControlCorner[] Corners =
        [MapControlCorner.TopRight, MapControlCorner.BottomRight, MapControlCorner.BottomLeft, MapControlCorner.TopLeft];
    private int _gpsCorner;

    public ControlsPage()
    {
        InitializeComponent();

        Map.AddControl(SmallButton("★", (_, _) => DisplayAlertAsync("Star", "A control of the app's own.", "OK")));
        Map.AddControl(SmallButton("Layers", (_, _) => DisplayAlertAsync("Layers", "Top left, first in its corner.", "OK")),
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
        ShowInsets();
    }

    private void ShowInsets()
    {
        var i = Map.ControlInsets;
        static string S(Size s) => $"{s.Width:F0}×{s.Height:F0}";
        InsetsLabel.Text = $"Controls take: top left {S(i.TopLeft)}, top right {S(i.TopRight)}\n" +
                           $"bottom left {S(i.BottomLeft)}, bottom right {S(i.BottomRight)}";
    }

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
