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

#else  // Vulkan build — offscreen (headless) render + CPU read-back into the in-tree bitmap

#include "null_map_observer.hpp"

#include <mbgl/vulkan/headless_backend.hpp>
#include <mbgl/renderer/renderer.hpp>
#include <mbgl/renderer/update_parameters.hpp>
#include <mbgl/gfx/backend_scope.hpp>
#include <mbgl/util/image.hpp>

#include <cstring>
#include <memory>
#include <mutex>
#include <vector>
#include <fstream>
#include <string>

// Lifecycle tracing to localise the Vulkan-Windows crash. Writes (and flushes) each
// step to %TEMP%\mln_vulkan_diag.log so the last line survives a hard crash. Cheap;
// remove once the offscreen path is stable.
static void VkDiag(const char* msg) {
    char dir[MAX_PATH];
    DWORD n = GetTempPathA(MAX_PATH, dir);
    try {
        std::ofstream f(std::string(dir, n) + "mln_vulkan_diag.log", std::ios::app);
        f << msg << "\n";
    } catch (...) { /* ignore */ }
}

/* Offscreen Vulkan frontend. There is no HWND / window surface: the map renders
 * into a headless color texture and the managed layer pulls the pixels back via
 * mbgl_frontend_read_pixels() and blits them into the WriteableBitmap. Same
 * airspace-free, in-tree model as the WGL path (which reads back GL-side). */
class VulkanOffscreenFrontend : public PlatformFrontend {
public:
    VulkanOffscreenFrontend(mbgl::Size sz, float pixelRatio, mbgl_render_fn cb, void* ud)
        : _size(sz)
        , _backend(sz, mbgl::gfx::Renderable::SwapBehaviour::NoFlush, mbgl::gfx::ContextMode::Unique)
        , _renderer(std::make_unique<mbgl::Renderer>(_backend, pixelRatio))
        , _renderCb(cb), _renderUd(ud)
    { VkDiag("ctor: backend+renderer constructed"); }

    ~VulkanOffscreenFrontend() override {
        VkDiag("dtor: begin");
        mbgl::gfx::BackendScope guard(_backend, mbgl::gfx::BackendScope::ScopeType::Implicit);
        _renderer.reset();
        VkDiag("dtor: end");
    }

    /* RendererFrontend */
    void reset() override { _renderer.reset(); }
    void setObserver(mbgl::RendererObserver& obs) override { _renderer->setObserver(&obs); }
    void update(std::shared_ptr<mbgl::UpdateParameters> params) override {
        VkDiag("update");
        { std::unique_lock<std::mutex> lock(_mutex); _updateParams = std::move(params); }
        if (_renderCb) _renderCb(_renderUd);
    }
    const mbgl::TaggedScheduler& getThreadPool() const override {
        return const_cast<mbgl::vulkan::HeadlessBackend&>(_backend).getThreadPool();
    }

    /* PlatformFrontend */
    void render() override {
        std::shared_ptr<mbgl::UpdateParameters> params;
        { std::unique_lock<std::mutex> lock(_mutex); params = std::move(_updateParams); }
        if (!params) return;
        // Default (Explicit) scope: the headless backend's activate() creates its impl
        // and validates the Vulkan context — Implicit would skip that. Read the frame
        // back inside the SAME scope, while the just-rendered image + context are still
        // live; reading it in a separate scope tears frame resources down first and
        // corrupts the heap. readStillImage() waits for the frame and copies the image.
        VkDiag("render: begin");
        mbgl::gfx::BackendScope guard(_backend);
        _renderer->render(params);
        VkDiag("render: renderer->render done");
        try {
            mbgl::PremultipliedImage img = _backend.readStillImage();
            VkDiag("render: readStillImage done");
            _lastImage.assign(img.data.get(), img.data.get() + img.bytes());
            VkDiag("render: cached frame");
        } catch (...) { VkDiag("render: readStillImage threw"); }
        VkDiag("render: end");
    }

    void setSize(mbgl::Size sz) override { VkDiag("setSize"); _size = sz; _backend.setSize(sz); }
    mbgl::Size getSize() const override { return _size; }
    mbgl::MapObserver& getObserver() override { return _nullObserver; }
    mbgl::Renderer* getRenderer() override { return _renderer.get(); }

    bool readPixels(uint8_t* out, size_t len) override {
        const size_t need = static_cast<size_t>(_size.width) * _size.height * 4u;
        if (!out || len < need || _lastImage.size() < need) return false;
        std::memcpy(out, _lastImage.data(), need);
        return true;
    }

private:
    mbgl::Size                               _size;
    mbgl::vulkan::HeadlessBackend            _backend;
    std::unique_ptr<mbgl::Renderer>          _renderer;
    std::vector<uint8_t>                     _lastImage;   // most recent frame, RGBA
    mbgl_render_fn                           _renderCb;
    void*                                    _renderUd;
    std::shared_ptr<mbgl::UpdateParameters>  _updateParams;
    std::mutex                               _mutex;
    NullMapObserver                          _nullObserver;
};

PlatformFrontend* createPlatformFrontend(
    void* /*surface_handle*/, void* /*gl_context*/,
    mbgl::Size sz, float pixelRatio,
    mbgl_render_fn renderCb, void* renderUd)
{
    VkDiag("create: begin");
    auto* fe = new VulkanOffscreenFrontend(sz, pixelRatio, renderCb, renderUd);
    VkDiag("create: end ok");
    return fe;
}

#endif  // MLN_RENDER_BACKEND_OPENGL
