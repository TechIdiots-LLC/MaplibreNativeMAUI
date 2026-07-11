// HTTPFileSource implementation for standalone Android NDK builds.
//
// Rather than shipping/cross-compiling libcurl for every Android ABI, HTTP is
// delegated to the host .NET runtime via a registered callback.  When a
// request arrives the C++ side calls the provider function (set from C# via
// mbgl_set_http_provider), which performs the actual HTTP fetch using
// Android's built-in HttpURLConnection through .NET's HttpClient.  When the
// fetch completes, the host calls mbgl_http_respond() which marshals the
// result back onto the correct mbgl RunLoop thread and invokes the callback.
//
// Protocol:
//   1. mbgl_set_http_provider(fn, userdata) -- called once at map init from C#.
//   2. HTTPFileSource::request() -- assigns a unique request_id, calls fn().
//   3. C# fetches the URL, then calls mbgl_http_respond(request_id, ...).
//   4. mbgl_http_respond posts a closure onto the RunLoop and calls callback.
//   5. If the AsyncRequest is destroyed before the response arrives,
//      mbgl_http_cancel(request_id) is called and the response is silently dropped.

// Include the C ABI header for mbgl_http_provider_fn / mbgl_http_error_t typedefs.
#include "mln_cabi.h"

#include <mbgl/storage/http_file_source.hpp>
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
};

HttpProviderState& state() {
    static HttpProviderState s;
    return s;
}

} // namespace

// ── C ABI implementations (called from mln_cabi.cpp via forward declarations) ─

extern "C" {

void mbgl_set_http_provider_impl(mbgl_http_provider_fn fn, void* userdata) noexcept {
    auto& s = state();
    std::lock_guard<std::mutex> lock(s.mutex);
    s.fn       = fn;
    s.userdata = userdata;
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

// ── HTTPFileSource implementation ─────────────────────────────────────────────

namespace mbgl {

class HTTPFileSource::Impl {};

HTTPFileSource::HTTPFileSource(const ResourceOptions&, const ClientOptions&)
    : impl(std::make_unique<Impl>()) {}

HTTPFileSource::~HTTPFileSource() = default;

// The AsyncRequest subclass that cancels the pending request on destruction.
namespace {
class AndroidHttpRequest : public AsyncRequest {
public:
    explicit AndroidHttpRequest(uint64_t id) : _id(id) {}

    ~AndroidHttpRequest() override {
        mbgl_http_cancel_impl(_id);
    }

private:
    uint64_t _id;
};
} // namespace

std::unique_ptr<AsyncRequest> HTTPFileSource::request(const Resource& resource, Callback callback) {
    auto& s = state();

    mbgl_http_provider_fn fn;
    void* userdata;
    uint64_t id;
    {
        std::lock_guard<std::mutex> lock(s.mutex);
        fn = s.fn;
        userdata = s.userdata;
        if (!fn) {
            // No provider registered — fall back to immediate connection error.
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

    // Extract byte-range if this is a range request (used by PMTiles).
    int64_t range_start = -1;
    int64_t range_end   = -1;
    if (resource.dataRange) {
        range_start = static_cast<int64_t>(resource.dataRange->first);
        range_end   = static_cast<int64_t>(resource.dataRange->second);
    }

    // Invoke the provider (may be called from any thread — C# will dispatch
    // the fetch to a thread pool and call back via mbgl_http_respond).
    fn(id, resource.url.c_str(), etag, modified, range_start, range_end, userdata);

    return std::make_unique<AndroidHttpRequest>(id);
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
