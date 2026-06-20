# Changelog

## master
### ✨ Features and improvements
- _...Add new stuff here..._

### 🐞 Bug fixes
- _...Add new stuff here..._

## 3.2.3
### 🐛 Bug fixes
- **WPF: popups don't reappear after restoring from minimize** — `UpdateNavPopupOpen()` was called synchronously in `StateChanged` while `IsVisible` on the `HwndHost` was still `false`, so the nav popup stayed closed. `UpdateAttributionPopupOpen()` was also called instead of `CollapseAttribution()`, meaning the attribution button was never re-shown (unlike the `Activated` path). Fixed by deferring the reopen to `Dispatcher.BeginInvoke(Render)` so WPF has finished making the visual tree visible, and calling `CollapseAttribution()` to restore the attribution button.

## 3.2.2
### 🐛 Bug fixes
- **WPF: popups reappear after minimize (race conditions)** — Two race conditions in `MlnMapHost` allowed popups to reopen after being closed by `StateChanged`: (1) `WM_ACTIVATE` can arrive on a minimized window, causing the `Activated` handler to call `UpdateNavPopupOpen()` and reopen the nav popup; (2) `LocationChanged`’s `Dispatcher.BeginInvoke(Render, …)` callbacks captured `wasOpen = true` before `StateChanged` ran and then re‑set `IsOpen = true` after `StateChanged` closed the popups. Both paths now check `parentWin.WindowState != WindowState.Minimized` before reopening.

## 3.2.1
### 🐛 Bug fixes
- **WPF: popups float above other windows when minimized** — `MlnMapHost` now explicitly closes the navigation, attribution button, and attribution detail `Popup` overlays when the parent window is minimized, and re-opens them (via `UpdateNavPopupOpen` / `UpdateAttributionPopupOpen`) when restored. Previously the popups remained visible as floating top-level HWNDs above all other windows.

## 3.2.0
### ✨ Features and improvements
- **.NET 10 multi-targeting** — All NuGet packages (`MapLibreNative.Maui`, `MapLibreNative.Maui.Handlers`, `MapLibreNative.Maui.WPF`, `MapLibreNative.Maui.Vulkan`) and sample projects now target both `net9.0` and `net10.0` platform TFMs. `global.json` updated to `rollForward: latestMajor` so the .NET 10 SDK is picked up automatically on CI.
- **Android HTTP networking** — Replaced the no-op `http_file_source_stub.cpp` with a full provider-callback bridge (`http_file_source_android.cpp`). C# `HttpClient` handles fetching; results are marshalled back to the mbgl RunLoop via `mbgl_http_respond`. Supports conditional GET (ETag / If-Modified-Since), byte-range requests for PMTiles (`Range: bytes=start-end`, HTTP 206 treated as success), and all mbgl response types (200, 304, 204, 404, 429, 5xx, errors).
- **Android gesture input** — `MapLibreMapController.Android.cs` now attaches `GestureDetector`, `ScaleGestureDetector`, and a raw two-pointer tracker to the `SurfaceView`. Supported gestures: single-finger pan (with fling/momentum), pinch-to-zoom, two-finger rotate, two-finger tilt (shove), double-tap zoom, single-tap (`OnMapClickReceived`), long-press (`OnMapLongClickReceived`). `SetRotateGesturesEnabled`, `SetScrollGesturesEnabled`, `SetTiltGesturesEnabled`, and `SetZoomGesturesEnabled` are now real flag setters instead of no-ops.
- **Android NDK platform** — `ANDROID_PLATFORM` corrected from `android-21` to `android-23` in both `native-android.yml` and `native-android-vulkan.yml`, matching `SupportedOSPlatformVersion=23.0`.
- **CI/release: multi-TFM publish fix** — `ConsoleExample` and `WpfExample` now receive `-f net10.0-windows10.0.19041.0 -p:UseLocalPackages=true` on all publish steps in both `ci.yml` and `release.yml`, resolving `NETSDK1129` (ambiguous TFM) and `NU1102` (Mono runtime not found) errors introduced when both projects gained `net10.0` TFMs.
- **`WpfExample.csproj`: conditional reference** — Uses `ProjectReference` to `MapLibreNative.Maui.WPF` when building locally and `PackageReference` (version `*-*`) when `UseLocalPackages=true`, preventing CI restore from pulling in Android/iOS TFMs via the bindings project chain.

### 🐛 Bug fixes
- **Android linker: undefined `_impl` symbols** — Forward declarations of `mbgl_set_http_provider_impl`, `mbgl_http_respond_impl`, and `mbgl_http_cancel_impl` in `mln_cabi.cpp` were missing `extern "C"`, causing `ld.lld` to look for C++-mangled names that don't match the `extern "C"` definitions in `http_file_source_android.cpp`. Fixed by adding `extern "C"` to all three forward declarations.
- **Android controller: ambiguous `View` type** — `MapTouchListener` used the unqualified `View` type, which was ambiguous between `Microsoft.Maui.Controls.View` and `Android.Views.View`. Qualified to `Android.Views.View` in both the interface declaration and the `OnTouch` parameter.


### ✨ Features and improvements
- **Feature state (set / get / remove)** — New `SetFeatureState`, `GetFeatureState`, and `RemoveFeatureState` methods on the controller and `MbglMap` wrapper. Backed by `mbgl_map_set_feature_state`, `mbgl_map_get_feature_state`, and `mbgl_map_remove_feature_state` in the C ABI. State is passed as a JSON object string (e.g. `{"hover":true}`); `source_layer_id` is optional for non-vector sources; `feature_id` and `state_key` are optional on remove to clear all features/keys in a source.
- **Viewport bounds** — New `GetVisibleBounds()` controller method (backed by `mbgl_map_latlng_bounds_for_camera`) returns the `(LatSW, LonSW, LatNE, LonNE)` lat-lng bounding box of the current camera viewport.
- **Memory pressure / debug logs** — New `ReduceMemoryUse()` and `DumpDebugLogs()` controller methods (backed by `mbgl_map_reduce_memory_use` / `mbgl_map_dump_debug_logs`) delegate to the underlying renderer for resource cleanup and diagnostic output.
- **Generic JSON source add** — New `AddSourceJson(sourceId, sourceJson)` on the controller and `MbglStyle` wrapper accepts any MapLibre source-spec JSON object and registers it with the active style, complementing the existing typed `GeoJsonSource`, `VectorSource`, etc.
- **Generic JSON layer add** — New `AddLayerJson(layerJson, beforeLayerId?)` on the controller and `MbglStyle` wrapper accepts a complete MapLibre layer-spec JSON object (must include `"id"` and `"type"`) and returns a non-owning `MbglLayer` handle.
- **Observer: `onRenderError` event** — `CabiMapObserver` now overrides `onRenderError(std::exception_ptr)` and fires `"onRenderError"` with the exception message as the detail string; the Windows and macOS/iOS controllers expose this as `OnRenderErrorReceived`.
- **Observer: `placementChanged` frame variants** — `onDidFinishRenderingFrame` now emits four distinct event names encoding both `needsRepaint` and `placementChanged` booleans: `"onDidFinishRenderingFrame"`, `"onDidFinishRenderingFrameNeedsRepaint"`, `"onDidFinishRenderingFramePlacementChanged"`, and `"onDidFinishRenderingFrameNeedsRepaintPlacementChanged"`.
- **Submodule bump** — `dependencies/maplibre-native` updated from `647636bf6115` to `fa8a9c8e3261` (iOS 6.27.0 / Android 13.3.0).
- **CI: iOS and macCatalyst sample builds** — CI and release workflows now build and upload iOS simulator (`.app`) and macCatalyst (`.app`) sample artifacts on the `macos-26` runner with Xcode 26.5.
- **CI: Windows — Ninja generator** — CMake configure in `native-windows.yml` and `native-windows-vulkan.yml` switched from `-G "Visual Studio 17 2022"` to `-G Ninja -DCMAKE_BUILD_TYPE=Release`; the VS generator fails when `ilammy/msvc-dev-cmd@v1` is active because it breaks `vswhere.exe` discovery.
- **CI: Windows — MAUI workloads** — `pack-wpf` job in `ci.yml` now runs `dotnet workload install maui` before packing/building, fixing `NETSDK1147` errors caused by missing Android/iOS workloads on the Windows runner.
- **CI: macCatalyst — correct macabi ABI** — macCatalyst build in `native-apple.yml` completely rewritten: two separate Ninja builds (x86_64 and arm64) each set `CMAKE_<LANG>_COMPILER_TARGET=ARCH-apple-ios15.0-macabi` for all four languages (C, CXX, ObjC, ObjCXX). `CMAKE_OSX_ARCHITECTURES` and `CMAKE_OSX_DEPLOYMENT_TARGET` are cleared to prevent macOS sysroot flags from overriding the macabi triple. Per-arch static libraries are merged with `libtool` then combined with `lipo`.
- **CI/release alignment** — Artifact and step names unified between `ci.yml` and `release.yml`: `nuget-packages`, `nuget-packages-vulkan`, `nuget-packages-wpf`, `windows-samples`, `mobile-samples`. Redundant `sample-windows` CI job removed (consolidated into `pack-wpf`). `EnableWindowsTargeting=true` added to iOS/macCatalyst release builds. Vulkan pack jobs now include the `iossimulator-arm64` slice.
- **`CMakeLists.txt`: declare `OBJC OBJCXX` languages on Apple** — Prevents a Ninja generator error (`CMAKE_OBJCXX_COMPILE_OBJECT` not set) when building `.mm` files on Apple platforms.

### 🐞 Bug fixes
- **Windows / WPF: map goes blank after drag** — `onDidFinishRenderingFrame*` events are fired from inside `_renderer->render()` after `_updateParams` has already been consumed. Handling them by setting `_renderNeedsUpdate = true` caused the next timer tick to call `glClear` + `SwapBuffers` with null params, blanking the screen. Fix: `NeedsRepaint` cases do nothing (mbgl re-queues `update()` itself); `PlacementChanged`-only calls `TriggerRepaint()` so fresh params arrive before the next render. Applies to both `MapLibreMapController.Windows.cs` and `wpf/MlnMapHost.cs`.
- **Windows / WPF: final pan frame not rendered after mouse release** — `WM_LBUTTONUP` handler did not set `_renderNeedsUpdate = true` or call `TriggerRepaint()` after `OnPanEnd()`, so the map stopped updating immediately when the mouse button was released. Fixed in both controllers.

## 3.0.3
### ✨ Features and improvements
- **NuGet package metadata** — New `Directory.Build.props` at the repo root injects `PackageLicenseExpression=BSD-2-Clause`, `PackageReadmeFile=README.md`, `RepositoryUrl`, and `Authors` into all four NuGet packages, resolving NuGet.org "missing license" and "missing readme" warnings
- **CI: Android APK sample** — CI workflow now builds an Android APK from `MauiSample` on `macos-latest` and uploads it as a build artifact; release workflow adds a `samples-mobile` job that builds the Android APK and attaches it to the GitHub Release

### 🐞 Bug fixes

## 3.0.2
### ✨ Features and improvements

### 🐞 Bug fixes
- **WPF attribution blank text** — `_attributionPopup` now sets `AllowsTransparency=false`; layered (transparent) WPF popups have a known rendering defect where `Hyperlink`/`Run` inlines inside a `TextBlock` fail to paint, leaving the attribution visually blank
- **WPF attribution positioning** — Attribution and ⓘ button popups now subscribe to `SizeChanged` on their `Border` children and reposition using the actual rendered height, replacing the previous fixed-constant bottom offset
- **WPF minimize/restore repaint** — `MlnMapHost` now handles the parent window's `StateChanged` event and sets `_renderNeedsUpdate = true` when the window is restored from minimized, ensuring the GL surface repaints when the restored size is unchanged

## 3.0.1
### ✨ Features and improvements
- **Attribution overlay — Android** — `MapLibreMapController` now wraps the `SurfaceView` in a `FrameLayout` and overlays a `TextView` (bottom-right corner) with clickable hyperlinks built from source HTML via `SpannableStringBuilder`/`URLSpan`; a collapsible ⓘ button is shown after the full text auto-collapses (5 seconds or on camera movement) ([#7](https://github.com/acalcutt/maplibre-maui/pull/7))
- **Attribution overlay — iOS/macCatalyst** — A `UITextView` (bottom-right, tappable `NSAttributedString` links) and a `UIButton` ⓘ toggle are added as Auto Layout subviews of `MapContainerView`; `LayoutSubviews` is updated to skip Auto Layout views when resizing the Metal surface ([#7](https://github.com/acalcutt/maplibre-maui/pull/7))
- **Attribution overlay — Windows MAUI** — Windows `MapLibreMapController` renders an attribution text band and a collapsible ⓘ button as child HWNDs; a Win32 `SetTimer`/`WM_TIMER` keeps the overlays z-ordered above the GL surface during modal resize loops ([#7](https://github.com/acalcutt/maplibre-maui/pull/7))
- **Attribution overlay — WPF** — `MlnMapHost` replaces the plain-text attribution with clickable `Hyperlink` inlines in a `TextBlock`; adds a second collapsible ⓘ `Popup` that appears after the full attribution auto-collapses (5-second `DispatcherTimer`) or when `onCameraIsChanging` fires ([#7](https://github.com/acalcutt/maplibre-maui/pull/7))
- **Windows MAUI maximize/restore layout** — `MapLibreMapHandler.Windows` subscribes to the host `Window.SizeChanged` event and defers `InvalidateMeasure` at `DispatcherQueuePriority.Low` so the map view and nav panel resize correctly on window maximize/restore without requiring a tab switch
- **Sample: custom style URL** — MAUI sample (`BasicMapPage`) adds a custom style URL `Entry` + Apply button; WPF sample (`WpfExample`) adds a second toolbar row with a preset style picker `ComboBox` and a freeform URL `TextBox`

### 🐞 Bug fixes

## 3.0.0
### ⚠️ Breaking changes
- **Package rename** — All NuGet packages renamed from `Maui.MapLibre.*` to `MapLibreNative.Maui.*` because the `Maui.MapLibre` prefix is reserved on NuGet.org by another party. Update `PackageReference` entries:
  - `Maui.MapLibre.Native` → `MapLibreNative.Maui`
  - `Maui.MapLibre.Native.Vulkan` → `MapLibreNative.Maui.Vulkan`
  - `Maui.MapLibre.WPF` → `MapLibreNative.Maui.WPF`
  - `Maui.Maplibre.Handlers` → `MapLibreNative.Maui.Handlers`
- **Namespace rename** — All C# namespaces updated. Update `using` directives and XAML `clr-namespace:` / `assembly=` references accordingly:
  - `Maui.MapLibre.Native` → `MapLibreNative.Maui`
  - `Maui.MapLibre.Native.Upstream` → `MapLibreNative.Maui` (Vulkan package now shares the same namespace as the base package)
  - `Maui.MapLibre.WPF` → `MapLibreNative.Maui.WPF`
  - `Maui.MapLibre.Handlers` (and sub-namespaces) → `MapLibreNative.Maui.Handlers`
- **Old binding layer removed** — `bindings/MlnMap.cs`, `MlnRuntime.cs`, and `NativeMethods.Mln.cs` (legacy compatibility wrappers) are deleted; callers must use the `MbglMap`/`MbglRunLoop`/`NativeMethods` types directly

### ✨ Features and improvements
- **Documentation site** — DocFX GitHub Pages site added under `docs/`; published automatically via new `docs.yml` workflow on every push to `main`
- **Vulkan package alignment** — `MapLibreNative.Maui.Vulkan` now shares the same `mln-cabi` ABI and `MapLibreNative.Maui` namespace as the base package; FFI sketch files removed
- **WPF attribution plain text** — `MlnMapHost` now strips HTML tags and decodes HTML entities (`&copy;` → ©, `&amp;`, `&lt;`, `&gt;`, `&quot;`, `&nbsp;`, `&reg;`, `&trade;`) before displaying the attribution string, so the popup shows readable text instead of raw markup
- **WPF attribution bounds** — Attribution popup `MaxWidth` is constrained to the map width minus margins; if the text would overflow the right edge the popup is repositioned to stay within the control bounds
- **WPF popup follow-window** — Navigation and attribution popups now reopen via `Dispatcher.BeginInvoke(DispatcherPriority.Render)` on `LocationChanged` so they track the window when it is dragged to a new screen position
- **CI concurrency** — Release workflow now cancels any in-progress run for the same branch when a new push arrives

### 🐞 Bug fixes
- **CI NuGet push error handling** — Pack/push steps now emit explicit success/failure messages and propagate exit codes correctly

## 2.0.2
### ✨ Features and improvements
- **WPF custom cursors** — `MlnMapHost` handles `WM_SETCURSOR` to show a hand cursor (`IDC_HAND`) while hovering and a move cursor (`IDC_SIZEALL`) while dragging the map

### 🐞 Bug fixes

## 2.0.1
### ✨ Features and improvements
- **C ABI typed handles** — `mbgl_map_t*`, `mbgl_frontend_t*`, `mbgl_runloop_t*` are now distinct opaque types; eliminates handle mix-up bugs at compile time (see 1.2.0 for full details, landed here)
- **Status codes, debug options, log callback** — see 1.2.0 feature list; all shipped in this release

### 🐞 Bug fixes
- **Windows MAUI DPI fallback** — `GetDpiForWindow` returns a scale factor (e.g. `1.25`), not raw DPI; the fallback when no window is available was incorrectly `96.0f` (which set `_pixelRatio` to 96×), corrected to `1.0f`
- **WPF popup DPI scaling** — Navigation and attribution popups switched from `PlacementMode.AbsolutePoint` + `PointToScreen` to `PlacementMode.Relative`; `AbsolutePoint` double-scaled logical offsets at DPI settings above 100%
- **WPF DPI on resize** — `_dpi` is now refreshed inside `OnRenderSizeChanged` so moving the window to a monitor with a different DPI (e.g. laptop screen → 4K external) keeps physical pixel dimensions in sync

## 2.0.0
### ✨ Features and improvements
- **Prefix rename: `mbgl` → `mln`** — All C ABI filenames, macros, and symbols updated: `mbgl_cabi.h/cpp` → `mln_cabi.h/cpp`, `MBGL_CABI_API/NOEXCEPT/EXPORT` → `MLN_CABI_API/NOEXCEPT/EXPORT`, `mbgl_cabi_version()` → `mln_cabi_version()`
- **WPF control rename** — `MbglMapHost` → `MlnMapHost` (class and file `wpf/MlnMapHost.cs`)
- **Sample CI artifacts** — CI now uploads downloadable artifacts for all sample apps:
  - `windows-samples-ci` — `WpfExample-win-x64` and `WpfExample-win-arm64` zips
  - `sample-android-apk` — signed Android APK from the MAUI sample
  - `sample-windows-maui` — self-contained Windows MAUI app (via `dotnet publish`)

### 🐞 Bug fixes
- **Windows MAUI initial black map** — `SetStyleString` now always stores `_styleString` even when `_map` is not yet initialized, so `TryInitialize` (fired on `View.Loaded`) picks up the style URL set by the MAUI property mapper before the view is ready
- **WpfExample crash on launch** — `mln-cabi.dll` is placed by NuGet in `native\win-x64\` (RID layout) but P/Invoke could not find it. Added `NativeLibrary.SetDllImportResolver` in `NativeMethods` static constructor to probe `<AppDir>\native\<rid>\mln-cabi.dll` before falling back to the OS search order
- **CI vcpkg cache abort** — `actions/cache@v4` restore failure on Windows native builds was killing all downstream steps. Added `continue-on-error: true` to the cache step in `native-windows.yml` and `native-windows-vulkan.yml` so a cache miss falls through to a clean vcpkg build
- **CI NuGet local feed path** — `dotnet nuget add source local-feed` used a relative path that NuGet resolved against the user profile instead of the workspace. Fixed to use `"${{ github.workspace }}\local-feed"` (absolute path)

## 1.2.0
### ✨ Features and improvements
- **C ABI typed handles** — `mbgl_map_t*`, `mbgl_frontend_t*`, `mbgl_runloop_t*` are now distinct opaque types; eliminates handle mix-up bugs at compile time
- **Status codes** — All mutating C ABI functions now return `mbgl_status_t` (`0 = OK`, `1 = NullHandle`, `2 = InvalidArg`); C# P/Invokes return `MbglStatus` enum
- **Debug overlays** — `mbgl_map_get_debug_options` / `mbgl_map_set_debug_options` exposed; `IMapLibreMapController.GetDebugOptions()` / `SetDebugOptions(int)` added to all platforms
- **Style inspection** — `IMapLibreMapController.GetStyleUrl()`, `GetStyleSourceIds()`, `GetStyleLayerIds()` added to all platforms
- **Layer read-back and visibility** — `IMapLibreMapController.GetLayerPaintProperty`, `GetLayerLayoutProperty`, `GetLayerVisibility`, `SetLayerVisibility` added to all platforms
- **Source attribution** — `MbglSource.GetAttribution()` returns the TileJSON attribution string
- **Log callback** — `NativeMethods.InstallLogCallback(LogFn)` lets the host intercept MapLibre native log messages; `MbglLogLevel` enum provided
- **`noexcept` guarantees** — All C ABI entry points are marked `noexcept`; exceptions are caught internally and surfaced via `mbgl_get_last_error()`
- Sample app: debug overlay toggle switch (TileBorders + Collision) demonstrates `SetDebugOptions` at runtime

## 1.1.1
### 🐞 Bug fixes
- Fixed native DLLs missing from NuGet package: `Pack=true` / `PackagePath` items were inside a TFM-conditioned `ItemGroup` that NuGet silently skips during the outer (multi-targeting) build pass. Moved those declarations to an unconditional `ItemGroup`; `CopyToOutputDirectory` remains TFM-conditioned for local builds. `runtimes/win-x64/native/mln-cabi.dll` and `runtimes/win-arm64/native/mln-cabi.dll` are now correctly included in `Maui.MapLibre.Native`.

## 1.1.0
### ✨ Features and improvements
- Camera operations exposed on `IMapLibreMapController`: `FlyTo`, `EaseTo`, `JumpTo` (bearing + pitch), `SetCameraTargetBounds` (min/max zoom + pitch)
- Camera read-back: `GetZoom()`, `GetBearing()`, `GetPitch()`, `GetCenter()`
- Projection helpers: `LatLngToScreenPoint`, `ScreenPointToLatLng`
- Queries: `QueryRenderedFeaturesAtPoint`, `QueryRenderedFeaturesInBox` (returns GeoJSON FeatureCollection string)
- Observer wiring fixed — all 14 `MapObserver` virtuals now correctly routed to the C callback via `CabiMapObserver`
- Light API: `MbglStyle.SetLightProperty(name, valueJson)` — anchor, color, intensity, position
- Transition options: `MbglStyle.SetTransition(durationMs, delayMs)`
- `CancelTransitions()` + `IsFullyLoaded` on `MbglMap`
- **Tier 1** — Interactive movement: `SetGestureInProgress`, `MoveBy`, `RotateBy`, `PitchBy` (all with optional animation duration)
- **Tier 1** — Map option post-create setters: `SetNorthOrientation`, `SetConstrainMode`, `SetViewportMode`
- **Tier 1** — Bounds read-back: `GetBounds()` → `BoundOptions` record (lat/lng box + zoom/pitch limits, NaN for unset)
- **Tier 1** — Style enumeration: `GetUrl()`, `GetName()`, `GetSourceIds()`, `GetLayerIds()`, `GetLayer(id)`, `GetSource(id)`
- **Tier 1** — Layer read-back: `GetPaintProperty(name)`, `GetLayoutProperty(name)`, `GetVisibility()`
- **Tier 2** — Tile LOD controls: `SetTileLodMinRadius`, `SetTileLodScale`, `SetTileLodPitchThreshold`, `SetTileLodZoomShift`, `SetTileLodMode` (0=Default, 1=Distance)
- **Tier 2** — Tile prefetch: `SetPrefetchZoomDelta` / `GetPrefetchZoomDelta`
- **Tier 2** — Camera fit to point set: `CameraForLatLngs(points, padding)` → `CameraResult`
- **Tier 2** — Batch projection: `PixelsForLatLngs(points)`, `LatLngsForPixels(pixels)`
- All new APIs surface on `IMapLibreMapController` and are implemented in all three platform controllers (Android, iOS/macCatalyst, Windows)

### 🐞 Bug fixes
- Memory leak in `mbgl_map_destroy`: `frontend.release()` → `frontend.reset()`

## 1.0.0

### ✨ Features and improvements

- Unified all platforms (Android, iOS, macCatalyst, Windows) to a single flat C ABI (`mln-cabi`) — removes all legacy Xamarin `Org.Maplibre.*` binding dependencies
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

### 🐞 Bug fixes

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
