namespace MapLibreNative.Maui;

/// <summary>
/// Vector geometry for the on-map 3D-terrain control's icon, ported from
/// maplibre-gl-js's <c>src/css/svg/maplibregl-ctrl-terrain.svg</c> so the control
/// reads the same as the web map's.
/// </summary>
/// <remarks>
/// <para>
/// The SVG is two closed shapes in a 22×22 box — a mountain-range outline and the
/// bar beneath it — drawn entirely with straight lines. They are exposed here as
/// point lists rather than SVG/XAML path text so every platform can feed them to
/// its own vector primitive (WPF/WinUI <c>PathGeometry</c>, Android
/// <c>Path</c>/<c>PathShape</c>, iOS <c>UIBezierPath</c>) without a path parser.
/// </para>
/// <para>
/// Coordinates are in the <see cref="Size"/>×<see cref="Size"/> box, so a renderer
/// scales by <c>target / Size</c>. The drawn shape does not fill that box: it spans
/// roughly x 0.7–21.3 and y 4.5–18.2, matching the padding gl-js's icon has inside
/// its button. Scale by the box, not by the geometry's own bounds, or the icon
/// grows and sits off-centre.
/// </para>
/// </remarks>
public static class TerrainIcon
{
    /// <summary>Side of the square coordinate box <see cref="Mountain"/> and <see cref="Base"/> are expressed in.</summary>
    public const double Size = 22.0;

    /// <summary>The mountain-range outline, as a closed polygon.</summary>
    public static readonly (double X, double Y)[] Mountain =
    [
        (1.754, 13.406),
        (6.207,  8.555),
        (9.297, 11.645),
        (12.578, 14.922),
        (13.547, 13.953),
        (10.238, 10.641),
        (14.082,  6.520),
        (20.230, 13.406),
        (21.312, 13.406),
        (21.312, 12.551),
        (14.105,  4.481),
        (9.265,  9.668),
        (6.169,  6.570),
        (0.689, 12.535),
        (0.689, 13.406),
    ];

    /// <summary>The ground bar under the mountains, as a closed polygon.</summary>
    public static readonly (double X, double Y)[] Base =
    [
        (0.688, 16.844),
        (21.313, 16.844),
        (21.313, 18.219),
        (0.688, 18.219),
    ];
}
