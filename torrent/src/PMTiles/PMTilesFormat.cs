namespace MapLibreNative.Maui.Torrent.PMTiles;

/// <summary>
/// How tile data and directories are compressed inside an archive.
/// </summary>
public enum PMTilesCompression : byte
{
    /// <summary>Not stated by the archive.</summary>
    Unknown = 0,

    /// <summary>Stored as-is.</summary>
    None = 1,

    /// <summary>gzip.</summary>
    Gzip = 2,

    /// <summary>Brotli. Not supported by this reader.</summary>
    Brotli = 3,

    /// <summary>Zstandard. Not supported by this reader.</summary>
    Zstd = 4,
}

/// <summary>
/// What the tiles in an archive actually are.
/// </summary>
public enum PMTilesTileType : byte
{
    /// <summary>Not stated by the archive.</summary>
    Unknown = 0,

    /// <summary>Mapbox Vector Tile.</summary>
    Mvt = 1,

    /// <summary>PNG.</summary>
    Png = 2,

    /// <summary>JPEG.</summary>
    Jpeg = 3,

    /// <summary>WebP.</summary>
    Webp = 4,

    /// <summary>AVIF.</summary>
    Avif = 5,

    /// <summary>MapLibre Tile.</summary>
    Mlt = 6,
}

/// <summary>
/// The fixed 127-byte header at the start of every v3 archive.
/// </summary>
/// <remarks>
/// Every offset here is absolute within the archive, which is what lets a reader
/// jump straight to a directory or a tile without scanning.
/// </remarks>
public sealed record PMTilesHeader
{
    /// <summary>Header length in bytes. Fixed by the specification.</summary>
    public const int Size = 127;

    /// <summary>Spec version. This reader understands 3.</summary>
    public required byte SpecVersion { get; init; }

    /// <summary>Offset of the root directory.</summary>
    public required long RootDirectoryOffset { get; init; }

    /// <summary>Length of the root directory.</summary>
    public required long RootDirectoryLength { get; init; }

    /// <summary>Offset of the JSON metadata.</summary>
    public required long MetadataOffset { get; init; }

    /// <summary>Length of the JSON metadata.</summary>
    public required long MetadataLength { get; init; }

    /// <summary>Offset of the leaf directory region.</summary>
    public required long LeafDirectoryOffset { get; init; }

    /// <summary>Length of the leaf directory region.</summary>
    public required long LeafDirectoryLength { get; init; }

    /// <summary>Offset of the tile data region.</summary>
    public required long TileDataOffset { get; init; }

    /// <summary>Length of the tile data region.</summary>
    public required long TileDataLength { get; init; }

    /// <summary>Number of tiles addressable, counting run-length duplicates.</summary>
    public required long AddressedTileCount { get; init; }

    /// <summary>Number of entries across all directories.</summary>
    public required long TileEntryCount { get; init; }

    /// <summary>Number of distinct tile blobs.</summary>
    public required long TileContentCount { get; init; }

    /// <summary>Whether tiles are stored in tile-id order.</summary>
    public required bool Clustered { get; init; }

    /// <summary>Compression applied to directories and metadata.</summary>
    public required PMTilesCompression InternalCompression { get; init; }

    /// <summary>Compression applied to tile data.</summary>
    public required PMTilesCompression TileCompression { get; init; }

    /// <summary>What the tiles are.</summary>
    public required PMTilesTileType TileType { get; init; }

    /// <summary>Lowest zoom present.</summary>
    public required byte MinZoom { get; init; }

    /// <summary>Highest zoom present.</summary>
    public required byte MaxZoom { get; init; }

    /// <summary>Western edge in degrees.</summary>
    public required double MinLongitude { get; init; }

    /// <summary>Southern edge in degrees.</summary>
    public required double MinLatitude { get; init; }

    /// <summary>Eastern edge in degrees.</summary>
    public required double MaxLongitude { get; init; }

    /// <summary>Northern edge in degrees.</summary>
    public required double MaxLatitude { get; init; }

    /// <summary>Suggested initial zoom.</summary>
    public required byte CenterZoom { get; init; }

    /// <summary>Suggested initial longitude.</summary>
    public required double CenterLongitude { get; init; }

    /// <summary>Suggested initial latitude.</summary>
    public required double CenterLatitude { get; init; }

    /// <summary>
    /// Parses a header from the first bytes of an archive.
    /// </summary>
    /// <param name="bytes">At least <see cref="Size"/> bytes from offset zero.</param>
    /// <returns>The parsed header.</returns>
    /// <exception cref="InvalidDataException">Not a PMTiles archive, or too new.</exception>
    public static PMTilesHeader Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size)
        {
            throw new InvalidDataException(
                $"PMTiles header needs {Size} bytes, got {bytes.Length}");
        }

        // "PMTiles" in ASCII. Checking two bytes matches the reference readers
        // and is enough to reject a file that is not one.
        if (bytes[0] != (byte)'P' || bytes[1] != (byte)'M')
        {
            throw new InvalidDataException(
                "not a PMTiles archive: wrong magic number");
        }

        byte specVersion = bytes[7];
        if (specVersion > 3)
        {
            throw new InvalidDataException(
                $"archive is spec version {specVersion}; this reader supports up to 3");
        }

        return new PMTilesHeader
        {
            SpecVersion = specVersion,
            RootDirectoryOffset = ReadInt64(bytes, 8),
            RootDirectoryLength = ReadInt64(bytes, 16),
            MetadataOffset = ReadInt64(bytes, 24),
            MetadataLength = ReadInt64(bytes, 32),
            LeafDirectoryOffset = ReadInt64(bytes, 40),
            LeafDirectoryLength = ReadInt64(bytes, 48),
            TileDataOffset = ReadInt64(bytes, 56),
            TileDataLength = ReadInt64(bytes, 64),
            AddressedTileCount = ReadInt64(bytes, 72),
            TileEntryCount = ReadInt64(bytes, 80),
            TileContentCount = ReadInt64(bytes, 88),
            Clustered = bytes[96] == 1,
            InternalCompression = (PMTilesCompression)bytes[97],
            TileCompression = (PMTilesCompression)bytes[98],
            TileType = (PMTilesTileType)bytes[99],
            MinZoom = bytes[100],
            MaxZoom = bytes[101],
            MinLongitude = ReadCoordinate(bytes, 102),
            MinLatitude = ReadCoordinate(bytes, 106),
            MaxLongitude = ReadCoordinate(bytes, 110),
            MaxLatitude = ReadCoordinate(bytes, 114),
            CenterZoom = bytes[118],
            CenterLongitude = ReadCoordinate(bytes, 119),
            CenterLatitude = ReadCoordinate(bytes, 123),
        };
    }

    /// <summary>
    /// The MIME type matching <see cref="TileType"/>.
    /// </summary>
    /// <returns>A content type, or octet-stream when unknown.</returns>
    public string ContentType() => TileType switch
    {
        PMTilesTileType.Mvt => "application/x-protobuf",
        PMTilesTileType.Png => "image/png",
        PMTilesTileType.Jpeg => "image/jpeg",
        PMTilesTileType.Webp => "image/webp",
        PMTilesTileType.Avif => "image/avif",
        PMTilesTileType.Mlt => "application/vnd.maplibre-vector-tile",
        _ => "application/octet-stream",
    };

    /// <summary>Reads a little-endian 64-bit offset or length.</summary>
    private static long ReadInt64(ReadOnlySpan<byte> bytes, int at) =>
        BitConverter.ToInt64(bytes.Slice(at, 8));

    /// <summary>
    /// Reads a coordinate, stored as a signed integer of ten-millionths of a
    /// degree rather than a float.
    /// </summary>
    private static double ReadCoordinate(ReadOnlySpan<byte> bytes, int at) =>
        BitConverter.ToInt32(bytes.Slice(at, 4)) / 1e7;
}
