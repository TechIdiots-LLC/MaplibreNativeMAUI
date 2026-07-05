using CommunityToolkit.Mvvm.ComponentModel;

namespace MauiSample;

public partial class ShapesViewModel : ObservableObject
{
    [ObservableProperty]
    private string _styleUrl = "https://demotiles.maplibre.org/style.json";

    [ObservableProperty]
    private string _status = "Ready";
}
