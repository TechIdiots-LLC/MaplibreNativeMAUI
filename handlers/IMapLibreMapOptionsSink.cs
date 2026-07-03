using MapLibreNative.Maui.Handlers.Geometry;

namespace MapLibreNative.Maui.Handlers;

public interface IMapLibreMapOptionsSink
{
    void SetCompassEnabled(bool compassEnabled);

    // TODO: styleString is not actually a part of options. consider moving
    void SetStyleString(string styleString);

    void SetMinMaxZoomPreference(double? min, double? max);

    void SetRotateGesturesEnabled(bool rotateGesturesEnabled);

    void SetScrollGesturesEnabled(bool scrollGesturesEnabled);

    void SetTiltGesturesEnabled(bool tiltGesturesEnabled);

    void SetTrackCameraPosition(bool trackCameraPosition);

    void SetZoomGesturesEnabled(bool zoomGesturesEnabled);

    void SetMyLocationEnabled(bool myLocationEnabled);

    void SetMyLocationTrackingMode(int myLocationTrackingMode);

    void SetMyLocationRenderMode(int myLocationRenderMode);

    void SetLogoViewMargins(int x, int y);

    void SetCompassGravity(int gravity);

    void SetCompassViewMargins(int x, int y);

    void SetAttributionButtonGravity(int gravity);

    void SetAttributionButtonMargins(int x, int y);

    /// <summary>Show or hide the zoom/compass navigation overlay.</summary>
    void SetShowNavigationControls(bool show);

    /// <summary>
    /// Show or hide the attribution overlay. When shown, attributions are read
    /// from all loaded TileJSON sources; <paramref name="customAttribution"/> is
    /// appended after them.
    /// </summary>
    void SetShowAttributionControl(bool show, string? customAttribution);

    /// <summary>Show or hide the GPS tracking control overlay.</summary>
    void SetShowGpsControl(bool show);

    /// <summary>Set the corner the navigation control is anchored to.</summary>
    void SetNavigationControlPosition(MapControlCorner corner);

    /// <summary>Set the corner the GPS control is anchored to.</summary>
    void SetGpsControlPosition(MapControlCorner corner);

    /// <summary>Set the corner the attribution control is anchored to.</summary>
    void SetAttributionControlPosition(MapControlCorner corner);

    //void SetLocationEngineProperties(LocationEngineRequest? locationEngineRequest);
}