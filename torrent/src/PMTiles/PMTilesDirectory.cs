namespace MapLibreNative.Maui.Torrent.PMTiles;

/// <summary>
/// One entry in a PMTiles directory.
/// </summary>
/// <param name="TileId">First tile id this entry covers.</param>
/// <param name="Offset">Byte offset, relative to the tile data or leaf region.</param>
/// <param name="Length">Byte length.</param>
/// <param name="RunLength">
/// How many consecutive tile ids share this blob. Zero means the entry points at
/// a leaf directory rather than at tile data — that distinction is the whole
/// mechanism by which an archive stays navigable without loading every index.
/// </param>
public readonly record struct PMTilesEntry(
    ulong TileId,
    long Offset,
    long Length,
    uint RunLength);

/// <summary>
/// Reading PMTiles directories, and the tile ordering they are sorted by.
/// </summary>
/// <remarks>
/// Directories are stored column-major: all the tile ids, then all the run
/// lengths, then all the lengths, then all the offsets. Grouping like with like
/// is what makes them compress well, and it means a reader cannot stop early —
/// the whole directory is parsed or none of it.
/// </remarks>
public static class PMTilesDirectory
{
    /// <summary>
    /// Converts tile coordinates to the Hilbert-curve id an archive sorts by.
    /// </summary>
    /// <param name="z">Zoom level, 0 to 26.</param>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>The tile id.</returns>
    /// <remarks>
    /// Hilbert order is why PMTiles works well over a network: tiles that are
    /// near each other on the map are near each other in the file, so one range
    /// read — or one torrent piece — tends to carry a whole neighbourhood.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Zoom too deep, or coordinates outside it.</exception>
    public static ulong ZxyToTileId(int z, int x, int y)
    {
        if (z is < 0 or > 26)
        {
            throw new ArgumentOutOfRangeException(
                nameof(z), z, "zoom must be between 0 and 26");
        }

        long limit = 1L << z;
        if (x < 0 || y < 0 || x >= limit || y >= limit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), $"tile {z}/{x}/{y} is outside the bounds of zoom {z}");
        }

        // Every zoom below this one, laid out before it: (4^z - 1) / 3.
        ulong accumulator = ((1UL << (z * 2)) - 1) / 3;

        long rx, ry;
        long tx = x;
        long ty = y;
        for (long s = limit / 2; s > 0; s /= 2)
        {
            rx = (tx & s) > 0 ? 1 : 0;
            ry = (ty & s) > 0 ? 1 : 0;
            accumulator += (ulong)((3 * rx) ^ ry) * (ulong)(s * s);
            Rotate(s, ref tx, ref ty, rx, ry);
        }

        return accumulator;
    }

    /// <summary>
    /// Rotates a quadrant so the curve stays continuous across its corners.
    /// </summary>
    private static void Rotate(long n, ref long x, ref long y, long rx, long ry)
    {
        if (ry != 0)
        {
            return;
        }

        if (rx == 1)
        {
            x = n - 1 - x;
            y = n - 1 - y;
        }

        (x, y) = (y, x);
    }

    /// <summary>
    /// Parses a directory from its decompressed bytes.
    /// </summary>
    /// <param name="bytes">The decompressed directory.</param>
    /// <returns>Entries, in tile-id order.</returns>
    /// <exception cref="InvalidDataException">The directory is malformed.</exception>
    public static PMTilesEntry[] Deserialize(ReadOnlySpan<byte> bytes)
    {
        int position = 0;
        ulong count = ReadVarint(bytes, ref position);
        if (count > int.MaxValue)
        {
            throw new InvalidDataException($"directory claims {count} entries");
        }

        var entries = new PMTilesEntry[count];

        // Tile ids are stored as deltas from the previous entry, which keeps
        // them small even at the end of a planet-scale archive.
        ulong tileId = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            tileId += ReadVarint(bytes, ref position);
            entries[i] = entries[i] with { TileId = tileId };
        }

        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = entries[i] with
            {
                RunLength = (uint)ReadVarint(bytes, ref position),
            };
        }

        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = entries[i] with
            {
                Length = (long)ReadVarint(bytes, ref position),
            };
        }

        for (int i = 0; i < entries.Length; i++)
        {
            ulong raw = ReadVarint(bytes, ref position);
            long offset;
            if (raw == 0 && i > 0)
            {
                // Zero means "immediately after the previous entry", the common
                // case in a clustered archive and the reason offsets compress.
                offset = entries[i - 1].Offset + entries[i - 1].Length;
            }
            else
            {
                offset = (long)(raw - 1);
            }

            entries[i] = entries[i] with { Offset = offset };
        }

        return entries;
    }

    /// <summary>
    /// Finds the entry covering a tile id.
    /// </summary>
    /// <param name="entries">A directory, in tile-id order.</param>
    /// <param name="tileId">The tile being looked for.</param>
    /// <returns>The covering entry, or null when the archive has no such tile.</returns>
    public static PMTilesEntry? FindTile(PMTilesEntry[] entries, ulong tileId)
    {
        int low = 0;
        int high = entries.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            ulong candidate = entries[middle].TileId;

            if (tileId > candidate)
            {
                low = middle + 1;
            }
            else if (tileId < candidate)
            {
                high = middle - 1;
            }
            else
            {
                return entries[middle];
            }
        }

        // No exact hit. The entry before the insertion point still covers this
        // tile if it is a leaf pointer (run length zero) or if the tile falls
        // inside its run.
        if (high >= 0)
        {
            PMTilesEntry previous = entries[high];
            if (previous.RunLength == 0 ||
                tileId - previous.TileId < previous.RunLength)
            {
                return previous;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads one base-128 varint.
    /// </summary>
    /// <exception cref="InvalidDataException">Truncated or over-long.</exception>
    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int position)
    {
        ulong value = 0;
        int shift = 0;

        while (true)
        {
            if (position >= bytes.Length)
            {
                throw new InvalidDataException(
                    "directory ended in the middle of a varint");
            }

            byte b = bytes[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException("varint longer than 10 bytes");
            }
        }
    }
}
