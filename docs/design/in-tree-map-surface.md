# Design: making the map surface (and its controls) first-class on every platform

**Status:** in progress. **Scope:** the host/controller layer per platform. No change to the `mln-cabi` C ABI.

**Implementation status**
- ✅ **WPF — `D3DImage`:** implemented as `MlnMapImage` + `GlDxInteropContext` (Vortice.Direct3D9). Renders MapLibre into a shared D3D9Ex surface via `WGL_NV_DX_interop2`, presents through `D3DImage`, and hosts all three on-map controls (nav / GPS / attribution) as real WPF children — full 4-way d-pad (rotate/pitch/reset-north) + live compass tick, GPS tracking modes + location indicator ("blue dot"), and attribution fetch/expand match `MlnMapHost`. Corner-positioning DPs, source/layer API, `RotateBy`/`PitchBy`, and `CameraIdle` event all at parity with `MlnMapHost`. `LayoutPropertyNames` expanded to the full symbol/line/fill/circle set. Kept alongside `MlnMapHost` (the default). Needs on-GPU validation of the interop path.
- ✅ **MAUI Windows — `SwapChainPanel`:** `SwapChainMapView` + `GlDxgiInteropContext` (Vortice.Direct3D11/DXGI + `ISwapChainPanelNative`), integrated into `MapLibreMapController.Windows` behind `UseSwapChainPanel` / `MAPLIBRE_WIN_RENDERER=swapchain`. mbgl renders through the GL→DXGI bridge (FBO backed by a shared D3D11 offscreen texture via `WGL_NV_DX_interop2`, `CopyResource`'d into the composition swap chain's back buffer, then Present; element-level vertical flip). All three on-map controls (nav / GPS / attribution) are real XAML children wired to the existing renderer-agnostic logic. Needs on-GPU validation; `WS_POPUP` remains the default.
- ✅ **Android (`TextureView`)**: swapped `SurfaceView` for `TextureView` in `MapLibreMapController.Android`. `TextureView` is an ordinary in-tree `View` backed by a `SurfaceTexture`; `SurfaceCallback`/`ISurfaceHolderCallback` replaced by `TextureSurfaceListener`/`ISurfaceTextureListener`. Surface lifecycle: `OnSurfaceTextureAvailable` wraps the `SurfaceTexture` in an `Android.Views.Surface`, acquires the `ANativeWindow`, and calls `InitMaplibre`; `OnSurfaceTextureDestroyed` disposes the `Surface` + releases the native window. No more compositing hole for sibling MAUI content.
- ✅ **iOS/mac (already in-tree):** no change needed.

## Problem

The complaint — *"the map and its controls feel like a hovering popup instead of really being part of the map"* — is a symptom of one underlying pattern: **MapLibre renders into a native GPU surface, and everything else (nav / GPS / attribution controls, and anything the app wants to draw over the map) has to be layered on top of that surface.** How cleanly that layering works depends entirely on the platform's compositor, and it ranges from "already fine" to "top-level popup window hacks."

It is worst on the two desktop compositors that enforce **airspace** (a native GPU child/owned window always draws above framework-drawn content in the same region):

| Platform | Map surface today | Controls today | Compositing reality |
|---|---|---|---|
| **WPF** | `HwndHost` child HWND, WGL/OpenGL ([`MlnMapHost.cs`](../../wpf/MlnMapHost.cs)) | WPF `Popup` objects (separate top-level windows) | **Airspace.** WPF content cannot draw over the HWND, so controls must be popups → don't clip to the map, float over other apps, need constant reposition/hide babysitting. |
| **MAUI Windows** | Borderless **top-level `WS_POPUP`** window tracked to the main window, WGL/OpenGL ([`MapLibreMapController.Windows.cs:546`](../../handlers/MapLibreMapController.Windows.cs#L546)) | Nav/GPS panels are also `WS_POPUP` windows ([`:1185`](../../handlers/MapLibreMapController.Windows.cs#L1185)) | **Airspace, worst case.** Even the *map itself* is a floating window chasing the parent; XAML never sees the pointer over the map. |
| **Android** | `SurfaceView` + native subview controls in a `FrameLayout` ([`MapLibreMapController.Android.cs:369`](../../handlers/MapLibreMapController.Android.cs#L369)) | Native views `AddView`'d into the container | **Mostly fine.** `SurfaceView` punches a compositing hole (z-order caveats vs. sibling MAUI content), but the built-in controls are real in-tree subviews so they layer correctly over the map. |
| **iOS / Mac Catalyst** | Metal `MTKView`/`CAMetalLayer` in a container `UIView` ([`MapLibreMapController.MaciOS.cs`](../../handlers/MapLibreMapController.MaciOS.cs)) | Native views `AddSubview`'d | **Clean.** UIKit/AppKit have no airspace rule; subviews composite over the Metal view normally. |

So: **iOS/mac are already right, Android is acceptable but has a hole, and WPF + MAUI Windows both need real work.** The official .NET MAUI Windows map handler avoids all of this only because it hosts a WinUI 3 `MapControl` (a real XAML element) and does no native rendering itself — it is not a reference for fixing native-GPU compositing.

## Goal

Make the map surface a **real, in-tree visual** on each platform so that:

1. Built-in controls become ordinary in-tree elements (correct z-order, clipping, hit-testing, DPI, transforms) and the popup/owned-window babysitting is deleted; and
2. **App content can be drawn over the map** as normal framework content — which is the prerequisite for the higher-level overlay elements in the companion section below.

The public API of `MlnMapHost` (WPF) and the MAUI `MapLibreMap`/controller stays source-compatible so the samples and VistumblerCS need no changes.

## Per-platform approach

The common idea everywhere: **render MapLibre into an offscreen framebuffer and hand that texture to the platform's in-tree compositor**, instead of exposing a native child/owned window.

### WPF — `D3DImage` via WGL↔DX interop

`D3DImage` is a WPF `ImageSource` backed by a D3D9 surface the WPF compositor samples. Bridge OpenGL→D3D on the GPU with `WGL_NV_DX_interop2` (no CPU round-trip):

```
MapLibre (mln-cabi) -> GL FBO -> [wglDXLock] shared D3D9Ex surface -> D3DImage -> WPF visual tree
```

1. Create a **D3D9Ex** device; make a `D3DUSAGE_RENDERTARGET` texture in `D3DFMT_A8R8G8B8`; take its `IDirect3DSurface9`.
2. `wglDXOpenDeviceNV` + `wglDXRegisterObjectNV` to expose that surface as a GL renderbuffer/texture.
3. Per render tick: `wglDXLockObjectsNV` → bind the FBO → `mln_frontend_render` → `wglDXUnlockObjectsNV`; then `D3DImage.Lock` / `SetBackBuffer` / `AddDirtyRect` / `Unlock`.
4. Change `MlnMapHost` from `HwndHost` to a templated `Control`/`Border` whose content is an `Image` (the `D3DImage`) with the control overlays as **sibling WPF children**.
Input moves from the `WndProc` switch to WPF routed events (`OnMouseDown/Move/Up/Wheel`, `CaptureMouse`); cursor via `this.Cursor`.

### MAUI Windows — `SwapChainPanel` (the WinUI analogue)

`SwapChainPanel` is a first-class XAML element that owns a DXGI swap chain — the correct in-tree GPU surface for WinUI, and the proper replacement for the `WS_POPUP` hack. Two rendering options:

- **ANGLE** (`libEGL`/`libGLESv2` over D3D11): MapLibre's GL renders through ANGLE into a swap chain bound to the `SwapChainPanel` via `ISwapChainPanelNative::SetSwapChain`. Reuses the existing GL frontend.
- **Same WGL→DXGI shared-texture bridge** as the WPF path, presented into the panel's swap chain.

Result: the map is a normal XAML element; nav/GPS/attribution become XAML children (or MAUI views), and the owned-window tracking, `PopupWndProc`, and per-tick realignment all go away. Pointer/keyboard arrive as normal XAML events.

### Android — prefer `TextureView` (or fix `SurfaceView` z-order)

`SurfaceView` composites in a separate layer (the hole). Options, cheapest first:

- Keep `SurfaceView` but set `setZOrderMediaOverlay(true)` / manage `setZOrderOnTop` so sibling content layers predictably. Lowest effort; keeps current perf.
- Move to **`TextureView`**, an ordinary in-tree `View` (renders to a SurfaceTexture the view hierarchy composites) — no hole, arbitrary content and transforms above it, at a modest GPU-copy cost. This is the "truly part of the tree" option and matches the desktop fixes conceptually.

Either way the built-in controls stay real subviews; the change is about letting **MAUI/app content** overlay reliably.

### iOS / Mac Catalyst — already in-tree; optional polish

The `MTKView` is a normal subview and UIKit/AppKit have no airspace rule, so overlays already composite. The only open question is stylistic: whether the built-in controls should remain native subviews or be reprojected as MAUI content for consistency with the other platforms (see below).

## Migration strategy (applies to WPF + MAUI Windows)

Keep both renderers behind the existing public surface so we can ship incrementally and A/B them:

1. Extract the public API into a small interface / abstract base implemented by the current native-window renderer **and** the new in-tree renderer.
2. Add a feature flag / factory (e.g. `RendererMode`) defaulting to the current implementation until the in-tree path is proven.
3. Port the built-in controls off popups/owned-windows to in-tree children **only** in the new renderer.
4. Once at parity (input, DPI, multi-monitor move, resize, style reload, GPS/attribution), flip the default and deprecate the old path.

## Risks & open questions

- **GPU interop availability.** `WGL_NV_DX_interop2` is broadly supported but not universal (rare on pure software/RDP GL); keep the native-window path as a fallback, or a `glReadPixels`→`WriteableBitmap` slow path. ANGLE on WinUI has its own device-loss handling to get right.
- **Resize / mixed-DPI / multi-monitor.** Surfaces must be recreated on size/DPI change; front-load testing on the multi-monitor cases the current WPF/Windows code handles explicitly.
- **Frame pacing.** In-tree presentation (D3DImage dirty-rects, DXGI present) has different vsync/tearing behavior than `SwapBuffers`; retune the `DispatcherTimer(Render,16ms)` cadence against the framework compositor.
- **Android hole vs. perf.** `TextureView` copies each frame; measure vs. `SurfaceView` before switching.

## Effort estimate

WPF `D3DImage` prototype (happy-path interop + input, no fallback): ~1–2 focused days. MAUI Windows `SwapChainPanel`/ANGLE prototype: similar-to-larger (device-loss + ANGLE bring-up). Android `TextureView` swap: small. iOS/mac: none required. Production parity across desktop (fallbacks, DPI/multi-monitor, controls ported, A/B flag): meaningfully more — build prototypes on a branch behind the factory flag first.

---

# Companion: adopting MAUI's over-the-map overlay elements

The above makes it *possible* to draw over the map cleanly. The second question is *what* to draw with. Today maplibre-maui-ac exposes only the **low-level** MapLibre model — `Sources` (`GeoJsonSource`, `VectorSource`, …) and `Layers` (`CircleLayer`, `FillLayer`, `LineLayer`, …) plus raw controller calls. There is **no high-level, declarative "put a marker / line / shape here"** element.

.NET MAUI Maps provides exactly that, as a small, well-shaped, MIT-licensed model worth adopting:

| MAUI element | Shape | Public surface |
|---|---|---|
| [`Pin`](../../../maui/src/Controls/Maps/src/Pin.cs) | Marker | `Location`, `Label`, `Address`, `PinType`, `MarkerId`; `MarkerClicked` |
| [`Polyline`](../../../maui/src/Controls/Maps/src/Polyline.cs) | Line | `Geopath : IList<Location>`, `StrokeColor`, `StrokeWidth` |
| [`Polygon`](../../../maui/src/Controls/Maps/src/Polygon.cs) | Filled area | `Geopath`, `FillColor`, `StrokeColor`, `StrokeWidth` |
| [`Circle`](../../../maui/src/Controls/Maps/src/Circle.cs) | Circle | `Center : Location`, `Radius : Distance`, `FillColor`, `StrokeColor`, `StrokeWidth` |

They hang off two `ObservableCollection`s on the map — `Map.Pins` and `Map.MapElements` — so XAML stays declarative and data-bindable ([`Map.cs`](../../../maui/src/Controls/Maps/src/Map.cs)).

### Why this fits us well

- It's a thin **adapter over the layers/sources we already have** — each element compiles down to a GeoJSON source + a style layer:
  - `Polyline` → GeoJSON `LineString` + `LineLayer`
  - `Polygon` → GeoJSON `Polygon` + `FillLayer` (+ `LineLayer` for the stroke)
  - `Pin` → GeoJSON `Point` + `SymbolLayer` (or the existing marker/annotation path)
  - **`Circle` → `GeographyUtils.ToCircumferencePositions(center, radius)` → GeoJSON `Polygon` + `FillLayer`.** We already ported `ToCircumferencePositions` and `Distance` into `bindings/Geometry`, so `Circle` is essentially free.
- It gives MAUI developers the API shape they already know from `Microsoft.Maui.Controls.Maps`, easing migration.
- Because these elements render as ordinary MapLibre style layers *inside* the surface, they composite correctly on **every** platform today — independent of the compositing work above.

### Proposed sample additions

Add pages to `sample/` (and matching XAML in `sample/WpfExample`) that exercise the new elements:

- **ShapesPage** — a `Polyline` route, a `Polygon` region, and a `Circle` radius (bindable center/radius) declared entirely in XAML.
- Extend the existing **MarkersPage** to use the declarative `Pin` element with `MarkerClicked`, instead of low-level source/layer calls.

### Suggested implementation order

1. `Circle` first — the geometry primitive is already in place; smallest end-to-end proof.
2. `Polyline` / `Polygon` — straight GeoJSON + line/fill layers.
3. `Pin` — reconcile with the existing marker/annotation path so there's one marker story.
4. Sample pages for each, on both MAUI and WPF.

This is a feature, not a refactor, and is independent of the compositing work above — it can land first.

**Implemented so far:** `Pin`, `Polyline`, `Polygon`, `Circle` under `handlers/Overlays` (declarative `StyleView` children; `Circle` uses the ported `GeographyUtils`/`Distance`). `SymbolLayerProperties` fully implemented (layout + paint properties, `ToDictionary()`/`FromJson()`). `LayoutPropertyNames` in both WPF renderers expanded to match the Windows/Android controller's comprehensive set.

### Follow-up: proper marker via SymbolLayer + sprite (not the legacy annotation API)

`Pin` currently renders as a **circle marker**. MapLibre's legacy `mbgl` annotation API (`SymbolAnnotation` etc.) is *not* exposed through `mln-cabi` and is deprecated upstream anyway — so it is not the path. The correct native marker is a **SymbolLayer with a registered sprite image**, and the cabi already exposes the key primitive: [`mbgl_style_add_image`](../../native/include/mln_cabi.h) → `MbglStyle.AddImage`. Upgrading `Pin` to a real icon + text-label marker requires:

1. ✅ ~~Implement `SymbolLayerProperties.ToDictionary()` (currently a stub that throws)~~ — **Done.** Full layout + paint property set (`icon-image/size/anchor/allow-overlap/offset/rotate`, `text-field/font/size/anchor/offset/halo-*/transform/max-width`, etc.) implemented with `ToDictionary()` and `FromJson()`.
2. ✅ ~~Surface `AddImage` (sprite registration) through `IMapLibreMapController` / `MapLibreMap` on Android, iOS/mac and Windows~~ — **Done.** `AddSpriteImage(imageId, width, height, rgba, pixelRatio, sdf)` and `RemoveSpriteImage(imageId)` added to `IMapLibreMapController`, implemented in all three platform controllers, and exposed on `MapLibreMap`.
3. \u2705 ~~Register a default marker sprite (or let `Pin` supply an image) and switch `Pin` to a SymbolLayer, keeping `Label`/`Address` as `text-field`~~ \u2014 **Done.** `Pin` generates a 24\u00d724 premultiplied-RGBA white antialiased circle at startup, registers it as `"mln_marker"` (SDF, `pixelRatio=1`) via `AddSpriteImage` on each `BuildOverlay`, then renders via `SymbolLayerProperties`: `icon-anchor:"bottom"`, `icon-color` from `TintColor`, `icon-halo-*` from `StrokeColor`/`StrokeWidth`, and an optional `text-field` / `text-*` block when `Label` is set. `CircleLayer` code removed.

This is a cross-platform task (touches the per-platform controllers), which is why the circle marker shipped as the working interim.

## References

- WPF `D3DImage`: <https://learn.microsoft.com/dotnet/api/system.windows.interop.d3dimage>
- WinUI `SwapChainPanel`: <https://learn.microsoft.com/uwp/api/windows.ui.xaml.controls.swapchainpanel>
- ANGLE (GL/GLES over D3D): <https://github.com/google/angle>
- `WGL_NV_DX_interop2`: <https://registry.khronos.org/OpenGL/extensions/NV/NV_DX_interop2.txt>
- Android `TextureView`: <https://developer.android.com/reference/android/view/TextureView>
- Airspace overview: <https://learn.microsoft.com/dotnet/desktop/wpf/advanced/technology-regions-overview>
- MAUI overlay elements: [`Pin`](../../../maui/src/Controls/Maps/src/Pin.cs), [`Polyline`](../../../maui/src/Controls/Maps/src/Polyline.cs), [`Polygon`](../../../maui/src/Controls/Maps/src/Polygon.cs), [`Circle`](../../../maui/src/Controls/Maps/src/Circle.cs)
