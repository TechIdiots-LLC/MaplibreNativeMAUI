namespace MapLibreNative.Maui;

/// <summary>
/// How much work 3D terrain is allowed to do per frame while it loads.
/// </summary>
/// <remarks>
/// Enabling terrain over fresh coverage — or zooming in over it — reveals a burst of tiles
/// that all want to build drawables and re-render their drape textures in the same frame.
/// These modes cap that burst, trading initial-load sharpness for smoother interaction, so
/// the right choice depends on the GPU the app runs on rather than on the map's content.
/// The budget only applies while terrain is enabled.
/// </remarks>
public enum TerrainLoadMode
{
    /// <summary>
    /// No per-frame budget: every revealed tile and drape builds immediately. Sharpest, and
    /// the default, but a large burst can stall a frame.
    /// </summary>
    Quality = 0,

    /// <summary>
    /// Caps each frame at 32 new tile builds and 16 drape re-renders.
    /// </summary>
    Balanced = 1,

    /// <summary>
    /// Caps each frame at 8 new tile builds and 4 drape re-renders — smoothest on weak GPUs,
    /// with the most visible progressive fill-in.
    /// </summary>
    Performance = 2,
}
