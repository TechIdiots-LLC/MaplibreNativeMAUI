/**
 * platform_frontend_apple.mm — MTKView-based Metal frontend for iOS / macCatalyst.
 *
 * surface_handle: unused on Apple (pass nullptr); MTKView is created internally
 *                 by the Metal backend after it initialises the device.
 * gl_context:     unused (pass nullptr)
 *
 * After creating the frontend call mbgl_frontend_get_native_view() to retrieve
 * the MTKView* as a void*, then add it as a subview in the MAUI handler.
 *
 * Modelled closely on MLNMapViewMetalImpl / MLNMapViewMetalRenderableResource
 * from maplibre-native/platform/ios/src/MLNMapView+Metal.mm.
 */

#include "platform_frontend.hpp"
#include "null_map_observer.hpp"

#include <mbgl/mtl/renderer_backend.hpp>
#include <mbgl/mtl/renderable_resource.hpp>
#include <mbgl/mtl/mtl_fwd.hpp>
#include <mbgl/renderer/renderer.hpp>
#include <mbgl/renderer/update_parameters.hpp>
#include <mbgl/gfx/backend_scope.hpp>

// metal-cpp (vendored by maplibre-native)
#include <Metal/Metal.hpp>

// ObjC / MetalKit
#import <Metal/Metal.h>
#import <MetalKit/MetalKit.h>
#import <QuartzCore/CAMetalLayer.h>

#include <memory>
#include <mutex>
#include <functional>

// Forward declarations
class MetalBackend;
class MetalFrontend;

// ── MTKView delegate ───────────────────────────────────────────────────────────

@interface MbglMetalViewDelegate : NSObject <MTKViewDelegate>
- (instancetype)initWithCallback:(std::function<void()>)cb;
@end

@implementation MbglMetalViewDelegate {
    std::function<void()> _cb;
}

- (instancetype)initWithCallback:(std::function<void()>)cb {
    if ((self = [super init])) _cb = std::move(cb);
    return self;
}

- (void)mtkView:(MTKView *)view drawableSizeWillChange:(CGSize)size {}

- (void)drawInMTKView:(MTKView *)view {
    _cb();
}

@end

// ── RenderableResource ─────────────────────────────────────────────────────────

class MetalRenderableResource final : public mbgl::mtl::RenderableResource {
public:
    explicit MetalRenderableResource(MetalBackend& backend_) : _backend(backend_) {}

    void bind() override;
    void swap() override;

    const mbgl::mtl::RendererBackend& getBackend() const override;

    const mbgl::mtl::MTLCommandBufferPtr& getCommandBuffer() const override {
        return _commandBufferPtr;
    }

    mbgl::mtl::MTLBlitPassDescriptorPtr getUploadPassDescriptor() const override {
        // Allocate a fresh descriptor each call; ownership transferred to caller.
        return NS::TransferPtr(MTL::BlitPassDescriptor::alloc()->init());
    }

    const mbgl::mtl::MTLRenderPassDescriptorPtr& getRenderPassDescriptor() const override {
        return _renderPassDescPtr;
    }

    mbgl::Size framebufferSize() const {
        CGSize sz = _mtlView ? _mtlView.drawableSize : CGSizeMake(0, 0);
        return { static_cast<uint32_t>(sz.width), static_cast<uint32_t>(sz.height) };
    }

    MetalBackend&          _backend;
    MTKView*               _mtlView    = nil;
    MbglMetalViewDelegate* _delegate   = nil;
    id<MTLCommandBuffer>   _commandBuffer  = nil;
    id<MTLCommandQueue>    _commandQueue   = nil;

private:
    mbgl::mtl::MTLCommandBufferPtr     _commandBufferPtr;
    mutable mbgl::mtl::MTLRenderPassDescriptorPtr _renderPassDescPtr;
};

// ── Metal backend ──────────────────────────────────────────────────────────────

class MetalBackend : public mbgl::mtl::RendererBackend,
                     public mbgl::gfx::Renderable {
public:
    MetalBackend(mbgl::Size sz)
        : mbgl::mtl::RendererBackend(mbgl::gfx::ContextMode::Unique)
        , mbgl::gfx::Renderable(sz, std::make_unique<MetalRenderableResource>(*this))
    {}

    ~MetalBackend() override = default;

    mbgl::gfx::Renderable& getDefaultRenderable() override { return *this; }

    void setSize(mbgl::Size sz) { this->size = sz; }

    void updateAssumedState() override {
        assumeFramebufferBinding(ImplicitFramebufferBinding);
        assumeViewport(0, 0, getResource<MetalRenderableResource>().framebufferSize());
    }

    void activate() override {}
    void deactivate() override {}

    /// Creates the MTKView; must be called after the backend (and its Metal
    /// device) is fully constructed. drawCallback fires inside drawInMTKView:.
    void createView(std::function<void()> drawCallback) {
        auto& res = getResource<MetalRenderableResource>();
        if (res._mtlView) return;

        // The base class initialises 'device' lazily via getContext() on first
        // use.  Force that now so the device is available before we build the view.
        (void)this->getContext();

        id<MTLDevice> device = (__bridge id<MTLDevice>)getDevice().get();
        res._mtlView = [[MTKView alloc] initWithFrame:CGRectZero device:device];
        res._mtlView.colorPixelFormat          = MTLPixelFormatBGRA8Unorm;
        res._mtlView.depthStencilPixelFormat   = MTLPixelFormatDepth32Float_Stencil8;
        res._mtlView.autoResizeDrawable        = NO;  // we set drawableSize manually
        res._mtlView.paused                    = YES; // we drive rendering ourselves
        res._mtlView.enableSetNeedsDisplay     = NO;
#if !TARGET_OS_MACCATALYST
        // autoresizingMask is an NSView/UIView property unavailable on
        // macCatalyst (UIKit over AppKit).  We drive frame size manually
        // anyway (autoResizeDrawable = NO), so skip this on Catalyst.
        res._mtlView.autoresizingMask          =
# if TARGET_OS_IPHONE
            UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
# else
            NSViewWidthSizable | NSViewHeightSizable;
# endif
#endif

        res._delegate = [[MbglMetalViewDelegate alloc]
                         initWithCallback:std::move(drawCallback)];
        res._mtlView.delegate = res._delegate;
    }

    void* getNativeView() {
        return (__bridge void*)getResource<MetalRenderableResource>()._mtlView;
    }
};

// ── RenderableResource impl (needs full MetalBackend definition) ───────────────

const mbgl::mtl::RendererBackend& MetalRenderableResource::getBackend() const {
    return _backend;
}

void MetalRenderableResource::bind() {
    if (!_commandQueue) {
        id<MTLDevice> device = (__bridge id<MTLDevice>)_backend.getDevice().get();
        _commandQueue = [device newCommandQueue];
    }

    if (!_commandBuffer) {
        _commandBuffer    = [_commandQueue commandBuffer];
        _commandBufferPtr = NS::RetainPtr((__bridge MTL::CommandBuffer*)_commandBuffer);
    }

    // Capture the current render pass descriptor for this frame.
    if (MTLRenderPassDescriptor* desc = _mtlView.currentRenderPassDescriptor) {
        _renderPassDescPtr = NS::RetainPtr((__bridge MTL::RenderPassDescriptor*)desc);
    }
}

void MetalRenderableResource::swap() {
    if (_commandBuffer) {
        if (id<CAMetalDrawable> drawable = _mtlView.currentDrawable) {
            [_commandBuffer presentDrawable:drawable];
        }
        [_commandBuffer commit];
    }

    _commandBuffer    = nil;
    _commandBufferPtr.reset();
    _renderPassDescPtr.reset();
}

// ── Metal frontend ─────────────────────────────────────────────────────────────

class MetalFrontend final : public PlatformFrontend {
public:
    MetalFrontend(mbgl::Size sz, float pixelRatio,
                  mbgl_render_fn renderCb, void* renderUd)
        : _backend(sz)
        , _renderer(std::make_unique<mbgl::Renderer>(_backend, pixelRatio))
        , _renderCb(renderCb), _renderUd(renderUd)
    {
        // Create the MTKView now that the backend has initialised Metal.
        _backend.createView([this] { this->drawFrame(); });
    }

    ~MetalFrontend() override {
        mbgl::gfx::BackendScope guard(_backend, mbgl::gfx::BackendScope::ScopeType::Implicit);
        _renderer.reset();
    }

    void reset() override { _renderer.reset(); }

    void setObserver(mbgl::RendererObserver& obs) override {
        _renderer->setObserver(&obs);
    }

    // Called by mbgl when it wants a new frame; we signal the MAUI layer.
    void update(std::shared_ptr<mbgl::UpdateParameters> params) override {
        {
            std::unique_lock<std::mutex> lock(_mutex);
            _updateParams = std::move(params);
        }
        if (_renderCb) _renderCb(_renderUd);
    }

    // Called by the MAUI layer (from the main thread) to render the pending frame.
    void render() override {
        std::shared_ptr<mbgl::UpdateParameters> params;
        {
            std::unique_lock<std::mutex> lock(_mutex);
            params = std::move(_updateParams);
        }
        if (!params) return;

        // Store so drawFrame() (called synchronously from [mtlView draw]) can use it.
        _pendingParams = std::move(params);
        [_backend.getResource<MetalRenderableResource>()._mtlView draw];
        _pendingParams.reset();
    }

    void setSize(mbgl::Size sz) override {
        _backend.setSize(sz);
        auto& res = _backend.getResource<MetalRenderableResource>();
        if (res._mtlView) {
            res._mtlView.drawableSize = CGSizeMake(sz.width, sz.height);
        }
    }

    mbgl::Size getSize() const override { return _backend.getSize(); }
    mbgl::MapObserver& getObserver() override { return _nullObserver; }
    mbgl::Renderer* getRenderer() override { return _renderer.get(); }
    const mbgl::TaggedScheduler& getThreadPool() const override { return const_cast<MetalBackend&>(_backend).getThreadPool(); }

    void* getNativeView() override { return _backend.getNativeView(); }

private:
    // Called synchronously from drawInMTKView: (i.e. from [mtlView draw] above).
    void drawFrame() {
        if (!_pendingParams || !_renderer) return;
        mbgl::gfx::BackendScope guard(_backend, mbgl::gfx::BackendScope::ScopeType::Implicit);
        _renderer->render(_pendingParams);
    }

    MetalBackend                            _backend;
    std::unique_ptr<mbgl::Renderer>         _renderer;
    mbgl_render_fn                          _renderCb;
    void*                                   _renderUd;
    std::shared_ptr<mbgl::UpdateParameters> _updateParams;
    std::shared_ptr<mbgl::UpdateParameters> _pendingParams;
    std::mutex                              _mutex;
    NullMapObserver                         _nullObserver;
};

// ── C++ factory (called by mbgl_cabi.cpp) ─────────────────────────────────────

PlatformFrontend* createPlatformFrontend(
    void* /*surface_handle*/, void* /*gl_context*/,
    mbgl::Size sz, float pixelRatio,
    mbgl_render_fn renderCb, void* renderUd)
{
    return new MetalFrontend(sz, pixelRatio, renderCb, renderUd);
}
