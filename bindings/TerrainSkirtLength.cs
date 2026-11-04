namespace MapLibreNative.Maui;

/// <summary>
/// Whether 3D terrain tiles carry vertical skirts along their edges.
/// </summary>
/// <remarks>
/// Neighbouring terrain tiles at different zoom levels do not meet exactly, and the skirts
/// hide the hairline gaps between them. Over an opaque map that is all they do; over a
/// transparent background they show as vertical artifacts. Mirrors maplibre-gl-js's
/// <c>MapOptions.terrainSkirtLength</c>. Changing it rebuilds the terrain mesh; it has no
/// effect while terrain is off.
/// </remarks>
public enum TerrainSkirtLength
{
    /// <summary>
    /// Skirt every tile edge by about a fifth of the tile's width at the current zoom. The default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// No skirts: nothing shows through a transparent background, at the cost of visible
    /// stitches between tiles at different zoom levels.
    /// </summary>
    None = 1,
}
