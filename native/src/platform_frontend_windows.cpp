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

#include <mln/gl/renderable_resource.hpp>
#include <mln/gl/renderer_backend.hpp>
#include <mln/gl/context.hpp>
#include <mln/renderer/renderer.hpp>
#include <mln/renderer/update_parameters.hpp>
#include <mln/gfx/backend_scope.hpp>
#include <memory>
#include <mutex>

#include "null_map_observer.hpp"

/* ── Renderable resource ────────────────────────────────────────────── */
class WGLRenderableResource : public mln::gl::RenderableResource {
public:
    WGLRenderableResource(class WGLBackend& backend) : _backend(backend) {}
    void bind() override;
private:
    class WGLBackend& _backend;
};

/* ── WGL backend ────────────────────────────────────────────────────── */
class WGLBackend : public mln::gl::RendererBackend,
                   public mln::gfx::Renderable {
public:
    WGLBackend(HDC hDC, HGLRC hGLRC, mln::Size sz)
        : mln::gfx::Renderable(sz, std::make_unique<WGLRenderableResource>(*this))
        // Unique (not Shared): our WGL context is a private off-screen surface, not
        // actually shared with host-drawn content, so mbgl's own per-frame clear
        // pass (renderer_impl.cpp's commonClearPass) should run normally. Shared
        // mode makes mbgl skip that clear entirely (it assumes the host owns
        // clearing), which left stale pixels from the previous frame on screen
        // whenever the new frame didn't fully repaint the viewport (e.g. right
        // after zooming out). We still need the state re-sync Shared mode gave us
        // for free (see render() below).
        , mln::gl::RendererBackend(mln::gfx::ContextMode::Unique)
        , _hDC(hDC), _hGLRC(hGLRC)
    {}

    mln::gfx::Renderable& getDefaultRenderable() override { return *this; }
    void setSize(mln::Size sz) { this->size = sz; }

protected:
    void activate()   override { wglMakeCurrent(_hDC, _hGLRC); }
    void deactivate() override { wglMakeCurrent(nullptr, nullptr); }
    mln::gl::ProcAddress getExtensionFunctionPointer(const char* name) override {
        return reinterpret_cast<mln::gl::ProcAddress>(wglGetProcAddress(name));
    }
    // Re-sync mbgl's cached GL state to match what is actually current on the
    // context, since our host toggles other GL contexts current on the same
    // thread between frames.
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
    WGLFrontend(HDC hDC, HGLRC hGLRC, mln::Size sz, float pixelRatio,
                mbgl_render_fn renderCb, void* renderUd)
        : _backend(hDC, hGLRC, sz)
        , _renderer(std::make_unique<mln::Renderer>(_backend, pixelRatio))
        , _renderCb(renderCb), _renderUd(renderUd)
    {}

    ~WGLFrontend() override {
        mln::gfx::BackendScope guard(_backend, mln::gfx::BackendScope::ScopeType::Implicit);
        _renderer.reset();
    }

    /* RendererFrontend */
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

    /* PlatformFrontend */
    void render() override {
        std::shared_ptr<mln::UpdateParameters> params;
        {
            std::unique_lock<std::mutex> lock(_mutex);
            params = std::move(_updateParams);
        }
        if (!params) return;
        mln::gfx::BackendScope guard(_backend, mln::gfx::BackendScope::ScopeType::Implicit);
        // Mark all cached GL state dirty so mbgl re-applies it unconditionally this
        // frame, rather than trusting values it cached from a previous frame on a
        // context another WGL surface may have made current in between. This is
        // what ContextMode::Shared used to trigger for us automatically via
        // Context::createCommandEncoder(); doing it explicitly lets the backend use
        // ContextMode::Unique instead, so mbgl's per-frame clear pass isn't skipped.
        _backend.getContext<mln::gl::Context>().setDirtyState();
        _renderer->render(params);
    }

    void setSize(mln::Size sz) override {
        _backend.setSize(sz);
    }

    mln::Size getSize() const override { return _backend.getSize(); }

    mln::MapObserver& getObserver() override { return _nullObserver; }
    mln::Renderer* getRenderer() override { return _renderer.get(); }
    const mln::TaggedScheduler& getThreadPool() const override { return const_cast<WGLBackend&>(_backend).getThreadPool(); }

private:
    WGLBackend                                _backend;
    std::unique_ptr<mln::Renderer>           _renderer;
    mbgl_render_fn                            _renderCb;
    void*                                     _renderUd;
    std::shared_ptr<mln::UpdateParameters>   _updateParams;
    std::mutex                                _mutex;
    NullMapObserver                           _nullObserver;
};

/* ── Factory (called by mln_cabi.cpp) ──────────────────────────────── */
PlatformFrontend* createPlatformFrontend(
    void* surface_handle, void* gl_context,
    mln::Size sz, float pixelRatio,
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

#include <mln/vulkan/headless_backend.hpp>
#include <mln/renderer/renderer.hpp>
#include <mln/renderer/update_parameters.hpp>
#include <mln/gfx/backend_scope.hpp>
#include <mln/util/image.hpp>

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
    VulkanOffscreenFrontend(mln::Size sz, float pixelRatio, mbgl_render_fn cb, void* ud)
        : _size(sz)
        , _backend(sz, mln::gfx::Renderable::SwapBehaviour::NoFlush, mln::gfx::ContextMode::Unique)
        , _renderer(std::make_unique<mln::Renderer>(_backend, pixelRatio))
        , _renderCb(cb), _renderUd(ud)
    { VkDiag("ctor: backend+renderer constructed"); }

    ~VulkanOffscreenFrontend() override {
        VkDiag("dtor: begin");
        mln::gfx::BackendScope guard(_backend, mln::gfx::BackendScope::ScopeType::Implicit);
        _renderer.reset();
        VkDiag("dtor: end");
    }

    /* RendererFrontend */
    void reset() override { _renderer.reset(); }
    void setObserver(mln::RendererObserver& obs) override { _renderer->setObserver(&obs); }
    void update(std::shared_ptr<mln::UpdateParameters> params) override {
        VkDiag("update");
        { std::unique_lock<std::mutex> lock(_mutex); _updateParams = std::move(params); }
        if (_renderCb) _renderCb(_renderUd);
    }
    const mln::TaggedScheduler& getThreadPool() const override {
        return const_cast<mln::vulkan::HeadlessBackend&>(_backend).getThreadPool();
    }

    /* PlatformFrontend */
    void render() override {
        std::shared_ptr<mln::UpdateParameters> params;
        { std::unique_lock<std::mutex> lock(_mutex); params = std::move(_updateParams); }
        if (!params) return;
        // Default (Explicit) scope: the headless backend's activate() creates its impl
        // and validates the Vulkan context — Implicit would skip that. Read the frame
        // back inside the SAME scope, while the just-rendered image + context are still
        // live; reading it in a separate scope tears frame resources down first and
        // corrupts the heap. readStillImage() waits for the frame and copies the image.
        VkDiag("render: begin");
        mln::gfx::BackendScope guard(_backend);
        _renderer->render(params);
        VkDiag("render: renderer->render done");
        try {
            mln::PremultipliedImage img = _backend.readStillImage();
            VkDiag("render: readStillImage done");
            // The offscreen color attachment is R8G8B8A8 (see texture2d.cpp), so
            // readStillImage() hands back RGBA bytes. The managed side blits this
            // straight into a WPF WriteableBitmap created as Bgra32 (the format the
            // OpenGL path fills by explicitly requesting GL_BGRA from glReadPixels)
            // — without swapping R and B here, red and blue channels come out
            // swapped on screen. Swap in place while copying into the cache.
            const uint8_t* src = img.data.get();
            const size_t n = img.bytes();
            _lastImage.resize(n);
            for (size_t i = 0; i + 4 <= n; i += 4) {
                _lastImage[i + 0] = src[i + 2]; // B
                _lastImage[i + 1] = src[i + 1]; // G
                _lastImage[i + 2] = src[i + 0]; // R
                _lastImage[i + 3] = src[i + 3]; // A
            }
            VkDiag("render: cached frame");
        } catch (...) { VkDiag("render: readStillImage threw"); }
        VkDiag("render: end");
    }

    void setSize(mln::Size sz) override { VkDiag("setSize"); _size = sz; _backend.setSize(sz); }
    mln::Size getSize() const override { return _size; }
    mln::MapObserver& getObserver() override { return _nullObserver; }
    mln::Renderer* getRenderer() override { return _renderer.get(); }

    bool readPixels(uint8_t* out, size_t len) override {
        const size_t need = static_cast<size_t>(_size.width) * _size.height * 4u;
        if (!out || len < need || _lastImage.size() < need) return false;
        std::memcpy(out, _lastImage.data(), need);
        return true;
    }

private:
    mln::Size                               _size;
    mln::vulkan::HeadlessBackend            _backend;
    std::unique_ptr<mln::Renderer>          _renderer;
    std::vector<uint8_t>                     _lastImage;   // most recent frame, RGBA
    mbgl_render_fn                           _renderCb;
    void*                                    _renderUd;
    std::shared_ptr<mln::UpdateParameters>  _updateParams;
    std::mutex                               _mutex;
    NullMapObserver                          _nullObserver;
};

PlatformFrontend* createPlatformFrontend(
    void* /*surface_handle*/, void* /*gl_context*/,
    mln::Size sz, float pixelRatio,
    mbgl_render_fn renderCb, void* renderUd)
{
    VkDiag("create: begin");
    auto* fe = new VulkanOffscreenFrontend(sz, pixelRatio, renderCb, renderUd);
    VkDiag("create: end ok");
    return fe;
}

#endif  // MLN_RENDER_BACKEND_OPENGL
