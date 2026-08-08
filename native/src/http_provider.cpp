// Host-provided networking: resource requests are answered by the .NET host
// rather than by a native HTTP stack.
//
// Originally this existed only for standalone Android NDK builds, where
// shipping libcurl for every ABI was unattractive and the JNI SDK's HTTP
// implementation is absent. It is now built on every platform, because routing
// requests through the host is useful in its own right: mbgl passes the byte
// range on the Resource, and PMTiles reads are all ranged, so a host that holds
// an archive somewhere other than a web server — a BitTorrent swarm, an
// embedded database, an encrypted bundle — can serve tiles without maplibre
// knowing the difference.
//
// Protocol:
//   1. mbgl_set_http_provider(fn, userdata) -- called once at map init from C#.
//   2. request() -- assigns a unique request_id, calls fn().
//   3. C# fetches the URL, then calls mbgl_http_respond(request_id, ...).
//   4. mbgl_http_respond posts a closure onto the RunLoop and calls callback.
//   5. If the AsyncRequest is destroyed before the response arrives,
//      mbgl_http_cancel(request_id) is called and the response is silently dropped.
//
// Two routes into that machinery, because the platforms differ:
//
//   Android — defines mbgl::HTTPFileSource directly. It has to: nothing else in
//     a standalone NDK build provides that symbol, and mbgl-core references it.
//
//   Everywhere else — maplibre-native already links its own HTTPFileSource, so
//     defining a second one would collide. Instead a FileSource is registered
//     at runtime for FileSourceType::Network, which needs no build changes and
//     is opt-in: the factory is installed only when a provider is actually set,
//     so an application that never calls mbgl_set_http_provider keeps
//     maplibre's own network stack, unchanged.

// Include the C ABI header for mbgl_http_provider_fn / mbgl_http_error_t typedefs.
#include "mln_cabi.h"

#include <mbgl/storage/file_source.hpp>
#include <mbgl/storage/file_source_manager.hpp>
#include <mbgl/storage/http_file_source.hpp>
#include <mbgl/storage/online_file_source.hpp>
#include <mbgl/storage/resource.hpp>
#include <mbgl/storage/resource_options.hpp>
#include <mbgl/storage/response.hpp>
#include <mbgl/util/async_request.hpp>
#include <mbgl/util/chrono.hpp>
#include <mbgl/util/client_options.hpp>
#include <mbgl/util/run_loop.hpp>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

// ── Shared state ─────────────────────────────────────────────────────────────

namespace {

struct PendingRequest {
    mbgl::FileSource::Callback callback;
    mbgl::util::RunLoop*       runLoop;  // the map thread's RunLoop
    std::atomic<bool>          cancelled{false};
};

struct HttpProviderState {
    std::mutex                                    mutex;
    mbgl_http_provider_fn                         fn      = nullptr;
    void*                                         userdata = nullptr;
    mbgl_http_cancel_fn                           cancelFn = nullptr;
    void*                                         cancelUserdata = nullptr;
    std::atomic<uint64_t>                         nextId{1};
    std::unordered_map<uint64_t, std::shared_ptr<PendingRequest>> pending;
    /// URL prefixes the host has claimed. Empty means "claim everything",
    /// which is what a host replacing the network stack outright wants.
    std::vector<std::string>                      claims;
};

HttpProviderState& state() {
    static HttpProviderState s;
    return s;
}

} // namespace

// ── C ABI implementations (called from mln_cabi.cpp via forward declarations) ─

extern "C" {

// Defined at the bottom of this file, once ProviderFileSource is in scope.
void mbgl_install_provider_file_source() noexcept;

void mbgl_set_http_provider_impl(mbgl_http_provider_fn fn, void* userdata) noexcept {
    {
        auto& s = state();
        std::lock_guard<std::mutex> lock(s.mutex);
        s.fn       = fn;
        s.userdata = userdata;
    }
#if !defined(__ANDROID__)
    // Take over FileSourceType::Network only once a provider actually exists.
    // An application that never registers one is left entirely alone, still
    // using whichever network stack maplibre-native built for its platform.
    //
    // Not done on Android, and deliberately so. There the provider is already
    // reached through mbgl::HTTPFileSource, which sits *underneath*
    // OnlineFileSource — so requests keep mbgl's retry/backoff, rate-limit
    // handling and queueing, with only the transport delegated to the host.
    // Registering this factory would replace OnlineFileSource outright and
    // throw that away, which would be a regression on the one platform that
    // already worked.
    if (fn) {
        mbgl_install_provider_file_source();
    }
#endif
}

void mbgl_http_provider_claim_prefix_impl(const char* url_prefix) noexcept {
    if (!url_prefix || !*url_prefix) return;
    auto& s = state();
    std::lock_guard<std::mutex> lock(s.mutex);
    s.claims.emplace_back(url_prefix);
}

void mbgl_http_provider_clear_claims_impl() noexcept {
    auto& s = state();
    std::lock_guard<std::mutex> lock(s.mutex);
    s.claims.clear();
}

void mbgl_set_http_cancel_provider_impl(mbgl_http_cancel_fn fn, void* userdata) noexcept {
    auto& s = state();
    std::lock_guard<std::mutex> lock(s.mutex);
    s.cancelFn       = fn;
    s.cancelUserdata = userdata;
}

void mbgl_http_respond_impl(uint64_t request_id,
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
                             int               must_revalidate) noexcept {
    // Find the pending request. Do NOT erase it yet: the entry must stay in the
    // map until the callback actually runs, so a cancellation arriving between
    // this point and the RunLoop executing the posted closure can still mark it
    // cancelled. Erasing here left a window where the AsyncRequest was destroyed
    // (its dtor's cancel found nothing to flag) while the already-posted closure
    // went on to invoke the callback into the freed OnlineFileRequest — a
    // use-after-free crash on the OnlineFileSource thread.
    std::shared_ptr<PendingRequest> req;
    {
        auto& s = state();
        std::lock_guard<std::mutex> lock(s.mutex);
        auto it = s.pending.find(request_id);
        if (it == s.pending.end()) return; // already cancelled or unknown
        req = it->second;
    }

    if (req->cancelled.load()) return;

    // Build the response (must be done before posting to RunLoop as strings
    // are owned by C# and may be freed after this call returns).
    mbgl::Response response;

    if (error != MBGL_HTTP_ERROR_NONE) {
        using Reason = mbgl::Response::Error::Reason;
        Reason reason;
        switch (error) {
            case MBGL_HTTP_ERROR_NOT_FOUND:  reason = Reason::NotFound;    break;
            case MBGL_HTTP_ERROR_SERVER:     reason = Reason::Server;      break;
            case MBGL_HTTP_ERROR_CONNECTION: reason = Reason::Connection;  break;
            case MBGL_HTTP_ERROR_RATE_LIMIT: reason = Reason::RateLimit;   break;
            default:                          reason = Reason::Other;       break;
        }
        response.error = std::make_unique<const mbgl::Response::Error>(
            reason, error_message ? error_message : "");
    } else if (no_content) {
        response.noContent = true;
    } else if (not_modified) {
        response.notModified = true;
    } else {
        // Copy payload
        if (data && data_len > 0) {
            response.data = std::make_shared<std::string>(data, static_cast<size_t>(data_len));
        } else {
            response.data = std::make_shared<std::string>();
        }
    }

    response.mustRevalidate = (must_revalidate != 0);

    // Parse ETag
    if (etag && *etag) response.etag = std::string(etag);

    // Parse Last-Modified
    if (modified && *modified) {
        response.modified = mbgl::util::parseTimestamp(modified);
    }

    // Parse Expires (prefer Cache-Control max-age if provided)
    if (cache_control && *cache_control) {
        // Simple max-age parsing: look for "max-age=N"
        const char* p = strstr(cache_control, "max-age=");
        if (p) {
            long secs = strtol(p + 8, nullptr, 10);
            if (secs > 0) {
                using namespace std::chrono;
                response.expires = time_point_cast<mbgl::Seconds>(
                    system_clock::now() + seconds(secs));
            }
        }
        const char* mr = strstr(cache_control, "must-revalidate");
        if (mr) response.mustRevalidate = true;
    } else if (expires && *expires) {
        response.expires = mbgl::util::parseTimestamp(expires);
    }

    // Marshal the callback back onto the requesting thread's RunLoop. The closure
    // re-checks the cancelled flag when it runs: the AsyncRequest destructor
    // (which sets the flag via mbgl_http_cancel_impl) executes on this same
    // RunLoop thread, so by execution time the flag conclusively says whether
    // the callback target is still alive. The map entry is erased only now.
    auto response_copy = response;
    req->runLoop->invoke([id = request_id, req, r = std::move(response_copy)]() mutable {
        {
            auto& s = state();
            std::lock_guard<std::mutex> lock(s.mutex);
            s.pending.erase(id);
        }
        if (req->cancelled.load()) return;   // request destroyed after respond was posted
        req->callback(r);
    });
}

void mbgl_http_cancel_impl(uint64_t request_id) noexcept {
    mbgl_http_cancel_fn cancelFn = nullptr;
    void*               cancelUserdata = nullptr;
    {
        auto& s = state();
        std::lock_guard<std::mutex> lock(s.mutex);
        auto it = s.pending.find(request_id);
        if (it != s.pending.end()) {
            it->second->cancelled.store(true);
            s.pending.erase(it);
        }
        cancelFn       = s.cancelFn;
        cancelUserdata = s.cancelUserdata;
    }
    // Notify the host provider so it aborts the in-flight fetch — outside the
    // lock, since the host callback may re-enter the HTTP layer. Harmless for a
    // request that already completed (the host's own bookkeeping no-ops).
    // This is what frees the connection for tiles still needed at the current
    // zoom; without it, superseded requests run to completion and starve them.
    if (cancelFn) cancelFn(request_id, cancelUserdata);
}

} // extern "C"

// ── Shared request dispatch ───────────────────────────────────────────────────

namespace mbgl {
namespace {

/// Cancels the host-side fetch when mbgl drops the request, which it does
/// constantly while panning and zooming. Without this, superseded requests run
/// to completion and starve the tiles actually on screen.
class ProviderRequest : public AsyncRequest {
public:
    explicit ProviderRequest(uint64_t id) : _id(id) {}

    ~ProviderRequest() override { mbgl_http_cancel_impl(_id); }

private:
    uint64_t _id;
};

/// The body of request(), shared by both routes into the provider so the
/// request table, cancellation and range handling exist in exactly one place.
std::unique_ptr<AsyncRequest> dispatchToProvider(const Resource&        resource,
                                                 FileSource::Callback&& callback) {
    auto& s = state();

    mbgl_http_provider_fn fn;
    void* userdata;
    uint64_t id;
    {
        std::lock_guard<std::mutex> lock(s.mutex);
        fn = s.fn;
        userdata = s.userdata;
        if (!fn) {
            // No provider registered — fail rather than hang waiting for a
            // response nobody is going to send.
            Response response;
            response.error = std::make_unique<const Response::Error>(
                Response::Error::Reason::Connection,
                "No HTTP provider registered (call mbgl_set_http_provider first)");
            callback(std::move(response));
            return nullptr;
        }

        id = s.nextId.fetch_add(1, std::memory_order_relaxed);

        auto pending = std::make_shared<PendingRequest>();
        pending->callback = std::move(callback);
        pending->runLoop  = util::RunLoop::Get();
        s.pending.emplace(id, std::move(pending));
    }

    // Determine conditional GET headers from resource
    const char* etag     = nullptr;
    const char* modified = nullptr;
    std::string etagStr, modifiedStr;

    if (resource.priorEtag) {
        etagStr = *resource.priorEtag;
        etag = etagStr.c_str();
    } else if (resource.priorModified) {
        modifiedStr = util::rfc1123(*resource.priorModified);
        modified = modifiedStr.c_str();
    }

    // Extract byte-range if this is a range request. Every PMTiles read is one,
    // which is what allows a host to serve an archive it holds in pieces.
    int64_t range_start = -1;
    int64_t range_end   = -1;
    if (resource.dataRange) {
        range_start = static_cast<int64_t>(resource.dataRange->first);
        range_end   = static_cast<int64_t>(resource.dataRange->second);
    }

    // Invoke the provider (may be called from any thread — C# will dispatch
    // the fetch to a thread pool and call back via mbgl_http_respond).
    fn(id, resource.url.c_str(), etag, modified, range_start, range_end, userdata);

    return std::make_unique<ProviderRequest>(id);
}

} // namespace
} // namespace mbgl

// ── Route 1: HTTPFileSource (Android only) ───────────────────────────────────
//
// Only defined where nothing else provides the symbol. On other platforms
// maplibre-native supplies its own and a second definition would not link.

#if defined(__ANDROID__)

namespace mbgl {

class HTTPFileSource::Impl {};

HTTPFileSource::HTTPFileSource(const ResourceOptions&, const ClientOptions&)
    : impl(std::make_unique<Impl>()) {}

HTTPFileSource::~HTTPFileSource() = default;

std::unique_ptr<AsyncRequest> HTTPFileSource::request(const Resource& resource, Callback callback) {
    return dispatchToProvider(resource, std::move(callback));
}

void HTTPFileSource::setResourceOptions(ResourceOptions) {}

ResourceOptions HTTPFileSource::getResourceOptions() {
    return {};
}

void HTTPFileSource::setClientOptions(ClientOptions) {}

ClientOptions HTTPFileSource::getClientOptions() {
    return {};
}

} // namespace mbgl

#endif // __ANDROID__

// ── Route 2: a registered Network FileSource (all platforms) ─────────────────

namespace mbgl {
namespace {

/// Routes requests either to the host or to maplibre's own network stack.
///
/// The important part is what it does *not* intercept. A host that only wants
/// to serve a couple of archives from somewhere unusual should not have to
/// reimplement HTTP for the whole map — and replacing OnlineFileSource means
/// losing its retry with backoff, rate-limit handling and queueing, which the
/// host would then owe everyone. So the host claims URL prefixes it can
/// satisfy, and everything else is delegated to a wrapped OnlineFileSource,
/// exactly as if no provider had been registered.
///
/// A host that genuinely wants to own all networking simply claims nothing,
/// which is read as "claim everything".
class ProviderFileSource final : public FileSource {
public:
    ProviderFileSource(const ResourceOptions& resourceOptions_, const ClientOptions& clientOptions_)
        : resourceOptions(resourceOptions_.clone()),
          clientOptions(clientOptions_.clone()),
          fallback(std::make_unique<OnlineFileSource>(resourceOptions_, clientOptions_)) {}

    std::unique_ptr<AsyncRequest> request(const Resource& resource, Callback callback) override {
        if (hostClaims(resource.url)) {
            return dispatchToProvider(resource, std::move(callback));
        }
        return fallback->request(resource, std::move(callback));
    }

    bool canRequest(const Resource& resource) const override { return fallback->canRequest(resource); }

    bool supportsCacheOnlyRequests() const override { return fallback->supportsCacheOnlyRequests(); }

    void forward(const Resource& resource, const Response& response, std::function<void()> callback) override {
        fallback->forward(resource, response, std::move(callback));
    }

    void pause() override { fallback->pause(); }
    void resume() override { fallback->resume(); }

    void setProperty(const std::string& key, const mapbox::base::Value& value) override {
        fallback->setProperty(key, value);
    }

    mapbox::base::Value getProperty(const std::string& key) const override {
        return fallback->getProperty(key);
    }

    void setResourceTransform(ResourceTransform transform) override {
        fallback->setResourceTransform(std::move(transform));
    }

    void setResourceOptions(ResourceOptions options) override {
        {
            std::lock_guard<std::mutex> lock(optionsMutex);
            resourceOptions = options.clone();
        }
        fallback->setResourceOptions(options.clone());
    }

    ResourceOptions getResourceOptions() override {
        std::lock_guard<std::mutex> lock(optionsMutex);
        return resourceOptions.clone();
    }

    void setClientOptions(ClientOptions options) override {
        {
            std::lock_guard<std::mutex> lock(optionsMutex);
            clientOptions = options.clone();
        }
        fallback->setClientOptions(options.clone());
    }

    ClientOptions getClientOptions() override {
        std::lock_guard<std::mutex> lock(optionsMutex);
        return clientOptions.clone();
    }

private:
    /// Prefix match rather than a callback into the host: this runs for every
    /// resource the map fetches, and a reverse P/Invoke per tile would be a
    /// poor trade for what is a handful of known archive URLs.
    static bool hostClaims(const std::string& url) {
        auto& s = state();
        std::lock_guard<std::mutex> lock(s.mutex);
        if (!s.fn) return false;
        if (s.claims.empty()) return true;
        for (const auto& prefix : s.claims) {
            if (0 == url.rfind(prefix, 0)) return true;
        }
        return false;
    }

    std::mutex                        optionsMutex;
    ResourceOptions                   resourceOptions;
    ClientOptions                     clientOptions;
    // Held as the base type on purpose: OnlineFileSource declares its
    // overrides private, so they are only reachable through FileSource.
    std::unique_ptr<FileSource>       fallback;
};

} // namespace
} // namespace mbgl

extern "C" {

/**
 * Install the provider-backed network file source.
 *
 * Called from mbgl_set_http_provider once a provider exists, so an application
 * that never registers one keeps maplibre's own network stack untouched.
 *
 * Registration is idempotent and one-way. mbgl caches file source instances, so
 * a map created before this point keeps whichever source it already resolved —
 * which is why the public API documents that the provider must be set before
 * the first map is created.
 */
void mbgl_install_provider_file_source() noexcept {
    static std::once_flag once;
    std::call_once(once, [] {
        mbgl::FileSourceManager::get()->registerFileSourceFactory(
            mbgl::FileSourceType::Network,
            [](const mbgl::ResourceOptions& resourceOptions, const mbgl::ClientOptions& clientOptions)
                -> std::unique_ptr<mbgl::FileSource> {
                return std::make_unique<mbgl::ProviderFileSource>(resourceOptions, clientOptions);
            });
    });
}

} // extern "C"
