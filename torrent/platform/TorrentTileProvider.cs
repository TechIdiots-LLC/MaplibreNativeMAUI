using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MapLibreNative.Maui.Torrent.PMTiles;
using MapLibreNative.Maui.Torrent.Swarm;

namespace MapLibreNative.Maui.Torrent;

/// <summary>
/// How the plugin should behave.
/// </summary>
public sealed class TorrentTileProviderOptions
{
    /// <summary>Where swarm pieces are cached. Persist it to keep what was fetched.</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>
    /// How long to wait for the swarm before falling back to HTTP.
    /// </summary>
    /// <remarks>
    /// Short on purpose. A cold piece can take half a minute, and a map that
    /// stalls that long looks broken — whereas HTTP answers immediately and the
    /// swarm will have the piece by the next pan.
    /// </remarks>
    public TimeSpan SwarmTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Seed what has been downloaded. On by default.</summary>
    public bool Seed { get; init; } = true;

    /// <summary>Cap on peer connections per archive.</summary>
    public int MaxConnections { get; init; } = 30;

    /// <summary>
    /// What a missing tile answers with: true for 404, false for 204.
    /// </summary>
    /// <remarks>
    /// Leave null to decide by tile type — 404 for raster so a sparse dataset
    /// overzooms its parent, 204 for vector. Set it only when an archive needs
    /// the opposite of its format's default.
    /// </remarks>
    public bool? Sparse { get; init; }

    /// <summary>Called with diagnostics, if you want them.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Serves map tiles out of a BitTorrent swarm.
/// </summary>
/// <remarks>
/// <para>
/// Point this at a pmtiles-swarm TileJSON URL. If the document carries a
/// <c>torrent</c> member, the archive is joined and its tiles are answered from
/// swarm pieces; if it does not, nothing changes and the map keeps using HTTP.
/// The style needs no special syntax either way.
/// </para>
/// <para>
/// The URL may also be the combined form pmtiles-swarm publishes, with the
/// handles in its fragment:
/// <c>…/tiles.json#torrent=&lt;url&gt;&amp;magnet=&lt;magnet&gt;</c>. Those are
/// read only when the document cannot be — an unreachable server, or a source
/// that never published a block — so the richer answer always wins when it is
/// available. A fragment is never sent in a request, so the same string is an
/// ordinary TileJSON URL to everything that does not know to look. See
/// <see cref="TorrentSourceUrl"/>.
/// </para>
/// <para>
/// Only the archive's own tile URLs are claimed. Everything else the map fetches
/// — other sources, sprites, fonts, the style itself — keeps maplibre's own
/// network stack, with its retry and rate-limit handling intact. That keeps the
/// blast radius to a handful of URLs rather than every resource.
/// </para>
/// <para>
/// HTTP is never abandoned. It answers while the swarm is still connecting, and
/// remains the fallback for anything the swarm cannot produce in time.
/// </para>
/// </remarks>
public static class TorrentTileProvider
{
    // Kept alive explicitly: the native side holds these for the map's
    // lifetime, and a collected delegate is a crash rather than an exception.
    private static readonly NativeMethods.HttpProviderDelegate s_provider = OnRequest;
    private static readonly NativeMethods.HttpCancelDelegate s_cancel = OnCancel;

    private static readonly ConcurrentDictionary<string, ArchiveEntry> s_archives = new();
    private static readonly ConcurrentDictionary<ulong, CancellationTokenSource> s_inFlight = new();
    private static readonly HttpClient s_http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static TorrentTileProviderOptions? s_options;
    private static bool s_registered;

    /// <summary>
    /// Joins the swarm behind a TileJSON URL, if it advertises one.
    /// </summary>
    /// <param name="tileJsonUrl">
    /// A pmtiles-swarm <c>tiles.json</c> URL, optionally carrying
    /// <c>#torrent=…&amp;magnet=…</c> as a fallback for when it cannot be fetched.
    /// </param>
    /// <param name="options">Cache location and timeouts.</param>
    /// <param name="cancellationToken">Cancels the join.</param>
    /// <returns>
    /// The descriptor that was joined, or null when neither the document nor the
    /// fragment named an archive. Null is a normal outcome, not a failure: an
    /// ordinary tile server simply cannot be accelerated.
    /// </returns>
    /// <remarks>
    /// Call before the first map is created. Returns as soon as the archive is
    /// registered; the swarm connects in the background, and tiles are served
    /// over HTTP until it is ready.
    /// </remarks>
    public static async Task<TorrentDescriptor?> AttachAsync(
        string tileJsonUrl,
        TorrentTileProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        s_options = options;

        TorrentSourceUrl sourceUrl = TorrentSourceUrl.Parse(tileJsonUrl);
        TorrentDescriptor? resolved = await ResolveAsync(sourceUrl, options, cancellationToken)
            .ConfigureAwait(false);

        if (resolved is not { } descriptor || !descriptor.CanJoin)
        {
            options.Log?.Invoke(
                $"[torrent] {sourceUrl.TileJsonUrl} advertises no joinable archive; using HTTP");
            return null;
        }

        // Prefer the .torrent: a magnet carries only an infohash, so the client
        // must find peers and complete a metadata exchange before it knows
        // anything at all — minutes against a large archive, versus a second.
        string torrentId = descriptor.TorrentUrl ?? descriptor.Magnet!;
        if (descriptor.TorrentUrl is not null)
        {
            torrentId = await CacheTorrentFileAsync(
                descriptor, options, cancellationToken).ConfigureAwait(false);
        }

        var engine = new MonoTorrentEngine(torrentId, new MonoTorrentEngineOptions
        {
            CacheDirectory = Path.Combine(options.CacheDirectory, descriptor.InfoHash),
            MaxConnections = options.MaxConnections,
            Seed = options.Seed,
        });

        var source = new TorrentByteSource(engine);
        var entry = new ArchiveEntry(descriptor, new PMTilesArchive(source), source)
        {
            Sparse = options.Sparse,
        };
        s_archives[descriptor.InfoHash.ToLowerInvariant()] = entry;

        Register(descriptor, sourceUrl.TileJsonUrl, options);

        // Warm the swarm without blocking the caller: the map should paint over
        // HTTP rather than wait for peers.
        _ = Task.Run(async () =>
        {
            try
            {
                await entry.Archive.GetHeaderAsync().ConfigureAwait(false);
                entry.Ready = true;
                options.Log?.Invoke($"[torrent] {descriptor.InfoHash} ready");
            }
            catch (Exception error)
            {
                options.Log?.Invoke($"[torrent] join failed: {error.Message}");
            }
        }, CancellationToken.None);

        return descriptor;
    }

    /// <summary>
    /// Stops serving from the swarm and releases every archive.
    /// </summary>
    /// <returns>A task that completes once everything is released.</returns>
    public static async Task DetachAllAsync()
    {
        NativeMethods.HttpProviderClearClaims();
        foreach (ArchiveEntry entry in s_archives.Values)
        {
            await entry.Source.DisposeAsync().ConfigureAwait(false);
        }

        s_archives.Clear();
    }

    /// <summary>
    /// Works out what archive is behind a source URL, from the document if it can
    /// be read and from the URL's own fragment if it cannot.
    /// </summary>
    /// <remarks>
    /// The document is the better answer and is asked first: its <c>torrent</c>
    /// block carries the infohash, the size, the web seeds and the mutable
    /// identity, none of which fit in a URL. The fragment is what is left when
    /// the document is unreachable or carries no block at all — a server that is
    /// down, or a style whose sources point somewhere that never published one.
    /// That is the case the fragment exists for, so an unreadable document is a
    /// reason to fall back rather than to fail.
    /// </remarks>
    private static async Task<TorrentDescriptor?> ResolveAsync(
        TorrentSourceUrl sourceUrl,
        TorrentTileProviderOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await s_http
                .GetStringAsync(sourceUrl.TileJsonUrl, cancellationToken)
                .ConfigureAwait(false);

            if (TorrentTileJson.Parse(json).Torrent is { CanJoin: true } published)
            {
                return published;
            }

            options.Log?.Invoke(
                $"[torrent] {sourceUrl.TileJsonUrl} carries no torrent block");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Unreachable, not JSON, not a TileJSON — all the same answer, and all
            // of them the reason the handles are in the URL in the first place.
            options.Log?.Invoke(
                $"[torrent] {sourceUrl.TileJsonUrl} unreadable ({error.Message})");
        }

        return await FromFragmentAsync(sourceUrl, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a descriptor out of the handles in the URL's fragment.
    /// </summary>
    /// <remarks>
    /// A fragment names handles, not an identity, so the infohash has to be
    /// recovered from one of them: read out of the magnet, or read out of the
    /// metainfo the <c>.torrent</c> URL points at. Everything else a document
    /// would have supplied is genuinely unknown here and stays null rather than
    /// being guessed at.
    /// </remarks>
    private static async Task<TorrentDescriptor?> FromFragmentAsync(
        TorrentSourceUrl sourceUrl,
        TorrentTileProviderOptions options,
        CancellationToken cancellationToken)
    {
        if (!sourceUrl.HasHandles)
        {
            return null;
        }

        string? infoHash = InfoHashOfMagnet(sourceUrl.Magnet);

        // Only a .torrent to go on, so the metainfo is fetched to learn what the
        // archive even is. Written to the cache under that name straight away, so
        // the join below finds it there rather than asking for it twice.
        if (infoHash is null && sourceUrl.TorrentUrl is { } torrentUrl)
        {
            try
            {
                byte[] bytes = await s_http
                    .GetByteArrayAsync(torrentUrl, cancellationToken)
                    .ConfigureAwait(false);

                infoHash = MonoTorrent.Torrent.Load(bytes)
                    .InfoHashes.V1OrV2.ToHex().ToLowerInvariant();

                Directory.CreateDirectory(options.CacheDirectory);
                await File.WriteAllBytesAsync(
                    Path.Combine(options.CacheDirectory, $"{infoHash}.torrent"),
                    bytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                options.Log?.Invoke(
                    $"[torrent] {torrentUrl} could not be read ({error.Message})");
                return null;
            }
        }

        if (infoHash is null)
        {
            return null;
        }

        options.Log?.Invoke(
            $"[torrent] {sourceUrl.TileJsonUrl}: joining {infoHash} from the URL fragment");

        return new TorrentDescriptor(
            infoHash,
            sourceUrl.Magnet,
            sourceUrl.TorrentUrl,
            Name: null,
            Size: null,
            WebSeeds: Array.Empty<string>(),
            Mutable: null);
    }

    /// <summary>Reads the infohash a magnet names, or null if it names none.</summary>
    private static string? InfoHashOfMagnet(string? magnet)
    {
        if (magnet is null)
        {
            return null;
        }

        try
        {
            // A BEP 46 magnet names a public key and may carry no xt at all, in
            // which case there is no infohash to be had until the DHT is asked.
            return MonoTorrent.MagnetLink.Parse(magnet)
                .InfoHashes?.V1OrV2.ToHex().ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Registers the callbacks and claims this archive's tile URLs.
    /// </summary>
    private static void Register(
        TorrentDescriptor descriptor,
        string tileJsonUrl,
        TorrentTileProviderOptions options)
    {
        if (!s_registered)
        {
            NativeMethods.SetHttpProvider(s_provider, IntPtr.Zero);
            NativeMethods.SetHttpCancelProvider(s_cancel, IntPtr.Zero);
            s_registered = true;
        }

        // The tile URLs live under the same prefix as the TileJSON, so claim
        // that. Matching is a plain prefix comparison on the native side, so it
        // must be exact rather than clever.
        int marker = tileJsonUrl.LastIndexOf("/tiles.json", StringComparison.OrdinalIgnoreCase);
        string prefix = marker > 0
            ? tileJsonUrl[..(marker + 1)]
            : tileJsonUrl;

        NativeMethods.HttpProviderClaimPrefix(prefix);
        options.Log?.Invoke($"[torrent] claimed {prefix}");
    }

    /// <summary>
    /// Fetches and caches the .torrent, so a restart does not refetch it.
    /// </summary>
    private static async Task<string> CacheTorrentFileAsync(
        TorrentDescriptor descriptor,
        TorrentTileProviderOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.CacheDirectory);
        string path = Path.Combine(
            options.CacheDirectory, $"{descriptor.InfoHash}.torrent");

        if (File.Exists(path))
        {
            return path;
        }

        byte[] bytes = await s_http
            .GetByteArrayAsync(descriptor.TorrentUrl!, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Called by the native layer for every request it hands us.
    /// </summary>
    /// <remarks>
    /// Runs on a native thread, so it must return immediately. The work is
    /// handed to the thread pool and answered later through
    /// <c>mbgl_http_respond</c>.
    /// </remarks>
    private static void OnRequest(
        ulong requestId,
        IntPtr urlPtr,
        IntPtr etagPtr,
        IntPtr modifiedPtr,
        long rangeStart,
        long rangeEnd,
        IntPtr userdata)
    {
        string url = ReadUtf8(urlPtr) ?? string.Empty;
        var cancellation = new CancellationTokenSource();
        s_inFlight[requestId] = cancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                await ServeAsync(requestId, url, cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The map moved on; mbgl has already forgotten this request.
            }
            catch (Exception error)
            {
                RespondError(
                    requestId,
                    NativeMethods.MlnHttpError.Other,
                    error.Message);
            }
            finally
            {
                s_inFlight.TryRemove(requestId, out _);
                cancellation.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Answers one request, from the swarm where possible and HTTP otherwise.
    /// </summary>
    private static async Task ServeAsync(
        ulong requestId,
        string url,
        CancellationToken cancellationToken)
    {
        TorrentTileProviderOptions options = s_options
            ?? throw new InvalidOperationException("the provider is not configured");

        if (TileRequest.TryParse(url, out TileRequest tile) &&
            s_archives.TryGetValue(tile.InfoHash, out ArchiveEntry? entry) &&
            entry.Ready)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeout.CancelAfter(options.SwarmTimeout);

                ReadOnlyMemory<byte>? bytes = await entry.Archive
                    .GetTileAsync(tile.Z, tile.X, tile.Y, timeout.Token)
                    .ConfigureAwait(false);

                if (bytes is null)
                {
                    // The archive genuinely has no tile here, which is an
                    // answer rather than a reason to ask HTTP the same thing.
                    //
                    // Which status says so matters. MapLibre only overzooms a
                    // parent tile when the child 404s, so a sparse raster-dem
                    // answered with 204 renders as holes wherever the data was
                    // never built — most of a terrain set covering only land.
                    // Vector is the reverse: an empty tile means no features
                    // here, and 404 makes the map log errors past coverage.
                    RespondMissing(requestId, await entry.IsSparseAsync());
                    return;
                }

                RespondOk(requestId, bytes.Value.ToArray(), entry.Descriptor.InfoHash);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The swarm was too slow. HTTP will answer now, and the piece
                // will very likely be here by the next pan.
                options.Log?.Invoke($"[torrent] swarm timed out for {tile.Z}/{tile.X}/{tile.Y}");
            }
            catch (Exception error)
            {
                options.Log?.Invoke($"[torrent] swarm read failed: {error.Message}");
            }
        }

        await ServeOverHttpAsync(requestId, url, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The fallback, and the only path until the swarm is connected.
    /// </summary>
    /// <remarks>
    /// The plugin has to do this itself: a claimed URL never reaches maplibre's
    /// own network stack, so there is nothing else left to handle it.
    /// </remarks>
    private static async Task ServeOverHttpAsync(
        ulong requestId,
        string url,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await s_http
            .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            RespondNoContent(requestId);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            NativeMethods.MlnHttpError error = (int)response.StatusCode switch
            {
                404 => NativeMethods.MlnHttpError.NotFound,
                429 => NativeMethods.MlnHttpError.RateLimit,
                >= 500 => NativeMethods.MlnHttpError.Server,
                _ => NativeMethods.MlnHttpError.Other,
            };
            RespondError(requestId, error, $"HTTP {(int)response.StatusCode}");
            return;
        }

        byte[] body = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        RespondOk(requestId, body, response.Headers.ETag?.Tag);
    }

    /// <summary>Cancels an in-flight request the map has given up on.</summary>
    private static void OnCancel(ulong requestId, IntPtr userdata)
    {
        if (s_inFlight.TryGetValue(requestId, out CancellationTokenSource? cancellation))
        {
            cancellation.Cancel();
        }
    }

    private static void RespondOk(ulong requestId, byte[] body, string? etag)
    {
        byte[]? etagBytes = ToNullTerminatedUtf8(etag);
        // An infohash is a content hash, so a tile under one can never change.
        byte[]? cacheControl = ToNullTerminatedUtf8(
            "public, max-age=31536000, immutable");
        byte[] safeBody = body.Length > 0 ? body : new byte[1];

        unsafe
        {
            fixed (byte* bodyPtr = safeBody)
            fixed (byte* e = etagBytes)
            fixed (byte* c = cacheControl)
            {
                NativeMethods.HttpRespond(
                    requestId,
                    NativeMethods.MlnHttpError.None,
                    IntPtr.Zero,
                    200,
                    (nint)bodyPtr,
                    body.Length,
                    e is null ? IntPtr.Zero : (IntPtr)e,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    c is null ? IntPtr.Zero : (IntPtr)c,
                    0, 0, 0);
            }
        }
    }

    /// <summary>
    /// Answers a tile the archive does not hold.
    /// </summary>
    /// <param name="requestId">The request being answered.</param>
    /// <param name="sparse">
    /// True to answer 404, which lets MapLibre overzoom the parent tile; false
    /// to answer 204, which tells it the tile is empty but present.
    /// </param>
    private static void RespondMissing(ulong requestId, bool sparse)
    {
        if (sparse)
        {
            RespondError(
                requestId, NativeMethods.MlnHttpError.NotFound, "no tile here");
            return;
        }

        NativeMethods.HttpRespond(
            requestId, NativeMethods.MlnHttpError.None,
            IntPtr.Zero, 204, IntPtr.Zero, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            1, 0, 0);
    }

    private static void RespondNoContent(ulong requestId) =>
        RespondMissing(requestId, sparse: false);

    private static void RespondError(
        ulong requestId,
        NativeMethods.MlnHttpError error,
        string message)
    {
        byte[]? messageBytes = ToNullTerminatedUtf8(message);
        unsafe
        {
            fixed (byte* m = messageBytes)
            {
                NativeMethods.HttpRespond(
                    requestId, error,
                    m is null ? IntPtr.Zero : (IntPtr)m,
                    0, IntPtr.Zero, 0,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    0, 0, 0);
            }
        }
    }

    private static byte[]? ToNullTerminatedUtf8(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var buffer = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);
        return buffer;
    }

    private static string? ReadUtf8(IntPtr pointer) =>
        pointer == IntPtr.Zero
            ? null
            : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(pointer);

    /// <summary>One archive this plugin is serving.</summary>
    private sealed record ArchiveEntry(
        TorrentDescriptor Descriptor,
        PMTilesArchive Archive,
        TorrentByteSource Source)
    {
        /// <summary>Whether the swarm has produced the header yet.</summary>
        public bool Ready { get; set; }

        /// <summary>Overrides the format-based default for missing tiles.</summary>
        public bool? Sparse { get; init; }

        /// <summary>
        /// Whether a missing tile should answer 404 rather than 204.
        /// </summary>
        /// <remarks>
        /// Defaults by tile type: raster 404 so a sparse dataset overzooms,
        /// vector 204. PMTiles cannot say whether raster data is a DEM, so
        /// raster defaults to the answer a DEM needs and anything that wants
        /// otherwise sets <see cref="Sparse"/>.
        /// </remarks>
        /// <returns>True to answer 404.</returns>
        public async ValueTask<bool> IsSparseAsync()
        {
            if (Sparse is { } explicitly)
            {
                return explicitly;
            }

            PMTilesHeader header = await Archive.GetHeaderAsync().ConfigureAwait(false);
            return header.TileType is not PMTilesTileType.Mvt;
        }
    }
}
