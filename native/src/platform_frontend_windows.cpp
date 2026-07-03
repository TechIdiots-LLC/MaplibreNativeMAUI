/**
 * platform_frontend_windows.cpp — Windows frontend.
 *
 * When built with MLN_WITH_OPENGL (MLN_RENDER_BACKEND_OPENGL defined by mbgl-core):
 *   WGL OpenGL frontend.
 *   Expects the caller (C# MaplibreMapHost) to:
 *     1. Create a Win32 child HWND with CS_OWNDC | CS_DBLCLKS
 *     2. Create a WGL context on that DC
 *     3. Pass the HDC and HGLRC as void* to mbgl_frontend_create_gl()
 *   The render_callback is invoked after each frame so the caller can
 *   call SwapBuffers on its own DC.
 *
 * When built with any other backend (e.g. MLN_WITH_VULKAN):
 *   Provides a stub that throws — Vulkan Windows frontend is not yet implemented.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "platform_frontend.hpp"

#ifdef MLN_RENDER_BACKEND_OPENGL

#include <mbgl/gl/renderable_resource.hpp>
#include <mbgl/gl/renderer_backend.hpp>
#include <mbgl/renderer/renderer.hpp>
#include <mbgl/renderer/update_parameters.hpp>
#include <mbgl/gfx/backend_scope.hpp>
#include <memory>
#include <mutex>

#include "null_map_observer.hpp"

/* ── Renderable resource ────────────────────────────────────────────── */
class WGLRenderableResource : public mbgl::gl::RenderableResource {
public:
    WGLRenderableResource(class WGLBackend& backend) : _backend(backend) {}
    void bind() override;
private:
    class WGLBackend& _backend;
};

/* ── WGL backend ────────────────────────────────────────────────────── */
class WGLBackend : public mbgl::gl::RendererBackend,
                   public mbgl::gfx::Renderable {
public:
    WGLBackend(HDC hDC, HGLRC hGLRC, mbgl::Size sz)
        : mbgl::gfx::Renderable(sz, std::make_unique<WGLRenderableResource>(*this))
        , mbgl::gl::RendererBackend(mbgl::gfx::ContextMode::Shared)
        , _hDC(hDC), _hGLRC(hGLRC)
    {}

    mbgl::gfx::Renderable& getDefaultRenderable() override { return *this; }
    void setSize(mbgl::Size sz) { this->size = sz; }

protected:
    void activate()   override { wglMakeCurrent(_hDC, _hGLRC); }
    void deactivate() override { wglMakeCurrent(nullptr, nullptr); }
    mbgl::gl::ProcAddress getExtensionFunctionPointer(const char* name) override {
        return reinterpret_cast<mbgl::gl::ProcAddress>(wglGetProcAddress(name));
    }
    // Re-sync mbgl's cached GL state to match what is actually current on the
    // context. The host (.NET MAUI/WPF controller) calls glBindFramebuffer(0),
    // glViewport, glClearColor, and glClear before each Render() call, so we
    // must tell mbgl to treat those values as unknown.
    //
    // Using ContextMode::Shared (above) causes Context::createCommandEncoder()
    // to call setDirtyState() automatically, which marks ALL GL state (blend,
    // stencil, program, textures, etc.) as dirty so mbgl re-applies each one
    // unconditionally. This prevents stale cached state from causing incorrect
    // rendering of multi-pass effects like hillshade (which is the root cause
    // of grey/white artifacts in hillshade and color-relief layers).
    //
    // We still call assumeFramebufferBinding and assumeViewport here because
    // setDirtyState() explicitly skips those (see the comment in context.cpp:
    // "does not set viewport/bindFramebuffer to dirty since they are handled
    // separately in the view object").
    void updateAssumedState() override {
        assumeFramebufferBinding(ImplicitFramebufferBinding);
        assumeViewport(0, 0, size);
    }

private:
    HDC   _hDC;
    HGLRC _hGLRC;
};

void WGLRenderableResource::bind() {
    _backend.setFramebufferBinding(0);
    _backend.setViewport(0, 0, _backend.getSize());
}

/* ── WGL frontend ───────────────────────────────────────────────────── */
class WGLFrontend : public PlatformFrontend {
public:
    WGLFrontend(HDC hDC, HGLRC hGLRC, mbgl::Size sz, float pixelRatio,
                mbgl_render_fn renderCb, void* renderUd)
        : _backend(hDC, hGLRC, sz)
        , _renderer(std::make_unique<mbgl::Renderer>(_backend, pixelRatio))
        , _renderCb(renderCb), _renderUd(renderUd)
    {}

    ~WGLFrontend() override {
        mbgl::gfx::BackendScope guard(_backend, mbgl::gfx::BackendScope::ScopeType::Implicit);
        _renderer.reset();
    }

    /* RendererFrontend */
    void reset() override { _renderer.reset(); }

    void setObserver(mbgl::RendererObserver& obs) override {
        _renderer->setObserver(&obs);
    }

    void update(std::shared_ptr<mbgl::UpdateParameters> params) override {
        {
            std::unique_lock<std::mutex> lock(_mutex);
            _updateParams = std::move(params);
        }
        if (_renderCb) _renderCb(_renderUd);
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

    void setSize(mbgl::Size sz) override {
        _backend.setSize(sz);
    }

    mbgl::Size getSize() const override { return _backend.getSize(); }

    mbgl::MapObserver& getObserver() override { return _nullObserver; }
    mbgl::Renderer* getRenderer() override { return _renderer.get(); }
    const mbgl::TaggedScheduler& getThreadPool() const override { return const_cast<WGLBackend&>(_backend).getThreadPool(); }

private:
    WGLBackend                                _backend;
    std::unique_ptr<mbgl::Renderer>           _renderer;
    mbgl_render_fn                            _renderCb;
    void*                                     _renderUd;
    std::shared_ptr<mbgl::UpdateParameters>   _updateParams;
    std::mutex                                _mutex;
    NullMapObserver                           _nullObserver;
};

/* ── Factory (called by mln_cabi.cpp) ──────────────────────────────── */
PlatformFrontend* createPlatformFrontend(
    void* surface_handle, void* gl_context,
    mbgl::Size sz, float pixelRatio,
    mbgl_render_fn renderCb, void* renderUd)
{
    return new WGLFrontend(
        reinterpret_cast<HDC>(surface_handle),
        reinterpret_cast<HGLRC>(gl_context),
        sz, pixelRatio, renderCb, renderUd
    );
}

#else  // non-OpenGL build (e.g. Vulkan) — stub until a Vulkan frontend is implemented

#include <stdexcept>

PlatformFrontend* createPlatformFrontend(
    void* /*surface_handle*/, void* /*gl_context*/,
    mbgl::Size /*sz*/, float /*pixelRatio*/,
    mbgl_render_fn /*renderCb*/, void* /*renderUd*/)
{
    throw std::runtime_error(
        "Windows Vulkan frontend is not yet implemented. "
        "This build was compiled without MLN_RENDER_BACKEND_OPENGL.");
}

#endif  // MLN_RENDER_BACKEND_OPENGL
