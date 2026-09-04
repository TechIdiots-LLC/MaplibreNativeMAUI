namespace MapLibreNative.Maui.Torrent.Swarm;

/// <summary>
/// How urgently a byte range is needed.
/// </summary>
public enum FetchPriority
{
    /// <summary>Fetch when there is nothing better to do.</summary>
    Normal,

    /// <summary>Almost certainly needed shortly — directories, metadata.</summary>
    High,

    /// <summary>A request is blocked on this right now.</summary>
    Critical,
}

/// <summary>
/// Everything the piece-mapping layer needs to know about a torrent.
/// </summary>
/// <remarks>
/// Note the two coordinate systems. Offsets passed to and from an engine are
/// relative to the archive file, while <see cref="PieceLength"/> and
/// <see cref="PieceCount"/> describe the torrent's global byte space.
/// <see cref="FileOffset"/> bridges them, so an archive packed alongside other
/// files in a multi-file torrent needs no special handling — and getting this
/// wrong reads the neighbouring file rather than failing loudly.
/// </remarks>
/// <param name="InfoHash">Hex infohash, which doubles as the archive's ETag.</param>
/// <param name="PieceLength">Piece length in bytes, from the info dictionary.</param>
/// <param name="PieceCount">Total pieces in the torrent.</param>
/// <param name="FileLength">Length of the archive itself.</param>
/// <param name="FileOffset">Where the archive starts in the torrent's byte space.</param>
/// <param name="Name">Display name, for logging.</param>
public sealed record TorrentInfo(
    string InfoHash,
    long PieceLength,
    long PieceCount,
    long FileLength,
    long FileOffset,
    string? Name = null);

/// <summary>
/// The BitTorrent client abstraction.
/// </summary>
/// <remarks>
/// Deliberately tiny: read a byte range, optionally with priority and
/// cancellation. Everything PMTiles-specific — piece alignment, caching,
/// directory prefetch — sits above this line, so swapping the client out means
/// implementing only this.
/// </remarks>
public interface ITorrentEngine : IAsyncDisposable
{
    /// <summary>
    /// A stable identifier available before <see cref="ReadyAsync"/> completes,
    /// since callers key caches on it before any metadata exists.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Completes once torrent metadata is available. Must be idempotent.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The torrent's geometry.</returns>
    ValueTask<TorrentInfo> ReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads bytes starting at <paramref name="offset"/> into the archive file.
    /// </summary>
    /// <param name="offset">File-relative byte offset.</param>
    /// <param name="length">How many bytes.</param>
    /// <param name="priority">How urgently they are needed.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Exactly <paramref name="length"/> bytes, or throws.</returns>
    ValueTask<ReadOnlyMemory<byte>> ReadRangeAsync(
        long offset,
        int length,
        FetchPriority priority = FetchPriority.Critical,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for a range to start downloading in the background. Non-blocking,
    /// and engines that cannot express priority may do nothing.
    /// </summary>
    /// <param name="offset">File-relative byte offset.</param>
    /// <param name="length">How many bytes.</param>
    /// <param name="priority">How urgently they are wanted.</param>
    void Hint(long offset, long length, FetchPriority priority);

    /// <summary>
    /// Withdraws a previous hint, so the range stops competing for bandwidth.
    /// </summary>
    /// <param name="offset">File-relative byte offset.</param>
    /// <param name="length">How many bytes.</param>
    void Unhint(long offset, long length);
}
