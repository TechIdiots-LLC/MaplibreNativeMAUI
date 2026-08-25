/**
 * null_map_observer.hpp — A no-op MapObserver shared by all platform frontends.
 */
#pragma once
#include <mln/map/map_observer.hpp>

class NullMapObserver : public mln::MapObserver {
public:
    void onCameraWillChange(mln::MapObserver::CameraChangeMode) override {}
    void onCameraIsChanging() override {}
    void onCameraDidChange(mln::MapObserver::CameraChangeMode) override {}
    void onWillStartLoadingMap() override {}
    void onDidFinishLoadingMap() override {}
    void onDidFailLoadingMap(mln::MapLoadError, const std::string&) override {}
    void onWillStartRenderingFrame() override {}
    void onDidFinishRenderingFrame(const mln::MapObserver::RenderFrameStatus&) override {}
    void onWillStartRenderingMap() override {}
    void onDidFinishRenderingMap(mln::MapObserver::RenderMode) override {}
    void onDidFinishLoadingStyle() override {}
    void onSourceChanged(mln::style::Source&) override {}
    void onDidBecomeIdle() override {}
    void onStyleImageMissing(const std::string&) override {}
};
