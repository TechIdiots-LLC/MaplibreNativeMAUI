/**
 * platform_frontend_android.cpp — Android frontend.
 *
 * When built with MLN_WITH_OPENGL (MLN_RENDER_BACKEND_OPENGL defined by mbgl-core):
 *   Uses EGL + ANativeWindow for OpenGL ES rendering.
 *   surface_handle: ANativeWindow*
 *   gl_context:     EGLContext (or NULL to create a new context sharing with the caller)
 *
 * When built with any other backend (e.g. MLN_WITH_VULKAN):
 *   Provides a stub that throws — Vulkan Android frontend is not yet implemented.
 */
#include "platform_frontend.hpp"

#ifdef MLN_RENDER_BACKEND_OPENGL

#include <EGL/egl.h>
#include <mbgl/gl/renderable_resource.hpp>
#include <mbgl/gl/renderer_backend.hpp>
#include <mbgl/renderer/renderer.hpp>
#include <mbgl/renderer/update_parameters.hpp>
#include <mbgl/gfx/backend_scope.hpp>
#include <android/native_window.h>
#include <memory>
#include <mutex>

#include "null_map_observer.hpp"

/* ── EGL renderable resource ────────────────────────────────────────── */
class EGLRenderableResource : public mbgl::gl::RenderableResource {
public:
    EGLRenderableResource(class EGLBackend& b) : _backend(b) {}
    void bind() override;
private:
    class EGLBackend& _backend;
};

/* ── EGL backend ─────────────────────────────────────────────────────── */
class EGLBackend : public mbgl::gl::RendererBackend,
                   public mbgl::gfx::Renderable {
public:
    EGLBackend(ANativeWindow* window, mbgl::Size sz)
        : mbgl::gfx::Renderable(sz, std::make_unique<EGLRenderableResource>(*this))
        , mbgl::gl::RendererBackend(mbgl::gfx::ContextMode::Unique)
    {
        _display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
        eglInitialize(_display, nullptr, nullptr);

        const EGLint attribs[] = {
            EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
            EGL_SURFACE_TYPE,    EGL_WINDOW_BIT,
            EGL_BLUE_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_RED_SIZE, 8,
            EGL_NONE
        };
        EGLint numConfigs;
        eglChooseConfig(_display, attribs, &_config, 1, &numConfigs);

        const EGLint ctxAttribs[] = { EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE };
        _context = eglCreateContext(_display, _config, EGL_NO_CONTEXT, ctxAttribs);
        _surface = eglCreateWindowSurface(_display, _config, window, nullptr);
    }

    ~EGLBackend() {
        eglDestroySurface(_display, _surface);
        eglDestroyContext(_display, _context);
        eglTerminate(_display);
    }

    void setSize(mbgl::Size sz) { this->size = sz; }
    mbgl::gfx::Renderable& getDefaultRenderable() override { return *this; }

    void swapBuffers() { eglSwapBuffers(_display, _surface); }

protected:
    void activate()   override { eglMakeCurrent(_display, _surface, _surface, _context); }
    void deactivate() override { eglMakeCurrent(_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT); }
    mbgl::gl::ProcAddress getExtensionFunctionPointer(const char* name) override {
        return reinterpret_cast<mbgl::gl::ProcAddress>(eglGetProcAddress(name));
    }
    // Re-sync mbgl's cached GL state so it re-binds framebuffer/viewport
    // each frame. Mirrors the Apple/Metal backend in this project, the Qt
    // GL backend and GLFW. Important on Android because the TextureView's
    // SurfaceTexture can be recreated under us (config change, surface
    // destroyed) and mbgl's cache must not be trusted across context activations.
    void updateAssumedState() override {
        assumeFramebufferBinding(ImplicitFramebufferBinding);
        assumeViewport(0, 0, size);
    }

private:
    EGLDisplay _display = EGL_NO_DISPLAY;
    EGLConfig  _config  = nullptr;
    EGLContext _context = EGL_NO_CONTEXT;
    EGLSurface _surface = EGL_NO_SURFACE;
};

void EGLRenderableResource::bind() {
    _backend.setFramebufferBinding(0);
    _backend.setViewport(0, 0, _backend.getSize());
}

/* ── EGL frontend ────────────────────────────────────────────────────── */
class EGLFrontend : public PlatformFrontend {
public:
    EGLFrontend(ANativeWindow* window, mbgl::Size sz, float pixelRatio,
                mbgl_render_fn renderCb, void* renderUd)
        : _backend(window, sz)
        , _renderer(std::make_unique<mbgl::Renderer>(_backend, pixelRatio))
        , _renderCb(renderCb), _renderUd(renderUd)
    {}

    ~EGLFrontend() override {
        // Unlike Windows/Apple, nothing on the Android side ever calls
        // eglMakeCurrent() outside of EGLBackend::activate(). ScopeType::Implicit
        // assumes the context is already current (true on Windows, where the C#
        // caller calls wglMakeCurrent itself) and is a no-op otherwise, so this
        // must be Explicit to actually make our EGL context current.
        mbgl::gfx::BackendScope guard(_backend, mbgl::gfx::BackendScope::ScopeType::Explicit);
        _renderer.reset();
    }

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

    void render() override {
        std::shared_ptr<mbgl::UpdateParameters> params;
        {
            std::unique_lock<std::mutex> lock(_mutex);
            params = std::move(_updateParams);
        }
        if (!params) return;
        // Explicit: see comment in ~EGLFrontend() above — nothing else ever
        // makes our EGL context current on Android.
        mbgl::gfx::BackendScope guard(_backend, mbgl::gfx::BackendScope::ScopeType::Explicit);
        _renderer->render(params);
        _backend.swapBuffers();
    }

    void setSize(mbgl::Size sz) override { _backend.setSize(sz); }
    mbgl::Size getSize() const override { return _backend.getSize(); }
    mbgl::MapObserver& getObserver() override { return _nullObserver; }
    mbgl::Renderer* getRenderer() override { return _renderer.get(); }
    const mbgl::TaggedScheduler& getThreadPool() const override { return const_cast<EGLBackend&>(_backend).getThreadPool(); }

private:
    EGLBackend                              _backend;
    std::unique_ptr<mbgl::Renderer>         _renderer;
    mbgl_render_fn                          _renderCb;
    void*                                   _renderUd;
    std::shared_ptr<mbgl::UpdateParameters> _updateParams;
    std::mutex                              _mutex;
    NullMapObserver                         _nullObserver;
};

PlatformFrontend* createPlatformFrontend(
    void* surface_handle, void* /*gl_context*/,
    mbgl::Size sz, float pixelRatio,
    mbgl_render_fn renderCb, void* renderUd)
{
    return new EGLFrontend(
        reinterpret_cast<ANativeWindow*>(surface_handle),
        sz, pixelRatio, renderCb, renderUd
    );
}

#else  // non-OpenGL build (e.g. Vulkan) — stub until a Vulkan frontend is implemented

#include <stdexcept>

PlatformFrontend* createPlatformFrontend(
    void* /*surface_handle*/, void* /*context*/,
    mbgl::Size /*sz*/, float /*pixelRatio*/,
    mbgl_render_fn /*renderCb*/, void* /*renderUd*/)
{
    throw std::runtime_error(
        "Android Vulkan frontend is not yet implemented. "
        "This build was compiled without MLN_RENDER_BACKEND_OPENGL.");
}

#endif  // MLN_RENDER_BACKEND_OPENGL
