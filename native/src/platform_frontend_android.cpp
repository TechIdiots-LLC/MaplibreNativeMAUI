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
#include <GLES2/gl2.h>
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
        , _window(window)
    {
        _display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
        eglInitialize(_display, nullptr, nullptr);

        // mbgl-core's fill layer renderer relies on the stencil buffer for
        // polygon tessellation and tile-boundary clipping (matching the
        // depth/stencil pixel format explicitly requested on Windows in
        // HiddenWglContext.Windows.cs). Without EGL_STENCIL_SIZE/EGL_DEPTH_SIZE
        // here, eglChooseConfig can hand back a config with zero stencil bits,
        // which shows up as a checkerboard pattern in fills and as seams/gaps
        // between tiles.
        const EGLint attribs[] = {
            EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
            EGL_SURFACE_TYPE,    EGL_WINDOW_BIT,
            EGL_BLUE_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_RED_SIZE, 8,
            EGL_DEPTH_SIZE, 24, EGL_STENCIL_SIZE, 8,
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

    // Resizing the EGL window surface in place doesn't reliably take on
    // Android across every creation/rotation ordering: eglCreateWindowSurface()
    // can bind to whatever buffer geometry the ANativeWindow had at creation
    // time, and ANativeWindow_setBuffersGeometry() alone isn't always enough
    // to bring an *existing* EGL surface's actual dimensions in sync — some
    // paths still leave content confined to (and misaligned within) the
    // surface's original shape after a resize. Destroying and recreating the
    // EGL surface whenever the size actually changes sidesteps the ambiguity
    // entirely: the new surface is always created fresh against the
    // just-resized native window, so there's no stale surface state to fall
    // out of sync with what mbgl-core thinks the size is. The shared
    // `_context` is simply rebound to the new surface on the next activate().
    void setSize(mbgl::Size sz) {
        if (sz.width == this->size.width && sz.height == this->size.height) return;
        this->size = sz;
        if (_window) {
            ANativeWindow_setBuffersGeometry(_window,
                static_cast<int32_t>(sz.width), static_cast<int32_t>(sz.height), 0);
        }
        if (_surface != EGL_NO_SURFACE) {
            eglDestroySurface(_display, _surface);
        }
        _surface = eglCreateWindowSurface(_display, _config, _window, nullptr);
    }
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
    //
    // assumeViewport() only updates mbgl-core's *cached* notion of the
    // current viewport (Context::viewport.setCurrentValue) — it does not
    // call glViewport() itself. glViewport is GL *context* state, not
    // surface state, so it does not reset when the EGL surface is resized or
    // recreated while the same context stays current: the real hardware
    // viewport stays frozen at whatever it was last explicitly set to. Once
    // that happens, mbgl-core's cache and the real GL state silently
    // disagree, and every later "no-op" viewport (cache already matches the
    // requested value) skips the real call forever — content keeps getting
    // rasterized into the *old* viewport rectangle regardless of how large
    // the actual framebuffer now is. Issuing the real glViewport() call here
    // keeps the assumption honest.
    void updateAssumedState() override {
        assumeFramebufferBinding(ImplicitFramebufferBinding);
        glViewport(0, 0, static_cast<GLsizei>(size.width), static_cast<GLsizei>(size.height));
        assumeViewport(0, 0, size);
    }

private:
    ANativeWindow* _window  = nullptr;
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
