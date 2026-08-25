/**
 * platform_frontend_vulkan_common.hpp — shared Vulkan PlatformFrontend.
 *
 * The Windows, Android, and Apple Vulkan builds differ only in how the render
 * surface is created (offscreen image / ANativeWindow / CAMetalLayer). Everything
 * else — owning the mln::Renderer, marshalling UpdateParameters onto the render
 * thread, driving render()/setSize() — is identical, so it lives here.
 *
 * Each platform's frontend .cpp defines a `Backend` deriving from
 * mln::vulkan::RendererBackend + mln::vulkan::Renderable that provides:
 *     Backend(<platform surface args...>, mln::Size, float pixelRatio)  // calls init()
 *     mln::Size getSize() const;
 *     void       setSize(mln::Size);
 *     const mln::TaggedScheduler& getThreadPool();
 *     void*      getNativeView();                     // nullptr unless a view is created (Apple)
 *     bool       readPixels(uint8_t* out, size_t len); // false unless offscreen read-back (Windows)
 * and instantiates VulkanFrontendT<Backend> from createPlatformFrontend().
 */
#pragma once

#include "platform_frontend.hpp"
#include "null_map_observer.hpp"

#include <mln/gfx/backend_scope.hpp>
#include <mln/renderer/renderer.hpp>
#include <mln/renderer/renderer_observer.hpp>
#include <mln/renderer/update_parameters.hpp>

#include <memory>
#include <mutex>
#include <utility>

template <class Backend>
class VulkanFrontendT final : public PlatformFrontend {
public:
    template <class... BackendArgs>
    VulkanFrontendT(float pixelRatio, mbgl_render_fn renderCb, void* renderUd, BackendArgs&&... args)
        : _backend(std::forward<BackendArgs>(args)...)
        , _renderer(std::make_unique<mln::Renderer>(_backend, pixelRatio))
        , _renderCb(renderCb), _renderUd(renderUd)
    {}

    ~VulkanFrontendT() override {
        mln::gfx::BackendScope guard(_backend, mln::gfx::BackendScope::ScopeType::Implicit);
        _renderer.reset();
    }

    /* RendererFrontend */
    void reset() override { _renderer.reset(); }

    void setObserver(mln::RendererObserver& obs) override { _renderer->setObserver(&obs); }

    void update(std::shared_ptr<mln::UpdateParameters> params) override {
        {
            std::unique_lock<std::mutex> lock(_mutex);
            _updateParams = std::move(params);
        }
        if (_renderCb) _renderCb(_renderUd);
    }

    const mln::TaggedScheduler& getThreadPool() const override {
        return const_cast<Backend&>(_backend).getThreadPool();
    }

    /* PlatformFrontend */
    void render() override {
        std::shared_ptr<mln::UpdateParameters> params;
        {
            std::unique_lock<std::mutex> lock(_mutex);
            params = std::move(_updateParams);
        }
        if (!params) return;
        mln::gfx::BackendScope guard(_backend, mln::gfx::BackendScope::ScopeType::Implicit);
        _renderer->render(params);
    }

    void setSize(mln::Size sz) override { _backend.setSize(sz); }
    mln::Size getSize() const override { return _backend.getSize(); }

    mln::MapObserver& getObserver() override { return _nullObserver; }
    mln::Renderer* getRenderer() override { return _renderer.get(); }

    void* getNativeView() override { return _backend.getNativeView(); }
    bool  readPixels(uint8_t* out, size_t len) override { return _backend.readPixels(out, len); }

private:
    Backend                                 _backend;
    std::unique_ptr<mln::Renderer>         _renderer;
    mbgl_render_fn                          _renderCb;
    void*                                   _renderUd;
    std::shared_ptr<mln::UpdateParameters> _updateParams;
    std::mutex                              _mutex;
    NullMapObserver                         _nullObserver;
};
