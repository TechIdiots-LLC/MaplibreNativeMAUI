/**
 * android_asset_file_source.cpp — asset:// served from the APK's assets/ folder.
 *
 * In the MapLibre Android SDK, asset:// (and file:///android_asset/) URLs are answered by
 * AssetManagerFileSource, which reads through the NDK's AAssetManager. That class lives in
 * the SDK's JNI library, not in mbgl-core, so this standalone NDK build falls back to core's
 * generic AssetFileSource — which resolves asset://x to <assetPath>/x on the filesystem. APK
 * assets are entries inside the APK, not files, so nothing under asset:// could be found, and
 * neither could a pmtiles://asset://… archive (PMTiles fetches its archive through the same
 * resource loader).
 *
 * This is the same idea, minus the jni.hpp wrappers: the host hands over its
 * android.content.res.AssetManager once (mln_android_set_asset_manager), and a
 * FileSourceType::Asset factory is registered that reads through it, honouring byte ranges
 * so PMTiles reads only what it asks for. MAUI puts Resources/Raw files in assets/, so
 * asset://name matches the MauiAsset of that name on every platform.
 */
#ifdef __ANDROID__

#include "mln_cabi.h"

#include <mln/actor/actor_ref.hpp>
#include <mln/platform/settings.hpp>
#include <mln/storage/file_source.hpp>
#include <mln/storage/file_source_manager.hpp>
#include <mln/storage/file_source_request.hpp>
#include <mln/storage/resource.hpp>
#include <mln/storage/resource_options.hpp>
#include <mln/storage/response.hpp>
#include <mln/util/client_options.hpp>
#include <mln/util/constants.hpp>
#include <mln/util/thread.hpp>
#include <mln/util/url.hpp>

#include <android/asset_manager.h>
#include <android/asset_manager_jni.h>
#include <jni.h>

#include <algorithm>
#include <cstdint>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <utility>

namespace mln {
namespace {

constexpr std::string_view androidAssetPathPrefix = "/android_asset/";

void stripLeadingSlashes(std::string& path) {
    path.erase(0, path.find_first_not_of('/'));
}

// asset://dir/name and file:///android_asset/dir/name both name assets/dir/name.
std::optional<std::string> androidAssetPathFromUrl(const std::string& url) {
    if (url.starts_with(util::ASSET_PROTOCOL)) {
        std::string path = util::percentDecode(url.substr(std::char_traits<char>::length(util::ASSET_PROTOCOL)));
        stripLeadingSlashes(path);
        return path;
    }
    if (url.starts_with(util::FILE_PROTOCOL)) {
        std::string path = util::percentDecode(url.substr(std::char_traits<char>::length(util::FILE_PROTOCOL)));
        if (!path.starts_with(androidAssetPathPrefix)) return std::nullopt;
        path.erase(0, androidAssetPathPrefix.size());
        stripLeadingSlashes(path);
        return path;
    }
    return std::nullopt;
}

// dataRange is inclusive, as a Range header is.
std::optional<std::string> readAsset(AAsset* asset, const std::optional<std::pair<uint64_t, uint64_t>>& dataRange) {
    off64_t offset = 0;
    off64_t length = AAsset_getLength64(asset);
    if (dataRange) {
        offset = static_cast<off64_t>(dataRange->first);
        length = std::min(length - offset, static_cast<off64_t>(dataRange->second) - offset + 1);
    }
    if (length <= 0) return std::string();
    if (offset > 0 && AAsset_seek64(asset, offset, SEEK_SET) < 0) return std::nullopt;

    std::string data(static_cast<size_t>(length), '\0');
    for (size_t read = 0; read < data.size();) {
        const int count = AAsset_read(asset, data.data() + read, data.size() - read);
        if (count <= 0) return std::nullopt;
        read += static_cast<size_t>(count);
    }
    return data;
}

// Runs on the source's own thread, as core's AssetFileSource::Impl does.
class AndroidAssetReader {
public:
    AndroidAssetReader(const ActorRef<AndroidAssetReader>&, AAssetManager* assetManager_,
                       const ResourceOptions& resourceOptions_, const ClientOptions& clientOptions_)
        : assetManager(assetManager_),
          resourceOptions(resourceOptions_.clone()),
          clientOptions(clientOptions_.clone()) {}

    void request(const std::string& url,
                 const std::optional<std::pair<uint64_t, uint64_t>>& dataRange,
                 const ActorRef<FileSourceRequest>& req) {
        Response response;
        // AAssetManager paths are relative to assets/ already.
        const auto path = androidAssetPathFromUrl(url);
        if (AAsset* asset = path ? AAssetManager_open(assetManager, path->c_str(), AASSET_MODE_RANDOM) : nullptr) {
            if (auto data = readAsset(asset, dataRange)) {
                response.data = std::make_shared<std::string>(std::move(*data));
            } else {
                response.error = std::make_unique<Response::Error>(Response::Error::Reason::Other,
                                                                   "Could not read asset");
            }
            AAsset_close(asset);
        } else {
            response.error = std::make_unique<Response::Error>(Response::Error::Reason::NotFound,
                                                               "Could not read asset");
        }
        req.invoke(&FileSourceRequest::setResponse, response);
    }

    void setResourceOptions(ResourceOptions options) { resourceOptions = std::move(options); }
    ResourceOptions getResourceOptions() { return resourceOptions.clone(); }
    void setClientOptions(ClientOptions options) { clientOptions = std::move(options); }
    ClientOptions getClientOptions() { return clientOptions.clone(); }

private:
    AAssetManager* assetManager;
    ResourceOptions resourceOptions;
    ClientOptions clientOptions;
};

class AndroidAssetFileSource final : public FileSource {
public:
    AndroidAssetFileSource(AAssetManager* assetManager, const ResourceOptions& resourceOptions,
                           const ClientOptions& clientOptions)
        : impl(std::make_unique<util::Thread<AndroidAssetReader>>(
              util::makeThreadPrioritySetter(platform::EXPERIMENTAL_THREAD_PRIORITY_FILE),
              "AndroidAssetFileSource",
              assetManager,
              resourceOptions.clone(),
              clientOptions.clone())) {}

    std::unique_ptr<AsyncRequest> request(const Resource& resource, Callback callback) override {
        auto req = std::make_unique<FileSourceRequest>(std::move(callback));
        impl->actor().invoke(&AndroidAssetReader::request, resource.url, resource.dataRange, req->actor());
        return req;
    }

    bool canRequest(const Resource& resource) const override {
        return androidAssetPathFromUrl(resource.url).has_value();
    }

    void pause() override { impl->pause(); }
    void resume() override { impl->resume(); }

    void setResourceOptions(ResourceOptions options) override {
        impl->actor().invoke(&AndroidAssetReader::setResourceOptions, options.clone());
    }
    ResourceOptions getResourceOptions() override {
        return impl->actor().ask(&AndroidAssetReader::getResourceOptions).get();
    }

    void setClientOptions(ClientOptions options) override {
        impl->actor().invoke(&AndroidAssetReader::setClientOptions, options.clone());
    }
    ClientOptions getClientOptions() override { return impl->actor().ask(&AndroidAssetReader::getClientOptions).get(); }

private:
    std::unique_ptr<util::Thread<AndroidAssetReader>> impl;
};

} // namespace
} // namespace mln

mln_status_t mln_android_set_asset_manager(void* jni_env, void* asset_manager_jobject) noexcept {
    if (!jni_env || !asset_manager_jobject)
        return MLN_INVALID_ARG;
    static std::once_flag once;
    std::call_once(once, [&] {
        JNIEnv* env = reinterpret_cast<JNIEnv*>(jni_env);
        // AAssetManager_fromJava's pointer is only valid while the Java object lives, so hold
        // a global reference to it for the life of the process. Never released: the
        // application's AssetManager outlives every map anyway.
        jobject global = env->NewGlobalRef(reinterpret_cast<jobject>(asset_manager_jobject));
        AAssetManager* assetManager = AAssetManager_fromJava(env, global);
        if (!assetManager) return;
        mln::FileSourceManager::get()->registerFileSourceFactory(
            mln::FileSourceType::Asset,
            [assetManager](const mln::ResourceOptions& resourceOptions, const mln::ClientOptions& clientOptions)
                -> std::unique_ptr<mln::FileSource> {
                return std::make_unique<mln::AndroidAssetFileSource>(assetManager, resourceOptions, clientOptions);
            });
    });
    return MLN_OK;
}

#endif // __ANDROID__
