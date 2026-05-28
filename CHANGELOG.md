# Changelog

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

## master
### ✨ Features and improvements
- _...Add new stuff here..._

### 🐞 Bug fixes
- _...Add new stuff here..._

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
