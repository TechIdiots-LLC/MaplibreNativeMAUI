/**
 * mln_cabi.h — Flat C ABI for MapLibre Native.
 *
 * Design principles (informed by maplibre-native-ffi):
 *  - All handles are typed opaque struct pointers — the C compiler rejects
 *    passing the wrong handle type.
 *  - Every mutating function returns mln_status_t so callers can detect
 *    failures without a separate out-parameter.
 *  - mln_get_last_error() returns a thread-local diagnostic string for the
 *    most recent non-OK return.
 *  - All exported functions are marked MLN_CABI_NOEXCEPT to prevent
 *    exceptions crossing the ABI boundary.
 *
 * Thread-safety: Map must be used on the same thread as its RunLoop.
 */
#pragma once

#include <stdint.h>
#include <stddef.h>  /* size_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ── Export macro ──────────────────────────────────────────────────────────── */
#if defined(_WIN32)
#  ifdef MLN_CABI_EXPORT
#    define MLN_CABI_API __declspec(dllexport)
#  else
#    define MLN_CABI_API __declspec(dllimport)
#  endif
#else
#  define MLN_CABI_API __attribute__((visibility("default")))
#endif

/* Marks every exported function noexcept in C++ to prevent exceptions
 * crossing the ABI boundary and to let the compiler generate better code. */
#ifdef __cplusplus
#  define MLN_CABI_NOEXCEPT noexcept
#else
#  define MLN_CABI_NOEXCEPT
#endif

/* ── Status codes ──────────────────────────────────────────────────────────── */
/** Return code from every mutating / factory function.
 *  On any non-OK return, call mln_get_last_error() for a diagnostic string. */
typedef enum mln_status_t {
    MLN_OK              =  0,  /**< Success. */
    MLN_INVALID_ARG     = -1,  /**< A required argument was NULL or out of range. */
    MLN_INVALID_STATE   = -2,  /**< Call is not valid in the current state. */
    MLN_WRONG_THREAD    = -3,  /**< Called from the wrong thread. */
    MLN_UNSUPPORTED     = -4,  /**< Operation not supported on this platform. */
    MLN_NATIVE_ERROR    = -5   /**< An internal C++ exception was caught; see mln_get_last_error(). */
} mln_status_t;

/** Returns a thread-local string describing the most recent non-OK status.
 *  Valid until the next MBGL call on this thread. Never NULL. */
MLN_CABI_API const char* mln_get_last_error(void) MLN_CABI_NOEXCEPT;

/* ── Opaque handle types ───────────────────────────────────────────────────── */
/* Forward-declared typed structs: passing the wrong handle type is a
 * compile error rather than a silent runtime bug. */
typedef struct mln_runloop_s  mln_runloop_t;
typedef struct mln_frontend_s mln_frontend_t;
typedef struct mln_map_s      mln_map_t;
typedef struct mln_style_s    mln_style_t;
typedef struct mln_source_s   mln_source_t;
typedef struct mln_layer_s    mln_layer_t;

/* ── Callbacks ─────────────────────────────────────────────────────────────── */
typedef void (*mln_render_fn)(void* userdata);
/** Observer callback fired for named map lifecycle events.
 *  @param event_name  Camel-case event name matching the MapObserver virtual method
 *                     (e.g. "onDidFinishLoadingStyle", "onDidBecomeIdle").
 *  @param detail      Optional extra detail: error message for onDidFailLoadingMap
 *                     and onRenderError (GPU allocation / render failure),
 *                     image ID for onStyleImageMissing, source ID for onSourceChanged,
 *                     "animated" or "immediate" for camera change events, else NULL.
 *                     Frame events: "onDidFinishRenderingFrameNeedsRepaint",
 *                     "onDidFinishRenderingFramePlacementChanged",
 *                     "onDidFinishRenderingFrameNeedsRepaintPlacementChanged",
 *                     or plain "onDidFinishRenderingFrame".
 *  @param userdata    Opaque pointer passed to mln_map_create. */
typedef void (*mln_map_observer_fn)(const char* event_name, const char* detail, void* userdata);

/* ── Debug options ─────────────────────────────────────────────────────────── */
/** Bitmask of debug visualisation overlays.  OR together the flags you want. */
typedef enum mln_debug_options_t {
    MLN_DEBUG_NONE        = 0,
    MLN_DEBUG_TILE_BORDERS = 1 << 1,  /**< Draw tile boundary outlines. */
    MLN_DEBUG_PARSE_STATUS = 1 << 2,  /**< Show tile parse/loading state. */
    MLN_DEBUG_TIMESTAMPS   = 1 << 3,  /**< Print frame timestamps on tiles. */
    MLN_DEBUG_COLLISION    = 1 << 4,  /**< Highlight symbol collision boxes. */
    MLN_DEBUG_OVERDRAW     = 1 << 5,  /**< Heat-map style overdraw visualisation. */
    MLN_DEBUG_STENCIL_CLIP = 1 << 6,  /**< Show stencil buffer clipping regions. */
    MLN_DEBUG_DEPTH_BUFFER = 1 << 7   /**< Show depth buffer contents. */
} mln_debug_options_t;

/* ── Log callback ──────────────────────────────────────────────────────────── */
typedef enum mln_log_level_t {
    MLN_LOG_DEBUG   = 0,
    MLN_LOG_INFO    = 1,
    MLN_LOG_WARNING = 2,
    MLN_LOG_ERROR   = 3
} mln_log_level_t;

/**
 * Log intercept callback.
 * @param level     Severity of the log record.
 * @param category  Short category string (e.g. "Parse", "Render", "Network").
 * @param message   The log message.
 * @param userdata  Opaque pointer passed to mln_install_log_callback.
 * @return          Non-zero to consume the record (suppress default output),
 *                  0 to let MapLibre also emit it to the default sink.
 */
typedef int (*mln_log_fn)(mln_log_level_t level,
                            const char*      category,
                            const char*      message,
                            void*            userdata);

/**
 * Install a global log intercept callback.
 * Pass NULL to remove any previously installed callback and restore default
 * logging behaviour.  The callback is invoked on whatever thread MapLibre
 * emits the log record — synchronise as needed.
 */
MLN_CABI_API mln_status_t mln_install_log_callback(mln_log_fn fn,
                                                        void*        userdata) MLN_CABI_NOEXCEPT;

/* ── Network status ────────────────────────────────────────────────────────── */
/** Toggle the process-global network state.  Pass 0 to force offline mode:
 *  all network requests are suspended and only cached / offline resources are
 *  served.  Pass 1 to restore online mode (queued requests resume). */
MLN_CABI_API mln_status_t mln_network_status_set(int online) MLN_CABI_NOEXCEPT;
/** Returns 1 if the network is in online mode, 0 if offline. */
MLN_CABI_API int           mln_network_status_get(void) MLN_CABI_NOEXCEPT;

/* ── RunLoop ───────────────────────────────────────────────────────────────── */
MLN_CABI_API mln_runloop_t* mln_runloop_create(void) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_runloop_destroy(mln_runloop_t* rl) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_runloop_run_once(mln_runloop_t* rl) MLN_CABI_NOEXCEPT;

/* ── Render backend ────────────────────────────────────────────────────────── */
/** Returns the renderer this build of mln-cabi was compiled against:
 *  "opengl", "vulkan", or "metal". Never NULL. Lets the (shared) managed layer
 *  pick the correct surface handshake at runtime — the GL and Vulkan packages
 *  ship the same C# but different native libraries under the same name. */
MLN_CABI_API const char* mln_get_render_backend(void) MLN_CABI_NOEXCEPT;

/* ── Frontend ──────────────────────────────────────────────────────────────── */
/** Backend-agnostic frontend factory. The meaning of surface_handle depends on
 *  the compiled backend and platform:
 *    OpenGL  (Windows): HDC              + gl_context = HGLRC
 *    Vulkan  (Windows): ignored (offscreen render + read-back via mln_frontend_read_pixels)
 *    Vulkan/GL (Android): ANativeWindow* + gl_context = NULL
 *    Metal/Vulkan (Apple): NULL          + gl_context = NULL (view is created internally;
 *                                          retrieve it via mln_frontend_get_native_view) */
MLN_CABI_API mln_frontend_t* mln_frontend_create(
    void*          surface_handle,
    void*          gl_context,
    int            width_px,
    int            height_px,
    float          pixel_ratio,
    mln_render_fn render_callback,
    void*          render_userdata) MLN_CABI_NOEXCEPT;
/** Deprecated alias for mln_frontend_create, kept for ABI/source compatibility. */
MLN_CABI_API mln_frontend_t* mln_frontend_create_gl(
    void*          surface_handle,
    void*          gl_context,
    int            width_px,
    int            height_px,
    float          pixel_ratio,
    mln_render_fn render_callback,
    void*          render_userdata) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t    mln_frontend_destroy(mln_frontend_t* fe) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t    mln_frontend_render(mln_frontend_t* fe) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t    mln_frontend_set_size(mln_frontend_t* fe, int width_px, int height_px) MLN_CABI_NOEXCEPT;
MLN_CABI_API void*            mln_frontend_get_native_view(mln_frontend_t* fe) MLN_CABI_NOEXCEPT;
/** Copies the most recently rendered frame as tightly-packed premultiplied RGBA
 *  (width*height*4 bytes, top-down) into out_buf. Used by the offscreen (Vulkan
 *  Windows) path to blit into the in-tree bitmap surface. Returns MLN_UNSUPPORTED
 *  for frontends that present directly (GL Windows read back GL-side; Android/Apple
 *  present to their own surface/view). buf_len must be >= width*height*4. */
MLN_CABI_API mln_status_t    mln_frontend_read_pixels(mln_frontend_t* fe,
                                                        uint8_t* out_buf,
                                                        size_t   buf_len) MLN_CABI_NOEXCEPT;

/* ── Map ───────────────────────────────────────────────────────────────────── */
MLN_CABI_API mln_map_t*     mln_map_create(
    mln_frontend_t*      fe,
    mln_runloop_t*       rl,
    const char*           cache_path,
    const char*           asset_path,
    float                 pixel_ratio,
    mln_map_observer_fn  observer,
    void*                 observer_userdata) MLN_CABI_NOEXCEPT;
/** Extended map factory.  Identical to mln_map_create plus resource options:
 *  @param api_key              API key appended to tile-server requests, or NULL.
 *  @param max_cache_size_bytes Maximum size of the on-disk cache database in
 *                              bytes, or 0 for the MapLibre default (~50 MB). */
MLN_CABI_API mln_map_t*     mln_map_create2(
    mln_frontend_t*      fe,
    mln_runloop_t*       rl,
    const char*           cache_path,
    const char*           asset_path,
    const char*           api_key,
    uint64_t              max_cache_size_bytes,
    float                 pixel_ratio,
    mln_map_observer_fn  observer,
    void*                 observer_userdata) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_destroy(mln_map_t* map) MLN_CABI_NOEXCEPT;

MLN_CABI_API mln_status_t   mln_map_set_style_url(mln_map_t* map, const char* url) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_set_style_json(mln_map_t* map, const char* json) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_set_size(mln_map_t* map, int width_px, int height_px) MLN_CABI_NOEXCEPT;

MLN_CABI_API mln_status_t   mln_map_jump_to(mln_map_t* map, double lat, double lon,
                                                double zoom, double bearing, double pitch) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_ease_to(mln_map_t* map, double lat, double lon,
                                                double zoom, double bearing, double pitch,
                                                int64_t duration_ms) MLN_CABI_NOEXCEPT;

MLN_CABI_API double          mln_map_get_zoom(mln_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API double          mln_map_get_bearing(mln_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API double          mln_map_get_pitch(mln_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API void            mln_map_get_center(mln_map_t* map, double* out_lat, double* out_lon) MLN_CABI_NOEXCEPT;

MLN_CABI_API mln_status_t   mln_map_set_min_zoom(mln_map_t* map, double zoom) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_set_max_zoom(mln_map_t* map, double zoom) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_trigger_repaint(mln_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_cancel_transitions(mln_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API int             mln_map_is_fully_loaded(mln_map_t* map) MLN_CABI_NOEXCEPT;

/* ── Map – debug overlays ───────────────────────────────────────────────────── */
/** Read the current debug overlay bitmask (OR of mln_debug_options_t flags). */
MLN_CABI_API int             mln_map_get_debug_options(mln_map_t* map) MLN_CABI_NOEXCEPT;
/** Set the debug overlay bitmask.  Pass MLN_DEBUG_NONE to disable all. */
MLN_CABI_API mln_status_t   mln_map_set_debug_options(mln_map_t* map, int options) MLN_CABI_NOEXCEPT;

/* ── Map – gesture / interactive movement ──────────────────────────────────── */
/** Inform the map that a user gesture is in progress (suppresses animated
 *  camera snap-back during panning).  Call with 1 on touch-down, 0 on touch-up. */
MLN_CABI_API mln_status_t   mln_map_set_gesture_in_progress(mln_map_t* map, int in_progress) MLN_CABI_NOEXCEPT;
/** Returns 1 while a gesture is flagged in progress via mln_map_set_gesture_in_progress. */
MLN_CABI_API int             mln_map_is_gesture_in_progress(mln_map_t* map) MLN_CABI_NOEXCEPT;
/** Returns 1 while a rotate transition/animation is running. */
MLN_CABI_API int             mln_map_is_rotating(mln_map_t* map) MLN_CABI_NOEXCEPT;
/** Returns 1 while a zoom/scale transition/animation is running. */
MLN_CABI_API int             mln_map_is_scaling(mln_map_t* map) MLN_CABI_NOEXCEPT;
/** Returns 1 while a pan transition/animation is running. */
MLN_CABI_API int             mln_map_is_panning(mln_map_t* map) MLN_CABI_NOEXCEPT;
/** Translate the viewport by (dx, dy) screen pixels, optionally animated. */
MLN_CABI_API mln_status_t   mln_map_move_by(mln_map_t* map, double dx, double dy,
                                                int64_t duration_ms) MLN_CABI_NOEXCEPT;
/** Rotate the map by dragging first→second screen coordinate. */
MLN_CABI_API mln_status_t   mln_map_rotate_by(mln_map_t* map,
                                                   double x0, double y0,
                                                   double x1, double y1) MLN_CABI_NOEXCEPT;
/** Pitch the map by delta degrees, optionally animated. */
MLN_CABI_API mln_status_t   mln_map_pitch_by(mln_map_t* map, double delta_degrees,
                                                 int64_t duration_ms) MLN_CABI_NOEXCEPT;

/* ── Map – map options (post-create) ────────────────────────────────────────── */
/** 0=None 1=NorthUp 2=Compass 3=Manual */
MLN_CABI_API mln_status_t   mln_map_set_north_orientation(mln_map_t* map, int orientation) MLN_CABI_NOEXCEPT;
/** 0=None 1=HeightOnly 2=WidthAndHeight */
MLN_CABI_API mln_status_t   mln_map_set_constrain_mode(mln_map_t* map, int mode) MLN_CABI_NOEXCEPT;
/** 0=Default 1=FlippedY */
MLN_CABI_API mln_status_t   mln_map_set_viewport_mode(mln_map_t* map, int mode) MLN_CABI_NOEXCEPT;

/* ── Map – camera constraints read-back ────────────────────────────────────── */
/** Read current BoundOptions.  Pass NULL for fields you don't need. */
MLN_CABI_API void            mln_map_get_bounds(mln_map_t* map,
                                                   double* out_lat_sw, double* out_lon_sw,
                                                   double* out_lat_ne, double* out_lon_ne,
                                                   double* out_min_zoom, double* out_max_zoom,
                                                   double* out_min_pitch, double* out_max_pitch) MLN_CABI_NOEXCEPT;

/* ── Map – tile LOD controls (Tier 2) ──────────────────────────────────────── */
MLN_CABI_API mln_status_t   mln_map_set_prefetch_zoom_delta(mln_map_t* map, int delta) MLN_CABI_NOEXCEPT;
MLN_CABI_API int             mln_map_get_prefetch_zoom_delta(mln_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_set_tile_lod_min_radius(mln_map_t* map, double radius) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_set_tile_lod_scale(mln_map_t* map, double scale) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_set_tile_lod_pitch_threshold(mln_map_t* map, double threshold_rad) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_set_tile_lod_zoom_shift(mln_map_t* map, double shift) MLN_CABI_NOEXCEPT;
/** mode: 0=default, 1=distance */
MLN_CABI_API mln_status_t   mln_map_set_tile_lod_mode(mln_map_t* map, int mode) MLN_CABI_NOEXCEPT;

/* ── Map – camera for points / geometry (Tier 2) ───────────────────────────── */
/** Compute camera to fit an arbitrary list of lat/lon pairs with padding.
 *  @param latlngs  Flat array of alternating lat, lon values (length = count * 2). */
MLN_CABI_API mln_status_t   mln_map_camera_for_latlngs(mln_map_t* map,
                                                            const double* latlngs, int count,
                                                            double pad_top, double pad_left,
                                                            double pad_bottom, double pad_right,
                                                            double* out_lat, double* out_lon,
                                                            double* out_zoom,
                                                            double* out_bearing,
                                                            double* out_pitch) MLN_CABI_NOEXCEPT;
/** Batch-project N lat/lon pairs to screen pixels.
 *  @param latlngs  Flat array [lat0, lon0, lat1, lon1, ...] length = count * 2.
 *  @param out_xy   Caller-allocated output [x0, y0, x1, y1, ...] length = count * 2. */
MLN_CABI_API mln_status_t   mln_map_pixels_for_latlngs(mln_map_t* map,
                                                            const double* latlngs, int count,
                                                            double* out_xy) MLN_CABI_NOEXCEPT;
/** Batch un-project N screen pixel pairs to lat/lon.
 *  @param xy        Flat array [x0, y0, x1, y1, ...] length = count * 2.
 *  @param out_ll    Caller-allocated output [lat0, lon0, ...] length = count * 2. */
MLN_CABI_API mln_status_t   mln_map_latlngs_for_pixels(mln_map_t* map,
                                                            const double* xy, int count,
                                                            double* out_ll) MLN_CABI_NOEXCEPT;

/* ── Style – enumeration (Tier 1) ──────────────────────────────────────────── */
/** Get style metadata.  Returned strings must be freed with mln_free_string(). */
MLN_CABI_API char*           mln_style_get_url(mln_style_t* st) MLN_CABI_NOEXCEPT;
MLN_CABI_API char*           mln_style_get_name(mln_style_t* st) MLN_CABI_NOEXCEPT;
/** Returns a JSON array of source IDs (IDs may contain any character, so a
 *  delimiter-joined string would be ambiguous); caller frees with mln_free_string(). */
MLN_CABI_API char*           mln_style_get_source_ids(mln_style_t* st) MLN_CABI_NOEXCEPT;
/** Returns a JSON array of layer IDs in draw order; caller frees. */
MLN_CABI_API char*           mln_style_get_layer_ids(mln_style_t* st) MLN_CABI_NOEXCEPT;
/** Get a layer handle by ID (returns NULL if not found). */
MLN_CABI_API mln_layer_t*   mln_style_get_layer(mln_style_t* st, const char* layer_id) MLN_CABI_NOEXCEPT;
/** Get a source handle by ID (returns NULL if not found). */
MLN_CABI_API mln_source_t*  mln_style_get_source(mln_style_t* st, const char* source_id) MLN_CABI_NOEXCEPT;
/**
 * Returns the attribution text for the given source handle (may be NULL or empty).
 * The returned string must be freed with mln_free_string().
 * Suitable for building an OSM-compliant attribution overlay.
 */
MLN_CABI_API char*           mln_source_get_attribution(mln_source_t* src) MLN_CABI_NOEXCEPT;

/* ── Layer – read-back ──────────────────────────────────────────────────────── */
/** Returns a JSON-encoded value or NULL if not set; caller frees. */
MLN_CABI_API char*           mln_layer_get_paint_property(mln_layer_t* layer, const char* name) MLN_CABI_NOEXCEPT;
MLN_CABI_API char*           mln_layer_get_layout_property(mln_layer_t* layer, const char* name) MLN_CABI_NOEXCEPT;
/** Returns 1 if visible, 0 if none. */
MLN_CABI_API int             mln_layer_get_visibility(mln_layer_t* layer) MLN_CABI_NOEXCEPT;

MLN_CABI_API mln_status_t   mln_map_on_scroll(mln_map_t* map, double delta, double cx, double cy) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_on_double_tap(mln_map_t* map, double x, double y) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_on_pan_start(mln_map_t* map, double x, double y) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_on_pan_move(mln_map_t* map, double dx, double dy) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_on_pan_end(mln_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_on_pinch(mln_map_t* map, double scale_factor,
                                                 double cx, double cy) MLN_CABI_NOEXCEPT;

/* ── Map – additional camera / bounds / projection ─────────────────────────── */
MLN_CABI_API mln_status_t   mln_map_fly_to(mln_map_t* map, double lat, double lon,
                                               double zoom, double bearing, double pitch,
                                               int64_t duration_ms) MLN_CABI_NOEXCEPT;

/* ── Map – camera with edge padding ────────────────────────────────────────────
 * Padded variants of the camera movement functions.  Padding (in screen pixels,
 * order top/left/bottom/right) shifts the camera's effective centre so the
 * target appears centred in the *unobscured* part of the viewport — use when
 * panels or overlays cover part of the map.  Pass NaN for zoom, bearing, or
 * pitch to leave that field unchanged. */
MLN_CABI_API mln_status_t   mln_map_jump_to_padded(mln_map_t* map,
                                                       double lat, double lon,
                                                       double zoom, double bearing, double pitch,
                                                       double pad_top, double pad_left,
                                                       double pad_bottom, double pad_right) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_ease_to_padded(mln_map_t* map,
                                                       double lat, double lon,
                                                       double zoom, double bearing, double pitch,
                                                       double pad_top, double pad_left,
                                                       double pad_bottom, double pad_right,
                                                       int64_t duration_ms) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_map_fly_to_padded(mln_map_t* map,
                                                      double lat, double lon,
                                                      double zoom, double bearing, double pitch,
                                                      double pad_top, double pad_left,
                                                      double pad_bottom, double pad_right,
                                                      int64_t duration_ms) MLN_CABI_NOEXCEPT;
/** Read the full camera state in one call, optionally offset by edge padding.
 *  Pass 0 for all pads to read the raw camera.  Pass NULL for outputs you
 *  don't need. */
MLN_CABI_API mln_status_t   mln_map_get_camera(mln_map_t* map,
                                                   double pad_top, double pad_left,
                                                   double pad_bottom, double pad_right,
                                                   double* out_lat, double* out_lon,
                                                   double* out_zoom,
                                                   double* out_bearing,
                                                   double* out_pitch) MLN_CABI_NOEXCEPT;
/** Multiply the map scale by @p scale (2.0 = one zoom level in), optionally
 *  about a screen anchor point.  Pass NaN for anchor_x/anchor_y to scale about
 *  the viewport centre. */
MLN_CABI_API mln_status_t   mln_map_scale_by(mln_map_t* map, double scale,
                                                 double anchor_x, double anchor_y,
                                                 int64_t duration_ms) MLN_CABI_NOEXCEPT;

/** Set geographic camera bounds and optional zoom/pitch limits.
 *  Pass NaN for any field to leave it unset (e.g. no lat/lng constraint). */
MLN_CABI_API mln_status_t   mln_map_set_bounds(mln_map_t* map,
                                                   double lat_sw, double lon_sw,
                                                   double lat_ne, double lon_ne,
                                                   double min_zoom, double max_zoom,
                                                   double min_pitch, double max_pitch) MLN_CABI_NOEXCEPT;

/** Compute CameraOptions that fits the given LatLngBounds with optional padding.
 *  Padding order: top, left, bottom, right (matches mln::EdgeInsets field order). */
MLN_CABI_API mln_status_t   mln_map_camera_for_bounds(mln_map_t* map,
                                                          double lat_sw, double lon_sw,
                                                          double lat_ne, double lon_ne,
                                                          double pad_top, double pad_left,
                                                          double pad_bottom, double pad_right,
                                                          double* out_lat, double* out_lon,
                                                          double* out_zoom, double* out_bearing,
                                                          double* out_pitch) MLN_CABI_NOEXCEPT;

MLN_CABI_API void            mln_map_pixel_for_latlng(mln_map_t* map,
                                                         double lat, double lon,
                                                         double* out_x, double* out_y) MLN_CABI_NOEXCEPT;
MLN_CABI_API void            mln_map_latlng_for_pixel(mln_map_t* map,
                                                         double x, double y,
                                                         double* out_lat, double* out_lon) MLN_CABI_NOEXCEPT;

MLN_CABI_API mln_status_t   mln_map_set_projection_mode(mln_map_t* map,
                                                            int axonometric,
                                                            double x_skew, double y_skew) MLN_CABI_NOEXCEPT;

/* ── Style – images ─────────────────────────────────────────────────────────── */
/** Add a sprite image from premultiplied RGBA bytes (length = width * height * 4). */
MLN_CABI_API mln_status_t   mln_style_add_image(mln_style_t* st, const char* image_id,
                                                    int width, int height,
                                                    float pixel_ratio, int sdf,
                                                    const uint8_t* rgba_premultiplied) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_style_remove_image(mln_style_t* st, const char* image_id) MLN_CABI_NOEXCEPT;

/** Returns the currently loaded style as a JSON string; caller must free with mln_free_string(). */
MLN_CABI_API char*           mln_style_get_json(mln_style_t* st) MLN_CABI_NOEXCEPT;

/** Set the global style transition duration and delay (milliseconds). */
MLN_CABI_API mln_status_t   mln_style_set_transition(mln_style_t* st,
                                                          int64_t duration_ms,
                                                          int64_t delay_ms) MLN_CABI_NOEXCEPT;

/** Set a Light property by name using a JSON-encoded value.
 *  Valid names: "anchor" ("map"|"viewport"), "color" ("#rrggbb"),
 *               "intensity" (0-1 float), "position" ([radial, azimuthal, polar]). */
MLN_CABI_API mln_status_t   mln_style_set_light_property(mln_style_t* st,
                                                              const char* name,
                                                              const char* value_json) MLN_CABI_NOEXCEPT;

/* ── Style – additional layer types ─────────────────────────────────────────── */
MLN_CABI_API mln_layer_t*   mln_style_add_location_indicator_layer(mln_style_t* st,
                                                                        const char* id,
                                                                        const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_layer_t*   mln_style_add_color_relief_layer(mln_style_t* st,
                                                                  const char* id,
                                                                  const char* src,
                                                                  const char* before) MLN_CABI_NOEXCEPT;

/* ── Feature queries ────────────────────────────────────────────────────────── */
/** Query rendered features at a screen point.
 *  Returns a JSON FeatureCollection string; caller must free with mln_free_string().
 *  @param layer_ids  Comma-separated layer IDs to restrict the query, or NULL for all. */
MLN_CABI_API char*           mln_map_query_rendered_features_at_point(mln_map_t* map,
                                                                          double x, double y,
                                                                          const char* layer_ids) MLN_CABI_NOEXCEPT;
/** Query rendered features in a screen bounding box. */
MLN_CABI_API char*           mln_map_query_rendered_features_in_box(mln_map_t* map,
                                                                        double x1, double y1,
                                                                        double x2, double y2,
                                                                        const char* layer_ids) MLN_CABI_NOEXCEPT;
/** Query all features in a source's data, regardless of visibility.
 *  Returns a JSON FeatureCollection string; caller must free with mln_free_string().
 *  @param source_layer_ids  Comma-separated source-layer names (required for
 *                           vector sources, ignored for GeoJSON sources), or NULL.
 *  @param filter_json       Style-spec filter expression JSON, or NULL for all. */
MLN_CABI_API char*           mln_map_query_source_features(mln_map_t* map,
                                                              const char* source_id,
                                                              const char* source_layer_ids,
                                                              const char* filter_json) MLN_CABI_NOEXCEPT;
/** Query a feature extension — used for GeoJSON cluster expansion.
 *  Returns a JSON string (a FeatureCollection for "children"/"leaves", a bare
 *  value for "expansion-zoom"), or NULL on error; caller frees with
 *  mln_free_string().
 *  @param feature_json     The cluster feature (GeoJSON Feature) returned by a
 *                          rendered-features query.
 *  @param extension        Extension name — "supercluster" for cluster sources.
 *  @param extension_field  "children", "leaves", or "expansion-zoom".
 *  @param args_json        Optional JSON object of arguments (e.g.
 *                          {"limit":10,"offset":0} for "leaves"), or NULL. */
MLN_CABI_API char*           mln_map_query_feature_extensions(mln_map_t* map,
                                                                 const char* source_id,
                                                                 const char* feature_json,
                                                                 const char* extension,
                                                                 const char* extension_field,
                                                                 const char* args_json) MLN_CABI_NOEXCEPT;
/** Free a string returned by any mbgl query function. */
MLN_CABI_API void            mln_free_string(char* str) MLN_CABI_NOEXCEPT;

/* ── Style ─────────────────────────────────────────────────────────────────── */
MLN_CABI_API mln_style_t*   mln_map_get_style(mln_map_t* map) MLN_CABI_NOEXCEPT;

/* ── Sources ───────────────────────────────────────────────────────────────── */
MLN_CABI_API mln_source_t*  mln_style_add_geojson_source(mln_style_t* st, const char* source_id) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_source_t*  mln_style_add_geojson_source_url(mln_style_t* st, const char* source_id, const char* url) MLN_CABI_NOEXCEPT;
/** Add a GeoJSON source with style-spec options (clustering etc.).
 *  @param options_json  JSON object of GeoJSON source options — the style-spec
 *                       keys minus "type"/"data": "cluster", "clusterRadius",
 *                       "clusterMaxZoom", "clusterMinPoints", "clusterProperties",
 *                       "maxzoom", "buffer", "tolerance", "lineMetrics".
 *                       Pass NULL or "{}" for defaults.
 *  Set data afterwards with mln_geojson_source_set_data / _set_url. */
MLN_CABI_API mln_source_t*  mln_style_add_geojson_source_options(mln_style_t* st,
                                                                     const char* source_id,
                                                                     const char* options_json) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_geojson_source_set_data(mln_source_t* src, const char* geojson) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_geojson_source_set_url(mln_source_t* src, const char* url) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_source_t*  mln_style_add_vector_source(mln_style_t* st, const char* source_id, const char* url) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_source_t*  mln_style_add_raster_source(mln_style_t* st, const char* source_id, const char* url, int tile_size) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_source_t*  mln_style_add_rasterdem_source(mln_style_t* st, const char* source_id, const char* url, int tile_size) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_source_t*  mln_style_add_image_source(mln_style_t* st, const char* source_id, const char* url,
                                                           double lat0, double lon0, double lat1, double lon1,
                                                           double lat2, double lon2, double lat3, double lon3) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_style_remove_source(mln_style_t* st, const char* source_id) MLN_CABI_NOEXCEPT;
MLN_CABI_API int             mln_style_has_source(mln_style_t* st, const char* source_id) MLN_CABI_NOEXCEPT;

/* ── Layers ────────────────────────────────────────────────────────────────── */
MLN_CABI_API mln_layer_t*   mln_style_add_fill_layer(mln_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_layer_t*   mln_style_add_line_layer(mln_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_layer_t*   mln_style_add_circle_layer(mln_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_layer_t*   mln_style_add_symbol_layer(mln_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_layer_t*   mln_style_add_raster_layer(mln_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_layer_t*   mln_style_add_heatmap_layer(mln_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_layer_t*   mln_style_add_hillshade_layer(mln_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_layer_t*   mln_style_add_fill_extrusion_layer(mln_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_layer_t*   mln_style_add_background_layer(mln_style_t* st, const char* id, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_style_remove_layer(mln_style_t* st, const char* layer_id) MLN_CABI_NOEXCEPT;
MLN_CABI_API int             mln_style_has_layer(mln_style_t* st, const char* layer_id) MLN_CABI_NOEXCEPT;

MLN_CABI_API mln_status_t   mln_layer_set_source_layer(mln_layer_t* layer, const char* source_layer) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_layer_set_filter(mln_layer_t* layer, const char* filter_json) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_layer_set_min_zoom(mln_layer_t* layer, float zoom) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_layer_set_max_zoom(mln_layer_t* layer, float zoom) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_layer_set_visibility(mln_layer_t* layer, int visible) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_layer_set_paint_property(mln_layer_t* layer, const char* name, const char* value_json) MLN_CABI_NOEXCEPT;
MLN_CABI_API mln_status_t   mln_layer_set_layout_property(mln_layer_t* layer, const char* name, const char* value_json) MLN_CABI_NOEXCEPT;

/* ── Viewport bounds ───────────────────────────────────────────────────────── */
/** Returns the lat/lng bounds of the current camera viewport.
 *  out_lat_sw / out_lon_sw = south-west corner; out_lat_ne / out_lon_ne = north-east. */
MLN_CABI_API mln_status_t   mln_map_latlng_bounds_for_camera(mln_map_t* map,
                                                                  double* out_lat_sw, double* out_lon_sw,
                                                                  double* out_lat_ne, double* out_lon_ne) MLN_CABI_NOEXCEPT;

/* ── Memory / debug ─────────────────────────────────────────────────────────── */
/** Ask the renderer to free cached resources to reduce memory pressure. */
MLN_CABI_API mln_status_t   mln_map_reduce_memory_use(mln_map_t* map) MLN_CABI_NOEXCEPT;
/** Dump renderer debug information to the log. */
MLN_CABI_API mln_status_t   mln_map_dump_debug_logs(mln_map_t* map) MLN_CABI_NOEXCEPT;

/* ── Feature state ──────────────────────────────────────────────────────────── */
/** Set per-feature state as a JSON object (e.g. {"hover":true}).
 *  @param source_layer_id  Pass NULL or "" for non-vector sources. */
MLN_CABI_API mln_status_t   mln_map_set_feature_state(mln_map_t* map,
                                                          const char* source_id,
                                                          const char* source_layer_id,
                                                          const char* feature_id,
                                                          const char* state_json) MLN_CABI_NOEXCEPT;
/** Get per-feature state as a JSON object string; caller must free with mln_free_string().
 *  Returns NULL on error or if no state is set. */
MLN_CABI_API char*           mln_map_get_feature_state(mln_map_t* map,
                                                          const char* source_id,
                                                          const char* source_layer_id,
                                                          const char* feature_id) MLN_CABI_NOEXCEPT;
/** Remove feature state.  Pass NULL/empty feature_id to clear all features in a source;
 *  pass NULL/empty state_key to clear all state keys for a feature. */
MLN_CABI_API mln_status_t   mln_map_remove_feature_state(mln_map_t* map,
                                                             const char* source_id,
                                                             const char* source_layer_id,
                                                             const char* feature_id,
                                                             const char* state_key) MLN_CABI_NOEXCEPT;

/* ── Style – generic JSON add ───────────────────────────────────────────────── */
/** Add a source from a MapLibre source spec JSON object (without the "id" key).
 *  @param source_id  The unique identifier to assign to this source. */
MLN_CABI_API mln_status_t   mln_style_add_source_json(mln_style_t* st,
                                                          const char* source_id,
                                                          const char* source_json) MLN_CABI_NOEXCEPT;
/** Add a layer from a complete MapLibre layer spec JSON (must include "id" and "type").
 *  @param before_id  Insert before this layer ID, or NULL to append.
 *  Returns a non-owning layer handle, or NULL on error. */
MLN_CABI_API mln_layer_t*   mln_style_add_layer_json(mln_style_t* st,
                                                         const char* layer_json,
                                                         const char* before_id) MLN_CABI_NOEXCEPT;

/* ── Style – 3D terrain ──────────────────────────────────────────────────────
 *
 * Terrain is a style root property, not a source or layer: it drapes the map
 * over elevation from an existing raster-dem source. Add that source first with
 * mln_style_add_source_json (or include it in the style JSON); it may be the
 * same source a hillshade layer uses. */
/** Enable 3D terrain from an existing raster-dem source.
 *  @param source_id     ID of a raster-dem source already in the style.
 *  @param exaggeration  Vertical exaggeration multiplier (1.0 = true scale). */
MLN_CABI_API mln_status_t   mln_style_set_terrain(mln_style_t* st,
                                                     const char* source_id,
                                                     float exaggeration) MLN_CABI_NOEXCEPT;
/** Disable 3D terrain (the map renders flat again). */
MLN_CABI_API mln_status_t   mln_style_remove_terrain(mln_style_t* st) MLN_CABI_NOEXCEPT;
/** Returns 1 when 3D terrain is currently enabled, 0 otherwise. */
MLN_CABI_API int             mln_style_is_terrain_enabled(mln_style_t* st) MLN_CABI_NOEXCEPT;

/**
 * Progressive-loading budget for 3D terrain. Trades initial-load sharpness for smoother
 * interaction on weaker GPUs, so it is a per-map, hardware-driven choice. Values match
 * mln::TerrainLoadMode. Has no effect while terrain is off.
 */
typedef enum mln_terrain_load_mode_t {
    MLN_TERRAIN_LOAD_QUALITY     = 0, /**< No budget: every revealed tile/drape builds at once.
                                        *   Sharp, but a big burst (zooming in over new coverage)
                                        *   can stall a frame. Default. */
    MLN_TERRAIN_LOAD_BALANCED    = 1, /**< 32 new-tile builds + 16 drape re-renders per frame. */
    MLN_TERRAIN_LOAD_PERFORMANCE = 2, /**< 8 new-tile builds + 4 drape re-renders per frame:
                                        *   smoothest on weak GPUs, most progressive fill-in. */
} mln_terrain_load_mode_t;

MLN_CABI_API mln_status_t   mln_map_set_terrain_load_mode(mln_map_t* map,
                                                            mln_terrain_load_mode_t mode) MLN_CABI_NOEXCEPT;
/** Returns the current mode, or MLN_TERRAIN_LOAD_QUALITY for a null handle. */
MLN_CABI_API mln_terrain_load_mode_t mln_map_get_terrain_load_mode(mln_map_t* map) MLN_CABI_NOEXCEPT;

/* ── Offline regions + ambient cache ─────────────────────────────────────────
 *
 * Wraps mln::DatabaseFileSource. The manager shares the map's cache database
 * when created with the same cache_path / asset_path / api_key, so tiles
 * downloaded into an offline region are served to the map automatically.
 *
 * THREADING: every callback below is invoked on MapLibre's internal database
 * thread — never on the thread that made the call. Marshal to your UI thread
 * as needed. Each one-shot callback fires exactly once per accepted call
 * (a non-OK return from the function itself means the callback will NOT fire).
 *
 * Region JSON: functions that return regions deliver a JSON array of objects:
 *   [{"id":1,"type":"tilepyramid"|"geometry","styleUrl":"...",
 *     "bounds":[latSw,lonSw,latNe,lonNe] (tilepyramid only),
 *     "geometry":{...GeoJSON geometry...} (geometry only),
 *     "minZoom":0,"maxZoom":15,"pixelRatio":1.0,"includeIdeographs":true}, ...]
 * Region metadata is opaque binary and is exposed separately via
 * mln_offline_region_get_metadata (not embedded in the JSON).
 */
typedef struct mln_offline_manager_s mln_offline_manager_t;

/** Special `reason` value passed to mln_offline_region_error_fn when the
 *  Mapbox tile count limit is exceeded (not an HTTP error). */
#define MLN_OFFLINE_TILE_COUNT_LIMIT 100

/** One-shot completion callback for operations with no payload. */
typedef void (*mln_offline_done_fn)(mln_status_t status,
                                     const char*   error_message,
                                     void*         userdata);
/** One-shot callback delivering a JSON array of regions (see format above).
 *  regions_json is NULL when status != MLN_OK. */
typedef void (*mln_offline_regions_fn)(mln_status_t status,
                                        const char*   error_message,
                                        const char*   regions_json,
                                        void*         userdata);
/** One-shot callback delivering a region status JSON object:
 *  {"downloadState":0|1,"completedResourceCount":N,"completedResourceSize":N,
 *   "completedTileCount":N,"requiredTileCount":N,"completedTileSize":N,
 *   "requiredResourceCount":N,"requiredResourceCountIsPrecise":bool,
 *   "complete":bool} */
typedef void (*mln_offline_status_fn)(mln_status_t status,
                                       const char*   error_message,
                                       const char*   status_json,
                                       void*         userdata);
/** Recurring download-progress callback (installed per region).
 *  @param download_state       0=Inactive 1=Active.
 *  @param required_is_precise  Non-zero once the required count is exact.
 *  @param complete             Non-zero when the region is fully downloaded. */
typedef void (*mln_offline_progress_fn)(int64_t  region_id,
                                         int      download_state,
                                         uint64_t completed_resources,
                                         uint64_t completed_bytes,
                                         uint64_t completed_tiles,
                                         uint64_t required_resources,
                                         int      required_is_precise,
                                         int      complete,
                                         void*    userdata);
/** Recurring download-error callback. Errors are usually recoverable (the
 *  downloader retries with backoff).
 *  @param reason  mln::Response::Error::Reason value (matches mln_http_error_t),
 *                 or MLN_OFFLINE_TILE_COUNT_LIMIT (100) when the Mapbox tile
 *                 count limit was reached. */
typedef void (*mln_offline_region_error_fn)(int64_t     region_id,
                                             int         reason,
                                             const char* message,
                                             void*       userdata);

/** Create an offline manager for the given cache database.  Use the same
 *  cache_path / asset_path / api_key / max_cache_size_bytes as the map so the
 *  underlying DatabaseFileSource instance is shared. Pass 0/NULL for defaults. */
MLN_CABI_API mln_offline_manager_t* mln_offline_manager_create(
    const char* cache_path,
    const char* asset_path,
    const char* api_key,
    uint64_t    max_cache_size_bytes) MLN_CABI_NOEXCEPT;
/** Destroy the manager. Pending operation callbacks may still fire afterwards
 *  (the internal state is kept alive until they complete). */
MLN_CABI_API mln_status_t mln_offline_manager_destroy(mln_offline_manager_t* m) MLN_CABI_NOEXCEPT;

/** List all offline regions in the database. */
MLN_CABI_API mln_status_t mln_offline_list_regions(mln_offline_manager_t* m,
                                                     mln_offline_regions_fn cb,
                                                     void* userdata) MLN_CABI_NOEXCEPT;
/** Create a tile-pyramid offline region (style URL + lat/lng bounds + zoom range).
 *  The new region starts Inactive; call mln_offline_set_region_download_state
 *  to begin downloading. The callback receives a one-element region array.
 *  @param metadata  Opaque binary metadata (may be NULL when metadata_len is 0). */
MLN_CABI_API mln_status_t mln_offline_create_region(mln_offline_manager_t* m,
                                                      const char* style_url,
                                                      double lat_sw, double lon_sw,
                                                      double lat_ne, double lon_ne,
                                                      double min_zoom, double max_zoom,
                                                      float  pixel_ratio,
                                                      int    include_ideographs,
                                                      const uint8_t* metadata, int metadata_len,
                                                      mln_offline_regions_fn cb,
                                                      void* userdata) MLN_CABI_NOEXCEPT;
/** Create an offline region for an arbitrary GeoJSON geometry (a Geometry,
 *  Feature, or single-feature FeatureCollection). */
MLN_CABI_API mln_status_t mln_offline_create_region_geometry(mln_offline_manager_t* m,
                                                               const char* style_url,
                                                               const char* geometry_geojson,
                                                               double min_zoom, double max_zoom,
                                                               float  pixel_ratio,
                                                               int    include_ideographs,
                                                               const uint8_t* metadata, int metadata_len,
                                                               mln_offline_regions_fn cb,
                                                               void* userdata) MLN_CABI_NOEXCEPT;
/** Delete a region and evict its resources (slow if auto-packing is enabled). */
MLN_CABI_API mln_status_t mln_offline_delete_region(mln_offline_manager_t* m,
                                                      int64_t region_id,
                                                      mln_offline_done_fn cb,
                                                      void* userdata) MLN_CABI_NOEXCEPT;
/** Force revalidation of all the region's tiles with the server. */
MLN_CABI_API mln_status_t mln_offline_invalidate_region(mln_offline_manager_t* m,
                                                          int64_t region_id,
                                                          mln_offline_done_fn cb,
                                                          void* userdata) MLN_CABI_NOEXCEPT;
/** Pause (active=0) or start/resume (active=1) downloading a region's resources. */
MLN_CABI_API mln_status_t mln_offline_set_region_download_state(mln_offline_manager_t* m,
                                                                  int64_t region_id,
                                                                  int active) MLN_CABI_NOEXCEPT;
/** Install (or replace) the progress/error observer for a region.
 *  Pass NULL for both callbacks to remove the observer. */
MLN_CABI_API mln_status_t mln_offline_set_region_observer(mln_offline_manager_t* m,
                                                            int64_t region_id,
                                                            mln_offline_progress_fn progress,
                                                            mln_offline_region_error_fn error,
                                                            void* userdata) MLN_CABI_NOEXCEPT;
/** Query the current status of a region. */
MLN_CABI_API mln_status_t mln_offline_get_region_status(mln_offline_manager_t* m,
                                                          int64_t region_id,
                                                          mln_offline_status_fn cb,
                                                          void* userdata) MLN_CABI_NOEXCEPT;
/** Replace a region's opaque binary metadata. */
MLN_CABI_API mln_status_t mln_offline_update_region_metadata(mln_offline_manager_t* m,
                                                               int64_t region_id,
                                                               const uint8_t* metadata, int metadata_len,
                                                               mln_offline_done_fn cb,
                                                               void* userdata) MLN_CABI_NOEXCEPT;
/** Read a region's metadata from the manager's cache (synchronous — the region
 *  must have been returned by a previous list/create/merge on this manager).
 *  Returns a buffer to free with mln_free_string(), or NULL if the region is
 *  unknown or has no metadata; *out_len receives the byte length. */
MLN_CABI_API char* mln_offline_region_get_metadata(mln_offline_manager_t* m,
                                                    int64_t region_id,
                                                    int* out_len) MLN_CABI_NOEXCEPT;
/** Merge regions from a secondary database file into this one.
 *  The side database may be upgraded in place (needs write access). */
MLN_CABI_API mln_status_t mln_offline_merge_database(mln_offline_manager_t* m,
                                                       const char* side_db_path,
                                                       mln_offline_regions_fn cb,
                                                       void* userdata) MLN_CABI_NOEXCEPT;
/** Set the Mapbox-tile count limit for offline regions (does not affect
 *  non-Mapbox tile sources). */
MLN_CABI_API mln_status_t mln_offline_set_tile_count_limit(mln_offline_manager_t* m,
                                                             uint64_t limit) MLN_CABI_NOEXCEPT;

/* ── Ambient cache / database maintenance ──────────────────────────────────── */
/** Cap the ambient (non-region) cache size in bytes. Call before heavy use. */
MLN_CABI_API mln_status_t mln_offline_set_maximum_ambient_cache_size(mln_offline_manager_t* m,
                                                                       uint64_t bytes,
                                                                       mln_offline_done_fn cb,
                                                                       void* userdata) MLN_CABI_NOEXCEPT;
/** Erase the ambient cache (offline regions are not affected). */
MLN_CABI_API mln_status_t mln_offline_clear_ambient_cache(mln_offline_manager_t* m,
                                                            mln_offline_done_fn cb,
                                                            void* userdata) MLN_CABI_NOEXCEPT;
/** Force revalidation of ambient-cache resources with the server. */
MLN_CABI_API mln_status_t mln_offline_invalidate_ambient_cache(mln_offline_manager_t* m,
                                                                 mln_offline_done_fn cb,
                                                                 void* userdata) MLN_CABI_NOEXCEPT;
/** Vacuum the database file to reclaim disk space. */
MLN_CABI_API mln_status_t mln_offline_pack_database(mln_offline_manager_t* m,
                                                      mln_offline_done_fn cb,
                                                      void* userdata) MLN_CABI_NOEXCEPT;
/** Delete and re-initialise the database (regions AND ambient cache). */
MLN_CABI_API mln_status_t mln_offline_reset_database(mln_offline_manager_t* m,
                                                       mln_offline_done_fn cb,
                                                       void* userdata) MLN_CABI_NOEXCEPT;
/** Enable/disable automatic packing after region deletion / cache clears
 *  (enabled by default). */
MLN_CABI_API mln_status_t mln_offline_set_pack_database_automatically(mln_offline_manager_t* m,
                                                                        int enabled) MLN_CABI_NOEXCEPT;

/* ── Version ───────────────────────────────────────────────────────────────── */
MLN_CABI_API const char*     mln_cabi_version(void) MLN_CABI_NOEXCEPT;

/* ── Android helpers (only compiled on Android) ────────────────────────────── */
#ifdef __ANDROID__
/** Acquire an ANativeWindow from a Java android.view.Surface.
 *  Caller must call mln_android_release_window when done. */
MLN_CABI_API void*  mln_android_acquire_window(void* jni_env, void* surface_jobject) MLN_CABI_NOEXCEPT;
/** Release an ANativeWindow obtained via mln_android_acquire_window. */
MLN_CABI_API void   mln_android_release_window(void* window) MLN_CABI_NOEXCEPT;
#endif /* __ANDROID__ */

/* ── Host HTTP provider (all platforms) ────────────────────────────────────── */
/*
 * Lets the host answer resource requests instead of a native HTTP stack.
 *
 * Originally Android-only, where a standalone NDK build has no HTTP
 * implementation of its own. It is available everywhere now because the
 * indirection is useful in its own right: the byte range mbgl asks for is
 * passed through, and PMTiles reads are all ranged, so a host holding an
 * archive somewhere other than a web server — a BitTorrent swarm, an embedded
 * database, an encrypted bundle — can satisfy tile reads directly.
 *
 * Registering a provider replaces the network file source for the whole
 * process, so it must be done before the first map is created. Registering
 * nothing leaves the platform's own network stack untouched.
 *
 * One behavioural difference worth knowing. On Android the provider sits
 * underneath mbgl's OnlineFileSource, so requests still get its retry with
 * backoff, rate-limit handling and queueing — only the transport is delegated.
 * Everywhere else the provider replaces OnlineFileSource entirely, because
 * there is no way to slot in beneath it without colliding with the platform's
 * own HTTPFileSource. A host registering a provider on those platforms is
 * therefore responsible for its own retry and backoff; failing a request means
 * mbgl will not retry it on the host's behalf.
 */
/**
 * Callback type for the HTTP provider.  Called by the native layer when it
 * needs to fetch a URL.  The host (C#) must call mln_http_respond() with the
 * same request_id when the fetch completes or fails.
 *
 * @param request_id  Unique ID for this request.  Pass back to mln_http_respond().
 * @param url         The URL to fetch (null-terminated UTF-8).
 * @param etag        Previous ETag for a conditional GET, or NULL.
 * @param modified    Previous Last-Modified for a conditional GET, or NULL.
 * @param range_start First byte of a Range request, or -1 for a full fetch.
 * @param range_end   Last byte (inclusive) of a Range request, or -1 for a full fetch.
 *                    When both are >= 0 send "Range: bytes=range_start-range_end".
 *                    HTTP 206 Partial Content responses are treated as success.
 * @param userdata    Opaque pointer supplied to mln_set_http_provider().
 */
typedef void (*mln_http_provider_fn)(
    uint64_t    request_id,
    const char* url,
    const char* etag,
    const char* modified,
    int64_t     range_start,
    int64_t     range_end,
    void*       userdata);

/**
 * Error codes passed to mln_http_respond().
 * Values are intentionally aligned with mln::Response::Error::Reason.
 */
typedef enum mln_http_error_t {
    MLN_HTTP_ERROR_NONE       = 0, /**< Success — no error. */
    MLN_HTTP_ERROR_NOT_FOUND  = 2, /**< HTTP 404. */
    MLN_HTTP_ERROR_SERVER     = 3, /**< HTTP 5xx. */
    MLN_HTTP_ERROR_CONNECTION = 4, /**< Network or connection failure. */
    MLN_HTTP_ERROR_RATE_LIMIT = 5, /**< HTTP 429. */
    MLN_HTTP_ERROR_OTHER      = 6, /**< Any other error. */
} mln_http_error_t;

/**
 * Register the HTTP provider callback.  Must be called before the first map is
 * created.  Pass NULL to remove the current provider (requests will fail with a
 * Connection error until a new provider is registered).
 */
MLN_CABI_API void mln_set_http_provider(mln_http_provider_fn fn,
                                           void*                 userdata) MLN_CABI_NOEXCEPT;

/**
 * Callback invoked when the native layer no longer needs a previously-started
 * request (mbgl superseded or dropped the tile, e.g. while zooming/panning).
 * The host must abort the corresponding in-flight fetch so its connection is
 * freed for requests that are still needed — without this, superseded requests
 * run to completion and can starve the tiles at the current zoom.
 *
 * @param request_id  The ID that was supplied to mln_http_provider_fn.
 * @param userdata    Opaque pointer supplied to mln_set_http_cancel_provider().
 */
typedef void (*mln_http_cancel_fn)(uint64_t request_id, void* userdata);

/**
 * Register the request-cancellation callback (optional but strongly
 * recommended). Must be called before the first map is created. Pass NULL to
 * remove it.
 */
MLN_CABI_API void mln_set_http_cancel_provider(mln_http_cancel_fn fn,
                                                void*               userdata) MLN_CABI_NOEXCEPT;

/**
 * Deliver a completed HTTP response back to the native layer.
 * Must be called exactly once per request unless mln_http_cancel() was already
 * called for the same request_id.  Safe to call from any thread.
 *
 * @param request_id       The ID supplied to mln_http_provider_fn.
 * @param error            MLN_HTTP_ERROR_NONE on success, otherwise an error code.
 * @param error_message    Human-readable error string (may be NULL).
 * @param http_status      Raw HTTP status code (e.g. 200, 404); ignored when error != NONE.
 * @param data             Response body bytes, or NULL.
 * @param data_len         Length of data in bytes.
 * @param etag             ETag header value, or NULL.
 * @param modified         Last-Modified header value (RFC 1123), or NULL.
 * @param expires          Expires header value (RFC 1123), or NULL.
 * @param cache_control    Cache-Control header value, or NULL.
 * @param no_content       1 if the response was 204 No Content (or 404 for a tile).
 * @param not_modified     1 if the response was 304 Not Modified.
 * @param must_revalidate  1 if Cache-Control: must-revalidate was present.
 */
MLN_CABI_API void mln_http_respond(
    uint64_t          request_id,
    mln_http_error_t error,
    const char*       error_message,
    int               http_status,
    const char*       data,
    int               data_len,
    const char*       etag,
    const char*       modified,
    const char*       expires,
    const char*       cache_control,
    int               no_content,
    int               not_modified,
    int               must_revalidate) MLN_CABI_NOEXCEPT;

/**
 * Cancel a pending HTTP request.  After this call, any subsequent
 * mln_http_respond() with the same request_id is silently ignored.
 * The host C# should also abort the in-flight HttpClient request.
 */
MLN_CABI_API void mln_http_cancel(uint64_t request_id) MLN_CABI_NOEXCEPT;

/**
 * Claim a URL prefix, so only matching requests reach the provider.
 *
 * Without any claim the provider receives everything, which means the host owns
 * the whole network stack — including retry and backoff, since mbgl's
 * OnlineFileSource is then out of the picture (see the note above).
 *
 * Claiming instead lets a host serve a handful of URLs from somewhere unusual —
 * an archive held in a BitTorrent swarm, say — while every other request is
 * still handled by maplibre's own network stack, with its retry, rate-limit
 * handling and queueing intact. Prefer this: the blast radius is a few URLs
 * rather than every resource the map fetches.
 *
 * Matching is a plain prefix comparison, deliberately: this runs for every
 * resource the map requests, so it must stay cheap. Call before the first map
 * is created, alongside mln_set_http_provider. Has no effect on Android, where
 * the provider sits beneath OnlineFileSource and necessarily sees all traffic.
 */
MLN_CABI_API void mln_http_provider_claim_prefix(const char* url_prefix) MLN_CABI_NOEXCEPT;

/** Drop every claimed prefix, returning the provider to handling all requests. */
MLN_CABI_API void mln_http_provider_clear_claims(void) MLN_CABI_NOEXCEPT;

#ifdef __cplusplus
} // extern "C"
#endif
