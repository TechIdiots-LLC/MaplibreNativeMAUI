# MapLibreNative.Maui

.NET MAUI and WPF bindings for [maplibre-native](https://github.com/maplibre/maplibre-native), backed by a thin C ABI (`mln-cabi`).

## Packages

| Package | Description |
|---|---|
| `MapLibreNative.Maui` | Core P/Invoke bindings to `mln-cabi.dll` — `MbglMap`, `MbglStyle`, `NativeMethods`, etc. |
| `MapLibreNative.Maui.Vulkan` | Drop-in replacement for `MapLibreNative.Maui` that ships Vulkan-enabled (Windows/Android) and Metal-enabled (iOS/macCatalyst) native DLLs — same C# API, different renderer |
| `MapLibreNative.Maui.Handlers` | .NET MAUI `MapLibreMap` view + handlers for Android, iOS, macCatalyst and Windows |
| `MapLibreNative.Maui.WPF` | WPF `MlnMapImage` — an in-tree `WriteableBitmap`-backed map control for classic WPF apps |

## Quick Start — MAUI

```csharp
// MauiProgram.cs
using MapLibreNative.Maui.Handlers;

builder.UseMapLibre();
```

```xml
<!-- MainPage.xaml -->
xmlns:maplibre="clr-namespace:MapLibreNative.Maui.Handlers;assembly=MapLibreNative.Maui.Handlers"

<maplibre:MapLibreMap StyleUrl="https://demotiles.maplibre.org/style.json" />
```

## Quick Start — WPF

```xml
<!-- MainWindow.xaml -->
xmlns:mlwpf="clr-namespace:MapLibreNative.Maui.WPF;assembly=MapLibreNative.Maui.WPF"

<mlwpf:MlnMapImage x:Name="MapHost" StyleUrl="https://demotiles.maplibre.org/style.json" />
```

## API Reference

See the [API Reference](xref:MapLibreNative.Maui.Handlers.MapLibreMap) for full documentation of all public types.
