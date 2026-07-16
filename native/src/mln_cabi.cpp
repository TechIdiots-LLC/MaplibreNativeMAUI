/**
 * mln_cabi.cpp — Implementation of the flat C ABI wrapper.
 *
 * Compiles as a plain C++ shared library (no C++/CLI, no JNI, no ObjC).
 * The platform-specific frontend (rendering surface binding) is handled by
 * PlatformFrontend, which has per-platform .cpp files included via CMake.
 */

#include "mln_cabi.h"
#include "mln_cabi_internal.hpp"

#include <mbgl/map/map.hpp>
#include <mbgl/map/map_options.hpp>
#include <mbgl/map/camera.hpp>
#include <mbgl/util/run_loop.hpp>
#include <mbgl/storage/resource_options.hpp>
#include <mbgl/style/style.hpp>
#include <mbgl/style/sources/geojson_source.hpp>
#include <mbgl/style/sources/vector_source.hpp>
#include <mbgl/style/sources/raster_source.hpp>
#include <mbgl/style/sources/raster_dem_source.hpp>
#include <mbgl/style/sources/image_source.hpp>
#include <mbgl/style/layers/fill_layer.hpp>
#include <mbgl/style/layers/line_layer.hpp>
#include <mbgl/style/layers/circle_layer.hpp>
#include <mbgl/style/layers/symbol_layer.hpp>
#include <mbgl/style/layers/raster_layer.hpp>
#include <mbgl/style/layers/heatmap_layer.hpp>
#include <mbgl/style/layers/hillshade_layer.hpp>
#include <mbgl/style/layers/fill_extrusion_layer.hpp>
#include <mbgl/style/layers/background_layer.hpp>
#include <mbgl/style/layers/location_indicator_layer.hpp>
#include <mbgl/style/layers/color_relief_layer.hpp>
#include <mbgl/style/conversion/geojson.hpp>
#include <mbgl/style/conversion/geojson_options.hpp>
#include <mbgl/style/conversion/filter.hpp>
#include <mbgl/style/conversion/source.hpp>
#include <mbgl/style/conversion/layer.hpp>
#include <mbgl/storage/network_status.hpp>
#include <mbgl/util/rapidjson.hpp>
#include <mbgl/style/rapidjson_conversion.hpp>
#include <mbgl/map/map_observer.hpp>
#include <mbgl/style/image.hpp>
#include <mbgl/style/transition_options.hpp>
#include <mbgl/style/light.hpp>
#include <mbgl/util/image.hpp>
#include <mbgl/renderer/renderer.hpp>
#include <mbgl/util/geojson.hpp>
#include <mbgl/util/logging.hpp>

#include <mbgl/map/bound_options.hpp>
#include <mbgl/style/conversion/stringify.hpp>
#include <rapidjson/writer.h>
#include <rapidjson/stringbuffer.h>

#include <memory>
#include <string>
#include <stdexcept>
#include <cmath>
#include <sstream>
#include <limits>
#include <atomic>
#include <mutex>

// Platform frontend is provided separately per platform.
#include "platform_frontend.hpp"

/// Factory function defined in platform_frontend_<platform>.cpp/.mm
extern PlatformFrontend* createPlatformFrontend(
    void*          surface_handle,
    void*          gl_context,
    mbgl::Size     size,
    float          pixel_ratio,
    mbgl_render_fn render_callback,
    void*          render_userdata);

/* ─── Thread-local error string ─────────────────────────────────────────────── */
static thread_local std::string s_last_error;

static mbgl_status_t set_error(mbgl_status_t code, std::string msg) noexcept {
    s_last_error = std::move(msg);
    return code;
}

static mbgl_status_t set_native_error(const std::exception& e) noexcept {
    s_last_error = e.what();
    return MBGL_NATIVE_ERROR;
}

const char* mbgl_get_last_error() noexcept {
    return s_last_error.c_str();
}

/* Bridges declared in mln_cabi_internal.hpp for sibling translation units
 * (mln_cabi_offline.cpp) — the helpers above stay static/TU-local. */
mbgl_status_t cabi_set_error(mbgl_status_t code, std::string msg) noexcept {
    return set_error(code, std::move(msg));
}
mbgl_status_t cabi_set_native_error(const std::exception& e) noexcept {
    return set_native_error(e);
}

/* ─── Log callback state ─────────────────────────────────────────────────────── */
static std::mutex       s_log_mutex;
static mbgl_log_fn      s_log_fn       = nullptr;
static void*            s_log_userdata = nullptr;

/** Custom mbgl::Log observer that forwards records to the C callback. */
class CabiLogObserver : public mbgl::Log::Observer {
public:
    bool onRecord(mbgl::EventSeverity severity,
                  mbgl::Event         event,
                  int64_t             /*code*/,
                  const std::string&  msg) override {
        std::lock_guard<std::mutex> lock(s_log_mutex);
        if (!s_log_fn) return false;

        mbgl_log_level_t level;
        switch (severity) {
            case mbgl::EventSeverity::Debug:   level = MBGL_LOG_DEBUG;   break;
            case mbgl::EventSeverity::Info:    level = MBGL_LOG_INFO;    break;
            case mbgl::EventSeverity::Warning: level = MBGL_LOG_WARNING; break;
            case mbgl::EventSeverity::Error:   level = MBGL_LOG_ERROR;   break;
            default:                           level = MBGL_LOG_INFO;    break;
        }

        const char* category = mbgl::Enum<mbgl::Event>::toString(event);
        int consumed = s_log_fn(level, category ? category : "", msg.c_str(), s_log_userdata);
        return consumed != 0;
    }
};

static CabiLogObserver* s_log_observer = nullptr;

mbgl_status_t mbgl_install_log_callback(mbgl_log_fn fn, void* userdata) noexcept {
    try {
        std::lock_guard<std::mutex> lock(s_log_mutex);
        s_log_fn       = fn;
        s_log_userdata = userdata;
        if (fn && !s_log_observer) {
            s_log_observer = new CabiLogObserver();
            mbgl::Log::setObserver(std::unique_ptr<mbgl::Log::Observer>(s_log_observer));
        } else if (!fn) {
            mbgl::Log::removeObserver();
            s_log_observer = nullptr;
        }
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Network status ────────────────────────────────────────────────────────── */

mbgl_status_t mbgl_network_status_set(int online) noexcept {
    try {
        mbgl::NetworkStatus::Set(online ? mbgl::NetworkStatus::Status::Online
                                        : mbgl::NetworkStatus::Status::Offline);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

int mbgl_network_status_get() noexcept {
    return mbgl::NetworkStatus::Get() == mbgl::NetworkStatus::Status::Online ? 1 : 0;
}

/* ─── Internal structs ──────────────────────────────────────────────────────── */
/** Bridges all MapObserver virtual calls to the C mbgl_map_observer_fn. */
class CabiMapObserver : public mbgl::MapObserver {
public:
    mbgl_map_observer_fn fn = nullptr;
    void*                ud = nullptr;

    void fire(const char* name, const char* detail = nullptr) const {
        if (fn) fn(name, detail, ud);
    }

    void onCameraWillChange(CameraChangeMode mode) override {
        fire("onCameraWillChange", mode == CameraChangeMode::Animated ? "animated" : "immediate");
    }
    void onCameraIsChanging() override { fire("onCameraIsChanging"); }
    void onCameraDidChange(CameraChangeMode mode) override {
        fire("onCameraDidChange", mode == CameraChangeMode::Animated ? "animated" : "immediate");
    }
    void onWillStartLoadingMap()  override { fire("onWillStartLoadingMap"); }
    void onDidFinishLoadingMap()  override { fire("onDidFinishLoadingMap"); }
    void onDidFailLoadingMap(mbgl::MapLoadError /*err*/, const std::string& msg) override {
        fire("onDidFailLoadingMap", msg.c_str());
    }
    void onWillStartRenderingFrame() override { fire("onWillStartRenderingFrame"); }
    void onDidFinishRenderingFrame(const RenderFrameStatus& s) override {
        // Encode needsRepaint and placementChanged as separate event names so the
        // C# side can branch without an extra detail string parse.
        if (s.needsRepaint && s.placementChanged)
            fire("onDidFinishRenderingFrameNeedsRepaintPlacementChanged");
        else if (s.needsRepaint)
            fire("onDidFinishRenderingFrameNeedsRepaint");
        else if (s.placementChanged)
            fire("onDidFinishRenderingFramePlacementChanged");
        else
            fire("onDidFinishRenderingFrame");
    }
    void onWillStartRenderingMap() override { fire("onWillStartRenderingMap"); }
    void onDidFinishRenderingMap(RenderMode) override { fire("onDidFinishRenderingMap"); }
    void onDidFinishLoadingStyle() override { fire("onDidFinishLoadingStyle"); }
    void onRenderError(std::exception_ptr ep) override {
        try {
            if (ep) std::rethrow_exception(ep);
        } catch (const std::exception& e) {
            fire("onRenderError", e.what());
        } catch (...) {
            fire("onRenderError", "unknown render error");
        }
    }
    void onSourceChanged(mbgl::style::Source& src) override {
        fire("onSourceChanged", src.getID().c_str());
    }
    void onDidBecomeIdle() override { fire("onDidBecomeIdle"); }
    void onStyleImageMissing(const std::string& id) override {
        fire("onStyleImageMissing", id.c_str());
    }
};

/* The public handle types are forward-declared in the header as
 * "struct mbgl_X_s".  Here we define them as type aliases so that the
 * internal CabiXxx types satisfy the ABI: we declare them as the same
 * struct by giving each concrete internal struct two names via typedef.
 * The simpler approach is just to reinterpret_cast at every boundary. */

struct CabiRunLoop {
    mbgl::util::RunLoop loop;
};
struct CabiMap {
    // Destruction order matters: map must die before frontend and observer.
    // unique_ptrs are destroyed in reverse declaration order, so declare
    // observer first so it is destroyed last.
    std::unique_ptr<CabiMapObserver>      observer;
    std::unique_ptr<PlatformFrontend>     frontend;
    std::unique_ptr<mbgl::Map>            map;
};

/* ─── Casting helpers ───────────────────────────────────────────────────────── */
/* The public handle types are opaque pointers to forward-declared structs.
 * We never define those structs; instead we reinterpret the pointer to/from
 * our internal types. */
template<typename T, typename H> static inline T* as(H* h) noexcept { return reinterpret_cast<T*>(h); }
template<typename H, typename T> static inline H* to(T* t) noexcept { return reinterpret_cast<H*>(t); }

/* Convenience aliases */
static inline CabiRunLoop*        rl_ptr(mbgl_runloop_t*  h) noexcept { return as<CabiRunLoop>(h); }
static inline PlatformFrontend*   fe_ptr(mbgl_frontend_t* h) noexcept { return as<PlatformFrontend>(h); }
static inline CabiMap*            map_ptr(mbgl_map_t*     h) noexcept { return as<CabiMap>(h); }
static inline mbgl::style::Style& style_ref(mbgl_style_t* h) noexcept { return *as<mbgl::style::Style>(h); }
static inline mbgl::style::Layer& layer_ref(mbgl_layer_t* h) noexcept { return *as<mbgl::style::Layer>(h); }
static inline mbgl::style::Source& source_ref(mbgl_source_t* h) noexcept { return *as<mbgl::style::Source>(h); }

/* Helper: safe C-string copy */
static inline std::string safe_str(const char* s) {
    return s ? std::string(s) : std::string{};
}

/* ─── RunLoop ───────────────────────────────────────────────────────────────── */

mbgl_runloop_t* mbgl_runloop_create() noexcept {
    try {
        return to<mbgl_runloop_t>(new CabiRunLoop{});
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_status_t mbgl_runloop_destroy(mbgl_runloop_t* rl) noexcept {
    if (!rl) return set_error(MBGL_INVALID_ARG, "mbgl_runloop_destroy: null handle");
    try { delete rl_ptr(rl); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_runloop_run_once(mbgl_runloop_t* rl) noexcept {
    if (!rl) return set_error(MBGL_INVALID_ARG, "mbgl_runloop_run_once: null handle");
    try { rl_ptr(rl)->loop.runOnce(); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Frontend ──────────────────────────────────────────────────────────────── */

const char* mbgl_get_render_backend() noexcept {
#if defined(MLN_RENDER_BACKEND_VULKAN)
    return "vulkan";
#elif defined(MLN_RENDER_BACKEND_METAL)
    return "metal";
#else
    return "opengl";
#endif
}

mbgl_frontend_t* mbgl_frontend_create(
    void*  surface_handle,
    void*  gl_context,
    int    width_px,
    int    height_px,
    float  pixel_ratio,
    mbgl_render_fn render_callback,
    void*  render_userdata) noexcept
{
    try {
        return to<mbgl_frontend_t>(createPlatformFrontend(
            surface_handle, gl_context,
            mbgl::Size{ static_cast<uint32_t>(width_px), static_cast<uint32_t>(height_px) },
            pixel_ratio,
            render_callback, render_userdata));
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_frontend_t* mbgl_frontend_create_gl(
    void*  surface_handle,
    void*  gl_context,
    int    width_px,
    int    height_px,
    float  pixel_ratio,
    mbgl_render_fn render_callback,
    void*  render_userdata) noexcept
{
    return mbgl_frontend_create(surface_handle, gl_context, width_px, height_px,
                                pixel_ratio, render_callback, render_userdata);
}

mbgl_status_t mbgl_frontend_destroy(mbgl_frontend_t* fe) noexcept {
    if (!fe) return set_error(MBGL_INVALID_ARG, "mbgl_frontend_destroy: null handle");
    try { delete fe_ptr(fe); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_frontend_render(mbgl_frontend_t* fe) noexcept {
    if (!fe) return set_error(MBGL_INVALID_ARG, "mbgl_frontend_render: null handle");
    try { fe_ptr(fe)->render(); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_frontend_set_size(mbgl_frontend_t* fe, int width_px, int height_px) noexcept {
    if (!fe) return set_error(MBGL_INVALID_ARG, "mbgl_frontend_set_size: null handle");
    try {
        fe_ptr(fe)->setSize(mbgl::Size{ static_cast<uint32_t>(width_px), static_cast<uint32_t>(height_px) });
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

void* mbgl_frontend_get_native_view(mbgl_frontend_t* fe) noexcept {
    if (!fe) return nullptr;
    return fe_ptr(fe)->getNativeView();
}

mbgl_status_t mbgl_frontend_read_pixels(mbgl_frontend_t* fe, uint8_t* out_buf, size_t buf_len) noexcept {
    if (!fe || !out_buf) return set_error(MBGL_INVALID_ARG, "mbgl_frontend_read_pixels: null arg");
    try {
        return fe_ptr(fe)->readPixels(out_buf, buf_len)
            ? MBGL_OK
            : set_error(MBGL_UNSUPPORTED, "mbgl_frontend_read_pixels: frontend has no CPU read-back");
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Map ───────────────────────────────────────────────────────────────────── */

static mbgl_map_t* map_create_impl(
    mbgl_frontend_t*  fe,
    const char*       cache_path,
    const char*       asset_path,
    const char*       api_key,
    uint64_t          max_cache_size_bytes,
    float             pixel_ratio,
    mbgl_map_observer_fn observer,
    void*             observer_userdata) noexcept
{
    if (!fe) { set_error(MBGL_INVALID_ARG, "mbgl_map_create: null frontend"); return nullptr; }
    try {
        auto* cabi_fe  = fe_ptr(fe);
        auto* cabi_map = new CabiMap{};
        cabi_map->frontend  = std::unique_ptr<PlatformFrontend>(cabi_fe);
        cabi_map->observer  = std::make_unique<CabiMapObserver>();
        cabi_map->observer->fn = observer;
        cabi_map->observer->ud = observer_userdata;

        mbgl::ResourceOptions resOpts;
        if (cache_path) resOpts.withCachePath(cache_path);
        if (asset_path) resOpts.withAssetPath(asset_path);
        if (api_key && *api_key)     resOpts.withApiKey(api_key);
        if (max_cache_size_bytes)    resOpts.withMaximumCacheSize(max_cache_size_bytes);

        mbgl::MapOptions mapOpts;
        mapOpts.withMapMode(mbgl::MapMode::Continuous)
               .withConstrainMode(mbgl::ConstrainMode::HeightOnly)
               .withViewportMode(mbgl::ViewportMode::Default)
               .withSize(cabi_fe->getSize())
               .withPixelRatio(pixel_ratio);

        cabi_map->map = std::make_unique<mbgl::Map>(
            *cabi_fe,
            *cabi_map->observer,
            mapOpts,
            resOpts);

        return to<mbgl_map_t>(cabi_map);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_map_t* mbgl_map_create(
    mbgl_frontend_t*  fe,
    mbgl_runloop_t*   /*rl*/,
    const char*       cache_path,
    const char*       asset_path,
    float             pixel_ratio,
    mbgl_map_observer_fn observer,
    void*             observer_userdata) noexcept
{
    return map_create_impl(fe, cache_path, asset_path, nullptr, 0,
                           pixel_ratio, observer, observer_userdata);
}

mbgl_map_t* mbgl_map_create2(
    mbgl_frontend_t*  fe,
    mbgl_runloop_t*   /*rl*/,
    const char*       cache_path,
    const char*       asset_path,
    const char*       api_key,
    uint64_t          max_cache_size_bytes,
    float             pixel_ratio,
    mbgl_map_observer_fn observer,
    void*             observer_userdata) noexcept
{
    return map_create_impl(fe, cache_path, asset_path, api_key, max_cache_size_bytes,
                           pixel_ratio, observer, observer_userdata);
}

mbgl_status_t mbgl_map_destroy(mbgl_map_t* map) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_destroy: null handle");
    try {
        auto* m = map_ptr(map);
        m->map.reset();
        m->frontend.reset();
        m->observer.reset();
        delete m;
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_style_url(mbgl_map_t* map, const char* url) noexcept {
    if (!map || !url) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_style_url: null argument");
    try { map_ptr(map)->map->getStyle().loadURL(url); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_style_json(mbgl_map_t* map, const char* json) noexcept {
    if (!map || !json) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_style_json: null argument");
    try { map_ptr(map)->map->getStyle().loadJSON(json); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_size(mbgl_map_t* map, int width_px, int height_px) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_size: null handle");
    try {
        mbgl::Size sz{ static_cast<uint32_t>(width_px), static_cast<uint32_t>(height_px) };
        auto* m = map_ptr(map);
        m->map->setSize(sz);
        m->frontend->setSize(sz);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_jump_to(mbgl_map_t* map, double lat, double lon, double zoom, double bearing, double pitch) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_jump_to: null handle");
    try {
        mbgl::CameraOptions cam;
        cam.center  = mbgl::LatLng{ lat, lon };
        cam.zoom    = zoom;
        cam.bearing = bearing;
        cam.pitch   = pitch;
        map_ptr(map)->map->jumpTo(cam);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_ease_to(mbgl_map_t* map, double lat, double lon, double zoom, double bearing, double pitch, int64_t duration_ms) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_ease_to: null handle");
    try {
        mbgl::CameraOptions cam;
        cam.center  = mbgl::LatLng{ lat, lon };
        cam.zoom    = zoom;
        cam.bearing = bearing;
        cam.pitch   = pitch;
        mbgl::AnimationOptions anim{ mbgl::Duration(std::chrono::milliseconds(duration_ms)) };
        map_ptr(map)->map->easeTo(cam, anim);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

double mbgl_map_get_zoom(mbgl_map_t* map) noexcept {
    if (!map) return 0.0;
    return map_ptr(map)->map->getCameraOptions().zoom.value_or(0.0);
}

double mbgl_map_get_bearing(mbgl_map_t* map) noexcept {
    if (!map) return 0.0;
    return map_ptr(map)->map->getCameraOptions().bearing.value_or(0.0);
}

double mbgl_map_get_pitch(mbgl_map_t* map) noexcept {
    if (!map) return 0.0;
    return map_ptr(map)->map->getCameraOptions().pitch.value_or(0.0);
}

void mbgl_map_get_center(mbgl_map_t* map, double* out_lat, double* out_lon) noexcept {
    if (!map) { if (out_lat) *out_lat = 0.0; if (out_lon) *out_lon = 0.0; return; }
    auto cam = map_ptr(map)->map->getCameraOptions();
    if (cam.center) { if (out_lat) *out_lat = cam.center->latitude(); if (out_lon) *out_lon = cam.center->longitude(); }
    else            { if (out_lat) *out_lat = 0.0; if (out_lon) *out_lon = 0.0; }
}

mbgl_status_t mbgl_map_set_min_zoom(mbgl_map_t* map, double zoom) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_min_zoom: null handle");
    try { map_ptr(map)->map->setBounds(mbgl::BoundOptions{}.withMinZoom(zoom)); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_max_zoom(mbgl_map_t* map, double zoom) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_max_zoom: null handle");
    try { map_ptr(map)->map->setBounds(mbgl::BoundOptions{}.withMaxZoom(zoom)); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_trigger_repaint(mbgl_map_t* map) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_trigger_repaint: null handle");
    try { map_ptr(map)->map->triggerRepaint(); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_cancel_transitions(mbgl_map_t* map) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_cancel_transitions: null handle");
    try { map_ptr(map)->map->cancelTransitions(); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

int mbgl_map_is_fully_loaded(mbgl_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isFullyLoaded() ? 1 : 0;
}

/* ─── Debug overlays ────────────────────────────────────────────────────────── */

int mbgl_map_get_debug_options(mbgl_map_t* map) noexcept {
    if (!map) return 0;
    return static_cast<int>(map_ptr(map)->map->getDebug());
}

mbgl_status_t mbgl_map_set_debug_options(mbgl_map_t* map, int options) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_debug_options: null handle");
    try {
        map_ptr(map)->map->setDebug(static_cast<mbgl::MapDebugOptions>(options));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* Input — MapLibre internal camera manipulation via ScreenCoordinate transform */
mbgl_status_t mbgl_map_on_scroll(mbgl_map_t* map, double delta, double cx, double cy) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_on_scroll: null handle");
    try {
        auto* m = map_ptr(map);
        // delta is already normalized to ±1.0 per scroll tick (mouseWheelDelta/120).
        // Multiply by 0.5 to get ~0.5 zoom levels per tick, matching typical map feel.
        double zoom = m->map->getCameraOptions().zoom.value_or(0.0) + delta * 0.5;
        mbgl::CameraOptions cam;
        cam.zoom   = std::max(0.0, std::min(22.0, zoom));
        cam.anchor = mbgl::ScreenCoordinate{ cx, cy };
        m->map->jumpTo(cam);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_on_double_tap(mbgl_map_t* map, double x, double y) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_on_double_tap: null handle");
    try {
        auto* m = map_ptr(map);
        double zoom = m->map->getCameraOptions().zoom.value_or(0.0) + 1.0;
        mbgl::CameraOptions cam;
        cam.zoom   = zoom;
        cam.anchor = mbgl::ScreenCoordinate{ x, y };
        mbgl::AnimationOptions anim{ mbgl::Duration(std::chrono::milliseconds(300)) };
        m->map->easeTo(cam, anim);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

static thread_local mbgl::CameraOptions s_panStart;
static thread_local mbgl::ScreenCoordinate s_panAnchor;

mbgl_status_t mbgl_map_on_pan_start(mbgl_map_t* map, double x, double y) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_on_pan_start: null handle");
    s_panStart  = map_ptr(map)->map->getCameraOptions();
    s_panAnchor = { x, y };
    return MBGL_OK;
}

mbgl_status_t mbgl_map_on_pan_move(mbgl_map_t* map, double dx, double dy) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_on_pan_move: null handle");
    try { map_ptr(map)->map->moveBy(mbgl::ScreenCoordinate{dx, dy}); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_on_pan_end(mbgl_map_t* /*map*/) noexcept {
    return MBGL_OK;
}

mbgl_status_t mbgl_map_on_pinch(mbgl_map_t* map, double scale_factor, double cx, double cy) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_on_pinch: null handle");
    // scale_factor is the per-frame incremental ratio (e.g. 1.02 or 0.98).
    // Guard against zero/negative to prevent log2 producing -infinity or NaN.
    if (scale_factor <= 0.0) return MBGL_OK;
    try {
        auto* m = map_ptr(map);
        double zoom = m->map->getCameraOptions().zoom.value_or(0.0) + std::log2(scale_factor);
        mbgl::CameraOptions cam;
        cam.zoom   = std::max(0.0, std::min(22.0, zoom));
        cam.anchor = mbgl::ScreenCoordinate{ cx, cy };
        m->map->jumpTo(cam);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Style ─────────────────────────────────────────────────────────────────── */

mbgl_style_t* mbgl_map_get_style(mbgl_map_t* map) noexcept {
    if (!map) return nullptr;
    return to<mbgl_style_t>(&map_ptr(map)->map->getStyle());
}

/* ─── Sources ───────────────────────────────────────────────────────────────── */

mbgl_source_t* mbgl_style_add_geojson_source(mbgl_style_t* st, const char* source_id) noexcept {
    if (!st || !source_id) { set_error(MBGL_INVALID_ARG, "mbgl_style_add_geojson_source: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mbgl::style::GeoJSONSource>(safe_str(source_id));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mbgl_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_source_t* mbgl_style_add_geojson_source_url(mbgl_style_t* st, const char* source_id, const char* url) noexcept {
    if (!st || !source_id || !url) { set_error(MBGL_INVALID_ARG, "mbgl_style_add_geojson_source_url: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mbgl::style::GeoJSONSource>(safe_str(source_id));
        src->setURL(safe_str(url));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mbgl_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_source_t* mbgl_style_add_geojson_source_options(mbgl_style_t* st,
                                                       const char* source_id,
                                                       const char* options_json) noexcept {
    if (!st || !source_id) { set_error(MBGL_INVALID_ARG, "mbgl_style_add_geojson_source_options: null arg"); return nullptr; }
    try {
        using namespace mbgl::style::conversion;
        auto options = mbgl::style::GeoJSONOptions::defaultOptions();
        if (options_json && *options_json) {
            mbgl::JSDocument doc;
            doc.Parse<0>(options_json);
            if (doc.HasParseError()) {
                set_error(MBGL_INVALID_ARG, "mbgl_style_add_geojson_source_options: JSON parse error");
                return nullptr;
            }
            const mbgl::JSValue& v = doc;
            Error err;
            auto converted = convert<mbgl::style::GeoJSONOptions>(Convertible(&v), err);
            if (!converted) {
                set_error(MBGL_INVALID_ARG,
                          std::string("mbgl_style_add_geojson_source_options: ") + err.message);
                return nullptr;
            }
            options = mbgl::makeMutable<mbgl::style::GeoJSONOptions>(std::move(*converted));
        }
        auto src = std::make_unique<mbgl::style::GeoJSONSource>(safe_str(source_id), std::move(options));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mbgl_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_status_t mbgl_geojson_source_set_data(mbgl_source_t* src, const char* geojson) noexcept {
    if (!src || !geojson) return set_error(MBGL_INVALID_ARG, "mbgl_geojson_source_set_data: null arg");
    try {
        auto* gs = as<mbgl::style::GeoJSONSource>(src);
        mbgl::style::conversion::Error err;
        auto result = mbgl::style::conversion::parseGeoJSON(safe_str(geojson), err);
        if (result) { gs->setGeoJSON(*result); return MBGL_OK; }
        return set_error(MBGL_INVALID_ARG, "mbgl_geojson_source_set_data: " + err.message);
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_geojson_source_set_url(mbgl_source_t* src, const char* url) noexcept {
    if (!src || !url) return set_error(MBGL_INVALID_ARG, "mbgl_geojson_source_set_url: null arg");
    try { as<mbgl::style::GeoJSONSource>(src)->setURL(safe_str(url)); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_source_t* mbgl_style_add_vector_source(mbgl_style_t* st, const char* source_id, const char* url) noexcept {
    if (!st || !source_id) { set_error(MBGL_INVALID_ARG, "mbgl_style_add_vector_source: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mbgl::style::VectorSource>(safe_str(source_id), safe_str(url));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mbgl_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_source_t* mbgl_style_add_raster_source(mbgl_style_t* st, const char* source_id, const char* url, int tile_size) noexcept {
    if (!st || !source_id) { set_error(MBGL_INVALID_ARG, "mbgl_style_add_raster_source: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mbgl::style::RasterSource>(safe_str(source_id), safe_str(url), static_cast<uint16_t>(tile_size));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mbgl_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_source_t* mbgl_style_add_rasterdem_source(mbgl_style_t* st, const char* source_id, const char* url, int tile_size) noexcept {
    if (!st || !source_id) { set_error(MBGL_INVALID_ARG, "mbgl_style_add_rasterdem_source: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mbgl::style::RasterDEMSource>(safe_str(source_id), safe_str(url), static_cast<uint16_t>(tile_size));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mbgl_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_source_t* mbgl_style_add_image_source(mbgl_style_t* st, const char* source_id, const char* url,
                                             double lat0, double lon0, double lat1, double lon1,
                                             double lat2, double lon2, double lat3, double lon3) noexcept
{
    if (!st || !source_id) { set_error(MBGL_INVALID_ARG, "mbgl_style_add_image_source: null arg"); return nullptr; }
    try {
        std::array<mbgl::LatLng, 4> coords{
            mbgl::LatLng{lat0,lon0}, mbgl::LatLng{lat1,lon1},
            mbgl::LatLng{lat2,lon2}, mbgl::LatLng{lat3,lon3}
        };
        auto src = std::make_unique<mbgl::style::ImageSource>(safe_str(source_id), coords);
        src->setURL(safe_str(url));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mbgl_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_status_t mbgl_style_remove_source(mbgl_style_t* st, const char* source_id) noexcept {
    if (!st || !source_id) return set_error(MBGL_INVALID_ARG, "mbgl_style_remove_source: null arg");
    try { style_ref(st).removeSource(safe_str(source_id)); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

int mbgl_style_has_source(mbgl_style_t* st, const char* source_id) noexcept {
    if (!st || !source_id) return 0;
    return style_ref(st).getSource(safe_str(source_id)) != nullptr ? 1 : 0;
}

/* ─── Layers ────────────────────────────────────────────────────────────────── */

template<typename LayerT>
static mbgl_layer_t* add_layer(mbgl_style_t* st, const char* layer_id, const char* source_id, const char* before_id) noexcept {
    try {
        auto layer = std::make_unique<LayerT>(safe_str(layer_id), source_id ? safe_str(source_id) : "");
        auto* raw = layer.get();
        if (before_id) style_ref(st).addLayer(std::move(layer), safe_str(before_id));
        else           style_ref(st).addLayer(std::move(layer));
        return reinterpret_cast<mbgl_layer_t*>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

template<typename LayerT>
static mbgl_layer_t* add_layer_no_source(mbgl_style_t* st, const char* layer_id, const char* before_id) noexcept {
    try {
        auto layer = std::make_unique<LayerT>(safe_str(layer_id));
        auto* raw = layer.get();
        if (before_id) style_ref(st).addLayer(std::move(layer), safe_str(before_id));
        else           style_ref(st).addLayer(std::move(layer));
        return reinterpret_cast<mbgl_layer_t*>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mbgl_layer_t* mbgl_style_add_fill_layer          (mbgl_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mbgl::style::FillLayer>(st,id,src,before); }
mbgl_layer_t* mbgl_style_add_line_layer          (mbgl_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mbgl::style::LineLayer>(st,id,src,before); }
mbgl_layer_t* mbgl_style_add_circle_layer        (mbgl_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mbgl::style::CircleLayer>(st,id,src,before); }
mbgl_layer_t* mbgl_style_add_symbol_layer        (mbgl_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mbgl::style::SymbolLayer>(st,id,src,before); }
mbgl_layer_t* mbgl_style_add_raster_layer        (mbgl_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mbgl::style::RasterLayer>(st,id,src,before); }
mbgl_layer_t* mbgl_style_add_heatmap_layer       (mbgl_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mbgl::style::HeatmapLayer>(st,id,src,before); }
mbgl_layer_t* mbgl_style_add_hillshade_layer     (mbgl_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mbgl::style::HillshadeLayer>(st,id,src,before); }
mbgl_layer_t* mbgl_style_add_fill_extrusion_layer(mbgl_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mbgl::style::FillExtrusionLayer>(st,id,src,before); }
mbgl_layer_t* mbgl_style_add_background_layer    (mbgl_style_t* st, const char* id, const char* before) noexcept { return add_layer_no_source<mbgl::style::BackgroundLayer>(st,id,before); }
mbgl_layer_t* mbgl_style_add_location_indicator_layer(mbgl_style_t* st, const char* id, const char* before) noexcept { return add_layer_no_source<mbgl::style::LocationIndicatorLayer>(st,id,before); }

mbgl_status_t mbgl_style_remove_layer(mbgl_style_t* st, const char* layer_id) noexcept {
    if (!st || !layer_id) return set_error(MBGL_INVALID_ARG, "mbgl_style_remove_layer: null arg");
    try { style_ref(st).removeLayer(safe_str(layer_id)); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

int mbgl_style_has_layer(mbgl_style_t* st, const char* layer_id) noexcept {
    if (!st || !layer_id) return 0;
    return style_ref(st).getLayer(safe_str(layer_id)) != nullptr ? 1 : 0;
}

mbgl_status_t mbgl_layer_set_source_layer(mbgl_layer_t* layer, const char* source_layer) noexcept {
    if (!layer || !source_layer) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_source_layer: null arg");
    try {
        auto* l = as<mbgl::style::Layer>(layer);
        l->setSourceLayer(safe_str(source_layer));
        return MBGL_OK;
    }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_layer_set_filter(mbgl_layer_t* layer, const char* filter_json) noexcept {
    if (!layer || !filter_json) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_filter: null arg");
    try {
        mbgl::JSDocument doc;
        doc.Parse(filter_json);
        if (doc.HasParseError()) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_filter: JSON parse error");
        mbgl::style::conversion::Error err;
        auto filter = mbgl::style::conversion::convert<mbgl::style::Filter>(doc, err);
        if (!filter) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_filter: " + err.message);
        as<mbgl::style::Layer>(layer)->setFilter(*filter);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_layer_set_min_zoom(mbgl_layer_t* layer, float zoom) noexcept {
    if (!layer) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_min_zoom: null handle");
    try { as<mbgl::style::Layer>(layer)->setMinZoom(zoom); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_layer_set_max_zoom(mbgl_layer_t* layer, float zoom) noexcept {
    if (!layer) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_max_zoom: null handle");
    try { as<mbgl::style::Layer>(layer)->setMaxZoom(zoom); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_layer_set_visibility(mbgl_layer_t* layer, int visible) noexcept {
    if (!layer) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_visibility: null handle");
    try {
        as<mbgl::style::Layer>(layer)->setVisibility(
            visible ? mbgl::style::VisibilityType::Visible : mbgl::style::VisibilityType::None);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_layer_set_paint_property(mbgl_layer_t* layer, const char* name, const char* value_json) noexcept {
    if (!layer || !name || !value_json) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_paint_property: null arg");
    try {
        mbgl::JSDocument doc;
        doc.Parse(value_json);
        if (doc.HasParseError()) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_paint_property: JSON parse error");
        const mbgl::JSValue& v = doc;
        as<mbgl::style::Layer>(layer)->setProperty(safe_str(name), mbgl::style::conversion::Convertible(&v));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_layer_set_layout_property(mbgl_layer_t* layer, const char* name, const char* value_json) noexcept {
    if (!layer || !name || !value_json) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_layout_property: null arg");
    try {
        mbgl::JSDocument doc;
        doc.Parse(value_json);
        if (doc.HasParseError()) return set_error(MBGL_INVALID_ARG, "mbgl_layer_set_layout_property: JSON parse error");
        const mbgl::JSValue& v = doc;
        as<mbgl::style::Layer>(layer)->setProperty(safe_str(name), mbgl::style::conversion::Convertible(&v));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Map – additional camera / bounds / projection ─────────────────────────── */

mbgl_status_t mbgl_map_fly_to(mbgl_map_t* map, double lat, double lon,
                               double zoom, double bearing, double pitch,
                               int64_t duration_ms) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_fly_to: null handle");
    try {
        mbgl::CameraOptions cam;
        cam.center  = mbgl::LatLng{ lat, lon };
        cam.zoom    = zoom;
        cam.bearing = bearing;
        cam.pitch   = pitch;
        mbgl::AnimationOptions anim{ mbgl::Duration(std::chrono::milliseconds(duration_ms)) };
        map_ptr(map)->map->flyTo(cam, anim);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Map – camera with edge padding ────────────────────────────────────────── */

/* Builds CameraOptions from the padded-variant arguments.  NaN zoom / bearing /
 * pitch fields are left unset so the current value is preserved. */
static mbgl::CameraOptions padded_camera(double lat, double lon,
                                         double zoom, double bearing, double pitch,
                                         double pad_top, double pad_left,
                                         double pad_bottom, double pad_right) {
    mbgl::CameraOptions cam;
    cam.center = mbgl::LatLng{ lat, lon };
    if (!std::isnan(zoom))    cam.zoom    = zoom;
    if (!std::isnan(bearing)) cam.bearing = bearing;
    if (!std::isnan(pitch))   cam.pitch   = pitch;
    cam.padding = mbgl::EdgeInsets{ pad_top, pad_left, pad_bottom, pad_right };
    return cam;
}

mbgl_status_t mbgl_map_jump_to_padded(mbgl_map_t* map,
                                        double lat, double lon,
                                        double zoom, double bearing, double pitch,
                                        double pad_top, double pad_left,
                                        double pad_bottom, double pad_right) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_jump_to_padded: null handle");
    try {
        map_ptr(map)->map->jumpTo(padded_camera(lat, lon, zoom, bearing, pitch,
                                                pad_top, pad_left, pad_bottom, pad_right));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_ease_to_padded(mbgl_map_t* map,
                                        double lat, double lon,
                                        double zoom, double bearing, double pitch,
                                        double pad_top, double pad_left,
                                        double pad_bottom, double pad_right,
                                        int64_t duration_ms) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_ease_to_padded: null handle");
    try {
        mbgl::AnimationOptions anim{ mbgl::Duration(std::chrono::milliseconds(duration_ms)) };
        map_ptr(map)->map->easeTo(padded_camera(lat, lon, zoom, bearing, pitch,
                                                pad_top, pad_left, pad_bottom, pad_right), anim);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_fly_to_padded(mbgl_map_t* map,
                                       double lat, double lon,
                                       double zoom, double bearing, double pitch,
                                       double pad_top, double pad_left,
                                       double pad_bottom, double pad_right,
                                       int64_t duration_ms) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_fly_to_padded: null handle");
    try {
        mbgl::AnimationOptions anim{ mbgl::Duration(std::chrono::milliseconds(duration_ms)) };
        map_ptr(map)->map->flyTo(padded_camera(lat, lon, zoom, bearing, pitch,
                                               pad_top, pad_left, pad_bottom, pad_right), anim);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_get_camera(mbgl_map_t* map,
                                    double pad_top, double pad_left,
                                    double pad_bottom, double pad_right,
                                    double* out_lat, double* out_lon,
                                    double* out_zoom,
                                    double* out_bearing,
                                    double* out_pitch) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_get_camera: null handle");
    try {
        std::optional<mbgl::EdgeInsets> padding;
        if (pad_top != 0 || pad_left != 0 || pad_bottom != 0 || pad_right != 0)
            padding = mbgl::EdgeInsets{ pad_top, pad_left, pad_bottom, pad_right };
        auto cam = map_ptr(map)->map->getCameraOptions(padding);
        if (out_lat)     *out_lat     = cam.center  ? cam.center->latitude()  : 0.0;
        if (out_lon)     *out_lon     = cam.center  ? cam.center->longitude() : 0.0;
        if (out_zoom)    *out_zoom    = cam.zoom    ? *cam.zoom    : 0.0;
        if (out_bearing) *out_bearing = cam.bearing ? *cam.bearing : 0.0;
        if (out_pitch)   *out_pitch   = cam.pitch   ? *cam.pitch   : 0.0;
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_scale_by(mbgl_map_t* map, double scale,
                                  double anchor_x, double anchor_y,
                                  int64_t duration_ms) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_scale_by: null handle");
    try {
        std::optional<mbgl::ScreenCoordinate> anchor;
        if (!std::isnan(anchor_x) && !std::isnan(anchor_y))
            anchor = mbgl::ScreenCoordinate{ anchor_x, anchor_y };
        mbgl::AnimationOptions anim;
        if (duration_ms > 0) anim.duration = mbgl::Duration(std::chrono::milliseconds(duration_ms));
        map_ptr(map)->map->scaleBy(scale, anchor, anim);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_bounds(mbgl_map_t* map,
                                   double lat_sw, double lon_sw,
                                   double lat_ne, double lon_ne,
                                   double min_zoom, double max_zoom,
                                   double min_pitch, double max_pitch) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_bounds: null handle");
    try {
        mbgl::BoundOptions opts;
        if (!std::isnan(lat_sw) && !std::isnan(lon_sw) &&
            !std::isnan(lat_ne) && !std::isnan(lon_ne)) {
            opts.withLatLngBounds(mbgl::LatLngBounds::hull(
                mbgl::LatLng{ lat_sw, lon_sw }, mbgl::LatLng{ lat_ne, lon_ne }));
        }
        if (!std::isnan(min_zoom))  opts.withMinZoom(min_zoom);
        if (!std::isnan(max_zoom))  opts.withMaxZoom(max_zoom);
        if (!std::isnan(min_pitch)) opts.withMinPitch(min_pitch);
        if (!std::isnan(max_pitch)) opts.withMaxPitch(max_pitch);
        map_ptr(map)->map->setBounds(opts);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_camera_for_bounds(mbgl_map_t* map,
                                          double lat_sw, double lon_sw,
                                          double lat_ne, double lon_ne,
                                          double pad_top,    double pad_left,
                                          double pad_bottom, double pad_right,
                                          double* out_lat, double* out_lon,
                                          double* out_zoom, double* out_bearing,
                                          double* out_pitch) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_camera_for_bounds: null handle");
    try {
        auto bounds  = mbgl::LatLngBounds::hull(mbgl::LatLng{ lat_sw, lon_sw },
                                                mbgl::LatLng{ lat_ne, lon_ne });
        mbgl::EdgeInsets padding{ pad_top, pad_left, pad_bottom, pad_right };
        auto cam = map_ptr(map)->map->cameraForLatLngBounds(bounds, padding);
        if (out_lat)     *out_lat     = cam.center  ? cam.center->latitude()  : 0.0;
        if (out_lon)     *out_lon     = cam.center  ? cam.center->longitude() : 0.0;
        if (out_zoom)    *out_zoom    = cam.zoom    ? *cam.zoom    : 0.0;
        if (out_bearing) *out_bearing = cam.bearing ? *cam.bearing : 0.0;
        if (out_pitch)   *out_pitch   = cam.pitch   ? *cam.pitch   : 0.0;
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

void mbgl_map_pixel_for_latlng(mbgl_map_t* map, double lat, double lon,
                                double* out_x, double* out_y) noexcept {
    if (!map || !out_x || !out_y) return;
    auto sc = map_ptr(map)->map->pixelForLatLng(mbgl::LatLng{ lat, lon });
    *out_x = sc.x;
    *out_y = sc.y;
}

void mbgl_map_latlng_for_pixel(mbgl_map_t* map, double x, double y,
                                double* out_lat, double* out_lon) noexcept {
    if (!map || !out_lat || !out_lon) return;
    auto ll = map_ptr(map)->map->latLngForPixel(mbgl::ScreenCoordinate{ x, y });
    *out_lat = ll.latitude();
    *out_lon = ll.longitude();
}

mbgl_status_t mbgl_map_set_projection_mode(mbgl_map_t* map, int axonometric,
                                            double x_skew, double y_skew) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_projection_mode: null handle");
    try {
        mbgl::ProjectionMode mode;
        mode.axonometric = (axonometric != 0);
        mode.xSkew = x_skew;
        mode.ySkew = y_skew;
        map_ptr(map)->map->setProjectionMode(mode);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Style – images ────────────────────────────────────────────────────────── */

mbgl_status_t mbgl_style_add_image(mbgl_style_t* st, const char* image_id,
                                    int width, int height, float pixel_ratio,
                                    int sdf, const uint8_t* rgba_premultiplied) noexcept {
    if (!st || !image_id || !rgba_premultiplied) return set_error(MBGL_INVALID_ARG, "mbgl_style_add_image: null arg");
    try {
        mbgl::PremultipliedImage img(
            { static_cast<uint32_t>(width), static_cast<uint32_t>(height) },
            rgba_premultiplied,
            static_cast<size_t>(width) * static_cast<size_t>(height) * 4u);
        style_ref(st).addImage(std::make_unique<mbgl::style::Image>(
            safe_str(image_id), std::move(img), pixel_ratio, sdf != 0));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_style_remove_image(mbgl_style_t* st, const char* image_id) noexcept {
    if (!st || !image_id) return set_error(MBGL_INVALID_ARG, "mbgl_style_remove_image: null arg");
    try { style_ref(st).removeImage(safe_str(image_id)); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

char* mbgl_style_get_json(mbgl_style_t* st) noexcept {
    if (!st) return nullptr;
    try {
        std::string json = style_ref(st).getJSON();
        char* result = new char[json.size() + 1];
        std::copy(json.begin(), json.end(), result);
        result[json.size()] = '\0';
        return result;
    } catch (...) { return nullptr; }
}

mbgl_status_t mbgl_style_set_transition(mbgl_style_t* st, int64_t duration_ms, int64_t delay_ms) noexcept {
    if (!st) return set_error(MBGL_INVALID_ARG, "mbgl_style_set_transition: null handle");
    try {
        mbgl::style::TransitionOptions opts;
        opts.duration = mbgl::Duration(std::chrono::milliseconds(duration_ms));
        opts.delay    = mbgl::Duration(std::chrono::milliseconds(delay_ms));
        style_ref(st).setTransitionOptions(opts);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_style_set_light_property(mbgl_style_t* st, const char* name, const char* value_json) noexcept {
    if (!st || !name || !value_json) return set_error(MBGL_INVALID_ARG, "mbgl_style_set_light_property: null arg");
    try {
        auto* light = style_ref(st).getLight();
        if (!light) return set_error(MBGL_INVALID_STATE, "mbgl_style_set_light_property: no light in style");
        mbgl::JSDocument doc;
        doc.Parse(value_json);
        if (doc.HasParseError()) return set_error(MBGL_INVALID_ARG, "mbgl_style_set_light_property: JSON parse error");
        const mbgl::JSValue& v = doc;
        light->setProperty(safe_str(name), mbgl::style::conversion::Convertible(&v));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Layers – additional types ─────────────────────────────────────────────── */

mbgl_layer_t* mbgl_style_add_color_relief_layer(mbgl_style_t* st, const char* id,
                                                 const char* src, const char* before) noexcept {
    return add_layer<mbgl::style::ColorReliefLayer>(st, id, src, before);
}

/* ─── Feature queries ────────────────────────────────────────────────────────── */

static std::vector<std::string> split_layer_ids(const char* csv) {
    std::vector<std::string> result;
    if (!csv || !*csv) return result;
    std::istringstream ss(csv);
    std::string token;
    while (std::getline(ss, token, ',')) {
        if (!token.empty()) result.push_back(std::move(token));
    }
    return result;
}

static char* features_to_json(std::vector<mbgl::Feature>&& features) {
    mbgl::FeatureCollection fc(features.begin(), features.end());
    std::string json = mapbox::geojson::stringify(mbgl::GeoJSON{ fc });
    char* result = new char[json.size() + 1];
    std::copy(json.begin(), json.end(), result);
    result[json.size()] = '\0';
    return result;
}

char* mbgl_map_query_rendered_features_at_point(mbgl_map_t* map, double x, double y,
                                                 const char* layer_ids) noexcept {
    if (!map) return nullptr;
    try {
        auto* m        = map_ptr(map);
        auto* renderer = m->frontend->getRenderer();
        if (!renderer) return nullptr;
        mbgl::RenderedQueryOptions opts;
        auto ids = split_layer_ids(layer_ids);
        if (!ids.empty()) opts.layerIDs = ids;
        auto features = renderer->queryRenderedFeatures(mbgl::ScreenCoordinate{ x, y }, opts);
        return features_to_json(std::move(features));
    } catch (...) { return nullptr; }
}

char* mbgl_map_query_rendered_features_in_box(mbgl_map_t* map,
                                               double x1, double y1,
                                               double x2, double y2,
                                               const char* layer_ids) noexcept {
    if (!map) return nullptr;
    try {
        auto* m        = map_ptr(map);
        auto* renderer = m->frontend->getRenderer();
        if (!renderer) return nullptr;
        mbgl::RenderedQueryOptions opts;
        auto ids = split_layer_ids(layer_ids);
        if (!ids.empty()) opts.layerIDs = ids;
        mbgl::ScreenBox box{ { x1, y1 }, { x2, y2 } };
        auto features = renderer->queryRenderedFeatures(box, opts);
        return features_to_json(std::move(features));
    } catch (...) { return nullptr; }
}

void mbgl_free_string(char* str) noexcept {
    delete[] str;
}

/* ─── Internal helpers ───────────────────────────────────────────────────────── */
static constexpr double kNaN = std::numeric_limits<double>::quiet_NaN();

static char* dup_string(const std::string& s) {
    char* result = new char[s.size() + 1];
    std::copy(s.begin(), s.end(), result);
    result[s.size()] = '\0';
    return result;
}

/* Bridge declared in mln_cabi_internal.hpp for sibling translation units. */
char* cabi_dup_string(const std::string& s) {
    return dup_string(s);
}

/* ─── Viewport bounds ───────────────────────────────────────────────────────── */

mbgl_status_t mbgl_map_latlng_bounds_for_camera(mbgl_map_t* map,
                                                  double* out_lat_sw, double* out_lon_sw,
                                                  double* out_lat_ne, double* out_lon_ne) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_latlng_bounds_for_camera: null handle");
    try {
        auto* m      = map_ptr(map);
        auto  bounds = m->map->latLngBoundsForCamera(m->map->getCameraOptions());
        if (out_lat_sw) *out_lat_sw = bounds.southwest().latitude();
        if (out_lon_sw) *out_lon_sw = bounds.southwest().longitude();
        if (out_lat_ne) *out_lat_ne = bounds.northeast().latitude();
        if (out_lon_ne) *out_lon_ne = bounds.northeast().longitude();
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Memory / debug ────────────────────────────────────────────────────────── */

mbgl_status_t mbgl_map_reduce_memory_use(mbgl_map_t* map) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_reduce_memory_use: null handle");
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (renderer) renderer->reduceMemoryUse();
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_dump_debug_logs(mbgl_map_t* map) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_dump_debug_logs: null handle");
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (renderer) renderer->dumpDebugLogs();
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Feature state helpers ─────────────────────────────────────────────────── */

static mbgl::Value jsValueToMbglValue(const mbgl::JSValue& v) {
    if (v.IsBool())   return v.GetBool();
    if (v.IsInt64())  return v.GetInt64();
    if (v.IsUint64()) return static_cast<int64_t>(v.GetUint64());
    if (v.IsDouble()) return v.GetDouble();
    if (v.IsString()) return std::string{v.GetString(), v.GetStringLength()};
    if (v.IsArray()) {
        std::vector<mbgl::Value> arr;
        arr.reserve(v.Size());
        for (const auto& elem : v.GetArray())
            arr.push_back(jsValueToMbglValue(elem));
        return arr;
    }
    if (v.IsObject()) {
        mbgl::PropertyMap obj;
        for (const auto& m : v.GetObject())
            obj[std::string{m.name.GetString(), m.name.GetStringLength()}] = jsValueToMbglValue(m.value);
        return obj;
    }
    return mbgl::NullValue{};
}

static mbgl::FeatureState jsonToFeatureState(const char* json) {
    mbgl::FeatureState state;
    if (!json || !*json) return state;
    mbgl::JSDocument doc;
    doc.Parse(json);
    if (doc.HasParseError() || !doc.IsObject()) return state;
    for (const auto& m : doc.GetObject())
        state[std::string{m.name.GetString(), m.name.GetStringLength()}] = jsValueToMbglValue(m.value);
    return state;
}

static void writeValue(const mbgl::Value& v, rapidjson::Writer<rapidjson::StringBuffer>& w) {
    v.match(
        [&](const mbgl::NullValue&)                { w.Null(); },
        [&](bool b)                                { w.Bool(b); },
        [&](uint64_t u)                            { w.Uint64(u); },
        [&](int64_t i)                             { w.Int64(i); },
        [&](double d)                              { w.Double(d); },
        [&](const std::string& s) {
            w.String(s.data(), static_cast<rapidjson::SizeType>(s.size()));
        },
        [&](const std::vector<mbgl::Value>& arr) {
            w.StartArray();
            for (const auto& e : arr) writeValue(e, w);
            w.EndArray();
        },
        [&](const mbgl::PropertyMap& obj) {
            w.StartObject();
            for (const auto& [k, v2] : obj) {
                w.Key(k.data(), static_cast<rapidjson::SizeType>(k.size()));
                writeValue(v2, w);
            }
            w.EndObject();
        }
    );
}

static char* featureStateToJson(const mbgl::FeatureState& state) {
    rapidjson::StringBuffer buf;
    rapidjson::Writer<rapidjson::StringBuffer> writer(buf);
    writer.StartObject();
    for (const auto& [key, val] : state) {
        writer.Key(key.data(), static_cast<rapidjson::SizeType>(key.size()));
        writeValue(val, writer);
    }
    writer.EndObject();
    return dup_string(std::string(buf.GetString(), buf.GetSize()));
}

/* ─── Feature state ─────────────────────────────────────────────────────────── */

mbgl_status_t mbgl_map_set_feature_state(mbgl_map_t* map,
                                           const char* source_id,
                                           const char* source_layer_id,
                                           const char* feature_id,
                                           const char* state_json) noexcept {
    if (!map || !source_id || !feature_id || !state_json)
        return set_error(MBGL_INVALID_ARG, "mbgl_map_set_feature_state: null arg");
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return set_error(MBGL_INVALID_STATE, "mbgl_map_set_feature_state: renderer not ready");
        std::optional<std::string> layerId =
            (source_layer_id && *source_layer_id) ? std::optional<std::string>{source_layer_id} : std::nullopt;
        renderer->setFeatureState(safe_str(source_id), layerId,
                                  safe_str(feature_id), jsonToFeatureState(state_json));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

char* mbgl_map_get_feature_state(mbgl_map_t* map,
                                   const char* source_id,
                                   const char* source_layer_id,
                                   const char* feature_id) noexcept {
    if (!map || !source_id || !feature_id) return nullptr;
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return nullptr;
        std::optional<std::string> layerId =
            (source_layer_id && *source_layer_id) ? std::optional<std::string>{source_layer_id} : std::nullopt;
        mbgl::FeatureState state;
        renderer->getFeatureState(state, safe_str(source_id), layerId, safe_str(feature_id));
        return featureStateToJson(state);
    } catch (...) { return nullptr; }
}

mbgl_status_t mbgl_map_remove_feature_state(mbgl_map_t* map,
                                              const char* source_id,
                                              const char* source_layer_id,
                                              const char* feature_id,
                                              const char* state_key) noexcept {
    if (!map || !source_id)
        return set_error(MBGL_INVALID_ARG, "mbgl_map_remove_feature_state: null source_id");
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return set_error(MBGL_INVALID_STATE, "mbgl_map_remove_feature_state: renderer not ready");
        std::optional<std::string> layerId =
            (source_layer_id && *source_layer_id) ? std::optional<std::string>{source_layer_id} : std::nullopt;
        std::optional<std::string> featureIdOpt =
            (feature_id && *feature_id) ? std::optional<std::string>{feature_id} : std::nullopt;
        std::optional<std::string> stateKeyOpt =
            (state_key && *state_key) ? std::optional<std::string>{state_key} : std::nullopt;
        renderer->removeFeatureState(safe_str(source_id), layerId, featureIdOpt, stateKeyOpt);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Source-feature / feature-extension queries ────────────────────────────── */

char* mbgl_map_query_source_features(mbgl_map_t* map,
                                       const char* source_id,
                                       const char* source_layer_ids,
                                       const char* filter_json) noexcept {
    if (!map || !source_id) return nullptr;
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return nullptr;

        mbgl::SourceQueryOptions opts;
        auto layers = split_layer_ids(source_layer_ids);
        if (!layers.empty()) opts.sourceLayers = layers;
        if (filter_json && *filter_json) {
            mbgl::JSDocument doc;
            doc.Parse<0>(filter_json);
            if (doc.HasParseError()) return nullptr;
            const mbgl::JSValue& v = doc;
            mbgl::style::conversion::Error err;
            auto filter = mbgl::style::conversion::convert<mbgl::style::Filter>(
                mbgl::style::conversion::Convertible(&v), err);
            if (!filter) { set_error(MBGL_INVALID_ARG, "mbgl_map_query_source_features: " + err.message); return nullptr; }
            opts.filter = std::move(*filter);
        }
        auto features = renderer->querySourceFeatures(safe_str(source_id), opts);
        return features_to_json(std::move(features));
    } catch (...) { return nullptr; }
}

char* mbgl_map_query_feature_extensions(mbgl_map_t* map,
                                          const char* source_id,
                                          const char* feature_json,
                                          const char* extension,
                                          const char* extension_field,
                                          const char* args_json) noexcept {
    if (!map || !source_id || !feature_json || !extension || !extension_field) return nullptr;
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return nullptr;

        mbgl::style::conversion::Error err;
        auto geojson = mbgl::style::conversion::parseGeoJSON(safe_str(feature_json), err);
        if (!geojson) { set_error(MBGL_INVALID_ARG, "mbgl_map_query_feature_extensions: " + err.message); return nullptr; }
        mbgl::Feature feature;
        if (geojson->is<mapbox::geojson::feature>()) {
            feature = mbgl::Feature{ geojson->get<mapbox::geojson::feature>() };
        } else if (geojson->is<mapbox::geojson::feature_collection>() &&
                   !geojson->get<mapbox::geojson::feature_collection>().empty()) {
            feature = mbgl::Feature{ geojson->get<mapbox::geojson::feature_collection>().front() };
        } else {
            set_error(MBGL_INVALID_ARG, "mbgl_map_query_feature_extensions: feature_json must be a GeoJSON Feature");
            return nullptr;
        }

        std::optional<std::map<std::string, mbgl::Value>> args;
        if (args_json && *args_json) {
            mbgl::JSDocument doc;
            doc.Parse<0>(args_json);
            if (!doc.HasParseError() && doc.IsObject()) {
                std::map<std::string, mbgl::Value> parsed;
                for (const auto& m : doc.GetObject())
                    parsed[std::string{m.name.GetString(), m.name.GetStringLength()}] = jsValueToMbglValue(m.value);
                args = std::move(parsed);
            }
        }

        auto result = renderer->queryFeatureExtensions(
            safe_str(source_id), feature, safe_str(extension), safe_str(extension_field), args);

        if (result.is<mbgl::FeatureCollection>()) {
            std::string json = mapbox::geojson::stringify(
                mbgl::GeoJSON{ result.get<mbgl::FeatureCollection>() });
            return dup_string(json);
        }
        rapidjson::StringBuffer buf;
        rapidjson::Writer<rapidjson::StringBuffer> writer(buf);
        writeValue(result.get<mbgl::Value>(), writer);
        return dup_string(std::string(buf.GetString(), buf.GetSize()));
    } catch (...) { return nullptr; }
}

/* ─── Style – generic JSON add ──────────────────────────────────────────────── */

mbgl_status_t mbgl_style_add_source_json(mbgl_style_t* st,
                                           const char* source_id,
                                           const char* source_json) noexcept {
    if (!st || !source_id || !source_json)
        return set_error(MBGL_INVALID_ARG, "mbgl_style_add_source_json: null arg");
    try {
        using namespace mbgl::style::conversion;
        mbgl::JSDocument doc;
        doc.Parse<0>(source_json);
        if (doc.HasParseError())
            return set_error(MBGL_INVALID_ARG, "mbgl_style_add_source_json: JSON parse error");
        const mbgl::JSValue& v = doc;
        Error err;
        auto source = convert<std::unique_ptr<mbgl::style::Source>>(
            Convertible(&v), err, safe_str(source_id));
        if (!source)
            return set_error(MBGL_INVALID_ARG, std::string("mbgl_style_add_source_json: ") + err.message);
        style_ref(st).addSource(std::move(*source));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_layer_t* mbgl_style_add_layer_json(mbgl_style_t* st,
                                          const char* layer_json,
                                          const char* before_id) noexcept {
    if (!st || !layer_json) { set_error(MBGL_INVALID_ARG, "mbgl_style_add_layer_json: null arg"); return nullptr; }
    try {
        using namespace mbgl::style::conversion;
        mbgl::JSDocument doc;
        doc.Parse<0>(layer_json);
        if (doc.HasParseError()) {
            set_error(MBGL_INVALID_ARG, "mbgl_style_add_layer_json: JSON parse error");
            return nullptr;
        }
        const mbgl::JSValue& v = doc;
        Error err;
        auto layer = convert<std::unique_ptr<mbgl::style::Layer>>(Convertible(&v), err);
        if (!layer) {
            set_error(MBGL_INVALID_ARG, std::string("mbgl_style_add_layer_json: ") + err.message);
            return nullptr;
        }
        auto* raw = layer->get();
        if (before_id && *before_id) style_ref(st).addLayer(std::move(*layer), safe_str(before_id));
        else                         style_ref(st).addLayer(std::move(*layer));
        return reinterpret_cast<mbgl_layer_t*>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

/* ─── Gesture helpers ───────────────────────────────────────────────────────── */

mbgl_status_t mbgl_map_set_gesture_in_progress(mbgl_map_t* map, int in_progress) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_gesture_in_progress: null handle");
    try { map_ptr(map)->map->setGestureInProgress(in_progress != 0); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

int mbgl_map_is_gesture_in_progress(mbgl_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isGestureInProgress() ? 1 : 0;
}

int mbgl_map_is_rotating(mbgl_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isRotating() ? 1 : 0;
}

int mbgl_map_is_scaling(mbgl_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isScaling() ? 1 : 0;
}

int mbgl_map_is_panning(mbgl_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isPanning() ? 1 : 0;
}

mbgl_status_t mbgl_map_move_by(mbgl_map_t* map, double dx, double dy, int64_t duration_ms) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_move_by: null handle");
    try {
        mbgl::AnimationOptions anim;
        if (duration_ms > 0) anim.duration = mbgl::Duration(std::chrono::milliseconds(duration_ms));
        map_ptr(map)->map->moveBy({dx, dy}, anim);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_rotate_by(mbgl_map_t* map, double x0, double y0, double x1, double y1) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_rotate_by: null handle");
    try { map_ptr(map)->map->rotateBy({x0, y0}, {x1, y1}); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_pitch_by(mbgl_map_t* map, double delta_degrees, int64_t duration_ms) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_pitch_by: null handle");
    try {
        mbgl::AnimationOptions anim;
        if (duration_ms > 0) anim.duration = mbgl::Duration(std::chrono::milliseconds(duration_ms));
        map_ptr(map)->map->pitchBy(delta_degrees, anim);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Map option setters ─────────────────────────────────────────────────────── */

mbgl_status_t mbgl_map_set_north_orientation(mbgl_map_t* map, int orientation) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_north_orientation: null handle");
    try {
        map_ptr(map)->map->setNorthOrientation(static_cast<mbgl::NorthOrientation>(orientation));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_constrain_mode(mbgl_map_t* map, int mode) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_constrain_mode: null handle");
    try {
        map_ptr(map)->map->setConstrainMode(static_cast<mbgl::ConstrainMode>(mode));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_viewport_mode(mbgl_map_t* map, int mode) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_viewport_mode: null handle");
    try {
        map_ptr(map)->map->setViewportMode(static_cast<mbgl::ViewportMode>(mode));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Bounds read-back ───────────────────────────────────────────────────────── */

void mbgl_map_get_bounds(mbgl_map_t* map,
                          double* out_lat_sw, double* out_lon_sw,
                          double* out_lat_ne, double* out_lon_ne,
                          double* out_min_zoom, double* out_max_zoom,
                          double* out_min_pitch, double* out_max_pitch) noexcept {
    if (!map) return;
    auto b = map_ptr(map)->map->getBounds();
    if (out_lat_sw)   *out_lat_sw   = b.bounds ? b.bounds->south() : kNaN;
    if (out_lon_sw)   *out_lon_sw   = b.bounds ? b.bounds->west()  : kNaN;
    if (out_lat_ne)   *out_lat_ne   = b.bounds ? b.bounds->north() : kNaN;
    if (out_lon_ne)   *out_lon_ne   = b.bounds ? b.bounds->east()  : kNaN;
    if (out_min_zoom) *out_min_zoom = b.minZoom.value_or(kNaN);
    if (out_max_zoom) *out_max_zoom = b.maxZoom.value_or(kNaN);
    if (out_min_pitch) *out_min_pitch = b.minPitch.value_or(kNaN);
    if (out_max_pitch) *out_max_pitch = b.maxPitch.value_or(kNaN);
}

/* ─── Tile LOD controls ──────────────────────────────────────────────────────── */

mbgl_status_t mbgl_map_set_prefetch_zoom_delta(mbgl_map_t* map, int delta) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_prefetch_zoom_delta: null handle");
    try {
        int clamped = delta < 0 ? 0 : (delta > 255 ? 255 : delta);
        map_ptr(map)->map->setPrefetchZoomDelta(static_cast<uint8_t>(clamped));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

int mbgl_map_get_prefetch_zoom_delta(mbgl_map_t* map) noexcept {
    if (!map) return 0;
    return static_cast<int>(map_ptr(map)->map->getPrefetchZoomDelta());
}

mbgl_status_t mbgl_map_set_tile_lod_min_radius(mbgl_map_t* map, double radius) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_tile_lod_min_radius: null handle");
    try { map_ptr(map)->map->setTileLodMinRadius(radius); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_tile_lod_scale(mbgl_map_t* map, double scale) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_tile_lod_scale: null handle");
    try { map_ptr(map)->map->setTileLodScale(scale); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_tile_lod_pitch_threshold(mbgl_map_t* map, double threshold_rad) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_tile_lod_pitch_threshold: null handle");
    try { map_ptr(map)->map->setTileLodPitchThreshold(threshold_rad); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_tile_lod_zoom_shift(mbgl_map_t* map, double shift) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_tile_lod_zoom_shift: null handle");
    try { map_ptr(map)->map->setTileLodZoomShift(shift); return MBGL_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_set_tile_lod_mode(mbgl_map_t* map, int mode) noexcept {
    if (!map) return set_error(MBGL_INVALID_ARG, "mbgl_map_set_tile_lod_mode: null handle");
    try {
        map_ptr(map)->map->setTileLodMode(static_cast<mbgl::TileLodMode>(mode));
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Camera for lat/lng point set ──────────────────────────────────────────── */

mbgl_status_t mbgl_map_camera_for_latlngs(mbgl_map_t* map,
                                           const double* latlngs, int count,
                                           double pad_top, double pad_left,
                                           double pad_bottom, double pad_right,
                                           double* out_lat, double* out_lon,
                                           double* out_zoom, double* out_bearing,
                                           double* out_pitch) noexcept {
    if (!map || !latlngs) return set_error(MBGL_INVALID_ARG, "mbgl_map_camera_for_latlngs: null arg");
    try {
        std::vector<mbgl::LatLng> pts;
        pts.reserve(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i)
            pts.emplace_back(latlngs[i * 2], latlngs[i * 2 + 1]);
        mbgl::EdgeInsets padding{ pad_top, pad_left, pad_bottom, pad_right };
        auto cam = map_ptr(map)->map->cameraForLatLngs(pts, padding);
        if (out_lat)     *out_lat     = cam.center ? cam.center->latitude()  : kNaN;
        if (out_lon)     *out_lon     = cam.center ? cam.center->longitude() : kNaN;
        if (out_zoom)    *out_zoom    = cam.zoom.value_or(kNaN);
        if (out_bearing) *out_bearing = cam.bearing.value_or(kNaN);
        if (out_pitch)   *out_pitch   = cam.pitch.value_or(kNaN);
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Batch projection ───────────────────────────────────────────────────────── */

mbgl_status_t mbgl_map_pixels_for_latlngs(mbgl_map_t* map,
                                           const double* latlngs, int count,
                                           double* out_xy) noexcept {
    if (!map || !latlngs || !out_xy) return set_error(MBGL_INVALID_ARG, "mbgl_map_pixels_for_latlngs: null arg");
    try {
        std::vector<mbgl::LatLng> pts;
        pts.reserve(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i)
            pts.emplace_back(latlngs[i * 2], latlngs[i * 2 + 1]);
        auto pixels = map_ptr(map)->map->pixelsForLatLngs(pts);
        for (size_t i = 0; i < pixels.size(); ++i) {
            out_xy[i * 2]     = pixels[i].x;
            out_xy[i * 2 + 1] = pixels[i].y;
        }
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mbgl_status_t mbgl_map_latlngs_for_pixels(mbgl_map_t* map,
                                           const double* xy, int count,
                                           double* out_ll) noexcept {
    if (!map || !xy || !out_ll) return set_error(MBGL_INVALID_ARG, "mbgl_map_latlngs_for_pixels: null arg");
    try {
        std::vector<mbgl::ScreenCoordinate> pts;
        pts.reserve(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i)
            pts.emplace_back(xy[i * 2], xy[i * 2 + 1]);
        auto latlngs = map_ptr(map)->map->latLngsForPixels(pts);
        for (size_t i = 0; i < latlngs.size(); ++i) {
            out_ll[i * 2]     = latlngs[i].latitude();
            out_ll[i * 2 + 1] = latlngs[i].longitude();
        }
        return MBGL_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Style enumeration ──────────────────────────────────────────────────────── */

char* mbgl_style_get_url(mbgl_style_t* st) noexcept {
    if (!st) return nullptr;
    try { return dup_string(style_ref(st).getURL()); }
    catch (...) { return nullptr; }
}

char* mbgl_style_get_name(mbgl_style_t* st) noexcept {
    if (!st) return nullptr;
    try { return dup_string(style_ref(st).getName()); }
    catch (...) { return nullptr; }
}

/* IDs are returned as a JSON array: IDs may contain any character (including
 * newlines), so a delimiter-joined string would be ambiguous. */
static char* ids_to_json(const std::vector<std::string>& ids) {
    rapidjson::StringBuffer buf;
    rapidjson::Writer<rapidjson::StringBuffer> writer(buf);
    writer.StartArray();
    for (const auto& id : ids)
        writer.String(id.data(), static_cast<rapidjson::SizeType>(id.size()));
    writer.EndArray();
    return dup_string(std::string(buf.GetString(), buf.GetSize()));
}

char* mbgl_style_get_source_ids(mbgl_style_t* st) noexcept {
    if (!st) return nullptr;
    try {
        std::vector<std::string> ids;
        for (auto* src : style_ref(st).getSources()) ids.push_back(src->getID());
        return ids_to_json(ids);
    } catch (...) { return nullptr; }
}

char* mbgl_style_get_layer_ids(mbgl_style_t* st) noexcept {
    if (!st) return nullptr;
    try {
        std::vector<std::string> ids;
        for (auto* layer : style_ref(st).getLayers()) ids.push_back(layer->getID());
        return ids_to_json(ids);
    } catch (...) { return nullptr; }
}

mbgl_layer_t* mbgl_style_get_layer(mbgl_style_t* st, const char* layer_id) noexcept {
    if (!st || !layer_id) return nullptr;
    return to<mbgl_layer_t>(style_ref(st).getLayer(safe_str(layer_id)));
}

mbgl_source_t* mbgl_style_get_source(mbgl_style_t* st, const char* source_id) noexcept {
    if (!st || !source_id) return nullptr;
    return to<mbgl_source_t>(style_ref(st).getSource(safe_str(source_id)));
}

char* mbgl_source_get_attribution(mbgl_source_t* src) noexcept {
    if (!src) return nullptr;
    try {
        const auto& attr = as<mbgl::style::Source>(src)->getAttribution();
        if (!attr) return nullptr;
        return dup_string(*attr);
    } catch (...) { return nullptr; }
}

/* ─── Layer read-back ────────────────────────────────────────────────────────── */

static char* style_property_to_json(const mbgl::style::StyleProperty& prop) {
    if (prop.getKind() == mbgl::style::StyleProperty::Kind::Undefined)
        return nullptr;
    rapidjson::StringBuffer sb;
    rapidjson::Writer<rapidjson::StringBuffer,
                       rapidjson::UTF8<>, rapidjson::UTF8<>,
                       rapidjson::CrtAllocator> writer(sb);
    mbgl::style::conversion::stringify(writer, prop.getValue());
    return dup_string(std::string(sb.GetString(), sb.GetSize()));
}

char* mbgl_layer_get_paint_property(mbgl_layer_t* layer, const char* name) noexcept {
    if (!layer || !name) return nullptr;
    try { return style_property_to_json(as<mbgl::style::Layer>(layer)->getProperty(safe_str(name))); }
    catch (...) { return nullptr; }
}

char* mbgl_layer_get_layout_property(mbgl_layer_t* layer, const char* name) noexcept {
    if (!layer || !name) return nullptr;
    try { return style_property_to_json(as<mbgl::style::Layer>(layer)->getProperty(safe_str(name))); }
    catch (...) { return nullptr; }
}

int mbgl_layer_get_visibility(mbgl_layer_t* layer) noexcept {
    if (!layer) return 1;
    return as<mbgl::style::Layer>(layer)->getVisibility()
               == mbgl::style::VisibilityType::Visible ? 1 : 0;
}

/* ─── Version ───────────────────────────────────────────────────────────────── */
const char* mln_cabi_version() noexcept {
    return "2.2.0";
}

/* ─── Android window helpers ────────────────────────────────────────────────── */
#ifdef __ANDROID__
#include <android/native_window_jni.h>
#include <jni.h>

// This standalone NDK build never runs the JNI_OnLoad that the upstream
// MapLibre Android SDK normally uses to populate mbgl::android::theJVM, so
// every background thread that calls attachThread() (e.g. the RunLoop's
// "Alarm" thread) aborts on assert(vm != nullptr) in jni.cpp. Capture the
// JavaVM here — the first JNIEnv we're handed, on the surface-creation path
// that always runs before the RunLoop/Alarm thread is spawned.
namespace mbgl { namespace android {
extern JavaVM* theJVM;
}} // namespace mbgl::android

void* mbgl_android_acquire_window(void* jni_env, void* surface_jobject) noexcept {
    JNIEnv* env = reinterpret_cast<JNIEnv*>(jni_env);
    if (!mbgl::android::theJVM) {
        env->GetJavaVM(&mbgl::android::theJVM);
    }
    return ANativeWindow_fromSurface(
        env,
        reinterpret_cast<jobject>(surface_jobject));
}

void mbgl_android_release_window(void* window) noexcept {
    ANativeWindow_release(reinterpret_cast<ANativeWindow*>(window));
}

// ── Android HTTP provider (implemented in http_file_source_android.cpp) ───────
extern "C" void mbgl_set_http_provider_impl(mbgl_http_provider_fn fn, void* userdata) noexcept;
extern "C" void mbgl_set_http_cancel_provider_impl(mbgl_http_cancel_fn fn, void* userdata) noexcept;
extern "C" void mbgl_http_respond_impl(uint64_t request_id,
                                   mbgl_http_error_t error,
                                   const char* error_message,
                                   int http_status,
                                   const char* data, int data_len,
                                   const char* etag,
                                   const char* modified,
                                   const char* expires,
                                   const char* cache_control,
                                   int no_content, int not_modified,
                                   int must_revalidate) noexcept;
extern "C" void mbgl_http_cancel_impl(uint64_t request_id) noexcept;

void mbgl_set_http_provider(mbgl_http_provider_fn fn, void* userdata) noexcept {
    mbgl_set_http_provider_impl(fn, userdata);
}

void mbgl_set_http_cancel_provider(mbgl_http_cancel_fn fn, void* userdata) noexcept {
    mbgl_set_http_cancel_provider_impl(fn, userdata);
}

void mbgl_http_respond(uint64_t request_id,
                       mbgl_http_error_t error,
                       const char* error_message,
                       int http_status,
                       const char* data, int data_len,
                       const char* etag,
                       const char* modified,
                       const char* expires,
                       const char* cache_control,
                       int no_content, int not_modified,
                       int must_revalidate) noexcept {
    mbgl_http_respond_impl(request_id, error, error_message, http_status,
                           data, data_len, etag, modified, expires,
                           cache_control, no_content, not_modified, must_revalidate);
}

void mbgl_http_cancel(uint64_t request_id) noexcept {
    mbgl_http_cancel_impl(request_id);
}
#endif
