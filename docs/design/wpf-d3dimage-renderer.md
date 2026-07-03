# Design: airspace-free WPF map via `D3DImage`

**Status:** proposal / not yet implemented
**Scope:** the WPF host only (`wpf/MlnMapHost.cs`). No change to the MAUI handlers or the `mln-cabi` C ABI.

## Problem

`MlnMapHost` is a [`HwndHost`](../../wpf/MlnMapHost.cs): it creates a Win32 child window and renders MapLibre into it with OpenGL (WGL). A hosted HWND is subject to the WPF **airspace** rule — the child window always composites *on top of* all WPF content in the same area. Consequences we currently live with:

- The map's own controls (navigation d-pad, GPS panel, attribution) cannot be normal WPF children drawn over the map. They are implemented as WPF `Popup` objects, which are separate top-level windows and therefore the only thing that can appear above the airspace.
- Because they are separate windows, popups do not clip to the map bounds, can float over other applications, and need continuous babysitting: hide on deactivate, reposition/reopen on window move, resize, and DPI change. This is the bulk of `MlnMapHost` (`InitNavPopup`, `PositionAttributionPopup`, the `Deactivated`/`Activated`/`StateChanged`/`LocationChanged` handlers, etc.).
- WPF effects (opacity, transforms, `Clip`, drop shadows) do not apply to the hosted surface.

This is exactly the "controls hover like a popup instead of being part of the map" complaint. Note the official .NET MAUI Windows map handler never hits this because it hosts a WinUI 3 `MapControl` (a real XAML element) and does no native rendering itself — so it is not a reference for solving *this* problem.

## Goal

Make the map surface a **real WPF visual** so that:

1. The nav / GPS / attribution controls become ordinary WPF elements in the same visual tree (correct z-order, clipping, hit-testing, DPI, and transforms for free), and all the popup-positioning machinery is deleted.
2. Public API of `MlnMapHost` (dependency properties, camera/source/layer methods, events) is preserved so `sample/WpfExample` and VistumblerCS need no changes.

## Approach: render to an offscreen FBO and present via `D3DImage`

WPF's `D3DImage` is an `ImageSource` backed by a Direct3D9 surface that the WPF compositor (also D3D-based) can sample. The trick is getting MapLibre's **OpenGL** output into a **D3D9** surface without a CPU round-trip. The standard, GPU-only path is the `WGL_NV_DX_interop2` extension (supported on all NVIDIA/AMD/Intel desktop drivers for the last decade):

```
MapLibre (mln-cabi)  ->  GL FBO (texture/renderbuffer)
        │  wglDXRegisterObjectNV + wglDXLockObjectsNV
        ▼
   shared D3D surface  ->  D3DImage.SetBackBuffer  ->  WPF visual tree
```

### Rendering pipeline per frame

1. Create a **D3D9Ex** device (`Direct3DCreate9Ex`, `IDirect3DDevice9Ex`) — required for `D3DImage` shared surfaces. Create a render-target texture with `D3DUSAGE_RENDERTARGET` in a shareable format (`D3DFMT_A8R8G8B8`) and grab its `IDirect3DSurface9`.
2. Open a WGL↔DX interop handle for the current WGL context: `wglDXOpenDeviceNV(d3dDevice)`. Register the D3D surface as a GL renderbuffer/texture: `wglDXRegisterObjectNV(...)` → a GL name.
3. Each render tick (replaces the `SwapBuffers` path in `OnRenderTick`):
   - `wglDXLockObjectsNV` the shared object.
   - Bind the FBO whose color attachment is the shared GL object; `mln_frontend_render` into it (MapLibre already renders to whatever framebuffer is bound — see the existing `_glBindFramebuffer` usage).
   - `wglDXUnlockObjectsNV`.
   - On the WPF/dispatcher thread: `D3DImage.Lock()` → `SetBackBuffer(D3DResourceType.IDirect3DSurface9, surfacePtr)` (only needs re-setting when the surface is recreated) → `AddDirtyRect(fullRect)` → `Unlock()`.
4. Host the `D3DImage` in an `Image` element (or paint it as the `Background`/a child of a `Border`) that **is** `MlnMapHost` — change the base class from `HwndHost` to a `FrameworkElement`/`Control` (likely a `Border`/`Grid`-derived control, or a `ContentControl` template) whose content is the `Image` plus the control overlays as sibling children.

### Input

With no child HWND, mouse/keyboard come from WPF routed events on the element (`OnMouseDown/Move/Up/Wheel`, `CaptureMouse`) instead of the current `WndProc` switch. Coordinates are WPF logical pixels → multiply by the DPI scale to get the physical pixels MapLibre expects (the existing `_dpi` handling carries over). Cursor changes use `this.Cursor` instead of `SetCursor`.

### Threading

The current design pumps `MbglRunLoop.RunOnce()` and renders on a `DispatcherTimer` on the UI thread; that can stay. The only new cross-thread concern is that `D3DImage` mutations (`Lock/SetBackBuffer/AddDirtyRect/Unlock`) must happen on the thread that owns the `D3DImage` (the UI thread) — which is where the render timer already runs, so no extra marshalling is needed for the single-threaded-render case. If we later move MapLibre rendering to a dedicated thread, the D3D surface must be produced on a device shared with the UI-thread `D3DImage` (D3D9Ex handles this).

## Migration strategy

Keep both renderers behind the existing public surface so we can ship incrementally and A/B them:

1. Extract the public API (DPs, camera/source/layer methods, events) into a small `IMlnMapView` interface or an abstract base, implemented by:
   - `MlnMapHostHwnd` — today's `HwndHost` implementation (rename of the current class), and
   - `MlnMapHostD3D` — the new `D3DImage` implementation.
2. Add a feature flag / factory (`MlnMapHost.RendererMode`) defaulting to the current HwndHost until the D3D path is proven, so `WpfExample` runs unchanged.
3. Port the controls from `Popup` to plain WPF children **only** in the D3D implementation. The HwndHost path keeps its popups.
4. Once the D3D path reaches parity (input, DPI, multi-monitor move, resize, style reload, GPS/attribution behavior), flip the default and deprecate the HwndHost path.

## Risks & open questions

- **Driver/interop availability.** `WGL_NV_DX_interop2` is broadly supported but not universal (unusual on pure software/RDP GL). Need a fallback: either keep the HwndHost path as the fallback renderer, or a slow `glReadPixels` → `WriteableBitmap` path for compatibility.
- **`D3DImage` + resize/DPI.** Surface must be recreated on size/DPI change; `SetBackBuffer` re-called. Front-load testing on the multi-monitor / mixed-DPI cases the current code handles explicitly.
- **Software fallback / `D3DImage` front-buffer.** On some remote/virtualized setups `D3DImage` falls back to software and can be slow; measure before committing.
- **vsync / tearing / frame pacing** differs from `SwapBuffers`; the `DispatcherTimer(Render, 16ms)` cadence needs re-tuning against WPF's own compositor.
- **Alternative not chosen:** a `SwapChainPanel`-style path is a WinUI/UWP concept, not available to classic WPF, so `D3DImage` is the correct WPF mechanism. On the MAUI/WinUI side the equivalent future work would be `SwapChainPanel`, sharing the same offscreen-render idea.

## Effort estimate

Prototype (single renderer, happy-path interop + input, no fallback): ~1–2 focused days. Production parity (fallback path, DPI/multi-monitor, controls ported off popups, A/B flag): meaningfully more. Recommend building the prototype on a separate branch behind the factory flag before committing to the migration.

## References

- WPF `D3DImage`: <https://learn.microsoft.com/dotnet/api/system.windows.interop.d3dimage>
- `WGL_NV_DX_interop2`: <https://registry.khronos.org/OpenGL/extensions/NV/NV_DX_interop2.txt>
- Airspace overview: <https://learn.microsoft.com/dotnet/desktop/wpf/advanced/technology-regions-overview>
- Current implementation: [`wpf/MlnMapHost.cs`](../../wpf/MlnMapHost.cs)
