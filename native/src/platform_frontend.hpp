/**
 * platform_frontend.hpp — Abstract interface for the platform rendering frontend.
 *
 * Each platform provides one implementation:
 *   Windows  : platform_frontend_windows.cpp  (WGL OpenGL)
 *   Android  : platform_frontend_android.cpp  (EGL + ANativeWindow)
 *   iOS/mac  : platform_frontend_apple.cpp    (Metal / EGL)
 */
#pragma once
#include <mln/map/map_observer.hpp>
#include <mln/renderer/renderer_frontend.hpp>
#include <mln/renderer/renderer_observer.hpp>
#include <mln/util/size.hpp>
#include <mln/actor/scheduler.hpp>
#include "mln_cabi.h"

namespace mln { class Renderer; }

class PlatformFrontend : public mln::RendererFrontend {
public:
    virtual ~PlatformFrontend() = default;

    /// Called on the render thread to actually submit the frame.
    virtual void render() = 0;

    /// Resize the rendering surface.
    virtual void setSize(mln::Size) = 0;

    /// Returns the current surface size (physical pixels).
    virtual mln::Size getSize() const = 0;

    /// Returns a default (no-op) MapObserver.
    virtual mln::MapObserver& getObserver() = 0;

    /// Returns the platform-native view created by the frontend, or nullptr.
    /// On Apple this is the MTKView*; on other platforms returns nullptr.
    virtual void* getNativeView() { return nullptr; }

    /// Returns the underlying Renderer for feature queries, or nullptr.
    virtual mln::Renderer* getRenderer() { return nullptr; }
};
