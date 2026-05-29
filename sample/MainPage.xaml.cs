using MapLibreNative.Maui.Handlers;

namespace MauiSample;
public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
		BindingContext = new MainPageViewModel();
	}

  protected override void OnAppearing()
  {
    base.OnAppearing();
    DeviceDisplay.Current.KeepScreenOn = true; 
  }
}

