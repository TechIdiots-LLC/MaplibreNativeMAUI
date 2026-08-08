using System.IO.Compression;

namespace MapLibreNative.Maui.Torrent.PMTiles;

/// <summary>
/// Somewhere bytes can be read from by offset and length.
/// </summary>
/// <remarks>
/// The archive reader is written against this rather than against a swarm, so
/// the same reader works over a torrent, a local file or an HTTP range request.
/// It is also what makes the reader testable without a network.
/// </remarks>
public interface IByteRangeSource
{
    /// <summary>
    /// Reads a byte range.
    /// </summary>
    /// <param name="offset">Byte offset into the archive.</param>
    /// <param name="length">How many bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// Exactly <paramref name="length"/> bytes, or fewer only when the range
    /// runs past the end of the archive.
    /// </returns>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads tiles out of a PMTiles archive.
/// </summary>
/// <remarks>
/// Header and directories are cached once read. That is not just a speed-up
/// over a swarm: the root directory is needed for every tile, so re-reading it
/// would mean re-fetching the same piece for every tile the map asks for.
/// </remarks>
public sealed class PMTilesArchive
{
    /// <summary>
    /// How deep a leaf chain may go before the archive is treated as malformed.
    /// The specification allows nesting; three levels is far past what any real
    /// archive uses, and a bound stops a corrupt file looping forever.
    /// </summary>
    private const int MaxDirectoryDepth = 4;

    private readonly IByteRangeSource _source;
    private readonly SemaphoreSlim _headerLock = new(1, 1);
    private readonly Dictionary<(long Offset, long Length), PMTilesEntry[]> _directories = [];
    private readonly SemaphoreSlim _directoryLock = new(1, 1);

    private PMTilesHeader? _header;
    private PMTilesEntry[]? _root;

    /// <summary>
    /// Creates a reader over a byte source.
    /// </summary>
    /// <param name="source">Where the archive bytes come from.</param>
    public PMTilesArchive(IByteRangeSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Reads the header, once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The archive header.</returns>
    public async ValueTask<PMTilesHeader> GetHeaderAsync(
        CancellationToken cancellationToken = default)
    {
        if (_header is not null)
        {
            return _header;
        }

        await _headerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_header is not null)
            {
                return _header;
            }

            // The header and the root directory are almost always within the
            // first 16 KiB, so one read gets both. Over a swarm that is the
            // difference between one piece fetch and two.
            ReadOnlyMemory<byte> prefix = await _source
                .ReadAsync(0, 16384, cancellationToken)
                .ConfigureAwait(false);

            PMTilesHeader header = PMTilesHeader.Parse(prefix.Span);

            long rootEnd = header.RootDirectoryOffset + header.RootDirectoryLength;
            if (rootEnd <= prefix.Length)
            {
                ReadOnlyMemory<byte> raw = prefix.Slice(
                    (int)header.RootDirectoryOffset,
                    (int)header.RootDirectoryLength);
                _root = PMTilesDirectory.Deserialize(
                    Decompress(raw.Span, header.InternalCompression));
            }

            _header = header;
            return header;
        }
        finally
        {
            _headerLock.Release();
        }
    }

    /// <summary>
    /// Reads the archive's JSON metadata.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The raw JSON.</returns>
    public async ValueTask<string> GetMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        PMTilesHeader header = await GetHeaderAsync(cancellationToken)
            .ConfigureAwait(false);

        ReadOnlyMemory<byte> raw = await _source
            .ReadAsync(
                header.MetadataOffset,
                (int)header.MetadataLength,
                cancellationToken)
            .ConfigureAwait(false);

        byte[] json = Decompress(raw.Span, header.InternalCompression);
        return System.Text.Encoding.UTF8.GetString(json);
    }

    /// <summary>
    /// Reads one tile.
    /// </summary>
    /// <param name="z">Zoom.</param>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The tile, decompressed, or null when the archive does not hold it.
    /// A missing tile is normal in a sparse archive and is not an error.
    /// </returns>
    public async ValueTask<ReadOnlyMemory<byte>?> GetTileAsync(
        int z,
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        PMTilesHeader header = await GetHeaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (z < header.MinZoom || z > header.MaxZoom)
        {
            return null;
        }

        ulong tileId;
        try
        {
            tileId = PMTilesDirectory.ZxyToTileId(z, x, y);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Outside the tile pyramid entirely, which the caller may not have
            // checked. Not an error, just nothing there.
            return null;
        }

        long directoryOffset = header.RootDirectoryOffset;
        long directoryLength = header.RootDirectoryLength;

        for (int depth = 0; depth < MaxDirectoryDepth; depth++)
        {
            PMTilesEntry[] directory = await GetDirectoryAsync(
                header, directoryOffset, directoryLength, cancellationToken)
                .ConfigureAwait(false);

            PMTilesEntry? found = PMTilesDirectory.FindTile(directory, tileId);
            if (found is null)
            {
                return null;
            }

            PMTilesEntry entry = found.Value;
            if (entry.RunLength > 0)
            {
                ReadOnlyMemory<byte> raw = await _source
                    .ReadAsync(
                        header.TileDataOffset + entry.Offset,
                        (int)entry.Length,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Decompress(raw.Span, header.TileCompression);
            }

            // Run length zero: this entry points at a leaf directory, so
            // descend and look again.
            directoryOffset = header.LeafDirectoryOffset + entry.Offset;
            directoryLength = entry.Length;
        }

        throw new InvalidDataException(
            $"tile {z}/{x}/{y} exceeded the maximum directory depth; the archive is malformed");
    }

    /// <summary>
    /// Reads a directory, caching it against its offset and length.
    /// </summary>
    private async ValueTask<PMTilesEntry[]> GetDirectoryAsync(
        PMTilesHeader header,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        if (offset == header.RootDirectoryOffset && _root is not null)
        {
            return _root;
        }

        var key = (offset, length);
        await _directoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_directories.TryGetValue(key, out PMTilesEntry[]? cached))
            {
                return cached;
            }
        }
        finally
        {
            _directoryLock.Release();
        }

        ReadOnlyMemory<byte> raw = await _source
            .ReadAsync(offset, (int)length, cancellationToken)
            .ConfigureAwait(false);

        PMTilesEntry[] entries = PMTilesDirectory.Deserialize(
            Decompress(raw.Span, header.InternalCompression));

        await _directoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A concurrent reader may have won the race; either copy is correct,
            // so keep whichever landed first and let this one go.
            if (_directories.TryGetValue(key, out PMTilesEntry[]? raced))
            {
                return raced;
            }

            _directories[key] = entries;
            if (offset == header.RootDirectoryOffset)
            {
                _root = entries;
            }
        }
        finally
        {
            _directoryLock.Release();
        }

        return entries;
    }

    /// <summary>
    /// Undoes whatever compression the archive declares.
    /// </summary>
    /// <exception cref="NotSupportedException">Brotli or Zstd, which this reader does not do.</exception>
    private static byte[] Decompress(
        ReadOnlySpan<byte> data,
        PMTilesCompression compression)
    {
        switch (compression)
        {
            case PMTilesCompression.None:
            case PMTilesCompression.Unknown:
                return data.ToArray();

            case PMTilesCompression.Gzip:
                using (var input = new MemoryStream(data.ToArray()))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gzip.CopyTo(output);
                    return output.ToArray();
                }

            default:
                throw new NotSupportedException(
                    $"archive uses {compression} compression, which this reader does not support");
        }
    }
}
