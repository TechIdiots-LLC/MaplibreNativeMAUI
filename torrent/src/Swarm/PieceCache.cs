namespace MapLibreNative.Maui.Torrent.Swarm;

/// <summary>
/// A byte-budgeted LRU cache of torrent pieces.
/// </summary>
/// <remarks>
/// Pieces are big — 16 MiB is common for the large archives this is aimed at —
/// so an unbounded map fills memory with data the engine's own store already
/// holds on disk. This keeps the hot pieces (directories, whatever is being
/// looked at right now) in memory and lets the rest fall back to the engine.
///
/// Counting bytes rather than entries is what makes the budget meaningful: an
/// entry count would mean something wildly different for a 64 KiB piece than
/// for a 16 MiB one.
/// </remarks>
public sealed class PieceCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, LinkedListNode<Entry>> _index = [];

    // Most recently used at the end, so eviction always takes the first node.
    private readonly LinkedList<Entry> _order = new();

    private long _maxBytes;
    private long _byteLength;

    /// <summary>
    /// Creates a cache with a byte budget.
    /// </summary>
    /// <param name="maxBytes">Budget in bytes. Zero disables caching entirely.</param>
    public PieceCache(long maxBytes)
    {
        _maxBytes = Math.Max(0, maxBytes);
    }

    /// <summary>Total bytes currently held.</summary>
    public long ByteLength
    {
        get { lock (_gate) { return _byteLength; } }
    }

    /// <summary>Current byte budget.</summary>
    public long MaxBytes
    {
        get { lock (_gate) { return _maxBytes; } }
    }

    /// <summary>Number of pieces currently held.</summary>
    public int Count
    {
        get { lock (_gate) { return _index.Count; } }
    }

    /// <summary>
    /// Changes the budget, evicting as needed.
    /// </summary>
    /// <remarks>
    /// Used once torrent metadata arrives and the real piece length is known. A
    /// budget chosen before that is a guess: a fixed 64 MiB holds four 16 MiB
    /// pieces, which is not enough to be useful.
    /// </remarks>
    /// <param name="maxBytes">The new budget.</param>
    public void Resize(long maxBytes)
    {
        lock (_gate)
        {
            _maxBytes = Math.Max(0, maxBytes);
            Evict();
        }
    }

    /// <summary>
    /// Looks up a piece, marking it most recently used.
    /// </summary>
    /// <param name="index">Piece index.</param>
    /// <param name="piece">The piece, when held.</param>
    /// <returns>Whether it was held.</returns>
    public bool TryGet(long index, out ReadOnlyMemory<byte> piece)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(index, out LinkedListNode<Entry>? node))
            {
                piece = default;
                return false;
            }

            _order.Remove(node);
            _order.AddLast(node);
            piece = node.Value.Data;
            return true;
        }
    }

    /// <summary>
    /// Stores a piece, evicting the least recently used entries if over budget.
    /// </summary>
    /// <param name="index">Piece index.</param>
    /// <param name="piece">The piece.</param>
    public void Set(long index, ReadOnlyMemory<byte> piece)
    {
        lock (_gate)
        {
            if (_maxBytes == 0)
            {
                return;
            }

            if (_index.TryGetValue(index, out LinkedListNode<Entry>? existing))
            {
                _byteLength -= existing.Value.Data.Length;
                _order.Remove(existing);
                _index.Remove(index);
            }

            // A piece larger than the whole budget would evict everything and
            // then itself; keep the cache useful by declining it.
            if (piece.Length > _maxBytes)
            {
                return;
            }

            LinkedListNode<Entry> node = _order.AddLast(new Entry(index, piece));
            _index[index] = node;
            _byteLength += piece.Length;
            Evict();
        }
    }

    /// <summary>Drops every cached piece.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _order.Clear();
            _byteLength = 0;
        }
    }

    /// <summary>Removes least-recently-used entries until inside the budget.</summary>
    private void Evict()
    {
        while (_byteLength > _maxBytes && _order.First is { } oldest)
        {
            _order.RemoveFirst();
            _index.Remove(oldest.Value.Index);
            _byteLength -= oldest.Value.Data.Length;
        }
    }

    private readonly record struct Entry(long Index, ReadOnlyMemory<byte> Data);
}
