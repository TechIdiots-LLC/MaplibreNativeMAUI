# Design: making the map surface (and its controls) first-class on every platform

**Status:** complete — the in-tree renderers are the only renderers; the old airspace-based paths (WPF `HwndHost`/`MlnMapHost` and MAUI Windows `WS_POPUP`) have been **removed**. **Scope:** the host/controller layer per platform. No change to the `mln-cabi` C ABI. (The sections below on the "old" surfaces and the migration strategy are retained as design rationale.)

**Implementation status**
- ✅ **WPF — `WriteableBitmap`:** implemented as `MlnMapImage` + `HiddenWglContext`. MapLibre renders into FBO 0 of a hidden, properly-sized Win32 window (`SetWindowPos`); after each frame `glReadPixels(GL_BGRA)` transfers pixels directly into a WPF `WriteableBitmap.BackBuffer` (Lock / AddDirtyRect / Unlock) displayed in a WPF `Image`. All three on-map controls (nav / GPS / attribution) are real WPF children. Earlier design targeted `D3DImage` via `WGL_NV_DX_interop2` + D3D9 — see [Why `glReadPixels`?](#why-glreadpixels-instead-of-wgl_nv_dx_interop2) below.
- ✅ **MAUI Windows — `WriteableBitmap`:** implemented as `MapImageView` + `HiddenWglContext`. Same hidden-window + `glReadPixels` approach; pixels written into a WinUI `WriteableBitmap` via `IBufferByteAccess`, displayed in a WinUI `Image` element. All three on-map controls are real XAML children wired to the renderer-agnostic logic. Earlier design targeted `SwapChainPanel` + D3D11 + `WGL_NV_DX_interop2` — same root cause; see below.
- ✅ **Android (`TextureView`)**: swapped `SurfaceView` for `TextureView` in `MapLibreMapController.Android`. `TextureView` is an ordinary in-tree `View` backed by a `SurfaceTexture`; `SurfaceCallback`/`ISurfaceHolderCallback` replaced by `TextureSurfaceListener`/`ISurfaceTextureListener`. Surface lifecycle: `OnSurfaceTextureAvailable` wraps the `SurfaceTexture` in an `Android.Views.Surface`, acquires the `ANativeWindow`, and calls `InitMaplibre`; `OnSurfaceTextureDestroyed` disposes the `Surface` + releases the native window. No more compositing hole for sibling MAUI content.
- ✅ **iOS/mac (already in-tree):** no change needed.

## Problem

The complaint — *"the map and its controls feel like a hovering popup instead of really being part of the map"* — is a symptom of one underlying pattern: **MapLibre renders into a native GPU surface, and everything else (nav / GPS / attribution controls, and anything the app wants to draw over the map) has to be layered on top of that surface.** How cleanly that layering works depends entirely on the platform's compositor, and it ranges from "already fine" to "top-level popup window hacks."

It is worst on the two desktop compositors that enforce **airspace** (a native GPU child/owned window always draws above framework-drawn content in the same region):

| Platform | Map surface today | Controls today | Compositing reality |
|---|---|---|---|
| **WPF** | `HwndHost` child HWND, WGL/OpenGL (`MlnMapHost.cs`, removed in 4.0.0) | WPF `Popup` objects (separate top-level windows) | **Airspace.** WPF content cannot draw over the HWND, so controls must be popups → don't clip to the map, float over other apps, need constant reposition/hide babysitting. |
| **MAUI Windows** | Borderless **top-level `WS_POPUP`** window tracked to the main window, WGL/OpenGL (`MapLibreMapController.Windows.cs`, removed in 4.0.0) | Nav/GPS panels are also `WS_POPUP` windows (also removed in 4.0.0) | **Airspace, worst case.** Even the *map itself* is a floating window chasing the parent; XAML never sees the pointer over the map. |
| **Android** | `SurfaceView` + native subview controls in a `FrameLayout` ([`MapLibreMapController.Android.cs`](https://github.com/TechIdiots-LLC/MaplibreNativeMAUI/blob/main/handlers/MapLibreMapController.Android.cs)) | Native views `AddView`'d into the container | **Mostly fine.** `SurfaceView` punches a compositing hole (z-order caveats vs. sibling MAUI content), but the built-in controls are real in-tree subviews so they layer correctly over the map. |
| **iOS / Mac Catalyst** | Metal `MTKView`/`CAMetalLayer` in a container `UIView` ([`MapLibreMapController.MaciOS.cs`](https://github.com/TechIdiots-LLC/MaplibreNativeMAUI/blob/main/handlers/MapLibreMapController.MaciOS.cs)) | Native views `AddSubview`'d | **Clean.** UIKit/AppKit have no airspace rule; subviews composite over the Metal view normally. |

So: **iOS/mac are already right, Android is acceptable but has a hole, and WPF + MAUI Windows both need real work.** The official .NET MAUI Windows map handler avoids all of this only because it hosts a WinUI 3 `MapControl` (a real XAML element) and does no native rendering itself — it is not a reference for fixing native-GPU compositing.

## Goal

Make the map surface a **real, in-tree visual** on each platform so that:

1. Built-in controls become ordinary in-tree elements (correct z-order, clipping, hit-testing, DPI, transforms) and the popup/owned-window babysitting is deleted; and
2. **App content can be drawn over the map** as normal framework content — which is the prerequisite for the higher-level overlay elements in the companion section below.

The MAUI `MapLibreMap`/controller public API is unchanged. On WPF the replacement control
`MlnMapImage` keeps the same public surface as the old `MlnMapHost` (properties, methods, events),
so consumers migrate with a one-line control swap (`MlnMapHost` → `MlnMapImage`).

## Per-platform approach

The common idea everywhere: **render MapLibre into an offscreen framebuffer and hand that texture to the platform's in-tree compositor**, instead of exposing a native child/owned window.

### WPF — `WriteableBitmap` via `glReadPixels`

The original plan called for `D3DImage` backed by a `WGL_NV_DX_interop2` shared D3D9 surface (GPU→GPU, no CPU round-trip). That design worked at the C# layer but produced a blank map at runtime because of a root-cause constraint in the native code — see [Why `glReadPixels`?](#why-glreadpixels-instead-of-wgl_nv_dx_interop2) below.

The implemented path:

```
MapLibre (mln-cabi) → GL FBO 0 (hidden Win32 window) → glReadPixels(GL_BGRA) → WriteableBitmap.BackBuffer → WPF Image
```

1. Create a **hidden Win32 window** (`WS_POPUP`, `CS_OWNDC`, single-buffered PFD) and a WGL context on it. MapLibre uses this window's DC as its surface handle.
2. `SetWindowPos` resizes the hidden window to match the map control dimensions on every resize — because the window is single-buffered, FBO 0 immediately reflects the new size.
3. Per render tick: `SetWindowPos` (if resized) → `wglMakeCurrent` → `glViewport` → `mln_frontend_render` → `WriteableBitmap.Lock()` → `glReadPixels(0,0,w,h, GL_BGRA, GL_UNSIGNED_BYTE, bitmap.BackBuffer)` → `bitmap.AddDirtyRect` → `bitmap.Unlock()`.
4. The `WriteableBitmap` is the source of a WPF `Image` element. A `ScaleTransform(1, -1)` on the `Image` corrects the GL bottom-left origin to WPF's top-left. Nav/GPS/attribution controls are ordinary sibling WPF children of the same container.

**Trade-off vs. the D3DImage plan:** `glReadPixels` is a GPU→CPU transfer (pipeline stall + DMA to system RAM) on every frame. For typical map interactions this is invisible; for high-FPS animation it may add a few milliseconds of latency. The benefit is **zero hardware dependencies**: no `WGL_NV_DX_interop2`, no D3D9, no Vortice packages — works on any GPU driver including software renderers and RDP.

### MAUI Windows — `WriteableBitmap` via `glReadPixels`

The original plan called for `SwapChainPanel` + D3D11 (either ANGLE or a WGL→DXGI shared-texture bridge). That design also produced a blank map at runtime for the same root-cause reason — see [Why `glReadPixels`?](#why-glreadpixels-instead-of-wgl_nv_dx_interop2) below.

The implemented path is identical in concept to the WPF path:

```
MapLibre (mln-cabi) → GL FBO 0 (hidden Win32 window) → glReadPixels(GL_BGRA) → WriteableBitmap (IBufferByteAccess) → WinUI Image
```

1. `HiddenWglContext` owns the same hidden-window + WGL context as the WPF control's `HiddenWglContext`.
2. `MapImageView` (named `SwapChainMapView` before 4.0.0) hosts a WinUI `Image` element backed by a `Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap`.
3. Per render tick: `wglMakeCurrent` → `glViewport` → `mln_frontend_render` → obtain the pixel buffer pointer via `IBufferByteAccess::Buffer` → `glReadPixels(... ptr)` → `bitmap.Invalidate()`.
4. `IBufferByteAccess` (GUID `905a0fef-bc53-11df-8c49-001e4fc686da`) is the WinRT COM interface for direct byte access to a `Windows.Storage.Streams.IBuffer` — the only way to write pixels into a WinUI `WriteableBitmap` without a second copy.
5. The same `ScaleTransform(1, -1)` flip is applied to the `Image` element. Nav/GPS/attribution overlays are ordinary XAML children.

Result: the map is a normal XAML element; nav/GPS/attribution become XAML children, and the owned-window tracking, `PopupWndProc`, and per-tick realignment all go away. Pointer/keyboard arrive as normal XAML events.

### Android — prefer `TextureView` (or fix `SurfaceView` z-order)

`SurfaceView` composites in a separate layer (the hole). Options, cheapest first:

- Keep `SurfaceView` but set `setZOrderMediaOverlay(true)` / manage `setZOrderOnTop` so sibling content layers predictably. Lowest effort; keeps current perf.
- Move to **`TextureView`**, an ordinary in-tree `View` (renders to a SurfaceTexture the view hierarchy composites) — no hole, arbitrary content and transforms above it, at a modest GPU-copy cost. This is the "truly part of the tree" option and matches the desktop fixes conceptually.

Either way the built-in controls stay real subviews; the change is about letting **MAUI/app content** overlay reliably.

### iOS / Mac Catalyst — already in-tree; optional polish

The `MTKView` is a normal subview and UIKit/AppKit have no airspace rule, so overlays already composite. The only open question is stylistic: whether the built-in controls should remain native subviews or be reprojected as MAUI content for consistency with the other platforms (see below).

## Migration strategy (completed)

The in-tree renderers were built alongside the old airspace-based paths behind an opt-in flag
(`MapLibreMapController.UseSwapChainPanel` on MAUI Windows; a separate `MlnMapImage` control on
WPF), the samples were switched over, and once the in-tree paths reached parity (input, DPI,
resize, style reload, nav/GPS/attribution) they became the default and the old paths were
**removed**:

- WPF: `MlnMapHost` (the `HwndHost` + `Popup` control) deleted; `MlnMapImage` is the only WPF control.
- MAUI Windows: the `WS_POPUP` GL window + GDI-painted overlays deleted from
  `MapLibreMapController.Windows`, along with the `UseSwapChainPanel` flag; the in-tree
  `MapImageView` path (named `SwapChainMapView` before 4.0.0) is the only renderer.

## Risks & open questions

- **GPU read-back cost.** `glReadPixels` stalls the GL pipeline and transfers to system RAM every frame. For most map interactions (pan, zoom, tilt) the frame rate is render-bound and the transfer cost is negligible. For very high-frequency animation or very large windows, a future optimisation could use a PBO (pixel buffer object) to overlap transfer with the next render tick — but measure first.
- **`WGL_NV_DX_interop2` revisited.** A future maintainer wanting GPU→GPU transfer would need to patch `WGLRenderableResource::bind()` in `platform_frontend_windows.cpp` (replace the hardcoded `glBindFramebuffer(0)` with the interop FBO binding), then restore the D3D9/D3D11 interop paths. The `glReadPixels` approach is the simpler baseline that works on every driver today.
- **Resize / mixed-DPI / multi-monitor.** Surfaces must be recreated on size/DPI change; front-load testing on the multi-monitor cases the current WPF/Windows code handles explicitly.
- **Frame pacing.** `WriteableBitmap.Invalidate` / `AddDirtyRect` participates in the WPF/WinUI compositor tick; latency is one compositor frame (~16 ms at 60 Hz). Acceptable for map interaction.
- **Android hole vs. perf.** `TextureView` copies each frame; measure vs. `SurfaceView` before switching.

## Effort estimate

WPF `WriteableBitmap` prototype (hidden window + glReadPixels + input, no fallback): ~1 focused day. MAUI Windows `Image`+`WriteableBitmap` prototype: similar. Android `TextureView` swap: small. iOS/mac: none required. Production parity across desktop (fallbacks, DPI/multi-monitor, controls ported, A/B flag): meaningfully more — build prototypes on a branch behind the factory flag first.

---

## Why `glReadPixels` instead of `WGL_NV_DX_interop2`?

The original design for both WPF (`D3DImage`) and MAUI Windows (`SwapChainPanel`) relied on `WGL_NV_DX_interop2` to avoid a CPU round-trip: OpenGL would render directly into a D3D surface, which the framework compositor would sample without any pixel copy.

Both produced a blank (solid fill) map at runtime. Root cause, found in `native/src/platform_frontend_windows.cpp`:

```cpp
void WGLRenderableResource::bind() {
    _backend.setFramebufferBinding(0);   // always binds FBO 0
    _backend.setViewport(0, 0, _backend.getSize());
}
```

`WGLRenderableResource::bind()` is called at the start of every `mbgl_frontend_render` invocation. It unconditionally binds framebuffer **0** — the default framebuffer of the WGL context's surface (the hidden Win32 window). There is no path through which it binds a custom FBO created by `WGL_NV_DX_interop2`. So MapLibre's output always lands in FBO 0 of the hidden window; the D3D-backed interop FBOs were never written.

Fixing this would require patching `platform_frontend_windows.cpp` (e.g. storing the desired FBO and using it in `bind()`), which would require rebuilding the native `mln-cabi.dll`. As a simpler, dependency-free solution: **just read FBO 0**. Resize the hidden window to match the map control, render normally, then `glReadPixels` after each frame. The `glReadPixels` approach trades GPU→GPU zero-copy for a GPU→CPU transfer, but eliminates the `WGL_NV_DX_interop2` requirement, the D3D9/D3D11 device, and the Vortice NuGet dependencies entirely.

---

# Companion: adopting MAUI's over-the-map overlay elements

The above makes it *possible* to draw over the map cleanly. The second question is *what* to draw with. Today maplibre-maui-ac exposes only the **low-level** MapLibre model — `Sources` (`GeoJsonSource`, `VectorSource`, …) and `Layers` (`CircleLayer`, `FillLayer`, `LineLayer`, …) plus raw controller calls. There is **no high-level, declarative "put a marker / line / shape here"** element.

.NET MAUI Maps provides exactly that, as a small, well-shaped, MIT-licensed model worth adopting:

| MAUI element | Shape | Public surface |
|---|---|---|
| [`Pin`](https://github.com/dotnet/maui/blob/main/src/Controls/Maps/src/Pin.cs) | Marker | `Location`, `Label`, `Address`, `PinType`, `MarkerId`; `MarkerClicked` |
| [`Polyline`](https://github.com/dotnet/maui/blob/main/src/Controls/Maps/src/Polyline.cs) | Line | `Geopath : IList<Location>`, `StrokeColor`, `StrokeWidth` |
| [`Polygon`](https://github.com/dotnet/maui/blob/main/src/Controls/Maps/src/Polygon.cs) | Filled area | `Geopath`, `FillColor`, `StrokeColor`, `StrokeWidth` |
| [`Circle`](https://github.com/dotnet/maui/blob/main/src/Controls/Maps/src/Circle.cs) | Circle | `Center : Location`, `Radius : Distance`, `FillColor`, `StrokeColor`, `StrokeWidth` |

They hang off two `ObservableCollection`s on the map — `Map.Pins` and `Map.MapElements` — so XAML stays declarative and data-bindable ([`Map.cs`](https://github.com/dotnet/maui/blob/main/src/Controls/Maps/src/Map.cs)).

### Why this fits us well

- It's a thin **adapter over the layers/sources we already have** — each element compiles down to a GeoJSON source + a style layer:
  - `Polyline` → GeoJSON `LineString` + `LineLayer`
  - `Polygon` → GeoJSON `Polygon` + `FillLayer` (+ `LineLayer` for the stroke)
  - `Pin` → GeoJSON `Point` + `SymbolLayer` (or the existing marker/annotation path)
  - **`Circle` → `GeographyUtils.ToCircumferencePositions(center, radius)` → GeoJSON `Polygon` + `FillLayer`.** We already ported `ToCircumferencePositions` and `Distance` into `bindings/Geometry`, so `Circle` is essentially free.
- It gives MAUI developers the API shape they already know from `Microsoft.Maui.Controls.Maps`, easing migration.
- Because these elements render as ordinary MapLibre style layers *inside* the surface, they composite correctly on **every** platform today — independent of the compositing work above.

### Sample additions

- ✅ **ShapesPage** (`sample/ShapesPage.xaml`, registered in `AppShell`) — a `Polyline` route, a `Polygon` region and a `Circle` radius as children of `MapLibreMap`; Grow/Shrink buttons re-set `Circle.Radius` to demonstrate live rebuilds. Geometry (`Geopath`, `Center`, `Radius`) is set in code-behind because `Location`/`Distance` are not XAML literals; colours/stroke are set in XAML.
- ✅ **MarkersPage** — converted from low-level `AddGeoJsonSource`/`AddCircleLayer`/`AddSymbolLayer` calls to declarative `Pin` elements; tapping a marker identifies it via `QueryRenderedFeaturesInBox` reading the `Pin`'s `label` from the feature properties (the `MarkerClicked` pattern).
- The overlay elements are **MAUI-only** (`StyleView : ContentView`), so they are not usable from the WPF sample; `sample/WpfExample` keeps its low-level shape/marker demos via `MlnMapImage`.

**Follow-up — dynamic add/remove.** `StyleView` only materialises an overlay on a `StyleLoaded` event that fires *after* the element is parented, so overlays declared/added before the style loads work, but overlays added at runtime (after the style is already loaded) do not — which is why `MarkersPage` uses static `Pin`s rather than the previous add/remove buttons. To support runtime add/remove: add `MapLibreMap.IsStyleLoaded`, and in `StyleView.OnParentChanged` materialise immediately when the style is already loaded and remove on detach (base no-op; `MapOverlayElement` overrides to call `RemoveOverlay`). Deferred here to avoid a core-class change mid-PR without runtime testing.

### Suggested implementation order

1. `Circle` first — the geometry primitive is already in place; smallest end-to-end proof.
2. `Polyline` / `Polygon` — straight GeoJSON + line/fill layers.
3. `Pin` — reconcile with the existing marker/annotation path so there's one marker story.
4. Sample pages for each, on both MAUI and WPF.

This is a feature, not a refactor, and is independent of the compositing work above — it can land first.

**Implemented so far:** `Pin`, `Polyline`, `Polygon`, `Circle` under `handlers/Overlays` (declarative `StyleView` children; `Circle` uses the ported `GeographyUtils`/`Distance`). `SymbolLayerProperties` fully implemented (layout + paint properties, `ToDictionary()`/`FromJson()`). `LayoutPropertyNames` in both WPF renderers expanded to match the Windows/Android controller's comprehensive set.

### Follow-up: proper marker via SymbolLayer + sprite (not the legacy annotation API)

`Pin` currently renders as a **circle marker**. MapLibre's legacy `mbgl` annotation API (`SymbolAnnotation` etc.) is *not* exposed through `mln-cabi` and is deprecated upstream anyway — so it is not the path. The correct native marker is a **SymbolLayer with a registered sprite image**, and the cabi already exposes the key primitive: [`mbgl_style_add_image`](https://github.com/TechIdiots-LLC/MaplibreNativeMAUI/blob/main/native/include/mln_cabi.h) → `MbglStyle.AddImage`. Upgrading `Pin` to a real icon + text-label marker requires:

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
- MAUI overlay elements: [`Pin`](https://github.com/dotnet/maui/blob/main/src/Controls/Maps/src/Pin.cs), [`Polyline`](https://github.com/dotnet/maui/blob/main/src/Controls/Maps/src/Polyline.cs), [`Polygon`](https://github.com/dotnet/maui/blob/main/src/Controls/Maps/src/Polygon.cs), [`Circle`](https://github.com/dotnet/maui/blob/main/src/Controls/Maps/src/Circle.cs)
