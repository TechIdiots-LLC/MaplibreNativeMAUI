# MaplibreNativeMAUI

[![License](https://img.shields.io/badge/License-BSD_2--Clause-blue.svg)](/LICENSE)
[![CI](https://github.com/TechIdiots-LLC/MaplibreNativeMAUI/actions/workflows/ci.yml/badge.svg)](https://github.com/TechIdiots-LLC/MaplibreNativeMAUI/actions/workflows/ci.yml)

_.NET MAUI library for rendering interactive maps with [MapLibre Native](https://github.com/maplibre/maplibre-native) on Android, iOS, macCatalyst, and Windows._

---

## Architecture

This library takes a **pure C ABI** approach rather than wrapping the platform-native MapLibre SDKs:

```
MapLibre Native (C++)
       │
       ▼
mln-cabi  (C++ native library — flat C ABI)
       │  P/Invoke
       ▼
MapLibreNative.Maui  (C# typed wrappers: MbglMap, MbglStyle, MbglFrontend …)
       │
       ▼
MapLibreNative.Maui.Handlers  (MAUI controls, handlers, sources, layers)
```

The `mln-cabi` native library is compiled per-platform:

| Platform | Renderer | CI |
|---|---|---|
| Android | OpenGL ES (EGL + ANativeWindow) | `native-android.yml` |
| Android | Vulkan | `native-android-vulkan.yml` |
| iOS / macCatalyst | Metal (MTKView) | `native-apple.yml` |
| Windows | OpenGL (WGL) | `native-windows.yml` |
| Windows | Vulkan | `native-windows-vulkan.yml` |

MapLibre Native is included as a **git submodule** at `dependencies/maplibre-native`.

---

## Getting Started

### Add the map to a page

```xaml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:maplibre="clr-namespace:MapLibreNative.Maui.Handlers;assembly=MapLibreNative.Maui.Handlers"
             xmlns:layers="clr-namespace:MapLibreNative.Maui.Handlers.Layers;assembly=MapLibreNative.Maui.Handlers"
             xmlns:sources="clr-namespace:MapLibreNative.Maui.Handlers.Sources;assembly=MapLibreNative.Maui.Handlers"
             x:Class="MyApp.MainPage">

    <maplibre:MapLibreMap StyleUrl="https://demotiles.maplibre.org/style.json"
                          MyLocationEnabled="True">

        <!-- Sources and layers are declared as child elements -->
        <sources:GeoJsonSource SourceName="my-source" FeatureCollection="{Binding GeoJson}" />
        <layers:LineLayer SourceName="my-source"
                          LayerName="my-line"
                          Properties="{Binding LineProperties}" />
    </maplibre:MapLibreMap>

</ContentPage>
```

### Register the handler in MauiProgram.cs

```csharp
using Maui.MapLibre.Handlers;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMaui()
            .ConfigureMauiHandlers(handlers =>
            {
                handlers.AddMapLibreHandlers();
            });
        return builder.Build();
    }
}
```

---

## MapLibreMap Properties

| Property | Type | Description |
|---|---|---|
| `StyleUrl` | `string` | MapLibre style URL or inline JSON |
| `MinZoom` | `float` | Minimum zoom level |
| `MaxZoom` | `float` | Maximum zoom level |
| `MyLocationEnabled` | `bool` | Show user location dot |
| `MyLocationTrackingMode` | `int` | Location tracking mode |
| `MyLocationRenderMode` | `int` | Location indicator render mode |
| `RotateGesturesEnabled` | `bool` | Enable rotation gesture |
| `ScrollGesturesEnabled` | `bool` | Enable pan gesture |
| `TiltGesturesEnabled` | `bool` | Enable pitch gesture |
| `ZoomGesturesEnabled` | `bool` | Enable pinch-to-zoom |
| `CompassEnabled` | `bool` | Show compass |

### Events (as `ICommand` bindable properties)

| Property | Fired when |
|---|---|
| `MapReadyCommand` | Native map is initialised |
| `StyleLoadedCommand` | Style has finished loading |
| `DidBecomeIdleCommand` | Map has finished all pending operations |
| `CameraMoveCommand` | Camera is moving |
| `CameraIdleCommand` | Camera has stopped |
| `MapClickCommand` | User taps the map (`LatLng`) |
| `MapLongClickCommand` | User long-presses the map (`LatLng`) |
| `UserLocationUpdateCommand` | Device location has changed |

---

## Sources

Declare sources as child elements of `MapLibreMap`, or add them programmatically via the controller.

| XAML type | Description |
|---|---|
| `GeoJsonSource` | Inline GeoJSON `FeatureCollection` or URL |
| `VectorSource` | Vector tile URL or TileJSON |
| `RasterSource` | Raster tile URL or TileJSON |
| `RasterDemSource` | Raster DEM tile source (for hillshade) |
| `ImageSource` | Image overlay bound to `LatLngQuad` coordinates |

```xaml
<sources:GeoJsonSource SourceName="points" FeatureCollection="{Binding PointsJson}" />
<sources:VectorSource SourceName="roads" TileUrl="https://example.com/tiles.json" />
```

---

## Layers

Declare layers as child elements of `MapLibreMap`. Each layer references a `SourceName` and accepts a `Properties` dictionary of [MapLibre style paint/layout properties](https://maplibre.org/maplibre-style-spec/layers/).

| XAML type | MapLibre layer type |
|---|---|
| `FillLayer` | `fill` |
| `LineLayer` | `line` |
| `CircleLayer` | `circle` |
| `SymbolLayer` | `symbol` |
| `RasterLayer` | `raster` |
| `HeatmapLayer` | `heatmap` |
| `FillExtrusionLayer` | `fill-extrusion` |
| `HillshadeLayer` | `hillshade` |
| `ColorReliefLayer` | `color-relief` |

```xaml
<layers:FillLayer SourceName="polygons"
                  LayerName="polygons-fill"
                  Properties="{Binding FillProperties}" />

<layers:LineLayer SourceName="roads"
                  LayerName="roads-line"
                  SourceLayer="transportation"
                  Properties="{Binding LineProperties}" />
```

### Property dictionaries

Properties are a `IDictionary<string, object?>` mapping MapLibre style property names to values or expressions:

```csharp
public IDictionary<string, object?> LineProperties => new Dictionary<string, object?>
{
    ["line-color"] = "#e55e5e",
    ["line-width"] = 3.0,
};
```

---

## Camera

Use the controller (obtained from `MapReadyCommand` or `StyleLoadedCommand`) to manipulate the camera:

```csharp
// Instant jump
controller.JumpTo(latitude: 51.5, longitude: -0.1, zoom: 12);

// Animated ease
controller.EaseTo(51.5, -0.1, zoom: 14, bearing: 0, pitch: 45, durationMs: 800);

// Animated fly-to
controller.FlyTo(51.5, -0.1, zoom: 14, bearing: 0, pitch: 0, durationMs: 1500);

// Fit bounds with padding
controller.SetBounds(latSw: 51.4, lonSw: -0.2, latNe: 51.6, lonNe: 0.0);

// Coordinate conversion
var (x, y) = controller.PixelForLatLng(51.5, -0.1);
var (lat, lon) = controller.LatLngForPixel(x, y);
```

---

## Feature Queries

```csharp
// Query features at a tapped screen position
string? geojson = controller.QueryRenderedFeaturesAtPoint(x, y, layerIds: "my-layer");

// Query features in a bounding box
string? geojson = controller.QueryRenderedFeaturesInBox(x1, y1, x2, y2);
```

The return value is a GeoJSON `FeatureCollection` string, or `null` if the renderer is not ready.

---

## Feature State

Per-feature state lets you change the visual appearance of individual features (e.g. hover effects) without re-loading the style:

```csharp
// Set state — stateJson is a JSON object
controller.SetFeatureState(sourceId: "my-source", featureId: "123", stateJson: "{\"hover\":true}");

// With an explicit source layer (required for vector tile sources)
controller.SetFeatureState("my-source", "123", "{\"hover\":true}", sourceLayerId: "my-layer");

// Read state back (returns JSON string or null)
string? state = controller.GetFeatureState("my-source", "123");

// Remove a single state key
controller.RemoveFeatureState("my-source", featureId: "123", stateKey: "hover");

// Remove all state for a feature
controller.RemoveFeatureState("my-source", featureId: "123");

// Remove all state for every feature in a source
controller.RemoveFeatureState("my-source");
```

---

## Viewport Bounds

```csharp
// Get the lat-lng bounding box of the current camera view
var (latSW, lonSW, latNE, lonNE) = controller.GetVisibleBounds();
```

---

## Memory Management

```csharp
// Ask the renderer to release cached GPU resources
controller.ReduceMemoryUse();

// Write renderer diagnostics to the log
controller.DumpDebugLogs();
```

---

## Generic JSON Sources and Layers

In addition to the typed XAML source/layer elements you can add sources and layers from raw MapLibre style-spec JSON:

```csharp
// Add any source type by spec JSON
controller.AddSourceJson("my-source",
    "{\"type\":\"geojson\",\"data\":{\"type\":\"FeatureCollection\",\"features\": []}}");

// Add any layer type by spec JSON
controller.AddLayerJson(
    "{\"id\":\"my-fill\",\"type\":\"fill\",\"source\":\"my-source\"," +
    "\"paint\":{\"fill-color\":\"#ff0000\",\"fill-opacity\":0.5}}");

// Insert before an existing layer
controller.AddLayerJson(layerJson, beforeLayerId: "labels");
```

---

## Style & Layer Inspection

Once a style is loaded, you can inspect and modify it via the controller:

```csharp
// Enumerate the loaded style
string   url     = controller.GetStyleUrl();
string[] sources = controller.GetStyleSourceIds();
string[] layers  = controller.GetStyleLayerIds();

// Read layer properties (returns JSON-encoded value, or null if not set)
string? color = controller.GetLayerPaintProperty("my-layer", "line-color");
string? vis   = controller.GetLayerLayoutProperty("my-layer", "visibility");

// Show / hide a layer
bool visible = controller.GetLayerVisibility("my-layer");
controller.SetLayerVisibility("my-layer", !visible);
```

---

## Debug Overlays

MapLibre Native has built-in debug overlays controlled by a bitmask:

```csharp
// Enable tile borders + collision boxes
controller.SetDebugOptions(0x02 | 0x10);

// Read current state
int current = controller.GetDebugOptions();

// Disable all
controller.SetDebugOptions(0);
```

The `MbglDebugOptions` enum in `MapLibreNative.Maui` names the individual bits (`TileBorders`, `ParseStatus`, `Timestamps`, `Collision`, `Overdraw`, `StencilClip`, `DepthBuffer`).

---

## WPF Usage

For WPF apps (not MAUI), use `MlnMapHost` from `MapLibreNative.Maui.WPF`:

```xml
xmlns:mlwpf="clr-namespace:MapLibreNative.Maui.WPF;assembly=MapLibreNative.Maui.WPF"

<mlwpf:MlnMapHost x:Name="MapHost"
                  StyleUrl="https://demotiles.maplibre.org/style.json"
                  ShowNavigationControls="True"
                  MapReady="MapHost_MapReady"
                  StyleLoaded="MapHost_StyleLoaded"
                  CameraIdle="MapHost_CameraIdle" />
```

`MlnMapHost` is a `HwndHost` that owns a child HWND rendered with OpenGL (WGL). It supports the same camera, source, layer, and query operations as the MAUI handler. See `sample/WpfExample` for a full working example.

---

## Building from Source

### Prerequisites

- .NET 10 SDK (LTS) — projects multi-target .NET 9 and .NET 10
- CMake ≥ 3.21
- **Android**: Android NDK r26+, `ANDROID_NDK` env var set
- **Apple**: Xcode 15+, macOS host
- **Windows**: Visual Studio 2022 with C++ workload

### Clone with submodules

```sh
git clone --recurse-submodules https://github.com/TechIdiots-LLC/MaplibreNativeMAUI.git
```

Or if you already cloned without submodules:

```sh
git submodule update --init --recursive
```

### Build native library

Each platform's CI workflow documents the exact CMake invocation. The native build output (`libmln-cabi.so` / `libmln-cabi.a` / `mln-cabi.dll`) must be placed under `bindings/` before packing.

```sh
# Example: Windows
cmake -B build/windows -DCMAKE_BUILD_TYPE=Release
cmake --build build/windows --config Release
```

### Build and run the sample

```sh
dotnet build sample/MauiSample.csproj -f net10.0-android
```

---

## License

This project is **BSD 2-Clause** licensed — see [LICENSE](/LICENSE).

| Dependency | License | Notes |
|---|---|---|
| [MapLibre Native](https://github.com/maplibre/maplibre-native) | BSD 2-Clause | Linked natively via `mln-cabi` |
| [maplibre-native-ffi](https://github.com/maplibre/maplibre-native-ffi) | BSD 2-Clause | Reference only — no code included; project structure and C ABI conventions (typed handles, status codes, log callback) informed the design of `mln-cabi` |
| Original [maplibre-maui](https://github.com/btrounson/maplibre-maui) by Benjamin Trounson | MIT | Portions adapted |
| [.NET MAUI](https://github.com/dotnet/maui) (`src/Core/maps`) | MIT | Map primitives in `bindings/Geometry` (`MapSpan`, `Distance`, `GeographyUtils`, `MapType`) adapted from the official MAUI maps source; its handler + property/command mapper pattern also informed the design of `MapLibreNative.Maui.Handlers` |
