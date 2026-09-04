using System.Text.Json;

namespace MapLibreNative.Maui.Torrent;

/// <summary>
/// The BEP 46 identity of an archive that gets republished.
/// </summary>
/// <param name="PublicKey">Hex public key the publisher signs with.</param>
/// <param name="Salt">Distinguishes several archives under one key.</param>
/// <param name="Sequence">Version counter; higher supersedes lower.</param>
public sealed record TorrentMutableIdentity(
    string PublicKey,
    string? Salt,
    long Sequence);

/// <summary>
/// The <c>torrent</c> member a pmtiles-swarm TileJSON carries.
/// </summary>
/// <remarks>
/// This is the whole contract between the server and a torrent-aware client.
/// TileJSON permits unknown members and MapLibre's style spec permits arbitrary
/// source properties, so a client that does not understand this ignores it and
/// fetches tiles over HTTP as usual — which is what makes one URL work for both
/// kinds of client.
///
/// Note it describes the *archive*, not tiles. There is nothing tile-specific
/// in a swarm: it holds one file, and both ends know how to read tiles out of
/// it.
/// </remarks>
/// <param name="InfoHash">Hex v1 infohash. Also the archive's content identity.</param>
/// <param name="Magnet">Magnet URI.</param>
/// <param name="TorrentUrl">Where the .torrent can be fetched.</param>
/// <param name="Name">Archive filename.</param>
/// <param name="Size">Archive size in bytes.</param>
/// <param name="WebSeeds">BEP 19 url-list entries.</param>
/// <param name="Mutable">Publisher key, when the archive gets republished.</param>
public sealed record TorrentDescriptor(
    string InfoHash,
    string? Magnet,
    string? TorrentUrl,
    string? Name,
    long? Size,
    IReadOnlyList<string> WebSeeds,
    TorrentMutableIdentity? Mutable)
{
    /// <summary>
    /// Whether there is enough here to join a swarm.
    /// </summary>
    public bool CanJoin => TorrentUrl is not null || Magnet is not null;
}

/// <summary>
/// A TileJSON document, as far as this plugin cares about it.
/// </summary>
/// <param name="Tiles">Tile URL templates.</param>
/// <param name="MinZoom">Lowest zoom.</param>
/// <param name="MaxZoom">Highest zoom.</param>
/// <param name="Torrent">The torrent block, when the server published one.</param>
public sealed record TorrentTileJson(
    IReadOnlyList<string> Tiles,
    int MinZoom,
    int MaxZoom,
    TorrentDescriptor? Torrent)
{
    /// <summary>
    /// Parses a TileJSON document.
    /// </summary>
    /// <param name="json">The document.</param>
    /// <returns>
    /// The parts this plugin uses. A document with no <c>torrent</c> member
    /// parses fine and reports null for it — an ordinary tile server is not an
    /// error, it simply cannot be accelerated.
    /// </returns>
    /// <exception cref="JsonException">The document is not valid JSON.</exception>
    public static TorrentTileJson Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        var tiles = new List<string>();
        if (root.TryGetProperty("tiles", out JsonElement tilesElement) &&
            tilesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in tilesElement.EnumerateArray())
            {
                if (entry.GetString() is { } url)
                {
                    tiles.Add(url);
                }
            }
        }

        return new TorrentTileJson(
            tiles,
            ReadInt(root, "minzoom") ?? 0,
            ReadInt(root, "maxzoom") ?? 22,
            ParseTorrent(root));
    }

    /// <summary>
    /// Reads the torrent block, if there is one worth having.
    /// </summary>
    private static TorrentDescriptor? ParseTorrent(JsonElement root)
    {
        if (!root.TryGetProperty("torrent", out JsonElement torrent) ||
            torrent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Without an infohash there is nothing to identify the archive by, so
        // treat the block as absent rather than half-usable.
        if (ReadString(torrent, "infohash") is not { } infoHash)
        {
            return null;
        }

        var webSeeds = new List<string>();
        if (torrent.TryGetProperty("webseeds", out JsonElement seeds) &&
            seeds.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement seed in seeds.EnumerateArray())
            {
                if (seed.GetString() is { } url)
                {
                    webSeeds.Add(url);
                }
            }
        }

        TorrentMutableIdentity? mutable = null;
        if (torrent.TryGetProperty("mutable", out JsonElement mutableElement) &&
            mutableElement.ValueKind == JsonValueKind.Object &&
            ReadString(mutableElement, "publicKey") is { } publicKey)
        {
            mutable = new TorrentMutableIdentity(
                publicKey,
                ReadString(mutableElement, "salt"),
                ReadInt(mutableElement, "seq") ?? 0);
        }

        return new TorrentDescriptor(
            infoHash,
            ReadString(torrent, "magnet"),
            ReadString(torrent, "torrent"),
            ReadString(torrent, "name"),
            ReadLong(torrent, "size"),
            webSeeds,
            mutable);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long parsed)
            ? parsed
            : null;
}
