/**
 * MbglOfflineManager.cs — Typed wrapper around mbgl_offline_manager_t:
 * offline region downloads and ambient-cache maintenance.
 *
 * Create it with the same cachePath / assetPath / apiKey as the map so the
 * underlying cache database (DatabaseFileSource) is shared — tiles downloaded
 * into an offline region are then served to the map automatically.
 *
 * Threading: native completion callbacks arrive on MapLibre's internal
 * database thread. The Task-based methods here are safe to await from any
 * thread, but the RegionProgress / RegionError events are raised on the
 * database thread — marshal to your UI thread before touching UI.
 */
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapLibreNative.Maui;

/// <summary>An offline region stored in the cache database.</summary>
/// <param name="Id">Database region id — pass to the per-region methods.</param>
/// <param name="Type"><c>"tilepyramid"</c> or <c>"geometry"</c>.</param>
/// <param name="StyleUrl">Style downloaded for the region.</param>
/// <param name="Bounds">[latSw, lonSw, latNe, lonNe] for tile-pyramid regions, else null.</param>
/// <param name="Geometry">GeoJSON geometry for geometry regions, else null.</param>
public sealed record MbglOfflineRegion(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("styleUrl")] string StyleUrl,
    [property: JsonPropertyName("bounds")] double[]? Bounds,
    [property: JsonPropertyName("geometry")] JsonElement? Geometry,
    [property: JsonPropertyName("minZoom")] double MinZoom,
    [property: JsonPropertyName("maxZoom")] double MaxZoom,
    [property: JsonPropertyName("pixelRatio")] float PixelRatio,
    [property: JsonPropertyName("includeIdeographs")] bool IncludeIdeographs);

/// <summary>Download status of an offline region.</summary>
public sealed record MbglOfflineRegionStatus(
    [property: JsonPropertyName("downloadState")] int DownloadState,
    [property: JsonPropertyName("completedResourceCount")] ulong CompletedResourceCount,
    [property: JsonPropertyName("completedResourceSize")] ulong CompletedResourceSize,
    [property: JsonPropertyName("completedTileCount")] ulong CompletedTileCount,
    [property: JsonPropertyName("requiredTileCount")] ulong RequiredTileCount,
    [property: JsonPropertyName("completedTileSize")] ulong CompletedTileSize,
    [property: JsonPropertyName("requiredResourceCount")] ulong RequiredResourceCount,
    [property: JsonPropertyName("requiredResourceCountIsPrecise")] bool RequiredResourceCountIsPrecise,
    [property: JsonPropertyName("complete")] bool Complete);

/// <summary>Progress payload for <see cref="MbglOfflineManager.RegionProgress"/>.</summary>
public readonly record struct MbglOfflineProgress(
    long RegionId,
    bool Active,
    ulong CompletedResources,
    ulong CompletedBytes,
    ulong CompletedTiles,
    ulong RequiredResources,
    bool RequiredIsPrecise,
    bool Complete);

/// <summary>Error payload for <see cref="MbglOfflineManager.RegionError"/>.</summary>
/// <param name="Reason">mbgl Response::Error::Reason value (2=NotFound 3=Server
/// 4=Connection 5=RateLimit 6=Other), or 100 = Mapbox tile count limit exceeded.</param>
public readonly record struct MbglOfflineError(long RegionId, int Reason, string Message);

/// <summary>
/// Manages offline region downloads and the ambient cache. Wraps
/// <c>mbgl_offline_manager_t</c>. Dispose when done; in-flight operations
/// complete safely after disposal.
/// </summary>
public sealed class MbglOfflineManager : IDisposable
{
    /// <summary><see cref="MbglOfflineError.Reason"/> value meaning the Mapbox
    /// tile count limit was exceeded.</summary>
    public const int TileCountLimitReason = 100;

    private IntPtr _handle;

    // One-shot callback delegates are rooted here until they fire, keyed by an
    // object identity; recurring observers are rooted until Dispose/replace.
    private readonly HashSet<object> _pending = new();
    private readonly Dictionary<long, (NativeMethods.OfflineProgressFn Progress,
                                       NativeMethods.OfflineRegionErrorFn Error)> _observers = new();
    private readonly object _lock = new();

    /// <summary>Raised (on the database thread) when an observed region's download progresses.</summary>
    public event Action<MbglOfflineProgress>? RegionProgress;
    /// <summary>Raised (on the database thread) when an observed region hits a download error.
    /// Errors are usually recoverable — the downloader retries with backoff.</summary>
    public event Action<MbglOfflineError>? RegionError;

    /// <param name="cachePath">Path of the cache database — use the same value the map
    /// was created with. Defaults to <see cref="MbglCache.DefaultPath"/>, which the map
    /// views also use by default, so the database is shared.</param>
    /// <param name="assetPath">Asset path matching the map's, or null.</param>
    /// <param name="apiKey">API key matching the map's, or null.</param>
    /// <param name="maxCacheSizeBytes">Maximum cache database size, or 0 for the default.</param>
    public MbglOfflineManager(string? cachePath = null, string? assetPath = null,
                              string? apiKey = null, ulong maxCacheSizeBytes = 0)
    {
        _handle = NativeMethods.OfflineManagerCreate(cachePath ?? MbglCache.DefaultPath,
                                                     assetPath, apiKey, maxCacheSizeBytes);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "mbgl_offline_manager_create failed: " + NativeMethods.GetLastError());
    }

    // ── Regions ────────────────────────────────────────────────────────────────

    /// <summary>Lists all offline regions in the database.</summary>
    public Task<MbglOfflineRegion[]> ListRegionsAsync()
    {
        var tcs = new TaskCompletionSource<MbglOfflineRegion[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeMethods.OfflineRegionsFn cb = null!;
        cb = (status, error, json, _) =>
        {
            Release(cb);
            CompleteRegions(tcs, status, error, json);
        };
        Retain(cb);
        Check(NativeMethods.OfflineListRegions(_handle, cb, IntPtr.Zero), cb);
        return tcs.Task;
    }

    /// <summary>
    /// Creates a tile-pyramid offline region (style + lat/lng bounds + zoom range).
    /// The region starts inactive — call <see cref="SetDownloadState"/> to begin
    /// downloading, optionally after <see cref="ObserveRegion"/>.
    /// </summary>
    public Task<MbglOfflineRegion> CreateRegionAsync(string styleUrl,
        double latSw, double lonSw, double latNe, double lonNe,
        double minZoom, double maxZoom,
        float pixelRatio = 1f, bool includeIdeographs = true, byte[]? metadata = null)
    {
        var tcs = new TaskCompletionSource<MbglOfflineRegion>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeMethods.OfflineRegionsFn cb = null!;
        cb = (status, error, json, _) =>
        {
            Release(cb);
            CompleteSingleRegion(tcs, status, error, json);
        };
        Retain(cb);
        Check(NativeMethods.OfflineCreateRegion(_handle, styleUrl, latSw, lonSw, latNe, lonNe,
            minZoom, maxZoom, pixelRatio, includeIdeographs ? 1 : 0,
            metadata, metadata?.Length ?? 0, cb, IntPtr.Zero), cb);
        return tcs.Task;
    }

    /// <summary>Creates an offline region covering a GeoJSON geometry
    /// (a Geometry, Feature, or single-feature FeatureCollection).</summary>
    public Task<MbglOfflineRegion> CreateRegionAsync(string styleUrl, string geometryGeoJson,
        double minZoom, double maxZoom,
        float pixelRatio = 1f, bool includeIdeographs = true, byte[]? metadata = null)
    {
        var tcs = new TaskCompletionSource<MbglOfflineRegion>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeMethods.OfflineRegionsFn cb = null!;
        cb = (status, error, json, _) =>
        {
            Release(cb);
            CompleteSingleRegion(tcs, status, error, json);
        };
        Retain(cb);
        Check(NativeMethods.OfflineCreateRegionGeometry(_handle, styleUrl, geometryGeoJson,
            minZoom, maxZoom, pixelRatio, includeIdeographs ? 1 : 0,
            metadata, metadata?.Length ?? 0, cb, IntPtr.Zero), cb);
        return tcs.Task;
    }

    /// <summary>Deletes a region and evicts its resources.</summary>
    public Task DeleteRegionAsync(long regionId) =>
        DoneCall(cb => NativeMethods.OfflineDeleteRegion(_handle, regionId, cb, IntPtr.Zero));

    /// <summary>Forces revalidation of the region's tiles with the server.</summary>
    public Task InvalidateRegionAsync(long regionId) =>
        DoneCall(cb => NativeMethods.OfflineInvalidateRegion(_handle, regionId, cb, IntPtr.Zero));

    /// <summary>Starts (true) or pauses (false) downloading the region's resources.</summary>
    public void SetDownloadState(long regionId, bool active)
        => ThrowOnError(NativeMethods.OfflineSetRegionDownloadState(_handle, regionId, active ? 1 : 0));

    /// <summary>
    /// Installs a progress/error observer for the region, routed to
    /// <see cref="RegionProgress"/> and <see cref="RegionError"/> (raised on the
    /// database thread). Replaces any previous observer for the region.
    /// </summary>
    public void ObserveRegion(long regionId)
    {
        NativeMethods.OfflineProgressFn progress =
            (id, state, res, bytes, tiles, required, precise, complete, _) =>
                RegionProgress?.Invoke(new MbglOfflineProgress(
                    id, state != 0, res, bytes, tiles, required, precise != 0, complete != 0));
        NativeMethods.OfflineRegionErrorFn error =
            (id, reason, message, _) =>
                RegionError?.Invoke(new MbglOfflineError(id, reason, message ?? string.Empty));

        lock (_lock) _observers[regionId] = (progress, error);
        ThrowOnError(NativeMethods.OfflineSetRegionObserver(_handle, regionId, progress, error, IntPtr.Zero));
    }

    /// <summary>Removes the region's observer.</summary>
    public void StopObservingRegion(long regionId)
    {
        ThrowOnError(NativeMethods.OfflineSetRegionObserver(_handle, regionId, null, null, IntPtr.Zero));
        lock (_lock) _observers.Remove(regionId);
    }

    /// <summary>
    /// Queries the current download status of a region.
    /// Note: if a required resource permanently 404s (e.g. a glyph range the
    /// server doesn't have), <see cref="MbglOfflineRegionStatus.Complete"/> from
    /// this query can stay false even though the download has finished — the
    /// <see cref="RegionProgress"/> observer's <c>Complete</c> flag is the
    /// authoritative completion signal.
    /// </summary>
    public Task<MbglOfflineRegionStatus> GetRegionStatusAsync(long regionId)
    {
        var tcs = new TaskCompletionSource<MbglOfflineRegionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeMethods.OfflineStatusFn cb = null!;
        cb = (status, error, json, _) =>
        {
            Release(cb);
            if (status != MbglStatus.Ok || json is null)
            {
                tcs.TrySetException(new InvalidOperationException(error ?? "offline status query failed"));
                return;
            }
            try { tcs.TrySetResult(JsonSerializer.Deserialize<MbglOfflineRegionStatus>(json)!); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };
        Retain(cb);
        Check(NativeMethods.OfflineGetRegionStatus(_handle, regionId, cb, IntPtr.Zero), cb);
        return tcs.Task;
    }

    /// <summary>Replaces the region's opaque binary metadata.</summary>
    public Task UpdateRegionMetadataAsync(long regionId, byte[] metadata) =>
        DoneCall(cb => NativeMethods.OfflineUpdateRegionMetadata(
            _handle, regionId, metadata, metadata.Length, cb, IntPtr.Zero));

    /// <summary>
    /// Reads a region's metadata from the manager's cache — the region must have
    /// been returned by a previous list/create/merge on this manager. Returns
    /// the metadata as of that call (an <see cref="UpdateRegionMetadataAsync"/>
    /// is reflected after the next <see cref="ListRegionsAsync"/>).
    /// </summary>
    public byte[]? GetRegionMetadata(long regionId)
    {
        var ptr = NativeMethods.OfflineRegionGetMetadata(_handle, regionId, out int len);
        if (ptr == IntPtr.Zero || len <= 0) return null;
        var result = new byte[len];
        Marshal.Copy(ptr, result, 0, len);
        NativeMethods.FreeString(ptr);
        return result;
    }

    /// <summary>Merges regions from a secondary cache database file into this one
    /// (side-loading). The side database may be upgraded in place.</summary>
    public Task<MbglOfflineRegion[]> MergeDatabaseAsync(string sideDbPath)
    {
        var tcs = new TaskCompletionSource<MbglOfflineRegion[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeMethods.OfflineRegionsFn cb = null!;
        cb = (status, error, json, _) =>
        {
            Release(cb);
            CompleteRegions(tcs, status, error, json);
        };
        Retain(cb);
        Check(NativeMethods.OfflineMergeDatabase(_handle, sideDbPath, cb, IntPtr.Zero), cb);
        return tcs.Task;
    }

    /// <summary>Sets the Mapbox tile count limit (does not affect non-Mapbox sources).</summary>
    public void SetTileCountLimit(ulong limit)
        => ThrowOnError(NativeMethods.OfflineSetTileCountLimit(_handle, limit));

    // ── Ambient cache / database maintenance ──────────────────────────────────

    /// <summary>Caps the ambient (non-region) cache size in bytes.</summary>
    public Task SetMaximumAmbientCacheSizeAsync(ulong bytes) =>
        DoneCall(cb => NativeMethods.OfflineSetMaximumAmbientCacheSize(_handle, bytes, cb, IntPtr.Zero));

    /// <summary>Erases the ambient cache. Offline regions are not affected.</summary>
    public Task ClearAmbientCacheAsync() =>
        DoneCall(cb => NativeMethods.OfflineClearAmbientCache(_handle, cb, IntPtr.Zero));

    /// <summary>Forces revalidation of ambient-cache resources with the server.</summary>
    public Task InvalidateAmbientCacheAsync() =>
        DoneCall(cb => NativeMethods.OfflineInvalidateAmbientCache(_handle, cb, IntPtr.Zero));

    /// <summary>Vacuums the database file to reclaim disk space.</summary>
    public Task PackDatabaseAsync() =>
        DoneCall(cb => NativeMethods.OfflinePackDatabase(_handle, cb, IntPtr.Zero));

    /// <summary>Deletes and re-initialises the database — regions AND ambient cache.</summary>
    public Task ResetDatabaseAsync() =>
        DoneCall(cb => NativeMethods.OfflineResetDatabase(_handle, cb, IntPtr.Zero));

    /// <summary>Enables/disables automatic packing after deletions (default on).</summary>
    public void SetPackDatabaseAutomatically(bool enabled)
        => ThrowOnError(NativeMethods.OfflineSetPackDatabaseAutomatically(_handle, enabled ? 1 : 0));

    // ── Plumbing ───────────────────────────────────────────────────────────────

    private Task DoneCall(Func<NativeMethods.OfflineDoneFn, MbglStatus> invoke)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeMethods.OfflineDoneFn cb = null!;
        cb = (status, error, _) =>
        {
            Release(cb);
            if (status == MbglStatus.Ok) tcs.TrySetResult();
            else tcs.TrySetException(new InvalidOperationException(error ?? "offline operation failed"));
        };
        Retain(cb);
        Check(invoke(cb), cb);
        return tcs.Task;
    }

    private static void CompleteRegions(TaskCompletionSource<MbglOfflineRegion[]> tcs,
        MbglStatus status, string? error, string? json)
    {
        if (status != MbglStatus.Ok || json is null)
        {
            tcs.TrySetException(new InvalidOperationException(error ?? "offline region query failed"));
            return;
        }
        try { tcs.TrySetResult(JsonSerializer.Deserialize<MbglOfflineRegion[]>(json) ?? []); }
        catch (Exception ex) { tcs.TrySetException(ex); }
    }

    private static void CompleteSingleRegion(TaskCompletionSource<MbglOfflineRegion> tcs,
        MbglStatus status, string? error, string? json)
    {
        if (status != MbglStatus.Ok || json is null)
        {
            tcs.TrySetException(new InvalidOperationException(error ?? "offline region create failed"));
            return;
        }
        try
        {
            var regions = JsonSerializer.Deserialize<MbglOfflineRegion[]>(json);
            if (regions is { Length: > 0 }) tcs.TrySetResult(regions[0]);
            else tcs.TrySetException(new InvalidOperationException("offline region create returned no region"));
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
    }

    /// <summary>Roots a one-shot callback delegate until it fires.</summary>
    private void Retain(object callback) { lock (_lock) _pending.Add(callback); }
    private void Release(object callback) { lock (_lock) _pending.Remove(callback); }

    /// <summary>If the native call was rejected (callback will never fire),
    /// un-roots the callback and throws.</summary>
    private void Check(MbglStatus status, object callback)
    {
        if (status == MbglStatus.Ok) return;
        Release(callback);
        ThrowOnError(status);
    }

    private static void ThrowOnError(MbglStatus status)
    {
        if (status != MbglStatus.Ok)
            throw new InvalidOperationException(
                $"offline operation failed ({status}): {NativeMethods.GetLastError()}");
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.OfflineManagerDestroy(_handle);
        _handle = IntPtr.Zero;
        lock (_lock) _observers.Clear();
        // _pending entries are dropped when their callbacks fire; native keeps
        // the shared state alive until then.
    }
}
