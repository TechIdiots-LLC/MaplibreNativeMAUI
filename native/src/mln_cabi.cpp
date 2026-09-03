/**
 * mln_cabi.cpp — Implementation of the flat C ABI wrapper.
 *
 * Compiles as a plain C++ shared library (no C++/CLI, no JNI, no ObjC).
 * The platform-specific frontend (rendering surface binding) is handled by
 * PlatformFrontend, which has per-platform .cpp files included via CMake.
 */

#include "mln_cabi.h"
#include "mln_cabi_internal.hpp"

#include <mln/map/map.hpp>
#include <mln/map/map_options.hpp>
#include <mln/map/camera.hpp>
#include <mln/util/run_loop.hpp>
#include <mln/storage/resource_options.hpp>
#include <mln/style/style.hpp>
#include <mln/style/sources/geojson_source.hpp>
#include <mln/style/sources/vector_source.hpp>
#include <mln/style/sources/raster_source.hpp>
#include <mln/style/sources/raster_dem_source.hpp>
#include <mln/style/sources/image_source.hpp>
#include <mln/style/terrain.hpp>
#include <mln/style/layers/fill_layer.hpp>
#include <mln/style/layers/line_layer.hpp>
#include <mln/style/layers/circle_layer.hpp>
#include <mln/style/layers/symbol_layer.hpp>
#include <mln/style/layers/raster_layer.hpp>
#include <mln/style/layers/heatmap_layer.hpp>
#include <mln/style/layers/hillshade_layer.hpp>
#include <mln/style/layers/fill_extrusion_layer.hpp>
#include <mln/style/layers/background_layer.hpp>
#include <mln/style/layers/location_indicator_layer.hpp>
#include <mln/style/layers/color_relief_layer.hpp>
#include <mln/style/conversion/geojson.hpp>
#include <mln/style/conversion/geojson_options.hpp>
#include <mln/style/conversion/filter.hpp>
#include <mln/style/conversion/source.hpp>
#include <mln/style/conversion/layer.hpp>
#include <mln/storage/network_status.hpp>
#include <mln/util/rapidjson.hpp>
#include <mln/style/rapidjson_conversion.hpp>
#include <mln/map/map_observer.hpp>
#include <mln/style/image.hpp>
#include <mln/style/transition_options.hpp>
#include <mln/style/light.hpp>
#include <mln/util/image.hpp>
#include <mln/renderer/renderer.hpp>
#include <mln/util/geojson.hpp>
#include <mln/util/logging.hpp>

#include <mln/map/bound_options.hpp>
#include <mln/style/conversion/stringify.hpp>
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
    mln::Size     size,
    float          pixel_ratio,
    mln_render_fn render_callback,
    void*          render_userdata);

/* ─── Thread-local error string ─────────────────────────────────────────────── */
static thread_local std::string s_last_error;

static mln_status_t set_error(mln_status_t code, std::string msg) noexcept {
    s_last_error = std::move(msg);
    return code;
}

static mln_status_t set_native_error(const std::exception& e) noexcept {
    s_last_error = e.what();
    return MLN_NATIVE_ERROR;
}

const char* mln_get_last_error() noexcept {
    return s_last_error.c_str();
}

/* Bridges declared in mln_cabi_internal.hpp for sibling translation units
 * (mln_cabi_offline.cpp) — the helpers above stay static/TU-local. */
mln_status_t cabi_set_error(mln_status_t code, std::string msg) noexcept {
    return set_error(code, std::move(msg));
}
mln_status_t cabi_set_native_error(const std::exception& e) noexcept {
    return set_native_error(e);
}

/* ─── Log callback state ─────────────────────────────────────────────────────── */
static std::mutex       s_log_mutex;
static mln_log_fn      s_log_fn       = nullptr;
static void*            s_log_userdata = nullptr;

/** Custom mln::Log observer that forwards records to the C callback. */
class CabiLogObserver : public mln::Log::Observer {
public:
    bool onRecord(mln::EventSeverity severity,
                  mln::Event         event,
                  int64_t             /*code*/,
                  const std::string&  msg) override {
        std::lock_guard<std::mutex> lock(s_log_mutex);
        if (!s_log_fn) return false;

        mln_log_level_t level;
        switch (severity) {
            case mln::EventSeverity::Debug:   level = MLN_LOG_DEBUG;   break;
            case mln::EventSeverity::Info:    level = MLN_LOG_INFO;    break;
            case mln::EventSeverity::Warning: level = MLN_LOG_WARNING; break;
            case mln::EventSeverity::Error:   level = MLN_LOG_ERROR;   break;
            default:                           level = MLN_LOG_INFO;    break;
        }

        const char* category = mln::Enum<mln::Event>::toString(event);
        int consumed = s_log_fn(level, category ? category : "", msg.c_str(), s_log_userdata);
        return consumed != 0;
    }
};

static CabiLogObserver* s_log_observer = nullptr;

mln_status_t mln_install_log_callback(mln_log_fn fn, void* userdata) noexcept {
    try {
        std::lock_guard<std::mutex> lock(s_log_mutex);
        s_log_fn       = fn;
        s_log_userdata = userdata;
        if (fn && !s_log_observer) {
            s_log_observer = new CabiLogObserver();
            mln::Log::setObserver(std::unique_ptr<mln::Log::Observer>(s_log_observer));
        } else if (!fn) {
            mln::Log::removeObserver();
            s_log_observer = nullptr;
        }
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Network status ────────────────────────────────────────────────────────── */

mln_status_t mln_network_status_set(int online) noexcept {
    try {
        mln::NetworkStatus::Set(online ? mln::NetworkStatus::Status::Online
                                        : mln::NetworkStatus::Status::Offline);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

int mln_network_status_get() noexcept {
    return mln::NetworkStatus::Get() == mln::NetworkStatus::Status::Online ? 1 : 0;
}

/* ─── Internal structs ──────────────────────────────────────────────────────── */
/** Bridges all MapObserver virtual calls to the C mln_map_observer_fn. */
class CabiMapObserver : public mln::MapObserver {
public:
    mln_map_observer_fn fn = nullptr;
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
    void onDidFailLoadingMap(mln::MapLoadError /*err*/, const std::string& msg) override {
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
    void onSourceChanged(mln::style::Source& src) override {
        fire("onSourceChanged", src.getID().c_str());
    }
    void onDidBecomeIdle() override { fire("onDidBecomeIdle"); }
    void onStyleImageMissing(const std::string& id) override {
        fire("onStyleImageMissing", id.c_str());
    }
};

/* The public handle types are forward-declared in the header as
 * "struct mln_X_s".  Here we define them as type aliases so that the
 * internal CabiXxx types satisfy the ABI: we declare them as the same
 * struct by giving each concrete internal struct two names via typedef.
 * The simpler approach is just to reinterpret_cast at every boundary. */

struct CabiRunLoop {
    mln::util::RunLoop loop;
};
struct CabiMap {
    // Destruction order matters: map must die before frontend and observer.
    // unique_ptrs are destroyed in reverse declaration order, so declare
    // observer first so it is destroyed last.
    std::unique_ptr<CabiMapObserver>      observer;
    std::unique_ptr<PlatformFrontend>     frontend;
    std::unique_ptr<mln::Map>            map;
};

/* ─── Casting helpers ───────────────────────────────────────────────────────── */
/* The public handle types are opaque pointers to forward-declared structs.
 * We never define those structs; instead we reinterpret the pointer to/from
 * our internal types. */
template<typename T, typename H> static inline T* as(H* h) noexcept { return reinterpret_cast<T*>(h); }
template<typename H, typename T> static inline H* to(T* t) noexcept { return reinterpret_cast<H*>(t); }

/* Convenience aliases */
static inline CabiRunLoop*        rl_ptr(mln_runloop_t*  h) noexcept { return as<CabiRunLoop>(h); }
static inline PlatformFrontend*   fe_ptr(mln_frontend_t* h) noexcept { return as<PlatformFrontend>(h); }
static inline CabiMap*            map_ptr(mln_map_t*     h) noexcept { return as<CabiMap>(h); }
static inline mln::style::Style& style_ref(mln_style_t* h) noexcept { return *as<mln::style::Style>(h); }
static inline mln::style::Layer& layer_ref(mln_layer_t* h) noexcept { return *as<mln::style::Layer>(h); }
static inline mln::style::Source& source_ref(mln_source_t* h) noexcept { return *as<mln::style::Source>(h); }

/* Helper: safe C-string copy */
static inline std::string safe_str(const char* s) {
    return s ? std::string(s) : std::string{};
}

/* ─── RunLoop ───────────────────────────────────────────────────────────────── */

mln_runloop_t* mln_runloop_create() noexcept {
    try {
        return to<mln_runloop_t>(new CabiRunLoop{});
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_status_t mln_runloop_destroy(mln_runloop_t* rl) noexcept {
    if (!rl) return set_error(MLN_INVALID_ARG, "mln_runloop_destroy: null handle");
    try { delete rl_ptr(rl); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_runloop_run_once(mln_runloop_t* rl) noexcept {
    if (!rl) return set_error(MLN_INVALID_ARG, "mln_runloop_run_once: null handle");
    try { rl_ptr(rl)->loop.runOnce(); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Frontend ──────────────────────────────────────────────────────────────── */

const char* mln_get_render_backend() noexcept {
#if defined(MLN_RENDER_BACKEND_VULKAN)
    return "vulkan";
#elif defined(MLN_RENDER_BACKEND_METAL)
    return "metal";
#else
    return "opengl";
#endif
}

mln_frontend_t* mln_frontend_create(
    void*  surface_handle,
    void*  gl_context,
    int    width_px,
    int    height_px,
    float  pixel_ratio,
    mln_render_fn render_callback,
    void*  render_userdata) noexcept
{
    try {
        return to<mln_frontend_t>(createPlatformFrontend(
            surface_handle, gl_context,
            mln::Size{ static_cast<uint32_t>(width_px), static_cast<uint32_t>(height_px) },
            pixel_ratio,
            render_callback, render_userdata));
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_frontend_t* mln_frontend_create_gl(
    void*  surface_handle,
    void*  gl_context,
    int    width_px,
    int    height_px,
    float  pixel_ratio,
    mln_render_fn render_callback,
    void*  render_userdata) noexcept
{
    return mln_frontend_create(surface_handle, gl_context, width_px, height_px,
                                pixel_ratio, render_callback, render_userdata);
}

mln_status_t mln_frontend_destroy(mln_frontend_t* fe) noexcept {
    if (!fe) return set_error(MLN_INVALID_ARG, "mln_frontend_destroy: null handle");
    try { delete fe_ptr(fe); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_frontend_render(mln_frontend_t* fe) noexcept {
    if (!fe) return set_error(MLN_INVALID_ARG, "mln_frontend_render: null handle");
    try { fe_ptr(fe)->render(); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_frontend_set_size(mln_frontend_t* fe, int width_px, int height_px) noexcept {
    if (!fe) return set_error(MLN_INVALID_ARG, "mln_frontend_set_size: null handle");
    try {
        fe_ptr(fe)->setSize(mln::Size{ static_cast<uint32_t>(width_px), static_cast<uint32_t>(height_px) });
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

void* mln_frontend_get_native_view(mln_frontend_t* fe) noexcept {
    if (!fe) return nullptr;
    return fe_ptr(fe)->getNativeView();
}

mln_status_t mln_frontend_read_pixels(mln_frontend_t* fe, uint8_t* out_buf, size_t buf_len) noexcept {
    if (!fe || !out_buf) return set_error(MLN_INVALID_ARG, "mln_frontend_read_pixels: null arg");
    try {
        return fe_ptr(fe)->readPixels(out_buf, buf_len)
            ? MLN_OK
            : set_error(MLN_UNSUPPORTED, "mln_frontend_read_pixels: frontend has no CPU read-back");
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Map ───────────────────────────────────────────────────────────────────── */

static mln_map_t* map_create_impl(
    mln_frontend_t*  fe,
    const char*       cache_path,
    const char*       asset_path,
    const char*       api_key,
    uint64_t          max_cache_size_bytes,
    float             pixel_ratio,
    mln_map_observer_fn observer,
    void*             observer_userdata) noexcept
{
    if (!fe) { set_error(MLN_INVALID_ARG, "mln_map_create: null frontend"); return nullptr; }
    try {
        auto* cabi_fe  = fe_ptr(fe);
        auto* cabi_map = new CabiMap{};
        cabi_map->frontend  = std::unique_ptr<PlatformFrontend>(cabi_fe);
        cabi_map->observer  = std::make_unique<CabiMapObserver>();
        cabi_map->observer->fn = observer;
        cabi_map->observer->ud = observer_userdata;

        mln::ResourceOptions resOpts;
        if (cache_path) resOpts.withCachePath(cache_path);
        if (asset_path) resOpts.withAssetPath(asset_path);
        if (api_key && *api_key)     resOpts.withApiKey(api_key);
        if (max_cache_size_bytes)    resOpts.withMaximumCacheSize(max_cache_size_bytes);

        mln::MapOptions mapOpts;
        mapOpts.withMapMode(mln::MapMode::Continuous)
               .withConstrainMode(mln::ConstrainMode::HeightOnly)
               .withViewportMode(mln::ViewportMode::Default)
               .withSize(cabi_fe->getSize())
               .withPixelRatio(pixel_ratio);

        cabi_map->map = std::make_unique<mln::Map>(
            *cabi_fe,
            *cabi_map->observer,
            mapOpts,
            resOpts);

        return to<mln_map_t>(cabi_map);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_map_t* mln_map_create(
    mln_frontend_t*  fe,
    mln_runloop_t*   /*rl*/,
    const char*       cache_path,
    const char*       asset_path,
    float             pixel_ratio,
    mln_map_observer_fn observer,
    void*             observer_userdata) noexcept
{
    return map_create_impl(fe, cache_path, asset_path, nullptr, 0,
                           pixel_ratio, observer, observer_userdata);
}

mln_map_t* mln_map_create2(
    mln_frontend_t*  fe,
    mln_runloop_t*   /*rl*/,
    const char*       cache_path,
    const char*       asset_path,
    const char*       api_key,
    uint64_t          max_cache_size_bytes,
    float             pixel_ratio,
    mln_map_observer_fn observer,
    void*             observer_userdata) noexcept
{
    return map_create_impl(fe, cache_path, asset_path, api_key, max_cache_size_bytes,
                           pixel_ratio, observer, observer_userdata);
}

mln_status_t mln_map_destroy(mln_map_t* map) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_destroy: null handle");
    try {
        auto* m = map_ptr(map);
        m->map.reset();
        m->frontend.reset();
        m->observer.reset();
        delete m;
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_style_url(mln_map_t* map, const char* url) noexcept {
    if (!map || !url) return set_error(MLN_INVALID_ARG, "mln_map_set_style_url: null argument");
    try { map_ptr(map)->map->getStyle().loadURL(url); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_style_json(mln_map_t* map, const char* json) noexcept {
    if (!map || !json) return set_error(MLN_INVALID_ARG, "mln_map_set_style_json: null argument");
    try { map_ptr(map)->map->getStyle().loadJSON(json); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_size(mln_map_t* map, int width_px, int height_px) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_size: null handle");
    try {
        mln::Size sz{ static_cast<uint32_t>(width_px), static_cast<uint32_t>(height_px) };
        auto* m = map_ptr(map);
        m->map->setSize(sz);
        m->frontend->setSize(sz);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_jump_to(mln_map_t* map, double lat, double lon, double zoom, double bearing, double pitch) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_jump_to: null handle");
    try {
        mln::CameraOptions cam;
        cam.center  = mln::LatLng{ lat, lon };
        cam.zoom    = zoom;
        cam.bearing = bearing;
        cam.pitch   = pitch;
        map_ptr(map)->map->jumpTo(cam);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_ease_to(mln_map_t* map, double lat, double lon, double zoom, double bearing, double pitch, int64_t duration_ms) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_ease_to: null handle");
    try {
        mln::CameraOptions cam;
        cam.center  = mln::LatLng{ lat, lon };
        cam.zoom    = zoom;
        cam.bearing = bearing;
        cam.pitch   = pitch;
        mln::AnimationOptions anim{ mln::Duration(std::chrono::milliseconds(duration_ms)) };
        map_ptr(map)->map->easeTo(cam, anim);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

double mln_map_get_zoom(mln_map_t* map) noexcept {
    if (!map) return 0.0;
    return map_ptr(map)->map->getCameraOptions().zoom.value_or(0.0);
}

double mln_map_get_bearing(mln_map_t* map) noexcept {
    if (!map) return 0.0;
    return map_ptr(map)->map->getCameraOptions().bearing.value_or(0.0);
}

double mln_map_get_pitch(mln_map_t* map) noexcept {
    if (!map) return 0.0;
    return map_ptr(map)->map->getCameraOptions().pitch.value_or(0.0);
}

void mln_map_get_center(mln_map_t* map, double* out_lat, double* out_lon) noexcept {
    if (!map) { if (out_lat) *out_lat = 0.0; if (out_lon) *out_lon = 0.0; return; }
    auto cam = map_ptr(map)->map->getCameraOptions();
    if (cam.center) { if (out_lat) *out_lat = cam.center->latitude(); if (out_lon) *out_lon = cam.center->longitude(); }
    else            { if (out_lat) *out_lat = 0.0; if (out_lon) *out_lon = 0.0; }
}

mln_status_t mln_map_set_min_zoom(mln_map_t* map, double zoom) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_min_zoom: null handle");
    try { map_ptr(map)->map->setBounds(mln::BoundOptions{}.withMinZoom(zoom)); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_max_zoom(mln_map_t* map, double zoom) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_max_zoom: null handle");
    try { map_ptr(map)->map->setBounds(mln::BoundOptions{}.withMaxZoom(zoom)); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_trigger_repaint(mln_map_t* map) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_trigger_repaint: null handle");
    try { map_ptr(map)->map->triggerRepaint(); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_cancel_transitions(mln_map_t* map) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_cancel_transitions: null handle");
    try { map_ptr(map)->map->cancelTransitions(); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

int mln_map_is_fully_loaded(mln_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isFullyLoaded() ? 1 : 0;
}

/* ─── Debug overlays ────────────────────────────────────────────────────────── */

int mln_map_get_debug_options(mln_map_t* map) noexcept {
    if (!map) return 0;
    return static_cast<int>(map_ptr(map)->map->getDebug());
}

mln_status_t mln_map_set_debug_options(mln_map_t* map, int options) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_debug_options: null handle");
    try {
        map_ptr(map)->map->setDebug(static_cast<mln::MapDebugOptions>(options));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* Input — MapLibre internal camera manipulation via ScreenCoordinate transform */
mln_status_t mln_map_on_scroll(mln_map_t* map, double delta, double cx, double cy) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_on_scroll: null handle");
    try {
        auto* m = map_ptr(map);
        // delta is already normalized to ±1.0 per scroll tick (mouseWheelDelta/120).
        // Multiply by 0.5 to get ~0.5 zoom levels per tick, matching typical map feel.
        double zoom = m->map->getCameraOptions().zoom.value_or(0.0) + delta * 0.5;
        mln::CameraOptions cam;
        cam.zoom   = std::max(0.0, std::min(22.0, zoom));
        cam.anchor = mln::ScreenCoordinate{ cx, cy };
        m->map->jumpTo(cam);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_on_double_tap(mln_map_t* map, double x, double y) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_on_double_tap: null handle");
    try {
        auto* m = map_ptr(map);
        double zoom = m->map->getCameraOptions().zoom.value_or(0.0) + 1.0;
        mln::CameraOptions cam;
        cam.zoom   = zoom;
        cam.anchor = mln::ScreenCoordinate{ x, y };
        mln::AnimationOptions anim{ mln::Duration(std::chrono::milliseconds(300)) };
        m->map->easeTo(cam, anim);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

static thread_local mln::CameraOptions s_panStart;
static thread_local mln::ScreenCoordinate s_panAnchor;

mln_status_t mln_map_on_pan_start(mln_map_t* map, double x, double y) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_on_pan_start: null handle");
    s_panStart  = map_ptr(map)->map->getCameraOptions();
    s_panAnchor = { x, y };
    return MLN_OK;
}

mln_status_t mln_map_on_pan_move(mln_map_t* map, double dx, double dy) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_on_pan_move: null handle");
    try { map_ptr(map)->map->moveBy(mln::ScreenCoordinate{dx, dy}); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_on_pan_end(mln_map_t* /*map*/) noexcept {
    return MLN_OK;
}

mln_status_t mln_map_on_pinch(mln_map_t* map, double scale_factor, double cx, double cy) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_on_pinch: null handle");
    // scale_factor is the per-frame incremental ratio (e.g. 1.02 or 0.98).
    // Guard against zero/negative to prevent log2 producing -infinity or NaN.
    if (scale_factor <= 0.0) return MLN_OK;
    try {
        auto* m = map_ptr(map);
        double zoom = m->map->getCameraOptions().zoom.value_or(0.0) + std::log2(scale_factor);
        mln::CameraOptions cam;
        cam.zoom   = std::max(0.0, std::min(22.0, zoom));
        cam.anchor = mln::ScreenCoordinate{ cx, cy };
        m->map->jumpTo(cam);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Style ─────────────────────────────────────────────────────────────────── */

mln_style_t* mln_map_get_style(mln_map_t* map) noexcept {
    if (!map) return nullptr;
    return to<mln_style_t>(&map_ptr(map)->map->getStyle());
}

/* ─── Sources ───────────────────────────────────────────────────────────────── */

mln_source_t* mln_style_add_geojson_source(mln_style_t* st, const char* source_id) noexcept {
    if (!st || !source_id) { set_error(MLN_INVALID_ARG, "mln_style_add_geojson_source: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mln::style::GeoJSONSource>(safe_str(source_id));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mln_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_source_t* mln_style_add_geojson_source_url(mln_style_t* st, const char* source_id, const char* url) noexcept {
    if (!st || !source_id || !url) { set_error(MLN_INVALID_ARG, "mln_style_add_geojson_source_url: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mln::style::GeoJSONSource>(safe_str(source_id));
        src->setURL(safe_str(url));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mln_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_source_t* mln_style_add_geojson_source_options(mln_style_t* st,
                                                       const char* source_id,
                                                       const char* options_json) noexcept {
    if (!st || !source_id) { set_error(MLN_INVALID_ARG, "mln_style_add_geojson_source_options: null arg"); return nullptr; }
    try {
        using namespace mln::style::conversion;
        auto options = mln::style::GeoJSONOptions::defaultOptions();
        if (options_json && *options_json) {
            mln::JSDocument doc;
            doc.Parse<0>(options_json);
            if (doc.HasParseError()) {
                set_error(MLN_INVALID_ARG, "mln_style_add_geojson_source_options: JSON parse error");
                return nullptr;
            }
            const mln::JSValue& v = doc;
            Error err;
            auto converted = convert<mln::style::GeoJSONOptions>(Convertible(&v), err);
            if (!converted) {
                set_error(MLN_INVALID_ARG,
                          std::string("mln_style_add_geojson_source_options: ") + err.message);
                return nullptr;
            }
            options = mln::makeMutable<mln::style::GeoJSONOptions>(std::move(*converted));
        }
        auto src = std::make_unique<mln::style::GeoJSONSource>(safe_str(source_id), std::move(options));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mln_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_status_t mln_geojson_source_set_data(mln_source_t* src, const char* geojson) noexcept {
    if (!src || !geojson) return set_error(MLN_INVALID_ARG, "mln_geojson_source_set_data: null arg");
    try {
        auto* gs = as<mln::style::GeoJSONSource>(src);
        mln::style::conversion::Error err;
        auto result = mln::style::conversion::parseGeoJSON(safe_str(geojson), err);
        if (result) { gs->setGeoJSON(*result); return MLN_OK; }
        return set_error(MLN_INVALID_ARG, "mln_geojson_source_set_data: " + err.message);
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_geojson_source_set_url(mln_source_t* src, const char* url) noexcept {
    if (!src || !url) return set_error(MLN_INVALID_ARG, "mln_geojson_source_set_url: null arg");
    try { as<mln::style::GeoJSONSource>(src)->setURL(safe_str(url)); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_source_t* mln_style_add_vector_source(mln_style_t* st, const char* source_id, const char* url) noexcept {
    if (!st || !source_id) { set_error(MLN_INVALID_ARG, "mln_style_add_vector_source: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mln::style::VectorSource>(safe_str(source_id), safe_str(url));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mln_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_source_t* mln_style_add_raster_source(mln_style_t* st, const char* source_id, const char* url, int tile_size) noexcept {
    if (!st || !source_id) { set_error(MLN_INVALID_ARG, "mln_style_add_raster_source: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mln::style::RasterSource>(safe_str(source_id), safe_str(url), static_cast<uint16_t>(tile_size));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mln_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_source_t* mln_style_add_rasterdem_source(mln_style_t* st, const char* source_id, const char* url, int tile_size) noexcept {
    if (!st || !source_id) { set_error(MLN_INVALID_ARG, "mln_style_add_rasterdem_source: null arg"); return nullptr; }
    try {
        auto src = std::make_unique<mln::style::RasterDEMSource>(safe_str(source_id), safe_str(url), static_cast<uint16_t>(tile_size));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mln_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_source_t* mln_style_add_image_source(mln_style_t* st, const char* source_id, const char* url,
                                             double lat0, double lon0, double lat1, double lon1,
                                             double lat2, double lon2, double lat3, double lon3) noexcept
{
    if (!st || !source_id) { set_error(MLN_INVALID_ARG, "mln_style_add_image_source: null arg"); return nullptr; }
    try {
        std::array<mln::LatLng, 4> coords{
            mln::LatLng{lat0,lon0}, mln::LatLng{lat1,lon1},
            mln::LatLng{lat2,lon2}, mln::LatLng{lat3,lon3}
        };
        auto src = std::make_unique<mln::style::ImageSource>(safe_str(source_id), coords);
        src->setURL(safe_str(url));
        auto* raw = src.get();
        style_ref(st).addSource(std::move(src));
        return to<mln_source_t>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_status_t mln_style_remove_source(mln_style_t* st, const char* source_id) noexcept {
    if (!st || !source_id) return set_error(MLN_INVALID_ARG, "mln_style_remove_source: null arg");
    try { style_ref(st).removeSource(safe_str(source_id)); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

int mln_style_has_source(mln_style_t* st, const char* source_id) noexcept {
    if (!st || !source_id) return 0;
    return style_ref(st).getSource(safe_str(source_id)) != nullptr ? 1 : 0;
}

/* ─── Layers ────────────────────────────────────────────────────────────────── */

template<typename LayerT>
static mln_layer_t* add_layer(mln_style_t* st, const char* layer_id, const char* source_id, const char* before_id) noexcept {
    try {
        auto layer = std::make_unique<LayerT>(safe_str(layer_id), source_id ? safe_str(source_id) : "");
        auto* raw = layer.get();
        if (before_id) style_ref(st).addLayer(std::move(layer), safe_str(before_id));
        else           style_ref(st).addLayer(std::move(layer));
        return reinterpret_cast<mln_layer_t*>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

template<typename LayerT>
static mln_layer_t* add_layer_no_source(mln_style_t* st, const char* layer_id, const char* before_id) noexcept {
    try {
        auto layer = std::make_unique<LayerT>(safe_str(layer_id));
        auto* raw = layer.get();
        if (before_id) style_ref(st).addLayer(std::move(layer), safe_str(before_id));
        else           style_ref(st).addLayer(std::move(layer));
        return reinterpret_cast<mln_layer_t*>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

mln_layer_t* mln_style_add_fill_layer          (mln_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mln::style::FillLayer>(st,id,src,before); }
mln_layer_t* mln_style_add_line_layer          (mln_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mln::style::LineLayer>(st,id,src,before); }
mln_layer_t* mln_style_add_circle_layer        (mln_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mln::style::CircleLayer>(st,id,src,before); }
mln_layer_t* mln_style_add_symbol_layer        (mln_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mln::style::SymbolLayer>(st,id,src,before); }
mln_layer_t* mln_style_add_raster_layer        (mln_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mln::style::RasterLayer>(st,id,src,before); }
mln_layer_t* mln_style_add_heatmap_layer       (mln_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mln::style::HeatmapLayer>(st,id,src,before); }
mln_layer_t* mln_style_add_hillshade_layer     (mln_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mln::style::HillshadeLayer>(st,id,src,before); }
mln_layer_t* mln_style_add_fill_extrusion_layer(mln_style_t* st, const char* id, const char* src, const char* before) noexcept { return add_layer<mln::style::FillExtrusionLayer>(st,id,src,before); }
mln_layer_t* mln_style_add_background_layer    (mln_style_t* st, const char* id, const char* before) noexcept { return add_layer_no_source<mln::style::BackgroundLayer>(st,id,before); }
mln_layer_t* mln_style_add_location_indicator_layer(mln_style_t* st, const char* id, const char* before) noexcept { return add_layer_no_source<mln::style::LocationIndicatorLayer>(st,id,before); }

mln_status_t mln_style_remove_layer(mln_style_t* st, const char* layer_id) noexcept {
    if (!st || !layer_id) return set_error(MLN_INVALID_ARG, "mln_style_remove_layer: null arg");
    try { style_ref(st).removeLayer(safe_str(layer_id)); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

int mln_style_has_layer(mln_style_t* st, const char* layer_id) noexcept {
    if (!st || !layer_id) return 0;
    return style_ref(st).getLayer(safe_str(layer_id)) != nullptr ? 1 : 0;
}

mln_status_t mln_layer_set_source_layer(mln_layer_t* layer, const char* source_layer) noexcept {
    if (!layer || !source_layer) return set_error(MLN_INVALID_ARG, "mln_layer_set_source_layer: null arg");
    try {
        auto* l = as<mln::style::Layer>(layer);
        l->setSourceLayer(safe_str(source_layer));
        return MLN_OK;
    }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_layer_set_filter(mln_layer_t* layer, const char* filter_json) noexcept {
    if (!layer || !filter_json) return set_error(MLN_INVALID_ARG, "mln_layer_set_filter: null arg");
    try {
        mln::JSDocument doc;
        doc.Parse(filter_json);
        if (doc.HasParseError()) return set_error(MLN_INVALID_ARG, "mln_layer_set_filter: JSON parse error");
        mln::style::conversion::Error err;
        auto filter = mln::style::conversion::convert<mln::style::Filter>(doc, err);
        if (!filter) return set_error(MLN_INVALID_ARG, "mln_layer_set_filter: " + err.message);
        as<mln::style::Layer>(layer)->setFilter(*filter);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_layer_set_min_zoom(mln_layer_t* layer, float zoom) noexcept {
    if (!layer) return set_error(MLN_INVALID_ARG, "mln_layer_set_min_zoom: null handle");
    try { as<mln::style::Layer>(layer)->setMinZoom(zoom); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_layer_set_max_zoom(mln_layer_t* layer, float zoom) noexcept {
    if (!layer) return set_error(MLN_INVALID_ARG, "mln_layer_set_max_zoom: null handle");
    try { as<mln::style::Layer>(layer)->setMaxZoom(zoom); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_layer_set_visibility(mln_layer_t* layer, int visible) noexcept {
    if (!layer) return set_error(MLN_INVALID_ARG, "mln_layer_set_visibility: null handle");
    try {
        as<mln::style::Layer>(layer)->setVisibility(
            visible ? mln::style::VisibilityType::Visible : mln::style::VisibilityType::None);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_layer_set_paint_property(mln_layer_t* layer, const char* name, const char* value_json) noexcept {
    if (!layer || !name || !value_json) return set_error(MLN_INVALID_ARG, "mln_layer_set_paint_property: null arg");
    try {
        mln::JSDocument doc;
        doc.Parse(value_json);
        if (doc.HasParseError()) return set_error(MLN_INVALID_ARG, "mln_layer_set_paint_property: JSON parse error");
        const mln::JSValue& v = doc;
        as<mln::style::Layer>(layer)->setProperty(safe_str(name), mln::style::conversion::Convertible(&v));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_layer_set_layout_property(mln_layer_t* layer, const char* name, const char* value_json) noexcept {
    if (!layer || !name || !value_json) return set_error(MLN_INVALID_ARG, "mln_layer_set_layout_property: null arg");
    try {
        mln::JSDocument doc;
        doc.Parse(value_json);
        if (doc.HasParseError()) return set_error(MLN_INVALID_ARG, "mln_layer_set_layout_property: JSON parse error");
        const mln::JSValue& v = doc;
        as<mln::style::Layer>(layer)->setProperty(safe_str(name), mln::style::conversion::Convertible(&v));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Map – additional camera / bounds / projection ─────────────────────────── */

mln_status_t mln_map_fly_to(mln_map_t* map, double lat, double lon,
                               double zoom, double bearing, double pitch,
                               int64_t duration_ms) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_fly_to: null handle");
    try {
        mln::CameraOptions cam;
        cam.center  = mln::LatLng{ lat, lon };
        cam.zoom    = zoom;
        cam.bearing = bearing;
        cam.pitch   = pitch;
        mln::AnimationOptions anim{ mln::Duration(std::chrono::milliseconds(duration_ms)) };
        map_ptr(map)->map->flyTo(cam, anim);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Map – camera with edge padding ────────────────────────────────────────── */

/* Builds CameraOptions from the padded-variant arguments.  NaN zoom / bearing /
 * pitch fields are left unset so the current value is preserved. */
static mln::CameraOptions padded_camera(double lat, double lon,
                                         double zoom, double bearing, double pitch,
                                         double pad_top, double pad_left,
                                         double pad_bottom, double pad_right) {
    mln::CameraOptions cam;
    cam.center = mln::LatLng{ lat, lon };
    if (!std::isnan(zoom))    cam.zoom    = zoom;
    if (!std::isnan(bearing)) cam.bearing = bearing;
    if (!std::isnan(pitch))   cam.pitch   = pitch;
    cam.padding = mln::EdgeInsets{ pad_top, pad_left, pad_bottom, pad_right };
    return cam;
}

mln_status_t mln_map_jump_to_padded(mln_map_t* map,
                                        double lat, double lon,
                                        double zoom, double bearing, double pitch,
                                        double pad_top, double pad_left,
                                        double pad_bottom, double pad_right) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_jump_to_padded: null handle");
    try {
        map_ptr(map)->map->jumpTo(padded_camera(lat, lon, zoom, bearing, pitch,
                                                pad_top, pad_left, pad_bottom, pad_right));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_ease_to_padded(mln_map_t* map,
                                        double lat, double lon,
                                        double zoom, double bearing, double pitch,
                                        double pad_top, double pad_left,
                                        double pad_bottom, double pad_right,
                                        int64_t duration_ms) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_ease_to_padded: null handle");
    try {
        mln::AnimationOptions anim{ mln::Duration(std::chrono::milliseconds(duration_ms)) };
        map_ptr(map)->map->easeTo(padded_camera(lat, lon, zoom, bearing, pitch,
                                                pad_top, pad_left, pad_bottom, pad_right), anim);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_fly_to_padded(mln_map_t* map,
                                       double lat, double lon,
                                       double zoom, double bearing, double pitch,
                                       double pad_top, double pad_left,
                                       double pad_bottom, double pad_right,
                                       int64_t duration_ms) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_fly_to_padded: null handle");
    try {
        mln::AnimationOptions anim{ mln::Duration(std::chrono::milliseconds(duration_ms)) };
        map_ptr(map)->map->flyTo(padded_camera(lat, lon, zoom, bearing, pitch,
                                               pad_top, pad_left, pad_bottom, pad_right), anim);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_get_camera(mln_map_t* map,
                                    double pad_top, double pad_left,
                                    double pad_bottom, double pad_right,
                                    double* out_lat, double* out_lon,
                                    double* out_zoom,
                                    double* out_bearing,
                                    double* out_pitch) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_get_camera: null handle");
    try {
        std::optional<mln::EdgeInsets> padding;
        if (pad_top != 0 || pad_left != 0 || pad_bottom != 0 || pad_right != 0)
            padding = mln::EdgeInsets{ pad_top, pad_left, pad_bottom, pad_right };
        auto cam = map_ptr(map)->map->getCameraOptions(padding);
        if (out_lat)     *out_lat     = cam.center  ? cam.center->latitude()  : 0.0;
        if (out_lon)     *out_lon     = cam.center  ? cam.center->longitude() : 0.0;
        if (out_zoom)    *out_zoom    = cam.zoom    ? *cam.zoom    : 0.0;
        if (out_bearing) *out_bearing = cam.bearing ? *cam.bearing : 0.0;
        if (out_pitch)   *out_pitch   = cam.pitch   ? *cam.pitch   : 0.0;
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_scale_by(mln_map_t* map, double scale,
                                  double anchor_x, double anchor_y,
                                  int64_t duration_ms) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_scale_by: null handle");
    try {
        std::optional<mln::ScreenCoordinate> anchor;
        if (!std::isnan(anchor_x) && !std::isnan(anchor_y))
            anchor = mln::ScreenCoordinate{ anchor_x, anchor_y };
        mln::AnimationOptions anim;
        if (duration_ms > 0) anim.duration = mln::Duration(std::chrono::milliseconds(duration_ms));
        map_ptr(map)->map->scaleBy(scale, anchor, anim);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_bounds(mln_map_t* map,
                                   double lat_sw, double lon_sw,
                                   double lat_ne, double lon_ne,
                                   double min_zoom, double max_zoom,
                                   double min_pitch, double max_pitch) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_bounds: null handle");
    try {
        mln::BoundOptions opts;
        if (!std::isnan(lat_sw) && !std::isnan(lon_sw) &&
            !std::isnan(lat_ne) && !std::isnan(lon_ne)) {
            opts.withLatLngBounds(mln::LatLngBounds::hull(
                mln::LatLng{ lat_sw, lon_sw }, mln::LatLng{ lat_ne, lon_ne }));
        }
        if (!std::isnan(min_zoom))  opts.withMinZoom(min_zoom);
        if (!std::isnan(max_zoom))  opts.withMaxZoom(max_zoom);
        if (!std::isnan(min_pitch)) opts.withMinPitch(min_pitch);
        if (!std::isnan(max_pitch)) opts.withMaxPitch(max_pitch);
        map_ptr(map)->map->setBounds(opts);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_camera_for_bounds(mln_map_t* map,
                                          double lat_sw, double lon_sw,
                                          double lat_ne, double lon_ne,
                                          double pad_top,    double pad_left,
                                          double pad_bottom, double pad_right,
                                          double* out_lat, double* out_lon,
                                          double* out_zoom, double* out_bearing,
                                          double* out_pitch) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_camera_for_bounds: null handle");
    try {
        auto bounds  = mln::LatLngBounds::hull(mln::LatLng{ lat_sw, lon_sw },
                                                mln::LatLng{ lat_ne, lon_ne });
        mln::EdgeInsets padding{ pad_top, pad_left, pad_bottom, pad_right };
        auto cam = map_ptr(map)->map->cameraForLatLngBounds(bounds, padding);
        if (out_lat)     *out_lat     = cam.center  ? cam.center->latitude()  : 0.0;
        if (out_lon)     *out_lon     = cam.center  ? cam.center->longitude() : 0.0;
        if (out_zoom)    *out_zoom    = cam.zoom    ? *cam.zoom    : 0.0;
        if (out_bearing) *out_bearing = cam.bearing ? *cam.bearing : 0.0;
        if (out_pitch)   *out_pitch   = cam.pitch   ? *cam.pitch   : 0.0;
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

void mln_map_pixel_for_latlng(mln_map_t* map, double lat, double lon,
                                double* out_x, double* out_y) noexcept {
    if (!map || !out_x || !out_y) return;
    auto sc = map_ptr(map)->map->pixelForLatLng(mln::LatLng{ lat, lon });
    *out_x = sc.x;
    *out_y = sc.y;
}

void mln_map_latlng_for_pixel(mln_map_t* map, double x, double y,
                                double* out_lat, double* out_lon) noexcept {
    if (!map || !out_lat || !out_lon) return;
    auto ll = map_ptr(map)->map->latLngForPixel(mln::ScreenCoordinate{ x, y });
    *out_lat = ll.latitude();
    *out_lon = ll.longitude();
}

mln_status_t mln_map_set_projection_mode(mln_map_t* map, int axonometric,
                                            double x_skew, double y_skew) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_projection_mode: null handle");
    try {
        mln::ProjectionMode mode;
        mode.axonometric = (axonometric != 0);
        mode.xSkew = x_skew;
        mode.ySkew = y_skew;
        map_ptr(map)->map->setProjectionMode(mode);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Style – images ────────────────────────────────────────────────────────── */

mln_status_t mln_style_add_image(mln_style_t* st, const char* image_id,
                                    int width, int height, float pixel_ratio,
                                    int sdf, const uint8_t* rgba_premultiplied) noexcept {
    if (!st || !image_id || !rgba_premultiplied) return set_error(MLN_INVALID_ARG, "mln_style_add_image: null arg");
    try {
        mln::PremultipliedImage img(
            { static_cast<uint32_t>(width), static_cast<uint32_t>(height) },
            rgba_premultiplied,
            static_cast<size_t>(width) * static_cast<size_t>(height) * 4u);
        style_ref(st).addImage(std::make_unique<mln::style::Image>(
            safe_str(image_id), std::move(img), pixel_ratio, sdf != 0));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_style_remove_image(mln_style_t* st, const char* image_id) noexcept {
    if (!st || !image_id) return set_error(MLN_INVALID_ARG, "mln_style_remove_image: null arg");
    try { style_ref(st).removeImage(safe_str(image_id)); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

char* mln_style_get_json(mln_style_t* st) noexcept {
    if (!st) return nullptr;
    try {
        std::string json = style_ref(st).getJSON();
        char* result = new char[json.size() + 1];
        std::copy(json.begin(), json.end(), result);
        result[json.size()] = '\0';
        return result;
    } catch (...) { return nullptr; }
}

mln_status_t mln_style_set_transition(mln_style_t* st, int64_t duration_ms, int64_t delay_ms) noexcept {
    if (!st) return set_error(MLN_INVALID_ARG, "mln_style_set_transition: null handle");
    try {
        mln::style::TransitionOptions opts;
        opts.duration = mln::Duration(std::chrono::milliseconds(duration_ms));
        opts.delay    = mln::Duration(std::chrono::milliseconds(delay_ms));
        style_ref(st).setTransitionOptions(opts);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_style_set_light_property(mln_style_t* st, const char* name, const char* value_json) noexcept {
    if (!st || !name || !value_json) return set_error(MLN_INVALID_ARG, "mln_style_set_light_property: null arg");
    try {
        auto* light = style_ref(st).getLight();
        if (!light) return set_error(MLN_INVALID_STATE, "mln_style_set_light_property: no light in style");
        mln::JSDocument doc;
        doc.Parse(value_json);
        if (doc.HasParseError()) return set_error(MLN_INVALID_ARG, "mln_style_set_light_property: JSON parse error");
        const mln::JSValue& v = doc;
        light->setProperty(safe_str(name), mln::style::conversion::Convertible(&v));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Layers – additional types ─────────────────────────────────────────────── */

mln_layer_t* mln_style_add_color_relief_layer(mln_style_t* st, const char* id,
                                                 const char* src, const char* before) noexcept {
    return add_layer<mln::style::ColorReliefLayer>(st, id, src, before);
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

static char* features_to_json(std::vector<mln::Feature>&& features) {
    mln::FeatureCollection fc(features.begin(), features.end());
    std::string json = mapbox::geojson::stringify(mln::GeoJSON{ fc });
    char* result = new char[json.size() + 1];
    std::copy(json.begin(), json.end(), result);
    result[json.size()] = '\0';
    return result;
}

char* mln_map_query_rendered_features_at_point(mln_map_t* map, double x, double y,
                                                 const char* layer_ids) noexcept {
    if (!map) return nullptr;
    try {
        auto* m        = map_ptr(map);
        auto* renderer = m->frontend->getRenderer();
        if (!renderer) return nullptr;
        mln::RenderedQueryOptions opts;
        auto ids = split_layer_ids(layer_ids);
        if (!ids.empty()) opts.layerIDs = ids;
        auto features = renderer->queryRenderedFeatures(mln::ScreenCoordinate{ x, y }, opts);
        return features_to_json(std::move(features));
    } catch (...) { return nullptr; }
}

char* mln_map_query_rendered_features_in_box(mln_map_t* map,
                                               double x1, double y1,
                                               double x2, double y2,
                                               const char* layer_ids) noexcept {
    if (!map) return nullptr;
    try {
        auto* m        = map_ptr(map);
        auto* renderer = m->frontend->getRenderer();
        if (!renderer) return nullptr;
        mln::RenderedQueryOptions opts;
        auto ids = split_layer_ids(layer_ids);
        if (!ids.empty()) opts.layerIDs = ids;
        mln::ScreenBox box{ { x1, y1 }, { x2, y2 } };
        auto features = renderer->queryRenderedFeatures(box, opts);
        return features_to_json(std::move(features));
    } catch (...) { return nullptr; }
}

void mln_free_string(char* str) noexcept {
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

mln_status_t mln_map_latlng_bounds_for_camera(mln_map_t* map,
                                                  double* out_lat_sw, double* out_lon_sw,
                                                  double* out_lat_ne, double* out_lon_ne) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_latlng_bounds_for_camera: null handle");
    try {
        auto* m      = map_ptr(map);
        auto  bounds = m->map->latLngBoundsForCamera(m->map->getCameraOptions());
        if (out_lat_sw) *out_lat_sw = bounds.southwest().latitude();
        if (out_lon_sw) *out_lon_sw = bounds.southwest().longitude();
        if (out_lat_ne) *out_lat_ne = bounds.northeast().latitude();
        if (out_lon_ne) *out_lon_ne = bounds.northeast().longitude();
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Memory / debug ────────────────────────────────────────────────────────── */

mln_status_t mln_map_reduce_memory_use(mln_map_t* map) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_reduce_memory_use: null handle");
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (renderer) renderer->reduceMemoryUse();
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_dump_debug_logs(mln_map_t* map) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_dump_debug_logs: null handle");
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (renderer) renderer->dumpDebugLogs();
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Feature state helpers ─────────────────────────────────────────────────── */

static mln::Value jsValueToMbglValue(const mln::JSValue& v) {
    if (v.IsBool())   return v.GetBool();
    if (v.IsInt64())  return v.GetInt64();
    if (v.IsUint64()) return static_cast<int64_t>(v.GetUint64());
    if (v.IsDouble()) return v.GetDouble();
    if (v.IsString()) return std::string{v.GetString(), v.GetStringLength()};
    if (v.IsArray()) {
        std::vector<mln::Value> arr;
        arr.reserve(v.Size());
        for (const auto& elem : v.GetArray())
            arr.push_back(jsValueToMbglValue(elem));
        return arr;
    }
    if (v.IsObject()) {
        mln::PropertyMap obj;
        for (const auto& m : v.GetObject())
            obj[std::string{m.name.GetString(), m.name.GetStringLength()}] = jsValueToMbglValue(m.value);
        return obj;
    }
    return mln::NullValue{};
}

static mln::FeatureState jsonToFeatureState(const char* json) {
    mln::FeatureState state;
    if (!json || !*json) return state;
    mln::JSDocument doc;
    doc.Parse(json);
    if (doc.HasParseError() || !doc.IsObject()) return state;
    for (const auto& m : doc.GetObject())
        state[std::string{m.name.GetString(), m.name.GetStringLength()}] = jsValueToMbglValue(m.value);
    return state;
}

static void writeValue(const mln::Value& v, rapidjson::Writer<rapidjson::StringBuffer>& w) {
    v.match(
        [&](const mln::NullValue&)                { w.Null(); },
        [&](bool b)                                { w.Bool(b); },
        [&](uint64_t u)                            { w.Uint64(u); },
        [&](int64_t i)                             { w.Int64(i); },
        [&](double d)                              { w.Double(d); },
        [&](const std::string& s) {
            w.String(s.data(), static_cast<rapidjson::SizeType>(s.size()));
        },
        [&](const std::vector<mln::Value>& arr) {
            w.StartArray();
            for (const auto& e : arr) writeValue(e, w);
            w.EndArray();
        },
        [&](const mln::PropertyMap& obj) {
            w.StartObject();
            for (const auto& [k, v2] : obj) {
                w.Key(k.data(), static_cast<rapidjson::SizeType>(k.size()));
                writeValue(v2, w);
            }
            w.EndObject();
        }
    );
}

static char* featureStateToJson(const mln::FeatureState& state) {
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

mln_status_t mln_map_set_feature_state(mln_map_t* map,
                                           const char* source_id,
                                           const char* source_layer_id,
                                           const char* feature_id,
                                           const char* state_json) noexcept {
    if (!map || !source_id || !feature_id || !state_json)
        return set_error(MLN_INVALID_ARG, "mln_map_set_feature_state: null arg");
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return set_error(MLN_INVALID_STATE, "mln_map_set_feature_state: renderer not ready");
        std::optional<std::string> layerId =
            (source_layer_id && *source_layer_id) ? std::optional<std::string>{source_layer_id} : std::nullopt;
        renderer->setFeatureState(safe_str(source_id), layerId,
                                  safe_str(feature_id), jsonToFeatureState(state_json));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

char* mln_map_get_feature_state(mln_map_t* map,
                                   const char* source_id,
                                   const char* source_layer_id,
                                   const char* feature_id) noexcept {
    if (!map || !source_id || !feature_id) return nullptr;
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return nullptr;
        std::optional<std::string> layerId =
            (source_layer_id && *source_layer_id) ? std::optional<std::string>{source_layer_id} : std::nullopt;
        mln::FeatureState state;
        renderer->getFeatureState(state, safe_str(source_id), layerId, safe_str(feature_id));
        return featureStateToJson(state);
    } catch (...) { return nullptr; }
}

mln_status_t mln_map_remove_feature_state(mln_map_t* map,
                                              const char* source_id,
                                              const char* source_layer_id,
                                              const char* feature_id,
                                              const char* state_key) noexcept {
    if (!map || !source_id)
        return set_error(MLN_INVALID_ARG, "mln_map_remove_feature_state: null source_id");
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return set_error(MLN_INVALID_STATE, "mln_map_remove_feature_state: renderer not ready");
        std::optional<std::string> layerId =
            (source_layer_id && *source_layer_id) ? std::optional<std::string>{source_layer_id} : std::nullopt;
        std::optional<std::string> featureIdOpt =
            (feature_id && *feature_id) ? std::optional<std::string>{feature_id} : std::nullopt;
        std::optional<std::string> stateKeyOpt =
            (state_key && *state_key) ? std::optional<std::string>{state_key} : std::nullopt;
        renderer->removeFeatureState(safe_str(source_id), layerId, featureIdOpt, stateKeyOpt);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Source-feature / feature-extension queries ────────────────────────────── */

char* mln_map_query_source_features(mln_map_t* map,
                                       const char* source_id,
                                       const char* source_layer_ids,
                                       const char* filter_json) noexcept {
    if (!map || !source_id) return nullptr;
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return nullptr;

        mln::SourceQueryOptions opts;
        auto layers = split_layer_ids(source_layer_ids);
        if (!layers.empty()) opts.sourceLayers = layers;
        if (filter_json && *filter_json) {
            mln::JSDocument doc;
            doc.Parse<0>(filter_json);
            if (doc.HasParseError()) return nullptr;
            const mln::JSValue& v = doc;
            mln::style::conversion::Error err;
            auto filter = mln::style::conversion::convert<mln::style::Filter>(
                mln::style::conversion::Convertible(&v), err);
            if (!filter) { set_error(MLN_INVALID_ARG, "mln_map_query_source_features: " + err.message); return nullptr; }
            opts.filter = std::move(*filter);
        }
        auto features = renderer->querySourceFeatures(safe_str(source_id), opts);
        return features_to_json(std::move(features));
    } catch (...) { return nullptr; }
}

char* mln_map_query_feature_extensions(mln_map_t* map,
                                          const char* source_id,
                                          const char* feature_json,
                                          const char* extension,
                                          const char* extension_field,
                                          const char* args_json) noexcept {
    if (!map || !source_id || !feature_json || !extension || !extension_field) return nullptr;
    try {
        auto* renderer = map_ptr(map)->frontend->getRenderer();
        if (!renderer) return nullptr;

        mln::style::conversion::Error err;
        auto geojson = mln::style::conversion::parseGeoJSON(safe_str(feature_json), err);
        if (!geojson) { set_error(MLN_INVALID_ARG, "mln_map_query_feature_extensions: " + err.message); return nullptr; }
        mln::Feature feature;
        if (geojson->is<mapbox::geojson::feature>()) {
            feature = mln::Feature{ geojson->get<mapbox::geojson::feature>() };
        } else if (geojson->is<mapbox::geojson::feature_collection>() &&
                   !geojson->get<mapbox::geojson::feature_collection>().empty()) {
            feature = mln::Feature{ geojson->get<mapbox::geojson::feature_collection>().front() };
        } else {
            set_error(MLN_INVALID_ARG, "mln_map_query_feature_extensions: feature_json must be a GeoJSON Feature");
            return nullptr;
        }

        std::optional<std::map<std::string, mln::Value>> args;
        if (args_json && *args_json) {
            mln::JSDocument doc;
            doc.Parse<0>(args_json);
            if (!doc.HasParseError() && doc.IsObject()) {
                std::map<std::string, mln::Value> parsed;
                for (const auto& m : doc.GetObject())
                    parsed[std::string{m.name.GetString(), m.name.GetStringLength()}] = jsValueToMbglValue(m.value);
                args = std::move(parsed);
            }
        }

        auto result = renderer->queryFeatureExtensions(
            safe_str(source_id), feature, safe_str(extension), safe_str(extension_field), args);

        if (result.is<mln::FeatureCollection>()) {
            std::string json = mapbox::geojson::stringify(
                mln::GeoJSON{ result.get<mln::FeatureCollection>() });
            return dup_string(json);
        }
        rapidjson::StringBuffer buf;
        rapidjson::Writer<rapidjson::StringBuffer> writer(buf);
        writeValue(result.get<mln::Value>(), writer);
        return dup_string(std::string(buf.GetString(), buf.GetSize()));
    } catch (...) { return nullptr; }
}

/* ─── Style – generic JSON add ──────────────────────────────────────────────── */

mln_status_t mln_style_add_source_json(mln_style_t* st,
                                           const char* source_id,
                                           const char* source_json) noexcept {
    if (!st || !source_id || !source_json)
        return set_error(MLN_INVALID_ARG, "mln_style_add_source_json: null arg");
    try {
        using namespace mln::style::conversion;
        mln::JSDocument doc;
        doc.Parse<0>(source_json);
        if (doc.HasParseError())
            return set_error(MLN_INVALID_ARG, "mln_style_add_source_json: JSON parse error");
        const mln::JSValue& v = doc;
        Error err;
        auto source = convert<std::unique_ptr<mln::style::Source>>(
            Convertible(&v), err, safe_str(source_id));
        if (!source)
            return set_error(MLN_INVALID_ARG, std::string("mln_style_add_source_json: ") + err.message);
        style_ref(st).addSource(std::move(*source));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_layer_t* mln_style_add_layer_json(mln_style_t* st,
                                          const char* layer_json,
                                          const char* before_id) noexcept {
    if (!st || !layer_json) { set_error(MLN_INVALID_ARG, "mln_style_add_layer_json: null arg"); return nullptr; }
    try {
        using namespace mln::style::conversion;
        mln::JSDocument doc;
        doc.Parse<0>(layer_json);
        if (doc.HasParseError()) {
            set_error(MLN_INVALID_ARG, "mln_style_add_layer_json: JSON parse error");
            return nullptr;
        }
        const mln::JSValue& v = doc;
        Error err;
        auto layer = convert<std::unique_ptr<mln::style::Layer>>(Convertible(&v), err);
        if (!layer) {
            set_error(MLN_INVALID_ARG, std::string("mln_style_add_layer_json: ") + err.message);
            return nullptr;
        }
        auto* raw = layer->get();
        if (before_id && *before_id) style_ref(st).addLayer(std::move(*layer), safe_str(before_id));
        else                         style_ref(st).addLayer(std::move(*layer));
        return reinterpret_cast<mln_layer_t*>(raw);
    } catch (const std::exception& e) { set_native_error(e); return nullptr; }
}

/* ─── 3D terrain ────────────────────────────────────────────────────────────── */

mln_status_t mln_style_set_terrain(mln_style_t* st, const char* source_id, float exaggeration) noexcept {
    if (!st || !source_id) return set_error(MLN_INVALID_ARG, "mln_style_set_terrain: null arg");
    try {
        style_ref(st).setTerrain(std::make_unique<mln::style::Terrain>(safe_str(source_id), exaggeration));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_style_remove_terrain(mln_style_t* st) noexcept {
    if (!st) return set_error(MLN_INVALID_ARG, "mln_style_remove_terrain: null handle");
    try {
        style_ref(st).setTerrain(nullptr);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

int mln_style_is_terrain_enabled(mln_style_t* st) noexcept {
    if (!st) return 0;
    try {
        return style_ref(st).getTerrain() ? 1 : 0;
    } catch (const std::exception&) { return 0; }
}

mln_status_t mln_map_set_terrain_load_mode(mln_map_t* map, mln_terrain_load_mode_t mode) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_terrain_load_mode: null handle");
    try {
        map_ptr(map)->map->setTerrainLoadMode(static_cast<mln::TerrainLoadMode>(mode));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_terrain_load_mode_t mln_map_get_terrain_load_mode(mln_map_t* map) noexcept {
    if (!map) return MLN_TERRAIN_LOAD_QUALITY;
    try {
        return static_cast<mln_terrain_load_mode_t>(map_ptr(map)->map->getTerrainLoadMode());
    } catch (const std::exception&) { return MLN_TERRAIN_LOAD_QUALITY; }
}

/* ─── Gesture helpers ───────────────────────────────────────────────────────── */

mln_status_t mln_map_set_gesture_in_progress(mln_map_t* map, int in_progress) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_gesture_in_progress: null handle");
    try { map_ptr(map)->map->setGestureInProgress(in_progress != 0); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

int mln_map_is_gesture_in_progress(mln_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isGestureInProgress() ? 1 : 0;
}

int mln_map_is_rotating(mln_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isRotating() ? 1 : 0;
}

int mln_map_is_scaling(mln_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isScaling() ? 1 : 0;
}

int mln_map_is_panning(mln_map_t* map) noexcept {
    if (!map) return 0;
    return map_ptr(map)->map->isPanning() ? 1 : 0;
}

mln_status_t mln_map_move_by(mln_map_t* map, double dx, double dy, int64_t duration_ms) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_move_by: null handle");
    try {
        mln::AnimationOptions anim;
        if (duration_ms > 0) anim.duration = mln::Duration(std::chrono::milliseconds(duration_ms));
        map_ptr(map)->map->moveBy({dx, dy}, anim);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_rotate_by(mln_map_t* map, double x0, double y0, double x1, double y1) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_rotate_by: null handle");
    try { map_ptr(map)->map->rotateBy({x0, y0}, {x1, y1}); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_pitch_by(mln_map_t* map, double delta_degrees, int64_t duration_ms) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_pitch_by: null handle");
    try {
        mln::AnimationOptions anim;
        if (duration_ms > 0) anim.duration = mln::Duration(std::chrono::milliseconds(duration_ms));
        map_ptr(map)->map->pitchBy(delta_degrees, anim);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Map option setters ─────────────────────────────────────────────────────── */

mln_status_t mln_map_set_north_orientation(mln_map_t* map, int orientation) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_north_orientation: null handle");
    try {
        map_ptr(map)->map->setNorthOrientation(static_cast<mln::NorthOrientation>(orientation));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_constrain_mode(mln_map_t* map, int mode) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_constrain_mode: null handle");
    try {
        map_ptr(map)->map->setConstrainMode(static_cast<mln::ConstrainMode>(mode));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_viewport_mode(mln_map_t* map, int mode) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_viewport_mode: null handle");
    try {
        map_ptr(map)->map->setViewportMode(static_cast<mln::ViewportMode>(mode));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Bounds read-back ───────────────────────────────────────────────────────── */

void mln_map_get_bounds(mln_map_t* map,
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

mln_status_t mln_map_set_prefetch_zoom_delta(mln_map_t* map, int delta) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_prefetch_zoom_delta: null handle");
    try {
        int clamped = delta < 0 ? 0 : (delta > 255 ? 255 : delta);
        map_ptr(map)->map->setPrefetchZoomDelta(static_cast<uint8_t>(clamped));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

int mln_map_get_prefetch_zoom_delta(mln_map_t* map) noexcept {
    if (!map) return 0;
    return static_cast<int>(map_ptr(map)->map->getPrefetchZoomDelta());
}

mln_status_t mln_map_set_tile_lod_min_radius(mln_map_t* map, double radius) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_tile_lod_min_radius: null handle");
    try { map_ptr(map)->map->setTileLodMinRadius(radius); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_tile_lod_scale(mln_map_t* map, double scale) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_tile_lod_scale: null handle");
    try { map_ptr(map)->map->setTileLodScale(scale); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_tile_lod_pitch_threshold(mln_map_t* map, double threshold_rad) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_tile_lod_pitch_threshold: null handle");
    try { map_ptr(map)->map->setTileLodPitchThreshold(threshold_rad); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_tile_lod_zoom_shift(mln_map_t* map, double shift) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_tile_lod_zoom_shift: null handle");
    try { map_ptr(map)->map->setTileLodZoomShift(shift); return MLN_OK; }
    catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_set_tile_lod_mode(mln_map_t* map, int mode) noexcept {
    if (!map) return set_error(MLN_INVALID_ARG, "mln_map_set_tile_lod_mode: null handle");
    try {
        map_ptr(map)->map->setTileLodMode(static_cast<mln::TileLodMode>(mode));
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Camera for lat/lng point set ──────────────────────────────────────────── */

mln_status_t mln_map_camera_for_latlngs(mln_map_t* map,
                                           const double* latlngs, int count,
                                           double pad_top, double pad_left,
                                           double pad_bottom, double pad_right,
                                           double* out_lat, double* out_lon,
                                           double* out_zoom, double* out_bearing,
                                           double* out_pitch) noexcept {
    if (!map || !latlngs) return set_error(MLN_INVALID_ARG, "mln_map_camera_for_latlngs: null arg");
    try {
        std::vector<mln::LatLng> pts;
        pts.reserve(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i)
            pts.emplace_back(latlngs[i * 2], latlngs[i * 2 + 1]);
        mln::EdgeInsets padding{ pad_top, pad_left, pad_bottom, pad_right };
        auto cam = map_ptr(map)->map->cameraForLatLngs(pts, padding);
        if (out_lat)     *out_lat     = cam.center ? cam.center->latitude()  : kNaN;
        if (out_lon)     *out_lon     = cam.center ? cam.center->longitude() : kNaN;
        if (out_zoom)    *out_zoom    = cam.zoom.value_or(kNaN);
        if (out_bearing) *out_bearing = cam.bearing.value_or(kNaN);
        if (out_pitch)   *out_pitch   = cam.pitch.value_or(kNaN);
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Batch projection ───────────────────────────────────────────────────────── */

mln_status_t mln_map_pixels_for_latlngs(mln_map_t* map,
                                           const double* latlngs, int count,
                                           double* out_xy) noexcept {
    if (!map || !latlngs || !out_xy) return set_error(MLN_INVALID_ARG, "mln_map_pixels_for_latlngs: null arg");
    try {
        std::vector<mln::LatLng> pts;
        pts.reserve(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i)
            pts.emplace_back(latlngs[i * 2], latlngs[i * 2 + 1]);
        auto pixels = map_ptr(map)->map->pixelsForLatLngs(pts);
        for (size_t i = 0; i < pixels.size(); ++i) {
            out_xy[i * 2]     = pixels[i].x;
            out_xy[i * 2 + 1] = pixels[i].y;
        }
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

mln_status_t mln_map_latlngs_for_pixels(mln_map_t* map,
                                           const double* xy, int count,
                                           double* out_ll) noexcept {
    if (!map || !xy || !out_ll) return set_error(MLN_INVALID_ARG, "mln_map_latlngs_for_pixels: null arg");
    try {
        std::vector<mln::ScreenCoordinate> pts;
        pts.reserve(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i)
            pts.emplace_back(xy[i * 2], xy[i * 2 + 1]);
        auto latlngs = map_ptr(map)->map->latLngsForPixels(pts);
        for (size_t i = 0; i < latlngs.size(); ++i) {
            out_ll[i * 2]     = latlngs[i].latitude();
            out_ll[i * 2 + 1] = latlngs[i].longitude();
        }
        return MLN_OK;
    } catch (const std::exception& e) { return set_native_error(e); }
}

/* ─── Style enumeration ──────────────────────────────────────────────────────── */

char* mln_style_get_url(mln_style_t* st) noexcept {
    if (!st) return nullptr;
    try { return dup_string(style_ref(st).getURL()); }
    catch (...) { return nullptr; }
}

char* mln_style_get_name(mln_style_t* st) noexcept {
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

char* mln_style_get_source_ids(mln_style_t* st) noexcept {
    if (!st) return nullptr;
    try {
        std::vector<std::string> ids;
        for (auto* src : style_ref(st).getSources()) ids.push_back(src->getID());
        return ids_to_json(ids);
    } catch (...) { return nullptr; }
}

char* mln_style_get_layer_ids(mln_style_t* st) noexcept {
    if (!st) return nullptr;
    try {
        std::vector<std::string> ids;
        for (auto* layer : style_ref(st).getLayers()) ids.push_back(layer->getID());
        return ids_to_json(ids);
    } catch (...) { return nullptr; }
}

mln_layer_t* mln_style_get_layer(mln_style_t* st, const char* layer_id) noexcept {
    if (!st || !layer_id) return nullptr;
    return to<mln_layer_t>(style_ref(st).getLayer(safe_str(layer_id)));
}

mln_source_t* mln_style_get_source(mln_style_t* st, const char* source_id) noexcept {
    if (!st || !source_id) return nullptr;
    return to<mln_source_t>(style_ref(st).getSource(safe_str(source_id)));
}

char* mln_source_get_attribution(mln_source_t* src) noexcept {
    if (!src) return nullptr;
    try {
        const auto& attr = as<mln::style::Source>(src)->getAttribution();
        if (!attr) return nullptr;
        return dup_string(*attr);
    } catch (...) { return nullptr; }
}

/* ─── Layer read-back ────────────────────────────────────────────────────────── */

static char* style_property_to_json(const mln::style::StyleProperty& prop) {
    if (prop.getKind() == mln::style::StyleProperty::Kind::Undefined)
        return nullptr;
    rapidjson::StringBuffer sb;
    rapidjson::Writer<rapidjson::StringBuffer,
                       rapidjson::UTF8<>, rapidjson::UTF8<>,
                       rapidjson::CrtAllocator> writer(sb);
    mln::style::conversion::stringify(writer, prop.getValue());
    return dup_string(std::string(sb.GetString(), sb.GetSize()));
}

char* mln_layer_get_paint_property(mln_layer_t* layer, const char* name) noexcept {
    if (!layer || !name) return nullptr;
    try { return style_property_to_json(as<mln::style::Layer>(layer)->getProperty(safe_str(name))); }
    catch (...) { return nullptr; }
}

char* mln_layer_get_layout_property(mln_layer_t* layer, const char* name) noexcept {
    if (!layer || !name) return nullptr;
    try { return style_property_to_json(as<mln::style::Layer>(layer)->getProperty(safe_str(name))); }
    catch (...) { return nullptr; }
}

int mln_layer_get_visibility(mln_layer_t* layer) noexcept {
    if (!layer) return 1;
    return as<mln::style::Layer>(layer)->getVisibility()
               == mln::style::VisibilityType::Visible ? 1 : 0;
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
// MapLibre Android SDK normally uses to populate mln::android::theJVM, so
// every background thread that calls attachThread() (e.g. the RunLoop's
// "Alarm" thread) aborts on assert(vm != nullptr) in jni.cpp. Capture the
// JavaVM here — the first JNIEnv we're handed, on the surface-creation path
// that always runs before the RunLoop/Alarm thread is spawned.
namespace mln { namespace android {
extern JavaVM* theJVM;
}} // namespace mln::android

void* mln_android_acquire_window(void* jni_env, void* surface_jobject) noexcept {
    JNIEnv* env = reinterpret_cast<JNIEnv*>(jni_env);
    if (!mln::android::theJVM) {
        env->GetJavaVM(&mln::android::theJVM);
    }
    return ANativeWindow_fromSurface(
        env,
        reinterpret_cast<jobject>(surface_jobject));
}

void mln_android_release_window(void* window) noexcept {
    ANativeWindow_release(reinterpret_cast<ANativeWindow*>(window));
}

// ── Host HTTP provider (implemented in http_provider.cpp) ────────────────────
extern "C" void mln_set_http_provider_impl(mln_http_provider_fn fn, void* userdata) noexcept;
extern "C" void mln_set_http_cancel_provider_impl(mln_http_cancel_fn fn, void* userdata) noexcept;
extern "C" void mln_http_provider_claim_prefix_impl(const char* url_prefix) noexcept;
extern "C" void mln_http_provider_clear_claims_impl(void) noexcept;
extern "C" void mln_http_respond_impl(uint64_t request_id,
                                   mln_http_error_t error,
                                   const char* error_message,
                                   int http_status,
                                   const char* data, int data_len,
                                   const char* etag,
                                   const char* modified,
                                   const char* expires,
                                   const char* cache_control,
                                   int no_content, int not_modified,
                                   int must_revalidate) noexcept;
extern "C" void mln_http_cancel_impl(uint64_t request_id) noexcept;

void mln_set_http_provider(mln_http_provider_fn fn, void* userdata) noexcept {
    mln_set_http_provider_impl(fn, userdata);
}

void mln_set_http_cancel_provider(mln_http_cancel_fn fn, void* userdata) noexcept {
    mln_set_http_cancel_provider_impl(fn, userdata);
}

void mln_http_provider_claim_prefix(const char* url_prefix) noexcept {
    mln_http_provider_claim_prefix_impl(url_prefix);
}

void mln_http_provider_clear_claims(void) noexcept {
    mln_http_provider_clear_claims_impl();
}

void mln_http_respond(uint64_t request_id,
                       mln_http_error_t error,
                       const char* error_message,
                       int http_status,
                       const char* data, int data_len,
                       const char* etag,
                       const char* modified,
                       const char* expires,
                       const char* cache_control,
                       int no_content, int not_modified,
                       int must_revalidate) noexcept {
    mln_http_respond_impl(request_id, error, error_message, http_status,
                           data, data_len, etag, modified, expires,
                           cache_control, no_content, not_modified, must_revalidate);
}

void mln_http_cancel(uint64_t request_id) noexcept {
    mln_http_cancel_impl(request_id);
}
#endif
