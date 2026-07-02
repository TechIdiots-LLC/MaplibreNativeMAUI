namespace MapLibreNative.Maui;

/// <summary>
/// The corner of the map that an on-map overlay control (navigation, GPS or
/// attribution) is anchored to.
/// </summary>
/// <remarks>
/// When multiple controls are assigned to the same corner they stack in a fixed
/// order — navigation, then GPS, then attribution — anchored from that corner
/// inward. For the top corners the first control sits at the top and later ones
/// stack downward; for the bottom corners the first control sits at the bottom
/// and later ones stack upward.
/// </remarks>
public enum MapControlCorner
{
    /// <summary>Top-left corner.</summary>
    TopLeft,

    /// <summary>Top-right corner.</summary>
    TopRight,

    /// <summary>Bottom-left corner.</summary>
    BottomLeft,

    /// <summary>Bottom-right corner.</summary>
    BottomRight,
}
