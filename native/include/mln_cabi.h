/**
 * mln_cabi.h — Flat C ABI for MapLibre Native.
 *
 * Design principles (informed by maplibre-native-ffi):
 *  - All handles are typed opaque struct pointers — the C compiler rejects
 *    passing the wrong handle type.
 *  - Every mutating function returns mbgl_status_t so callers can detect
 *    failures without a separate out-parameter.
 *  - mbgl_get_last_error() returns a thread-local diagnostic string for the
 *    most recent non-OK return.
 *  - All exported functions are marked MLN_CABI_NOEXCEPT to prevent
 *    exceptions crossing the ABI boundary.
 *
 * Thread-safety: Map must be used on the same thread as its RunLoop.
 */
#pragma once

#include <stdint.h>

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
 *  On any non-OK return, call mbgl_get_last_error() for a diagnostic string. */
typedef enum mbgl_status_t {
    MBGL_OK              =  0,  /**< Success. */
    MBGL_INVALID_ARG     = -1,  /**< A required argument was NULL or out of range. */
    MBGL_INVALID_STATE   = -2,  /**< Call is not valid in the current state. */
    MBGL_WRONG_THREAD    = -3,  /**< Called from the wrong thread. */
    MBGL_UNSUPPORTED     = -4,  /**< Operation not supported on this platform. */
    MBGL_NATIVE_ERROR    = -5   /**< An internal C++ exception was caught; see mbgl_get_last_error(). */
} mbgl_status_t;

/** Returns a thread-local string describing the most recent non-OK status.
 *  Valid until the next MBGL call on this thread. Never NULL. */
MLN_CABI_API const char* mbgl_get_last_error(void) MLN_CABI_NOEXCEPT;

/* ── Opaque handle types ───────────────────────────────────────────────────── */
/* Forward-declared typed structs: passing the wrong handle type is a
 * compile error rather than a silent runtime bug. */
typedef struct mbgl_runloop_s  mbgl_runloop_t;
typedef struct mbgl_frontend_s mbgl_frontend_t;
typedef struct mbgl_map_s      mbgl_map_t;
typedef struct mbgl_style_s    mbgl_style_t;
typedef struct mbgl_source_s   mbgl_source_t;
typedef struct mbgl_layer_s    mbgl_layer_t;

/* ── Callbacks ─────────────────────────────────────────────────────────────── */
typedef void (*mbgl_render_fn)(void* userdata);
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
 *  @param userdata    Opaque pointer passed to mbgl_map_create. */
typedef void (*mbgl_map_observer_fn)(const char* event_name, const char* detail, void* userdata);

/* ── Debug options ─────────────────────────────────────────────────────────── */
/** Bitmask of debug visualisation overlays.  OR together the flags you want. */
typedef enum mbgl_debug_options_t {
    MBGL_DEBUG_NONE        = 0,
    MBGL_DEBUG_TILE_BORDERS = 1 << 1,  /**< Draw tile boundary outlines. */
    MBGL_DEBUG_PARSE_STATUS = 1 << 2,  /**< Show tile parse/loading state. */
    MBGL_DEBUG_TIMESTAMPS   = 1 << 3,  /**< Print frame timestamps on tiles. */
    MBGL_DEBUG_COLLISION    = 1 << 4,  /**< Highlight symbol collision boxes. */
    MBGL_DEBUG_OVERDRAW     = 1 << 5,  /**< Heat-map style overdraw visualisation. */
    MBGL_DEBUG_STENCIL_CLIP = 1 << 6,  /**< Show stencil buffer clipping regions. */
    MBGL_DEBUG_DEPTH_BUFFER = 1 << 7   /**< Show depth buffer contents. */
} mbgl_debug_options_t;

/* ── Log callback ──────────────────────────────────────────────────────────── */
typedef enum mbgl_log_level_t {
    MBGL_LOG_DEBUG   = 0,
    MBGL_LOG_INFO    = 1,
    MBGL_LOG_WARNING = 2,
    MBGL_LOG_ERROR   = 3
} mbgl_log_level_t;

/**
 * Log intercept callback.
 * @param level     Severity of the log record.
 * @param category  Short category string (e.g. "Parse", "Render", "Network").
 * @param message   The log message.
 * @param userdata  Opaque pointer passed to mbgl_install_log_callback.
 * @return          Non-zero to consume the record (suppress default output),
 *                  0 to let MapLibre also emit it to the default sink.
 */
typedef int (*mbgl_log_fn)(mbgl_log_level_t level,
                            const char*      category,
                            const char*      message,
                            void*            userdata);

/**
 * Install a global log intercept callback.
 * Pass NULL to remove any previously installed callback and restore default
 * logging behaviour.  The callback is invoked on whatever thread MapLibre
 * emits the log record — synchronise as needed.
 */
MLN_CABI_API mbgl_status_t mbgl_install_log_callback(mbgl_log_fn fn,
                                                        void*        userdata) MLN_CABI_NOEXCEPT;

/* ── Network status ────────────────────────────────────────────────────────── */
/** Toggle the process-global network state.  Pass 0 to force offline mode:
 *  all network requests are suspended and only cached / offline resources are
 *  served.  Pass 1 to restore online mode (queued requests resume). */
MLN_CABI_API mbgl_status_t mbgl_network_status_set(int online) MLN_CABI_NOEXCEPT;
/** Returns 1 if the network is in online mode, 0 if offline. */
MLN_CABI_API int           mbgl_network_status_get(void) MLN_CABI_NOEXCEPT;

/* ── RunLoop ───────────────────────────────────────────────────────────────── */
MLN_CABI_API mbgl_runloop_t* mbgl_runloop_create(void) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_runloop_destroy(mbgl_runloop_t* rl) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_runloop_run_once(mbgl_runloop_t* rl) MLN_CABI_NOEXCEPT;

/* ── Frontend ──────────────────────────────────────────────────────────────── */
MLN_CABI_API mbgl_frontend_t* mbgl_frontend_create_gl(
    void*          surface_handle,
    void*          gl_context,
    int            width_px,
    int            height_px,
    float          pixel_ratio,
    mbgl_render_fn render_callback,
    void*          render_userdata) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t    mbgl_frontend_destroy(mbgl_frontend_t* fe) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t    mbgl_frontend_render(mbgl_frontend_t* fe) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t    mbgl_frontend_set_size(mbgl_frontend_t* fe, int width_px, int height_px) MLN_CABI_NOEXCEPT;
MLN_CABI_API void*            mbgl_frontend_get_native_view(mbgl_frontend_t* fe) MLN_CABI_NOEXCEPT;

/* ── Map ───────────────────────────────────────────────────────────────────── */
MLN_CABI_API mbgl_map_t*     mbgl_map_create(
    mbgl_frontend_t*      fe,
    mbgl_runloop_t*       rl,
    const char*           cache_path,
    const char*           asset_path,
    float                 pixel_ratio,
    mbgl_map_observer_fn  observer,
    void*                 observer_userdata) MLN_CABI_NOEXCEPT;
/** Extended map factory.  Identical to mbgl_map_create plus resource options:
 *  @param api_key              API key appended to tile-server requests, or NULL.
 *  @param max_cache_size_bytes Maximum size of the on-disk cache database in
 *                              bytes, or 0 for the MapLibre default (~50 MB). */
MLN_CABI_API mbgl_map_t*     mbgl_map_create2(
    mbgl_frontend_t*      fe,
    mbgl_runloop_t*       rl,
    const char*           cache_path,
    const char*           asset_path,
    const char*           api_key,
    uint64_t              max_cache_size_bytes,
    float                 pixel_ratio,
    mbgl_map_observer_fn  observer,
    void*                 observer_userdata) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_destroy(mbgl_map_t* map) MLN_CABI_NOEXCEPT;

MLN_CABI_API mbgl_status_t   mbgl_map_set_style_url(mbgl_map_t* map, const char* url) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_set_style_json(mbgl_map_t* map, const char* json) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_set_size(mbgl_map_t* map, int width_px, int height_px) MLN_CABI_NOEXCEPT;

MLN_CABI_API mbgl_status_t   mbgl_map_jump_to(mbgl_map_t* map, double lat, double lon,
                                                double zoom, double bearing, double pitch) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_ease_to(mbgl_map_t* map, double lat, double lon,
                                                double zoom, double bearing, double pitch,
                                                int64_t duration_ms) MLN_CABI_NOEXCEPT;

MLN_CABI_API double          mbgl_map_get_zoom(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API double          mbgl_map_get_bearing(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API double          mbgl_map_get_pitch(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API void            mbgl_map_get_center(mbgl_map_t* map, double* out_lat, double* out_lon) MLN_CABI_NOEXCEPT;

MLN_CABI_API mbgl_status_t   mbgl_map_set_min_zoom(mbgl_map_t* map, double zoom) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_set_max_zoom(mbgl_map_t* map, double zoom) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_trigger_repaint(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_cancel_transitions(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API int             mbgl_map_is_fully_loaded(mbgl_map_t* map) MLN_CABI_NOEXCEPT;

/* ── Map – debug overlays ───────────────────────────────────────────────────── */
/** Read the current debug overlay bitmask (OR of mbgl_debug_options_t flags). */
MLN_CABI_API int             mbgl_map_get_debug_options(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
/** Set the debug overlay bitmask.  Pass MBGL_DEBUG_NONE to disable all. */
MLN_CABI_API mbgl_status_t   mbgl_map_set_debug_options(mbgl_map_t* map, int options) MLN_CABI_NOEXCEPT;

/* ── Map – gesture / interactive movement ──────────────────────────────────── */
/** Inform the map that a user gesture is in progress (suppresses animated
 *  camera snap-back during panning).  Call with 1 on touch-down, 0 on touch-up. */
MLN_CABI_API mbgl_status_t   mbgl_map_set_gesture_in_progress(mbgl_map_t* map, int in_progress) MLN_CABI_NOEXCEPT;
/** Returns 1 while a gesture is flagged in progress via mbgl_map_set_gesture_in_progress. */
MLN_CABI_API int             mbgl_map_is_gesture_in_progress(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
/** Returns 1 while a rotate transition/animation is running. */
MLN_CABI_API int             mbgl_map_is_rotating(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
/** Returns 1 while a zoom/scale transition/animation is running. */
MLN_CABI_API int             mbgl_map_is_scaling(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
/** Returns 1 while a pan transition/animation is running. */
MLN_CABI_API int             mbgl_map_is_panning(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
/** Translate the viewport by (dx, dy) screen pixels, optionally animated. */
MLN_CABI_API mbgl_status_t   mbgl_map_move_by(mbgl_map_t* map, double dx, double dy,
                                                int64_t duration_ms) MLN_CABI_NOEXCEPT;
/** Rotate the map by dragging first→second screen coordinate. */
MLN_CABI_API mbgl_status_t   mbgl_map_rotate_by(mbgl_map_t* map,
                                                   double x0, double y0,
                                                   double x1, double y1) MLN_CABI_NOEXCEPT;
/** Pitch the map by delta degrees, optionally animated. */
MLN_CABI_API mbgl_status_t   mbgl_map_pitch_by(mbgl_map_t* map, double delta_degrees,
                                                 int64_t duration_ms) MLN_CABI_NOEXCEPT;

/* ── Map – map options (post-create) ────────────────────────────────────────── */
/** 0=None 1=NorthUp 2=Compass 3=Manual */
MLN_CABI_API mbgl_status_t   mbgl_map_set_north_orientation(mbgl_map_t* map, int orientation) MLN_CABI_NOEXCEPT;
/** 0=None 1=HeightOnly 2=WidthAndHeight */
MLN_CABI_API mbgl_status_t   mbgl_map_set_constrain_mode(mbgl_map_t* map, int mode) MLN_CABI_NOEXCEPT;
/** 0=Default 1=FlippedY */
MLN_CABI_API mbgl_status_t   mbgl_map_set_viewport_mode(mbgl_map_t* map, int mode) MLN_CABI_NOEXCEPT;

/* ── Map – camera constraints read-back ────────────────────────────────────── */
/** Read current BoundOptions.  Pass NULL for fields you don't need. */
MLN_CABI_API void            mbgl_map_get_bounds(mbgl_map_t* map,
                                                   double* out_lat_sw, double* out_lon_sw,
                                                   double* out_lat_ne, double* out_lon_ne,
                                                   double* out_min_zoom, double* out_max_zoom,
                                                   double* out_min_pitch, double* out_max_pitch) MLN_CABI_NOEXCEPT;

/* ── Map – tile LOD controls (Tier 2) ──────────────────────────────────────── */
MLN_CABI_API mbgl_status_t   mbgl_map_set_prefetch_zoom_delta(mbgl_map_t* map, int delta) MLN_CABI_NOEXCEPT;
MLN_CABI_API int             mbgl_map_get_prefetch_zoom_delta(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_set_tile_lod_min_radius(mbgl_map_t* map, double radius) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_set_tile_lod_scale(mbgl_map_t* map, double scale) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_set_tile_lod_pitch_threshold(mbgl_map_t* map, double threshold_rad) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_set_tile_lod_zoom_shift(mbgl_map_t* map, double shift) MLN_CABI_NOEXCEPT;
/** mode: 0=default, 1=distance */
MLN_CABI_API mbgl_status_t   mbgl_map_set_tile_lod_mode(mbgl_map_t* map, int mode) MLN_CABI_NOEXCEPT;

/* ── Map – camera for points / geometry (Tier 2) ───────────────────────────── */
/** Compute camera to fit an arbitrary list of lat/lon pairs with padding.
 *  @param latlngs  Flat array of alternating lat, lon values (length = count * 2). */
MLN_CABI_API mbgl_status_t   mbgl_map_camera_for_latlngs(mbgl_map_t* map,
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
MLN_CABI_API mbgl_status_t   mbgl_map_pixels_for_latlngs(mbgl_map_t* map,
                                                            const double* latlngs, int count,
                                                            double* out_xy) MLN_CABI_NOEXCEPT;
/** Batch un-project N screen pixel pairs to lat/lon.
 *  @param xy        Flat array [x0, y0, x1, y1, ...] length = count * 2.
 *  @param out_ll    Caller-allocated output [lat0, lon0, ...] length = count * 2. */
MLN_CABI_API mbgl_status_t   mbgl_map_latlngs_for_pixels(mbgl_map_t* map,
                                                            const double* xy, int count,
                                                            double* out_ll) MLN_CABI_NOEXCEPT;

/* ── Style – enumeration (Tier 1) ──────────────────────────────────────────── */
/** Get style metadata.  Returned strings must be freed with mbgl_free_string(). */
MLN_CABI_API char*           mbgl_style_get_url(mbgl_style_t* st) MLN_CABI_NOEXCEPT;
MLN_CABI_API char*           mbgl_style_get_name(mbgl_style_t* st) MLN_CABI_NOEXCEPT;
/** Returns a JSON array of source IDs (IDs may contain any character, so a
 *  delimiter-joined string would be ambiguous); caller frees with mbgl_free_string(). */
MLN_CABI_API char*           mbgl_style_get_source_ids(mbgl_style_t* st) MLN_CABI_NOEXCEPT;
/** Returns a JSON array of layer IDs in draw order; caller frees. */
MLN_CABI_API char*           mbgl_style_get_layer_ids(mbgl_style_t* st) MLN_CABI_NOEXCEPT;
/** Get a layer handle by ID (returns NULL if not found). */
MLN_CABI_API mbgl_layer_t*   mbgl_style_get_layer(mbgl_style_t* st, const char* layer_id) MLN_CABI_NOEXCEPT;
/** Get a source handle by ID (returns NULL if not found). */
MLN_CABI_API mbgl_source_t*  mbgl_style_get_source(mbgl_style_t* st, const char* source_id) MLN_CABI_NOEXCEPT;
/**
 * Returns the attribution text for the given source handle (may be NULL or empty).
 * The returned string must be freed with mbgl_free_string().
 * Suitable for building an OSM-compliant attribution overlay.
 */
MLN_CABI_API char*           mbgl_source_get_attribution(mbgl_source_t* src) MLN_CABI_NOEXCEPT;

/* ── Layer – read-back ──────────────────────────────────────────────────────── */
/** Returns a JSON-encoded value or NULL if not set; caller frees. */
MLN_CABI_API char*           mbgl_layer_get_paint_property(mbgl_layer_t* layer, const char* name) MLN_CABI_NOEXCEPT;
MLN_CABI_API char*           mbgl_layer_get_layout_property(mbgl_layer_t* layer, const char* name) MLN_CABI_NOEXCEPT;
/** Returns 1 if visible, 0 if none. */
MLN_CABI_API int             mbgl_layer_get_visibility(mbgl_layer_t* layer) MLN_CABI_NOEXCEPT;

MLN_CABI_API mbgl_status_t   mbgl_map_on_scroll(mbgl_map_t* map, double delta, double cx, double cy) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_on_double_tap(mbgl_map_t* map, double x, double y) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_on_pan_start(mbgl_map_t* map, double x, double y) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_on_pan_move(mbgl_map_t* map, double dx, double dy) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_on_pan_end(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_on_pinch(mbgl_map_t* map, double scale_factor,
                                                 double cx, double cy) MLN_CABI_NOEXCEPT;

/* ── Map – additional camera / bounds / projection ─────────────────────────── */
MLN_CABI_API mbgl_status_t   mbgl_map_fly_to(mbgl_map_t* map, double lat, double lon,
                                               double zoom, double bearing, double pitch,
                                               int64_t duration_ms) MLN_CABI_NOEXCEPT;

/* ── Map – camera with edge padding ────────────────────────────────────────────
 * Padded variants of the camera movement functions.  Padding (in screen pixels,
 * order top/left/bottom/right) shifts the camera's effective centre so the
 * target appears centred in the *unobscured* part of the viewport — use when
 * panels or overlays cover part of the map.  Pass NaN for zoom, bearing, or
 * pitch to leave that field unchanged. */
MLN_CABI_API mbgl_status_t   mbgl_map_jump_to_padded(mbgl_map_t* map,
                                                       double lat, double lon,
                                                       double zoom, double bearing, double pitch,
                                                       double pad_top, double pad_left,
                                                       double pad_bottom, double pad_right) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_ease_to_padded(mbgl_map_t* map,
                                                       double lat, double lon,
                                                       double zoom, double bearing, double pitch,
                                                       double pad_top, double pad_left,
                                                       double pad_bottom, double pad_right,
                                                       int64_t duration_ms) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_map_fly_to_padded(mbgl_map_t* map,
                                                      double lat, double lon,
                                                      double zoom, double bearing, double pitch,
                                                      double pad_top, double pad_left,
                                                      double pad_bottom, double pad_right,
                                                      int64_t duration_ms) MLN_CABI_NOEXCEPT;
/** Read the full camera state in one call, optionally offset by edge padding.
 *  Pass 0 for all pads to read the raw camera.  Pass NULL for outputs you
 *  don't need. */
MLN_CABI_API mbgl_status_t   mbgl_map_get_camera(mbgl_map_t* map,
                                                   double pad_top, double pad_left,
                                                   double pad_bottom, double pad_right,
                                                   double* out_lat, double* out_lon,
                                                   double* out_zoom,
                                                   double* out_bearing,
                                                   double* out_pitch) MLN_CABI_NOEXCEPT;
/** Multiply the map scale by @p scale (2.0 = one zoom level in), optionally
 *  about a screen anchor point.  Pass NaN for anchor_x/anchor_y to scale about
 *  the viewport centre. */
MLN_CABI_API mbgl_status_t   mbgl_map_scale_by(mbgl_map_t* map, double scale,
                                                 double anchor_x, double anchor_y,
                                                 int64_t duration_ms) MLN_CABI_NOEXCEPT;

/** Set geographic camera bounds and optional zoom/pitch limits.
 *  Pass NaN for any field to leave it unset (e.g. no lat/lng constraint). */
MLN_CABI_API mbgl_status_t   mbgl_map_set_bounds(mbgl_map_t* map,
                                                   double lat_sw, double lon_sw,
                                                   double lat_ne, double lon_ne,
                                                   double min_zoom, double max_zoom,
                                                   double min_pitch, double max_pitch) MLN_CABI_NOEXCEPT;

/** Compute CameraOptions that fits the given LatLngBounds with optional padding.
 *  Padding order: top, left, bottom, right (matches mbgl::EdgeInsets field order). */
MLN_CABI_API mbgl_status_t   mbgl_map_camera_for_bounds(mbgl_map_t* map,
                                                          double lat_sw, double lon_sw,
                                                          double lat_ne, double lon_ne,
                                                          double pad_top, double pad_left,
                                                          double pad_bottom, double pad_right,
                                                          double* out_lat, double* out_lon,
                                                          double* out_zoom, double* out_bearing,
                                                          double* out_pitch) MLN_CABI_NOEXCEPT;

MLN_CABI_API void            mbgl_map_pixel_for_latlng(mbgl_map_t* map,
                                                         double lat, double lon,
                                                         double* out_x, double* out_y) MLN_CABI_NOEXCEPT;
MLN_CABI_API void            mbgl_map_latlng_for_pixel(mbgl_map_t* map,
                                                         double x, double y,
                                                         double* out_lat, double* out_lon) MLN_CABI_NOEXCEPT;

MLN_CABI_API mbgl_status_t   mbgl_map_set_projection_mode(mbgl_map_t* map,
                                                            int axonometric,
                                                            double x_skew, double y_skew) MLN_CABI_NOEXCEPT;

/* ── Style – images ─────────────────────────────────────────────────────────── */
/** Add a sprite image from premultiplied RGBA bytes (length = width * height * 4). */
MLN_CABI_API mbgl_status_t   mbgl_style_add_image(mbgl_style_t* st, const char* image_id,
                                                    int width, int height,
                                                    float pixel_ratio, int sdf,
                                                    const uint8_t* rgba_premultiplied) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_style_remove_image(mbgl_style_t* st, const char* image_id) MLN_CABI_NOEXCEPT;

/** Returns the currently loaded style as a JSON string; caller must free with mbgl_free_string(). */
MLN_CABI_API char*           mbgl_style_get_json(mbgl_style_t* st) MLN_CABI_NOEXCEPT;

/** Set the global style transition duration and delay (milliseconds). */
MLN_CABI_API mbgl_status_t   mbgl_style_set_transition(mbgl_style_t* st,
                                                          int64_t duration_ms,
                                                          int64_t delay_ms) MLN_CABI_NOEXCEPT;

/** Set a Light property by name using a JSON-encoded value.
 *  Valid names: "anchor" ("map"|"viewport"), "color" ("#rrggbb"),
 *               "intensity" (0-1 float), "position" ([radial, azimuthal, polar]). */
MLN_CABI_API mbgl_status_t   mbgl_style_set_light_property(mbgl_style_t* st,
                                                              const char* name,
                                                              const char* value_json) MLN_CABI_NOEXCEPT;

/* ── Style – additional layer types ─────────────────────────────────────────── */
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_location_indicator_layer(mbgl_style_t* st,
                                                                        const char* id,
                                                                        const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_color_relief_layer(mbgl_style_t* st,
                                                                  const char* id,
                                                                  const char* src,
                                                                  const char* before) MLN_CABI_NOEXCEPT;

/* ── Feature queries ────────────────────────────────────────────────────────── */
/** Query rendered features at a screen point.
 *  Returns a JSON FeatureCollection string; caller must free with mbgl_free_string().
 *  @param layer_ids  Comma-separated layer IDs to restrict the query, or NULL for all. */
MLN_CABI_API char*           mbgl_map_query_rendered_features_at_point(mbgl_map_t* map,
                                                                          double x, double y,
                                                                          const char* layer_ids) MLN_CABI_NOEXCEPT;
/** Query rendered features in a screen bounding box. */
MLN_CABI_API char*           mbgl_map_query_rendered_features_in_box(mbgl_map_t* map,
                                                                        double x1, double y1,
                                                                        double x2, double y2,
                                                                        const char* layer_ids) MLN_CABI_NOEXCEPT;
/** Query all features in a source's data, regardless of visibility.
 *  Returns a JSON FeatureCollection string; caller must free with mbgl_free_string().
 *  @param source_layer_ids  Comma-separated source-layer names (required for
 *                           vector sources, ignored for GeoJSON sources), or NULL.
 *  @param filter_json       Style-spec filter expression JSON, or NULL for all. */
MLN_CABI_API char*           mbgl_map_query_source_features(mbgl_map_t* map,
                                                              const char* source_id,
                                                              const char* source_layer_ids,
                                                              const char* filter_json) MLN_CABI_NOEXCEPT;
/** Query a feature extension — used for GeoJSON cluster expansion.
 *  Returns a JSON string (a FeatureCollection for "children"/"leaves", a bare
 *  value for "expansion-zoom"), or NULL on error; caller frees with
 *  mbgl_free_string().
 *  @param feature_json     The cluster feature (GeoJSON Feature) returned by a
 *                          rendered-features query.
 *  @param extension        Extension name — "supercluster" for cluster sources.
 *  @param extension_field  "children", "leaves", or "expansion-zoom".
 *  @param args_json        Optional JSON object of arguments (e.g.
 *                          {"limit":10,"offset":0} for "leaves"), or NULL. */
MLN_CABI_API char*           mbgl_map_query_feature_extensions(mbgl_map_t* map,
                                                                 const char* source_id,
                                                                 const char* feature_json,
                                                                 const char* extension,
                                                                 const char* extension_field,
                                                                 const char* args_json) MLN_CABI_NOEXCEPT;
/** Free a string returned by any mbgl query function. */
MLN_CABI_API void            mbgl_free_string(char* str) MLN_CABI_NOEXCEPT;

/* ── Style ─────────────────────────────────────────────────────────────────── */
MLN_CABI_API mbgl_style_t*   mbgl_map_get_style(mbgl_map_t* map) MLN_CABI_NOEXCEPT;

/* ── Sources ───────────────────────────────────────────────────────────────── */
MLN_CABI_API mbgl_source_t*  mbgl_style_add_geojson_source(mbgl_style_t* st, const char* source_id) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_source_t*  mbgl_style_add_geojson_source_url(mbgl_style_t* st, const char* source_id, const char* url) MLN_CABI_NOEXCEPT;
/** Add a GeoJSON source with style-spec options (clustering etc.).
 *  @param options_json  JSON object of GeoJSON source options — the style-spec
 *                       keys minus "type"/"data": "cluster", "clusterRadius",
 *                       "clusterMaxZoom", "clusterMinPoints", "clusterProperties",
 *                       "maxzoom", "buffer", "tolerance", "lineMetrics".
 *                       Pass NULL or "{}" for defaults.
 *  Set data afterwards with mbgl_geojson_source_set_data / _set_url. */
MLN_CABI_API mbgl_source_t*  mbgl_style_add_geojson_source_options(mbgl_style_t* st,
                                                                     const char* source_id,
                                                                     const char* options_json) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_geojson_source_set_data(mbgl_source_t* src, const char* geojson) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_geojson_source_set_url(mbgl_source_t* src, const char* url) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_source_t*  mbgl_style_add_vector_source(mbgl_style_t* st, const char* source_id, const char* url) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_source_t*  mbgl_style_add_raster_source(mbgl_style_t* st, const char* source_id, const char* url, int tile_size) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_source_t*  mbgl_style_add_rasterdem_source(mbgl_style_t* st, const char* source_id, const char* url, int tile_size) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_source_t*  mbgl_style_add_image_source(mbgl_style_t* st, const char* source_id, const char* url,
                                                           double lat0, double lon0, double lat1, double lon1,
                                                           double lat2, double lon2, double lat3, double lon3) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_style_remove_source(mbgl_style_t* st, const char* source_id) MLN_CABI_NOEXCEPT;
MLN_CABI_API int             mbgl_style_has_source(mbgl_style_t* st, const char* source_id) MLN_CABI_NOEXCEPT;

/* ── Layers ────────────────────────────────────────────────────────────────── */
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_fill_layer(mbgl_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_line_layer(mbgl_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_circle_layer(mbgl_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_symbol_layer(mbgl_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_raster_layer(mbgl_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_heatmap_layer(mbgl_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_hillshade_layer(mbgl_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_fill_extrusion_layer(mbgl_style_t* st, const char* id, const char* src, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_background_layer(mbgl_style_t* st, const char* id, const char* before) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_style_remove_layer(mbgl_style_t* st, const char* layer_id) MLN_CABI_NOEXCEPT;
MLN_CABI_API int             mbgl_style_has_layer(mbgl_style_t* st, const char* layer_id) MLN_CABI_NOEXCEPT;

MLN_CABI_API mbgl_status_t   mbgl_layer_set_source_layer(mbgl_layer_t* layer, const char* source_layer) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_layer_set_filter(mbgl_layer_t* layer, const char* filter_json) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_layer_set_min_zoom(mbgl_layer_t* layer, float zoom) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_layer_set_max_zoom(mbgl_layer_t* layer, float zoom) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_layer_set_visibility(mbgl_layer_t* layer, int visible) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_layer_set_paint_property(mbgl_layer_t* layer, const char* name, const char* value_json) MLN_CABI_NOEXCEPT;
MLN_CABI_API mbgl_status_t   mbgl_layer_set_layout_property(mbgl_layer_t* layer, const char* name, const char* value_json) MLN_CABI_NOEXCEPT;

/* ── Viewport bounds ───────────────────────────────────────────────────────── */
/** Returns the lat/lng bounds of the current camera viewport.
 *  out_lat_sw / out_lon_sw = south-west corner; out_lat_ne / out_lon_ne = north-east. */
MLN_CABI_API mbgl_status_t   mbgl_map_latlng_bounds_for_camera(mbgl_map_t* map,
                                                                  double* out_lat_sw, double* out_lon_sw,
                                                                  double* out_lat_ne, double* out_lon_ne) MLN_CABI_NOEXCEPT;

/* ── Memory / debug ─────────────────────────────────────────────────────────── */
/** Ask the renderer to free cached resources to reduce memory pressure. */
MLN_CABI_API mbgl_status_t   mbgl_map_reduce_memory_use(mbgl_map_t* map) MLN_CABI_NOEXCEPT;
/** Dump renderer debug information to the log. */
MLN_CABI_API mbgl_status_t   mbgl_map_dump_debug_logs(mbgl_map_t* map) MLN_CABI_NOEXCEPT;

/* ── Feature state ──────────────────────────────────────────────────────────── */
/** Set per-feature state as a JSON object (e.g. {"hover":true}).
 *  @param source_layer_id  Pass NULL or "" for non-vector sources. */
MLN_CABI_API mbgl_status_t   mbgl_map_set_feature_state(mbgl_map_t* map,
                                                          const char* source_id,
                                                          const char* source_layer_id,
                                                          const char* feature_id,
                                                          const char* state_json) MLN_CABI_NOEXCEPT;
/** Get per-feature state as a JSON object string; caller must free with mbgl_free_string().
 *  Returns NULL on error or if no state is set. */
MLN_CABI_API char*           mbgl_map_get_feature_state(mbgl_map_t* map,
                                                          const char* source_id,
                                                          const char* source_layer_id,
                                                          const char* feature_id) MLN_CABI_NOEXCEPT;
/** Remove feature state.  Pass NULL/empty feature_id to clear all features in a source;
 *  pass NULL/empty state_key to clear all state keys for a feature. */
MLN_CABI_API mbgl_status_t   mbgl_map_remove_feature_state(mbgl_map_t* map,
                                                             const char* source_id,
                                                             const char* source_layer_id,
                                                             const char* feature_id,
                                                             const char* state_key) MLN_CABI_NOEXCEPT;

/* ── Style – generic JSON add ───────────────────────────────────────────────── */
/** Add a source from a MapLibre source spec JSON object (without the "id" key).
 *  @param source_id  The unique identifier to assign to this source. */
MLN_CABI_API mbgl_status_t   mbgl_style_add_source_json(mbgl_style_t* st,
                                                          const char* source_id,
                                                          const char* source_json) MLN_CABI_NOEXCEPT;
/** Add a layer from a complete MapLibre layer spec JSON (must include "id" and "type").
 *  @param before_id  Insert before this layer ID, or NULL to append.
 *  Returns a non-owning layer handle, or NULL on error. */
MLN_CABI_API mbgl_layer_t*   mbgl_style_add_layer_json(mbgl_style_t* st,
                                                         const char* layer_json,
                                                         const char* before_id) MLN_CABI_NOEXCEPT;

/* ── Offline regions + ambient cache ─────────────────────────────────────────
 *
 * Wraps mbgl::DatabaseFileSource. The manager shares the map's cache database
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
 * mbgl_offline_region_get_metadata (not embedded in the JSON).
 */
typedef struct mbgl_offline_manager_s mbgl_offline_manager_t;

/** Special `reason` value passed to mbgl_offline_region_error_fn when the
 *  Mapbox tile count limit is exceeded (not an HTTP error). */
#define MBGL_OFFLINE_TILE_COUNT_LIMIT 100

/** One-shot completion callback for operations with no payload. */
typedef void (*mbgl_offline_done_fn)(mbgl_status_t status,
                                     const char*   error_message,
                                     void*         userdata);
/** One-shot callback delivering a JSON array of regions (see format above).
 *  regions_json is NULL when status != MBGL_OK. */
typedef void (*mbgl_offline_regions_fn)(mbgl_status_t status,
                                        const char*   error_message,
                                        const char*   regions_json,
                                        void*         userdata);
/** One-shot callback delivering a region status JSON object:
 *  {"downloadState":0|1,"completedResourceCount":N,"completedResourceSize":N,
 *   "completedTileCount":N,"requiredTileCount":N,"completedTileSize":N,
 *   "requiredResourceCount":N,"requiredResourceCountIsPrecise":bool,
 *   "complete":bool} */
typedef void (*mbgl_offline_status_fn)(mbgl_status_t status,
                                       const char*   error_message,
                                       const char*   status_json,
                                       void*         userdata);
/** Recurring download-progress callback (installed per region).
 *  @param download_state       0=Inactive 1=Active.
 *  @param required_is_precise  Non-zero once the required count is exact.
 *  @param complete             Non-zero when the region is fully downloaded. */
typedef void (*mbgl_offline_progress_fn)(int64_t  region_id,
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
 *  @param reason  mbgl::Response::Error::Reason value (matches mbgl_http_error_t),
 *                 or MBGL_OFFLINE_TILE_COUNT_LIMIT (100) when the Mapbox tile
 *                 count limit was reached. */
typedef void (*mbgl_offline_region_error_fn)(int64_t     region_id,
                                             int         reason,
                                             const char* message,
                                             void*       userdata);

/** Create an offline manager for the given cache database.  Use the same
 *  cache_path / asset_path / api_key / max_cache_size_bytes as the map so the
 *  underlying DatabaseFileSource instance is shared. Pass 0/NULL for defaults. */
MLN_CABI_API mbgl_offline_manager_t* mbgl_offline_manager_create(
    const char* cache_path,
    const char* asset_path,
    const char* api_key,
    uint64_t    max_cache_size_bytes) MLN_CABI_NOEXCEPT;
/** Destroy the manager. Pending operation callbacks may still fire afterwards
 *  (the internal state is kept alive until they complete). */
MLN_CABI_API mbgl_status_t mbgl_offline_manager_destroy(mbgl_offline_manager_t* m) MLN_CABI_NOEXCEPT;

/** List all offline regions in the database. */
MLN_CABI_API mbgl_status_t mbgl_offline_list_regions(mbgl_offline_manager_t* m,
                                                     mbgl_offline_regions_fn cb,
                                                     void* userdata) MLN_CABI_NOEXCEPT;
/** Create a tile-pyramid offline region (style URL + lat/lng bounds + zoom range).
 *  The new region starts Inactive; call mbgl_offline_set_region_download_state
 *  to begin downloading. The callback receives a one-element region array.
 *  @param metadata  Opaque binary metadata (may be NULL when metadata_len is 0). */
MLN_CABI_API mbgl_status_t mbgl_offline_create_region(mbgl_offline_manager_t* m,
                                                      const char* style_url,
                                                      double lat_sw, double lon_sw,
                                                      double lat_ne, double lon_ne,
                                                      double min_zoom, double max_zoom,
                                                      float  pixel_ratio,
                                                      int    include_ideographs,
                                                      const uint8_t* metadata, int metadata_len,
                                                      mbgl_offline_regions_fn cb,
                                                      void* userdata) MLN_CABI_NOEXCEPT;
/** Create an offline region for an arbitrary GeoJSON geometry (a Geometry,
 *  Feature, or single-feature FeatureCollection). */
MLN_CABI_API mbgl_status_t mbgl_offline_create_region_geometry(mbgl_offline_manager_t* m,
                                                               const char* style_url,
                                                               const char* geometry_geojson,
                                                               double min_zoom, double max_zoom,
                                                               float  pixel_ratio,
                                                               int    include_ideographs,
                                                               const uint8_t* metadata, int metadata_len,
                                                               mbgl_offline_regions_fn cb,
                                                               void* userdata) MLN_CABI_NOEXCEPT;
/** Delete a region and evict its resources (slow if auto-packing is enabled). */
MLN_CABI_API mbgl_status_t mbgl_offline_delete_region(mbgl_offline_manager_t* m,
                                                      int64_t region_id,
                                                      mbgl_offline_done_fn cb,
                                                      void* userdata) MLN_CABI_NOEXCEPT;
/** Force revalidation of all the region's tiles with the server. */
MLN_CABI_API mbgl_status_t mbgl_offline_invalidate_region(mbgl_offline_manager_t* m,
                                                          int64_t region_id,
                                                          mbgl_offline_done_fn cb,
                                                          void* userdata) MLN_CABI_NOEXCEPT;
/** Pause (active=0) or start/resume (active=1) downloading a region's resources. */
MLN_CABI_API mbgl_status_t mbgl_offline_set_region_download_state(mbgl_offline_manager_t* m,
                                                                  int64_t region_id,
                                                                  int active) MLN_CABI_NOEXCEPT;
/** Install (or replace) the progress/error observer for a region.
 *  Pass NULL for both callbacks to remove the observer. */
MLN_CABI_API mbgl_status_t mbgl_offline_set_region_observer(mbgl_offline_manager_t* m,
                                                            int64_t region_id,
                                                            mbgl_offline_progress_fn progress,
                                                            mbgl_offline_region_error_fn error,
                                                            void* userdata) MLN_CABI_NOEXCEPT;
/** Query the current status of a region. */
MLN_CABI_API mbgl_status_t mbgl_offline_get_region_status(mbgl_offline_manager_t* m,
                                                          int64_t region_id,
                                                          mbgl_offline_status_fn cb,
                                                          void* userdata) MLN_CABI_NOEXCEPT;
/** Replace a region's opaque binary metadata. */
MLN_CABI_API mbgl_status_t mbgl_offline_update_region_metadata(mbgl_offline_manager_t* m,
                                                               int64_t region_id,
                                                               const uint8_t* metadata, int metadata_len,
                                                               mbgl_offline_done_fn cb,
                                                               void* userdata) MLN_CABI_NOEXCEPT;
/** Read a region's metadata from the manager's cache (synchronous — the region
 *  must have been returned by a previous list/create/merge on this manager).
 *  Returns a buffer to free with mbgl_free_string(), or NULL if the region is
 *  unknown or has no metadata; *out_len receives the byte length. */
MLN_CABI_API char* mbgl_offline_region_get_metadata(mbgl_offline_manager_t* m,
                                                    int64_t region_id,
                                                    int* out_len) MLN_CABI_NOEXCEPT;
/** Merge regions from a secondary database file into this one.
 *  The side database may be upgraded in place (needs write access). */
MLN_CABI_API mbgl_status_t mbgl_offline_merge_database(mbgl_offline_manager_t* m,
                                                       const char* side_db_path,
                                                       mbgl_offline_regions_fn cb,
                                                       void* userdata) MLN_CABI_NOEXCEPT;
/** Set the Mapbox-tile count limit for offline regions (does not affect
 *  non-Mapbox tile sources). */
MLN_CABI_API mbgl_status_t mbgl_offline_set_tile_count_limit(mbgl_offline_manager_t* m,
                                                             uint64_t limit) MLN_CABI_NOEXCEPT;

/* ── Ambient cache / database maintenance ──────────────────────────────────── */
/** Cap the ambient (non-region) cache size in bytes. Call before heavy use. */
MLN_CABI_API mbgl_status_t mbgl_offline_set_maximum_ambient_cache_size(mbgl_offline_manager_t* m,
                                                                       uint64_t bytes,
                                                                       mbgl_offline_done_fn cb,
                                                                       void* userdata) MLN_CABI_NOEXCEPT;
/** Erase the ambient cache (offline regions are not affected). */
MLN_CABI_API mbgl_status_t mbgl_offline_clear_ambient_cache(mbgl_offline_manager_t* m,
                                                            mbgl_offline_done_fn cb,
                                                            void* userdata) MLN_CABI_NOEXCEPT;
/** Force revalidation of ambient-cache resources with the server. */
MLN_CABI_API mbgl_status_t mbgl_offline_invalidate_ambient_cache(mbgl_offline_manager_t* m,
                                                                 mbgl_offline_done_fn cb,
                                                                 void* userdata) MLN_CABI_NOEXCEPT;
/** Vacuum the database file to reclaim disk space. */
MLN_CABI_API mbgl_status_t mbgl_offline_pack_database(mbgl_offline_manager_t* m,
                                                      mbgl_offline_done_fn cb,
                                                      void* userdata) MLN_CABI_NOEXCEPT;
/** Delete and re-initialise the database (regions AND ambient cache). */
MLN_CABI_API mbgl_status_t mbgl_offline_reset_database(mbgl_offline_manager_t* m,
                                                       mbgl_offline_done_fn cb,
                                                       void* userdata) MLN_CABI_NOEXCEPT;
/** Enable/disable automatic packing after region deletion / cache clears
 *  (enabled by default). */
MLN_CABI_API mbgl_status_t mbgl_offline_set_pack_database_automatically(mbgl_offline_manager_t* m,
                                                                        int enabled) MLN_CABI_NOEXCEPT;

/* ── Version ───────────────────────────────────────────────────────────────── */
MLN_CABI_API const char*     mln_cabi_version(void) MLN_CABI_NOEXCEPT;

/* ── Android helpers (only compiled on Android) ────────────────────────────── */
#ifdef __ANDROID__
/** Acquire an ANativeWindow from a Java android.view.Surface.
 *  Caller must call mbgl_android_release_window when done. */
MLN_CABI_API void*  mbgl_android_acquire_window(void* jni_env, void* surface_jobject) MLN_CABI_NOEXCEPT;
/** Release an ANativeWindow obtained via mbgl_android_acquire_window. */
MLN_CABI_API void   mbgl_android_release_window(void* window) MLN_CABI_NOEXCEPT;

/* ── Android HTTP provider ─────────────────────────────────────────────────── */
/**
 * Callback type for the HTTP provider.  Called by the native layer when it
 * needs to fetch a URL.  The host (C#) must call mbgl_http_respond() with the
 * same request_id when the fetch completes or fails.
 *
 * @param request_id  Unique ID for this request.  Pass back to mbgl_http_respond().
 * @param url         The URL to fetch (null-terminated UTF-8).
 * @param etag        Previous ETag for a conditional GET, or NULL.
 * @param modified    Previous Last-Modified for a conditional GET, or NULL.
 * @param range_start First byte of a Range request, or -1 for a full fetch.
 * @param range_end   Last byte (inclusive) of a Range request, or -1 for a full fetch.
 *                    When both are >= 0 send "Range: bytes=range_start-range_end".
 *                    HTTP 206 Partial Content responses are treated as success.
 * @param userdata    Opaque pointer supplied to mbgl_set_http_provider().
 */
typedef void (*mbgl_http_provider_fn)(
    uint64_t    request_id,
    const char* url,
    const char* etag,
    const char* modified,
    int64_t     range_start,
    int64_t     range_end,
    void*       userdata);

/**
 * Error codes passed to mbgl_http_respond().
 * Values are intentionally aligned with mbgl::Response::Error::Reason.
 */
typedef enum mbgl_http_error_t {
    MBGL_HTTP_ERROR_NONE       = 0, /**< Success — no error. */
    MBGL_HTTP_ERROR_NOT_FOUND  = 2, /**< HTTP 404. */
    MBGL_HTTP_ERROR_SERVER     = 3, /**< HTTP 5xx. */
    MBGL_HTTP_ERROR_CONNECTION = 4, /**< Network or connection failure. */
    MBGL_HTTP_ERROR_RATE_LIMIT = 5, /**< HTTP 429. */
    MBGL_HTTP_ERROR_OTHER      = 6, /**< Any other error. */
} mbgl_http_error_t;

/**
 * Register the HTTP provider callback.  Must be called before the first map is
 * created.  Pass NULL to remove the current provider (requests will fail with a
 * Connection error until a new provider is registered).
 */
MLN_CABI_API void mbgl_set_http_provider(mbgl_http_provider_fn fn,
                                           void*                 userdata) MLN_CABI_NOEXCEPT;

/**
 * Callback invoked when the native layer no longer needs a previously-started
 * request (mbgl superseded or dropped the tile, e.g. while zooming/panning).
 * The host must abort the corresponding in-flight fetch so its connection is
 * freed for requests that are still needed — without this, superseded requests
 * run to completion and can starve the tiles at the current zoom.
 *
 * @param request_id  The ID that was supplied to mbgl_http_provider_fn.
 * @param userdata    Opaque pointer supplied to mbgl_set_http_cancel_provider().
 */
typedef void (*mbgl_http_cancel_fn)(uint64_t request_id, void* userdata);

/**
 * Register the request-cancellation callback (optional but strongly
 * recommended). Must be called before the first map is created. Pass NULL to
 * remove it.
 */
MLN_CABI_API void mbgl_set_http_cancel_provider(mbgl_http_cancel_fn fn,
                                                void*               userdata) MLN_CABI_NOEXCEPT;

/**
 * Deliver a completed HTTP response back to the native layer.
 * Must be called exactly once per request unless mbgl_http_cancel() was already
 * called for the same request_id.  Safe to call from any thread.
 *
 * @param request_id       The ID supplied to mbgl_http_provider_fn.
 * @param error            MBGL_HTTP_ERROR_NONE on success, otherwise an error code.
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
MLN_CABI_API void mbgl_http_respond(
    uint64_t          request_id,
    mbgl_http_error_t error,
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
 * mbgl_http_respond() with the same request_id is silently ignored.
 * The host C# should also abort the in-flight HttpClient request.
 */
MLN_CABI_API void mbgl_http_cancel(uint64_t request_id) MLN_CABI_NOEXCEPT;
#endif

#ifdef __cplusplus
} // extern "C"
#endif
