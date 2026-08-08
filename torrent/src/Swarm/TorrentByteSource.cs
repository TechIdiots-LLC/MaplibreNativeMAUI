using MapLibreNative.Maui.Torrent.PMTiles;

namespace MapLibreNative.Maui.Torrent.Swarm;

/// <summary>
/// Counters describing what the source has done.
/// </summary>
/// <param name="CacheHits">Piece reads answered from memory.</param>
/// <param name="CacheMisses">Piece reads that went to the engine.</param>
/// <param name="BytesFetched">Bytes read from the engine, i.e. whole pieces.</param>
/// <param name="BytesServed">Bytes handed back to the archive reader.</param>
/// <param name="Cancelled">Piece reads dropped because every waiter went away.</param>
public readonly record struct TorrentSourceStats(
    long CacheHits,
    long CacheMisses,
    long BytesFetched,
    long BytesServed,
    long Cancelled);

/// <summary>
/// Tuning for a torrent-backed source.
/// </summary>
public sealed class TorrentByteSourceOptions
{
    /// <summary>
    /// Explicit byte budget for the piece cache. Leave null to size it from the
    /// torrent's piece length once metadata arrives.
    /// </summary>
    public long? CacheBytes { get; init; }

    /// <summary>
    /// How many pieces to hold when <see cref="CacheBytes"/> is not given.
    /// The effective budget is <c>max(16 MiB, CachePieces × pieceLength)</c>.
    /// </summary>
    public int CachePieces { get; init; } = 8;
}

/// <summary>
/// Reads an archive out of a BitTorrent swarm, a piece at a time.
/// </summary>
/// <remarks>
/// This is the layer that turns "give me bytes 4096 to 8192" into "fetch piece
/// 217", and it is where the read amplification lives: BitTorrent's unit is a
/// piece, so a four-kilobyte directory read costs a whole piece. That sounds
/// wasteful and is the opposite — PMTiles stores tiles in Hilbert order, so the
/// rest of that piece is the surrounding neighbourhood, which is very likely
/// what gets asked for next.
/// </remarks>
public sealed class TorrentByteSource : IByteRangeSource, IAsyncDisposable
{
    /// <summary>Floor for the cache, so small pieces still get a useful budget.</summary>
    private const long MinimumCacheBytes = 16L * 1024 * 1024;

    private readonly ITorrentEngine _engine;
    private readonly TorrentByteSourceOptions _options;
    private readonly PieceCache _cache;
    private readonly Lock _pendingGate = new();
    private readonly Dictionary<long, PendingPiece> _pending = [];
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private TorrentInfo? _info;
    private long _cacheHits;
    private long _cacheMisses;
    private long _bytesFetched;
    private long _bytesServed;
    private long _cancelled;
    private bool _disposed;

    /// <summary>
    /// Creates a source over an engine.
    /// </summary>
    /// <param name="engine">The swarm client.</param>
    /// <param name="options">Cache tuning.</param>
    public TorrentByteSource(
        ITorrentEngine engine,
        TorrentByteSourceOptions? options = null)
    {
        _engine = engine;
        _options = options ?? new TorrentByteSourceOptions();
        // Provisional: resized once the real piece length is known.
        _cache = new PieceCache(_options.CacheBytes ?? MinimumCacheBytes);
    }

    /// <summary>The underlying engine, for swarm introspection.</summary>
    public ITorrentEngine Engine => _engine;

    /// <summary>Counters describing cache and fetch behaviour.</summary>
    public TorrentSourceStats Stats => new(
        Interlocked.Read(ref _cacheHits),
        Interlocked.Read(ref _cacheMisses),
        Interlocked.Read(ref _bytesFetched),
        Interlocked.Read(ref _bytesServed),
        Interlocked.Read(ref _cancelled));

    /// <summary>Bytes currently held in the piece cache.</summary>
    public long CachedBytes => _cache.ByteLength;

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TorrentInfo info = await InitAsync(cancellationToken).ConfigureAwait(false);

        if (offset < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), $"invalid range: offset {offset}, length {length}");
        }

        if (offset >= info.FileLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"offset {offset} is past the end of the archive ({info.FileLength} bytes)");
        }

        // The archive reader speculatively over-reads — 16 KiB for the header,
        // for instance — which would run off the end of a small archive. An
        // HTTP source gets this clamping from the server; here it is ours.
        int wanted = (int)Math.Min(length, info.FileLength - offset);
        if (wanted == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        long firstPiece = PieceIndexOf(info, offset);
        long lastPiece = PieceIndexOf(info, offset + wanted - 1);

        // Fetch every covering piece at once. Serialising them was the single
        // biggest latency cost in the original implementation: a range spanning
        // three pieces paid three sequential swarm round-trips.
        var fetches = new List<Task<ReadOnlyMemory<byte>>>();
        for (long index = firstPiece; index <= lastPiece; index++)
        {
            fetches.Add(GetPieceAsync(index, cancellationToken));
        }

        ReadOnlyMemory<byte>[] pieces =
            await Task.WhenAll(fetches).ConfigureAwait(false);

        var output = new byte[wanted];
        int written = 0;
        for (int n = 0; n < pieces.Length; n++)
        {
            long index = firstPiece + n;
            (long pieceStart, _) = PieceFileRange(info, index);
            int from = (int)Math.Max(0, offset - pieceStart);
            int to = (int)Math.Min(
                pieces[n].Length,
                offset + wanted - pieceStart);
            if (to <= from)
            {
                continue;
            }

            pieces[n][from..to].CopyTo(output.AsMemory(written));
            written += to - from;
        }

        if (written != wanted)
        {
            throw new InvalidDataException(
                $"short read: assembled {written} of {wanted} bytes at offset {offset}");
        }

        Interlocked.Add(ref _bytesServed, written);
        return output;
    }

    /// <summary>
    /// Resolves and validates torrent metadata, once.
    /// </summary>
    private async ValueTask<TorrentInfo> InitAsync(CancellationToken cancellationToken)
    {
        if (_info is not null)
        {
            return _info;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_info is not null)
            {
                return _info;
            }

            TorrentInfo info = await _engine
                .ReadyAsync(cancellationToken)
                .ConfigureAwait(false);

            if (info.PieceLength <= 0)
            {
                throw new InvalidDataException(
                    $"torrent reports a piece length of {info.PieceLength}");
            }

            if (info.FileLength <= 0)
            {
                throw new InvalidDataException(
                    $"torrent reports a file length of {info.FileLength}");
            }

            // Size the cache in pieces now the piece length is known. Large
            // archives are routinely cut at 16 MiB per piece, where a fixed
            // byte budget holds too few pieces to be worth having.
            if (_options.CacheBytes is null)
            {
                _cache.Resize(Math.Max(
                    MinimumCacheBytes,
                    _options.CachePieces * info.PieceLength));
            }

            _info = info;
            return info;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Maps a file-relative offset to the piece containing it.</summary>
    private static long PieceIndexOf(TorrentInfo info, long fileOffset) =>
        (info.FileOffset + fileOffset) / info.PieceLength;

    /// <summary>
    /// The part of a piece that lies inside the archive, in file-relative
    /// bounds.
    /// </summary>
    /// <remarks>
    /// A piece at either end of a multi-file torrent extends past the file it
    /// belongs to: the first one begins before the archive does, the last one
    /// ends after it. Both ends are clipped here, and both the fetch and the
    /// reassembly must agree on the clipped values — using the raw start in one
    /// and the clipped start in the other shifts every byte by the file offset,
    /// which returns plausible-looking wrong data rather than failing.
    /// </remarks>
    private static (long Start, long End) PieceFileRange(TorrentInfo info, long index)
    {
        long unclamped = (index * info.PieceLength) - info.FileOffset;
        return (
            Math.Max(0, unclamped),
            Math.Min(info.FileLength, unclamped + info.PieceLength));
    }

    /// <summary>
    /// Fetches one piece, sharing in-flight work between concurrent callers.
    /// </summary>
    /// <remarks>
    /// Cancellation is reference counted. An abandoned request stops waiting
    /// immediately, but the underlying fetch is only cancelled once *every*
    /// waiter has gone. Passing a caller's token straight through would let one
    /// abandoned tile kill a piece another tile is still waiting on — and a
    /// panning map abandons requests constantly, so this is the common case
    /// rather than an edge one.
    /// </remarks>
    private Task<ReadOnlyMemory<byte>> GetPieceAsync(
        long index,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGet(index, out ReadOnlyMemory<byte> cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return Task.FromResult(cached);
        }

        Interlocked.Increment(ref _cacheMisses);

        PendingPiece pending;
        lock (_pendingGate)
        {
            if (!_pending.TryGetValue(index, out PendingPiece? existing))
            {
                existing = new PendingPiece();
                existing.Task = FetchPieceAsync(index, existing);
                _pending[index] = existing;
            }

            pending = existing;
            pending.Waiters++;
        }

        return AwaitPieceAsync(index, pending, cancellationToken);
    }

    /// <summary>
    /// Waits for a shared fetch, detaching cleanly if this caller gives up.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>> AwaitPieceAsync(
        long index,
        PendingPiece pending,
        CancellationToken cancellationToken)
    {
        try
        {
            // WaitAsync observes this caller's cancellation without disturbing
            // the shared fetch, which is the whole point of the indirection.
            return await pending.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Detach(index, pending);
        }
    }

    /// <summary>
    /// Drops one waiter, cancelling the shared fetch when the last leaves.
    /// </summary>
    private void Detach(long index, PendingPiece pending)
    {
        bool abandoned;
        lock (_pendingGate)
        {
            pending.Waiters--;
            abandoned = pending.Waiters == 0 && !pending.Task.IsCompleted;
        }

        if (abandoned)
        {
            Interlocked.Increment(ref _cancelled);
            pending.Cancellation.Cancel();
        }
    }

    /// <summary>Reads one piece from the engine and caches it.</summary>
    private async Task<ReadOnlyMemory<byte>> FetchPieceAsync(
        long index,
        PendingPiece pending)
    {
        try
        {
            TorrentInfo info = _info
                ?? throw new InvalidOperationException("metadata is not available");

            (long start, long end) = PieceFileRange(info, index);
            int length = (int)(end - start);

            ReadOnlyMemory<byte> piece = await _engine
                .ReadRangeAsync(
                    start,
                    length,
                    FetchPriority.Critical,
                    pending.Cancellation.Token)
                .ConfigureAwait(false);

            Interlocked.Add(ref _bytesFetched, piece.Length);
            _cache.Set(index, piece);
            return piece;
        }
        finally
        {
            lock (_pendingGate)
            {
                if (_pending.TryGetValue(index, out PendingPiece? current) &&
                    ReferenceEquals(current, pending))
                {
                    _pending.Remove(index);
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        PendingPiece[] outstanding;
        lock (_pendingGate)
        {
            outstanding = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (PendingPiece piece in outstanding)
        {
            piece.Cancellation.Cancel();
        }

        _cache.Clear();
        _initLock.Dispose();
        await _engine.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>One in-flight piece fetch, shared by every caller waiting on it.</summary>
    private sealed class PendingPiece
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public Task<ReadOnlyMemory<byte>> Task { get; set; } = null!;

        public int Waiters { get; set; }
    }
}
