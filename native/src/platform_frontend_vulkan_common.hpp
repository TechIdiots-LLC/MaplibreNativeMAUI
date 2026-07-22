/**
 * platform_frontend_vulkan_common.hpp — shared Vulkan PlatformFrontend.
 *
 * The Windows, Android, and Apple Vulkan builds differ only in how the render
 * surface is created (offscreen image / ANativeWindow / CAMetalLayer). Everything
 * else — owning the mbgl::Renderer, marshalling UpdateParameters onto the render
 * thread, driving render()/setSize() — is identical, so it lives here.
 *
 * Each platform's frontend .cpp defines a `Backend` deriving from
 * mbgl::vulkan::RendererBackend + mbgl::vulkan::Renderable that provides:
 *     Backend(<platform surface args...>, mbgl::Size, float pixelRatio)  // calls init()
 *     mbgl::Size getSize() const;
 *     void       setSize(mbgl::Size);
 *     const mbgl::TaggedScheduler& getThreadPool();
 *     void*      getNativeView();                     // nullptr unless a view is created (Apple)
 *     bool       readPixels(uint8_t* out, size_t len); // false unless offscreen read-back (Windows)
 * and instantiates VulkanFrontendT<Backend> from createPlatformFrontend().
 */
#pragma once

#include "platform_frontend.hpp"
#include "null_map_observer.hpp"

#include <mbgl/gfx/backend_scope.hpp>
#include <mbgl/renderer/renderer.hpp>
#include <mbgl/renderer/renderer_observer.hpp>
#include <mbgl/renderer/update_parameters.hpp>

#include <memory>
#include <mutex>
#include <utility>

template <class Backend>
class VulkanFrontendT final : public PlatformFrontend {
public:
    template <class... BackendArgs>
    VulkanFrontendT(float pixelRatio, mbgl_render_fn renderCb, void* renderUd, BackendArgs&&... args)
        : _backend(std::forward<BackendArgs>(args)...)
        , _renderer(std::make_unique<mbgl::Renderer>(_backend, pixelRatio))
        , _renderCb(renderCb), _renderUd(renderUd)
    {}

    ~VulkanFrontendT() override {
        mbgl::gfx::BackendScope guard(_backend, mbgl::gfx::BackendScope::ScopeType::Implicit);
        _renderer.reset();
    }

    /* RendererFrontend */
    void reset() override { _renderer.reset(); }

    void setObserver(mbgl::RendererObserver& obs) override { _renderer->setObserver(&obs); }

    void update(std::shared_ptr<mbgl::UpdateParameters> params) override {
        {
            std::unique_lock<std::mutex> lock(_mutex);
            _updateParams = std::move(params);
        }
        if (_renderCb) _renderCb(_renderUd);
    }

    const mbgl::TaggedScheduler& getThreadPool() const override {
        return const_cast<Backend&>(_backend).getThreadPool();
    }

    /* PlatformFrontend */
    void render() override {
        std::shared_ptr<mbgl::UpdateParameters> params;
        {
            std::unique_lock<std::mutex> lock(_mutex);
            params = std::move(_updateParams);
        }
        if (!params) return;
        mbgl::gfx::BackendScope guard(_backend, mbgl::gfx::BackendScope::ScopeType::Implicit);
        _renderer->render(params);
    }

    void setSize(mbgl::Size sz) override { _backend.setSize(sz); }
    mbgl::Size getSize() const override { return _backend.getSize(); }

    mbgl::MapObserver& getObserver() override { return _nullObserver; }
    mbgl::Renderer* getRenderer() override { return _renderer.get(); }

    void* getNativeView() override { return _backend.getNativeView(); }
    bool  readPixels(uint8_t* out, size_t len) override { return _backend.readPixels(out, len); }

private:
    Backend                                 _backend;
    std::unique_ptr<mbgl::Renderer>         _renderer;
    mbgl_render_fn                          _renderCb;
    void*                                   _renderUd;
    std::shared_ptr<mbgl::UpdateParameters> _updateParams;
    std::mutex                              _mutex;
    NullMapObserver                         _nullObserver;
};
