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
#include <mln/gl/renderable_resource.hpp>
#include <mln/gl/renderer_backend.hpp>
#include <mln/renderer/renderer.hpp>
#include <mln/renderer/update_parameters.hpp>
#include <mln/gfx/backend_scope.hpp>
#include <android/native_window.h>
#include <memory>
#include <mutex>

#include "null_map_observer.hpp"

/* ── EGL renderable resource ────────────────────────────────────────── */
class EGLRenderableResource : public mln::gl::RenderableResource {
public:
    EGLRenderableResource(class EGLBackend& b) : _backend(b) {}
    void bind() override;
private:
    class EGLBackend& _backend;
};

/* ── EGL backend ─────────────────────────────────────────────────────── */
class EGLBackend : public mln::gl::RendererBackend,
                   public mln::gfx::Renderable {
public:
    EGLBackend(ANativeWindow* window, mln::Size sz)
        : mln::gfx::Renderable(sz, std::make_unique<EGLRenderableResource>(*this))
        , mln::gl::RendererBackend(mln::gfx::ContextMode::Unique)
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
    void setSize(mln::Size sz) {
        if (sz.width == getSize().width && sz.height == getSize().height) return;
        setRenderableSize(sz);
        if (_window) {
            ANativeWindow_setBuffersGeometry(_window,
                static_cast<int32_t>(sz.width), static_cast<int32_t>(sz.height), 0);
        }
        if (_surface != EGL_NO_SURFACE) {
            eglDestroySurface(_display, _surface);
        }
        _surface = eglCreateWindowSurface(_display, _config, _window, nullptr);
    }
    mln::gfx::Renderable& getDefaultRenderable() override { return *this; }

    void swapBuffers() { eglSwapBuffers(_display, _surface); }

protected:
    void activate()   override { eglMakeCurrent(_display, _surface, _surface, _context); }
    void deactivate() override { eglMakeCurrent(_display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT); }
    mln::gl::ProcAddress getExtensionFunctionPointer(const char* name) override {
        return reinterpret_cast<mln::gl::ProcAddress>(eglGetProcAddress(name));
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
        glViewport(0, 0, static_cast<GLsizei>(getSize().width), static_cast<GLsizei>(getSize().height));
        assumeViewport(0, 0, getSize());
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
    EGLFrontend(ANativeWindow* window, mln::Size sz, float pixelRatio,
                mln_render_fn renderCb, void* renderUd)
        : _backend(window, sz)
        , _renderer(std::make_unique<mln::Renderer>(_backend, pixelRatio))
        , _renderCb(renderCb), _renderUd(renderUd)
    {}

    ~EGLFrontend() override {
        // Unlike Windows/Apple, nothing on the Android side ever calls
        // eglMakeCurrent() outside of EGLBackend::activate(). ScopeType::Implicit
        // assumes the context is already current (true on Windows, where the C#
        // caller calls wglMakeCurrent itself) and is a no-op otherwise, so this
        // must be Explicit to actually make our EGL context current.
        mln::gfx::BackendScope guard(_backend, mln::gfx::BackendScope::ScopeType::Explicit);
        _renderer.reset();
    }

    void reset() override { _renderer.reset(); }

    void setObserver(mln::RendererObserver& obs) override {
        _renderer->setObserver(&obs);
    }

    void update(std::shared_ptr<mln::UpdateParameters> params) override {
        {
            std::unique_lock<std::mutex> lock(_mutex);
            _updateParams = std::move(params);
        }
        if (_renderCb) _renderCb(_renderUd);
    }

    void render() override {
        std::shared_ptr<mln::UpdateParameters> params;
        {
            std::unique_lock<std::mutex> lock(_mutex);
            params = std::move(_updateParams);
        }
        if (!params) return;
        // Explicit: see comment in ~EGLFrontend() above — nothing else ever
        // makes our EGL context current on Android.
        mln::gfx::BackendScope guard(_backend, mln::gfx::BackendScope::ScopeType::Explicit);
        _renderer->render(params);
        _backend.swapBuffers();
    }

    void setSize(mln::Size sz) override { _backend.setSize(sz); }
    mln::Size getSize() const override { return _backend.getSize(); }
    mln::MapObserver& getObserver() override { return _nullObserver; }
    mln::Renderer* getRenderer() override { return _renderer.get(); }
    const mln::TaggedScheduler& getThreadPool() const override { return const_cast<EGLBackend&>(_backend).getThreadPool(); }

private:
    EGLBackend                              _backend;
    std::unique_ptr<mln::Renderer>         _renderer;
    mln_render_fn                          _renderCb;
    void*                                   _renderUd;
    std::shared_ptr<mln::UpdateParameters> _updateParams;
    std::mutex                              _mutex;
    NullMapObserver                         _nullObserver;
};

PlatformFrontend* createPlatformFrontend(
    void* surface_handle, void* /*gl_context*/,
    mln::Size sz, float pixelRatio,
    mln_render_fn renderCb, void* renderUd)
{
    return new EGLFrontend(
        reinterpret_cast<ANativeWindow*>(surface_handle),
        sz, pixelRatio, renderCb, renderUd
    );
}

#else  // Vulkan build — render into the TextureView's ANativeWindow via VK_KHR_android_surface

#include "platform_frontend_vulkan_common.hpp"

#include <mln/vulkan/renderer_backend.hpp>
#include <mln/vulkan/renderable_resource.hpp>
#include <mln/vulkan/context.hpp>

#include <android/native_window.h>
#include <vulkan/vulkan_android.h>

#include <vector>

namespace {

class AndroidVulkanBackend;

/* ── Surface resource (mirrors maplibre-native android_vulkan_renderer_backend) ── */
class AndroidVulkanResource final : public mln::vulkan::SurfaceRenderableResource {
public:
    explicit AndroidVulkanResource(AndroidVulkanBackend& b);

    std::vector<const char*> getDeviceExtensions() override { return {VK_KHR_SWAPCHAIN_EXTENSION_NAME}; }
    void createPlatformSurface() override;
    void bind() override {}
};

/* ── Backend ─────────────────────────────────────────────────────────────────── */
class AndroidVulkanBackend final : public mln::vulkan::RendererBackend,
                                   public mln::vulkan::Renderable {
public:
    AndroidVulkanBackend(ANativeWindow* window, mln::Size sz)
        : mln::vulkan::RendererBackend(mln::gfx::ContextMode::Unique),
          mln::vulkan::Renderable(sz, std::make_unique<AndroidVulkanResource>(*this)),
          _window(window) {
        init();
    }
    ~AndroidVulkanBackend() override { context.reset(); }

    ANativeWindow* getWindow() const { return _window; }

    mln::gfx::Renderable& getDefaultRenderable() override { return *this; }

    // Backend contract required by VulkanFrontendT<Backend>.
    mln::Size getSize() const { return mln::gfx::Renderable::getSize(); }
    void setSize(mln::Size sz) {
        setRenderableSize(sz);
        if (context) static_cast<mln::vulkan::Context&>(*context).requestSurfaceUpdate();
    }
    void* getNativeView() { return nullptr; }        // presents into the ANativeWindow directly
    bool  readPixels(uint8_t*, size_t) { return false; }

protected:
    std::vector<const char*> getInstanceExtensions() override {
        auto ext = mln::vulkan::RendererBackend::getInstanceExtensions();
        ext.push_back(VK_KHR_SURFACE_EXTENSION_NAME);
        ext.push_back(VK_KHR_ANDROID_SURFACE_EXTENSION_NAME);
        return ext;
    }
    void activate() override {}
    void deactivate() override {}

private:
    ANativeWindow* _window;
};

AndroidVulkanResource::AndroidVulkanResource(AndroidVulkanBackend& b)
    : mln::vulkan::SurfaceRenderableResource(b) {}

void AndroidVulkanResource::createPlatformSurface() {
    auto& b = static_cast<AndroidVulkanBackend&>(backend);
    const vk::AndroidSurfaceCreateInfoKHR createInfo({}, b.getWindow());
    surface = b.getInstance()->createAndroidSurfaceKHRUnique(createInfo, nullptr, b.getDispatcher());

    const int apiLevel = android_get_device_api_level();
    if (apiLevel < __ANDROID_API_Q__) setSurfaceTransformPollingInterval(30);
}

} // namespace

PlatformFrontend* createPlatformFrontend(
    void* surface_handle, void* /*context*/,
    mln::Size sz, float pixelRatio,
    mln_render_fn renderCb, void* renderUd)
{
    return new VulkanFrontendT<AndroidVulkanBackend>(
        pixelRatio, renderCb, renderUd,
        reinterpret_cast<ANativeWindow*>(surface_handle), sz);
}

#endif  // MLN_RENDER_BACKEND_OPENGL
