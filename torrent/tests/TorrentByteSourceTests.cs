using MapLibreNative.Maui.Torrent.PMTiles;
using MapLibreNative.Maui.Torrent.Swarm;
using Xunit;

namespace MapLibreNative.Maui.Torrent.Tests;

/// <summary>
/// Piece mapping, sharing and cancellation.
/// </summary>
public class TorrentByteSourceTests
{
    /// <summary>
    /// An engine over an in-memory archive, with controllable timing so the
    /// concurrency behaviour is testable without a swarm.
    /// </summary>
    private sealed class FakeEngine : ITorrentEngine
    {
        private readonly byte[] _data;
        private readonly TorrentInfo _info;
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly Lock _gate = new();

        public FakeEngine(
            byte[] data,
            long pieceLength,
            long fileOffset = 0,
            long trailingBytes = 0,
            bool manual = false)
        {
            _data = data;
            Manual = manual;
            long total = fileOffset + data.Length + trailingBytes;
            _info = new TorrentInfo(
                InfoHash: new string('a', 40),
                PieceLength: pieceLength,
                PieceCount: (total + pieceLength - 1) / pieceLength,
                FileLength: data.Length,
                FileOffset: fileOffset,
                Name: "fake.pmtiles");
        }

        public bool Manual { get; }

        public List<(long Offset, int Length)> Reads { get; } = [];

        public int Cancelled { get; private set; }

        public string Key => $"torrent:{_info.InfoHash}";

        public int PendingReads
        {
            get { lock (_gate) { return _gates.Count; } }
        }

        public ValueTask<TorrentInfo> ReadyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_info);

        public async ValueTask<ReadOnlyMemory<byte>> ReadRangeAsync(
            long offset,
            int length,
            FetchPriority priority = FetchPriority.Critical,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Reads.Add((offset, length));
            }

            if (Manual)
            {
                var gate = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    _gates.Add(gate);
                }

                using (cancellationToken.Register(() =>
                {
                    lock (_gate)
                    {
                        Cancelled++;
                    }

                    gate.TrySetCanceled(cancellationToken);
                }))
                {
                    await gate.Task.ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _data.AsMemory((int)offset, length);
        }

        /// <summary>Releases every blocked read.</summary>
        public void Flush()
        {
            TaskCompletionSource[] waiting;
            lock (_gate)
            {
                waiting = [.. _gates];
                _gates.Clear();
            }

            foreach (TaskCompletionSource gate in waiting)
            {
                gate.TrySetResult();
            }
        }

        public void Hint(long offset, long length, FetchPriority priority) { }

        public void Unhint(long offset, long length) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Deterministic filler, so assembled ranges are checkable byte for byte.</summary>
    private static byte[] Ramp(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    [Fact]
    public async Task ReadsARangeInsideOnePiece()
    {
        byte[] data = Ramp(1000);
        var engine = new FakeEngine(data, pieceLength: 256);
        var source = new TorrentByteSource(engine);

        ReadOnlyMemory<byte> got = await source.ReadAsync(10, 20);

        Assert.Equal(data.AsSpan(10, 20).ToArray(), got.ToArray());
        Assert.Single(engine.Reads);
    }

    [Fact]
    public async Task AssemblesARangeSpanningSeveralPieces()
    {
        byte[] data = Ramp(1000);
        var engine = new FakeEngine(data, pieceLength: 256);
        var source = new TorrentByteSource(engine);

        // Bytes 200..799 at 256 per piece land in pieces 0, 1, 2 and 3.
        ReadOnlyMemory<byte> got = await source.ReadAsync(200, 600);

        Assert.Equal(data.AsSpan(200, 600).ToArray(), got.ToArray());
        Assert.Equal(4, engine.Reads.Count);
    }

    [Fact]
    public async Task ReadsCorrectlyWhenTheArchiveIsNotAtTheStartOfTheTorrent()
    {
        // A multi-file torrent. Dropping the file offset here reads the
        // neighbouring file and returns plausible-looking wrong bytes, which is
        // the worst kind of bug, so it gets its own test.
        byte[] data = Ramp(1000);
        var engine = new FakeEngine(
            data, pieceLength: 256, fileOffset: 100, trailingBytes: 50);
        var source = new TorrentByteSource(engine);

        Assert.Equal(data.AsSpan(0, 40).ToArray(), (await source.ReadAsync(0, 40)).ToArray());
        Assert.Equal(data.AsSpan(500, 300).ToArray(), (await source.ReadAsync(500, 300)).ToArray());
        Assert.Equal(data.AsSpan(980, 20).ToArray(), (await source.ReadAsync(980, 20)).ToArray());
    }

    [Fact]
    public async Task ClampsAReadThatRunsPastTheEnd()
    {
        // The archive reader over-reads the header on purpose.
        byte[] data = Ramp(300);
        var engine = new FakeEngine(data, pieceLength: 256);
        var source = new TorrentByteSource(engine);

        ReadOnlyMemory<byte> got = await source.ReadAsync(0, 16384);

        Assert.Equal(300, got.Length);
        Assert.Equal(data, got.ToArray());
    }

    [Fact]
    public async Task RejectsAnOffsetPastTheEnd()
    {
        var engine = new FakeEngine(Ramp(100), pieceLength: 64);
        var source = new TorrentByteSource(engine);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await source.ReadAsync(500, 10));
    }

    [Fact]
    public async Task ServesARepeatedReadFromTheCache()
    {
        var engine = new FakeEngine(Ramp(1000), pieceLength: 256);
        var source = new TorrentByteSource(engine);

        await source.ReadAsync(0, 100);
        await source.ReadAsync(20, 50);

        Assert.Single(engine.Reads);
        Assert.Equal(1, source.Stats.CacheMisses);
        Assert.Equal(1, source.Stats.CacheHits);
    }

    [Fact]
    public async Task SharesOneFetchBetweenConcurrentReadsOfTheSamePiece()
    {
        // A map asks for a screenful at once, and neighbouring tiles land in
        // the same piece. Fetching it once per tile would multiply swarm
        // traffic by the number of tiles on screen.
        var engine = new FakeEngine(Ramp(1000), pieceLength: 512, manual: true);
        var source = new TorrentByteSource(engine);

        Task<ReadOnlyMemory<byte>>[] reads =
        [
            source.ReadAsync(0, 10).AsTask(),
            source.ReadAsync(20, 10).AsTask(),
            source.ReadAsync(40, 10).AsTask(),
        ];

        await WaitForAsync(() => engine.PendingReads == 1);
        engine.Flush();
        await Task.WhenAll(reads);

        Assert.Single(engine.Reads);
    }

    [Fact]
    public async Task OneAbandonedReadDoesNotCancelAPieceAnotherIsWaitingOn()
    {
        // The behaviour the reference counting exists for. A panning map
        // abandons requests constantly; if that killed shared fetches, the
        // surviving tiles would fail for no reason.
        var engine = new FakeEngine(Ramp(1000), pieceLength: 512, manual: true);
        var source = new TorrentByteSource(engine);

        using var giveUp = new CancellationTokenSource();
        Task<ReadOnlyMemory<byte>> abandoned =
            source.ReadAsync(0, 10, giveUp.Token).AsTask();
        Task<ReadOnlyMemory<byte>> survivor = source.ReadAsync(20, 10).AsTask();

        await WaitForAsync(() => engine.PendingReads == 1);
        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);

        engine.Flush();
        ReadOnlyMemory<byte> got = await survivor;

        Assert.Equal(10, got.Length);
        Assert.Equal(0, engine.Cancelled);
        Assert.Equal(0, source.Stats.Cancelled);
    }

    [Fact]
    public async Task CancelsTheFetchOnceEveryWaiterHasGone()
    {
        var engine = new FakeEngine(Ramp(1000), pieceLength: 512, manual: true);
        var source = new TorrentByteSource(engine);

        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        Task<ReadOnlyMemory<byte>> a = source.ReadAsync(0, 10, first.Token).AsTask();
        Task<ReadOnlyMemory<byte>> b = source.ReadAsync(20, 10, second.Token).AsTask();

        await WaitForAsync(() => engine.PendingReads == 1);

        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await a);
        Assert.Equal(0, source.Stats.Cancelled);

        second.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await b);

        await WaitForAsync(() => source.Stats.Cancelled == 1);
        Assert.Equal(1, source.Stats.Cancelled);
    }

    [Fact]
    public async Task DoesNotCountASuccessfulFetchAsCancelled()
    {
        // The counter must reflect abandoned work, not every completed read —
        // an earlier version of this design got that wrong and made the stats
        // useless.
        var engine = new FakeEngine(Ramp(1000), pieceLength: 512, manual: true);
        var source = new TorrentByteSource(engine);

        Task<ReadOnlyMemory<byte>> read = source.ReadAsync(0, 10).AsTask();
        await WaitForAsync(() => engine.PendingReads == 1);
        engine.Flush();
        await read;

        Assert.Equal(0, source.Stats.Cancelled);
        Assert.Equal(0, engine.Cancelled);
    }

    [Fact]
    public async Task ReportsWhatItHasDone()
    {
        var engine = new FakeEngine(Ramp(1000), pieceLength: 256);
        var source = new TorrentByteSource(engine);

        await source.ReadAsync(0, 100);
        TorrentSourceStats stats = source.Stats;

        Assert.Equal(100, stats.BytesServed);
        Assert.Equal(256, stats.BytesFetched);
        Assert.True(source.CachedBytes > 0);
    }

    [Fact]
    public async Task SizesTheCacheFromThePieceLength()
    {
        // A fixed byte budget is a trap with large pieces: 16 MiB holds exactly
        // one 16 MiB piece, so the cache never helps.
        var engine = new FakeEngine(Ramp(1000), pieceLength: 8L * 1024 * 1024);
        var source = new TorrentByteSource(
            engine, new TorrentByteSourceOptions { CachePieces = 4 });

        await source.ReadAsync(0, 10);

        // 4 x 8 MiB beats the 16 MiB floor.
        Assert.Equal(32L * 1024 * 1024, GetCacheBudget(source));
    }

    [Fact]
    public async Task DisposingReleasesTheEngine()
    {
        var engine = new FakeEngine(Ramp(100), pieceLength: 64);
        var source = new TorrentByteSource(engine);
        await source.ReadAsync(0, 10);

        await source.DisposeAsync();
        Assert.Equal(0, source.CachedBytes);
    }

    [Fact]
    public async Task ReadsAnArchiveThroughTheSource()
    {
        // The layers together: a real archive, read a piece at a time out of a
        // simulated swarm.
        byte[] archive = File.ReadAllBytes("plain.pmtiles");
        var engine = new FakeEngine(archive, pieceLength: 64);
        var source = new TorrentByteSource(engine);
        var pmtiles = new PMTilesArchive(source);

        PMTilesHeader header = await pmtiles.GetHeaderAsync();
        Assert.Equal(PMTilesTileType.Mvt, header.TileType);

        ReadOnlyMemory<byte>? tile = await pmtiles.GetTileAsync(1, 1, 1);
        Assert.NotNull(tile);
        Assert.Equal("one-one-one",
            System.Text.Encoding.UTF8.GetString(tile!.Value.Span));
    }

    /// <summary>The cache budget, which is only observable indirectly.</summary>
    private static long GetCacheBudget(TorrentByteSource source)
    {
        object? cache = typeof(TorrentByteSource)
            .GetField("_cache",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!
            .GetValue(source);
        return ((PieceCache)cache!).MaxBytes;
    }

    /// <summary>Spins until a condition holds, or gives up.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("condition never became true");
    }
}
