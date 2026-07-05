/**
 * MbglNetwork.cs — Process-global network state for MapLibre resource loading.
 */
namespace MapLibreNative.Maui;

/// <summary>
/// Controls MapLibre's process-global network state. When offline, all network
/// requests are suspended and only cached / offline resources are served;
/// switching back online resumes queued requests.
/// </summary>
public static class MbglNetwork
{
    /// <summary>Gets or sets whether MapLibre may access the network.</summary>
    public static bool Online
    {
        get => NativeMethods.NetworkStatusGet() != 0;
        set => NativeMethods.NetworkStatusSet(value ? 1 : 0);
    }
}
