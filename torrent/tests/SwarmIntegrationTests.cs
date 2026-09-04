using System.Diagnostics;
using MapLibreNative.Maui.Torrent.PMTiles;
using MapLibreNative.Maui.Torrent.Swarm;
using Xunit;
using Xunit.Abstractions;

namespace MapLibreNative.Maui.Torrent.Tests;

/// <summary>
/// Reads a real archive out of a real swarm.
/// </summary>
/// <remarks>
/// Skipped unless <c>PMTILES_TORRENT_TEST_ID</c> names a magnet URI or a
/// .torrent path. Nothing large is committed and CI never runs it — a test that
/// depends on public peers being reachable is not something to gate a build on.
///
/// To run it:
/// <code>
/// set PMTILES_TORRENT_TEST_ID=C:\path\to\planetiler-openmaptiles-latest.pmtiles.torrent
/// set PMTILES_TORRENT_TEST_CACHE=C:\temp\swarm-cache
/// dotnet test --filter SwarmIntegrationTests
/// </code>
/// Expect the first tile to take a while: a 16 MiB piece has to arrive before
/// any of it can be read, and these torrents carry no web seeds.
/// </remarks>
public class SwarmIntegrationTests(ITestOutputHelper output)
{
    private static string? TorrentId =>
        Environment.GetEnvironmentVariable("PMTILES_TORRENT_TEST_ID");

    private static string CacheDirectory =>
        Environment.GetEnvironmentVariable("PMTILES_TORRENT_TEST_CACHE")
        ?? Path.Combine(Path.GetTempPath(), "pmtiles-torrent-test");

    [SkippableFact]
    public async Task ReadsTilesFromTheSwarm()
    {
        Skip.If(string.IsNullOrWhiteSpace(TorrentId),
            "set PMTILES_TORRENT_TEST_ID to a magnet or .torrent path");

        await using var engine = new MonoTorrentEngine(TorrentId!, new MonoTorrentEngineOptions
        {
            CacheDirectory = CacheDirectory,
            ReadyTimeout = TimeSpan.FromMinutes(5),
            ReadTimeout = TimeSpan.FromMinutes(5),
        });

        var clock = Stopwatch.StartNew();
        TorrentInfo info = await engine.ReadyAsync();
        output.WriteLine($"metadata in {clock.Elapsed.TotalSeconds:F1}s");
        output.WriteLine($"  infohash    {info.InfoHash}");
        output.WriteLine($"  name        {info.Name}");
        output.WriteLine($"  size        {info.FileLength / 1024.0 / 1024 / 1024:F2} GiB");
        output.WriteLine($"  pieceLength {info.PieceLength / 1024 / 1024} MiB");
        output.WriteLine($"  pieces      {info.PieceCount}");

        await using var source = new TorrentByteSource(engine);
        var archive = new PMTilesArchive(source);

        clock.Restart();
        PMTilesHeader header = await archive.GetHeaderAsync();
        output.WriteLine($"header in {clock.Elapsed.TotalSeconds:F1}s");
        output.WriteLine($"  type   {header.TileType}");
        output.WriteLine($"  zoom   {header.MinZoom}-{header.MaxZoom}");
        output.WriteLine($"  bounds {header.MinLongitude:F2},{header.MinLatitude:F2} " +
                         $"{header.MaxLongitude:F2},{header.MaxLatitude:F2}");

        Assert.Equal(3, header.SpecVersion);
        Assert.True(header.MaxZoom >= header.MinZoom);

        // z0 is one tile covering the world, so it exists in any archive and
        // needs no guessing about where coverage is.
        clock.Restart();
        ReadOnlyMemory<byte>? tile = await archive.GetTileAsync(0, 0, 0);
        output.WriteLine($"tile 0/0/0 in {clock.Elapsed.TotalSeconds:F1}s " +
                         $"({tile?.Length ?? 0} bytes)");
        Assert.NotNull(tile);
        Assert.NotEmpty(tile!.Value.ToArray());

        // The second tile should be far quicker: it is very likely inside a
        // piece already fetched, which is the property Hilbert ordering buys.
        clock.Restart();
        ReadOnlyMemory<byte>? neighbour = await archive.GetTileAsync(1, 0, 0);
        output.WriteLine($"tile 1/0/0 in {clock.Elapsed.TotalSeconds:F1}s " +
                         $"({neighbour?.Length ?? 0} bytes)");

        TorrentSourceStats stats = source.Stats;
        output.WriteLine(
            $"stats: hits={stats.CacheHits} misses={stats.CacheMisses} " +
            $"fetched={stats.BytesFetched / 1024 / 1024}MiB " +
            $"served={stats.BytesServed}B cancelled={stats.Cancelled}");

        // Read amplification is the point of the piece cache: far more is
        // fetched than served, and the surplus is the neighbourhood.
        Assert.True(stats.BytesFetched >= stats.BytesServed);
    }
}
