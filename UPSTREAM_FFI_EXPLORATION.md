# Upstream FFI Exploration (`ffi-upstream` branch)

## Goal

Evaluate replacing the hand-written `mln-cabi` native layer with the official
[maplibre-native-ffi](https://github.com/maplibre/maplibre-native-ffi) C API as
the foundation for maplibre-maui-ac.

---

## Repository Layout (reference)

```
c:\Users\Andrew\Documents\GitHub\
  maplibre-maui-ac/          ← this repo  (branch: ffi-upstream)
  maplibre-native-ffi/       ← official upstream C API project (sibling)
```

`maplibre-native-ffi` lives as a sibling repo. If we adopt it, the most natural
integration is either:
1. **Git submodule** under `dependencies/maplibre-native-ffi/`
2. **Pre-built native package** consumed as a NuGet native asset

---

## API Comparison

### Handle / object model

| Concept | mln-cabi (current) | maplibre-native-ffi (upstream) |
|---|---|---|
| Event loop / context | `mbgl_runloop_t*` | `mln_runtime*` |
| Render target | `mbgl_frontend_t*` (OpenGL) | `mln_render_session*` (Metal/Vulkan) |
| Map | `mbgl_map_t*` | `mln_map*` |
| Style | `mbgl_style_t*` | (no separate handle — style ops on `mln_map*`) |
| Status enum | `mbgl_status_t` | `mln_status` |
| Prefix | `mbgl_` | `mln_` |

### Creation flow

**Current (`mln-cabi`)**
```c
mbgl_runloop_t* rl  = mbgl_runloop_create();
mbgl_frontend_t* fe = mbgl_frontend_create_gl(surface, ctx, w, h, dpr, cb, ud);
mbgl_map_t* map     = mbgl_map_create(fe, rl, cache, asset, dpr, observer, ud);
// fe ownership transfers into map
```

**Upstream (`mln`) — entry points updated in 2025 revision:**
```c
mln_runtime_options opts = mln_runtime_options_default();
mln_runtime* rt = NULL;
mln_runtime_create(&opts, &rt);

mln_map_options mopts = mln_map_options_default();
// mopts now takes initial width/height/scale_factor, not a fields mask
mopts.width = w; mopts.height = h; mopts.scale_factor = dpr;
mln_map* map = NULL;
mln_map_create(rt, &mopts, &map);

// Attach render target — WGL example (Windows OpenGL):
mln_wgl_surface_descriptor desc = mln_wgl_surface_descriptor_default();
desc.hdc = hdc; desc.hglrc = hglrc;
desc.width = w; desc.height = h; desc.scale_factor = dpr;
mln_render_session* session = NULL;
mln_wgl_surface_attach(map, &desc, &session);  // was mln_map_attach_metal_surface_session
```

### Event / callback model

| | mln-cabi | mln |
|---|---|---|
| Map events | Push callback (`mbgl_map_observer_fn`) | Poll: `mln_runtime_poll_event(&event)` |
| Render ready | Push callback (`mbgl_render_fn`) | `MLN_RUNTIME_EVENT_MAP_RENDER_UPDATE_AVAILABLE` |
| Log | Push callback (`mbgl_log_fn`) | Push callback (unchanged concept) |

The polling model requires a driver loop on the owner thread:
```csharp
while (mln_runtime_poll_event(rt, out var ev) == MlnStatus.Ok)
    HandleEvent(ev);
```

### Render backends

| Platform | mln-cabi (current) | mln (upstream) |
|---|---|---|
| iOS / macCatalyst | Metal (MTKView) | Metal ✓ |
| Android | OpenGL ES / EGL | EGL ✓ (`mln_egl_surface_attach` — added) |
| Windows | OpenGL / WGL | WGL ✓ (`mln_wgl_surface_attach` — added) |

> **Update (2025)**: The critical gap noted in the original exploration — that
> `maplibre-native-ffi` had no OpenGL backend — is **now resolved**.
> `surface.h` now declares `mln_egl_surface_attach` (Android/Linux) and
> `mln_wgl_surface_attach` (Windows), both guarded by
> `#ifdef MLN_RENDER_BACKEND_OPENGL`. A binary built with
> `MLN_FFI_RENDER_BACKEND=opengl` supports all four platforms. The primary
> remaining difference from `mln-cabi` is the session ownership model
> (see below) and the polled vs. push-callback event model.

---

## What Stays the Same

- The MAUI handler layer (`Maui.Maplibre.Handlers`) does not change at all.
  It talks to the `Maui.MapLibre.Native` C# wrappers, not directly to C.
- The `Maui.MapLibre.Native` project is the only thing that needs to change:
  `NativeMethods.cs`, `MbglMap.cs`, `MbglFrontend.cs`, `MbglRunLoop.cs`.
- Style operations (`SetStyleUrl`, `SetStyleJson`, source/layer manipulation)
  map 1:1 to `mln_map_set_style_*` equivalents.
- Camera operations (`JumpTo`, `EaseTo`, `FlyTo`, `SetBounds`) all exist on
  `mln_map` with richer field-mask options structs.

---

## What Needs to Change

### `bindings/` project

| File | Change |
|---|---|
| `NativeMethods.cs` | Replace `mbgl_*` P/Invokes with `mln_*` P/Invokes from `maplibre_native_c.h`. Use source-generated P/Invokes (`LibraryImport`). |
| `MbglRunLoop.cs` → `MlnRuntime.cs` | Wraps `mln_runtime*`. Add `PollEvents()` loop. |
| `MbglFrontend.cs` → removed | Frontend is now `mln_render_session*`, created via `mln_map_attach_*_session()`. |
| `MbglMap.cs` → `MlnMap.cs` | Use `mln_map_create(runtime, options, &map)`. Attach session separately. |
| `MbglStyle.cs` | Merge style methods into `MlnMap` — no separate style handle in upstream. |

### `native/` project (C++ layer)

The entire `mln-cabi` native C++ project would be **replaced** by
`maplibre-native-ffi` as a dependency. We would no longer maintain our own C
wrapper; instead we build/consume the upstream library.

### Native library name

The P/Invoke `DllImport` name changes from `"mln-cabi"` to `"maplibre_native_c"`
(or whatever the upstream publishes as the shared library name).

---

## Upstream API Changes (2025 Review)

This section documents breaking changes and new capabilities found during a
second review of the upstream headers.

### Breaking struct changes

| Struct | Before | After |
|---|---|---|
| `mln_map_options` | `{size, fields, mode, north_orientation, constrain_mode, viewport_mode}` | `{size, width, height, scale_factor, map_mode}` — viewport options moved to `mln_map_set_viewport_options()` |
| `mln_metal_surface_descriptor` | flat `{size, width, height, scale_factor, layer, device}` | nested `{size, extent{size,w,h,dpr}, context{size,device}, layer}` |
| `mln_vulkan_surface_descriptor` | flat with top-level `width/height/scale_factor` | nested `{size, extent{...}, context{size,instance,phys,dev,queue,qfi}, surface}` |
| `mln_runtime_event` | `{size, source_type, event_type, payload_type, map_handle}` | `{size, type, source_type, source(void*), code(int32_t), payload_type, payload(const void*), payload_size, message(const char*), message_size}` |

### Surface attach entry point renames

| Old entry point | New entry point |
|---|---|
| `mln_map_attach_metal_surface_session` | `mln_metal_surface_attach` |
| `mln_map_attach_vulkan_surface_session` | `mln_vulkan_surface_attach` |
| _(did not exist)_ | `mln_egl_surface_attach` |
| _(did not exist)_ | `mln_wgl_surface_attach` |

### New capabilities

- **`mln_render_backend_flag` / `mln_supported_render_backend_mask()`** — query
  which backends (`Metal | Vulkan | OpenGL`) the current binary supports.
- **`mln_c_version()`** — ABI contract version (use to assert compatibility).
- **`mln_map_get/set_viewport_options()`** — viewport options (north orientation,
  constrain mode, frustum offset) now live in a separate field-mask struct.
- **`mln_map_get/set_tile_options()`** — tile prefetch / LOD tuning now a
  separate field-mask struct.
- **`mln_map_is_fully_loaded()`, `mln_map_dump_debug_logs()`** — new utilities.
- **`mln_map_set_rendering_stats_view_enabled()`** — toggle stats overlay.
- **New event payloads**: `mln_runtime_event_render_frame` (includes rendering
  stats, needs_repaint, placement_changed), `mln_runtime_event_tile_action`
  (full tile lifecycle), `mln_runtime_event_style_image_missing`.
- **`mln_style_id_list`** — owned handle for listing source/layer IDs (cleaner
  than our current newline-delimited string approach).
- **Custom geometry source** (`mln_map_add_custom_geometry_source`) — C#-side
  callbacks provide tile data on demand.
- **Resource provider** (`mln_runtime_set_resource_provider`) — custom HTTP
  interceptor.
- **`mln_map_add_geojson_source_data`** now takes a typed `mln_geojson*` struct
  instead of a raw JSON string.

### Changes applied to `NativeMethods.Mln.cs` / `MlnMap.cs` / `MlnRuntime.cs`

All breaking struct changes listed above have been applied. The sketch files now
reflect the current upstream ABI. `GeoJSON` P/Invokes that passed a plain string
to `mln_map_add/set_geojson_source_data` remain as string-passing stubs since
`mln_geojson` is a complex typed union — they will need proper marshalling when
that path is exercised.

---

## Suggested Integration Strategy

### Phase 1 — iOS/macCatalyst only (Metal, fully supported upstream)

1. Add `maplibre-native-ffi` as a git submodule at
   `dependencies/maplibre-native-ffi/`.
2. Build it for iOS/macCatalyst in the Apple CI job (it already builds
   MapLibre Native as its own submodule — reconcile with our existing
   `dependencies/maplibre-native`).
3. Replace `NativeMethods.cs` with the `mln_*` sketch (see
   `bindings/NativeMethods.Mln.cs`).
4. Replace `MbglMap.cs` / `MbglRunLoop.cs` with `MlnMap.cs` / `MlnRuntime.cs`.
5. Keep the old `mln-cabi` code paths behind `#if !USE_MLN_FFI` until Android
   and Windows are ready.

### Phase 2 — Android (EGL) and Windows (WGL)

Both EGL and WGL surface attach functions now exist in upstream. Enabling them
requires building `maplibre-native-ffi` with `MLN_FFI_RENDER_BACKEND=opengl`.

- **Android**: Call `mln_egl_surface_attach` with the `EGLDisplay`, `EGLContext`,
  and `EGLSurface` obtained from the Android `GLSurfaceView` / `SurfaceTexture`.
- **Windows**: Call `mln_wgl_surface_attach` with the `HDC` and `HGLRC` that
  our `MbglMapHost` (WPF) or the MAUI Windows controller already sets up. The
  WGL context creation code in `MbglFrontend.cs` / `MbglMapHost.cs` can be reused
  as-is — only the `mln_wgl_surface_attach` call replaces the old `mbgl_frontend`.

Note: both use `MLN_STATUS_UNSUPPORTED` when the binary is not an OpenGL build.

### Phase 3 — Remove mln-cabi

Once all three platforms are on `mln_*`, delete the `native/` C++ project and
pull `maplibre-native-ffi` as the sole native dependency.

---

## Open Questions

1. **ABI stability**: `maplibre-native-ffi` is pre-1.0 (`mln_c_version()` returns 0).
   The ABI can change between releases. We need to pin to a commit/tag.
2. **Duplicate MapLibre Native submodule**: both repos bring in
   `maplibre-native` as a submodule. We need to reconcile which version is used.
3. **ClangSharp generation**: the C# binding convention document recommends
   generating the internal P/Invoke layer with ClangSharp. We could integrate
   that into the CI pipeline.
4. **net10.0 target**: the upstream C# convention targets `net10.0`, but our
   MAUI project targets `net9.0-android` etc. We should stay on `net9.0` for
   now and revisit when MAUI ships on .NET 10.
5. **Thread model**: `mln_runtime` is 1:1 with its owner thread (same as our
   RunLoop). The polling event loop fits naturally into a dedicated render
   thread.

---

## Files Added in This Branch

- `UPSTREAM_FFI_EXPLORATION.md` — this document
- `bindings/NativeMethods.Mln.cs` — sketch of P/Invoke layer for `mln_*` API
- `bindings/MlnRuntime.cs` — sketch of `RuntimeHandle` wrapper
- `bindings/MlnMap.cs` — sketch of `MapHandle` wrapper
