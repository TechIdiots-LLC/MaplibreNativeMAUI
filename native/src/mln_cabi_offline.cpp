/**
 * mln_cabi_offline.cpp — Offline regions + ambient cache C ABI, wrapping
 * mbgl::DatabaseFileSource.
 *
 * The manager obtains the shared DatabaseFileSource via FileSourceManager, so
 * a manager created with the same resource options as the map shares the
 * map's cache database.
 *
 * Threading: DatabaseFileSource invokes every completion callback on its
 * internal database thread. The C callbacks are invoked directly from that
 * thread; hosts must marshal to their UI thread as needed.
 *
 * OfflineRegion objects are move-only-ish handles that mbgl's region APIs
 * take by reference, so the manager caches each region by ID in a std::map
 * (node addresses are stable) and looks them up per call.
 */

#include "mln_cabi.h"
#include "mln_cabi_internal.hpp"

#include <mbgl/storage/database_file_source.hpp>
#include <mbgl/storage/file_source_manager.hpp>
#include <mbgl/storage/offline.hpp>
#include <mbgl/storage/resource_options.hpp>
#include <mbgl/storage/response.hpp>
#include <mbgl/style/conversion/geojson.hpp>
#include <mbgl/util/client_options.hpp>
#include <mbgl/util/geo.hpp>
#include <mbgl/util/geojson.hpp>
#include <mbgl/util/geometry.hpp>
#include <mbgl/util/rapidjson.hpp>

#include <rapidjson/stringbuffer.h>
#include <rapidjson/writer.h>

#include <cstring>
#include <exception>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <variant>
#include <vector>

namespace {

struct OfflineState {
    std::shared_ptr<mbgl::DatabaseFileSource> db;
    std::mutex mutex;
    std::map<int64_t, mbgl::OfflineRegion> regions;

    /** Insert or replace a region in the cache. Caller must NOT hold mutex. */
    void cache(mbgl::OfflineRegion&& region) {
        std::lock_guard<std::mutex> lock(mutex);
        int64_t id = region.getID();
        regions.erase(id);
        regions.emplace(id, std::move(region));
    }
};

struct CabiOfflineManager {
    std::shared_ptr<OfflineState> state;
};

CabiOfflineManager* mgr_ptr(mbgl_offline_manager_t* m) noexcept {
    return reinterpret_cast<CabiOfflineManager*>(m);
}

std::string exception_message(std::exception_ptr ep) {
    try {
        if (ep) std::rethrow_exception(ep);
    } catch (const std::exception& e) {
        return e.what();
    } catch (...) {
        return "unknown error";
    }
    return {};
}

using JsonWriter = rapidjson::Writer<rapidjson::StringBuffer>;

void write_region(JsonWriter& w, const mbgl::OfflineRegion& region) {
    w.StartObject();
    w.Key("id");
    w.Int64(region.getID());

    const auto& def = region.getDefinition();
    if (const auto* tp = std::get_if<mbgl::OfflineTilePyramidRegionDefinition>(&def)) {
        w.Key("type");     w.String("tilepyramid");
        w.Key("styleUrl"); w.String(tp->styleURL.data(), static_cast<rapidjson::SizeType>(tp->styleURL.size()));
        w.Key("bounds");
        w.StartArray();
        w.Double(tp->bounds.south());
        w.Double(tp->bounds.west());
        w.Double(tp->bounds.north());
        w.Double(tp->bounds.east());
        w.EndArray();
        w.Key("minZoom");           w.Double(tp->minZoom);
        w.Key("maxZoom");           w.Double(tp->maxZoom);
        w.Key("pixelRatio");        w.Double(tp->pixelRatio);
        w.Key("includeIdeographs"); w.Bool(tp->includeIdeographs);
    } else if (const auto* g = std::get_if<mbgl::OfflineGeometryRegionDefinition>(&def)) {
        w.Key("type");     w.String("geometry");
        w.Key("styleUrl"); w.String(g->styleURL.data(), static_cast<rapidjson::SizeType>(g->styleURL.size()));
        std::string geomJson = mapbox::geojson::stringify(mbgl::GeoJSON{g->geometry});
        w.Key("geometry");
        w.RawValue(geomJson.data(), geomJson.size(), rapidjson::kObjectType);
        w.Key("minZoom");           w.Double(g->minZoom);
        w.Key("maxZoom");           w.Double(g->maxZoom);
        w.Key("pixelRatio");        w.Double(g->pixelRatio);
        w.Key("includeIdeographs"); w.Bool(g->includeIdeographs);
    }
    w.EndObject();
}

std::string regions_to_json(const mbgl::OfflineRegions& regions) {
    rapidjson::StringBuffer buf;
    JsonWriter w(buf);
    w.StartArray();
    for (const auto& r : regions) write_region(w, r);
    w.EndArray();
    return {buf.GetString(), buf.GetSize()};
}

std::string region_to_json_array(const mbgl::OfflineRegion& region) {
    rapidjson::StringBuffer buf;
    JsonWriter w(buf);
    w.StartArray();
    write_region(w, region);
    w.EndArray();
    return {buf.GetString(), buf.GetSize()};
}

std::string status_to_json(const mbgl::OfflineRegionStatus& s) {
    rapidjson::StringBuffer buf;
    JsonWriter w(buf);
    w.StartObject();
    w.Key("downloadState");
    w.Int(s.downloadState == mbgl::OfflineRegionDownloadState::Active ? 1 : 0);
    w.Key("completedResourceCount");         w.Uint64(s.completedResourceCount);
    w.Key("completedResourceSize");          w.Uint64(s.completedResourceSize);
    w.Key("completedTileCount");             w.Uint64(s.completedTileCount);
    w.Key("requiredTileCount");              w.Uint64(s.requiredTileCount);
    w.Key("completedTileSize");              w.Uint64(s.completedTileSize);
    w.Key("requiredResourceCount");          w.Uint64(s.requiredResourceCount);
    w.Key("requiredResourceCountIsPrecise"); w.Bool(s.requiredResourceCountIsPrecise);
    w.Key("complete");                       w.Bool(s.complete());
    w.EndObject();
    return {buf.GetString(), buf.GetSize()};
}

/** Wraps a done_fn into mbgl's std::exception_ptr completion callback.
 *  Captures the state shared_ptr so it outlives the manager handle. */
std::function<void(std::exception_ptr)> wrap_done(std::shared_ptr<OfflineState> state,
                                                  mbgl_offline_done_fn cb, void* ud) {
    return [state = std::move(state), cb, ud](std::exception_ptr err) {
        if (!cb) return;
        if (err) {
            std::string msg = exception_message(err);
            cb(MBGL_NATIVE_ERROR, msg.c_str(), ud);
        } else {
            cb(MBGL_OK, nullptr, ud);
        }
    };
}

/** Wraps a regions_fn into mbgl's expected<OfflineRegions> callback, caching
 *  the returned regions so later per-region calls can find them. */
std::function<void(mbgl::expected<mbgl::OfflineRegions, std::exception_ptr>)>
wrap_regions(std::shared_ptr<OfflineState> state, mbgl_offline_regions_fn cb, void* ud) {
    return [state = std::move(state), cb, ud](
               mbgl::expected<mbgl::OfflineRegions, std::exception_ptr> result) {
        if (!result) {
            std::string msg = exception_message(result.error());
            cb(MBGL_NATIVE_ERROR, msg.c_str(), nullptr, ud);
            return;
        }
        std::string json = regions_to_json(*result);
        for (auto& r : *result) state->cache(std::move(r));
        cb(MBGL_OK, nullptr, json.c_str(), ud);
    };
}

/** Observer bridging mbgl's region callbacks to the C function pointers. */
class CabiRegionObserver : public mbgl::OfflineRegionObserver {
public:
    CabiRegionObserver(int64_t id_, mbgl_offline_progress_fn progress_,
                       mbgl_offline_region_error_fn error_, void* ud_)
        : id(id_), progress(progress_), error(error_), ud(ud_) {}

    void statusChanged(mbgl::OfflineRegionStatus s) override {
        if (!progress) return;
        progress(id,
                 s.downloadState == mbgl::OfflineRegionDownloadState::Active ? 1 : 0,
                 s.completedResourceCount,
                 s.completedResourceSize,
                 s.completedTileCount,
                 s.requiredResourceCount,
                 s.requiredResourceCountIsPrecise ? 1 : 0,
                 s.complete() ? 1 : 0,
                 ud);
    }

    void responseError(mbgl::Response::Error e) override {
        if (!error) return;
        error(id, static_cast<int>(e.reason), e.message.c_str(), ud);
    }

    void mapboxTileCountLimitExceeded(uint64_t limit) override {
        if (!error) return;
        std::string msg = "Mapbox tile count limit exceeded: " + std::to_string(limit);
        error(id, MBGL_OFFLINE_TILE_COUNT_LIMIT, msg.c_str(), ud);
    }

private:
    int64_t                      id;
    mbgl_offline_progress_fn     progress;
    mbgl_offline_region_error_fn error;
    void*                        ud;
};

std::vector<uint8_t> to_metadata(const uint8_t* data, int len) {
    if (!data || len <= 0) return {};
    return {data, data + len};
}

} // namespace

/* ─── Manager lifecycle ─────────────────────────────────────────────────────── */

mbgl_offline_manager_t* mbgl_offline_manager_create(const char* cache_path,
                                                    const char* asset_path,
                                                    const char* api_key,
                                                    uint64_t    max_cache_size_bytes) noexcept {
    try {
        mbgl::ResourceOptions resOpts;
        if (cache_path && *cache_path) resOpts.withCachePath(cache_path);
        if (asset_path && *asset_path) resOpts.withAssetPath(asset_path);
        if (api_key && *api_key)       resOpts.withApiKey(api_key);
        if (max_cache_size_bytes)      resOpts.withMaximumCacheSize(max_cache_size_bytes);

        auto fs = mbgl::FileSourceManager::get()->getFileSource(
            mbgl::FileSourceType::Database, resOpts, mbgl::ClientOptions());
        if (!fs) {
            cabi_set_error(MBGL_NATIVE_ERROR, "mbgl_offline_manager_create: no Database file source available");
            return nullptr;
        }

        auto* mgr  = new CabiOfflineManager{};
        mgr->state       = std::make_shared<OfflineState>();
        mgr->state->db   = std::static_pointer_cast<mbgl::DatabaseFileSource>(fs);
        return reinterpret_cast<mbgl_offline_manager_t*>(mgr);
    } catch (const std::exception& e) { cabi_set_native_error(e); return nullptr; }
}

mbgl_status_t mbgl_offline_manager_destroy(mbgl_offline_manager_t* m) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_manager_destroy: null handle");
    try { delete mgr_ptr(m); return MBGL_OK; }
    catch (const std::exception& e) { return cabi_set_native_error(e); }
}

/* ─── Regions ───────────────────────────────────────────────────────────────── */

mbgl_status_t mbgl_offline_list_regions(mbgl_offline_manager_t* m,
                                        mbgl_offline_regions_fn cb,
                                        void* userdata) noexcept {
    if (!m || !cb) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_list_regions: null arg");
    try {
        auto state = mgr_ptr(m)->state;
        state->db->listOfflineRegions(wrap_regions(state, cb, userdata));
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

static mbgl_status_t create_region_impl(mbgl_offline_manager_t* m,
                                        mbgl::OfflineRegionDefinition definition,
                                        const uint8_t* metadata, int metadata_len,
                                        mbgl_offline_regions_fn cb, void* userdata,
                                        const char* fn_name) noexcept {
    try {
        auto state = mgr_ptr(m)->state;
        state->db->createOfflineRegion(
            definition, to_metadata(metadata, metadata_len),
            [state, cb, userdata](mbgl::expected<mbgl::OfflineRegion, std::exception_ptr> result) {
                if (!result) {
                    std::string msg = exception_message(result.error());
                    cb(MBGL_NATIVE_ERROR, msg.c_str(), nullptr, userdata);
                    return;
                }
                std::string json = region_to_json_array(*result);
                state->cache(std::move(*result));
                cb(MBGL_OK, nullptr, json.c_str(), userdata);
            });
        return MBGL_OK;
    } catch (const std::exception& e) {
        return cabi_set_error(MBGL_NATIVE_ERROR, std::string(fn_name) + ": " + e.what());
    }
}

mbgl_status_t mbgl_offline_create_region(mbgl_offline_manager_t* m,
                                         const char* style_url,
                                         double lat_sw, double lon_sw,
                                         double lat_ne, double lon_ne,
                                         double min_zoom, double max_zoom,
                                         float  pixel_ratio,
                                         int    include_ideographs,
                                         const uint8_t* metadata, int metadata_len,
                                         mbgl_offline_regions_fn cb,
                                         void* userdata) noexcept {
    if (!m || !style_url || !cb)
        return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_create_region: null arg");
    auto bounds = mbgl::LatLngBounds::hull(mbgl::LatLng{lat_sw, lon_sw}, mbgl::LatLng{lat_ne, lon_ne});
    return create_region_impl(
        m,
        mbgl::OfflineTilePyramidRegionDefinition(style_url, bounds, min_zoom, max_zoom,
                                                 pixel_ratio, include_ideographs != 0),
        metadata, metadata_len, cb, userdata, "mbgl_offline_create_region");
}

mbgl_status_t mbgl_offline_create_region_geometry(mbgl_offline_manager_t* m,
                                                  const char* style_url,
                                                  const char* geometry_geojson,
                                                  double min_zoom, double max_zoom,
                                                  float  pixel_ratio,
                                                  int    include_ideographs,
                                                  const uint8_t* metadata, int metadata_len,
                                                  mbgl_offline_regions_fn cb,
                                                  void* userdata) noexcept {
    if (!m || !style_url || !geometry_geojson || !cb)
        return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_create_region_geometry: null arg");

    mbgl::style::conversion::Error err;
    auto geojson = mbgl::style::conversion::parseGeoJSON(geometry_geojson, err);
    if (!geojson)
        return cabi_set_error(MBGL_INVALID_ARG,
                              "mbgl_offline_create_region_geometry: " + err.message);

    mbgl::Geometry<double> geometry;
    if (geojson->is<mapbox::geojson::geometry>()) {
        geometry = geojson->get<mapbox::geojson::geometry>();
    } else if (geojson->is<mapbox::geojson::feature>()) {
        geometry = geojson->get<mapbox::geojson::feature>().geometry;
    } else if (geojson->is<mapbox::geojson::feature_collection>() &&
               geojson->get<mapbox::geojson::feature_collection>().size() == 1) {
        geometry = geojson->get<mapbox::geojson::feature_collection>().front().geometry;
    } else {
        return cabi_set_error(MBGL_INVALID_ARG,
                              "mbgl_offline_create_region_geometry: expected a GeoJSON Geometry, "
                              "Feature, or single-feature FeatureCollection");
    }

    return create_region_impl(
        m,
        mbgl::OfflineGeometryRegionDefinition(style_url, std::move(geometry), min_zoom, max_zoom,
                                              pixel_ratio, include_ideographs != 0),
        metadata, metadata_len, cb, userdata, "mbgl_offline_create_region_geometry");
}

/** Looks up a cached region; returns nullptr (and sets last-error) if unknown.
 *  The returned pointer stays valid while the region remains in the map —
 *  std::map node addresses are stable across inserts/erases of other keys. */
static mbgl::OfflineRegion* find_region(const std::shared_ptr<OfflineState>& state,
                                        int64_t region_id, const char* fn_name) {
    std::lock_guard<std::mutex> lock(state->mutex);
    auto it = state->regions.find(region_id);
    if (it == state->regions.end()) {
        cabi_set_error(MBGL_INVALID_ARG,
                       std::string(fn_name) + ": unknown region id " + std::to_string(region_id) +
                           " (regions must come from list/create/merge on this manager)");
        return nullptr;
    }
    return &it->second;
}

mbgl_status_t mbgl_offline_delete_region(mbgl_offline_manager_t* m,
                                         int64_t region_id,
                                         mbgl_offline_done_fn cb,
                                         void* userdata) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_delete_region: null handle");
    try {
        auto state   = mgr_ptr(m)->state;
        auto* region = find_region(state, region_id, "mbgl_offline_delete_region");
        if (!region) return MBGL_INVALID_ARG;
        // The region object must stay cached (alive) until the operation
        // completes; drop it from the cache in the completion callback.
        state->db->deleteOfflineRegion(
            *region, [state, region_id, cb, userdata](std::exception_ptr err) {
                if (!err) {
                    std::lock_guard<std::mutex> lock(state->mutex);
                    state->regions.erase(region_id);
                }
                wrap_done(state, cb, userdata)(err);
            });
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_invalidate_region(mbgl_offline_manager_t* m,
                                             int64_t region_id,
                                             mbgl_offline_done_fn cb,
                                             void* userdata) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_invalidate_region: null handle");
    try {
        auto state   = mgr_ptr(m)->state;
        auto* region = find_region(state, region_id, "mbgl_offline_invalidate_region");
        if (!region) return MBGL_INVALID_ARG;
        state->db->invalidateOfflineRegion(*region, wrap_done(state, cb, userdata));
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_set_region_download_state(mbgl_offline_manager_t* m,
                                                     int64_t region_id,
                                                     int active) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_set_region_download_state: null handle");
    try {
        auto state   = mgr_ptr(m)->state;
        auto* region = find_region(state, region_id, "mbgl_offline_set_region_download_state");
        if (!region) return MBGL_INVALID_ARG;
        state->db->setOfflineRegionDownloadState(
            *region, active ? mbgl::OfflineRegionDownloadState::Active
                            : mbgl::OfflineRegionDownloadState::Inactive);
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_set_region_observer(mbgl_offline_manager_t* m,
                                               int64_t region_id,
                                               mbgl_offline_progress_fn progress,
                                               mbgl_offline_region_error_fn error,
                                               void* userdata) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_set_region_observer: null handle");
    try {
        auto state   = mgr_ptr(m)->state;
        auto* region = find_region(state, region_id, "mbgl_offline_set_region_observer");
        if (!region) return MBGL_INVALID_ARG;
        state->db->setOfflineRegionObserver(
            *region, std::make_unique<CabiRegionObserver>(region_id, progress, error, userdata));
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_get_region_status(mbgl_offline_manager_t* m,
                                             int64_t region_id,
                                             mbgl_offline_status_fn cb,
                                             void* userdata) noexcept {
    if (!m || !cb) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_get_region_status: null arg");
    try {
        auto state   = mgr_ptr(m)->state;
        auto* region = find_region(state, region_id, "mbgl_offline_get_region_status");
        if (!region) return MBGL_INVALID_ARG;
        state->db->getOfflineRegionStatus(
            *region,
            [state, cb, userdata](mbgl::expected<mbgl::OfflineRegionStatus, std::exception_ptr> result) {
                if (!result) {
                    std::string msg = exception_message(result.error());
                    cb(MBGL_NATIVE_ERROR, msg.c_str(), nullptr, userdata);
                    return;
                }
                std::string json = status_to_json(*result);
                cb(MBGL_OK, nullptr, json.c_str(), userdata);
            });
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_update_region_metadata(mbgl_offline_manager_t* m,
                                                  int64_t region_id,
                                                  const uint8_t* metadata, int metadata_len,
                                                  mbgl_offline_done_fn cb,
                                                  void* userdata) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_update_region_metadata: null handle");
    try {
        auto state   = mgr_ptr(m)->state;
        auto* region = find_region(state, region_id, "mbgl_offline_update_region_metadata");
        if (!region) return MBGL_INVALID_ARG;
        state->db->updateOfflineMetadata(
            region_id, to_metadata(metadata, metadata_len),
            [state, cb, userdata](mbgl::expected<mbgl::OfflineRegionMetadata, std::exception_ptr> result) {
                if (!cb) return;
                if (!result) {
                    std::string msg = exception_message(result.error());
                    cb(MBGL_NATIVE_ERROR, msg.c_str(), userdata);
                } else {
                    cb(MBGL_OK, nullptr, userdata);
                }
            });
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

char* mbgl_offline_region_get_metadata(mbgl_offline_manager_t* m,
                                       int64_t region_id,
                                       int* out_len) noexcept {
    if (out_len) *out_len = 0;
    if (!m) return nullptr;
    try {
        auto state = mgr_ptr(m)->state;
        std::lock_guard<std::mutex> lock(state->mutex);
        auto it = state->regions.find(region_id);
        if (it == state->regions.end()) return nullptr;
        const auto& metadata = it->second.getMetadata();
        if (metadata.empty()) return nullptr;
        char* result = new char[metadata.size() + 1];
        std::memcpy(result, metadata.data(), metadata.size());
        result[metadata.size()] = '\0';
        if (out_len) *out_len = static_cast<int>(metadata.size());
        return result;
    } catch (...) { return nullptr; }
}

mbgl_status_t mbgl_offline_merge_database(mbgl_offline_manager_t* m,
                                          const char* side_db_path,
                                          mbgl_offline_regions_fn cb,
                                          void* userdata) noexcept {
    if (!m || !side_db_path || !cb)
        return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_merge_database: null arg");
    try {
        auto state = mgr_ptr(m)->state;
        state->db->mergeOfflineRegions(side_db_path, wrap_regions(state, cb, userdata));
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_set_tile_count_limit(mbgl_offline_manager_t* m,
                                                uint64_t limit) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_set_tile_count_limit: null handle");
    try { mgr_ptr(m)->state->db->setOfflineMapboxTileCountLimit(limit); return MBGL_OK; }
    catch (const std::exception& e) { return cabi_set_native_error(e); }
}

/* ─── Ambient cache / database maintenance ──────────────────────────────────── */

mbgl_status_t mbgl_offline_set_maximum_ambient_cache_size(mbgl_offline_manager_t* m,
                                                          uint64_t bytes,
                                                          mbgl_offline_done_fn cb,
                                                          void* userdata) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_set_maximum_ambient_cache_size: null handle");
    try {
        auto state = mgr_ptr(m)->state;
        state->db->setMaximumAmbientCacheSize(bytes, wrap_done(state, cb, userdata));
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_clear_ambient_cache(mbgl_offline_manager_t* m,
                                               mbgl_offline_done_fn cb,
                                               void* userdata) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_clear_ambient_cache: null handle");
    try {
        auto state = mgr_ptr(m)->state;
        state->db->clearAmbientCache(wrap_done(state, cb, userdata));
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_invalidate_ambient_cache(mbgl_offline_manager_t* m,
                                                    mbgl_offline_done_fn cb,
                                                    void* userdata) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_invalidate_ambient_cache: null handle");
    try {
        auto state = mgr_ptr(m)->state;
        state->db->invalidateAmbientCache(wrap_done(state, cb, userdata));
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_pack_database(mbgl_offline_manager_t* m,
                                         mbgl_offline_done_fn cb,
                                         void* userdata) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_pack_database: null handle");
    try {
        auto state = mgr_ptr(m)->state;
        state->db->packDatabase(wrap_done(state, cb, userdata));
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_reset_database(mbgl_offline_manager_t* m,
                                          mbgl_offline_done_fn cb,
                                          void* userdata) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_reset_database: null handle");
    try {
        auto state = mgr_ptr(m)->state;
        state->db->resetDatabase(wrap_done(state, cb, userdata));
        return MBGL_OK;
    } catch (const std::exception& e) { return cabi_set_native_error(e); }
}

mbgl_status_t mbgl_offline_set_pack_database_automatically(mbgl_offline_manager_t* m,
                                                           int enabled) noexcept {
    if (!m) return cabi_set_error(MBGL_INVALID_ARG, "mbgl_offline_set_pack_database_automatically: null handle");
    try { mgr_ptr(m)->state->db->runPackDatabaseAutomatically(enabled != 0); return MBGL_OK; }
    catch (const std::exception& e) { return cabi_set_native_error(e); }
}
