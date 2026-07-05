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
       ├───────────────────────────────┬───────────────────────────────┐
       ▼                               ▼                               ▼
MapLibreNative.Maui.Handlers    MapLibreNative.Maui.WPF        (any consumer of the
 (MAUI MapLibreMap + handlers,   (WPF MlnMapImage control)      typed C# bindings)
  sources, layers, overlays;
  Android/iOS/macOS/Windows)
```

Both the MAUI handlers and the WPF control render the map as an **in-tree framework
visual** (WinUI/WPF `Image` fed by `glReadPixels`, Android `TextureView`, iOS/mac `MTKView`)
with their on-map controls as ordinary framework children — see [Surface integration](#surface-integration-how-the-rendered-map-reaches-the-ui).

The `mln-cabi` native library is compiled per-platform:

| Platform | Renderer | CI |
|---|---|---|
| Android | OpenGL ES (EGL + ANativeWindow) | `native-android.yml` |
| Android | Vulkan | `native-android-vulkan.yml` |
| iOS / macCatalyst | Metal (MTKView) | `native-apple.yml` |
| Windows | OpenGL (WGL) | `native-windows.yml` |
| Windows | Vulkan | `native-windows-vulkan.yml` |

MapLibre Native is included as a **git submodule** at `dependencies/maplibre-native`.

### Surface integration (how the rendered map reaches the UI)

On the desktop compositors that enforce *airspace* (WPF and WinUI), the native GL surface and the
map's controls used to float above the framework content (an `HwndHost`/`WS_POPUP` window with the
controls as separate top-level popups). Every platform now renders the map as an ordinary **in-tree
framework visual** with its controls as real framework children. See
[docs/design/in-tree-map-surface.md](docs/design/in-tree-map-surface.md).

| Platform | Map surface | Controls |
|---|---|---|
| WPF | `MlnMapImage` — WPF `Image` backed by a `WriteableBitmap` (pixels transferred each frame via `glReadPixels`) | real WPF children |
| MAUI Windows | WinUI `Image` + `WriteableBitmap` via `glReadPixels` | real XAML children |
| Android | `TextureView` (an ordinary in-tree `View`) | native subviews |
| iOS / macCatalyst | `MTKView` | native subviews |

The old airspace-based paths (WPF `HwndHost`/`MlnMapHost`, MAUI Windows `WS_POPUP`) have been removed.

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
using MapLibreNative.Maui.Handlers;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureMauiHandlers(handlers =>
            {
                handlers.AddHandler(typeof(MapLibreMap), typeof(MapLibreMapHandler));
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
| `ShowNavigationControls` | `bool` | Show the zoom / rotate-pitch d-pad overlay (default `true`) |
| `ShowGpsControl` | `bool` | Show the GPS tracking control overlay (default `true`) |
| `ShowAttributionControl` | `bool` | Show the attribution overlay (default `true`) |
| `CustomAttribution` | `string` | Extra attribution text appended after source-derived attributions |
| `NavigationControlPosition` / `GpsControlPosition` / `AttributionControlPosition` | `MapControlCorner` | Corner each control is anchored to |
| `ItemsSource` / `ItemTemplate` | — | Data-bound overlay elements, see [Data binding](#data-binding-itemssource) |
| `VisibleRegion` | `MapSpan?` | Read-only visible region, refreshed on camera idle |

### Events (as `ICommand` bindable properties)

| Property | Fired when |
|---|---|
| `MapReadyCommand` | Native map is initialised |
| `StyleLoadedCommand` | Style has finished loading |
| `DidBecomeIdleCommand` | Map has finished all pending operations |
| `CameraMoveStartedCommand` | Camera movement has started (reason `int`) |
| `CameraMoveCommand` | Camera is moving |
| `CameraIdleCommand` | Camera has stopped |
| `CameraTrackingChangedCommand` / `CameraTrackingDismissedCommand` | Location-tracking mode changed / was dismissed |
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

### Clustered GeoJSON sources

Pass style-spec GeoJSON source options (clustering etc.) when adding a source
programmatically:

```csharp
// Cluster points within 50px, up to zoom 14
controller.AddGeoJsonSource("aps", featureCollectionJson,
    "{\"cluster\":true,\"clusterRadius\":50,\"clusterMaxZoom\":14}");
```

Cluster features carry `cluster: true`, `cluster_id`, and `point_count` properties.
Drill into a cluster returned by a rendered-features query:

```csharp
double? zoom  = controller.GetClusterExpansionZoom("aps", clusterFeatureJson);
string? kids  = controller.GetClusterChildren("aps", clusterFeatureJson);
string? items = controller.GetClusterLeaves("aps", clusterFeatureJson, limit: 25);
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

`hillshade` layers can be added programmatically via `controller.AddHillshadeLayer(...)`;
`color-relief` (and any other spec layer type) via [`controller.AddLayerJson(...)`](#generic-json-sources-and-layers).

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

## Overlay elements

For the common "just draw a marker / line / shape here" cases you can use the high-level overlay
elements instead of wiring sources and layers by hand. They mirror the
`Microsoft.Maui.Controls.Maps` element model (`Pin`, `Polyline`, `Polygon`, `Circle`) but are
declared as child elements of `MapLibreMap`. Each element materialises itself as a GeoJSON source
plus style layer(s) when the style loads, so it renders **inside** the map surface and composites
correctly on every platform. Changing a property (or mutating a `Geopath` collection) rebuilds it.

| XAML type | Draws | Key properties |
|---|---|---|
| `Pin` | SDF symbol marker (icon + optional label) | `Location`, `Label`, `Address`, `TintColor`/`IconColor`; `MarkerClicked` |
| `Polyline` | Line | `Geopath` (`IList<Location>`), `StrokeColor`, `StrokeWidth` |
| `Polygon` | Filled area | `Geopath`, `FillColor`, `StrokeColor`, `StrokeWidth` |
| `Circle` | Circle of a geographic radius | `Center`, `Radius` (`Distance`), `FillColor`, `StrokeColor`, `StrokeWidth` |

```xaml
xmlns:overlays="clr-namespace:MapLibreNative.Maui.Handlers.Overlays;assembly=MapLibreNative.Maui.Handlers"

<maplibre:MapLibreMap StyleUrl="https://demotiles.maplibre.org/style.json">
    <overlays:Pin Location="{Binding Home}" Label="Home" TintColor="Crimson" />
    <overlays:Circle Center="{Binding Home}" Radius="{Binding Range}"
                     FillColor="#3300A2FF" StrokeColor="#00A2FF" StrokeWidth="2" />
</maplibre:MapLibreMap>
```

`Circle.Radius` uses the ported `MapLibreNative.Maui.Geometry.Distance` (e.g. `Distance.FromMeters(500)`),
and the circle geometry is generated with `GeographyUtils.ToCircumferencePositions`.

`Pin` renders as a `SymbolLayer` backed by an SDF sprite (`mln_marker`), so `IconColor`/`TintColor`
tinting and text labels work out of the box. `MarkerClicked` is exposed but must be wired from the
map's click/feature-query pipeline.

### Data binding (`ItemsSource`)

Instead of declaring overlay children statically you can bind a collection to `MapLibreMap.ItemsSource`
and supply an `ItemTemplate` (or `ItemTemplateSelector`) whose `DataTemplate` produces an overlay
element. Each item becomes that element's `BindingContext`, mirroring
`Microsoft.Maui.Controls.Maps.Map`:

```xaml
<maplibre:MapLibreMap ItemsSource="{Binding Stops}">
    <maplibre:MapLibreMap.ItemTemplate>
        <DataTemplate>
            <overlays:Pin Location="{Binding Coordinate}" Label="{Binding Name}" />
        </DataTemplate>
    </maplibre:MapLibreMap.ItemTemplate>
</maplibre:MapLibreMap>
```

Collections implementing `INotifyCollectionChanged` (e.g. `ObservableCollection<T>`) sync
add/remove/replace/reset automatically. The template must create a `MapOverlayElement` (`Pin`,
`Polyline`, `Polygon`, or `Circle`).

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

// Constrain the camera to a bounding box (optionally with zoom/pitch limits)
controller.SetCameraTargetBounds(new LatLngBounds(
    ne: new LatLng(51.6, 0.0), sw: new LatLng(51.4, -0.2)));

// Fit a region into view (MapSpan overloads of JumpTo / EaseTo / FlyTo)
controller.EaseTo(MapSpan.FromCenterAndRadius(
    new MapCoordinate(51.5, -0.1), Distance.FromKilometers(5)));

// Coordinate conversion
var (x, y) = controller.LatLngToScreenPoint(51.5, -0.1);
LatLng ll  = controller.ScreenPointToLatLng(x, y);

// Edge padding: centre the target in the *unobscured* part of the viewport
// (padding order: top, left, bottom, right, in screen pixels). Useful when a
// panel or overlay covers part of the map. Pass double.NaN for zoom / bearing /
// pitch to keep the current value.
controller.EaseTo(51.5, -0.1, zoom: 14, bearing: 0, pitch: 0,
                  padTop: 0, padLeft: 300, padBottom: 0, padRight: 0);

// Anchored zoom: multiply the map scale (2.0 = one zoom level in),
// optionally about a screen point
controller.ScaleBy(2.0, anchorX: x, anchorY: y, durationMs: 250);
```

The map also exposes the currently visible region for read-back. `MapLibreMap.VisibleRegion`
(a `MapSpan?`) is refreshed whenever the camera becomes idle and raises `PropertyChanged` for data
binding; `GetVisibleRegion()` reads it on demand:

```csharp
if (map.VisibleRegion is { } region)
{
    var center = region.Center;                 // MapCoordinate
    var span = (region.LatitudeDegrees, region.LongitudeDegrees);
}
```

---

## Feature Queries

```csharp
// Query features at a tapped screen position
string? geojson = controller.QueryRenderedFeaturesAtPoint(x, y, layerIds: "my-layer");

// Query features in a bounding box
string? geojson = controller.QueryRenderedFeaturesInBox(x1, y1, x2, y2);

// Query all features in a source's *data*, regardless of visibility
// (sourceLayerIds is required for vector sources, ignored for GeoJSON;
//  filterJson is an optional style-spec filter expression)
string? all = controller.QuerySourceFeatures("my-source",
    sourceLayerIds: null, filterJson: "[\"==\",[\"get\",\"type\"],\"wifi\"]");
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

## Offline Mode

MapLibre's network access can be toggled process-wide. When offline, all network
requests are suspended and only cached resources are served; going back online
resumes queued requests.

```csharp
using MapLibreNative.Maui;

MbglNetwork.Online = false;  // force offline — serve from cache only
MbglNetwork.Online = true;   // resume network access
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

For classic WPF apps (not MAUI), use `MlnMapImage` from `MapLibreNative.Maui.WPF`:

```xml
xmlns:mlwpf="clr-namespace:MapLibreNative.Maui.WPF;assembly=MapLibreNative.Maui.WPF"

<mlwpf:MlnMapImage x:Name="MapHost"
                   StyleUrl="https://demotiles.maplibre.org/style.json"
                   ShowNavigationControls="True"
                   MapReady="MapHost_MapReady"
                   StyleLoaded="MapHost_StyleLoaded"
                   CameraIdle="MapHost_CameraIdle" />
```

`MlnMapImage` renders MapLibre into a WPF `Image` backed by a `WriteableBitmap` (pixels transferred
each frame via `glReadPixels`), so the map is an ordinary WPF visual and its nav / GPS / attribution
controls are real WPF children — no `HwndHost`, no floating `Popup`s, correct z-order/clipping, and
input via normal WPF events. It supports the same camera, source, layer, and query operations as the
MAUI handler. See `sample/WpfExample` for a full working example, and
[docs/design/in-tree-map-surface.md](docs/design/in-tree-map-surface.md) for the design.

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
