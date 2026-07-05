using CommunityToolkit.Mvvm.ComponentModel;

namespace MauiSample;

public partial class OfflineViewModel : ObservableObject
{
    [ObservableProperty]
    private string _status = "Ready — pan/zoom to an area, then download it.";
}
