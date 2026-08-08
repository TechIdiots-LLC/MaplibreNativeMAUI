using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Streaming;

namespace MapLibreNative.Maui.Torrent.Swarm;

/// <summary>
/// How to join a swarm.
/// </summary>
public sealed class MonoTorrentEngineOptions
{
    /// <summary>
    /// Where downloaded pieces are kept. Persist it between runs and the client
    /// keeps what it already fetched — and keeps seeding it.
    /// </summary>
    public required string CacheDirectory { get; init; }

    /// <summary>
    /// Cap on peer connections for this torrent.
    /// </summary>
    /// <remarks>
    /// Deliberately modest. Every peer is a socket and a NAT table entry, and
    /// this runs on phones and behind consumer routers, which start dropping
    /// connections long before a server would.
    /// </remarks>
    public int MaxConnections { get; init; } = 30;

    /// <summary>How long to wait for torrent metadata. Magnets need far longer than a .torrent.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>How long to wait for one read before giving up on the swarm.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Extra tracker announce URLs, beyond any the torrent carries.</summary>
    public IReadOnlyList<string> Trackers { get; init; } = [];

    /// <summary>
    /// Whether to seed what has been downloaded. On by default: a client that
    /// reads without serving is a drain on the swarm it depends on.
    /// </summary>
    public bool Seed { get; init; } = true;
}

/// <summary>
/// An <see cref="ITorrentEngine"/> backed by MonoTorrent.
/// </summary>
/// <remarks>
/// Reads go through MonoTorrent's streaming provider, which prioritises the
/// pieces around the current position rather than waiting for the normal
/// picker. That is what makes an on-demand tile read finish in seconds instead
/// of whenever the sequential download happens to arrive.
///
/// The provider's stream is not safe for concurrent use, so reads are
/// serialised. That costs the parallelism the layer above would otherwise get
/// from fetching several pieces at once — an acceptable trade for a client,
/// where the constraint is usually one slow swarm rather than many fast pieces.
/// </remarks>
public sealed class MonoTorrentEngine : ITorrentEngine
{
    private readonly MonoTorrentEngineOptions _options;
    private readonly object _torrentId;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private ClientEngine? _engine;
    private TorrentManager? _manager;
    private Stream? _readStream;
    private TorrentInfo? _info;
    private bool _disposed;

    /// <summary>
    /// Joins a swarm from a magnet URI or a .torrent file.
    /// </summary>
    /// <param name="torrentId">A magnet URI, or a path to a .torrent.</param>
    /// <param name="options">Where to cache, and how hard to try.</param>
    public MonoTorrentEngine(string torrentId, MonoTorrentEngineOptions options)
    {
        _torrentId = torrentId;
        _options = options;
        Key = $"torrent:{torrentId}";
    }

    /// <inheritdoc />
    public string Key { get; private set; }

    /// <inheritdoc />
    public async ValueTask<TorrentInfo> ReadyAsync(
        CancellationToken cancellationToken = default)
    {
        if (_info is not null)
        {
            return _info;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_info is not null)
            {
                return _info;
            }

            Directory.CreateDirectory(_options.CacheDirectory);

            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = _options.CacheDirectory,
                MaximumConnections = _options.MaxConnections,
                // Both help a client find peers without a tracker round-trip,
                // and local discovery in particular makes two devices on the
                // same network serve each other rather than the internet.
                AllowLocalPeerDiscovery = true,
                AutoSaveLoadFastResume = true,
            }.ToSettings();

            _engine = new ClientEngine(settings);
            _manager = await AddAsync(_engine, cancellationToken).ConfigureAwait(false);

            await _manager.StartAsync().ConfigureAwait(false);

            // A magnet has to complete a metadata exchange before anything about
            // the archive is known; a .torrent already carries it.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(_options.ReadyTimeout);
            await _manager.WaitForMetadataAsync(timeout.Token).ConfigureAwait(false);

            ITorrentManagerFile file = PickArchive(_manager);

            _readStream = await _manager.StreamProvider
                .CreateStreamAsync(file, prebuffer: false, timeout.Token)
                .ConfigureAwait(false);

            _info = new TorrentInfo(
                InfoHash: _manager.InfoHashes.V1OrV2.ToHex().ToLowerInvariant(),
                PieceLength: _manager.Torrent!.PieceLength,
                PieceCount: _manager.Torrent.PieceCount,
                FileLength: file.Length,
                // The streaming provider gives a stream over the file itself,
                // so offsets are already file-relative and the layer above must
                // not shift them again.
                FileOffset: 0,
                Name: file.Path);

            Key = $"torrent:{_info.InfoHash}";
            return _info;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadRangeAsync(
        long offset,
        int length,
        FetchPriority priority = FetchPriority.Critical,
        CancellationToken cancellationToken = default)
    {
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        Stream stream = _readStream
            ?? throw new InvalidOperationException("the torrent is not ready");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.ReadTimeout);

        await _readLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[length];
            int filled = 0;
            while (filled < length)
            {
                int read = await stream
                    .ReadAsync(buffer.AsMemory(filled), timeout.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            if (filled != length)
            {
                throw new EndOfStreamException(
                    $"wanted {length} bytes at {offset}, got {filled}");
            }

            return buffer;
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>
    /// Not implemented: MonoTorrent's streaming provider owns piece priority,
    /// and setting it underneath would fight the read that is in flight.
    /// </summary>
    /// <param name="offset">Ignored.</param>
    /// <param name="length">Ignored.</param>
    /// <param name="priority">Ignored.</param>
    public void Hint(long offset, long length, FetchPriority priority)
    {
    }

    /// <inheritdoc cref="Hint" />
    /// <param name="offset">Ignored.</param>
    /// <param name="length">Ignored.</param>
    public void Unhint(long offset, long length)
    {
    }

    /// <summary>
    /// Adds the torrent, from a magnet or a file.
    /// </summary>
    private async Task<TorrentManager> AddAsync(
        ClientEngine engine,
        CancellationToken cancellationToken)
    {
        string id = (string)_torrentId;

        if (id.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var link = MagnetLink.Parse(id);
            return await engine
                .AddStreamingAsync(link, _options.CacheDirectory)
                .ConfigureAwait(false);
        }

        // Fully qualified: this project's own namespace ends in ".Torrent", so
        // an unqualified Torrent resolves to the namespace, not the type.
        MonoTorrent.Torrent torrent =
            await MonoTorrent.Torrent.LoadAsync(id).ConfigureAwait(false);
        return await engine
            .AddStreamingAsync(torrent, _options.CacheDirectory)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the archive out of the torrent.
    /// </summary>
    /// <remarks>
    /// Prefers a .pmtiles file, then falls back to the largest — a torrent
    /// carrying an archive plus a checksum and a readme should still work.
    /// </remarks>
    private static ITorrentManagerFile PickArchive(TorrentManager manager)
    {
        IList<ITorrentManagerFile> files = manager.Files;
        if (files.Count == 0)
        {
            throw new InvalidDataException("the torrent contains no files");
        }

        return files
            .Where(f => f.Path.EndsWith(".pmtiles", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault()
            ?? files.OrderByDescending(f => f.Length).First();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_readStream is not null)
        {
            await _readStream.DisposeAsync().ConfigureAwait(false);
        }

        if (_manager is not null && !_options.Seed)
        {
            await _manager.StopAsync().ConfigureAwait(false);
        }

        if (_engine is not null)
        {
            await _engine.StopAllAsync().ConfigureAwait(false);
            _engine.Dispose();
        }

        _readLock.Dispose();
        _startLock.Dispose();
    }
}
