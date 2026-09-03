using MapLibreNative.Maui.Handlers;

namespace MauiSample;

/// <summary>
/// Shared helper for the sample's map pages, where the map is pinned in the top row
/// of a Grid and only the controls beneath it scroll. The map must not scroll: the
/// on-map controls are its children, so a scrolling map carries them out of view.
/// </summary>
internal static class MapPageLayout
{
    // Fraction of the page height the map occupies. Keeping the map shorter
    // than the viewport leaves a strip of controls always visible below it —
    // that strip is the scroll grab-area, and it's what keeps "drag the map to
    // pan" and "drag the controls to scroll" unambiguous. Applies in both
    // orientations since it's a fraction of the current page height.
    private const double MapHeightFraction = 0.62;
    private const double MinMapHeight = 200;

    /// <summary>Size the map to a fraction of the page so a control strip
    /// always peeks below it. Call from a page's OnSizeAllocated.</summary>
    public static void SizeToViewport(this MapLibreMap map, double pageHeight)
    {
        if (pageHeight > 0)
            map.HeightRequest = Math.Max(MinMapHeight, pageHeight * MapHeightFraction);
    }
}
