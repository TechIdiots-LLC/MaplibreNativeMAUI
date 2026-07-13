namespace MapLibreNative.Maui;

/// <summary>
/// How the GPS control chooses the camera zoom when Follow mode engages
/// (via the on-map GPS button, or the first fix that arrives while following).
/// Later fixes never change the zoom, so a manual pinch/scroll zoom sticks
/// until Follow is re-entered.
/// </summary>
public enum GpsFollowZoomMode
{
    /// <summary>
    /// Keep the current zoom, only zooming in to a sensible level (14) when the
    /// map is very far out (zoom &lt; 8). The historical behaviour; the default.
    /// </summary>
    KeepCurrent,

    /// <summary>
    /// Compute the zoom from the fix's reported accuracy so the accuracy circle
    /// is comfortably visible on screen — a good fix lands at street level, a
    /// poor cell-tower-grade fix stays zoomed out to cover its uncertainty.
    /// </summary>
    Accuracy,

    /// <summary>Always ease to the fixed level in <c>GpsFollowZoom</c>.</summary>
    Fixed,
}
