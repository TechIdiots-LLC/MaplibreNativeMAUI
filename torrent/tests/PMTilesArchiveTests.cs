using MapLibreNative.Maui.Torrent.PMTiles;
using Xunit;

namespace MapLibreNative.Maui.Torrent.Tests;

/// <summary>
/// Reading whole archives, against fixtures produced by the reference
/// JavaScript implementation rather than by this code.
/// </summary>
public class PMTilesArchiveTests
{
    /// <summary>
    /// A byte source over an in-memory archive, counting reads so the caching
    /// behaviour is observable.
    /// </summary>
    private sealed class ArraySource(byte[] bytes) : IByteRangeSource
    {
        public int Reads { get; private set; }

        public long BytesRead { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(
            long offset, int length, CancellationToken cancellationToken = default)
        {
            Reads++;
            // A read past the end returns what exists — the reader over-reads
            // the header on purpose, and a real source behaves this way.
            int available = (int)Math.Max(0, Math.Min(length, bytes.Length - offset));
            BytesRead += available;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(
                bytes.AsMemory((int)offset, available));
        }
    }

    private static ArraySource Load(string name) =>
        new(File.ReadAllBytes(name));

    public static TheoryData<string> AllFixtures() =>
        new() { "plain.pmtiles", "gzip.pmtiles", "mixed.pmtiles" };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task ReadsTheHeader(string fixture)
    {
        var archive = new PMTilesArchive(Load(fixture));
        PMTilesHeader header = await archive.GetHeaderAsync();

        Assert.Equal(3, header.SpecVersion);
        Assert.Equal(PMTilesTileType.Mvt, header.TileType);
        Assert.Equal(0, header.MinZoom);
        Assert.Equal(2, header.MaxZoom);
        Assert.True(header.Clustered);
        Assert.Equal("application/x-protobuf", header.ContentType());
        Assert.Equal(-180, header.MinLongitude, 3);
        Assert.Equal(85, header.MaxLatitude, 3);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task ReadsEveryTileTheArchiveHolds(string fixture)
    {
        var archive = new PMTilesArchive(Load(fixture));

        Assert.Equal("ROOT", await ReadTile(archive, 0, 0, 0));
        Assert.Equal("one-zero-zero", await ReadTile(archive, 1, 0, 0));
        Assert.Equal("one-one-one", await ReadTile(archive, 1, 1, 1));
        Assert.Equal("two-three-two", await ReadTile(archive, 2, 3, 2));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task ReportsNullForATileTheArchiveDoesNotHold(string fixture)
    {
        var archive = new PMTilesArchive(Load(fixture));

        // Present zoom, absent tile.
        Assert.Null(await archive.GetTileAsync(1, 0, 1));
        // Below the archive's minimum and above its maximum.
        Assert.Null(await archive.GetTileAsync(5, 0, 0));
        // Outside the pyramid entirely, which a caller may not have checked.
        Assert.Null(await archive.GetTileAsync(1, 9, 9));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task ReadsTheMetadata(string fixture)
    {
        var archive = new PMTilesArchive(Load(fixture));
        string json = await archive.GetMetadataAsync();
        Assert.Contains("Fixture", json);
    }

    [Fact]
    public async Task GzipAndPlainArchivesYieldIdenticalTiles()
    {
        // The two differ only in compression, so any divergence is the reader
        // applying the wrong field.
        var plain = new PMTilesArchive(Load("plain.pmtiles"));
        var gzip = new PMTilesArchive(Load("gzip.pmtiles"));
        var mixed = new PMTilesArchive(Load("mixed.pmtiles"));

        foreach ((int z, int x, int y) in new[] { (0, 0, 0), (1, 0, 0), (1, 1, 1), (2, 3, 2) })
        {
            string expected = await ReadTile(plain, z, x, y);
            Assert.Equal(expected, await ReadTile(gzip, z, x, y));
            Assert.Equal(expected, await ReadTile(mixed, z, x, y));
        }
    }

    [Fact]
    public async Task ReadsTheHeaderAndRootDirectoryInOneGo()
    {
        // Over a swarm this is the difference between one piece fetch and two,
        // so it is worth asserting rather than assuming.
        var source = Load("plain.pmtiles");
        var archive = new PMTilesArchive(source);

        await archive.GetHeaderAsync();
        Assert.Equal(1, source.Reads);

        // The first tile needs only its data: the directory is already in hand.
        await archive.GetTileAsync(0, 0, 0);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task CachesTheHeaderAcrossManyTiles()
    {
        var source = Load("plain.pmtiles");
        var archive = new PMTilesArchive(source);

        for (int i = 0; i < 5; i++)
        {
            await archive.GetTileAsync(0, 0, 0);
        }

        // One read for header plus root, then one per tile. Without caching
        // this would be two per tile.
        Assert.Equal(6, source.Reads);
    }

    [Fact]
    public async Task ConcurrentReadersShareOneHeaderRead()
    {
        var source = Load("plain.pmtiles");
        var archive = new PMTilesArchive(source);

        // A map requests a screenful of tiles at once; the header must not be
        // fetched once per tile.
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
            await archive.GetHeaderAsync()));

        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task RejectsSomethingThatIsNotAnArchive()
    {
        var archive = new PMTilesArchive(new ArraySource(new byte[256]));
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await archive.GetHeaderAsync());
    }

    [Fact]
    public async Task RejectsAnArchiveFromTheFuture()
    {
        byte[] bytes = File.ReadAllBytes("plain.pmtiles");
        bytes[7] = 4;

        var archive = new PMTilesArchive(new ArraySource(bytes));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await archive.GetHeaderAsync());
        Assert.Contains("spec version 4", error.Message);
    }

    [Fact]
    public async Task ReportsAnUnsupportedCompressionClearly()
    {
        byte[] bytes = File.ReadAllBytes("plain.pmtiles");
        bytes[97] = (byte)PMTilesCompression.Zstd;

        var archive = new PMTilesArchive(new ArraySource(bytes));
        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await archive.GetHeaderAsync());
        Assert.Contains("Zstd", error.Message);
    }

    private static async Task<string> ReadTile(
        PMTilesArchive archive, int z, int x, int y)
    {
        ReadOnlyMemory<byte>? tile = await archive.GetTileAsync(z, x, y);
        Assert.NotNull(tile);
        return System.Text.Encoding.UTF8.GetString(tile!.Value.Span);
    }
}
