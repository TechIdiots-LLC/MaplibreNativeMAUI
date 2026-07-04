# Changelog

## master
### ✨ Features and improvements
- _...Add new stuff here..._

### 🐞 Bug fixes
- _...Add new stuff here..._

## 4.0.0
### ⚠️ Breaking changes
- **WPF: `MlnMapHost` removed** — The old `HwndHost`+`Popup` renderer has been deleted. `MlnMapImage` is now the only WPF map control. Migration: replace `<wpf:MlnMapHost …>` with `<wpf:MlnMapImage …>` in XAML; the public API surface (properties, events, methods) is identical, including the `MapClicked` event and its `MlnMapClickEventArgs` args.
- **MAUI Windows: `WS_POPUP` GL window renderer removed** — The floating `WS_POPUP` renderer is gone; `SwapChainMapView` (in-tree `Image`+`WriteableBitmap`) is now the only Windows path. `MapLibreMapController.UseSwapChainPanel` and `MAPLIBRE_WIN_RENDERER=swapchain` are no longer meaningful.

### ✨ Features and improvements
- **Airspace-free map rendering — WPF (`MlnMapImage`)** — The map is a real WPF `Image` element; nav/GPS/attribution controls are ordinary WPF children with correct z-order, clipping, DPI, and hit-testing. No more floating `Popup` windows, no `HwndHost` airspace, no per-tick overlay realignment. Powered by `glReadPixels(GL_BGRA)` into a `WriteableBitmap` after each frame — see [`docs/design/in-tree-map-surface.md`](docs/design/in-tree-map-surface.md).
- **Airspace-free map rendering — MAUI Windows (`SwapChainMapView`)** — The map is a real WinUI `Image` element; nav/GPS/attribution are real XAML children. The floating `WS_POPUP` window, `PopupWndProc`, and per-tick realignment are gone. Same `glReadPixels`+`WriteableBitmap` approach as WPF, with pixels written via `IBufferByteAccess`.
- **Android: `TextureView` replaces `SurfaceView`** — `TextureView` is an ordinary in-tree `View` with no compositing hole, so MAUI content layers reliably above the map on Android. `SurfaceCallback`/`ISurfaceHolderCallback` replaced by `TextureSurfaceListener`/`ISurfaceTextureListener`.
- **Declarative over-the-map overlay elements** — New `Pin`, `Polyline`, `Polygon`, and `Circle` overlay types modelled after `Microsoft.Maui.Controls.Maps`. Hang off `MapLibreMap.Pins` and `MapLibreMap.MapElements` (`ObservableCollection`s); changes sync to MapLibre style layers automatically. Each compiles to a GeoJSON source + style layer: `Polyline`→`LineLayer`, `Polygon`→`FillLayer`+`LineLayer`, `Pin`→`SymbolLayer`, `Circle`→circumference polygon+`FillLayer`.
- **`Pin` upgrade: `SymbolLayer` + SDF sprite** — `Pin` now renders via a `SymbolLayer` with an SDF sprite (`mln_marker`) instead of a `CircleLayer`. Supports `icon-color` tinting, configurable text labels, and all standard symbol layout/paint properties.
- **`AddSpriteImage` / `RemoveSpriteImage`** — New methods on `IMapLibreMapController` (and `MlnMapImage`) for adding and removing named SDF or raster sprites at runtime, on all platforms.
- **MAUI Windows nav d-pad** — The MAUI Windows navigation panel now has a full 4-way rotate/pitch/compass d-pad (▲/▼ pitch, ◀/▶ rotate, centre reset-north + live compass tick), matching the WPF renderer added in 3.3.0.
- **`SymbolLayerProperties`** — New properties class covering the full symbol layer paint and layout property set.
- **`MapSpan` camera overloads** — New `SetCamera(MapSpan)` and related overloads in `MapLibreNative.Maui` for fitting a geographic bounding box into view.
- **Sample: `ShapesPage`** — New sample page demonstrating `Polyline`, `Polygon`, and `Circle` overlay elements. `MarkersPage` converted to declarative `Pin` elements.
- **Vortice D3D packages removed** — `Vortice.Direct3D9` (WPF), `Vortice.Direct3D11`, and `Vortice.DXGI` (MAUI handlers) are no longer dependencies.

### 🐞 Bug fixes
- **Blank map on WPF and MAUI Windows** — `WGLRenderableResource::bind()` in `platform_frontend_windows.cpp` unconditionally calls `glBindFramebuffer(0)`, so the `WGL_NV_DX_interop2` custom FBOs were never rendered into. Fixed by dropping D3D interop entirely and reading FBO 0 directly with `glReadPixels`.
- **WPF: overlay button clicks not registering** — Hit-testing on nav/GPS `Border` buttons in `MlnMapImage` was blocked by the map `Image` absorbing pointer events. Fixed by routing pointer events correctly through the overlay children.
- **MAUI Windows: renderer crash on startup** — `SwapChainMapView` could throw on the first render tick before `WriteableBitmap` was allocated. Added null guards around `OnRendering` and deferred `Start()` to `View.Loaded`.
- **Nav d-pad rotate: cumulative drift** — Repeated ±15° bearing increments accumulated floating-point error so a full 24-step turn did not return to north. Fixed by snapping the new bearing to the nearest multiple of the increment.
- **MAUI Windows zoom buttons: white squares / tap stealing** — `+`/`−` buttons showed as white squares on some WinUI font configurations (full-width Unicode U+FF0B/FF0D). Replaced with ASCII `+`/`−` at FontSize 18 Bold. Also fixed the map `Image` absorbing taps meant for overlay buttons.
- **ShapesPage crash on geometry update** — Rebuilding a `Polyline`/`Polygon` source on `ObservableCollection.CollectionChanged` disposed the GeoJSON source while MapLibre still referenced it. Fixed by updating the geometry in place instead of removing and re-adding the source.
## 3.3.0
### âœ¨ Features and improvements
- **Navigation d-pad rotate/pitch control (all platforms)** â€” The navigation panel's compass button is replaced by a round 4-way d-pad drawn above the zoom buttons.  The **â–²/â–¼** arrows tilt the map (pitch Â±10Â°, clamped 0â€“60Â°), the **â—€/â–¶** arrows rotate it (bearing Â±15Â°, right = clockwise), and the centre resets the camera to north.  A hollow compass ring around the arrows carries a blue north tick that rotates to track the current map bearing.  Implemented on Windows MAUI (GDI-drawn `WS_POPUP` overlay), WPF (`Popup` with a 3Ã—3 hit grid + `Ellipse` ring), Android (`FrameLayout` with a 3Ã—3 `LinearLayout` grid + `GradientDrawable` ring), and iOS/macOS (`UIView` with framed arrow buttons + a bordered ring).  The d-pad is sized to match the zoom-button width so the navigation and GPS panels line up when stacked in the same corner.  Replaces the previous Windows drag-to-rotate compass with discrete, tappable buttons that work uniformly across desktop and mobile.
- **GPS overlay control (all platforms)** â€” A new GPS panel is drawn as a floating overlay on the map, matching the style and shadow of the navigation and attribution panels.  The panel contains two buttons: a GPS tracking-mode button (top) and a bearing/north-reset button (bottom).  Controlled via a new `ShowGpsControl` bindable property on `MapLibreMap` (default `true`) and a matching `ShowGpsControl` dependency property on `MlnMapHost`.  Implemented on Windows MAUI, WPF, **Android, and iOS/macOS** â€” the mobile controllers build the panel from native views (Android `LinearLayout` of buttons; iOS/macOS `UIStackView`), because `mln-cabi` exposes no built-in platform controls.

- **Configurable control placement + stacking (all platforms)** â€” Navigation, GPS, and attribution overlays can each be anchored to any corner via a new `MapControlCorner` enum (`TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`) and three properties: `NavigationControlPosition`, `GpsControlPosition`, `AttributionControlPosition` (MAUI `BindableProperty`s / WPF `DependencyProperty`s).  Controls that share a corner **stack automatically** in a fixed order (navigation â†’ GPS â†’ attribution): top corners stack downward, bottom corners stack upward.  Works identically on Windows (Win32 `SetWindowPos`), WPF (`Popup` placement), Android (`FrameLayout` gravity + margins), and iOS/macOS (`NSLayoutConstraint` anchors).

- **Default placement: navigation + GPS stacked top-right** â€” By default the navigation and GPS panels now both anchor to the top-right corner and stack, with navigation on top and GPS directly below it; attribution defaults to the bottom-left corner.

- **4-state GPS tracking cycle** â€” The GPS button cycles through four modes on each click:
  - **â—‹ Off** (gray) â€” no location indicator, camera free.
  - **âŠ™ Show** (blue `#1E88E5`) â€” blue accuracy-circle + bearing dot at the last fix; camera remains free.
  - **â—Ž Follow** (deep blue `#0070C5`, light-blue button tint) â€” dot shown and the camera re-centres on every incoming GPS fix (north-up).
  - **â–² FollowBearing** (orange-amber `#F57C00`, warm button tint) â€” dot shown, camera follows location *and* rotates so the current GPS bearing is always "heading up", matching Google Maps turn-by-turn navigation mode.

- **Smart bearing-reset button** â€” The â†‘ button in the GPS panel behaves contextually: in **FollowBearing** mode it drops back to **Follow** (stops heading-up rotation) *and* resets the camera bearing to north in a single animated step; in all other modes it resets bearing to north as before.

- **`IMapLibreMapController.UpdateGpsLocation`** â€” New method on the shared controller interface: `void UpdateGpsLocation(double lat, double lon, float bearing = 0, float accuracyMeters = 10)`.  Implemented on **all platforms**: it drives the location-indicator layer (a blue accuracy circle sized by `accuracyMeters`, plus a bearing dot) and all camera-follow logic.  The method caches the last fix so switching from Offâ†’Show or Offâ†’Follow immediately places the dot without waiting for a new fix.  The mobile controllers reuse the same portable location-indicator style layer (`AddLocationIndicatorLayer`) as Windows.

- **Overlays clamp to the monitor work area (Windows + WPF)** â€” The floating overlay windows/popups no longer render over or behind the Windows taskbar when the map window is dragged partially off-screen; each overlay is clamped to the work area of the monitor under its own intended position (`MonitorFromPoint`), which also keeps overlays following the window correctly across multiple monitors.

- **Attribution always credits MapLibre (all platforms)** â€” The attribution overlay now always includes a "MapLibre" link to <https://maplibre.org/> when no source attribution already references MapLibre, matching the behaviour of maplibre-gl-js.  The link is prepended to the source/custom attributions via a shared `MbglStyle.EnsureMapLibreAttribution` helper used by every platform's attribution builder.

- **MAUI sample: GPS control demo page** â€” New "GPS" tab added to `MauiSample` (`GpsControlPage` + `GpsControlViewModel`).  A `IDispatcherTimer`-driven simulation walks seven waypoints around Seattle every 1.5 s, calling `controller.UpdateGpsLocation()` on each tick.  The page explains the four tracking-mode buttons, includes Start/Stop simulation controls, and adds three live **corner pickers** (Navigation / GPS / Attribution) bound to the map so control placement and stacking can be changed at runtime.  Registered in `AppShell.xaml` between the Markers and DD Test tabs.

- **WPF sample: GPS simulation** â€” `WpfExample` gains a "GPS sim: â–¶ Start / â¹ Stop" toolbar section.  A `DispatcherTimer` steps through the same Seattle waypoint loop, calling `MapHost.UpdateGpsLocation()`, and updates the status bar with the current fix details.  `ShowGpsControl="True"` and explicit `NavigationControlPosition` / `GpsControlPosition` / `AttributionControlPosition` values are set on the `MlnMapHost` element to demonstrate the top-right nav+GPS stacking.

### ðŸž Bug fixes
- **Windows MAUI: map filled only part of the window and overlays were misplaced at high DPI** â€” The `MapLibreMapController` captured `XamlRoot.RasterizationScale` once in its constructor, where it can still read a stale `1.0` before the monitor DPI resolves. At a scale like 225% the GL child window was then sized in logical pixels and filled only `1/scale` of the physical window (with the navigation/GPS/attribution overlays pushed off-screen). The pixel ratio is now refreshed from the live `XamlRoot.RasterizationScale` at initialization (before the frontend/map are created) and on every resize, so the map fills the window and the overlays sit correctly at any DPI, including moving the window between monitors with different scaling.
- **WPF: flickering line along the top edge of the map** â€” `MlnMapHost` sized the child GL window, the `glViewport`, and the mbgl framebuffer with truncation (`(int)(ActualHeight * dpi)`), while WPF's `HwndHost` sizes the hosted window with device-pixel *rounding*.  When the two disagreed by 1 px, the top row of the window was left undrawn and showed the GL clear colour, flickering as the buffer swaps and WPF layout fought over that pixel.  Fixed by driving the viewport and mbgl size from the child window's actual client rect (`GetClientRect`) each frame and rounding (rather than truncating) the window size so it matches WPF's own layout rounding.
- **Nav/GPS overlays not hidden when the map is too small (Windows + WPF)** â€” The corner/stacking refactor dropped the WPF host's fit check entirely and only checked navigation height on Windows.  Both hosts now hide the navigation and GPS panels when the map area can't contain them in width or height (including the stacking offset when nav and GPS share a corner), and re-evaluate the fit on every resize.
- **Windows: hillshade and color-relief layers rendered with grey/white artifacts** â€” The Windows WGL backend was constructed with `ContextMode::Unique`, which causes mbgl's GL state cache to *not* call `setDirtyState()` at the start of each frame.  Because the C# host calls `glBindFramebuffer(0)`, `glViewport`, `glClearColor`, and `glClear` before each `Render()` invocation, mbgl's cached GL state diverged from the actual driver state; on multi-pass effects such as hillshade and color-relief this manifested as missing or incorrect draws.  Fixed by switching to `ContextMode::Shared` in `WGLBackend`, which causes `Context::createCommandEncoder()` to call `setDirtyState()` unconditionally so all blend, stencil, program, and texture state is re-applied each frame.  The existing `assumeFramebufferBinding` / `assumeViewport` calls in `updateAssumedState()` are retained because `setDirtyState()` explicitly skips those (as documented in `context.cpp`).

- **Android/MaciOS: `OnMapClickReceived` and `OnMapLongClickReceived` missing screen coordinates** â€” The event delegates on Android were typed `Func<LatLng, bool>?`, omitting the `double screenX, double screenY` parameters that were added to the Windows implementation in 3.2.8 (`MapClickEventArgs.ScreenX`/`ScreenY`).  Updated to `Func<LatLng, double, double, bool>?` and both `OnSingleTapConfirmed` and `OnLongPress` gesture-detector callbacks now compute and forward the physical screen coordinates.  **Breaking change** for any Android/MaciOS code that subscribed to these events with the old two-parameter lambda.

## 3.2.10
### âœ¨ Features and improvements
- **WPF sample: data-driven circle-color test harness** â€” `WpfExample` gains a "Run Data-Driven Circle Test" button (and a `--autotest` CLI flag for unattended/CI use) that adds a shared GeoJSON source at runtime with circle layers using literal, `property`+`stops`, `case`, and `match` `circle-color` forms, plus a vector-tile comparison against the public MapLibre demotiles source (`source-layer "centroids"`), and logs `QueryRenderedFeaturesInBox` results for each to `%TEMP%\maplibre_datadriven_test.log`. Written to investigate a VistumblerCS report of circle layers rendering zero features; kept as a regression check for this class of bug. (An initial pass at this investigation concluded the renderer was fine and pinned the bug entirely on a consuming app's server `.htaccess` mislabeling 404 responses as gzip â€” that header bug was real and is fixed upstream in the consuming app, but it was not the whole story; see the source-layer fix below, found by extending this same harness to a populated vector source.)
- **MAUI sample: matching data-driven circle-color test page** â€” `MauiSample` gains a "DD Test" tab (`DataDrivenCircleTestPage`) that ports the WPF harness above to `IMapLibreMapController`/`MapLibreMap`: the same GeoJSON literal/`stops`/`case`/`match` circle layers, the same demotiles vector source-layer (`"centroids"`) regression check for the `setSourceLayer` relayout bug below, and on-screen `QueryRenderedFeaturesInBox` results instead of a file log. Builds on all `MauiSample` TFMs (Android/iOS/macCatalyst/Windows); verified to compile clean on `net9.0-windows10.0.19041.0` locally, exercised at runtime via CI's existing per-platform sample build/publish steps.

### ðŸž Bug fixes
- **Runtime vector-source circle/symbol/etc. layers could render zero features** â€” a layer added at runtime (`AddCircleLayer` et al.) against a vector-tile source, with its `source-layer` set via `SetSourceLayer` *after* the layer is added to the style, would never render any features from tiles that were already loaded â€” even with a perfectly valid `circle-color`/`circle-radius` and correct tile data. Root cause: upstream `mbgl::style::Layer::setSourceLayer()` is the only mutating layer setter that does not call `observer->onLayerChanged()` (unlike `setFilter`, `setVisibility`, `setMinZoom`, `setMaxZoom`), so the render orchestrator was never told to re-evaluate already-loaded tiles against the new source-layer. Adding any filter to the layer "fixed" it as a side effect (filters do notify), which is why this was previously misread as a `circle-color`/data-driven-paint bug rather than a source-layer bug. Fixed in `mbgl_layer_set_source_layer` (`native/src/mln_cabi.cpp`) by triggering the missing notification locally â€” toggling layer visibility to its opposite value and back immediately after applying the source-layer â€” rather than patching the `dependencies/maplibre-native` submodule. Verified with the WPF sample harness above: runtime circle layers against a vector source-layer went from 0 rendered features to the full expected set.

## 3.2.9
### âœ¨ Features and improvements
- _...Add new stuff here..._

### ðŸž Bug fixes
- **Android: app crashed every time a map view was created** â€” this standalone NDK build of `mln-cabi` never runs the `JNI_OnLoad` that the upstream MapLibre Android SDK normally uses to populate `mbgl::android::theJVM`. Every background thread that calls `attachThread()` (notably the `RunLoop`'s "Alarm" timer thread, spawned as soon as a map is constructed) hit `assert(vm != nullptr)` in `jni.cpp` and aborted the process â€” on every Android device and ABI, not just emulators. Fixed by capturing the `JavaVM*` via `JNIEnv::GetJavaVM()` in `mbgl_android_acquire_window()`, which already receives a `JNIEnv*` on the surface-creation path that runs before the `RunLoop`/Alarm thread is spawned.
- **Android: divide-by-zero crash on first map render** â€” `EGLFrontend` constructed its `BackendScope` with `ScopeType::Implicit`, which assumes the EGL/GL context is already current and is a no-op otherwise. On Windows/Apple this is true because the C# caller (or system callback) makes the context current itself; on Android, nothing ever did, so `eglMakeCurrent` was never called. The first GL call (`glGetIntegerv(GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT, ...)`) silently failed against the uncurrent context, leaving the queried alignment at `0` and causing a `SIGFPE` in `Buffer::align()`. Fixed by using `ScopeType::Explicit` in `EGLFrontend::render()`/`~EGLFrontend()`, which makes the `BackendScope` itself call `EGLBackend::activate()`/`deactivate()`.

## 3.2.8
### âœ¨ Features and improvements
- **`MapClicked` event on all platforms** â€” `MapLibreMap` (MAUI) and `MlnMapHost` (WPF) now fire a `MapClicked` / `MapClick` event with geographic coordinates (`LatLng`) and physical screen coordinates (`ScreenX`, `ScreenY`) whenever the user taps the map without panning. A pan is distinguished from a click by a 5-pixel movement threshold. `MapClickEventArgs` gains two new properties: `ScreenX` and `ScreenY`.
- **`QueryRenderedFeaturesInBox` public helpers** â€” `MapLibreMap` gains two overloads of `QueryRenderedFeaturesInBox`: a centred-box helper `(cx, cy, thresholdPx, layerIds)` and a raw-box form `(x1, y1, x2, y2, layerIds)`. `MlnMapHost` gains a matching `QueryRenderedFeaturesInBox(cx, cy, thresholdPx, layerIds[])` public method. Both delegate to the existing native bindings.
- **`LatLngToScreenPoint` on `MapLibreMap`** â€” new passthrough to `IMapLibreMapController.LatLngToScreenPoint`.
- **WPF: `MlnMapClickEventArgs`** â€” new event-args class in `MapLibreNative.Maui.WPF` namespace carrying `ScreenX`, `ScreenY`, `Latitude`, `Longitude`.
### âœ¨ Features and improvements
- **WPF: `RawJson` wrapper for layer paint/layout expressions** â€” `MlnMapHost.ApplyLayerProperties` now handles a new `public record RawJson(string Json)` type in the property dictionary. When a value is `RawJson`, its `Json` string is forwarded verbatim to `SetPaintProperty` / `SetLayoutProperty` instead of being quoted as a JSON string literal. This makes it possible to pass MapLibre expressions (e.g. `["interpolate", â€¦]`) directly via `AddCircleLayer`'s `properties` dictionary without extra reflection.
- **WPF: zoom-interpolated `circle-radius` for wifi layers** â€” `AddWifiCircleLayer` in `MaplibreWifiExtensions` now sets `circle-radius` to a linear interpolation expression (z1â†’1.5 px, z8â†’2.5 px, z13â†’4 px, z17â†’7 px, z19â†’9 px) instead of a fixed 5 px value, so individual AP dots scale appropriately from world overview down to street level at z19.

## 3.2.6
### âœ¨ Features and improvements
- **WPF: `AddVectorSourceUrl`, `AddRasterSource`, `AddRasterDemSource`, `AddImageSource`, `AddSourceJson`** â€” Five new `MlnMapHost` source methods matching the MAUI controller surface. `AddVectorSourceUrl` adds a `type:"vector"` source backed by a TileJSON URL. `AddRasterSource` and `AddRasterDemSource` add raster/raster-DEM tile sources with an optional tile-size parameter. `AddImageSource` now accepts full lat/lng quad corner coordinates (top-right, top-left, bottom-right, bottom-left) backed by the already-implemented `mbgl_style_add_image_source` C ABI function. `AddSourceJson` accepts a raw MapLibre source-spec JSON object of any type.
- **Image source lat/lng quad support** â€” `mbgl_style_add_image_source` was already implemented in `mln_cabi.cpp` and declared in `mln_cabi.h` but was not wired up to C#. Added `StyleAddImageSource` P/Invoke to `NativeMethods.cs`, `AddImageSource` method to `MbglStyle`, and updated `MapLibreMapController.Windows.cs` and `MlnMapHost` to use it. The raster-source fallback is only used when no coordinates are provided.

## 3.2.5
### ðŸ› Bug fixes
- **WPF: attribution button appears above other apps when Vistumbler is in the background** â€” Map observer events (`onDidBecomeIdle`, `onCameraIsChanging`) fire on a background thread even while another application has focus. `onCameraIsChanging` dispatched `CollapseAttribution()` which opened `_attrButtonPopup` with no `IsActive` check; `onDidBecomeIdle` could call `RefreshAttribution()` â†’ `ExpandAttribution()` â†’ start `_attrCollapseTimer`, whose tick then opened the button popup 5 seconds later. Both `ExpandAttribution()` and `CollapseAttribution()` now check `parentWin?.IsActive == true` (in addition to the existing `WindowState != Minimized` guard) and skip opening any popup when the window does not have focus.

## 3.2.4
### ðŸ› Bug fixes
- **WPF: attribution button reappears while window is minimized** â€” The `_attrCollapseTimer` (a 5-second `DispatcherTimer` that transitions the attribution panel from expanded to collapsed/button view) was left running after minimize. When it fired, `CollapseAttribution()` opened `_attrButtonPopup` without checking `WindowState`, causing the button to float above other windows. Fixed by stopping `_attrCollapseTimer` in the `StateChanged` minimize branch, and adding a `WindowState.Minimized` guard in `CollapseAttribution()`.

## 3.2.3
### ðŸ› Bug fixes
- **WPF: popups don't reappear after restoring from minimize** â€” `UpdateNavPopupOpen()` was called synchronously in `StateChanged` while `IsVisible` on the `HwndHost` was still `false`, so the nav popup stayed closed. `UpdateAttributionPopupOpen()` was also called instead of `CollapseAttribution()`, meaning the attribution button was never re-shown (unlike the `Activated` path). Fixed by deferring the reopen to `Dispatcher.BeginInvoke(Render)` so WPF has finished making the visual tree visible, and calling `CollapseAttribution()` to restore the attribution button.

## 3.2.2
### ðŸ› Bug fixes
- **WPF: popups reappear after minimize (race conditions)** â€” Two race conditions in `MlnMapHost` allowed popups to reopen after being closed by `StateChanged`: (1) `WM_ACTIVATE` can arrive on a minimized window, causing the `Activated` handler to call `UpdateNavPopupOpen()` and reopen the nav popup; (2) `LocationChanged`â€™s `Dispatcher.BeginInvoke(Render, â€¦)` callbacks captured `wasOpenâ€¯=â€¯true` before `StateChanged` ran and then reâ€‘set `IsOpenâ€¯=â€¯true` after `StateChanged` closed the popups. Both paths now check `parentWin.WindowStateâ€¯!=â€¯WindowState.Minimized` before reopening.

## 3.2.1
### ðŸ› Bug fixes
- **WPF: popups float above other windows when minimized** â€” `MlnMapHost` now explicitly closes the navigation, attribution button, and attribution detail `Popup` overlays when the parent window is minimized, and re-opens them (via `UpdateNavPopupOpen` / `UpdateAttributionPopupOpen`) when restored. Previously the popups remained visible as floating top-level HWNDs above all other windows.

## 3.2.0
### âœ¨ Features and improvements
- **.NET 10 multi-targeting** â€” All NuGet packages (`MapLibreNative.Maui`, `MapLibreNative.Maui.Handlers`, `MapLibreNative.Maui.WPF`, `MapLibreNative.Maui.Vulkan`) and sample projects now target both `net9.0` and `net10.0` platform TFMs. `global.json` updated to `rollForward: latestMajor` so the .NET 10 SDK is picked up automatically on CI.
- **Android HTTP networking** â€” Replaced the no-op `http_file_source_stub.cpp` with a full provider-callback bridge (`http_file_source_android.cpp`). C# `HttpClient` handles fetching; results are marshalled back to the mbgl RunLoop via `mbgl_http_respond`. Supports conditional GET (ETag / If-Modified-Since), byte-range requests for PMTiles (`Range: bytes=start-end`, HTTP 206 treated as success), and all mbgl response types (200, 304, 204, 404, 429, 5xx, errors).
- **Android gesture input** â€” `MapLibreMapController.Android.cs` now attaches `GestureDetector`, `ScaleGestureDetector`, and a raw two-pointer tracker to the `SurfaceView`. Supported gestures: single-finger pan (with fling/momentum), pinch-to-zoom, two-finger rotate, two-finger tilt (shove), double-tap zoom, single-tap (`OnMapClickReceived`), long-press (`OnMapLongClickReceived`). `SetRotateGesturesEnabled`, `SetScrollGesturesEnabled`, `SetTiltGesturesEnabled`, and `SetZoomGesturesEnabled` are now real flag setters instead of no-ops.
- **Android NDK platform** â€” `ANDROID_PLATFORM` corrected from `android-21` to `android-23` in both `native-android.yml` and `native-android-vulkan.yml`, matching `SupportedOSPlatformVersion=23.0`.
- **CI/release: multi-TFM publish fix** â€” `ConsoleExample` and `WpfExample` now receive `-f net10.0-windows10.0.19041.0 -p:UseLocalPackages=true` on all publish steps in both `ci.yml` and `release.yml`, resolving `NETSDK1129` (ambiguous TFM) and `NU1102` (Mono runtime not found) errors introduced when both projects gained `net10.0` TFMs.
- **`WpfExample.csproj`: conditional reference** â€” Uses `ProjectReference` to `MapLibreNative.Maui.WPF` when building locally and `PackageReference` (version `*-*`) when `UseLocalPackages=true`, preventing CI restore from pulling in Android/iOS TFMs via the bindings project chain.

### ðŸ› Bug fixes
- **Android linker: undefined `_impl` symbols** â€” Forward declarations of `mbgl_set_http_provider_impl`, `mbgl_http_respond_impl`, and `mbgl_http_cancel_impl` in `mln_cabi.cpp` were missing `extern "C"`, causing `ld.lld` to look for C++-mangled names that don't match the `extern "C"` definitions in `http_file_source_android.cpp`. Fixed by adding `extern "C"` to all three forward declarations.
- **Android controller: ambiguous `View` type** â€” `MapTouchListener` used the unqualified `View` type, which was ambiguous between `Microsoft.Maui.Controls.View` and `Android.Views.View`. Qualified to `Android.Views.View` in both the interface declaration and the `OnTouch` parameter.


### âœ¨ Features and improvements
- **Feature state (set / get / remove)** â€” New `SetFeatureState`, `GetFeatureState`, and `RemoveFeatureState` methods on the controller and `MbglMap` wrapper. Backed by `mbgl_map_set_feature_state`, `mbgl_map_get_feature_state`, and `mbgl_map_remove_feature_state` in the C ABI. State is passed as a JSON object string (e.g. `{"hover":true}`); `source_layer_id` is optional for non-vector sources; `feature_id` and `state_key` are optional on remove to clear all features/keys in a source.
- **Viewport bounds** â€” New `GetVisibleBounds()` controller method (backed by `mbgl_map_latlng_bounds_for_camera`) returns the `(LatSW, LonSW, LatNE, LonNE)` lat-lng bounding box of the current camera viewport.
- **Memory pressure / debug logs** â€” New `ReduceMemoryUse()` and `DumpDebugLogs()` controller methods (backed by `mbgl_map_reduce_memory_use` / `mbgl_map_dump_debug_logs`) delegate to the underlying renderer for resource cleanup and diagnostic output.
- **Generic JSON source add** â€” New `AddSourceJson(sourceId, sourceJson)` on the controller and `MbglStyle` wrapper accepts any MapLibre source-spec JSON object and registers it with the active style, complementing the existing typed `GeoJsonSource`, `VectorSource`, etc.
- **Generic JSON layer add** â€” New `AddLayerJson(layerJson, beforeLayerId?)` on the controller and `MbglStyle` wrapper accepts a complete MapLibre layer-spec JSON object (must include `"id"` and `"type"`) and returns a non-owning `MbglLayer` handle.
- **Observer: `onRenderError` event** â€” `CabiMapObserver` now overrides `onRenderError(std::exception_ptr)` and fires `"onRenderError"` with the exception message as the detail string; the Windows and macOS/iOS controllers expose this as `OnRenderErrorReceived`.
- **Observer: `placementChanged` frame variants** â€” `onDidFinishRenderingFrame` now emits four distinct event names encoding both `needsRepaint` and `placementChanged` booleans: `"onDidFinishRenderingFrame"`, `"onDidFinishRenderingFrameNeedsRepaint"`, `"onDidFinishRenderingFramePlacementChanged"`, and `"onDidFinishRenderingFrameNeedsRepaintPlacementChanged"`.
- **Submodule bump** â€” `dependencies/maplibre-native` updated from `647636bf6115` to `fa8a9c8e3261` (iOS 6.27.0 / Android 13.3.0).
- **CI: iOS and macCatalyst sample builds** â€” CI and release workflows now build and upload iOS simulator (`.app`) and macCatalyst (`.app`) sample artifacts on the `macos-26` runner with Xcode 26.5.
- **CI: Windows â€” Ninja generator** â€” CMake configure in `native-windows.yml` and `native-windows-vulkan.yml` switched from `-G "Visual Studio 17 2022"` to `-G Ninja -DCMAKE_BUILD_TYPE=Release`; the VS generator fails when `ilammy/msvc-dev-cmd@v1` is active because it breaks `vswhere.exe` discovery.
- **CI: Windows â€” MAUI workloads** â€” `pack-wpf` job in `ci.yml` now runs `dotnet workload install maui` before packing/building, fixing `NETSDK1147` errors caused by missing Android/iOS workloads on the Windows runner.
- **CI: macCatalyst â€” correct macabi ABI** â€” macCatalyst build in `native-apple.yml` completely rewritten: two separate Ninja builds (x86_64 and arm64) each set `CMAKE_<LANG>_COMPILER_TARGET=ARCH-apple-ios15.0-macabi` for all four languages (C, CXX, ObjC, ObjCXX). `CMAKE_OSX_ARCHITECTURES` and `CMAKE_OSX_DEPLOYMENT_TARGET` are cleared to prevent macOS sysroot flags from overriding the macabi triple. Per-arch static libraries are merged with `libtool` then combined with `lipo`.
- **CI/release alignment** â€” Artifact and step names unified between `ci.yml` and `release.yml`: `nuget-packages`, `nuget-packages-vulkan`, `nuget-packages-wpf`, `windows-samples`, `mobile-samples`. Redundant `sample-windows` CI job removed (consolidated into `pack-wpf`). `EnableWindowsTargeting=true` added to iOS/macCatalyst release builds. Vulkan pack jobs now include the `iossimulator-arm64` slice.
- **`CMakeLists.txt`: declare `OBJC OBJCXX` languages on Apple** â€” Prevents a Ninja generator error (`CMAKE_OBJCXX_COMPILE_OBJECT` not set) when building `.mm` files on Apple platforms.

### ðŸž Bug fixes
- **Windows / WPF: map goes blank after drag** â€” `onDidFinishRenderingFrame*` events are fired from inside `_renderer->render()` after `_updateParams` has already been consumed. Handling them by setting `_renderNeedsUpdate = true` caused the next timer tick to call `glClear` + `SwapBuffers` with null params, blanking the screen. Fix: `NeedsRepaint` cases do nothing (mbgl re-queues `update()` itself); `PlacementChanged`-only calls `TriggerRepaint()` so fresh params arrive before the next render. Applies to both `MapLibreMapController.Windows.cs` and `wpf/MlnMapHost.cs`.
- **Windows / WPF: final pan frame not rendered after mouse release** â€” `WM_LBUTTONUP` handler did not set `_renderNeedsUpdate = true` or call `TriggerRepaint()` after `OnPanEnd()`, so the map stopped updating immediately when the mouse button was released. Fixed in both controllers.

## 3.0.3
### âœ¨ Features and improvements
- **NuGet package metadata** â€” New `Directory.Build.props` at the repo root injects `PackageLicenseExpression=BSD-2-Clause`, `PackageReadmeFile=README.md`, `RepositoryUrl`, and `Authors` into all four NuGet packages, resolving NuGet.org "missing license" and "missing readme" warnings
- **CI: Android APK sample** â€” CI workflow now builds an Android APK from `MauiSample` on `macos-latest` and uploads it as a build artifact; release workflow adds a `samples-mobile` job that builds the Android APK and attaches it to the GitHub Release

### ðŸž Bug fixes

## 3.0.2
### âœ¨ Features and improvements

### ðŸž Bug fixes
- **WPF attribution blank text** â€” `_attributionPopup` now sets `AllowsTransparency=false`; layered (transparent) WPF popups have a known rendering defect where `Hyperlink`/`Run` inlines inside a `TextBlock` fail to paint, leaving the attribution visually blank
- **WPF attribution positioning** â€” Attribution and â“˜ button popups now subscribe to `SizeChanged` on their `Border` children and reposition using the actual rendered height, replacing the previous fixed-constant bottom offset
- **WPF minimize/restore repaint** â€” `MlnMapHost` now handles the parent window's `StateChanged` event and sets `_renderNeedsUpdate = true` when the window is restored from minimized, ensuring the GL surface repaints when the restored size is unchanged

## 3.0.1
### âœ¨ Features and improvements
- **Attribution overlay â€” Android** â€” `MapLibreMapController` now wraps the `SurfaceView` in a `FrameLayout` and overlays a `TextView` (bottom-right corner) with clickable hyperlinks built from source HTML via `SpannableStringBuilder`/`URLSpan`; a collapsible â“˜ button is shown after the full text auto-collapses (5 seconds or on camera movement) ([#7](https://github.com/acalcutt/maplibre-maui/pull/7))
- **Attribution overlay â€” iOS/macCatalyst** â€” A `UITextView` (bottom-right, tappable `NSAttributedString` links) and a `UIButton` â“˜ toggle are added as Auto Layout subviews of `MapContainerView`; `LayoutSubviews` is updated to skip Auto Layout views when resizing the Metal surface ([#7](https://github.com/acalcutt/maplibre-maui/pull/7))
- **Attribution overlay â€” Windows MAUI** â€” Windows `MapLibreMapController` renders an attribution text band and a collapsible â“˜ button as child HWNDs; a Win32 `SetTimer`/`WM_TIMER` keeps the overlays z-ordered above the GL surface during modal resize loops ([#7](https://github.com/acalcutt/maplibre-maui/pull/7))
- **Attribution overlay â€” WPF** â€” `MlnMapHost` replaces the plain-text attribution with clickable `Hyperlink` inlines in a `TextBlock`; adds a second collapsible â“˜ `Popup` that appears after the full attribution auto-collapses (5-second `DispatcherTimer`) or when `onCameraIsChanging` fires ([#7](https://github.com/acalcutt/maplibre-maui/pull/7))
- **Windows MAUI maximize/restore layout** â€” `MapLibreMapHandler.Windows` subscribes to the host `Window.SizeChanged` event and defers `InvalidateMeasure` at `DispatcherQueuePriority.Low` so the map view and nav panel resize correctly on window maximize/restore without requiring a tab switch
- **Sample: custom style URL** â€” MAUI sample (`BasicMapPage`) adds a custom style URL `Entry` + Apply button; WPF sample (`WpfExample`) adds a second toolbar row with a preset style picker `ComboBox` and a freeform URL `TextBox`

### ðŸž Bug fixes

## 3.0.0
### âš ï¸ Breaking changes
- **Package rename** â€” All NuGet packages renamed from `Maui.MapLibre.*` to `MapLibreNative.Maui.*` because the `Maui.MapLibre` prefix is reserved on NuGet.org by another party. Update `PackageReference` entries:
  - `Maui.MapLibre.Native` â†’ `MapLibreNative.Maui`
  - `Maui.MapLibre.Native.Vulkan` â†’ `MapLibreNative.Maui.Vulkan`
  - `Maui.MapLibre.WPF` â†’ `MapLibreNative.Maui.WPF`
  - `Maui.Maplibre.Handlers` â†’ `MapLibreNative.Maui.Handlers`
- **Namespace rename** â€” All C# namespaces updated. Update `using` directives and XAML `clr-namespace:` / `assembly=` references accordingly:
  - `Maui.MapLibre.Native` â†’ `MapLibreNative.Maui`
  - `Maui.MapLibre.Native.Upstream` â†’ `MapLibreNative.Maui` (Vulkan package now shares the same namespace as the base package)
  - `Maui.MapLibre.WPF` â†’ `MapLibreNative.Maui.WPF`
  - `Maui.MapLibre.Handlers` (and sub-namespaces) â†’ `MapLibreNative.Maui.Handlers`
- **Old binding layer removed** â€” `bindings/MlnMap.cs`, `MlnRuntime.cs`, and `NativeMethods.Mln.cs` (legacy compatibility wrappers) are deleted; callers must use the `MbglMap`/`MbglRunLoop`/`NativeMethods` types directly

### âœ¨ Features and improvements
- **Documentation site** â€” DocFX GitHub Pages site added under `docs/`; published automatically via new `docs.yml` workflow on every push to `main`
- **Vulkan package alignment** â€” `MapLibreNative.Maui.Vulkan` now shares the same `mln-cabi` ABI and `MapLibreNative.Maui` namespace as the base package; FFI sketch files removed
- **WPF attribution plain text** â€” `MlnMapHost` now strips HTML tags and decodes HTML entities (`&copy;` â†’ Â©, `&amp;`, `&lt;`, `&gt;`, `&quot;`, `&nbsp;`, `&reg;`, `&trade;`) before displaying the attribution string, so the popup shows readable text instead of raw markup
- **WPF attribution bounds** â€” Attribution popup `MaxWidth` is constrained to the map width minus margins; if the text would overflow the right edge the popup is repositioned to stay within the control bounds
- **WPF popup follow-window** â€” Navigation and attribution popups now reopen via `Dispatcher.BeginInvoke(DispatcherPriority.Render)` on `LocationChanged` so they track the window when it is dragged to a new screen position
- **CI concurrency** â€” Release workflow now cancels any in-progress run for the same branch when a new push arrives

### ðŸž Bug fixes
- **CI NuGet push error handling** â€” Pack/push steps now emit explicit success/failure messages and propagate exit codes correctly

## 2.0.2
### âœ¨ Features and improvements
- **WPF custom cursors** â€” `MlnMapHost` handles `WM_SETCURSOR` to show a hand cursor (`IDC_HAND`) while hovering and a move cursor (`IDC_SIZEALL`) while dragging the map

### ðŸž Bug fixes

## 2.0.1
### âœ¨ Features and improvements
- **C ABI typed handles** â€” `mbgl_map_t*`, `mbgl_frontend_t*`, `mbgl_runloop_t*` are now distinct opaque types; eliminates handle mix-up bugs at compile time (see 1.2.0 for full details, landed here)
- **Status codes, debug options, log callback** â€” see 1.2.0 feature list; all shipped in this release

### ðŸž Bug fixes
- **Windows MAUI DPI fallback** â€” `GetDpiForWindow` returns a scale factor (e.g. `1.25`), not raw DPI; the fallback when no window is available was incorrectly `96.0f` (which set `_pixelRatio` to 96Ã—), corrected to `1.0f`
- **WPF popup DPI scaling** â€” Navigation and attribution popups switched from `PlacementMode.AbsolutePoint` + `PointToScreen` to `PlacementMode.Relative`; `AbsolutePoint` double-scaled logical offsets at DPI settings above 100%
- **WPF DPI on resize** â€” `_dpi` is now refreshed inside `OnRenderSizeChanged` so moving the window to a monitor with a different DPI (e.g. laptop screen â†’ 4K external) keeps physical pixel dimensions in sync

## 2.0.0
### âœ¨ Features and improvements
- **Prefix rename: `mbgl` â†’ `mln`** â€” All C ABI filenames, macros, and symbols updated: `mbgl_cabi.h/cpp` â†’ `mln_cabi.h/cpp`, `MBGL_CABI_API/NOEXCEPT/EXPORT` â†’ `MLN_CABI_API/NOEXCEPT/EXPORT`, `mbgl_cabi_version()` â†’ `mln_cabi_version()`
- **WPF control rename** â€” `MbglMapHost` â†’ `MlnMapHost` (class and file `wpf/MlnMapHost.cs`)
- **Sample CI artifacts** â€” CI now uploads downloadable artifacts for all sample apps:
  - `windows-samples-ci` â€” `WpfExample-win-x64` and `WpfExample-win-arm64` zips
  - `sample-android-apk` â€” signed Android APK from the MAUI sample
  - `sample-windows-maui` â€” self-contained Windows MAUI app (via `dotnet publish`)

### ðŸž Bug fixes
- **Windows MAUI initial black map** â€” `SetStyleString` now always stores `_styleString` even when `_map` is not yet initialized, so `TryInitialize` (fired on `View.Loaded`) picks up the style URL set by the MAUI property mapper before the view is ready
- **WpfExample crash on launch** â€” `mln-cabi.dll` is placed by NuGet in `native\win-x64\` (RID layout) but P/Invoke could not find it. Added `NativeLibrary.SetDllImportResolver` in `NativeMethods` static constructor to probe `<AppDir>\native\<rid>\mln-cabi.dll` before falling back to the OS search order
- **CI vcpkg cache abort** â€” `actions/cache@v4` restore failure on Windows native builds was killing all downstream steps. Added `continue-on-error: true` to the cache step in `native-windows.yml` and `native-windows-vulkan.yml` so a cache miss falls through to a clean vcpkg build
- **CI NuGet local feed path** â€” `dotnet nuget add source local-feed` used a relative path that NuGet resolved against the user profile instead of the workspace. Fixed to use `"${{ github.workspace }}\local-feed"` (absolute path)

## 1.2.0
### âœ¨ Features and improvements
- **C ABI typed handles** â€” `mbgl_map_t*`, `mbgl_frontend_t*`, `mbgl_runloop_t*` are now distinct opaque types; eliminates handle mix-up bugs at compile time
- **Status codes** â€” All mutating C ABI functions now return `mbgl_status_t` (`0 = OK`, `1 = NullHandle`, `2 = InvalidArg`); C# P/Invokes return `MbglStatus` enum
- **Debug overlays** â€” `mbgl_map_get_debug_options` / `mbgl_map_set_debug_options` exposed; `IMapLibreMapController.GetDebugOptions()` / `SetDebugOptions(int)` added to all platforms
- **Style inspection** â€” `IMapLibreMapController.GetStyleUrl()`, `GetStyleSourceIds()`, `GetStyleLayerIds()` added to all platforms
- **Layer read-back and visibility** â€” `IMapLibreMapController.GetLayerPaintProperty`, `GetLayerLayoutProperty`, `GetLayerVisibility`, `SetLayerVisibility` added to all platforms
- **Source attribution** â€” `MbglSource.GetAttribution()` returns the TileJSON attribution string
- **Log callback** â€” `NativeMethods.InstallLogCallback(LogFn)` lets the host intercept MapLibre native log messages; `MbglLogLevel` enum provided
- **`noexcept` guarantees** â€” All C ABI entry points are marked `noexcept`; exceptions are caught internally and surfaced via `mbgl_get_last_error()`
- Sample app: debug overlay toggle switch (TileBorders + Collision) demonstrates `SetDebugOptions` at runtime

## 1.1.1
### ðŸž Bug fixes
- Fixed native DLLs missing from NuGet package: `Pack=true` / `PackagePath` items were inside a TFM-conditioned `ItemGroup` that NuGet silently skips during the outer (multi-targeting) build pass. Moved those declarations to an unconditional `ItemGroup`; `CopyToOutputDirectory` remains TFM-conditioned for local builds. `runtimes/win-x64/native/mln-cabi.dll` and `runtimes/win-arm64/native/mln-cabi.dll` are now correctly included in `Maui.MapLibre.Native`.

## 1.1.0
### âœ¨ Features and improvements
- Camera operations exposed on `IMapLibreMapController`: `FlyTo`, `EaseTo`, `JumpTo` (bearing + pitch), `SetCameraTargetBounds` (min/max zoom + pitch)
- Camera read-back: `GetZoom()`, `GetBearing()`, `GetPitch()`, `GetCenter()`
- Projection helpers: `LatLngToScreenPoint`, `ScreenPointToLatLng`
- Queries: `QueryRenderedFeaturesAtPoint`, `QueryRenderedFeaturesInBox` (returns GeoJSON FeatureCollection string)
- Observer wiring fixed â€” all 14 `MapObserver` virtuals now correctly routed to the C callback via `CabiMapObserver`
- Light API: `MbglStyle.SetLightProperty(name, valueJson)` â€” anchor, color, intensity, position
- Transition options: `MbglStyle.SetTransition(durationMs, delayMs)`
- `CancelTransitions()` + `IsFullyLoaded` on `MbglMap`
- **Tier 1** â€” Interactive movement: `SetGestureInProgress`, `MoveBy`, `RotateBy`, `PitchBy` (all with optional animation duration)
- **Tier 1** â€” Map option post-create setters: `SetNorthOrientation`, `SetConstrainMode`, `SetViewportMode`
- **Tier 1** â€” Bounds read-back: `GetBounds()` â†’ `BoundOptions` record (lat/lng box + zoom/pitch limits, NaN for unset)
- **Tier 1** â€” Style enumeration: `GetUrl()`, `GetName()`, `GetSourceIds()`, `GetLayerIds()`, `GetLayer(id)`, `GetSource(id)`
- **Tier 1** â€” Layer read-back: `GetPaintProperty(name)`, `GetLayoutProperty(name)`, `GetVisibility()`
- **Tier 2** â€” Tile LOD controls: `SetTileLodMinRadius`, `SetTileLodScale`, `SetTileLodPitchThreshold`, `SetTileLodZoomShift`, `SetTileLodMode` (0=Default, 1=Distance)
- **Tier 2** â€” Tile prefetch: `SetPrefetchZoomDelta` / `GetPrefetchZoomDelta`
- **Tier 2** â€” Camera fit to point set: `CameraForLatLngs(points, padding)` â†’ `CameraResult`
- **Tier 2** â€” Batch projection: `PixelsForLatLngs(points)`, `LatLngsForPixels(pixels)`
- All new APIs surface on `IMapLibreMapController` and are implemented in all three platform controllers (Android, iOS/macCatalyst, Windows)

### ðŸž Bug fixes
- Memory leak in `mbgl_map_destroy`: `frontend.release()` â†’ `frontend.reset()`

## 1.0.0

### âœ¨ Features and improvements

- Unified all platforms (Android, iOS, macCatalyst, Windows) to a single flat C ABI (`mln-cabi`) â€” removes all legacy Xamarin `Org.Maplibre.*` binding dependencies
- Added sample pages: `BasicMapPage`, `GeoJsonLayersPage`, `MarkersPage`
- Android frontend: EGL surface + `ANativeWindow` via JNI, backed by `SurfaceView`
- iOS / macCatalyst frontend: Metal via `MTKView` (owned by C++ backend, inserted as UIKit subview)
- Windows frontend: OpenGL 3.2 core context via `wglCreateContextAttribsARB`, child HWND render target
- Camera: `flyTo`, `easeTo`, `jumpTo` (bearing + pitch), `setBounds`, `cameraForBounds` (C ABI level)
- Style: `addImage` / `removeImage` (sprite images), `setProjectionMode` (axonometric)
- Layers: fill, line, circle, symbol, raster, heatmap, hillshade, fill-extrusion, background, location-indicator, color-relief
- Sources: GeoJSON (inline + URL), vector, raster, raster-DEM, image
- Multi-target NuGet packages: `net9.0-android`, `net9.0-ios`, `net9.0-maccatalyst`, `net9.0-windows10.0.19041.0`
- GitHub Actions CI with per-platform native builds (Windows x64/arm64, Android arm64/x86_64, iOS arm64, macCatalyst) and NuGet packaging on `macos-15`
- BSD 2-Clause license (matching maplibre-native)
- maplibre-native pinned as a git submodule

### ðŸž Bug fixes

- Fixed linker errors on Android (`LocalGlyphRasterizer`, `Collator`, `formatNumber`, `Log::platformRecord` stubs; HTTP file source stub; LTO flag alignment)
- Fixed Metal frontend: `MTKView` rewrites, `activate`/`deactivate` on `MetalBackend`, correct `BackendScope` namespace in `drawFrame`
- Fixed `RendererFrontend::getThreadPool()` in all platform frontends; `getSize()` vs `.size` API correction
- Fixed `onDidFinishRenderingFrame` signature; added `metal-cpp` include path and `MetalKit`
- Fixed macCatalyst `UIViewAutoresizing` guard for `TARGET_OS_IPHONE`
- Fixed Android build parallelism cap (4 jobs) to prevent linker OOM
- Fixed Android CI artifact path (wildcard); macOS CI runner upgraded to `macos-15` for Xcode 16 / Mac Catalyst 18.0
- Fixed Windows CI artifact wildcard path; `.NET 9` SDK pinned in `global.json`
- Removed dead `default_file_source.hpp` include; added missing `map_observer.hpp`
- Removed duplicate native cabi headers and unused `nuget.config`
- Removed legacy Xamarin binding projects (`Org.Maplibre.Android`, `Org.Maplibre.MaciOS`)
- Removed `Org.Maplibre` casts from `Maps/Style.cs`, `Maps/Source.cs`, `Maps/Layer.cs`; `NativeMethods` visibility set to `public`

