/**
 * MbglCache.cs — Default location of the persistent tile/resource cache
 * database shared by the map views and MbglOfflineManager.
 */
using System.Diagnostics;

namespace MapLibreNative.Maui;

/// <summary>
/// Provides the default path of MapLibre's cache database. The map views and
/// <see cref="MbglOfflineManager"/> both default to this path, so offline
/// regions downloaded by the manager are served to the map automatically.
/// </summary>
public static class MbglCache
{
    private static string? _defaultPath;

    /// <summary>
    /// Default cache database path:
    /// <c>{LocalApplicationData}/MapLibreNative.Maui/{processName}/cache.db</c>
    /// (per-process so unrelated apps don't contend on one SQLite file).
    /// The directory is created on first access.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            if (_defaultPath != null) return _defaultPath;
            string processName;
            try { processName = Process.GetCurrentProcess().ProcessName; }
            catch { processName = "default"; }
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MapLibreNative.Maui", processName);
            Directory.CreateDirectory(dir);
            return _defaultPath = Path.Combine(dir, "cache.db");
        }
    }
}
