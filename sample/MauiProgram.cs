using MapLibreNative.Maui.Handlers;
using Microsoft.Extensions.Logging;

namespace MauiSample;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			})
            .ConfigureMauiHandlers(handlers =>
            {
                handlers.AddHandler(typeof(MapLibreMap), typeof(MapLibreMapHandler));
            });

#if DEBUG
		builder.Logging.AddDebug();
#endif

#if WINDOWS
		// Use the airspace-free SwapChainPanel renderer instead of the WS_POPUP GL window.
		MapLibreMapController.UseSwapChainPanel = true;
#endif

		return builder.Build();
	}
}
