using System.Text;

namespace MapLibreNative.Maui.Torrent;

/// <summary>
/// A TileJSON URL with the swarm handles a publisher put in its fragment.
/// </summary>
/// <remarks>
/// <para>
/// pmtiles-swarm publishes an archived source as one string carrying both
/// halves of the story:
/// </para>
/// <code>
/// https://host/latest/&lt;category&gt;/tiles.json#torrent=&lt;url&gt;&amp;magnet=&lt;magnet&gt;
/// </code>
/// <para>
/// A fragment is never sent in an HTTP request, so that is an ordinary TileJSON
/// URL to MapLibre and to every other consumer of the style; only something
/// that goes looking for it sees anything else. That is what makes it safe to
/// put in a style file served to everybody, and it is why one URL works for a
/// torrent-aware client and a plain one alike.
/// </para>
/// <para>
/// The handles are a fallback, not the primary route. The TileJSON document
/// itself carries a richer <c>torrent</c> block — infohash, size, web seeds,
/// the mutable identity — so that is read first and this is what remains when
/// the document cannot be had. Which is exactly the case the fragment exists
/// for: it is consulted when the thing in front of it is unreachable.
/// </para>
/// </remarks>
/// <param name="TileJsonUrl">The URL with the fragment removed.</param>
/// <param name="TorrentUrl">Where the <c>.torrent</c> can be fetched, if named.</param>
/// <param name="Magnet">Magnet URI, if named.</param>
public sealed record TorrentSourceUrl(
    string TileJsonUrl,
    string? TorrentUrl,
    string? Magnet)
{
    /// <summary>Whether the fragment named anything that could join a swarm.</summary>
    public bool HasHandles => TorrentUrl is not null || Magnet is not null;

    /// <summary>
    /// Splits a source URL into the document to fetch and the handles behind it.
    /// </summary>
    /// <param name="url">A source URL, with or without a fragment.</param>
    /// <returns>
    /// The parts. A URL with no fragment, or one whose fragment names neither
    /// handle, parses fine and reports null for both — an ordinary TileJSON URL
    /// is not an error.
    /// </returns>
    /// <remarks>
    /// Unlike the browser, this does not require <c>torrent=</c>. A browser
    /// cannot act on a magnet alone: WebTorrent has no DHT, so there is nothing
    /// to ask for the metadata a magnet omits, and pmtiles-swarm's magnets are
    /// BEP 46 mutable ones besides. MonoTorrent has both a DHT and BEP 9
    /// metadata exchange, so a magnet on its own is a usable handle here and
    /// refusing it would discard a route this client can actually take.
    /// </remarks>
    public static TorrentSourceUrl Parse(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        int hash = url.IndexOf('#');
        if (hash < 0)
        {
            return new TorrentSourceUrl(url, null, null);
        }

        string tileJsonUrl = url[..hash];
        string? torrent = null;
        string? magnet = null;

        foreach (string pair in url[(hash + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string name = Decode(pair[..equals]);
            string value = Decode(pair[(equals + 1)..]);
            if (value.Length == 0)
            {
                continue;
            }

            if (name == "torrent")
            {
                torrent ??= value;
            }
            else if (name == "magnet")
            {
                magnet ??= value;
            }
        }

        return new TorrentSourceUrl(tileJsonUrl, torrent, magnet);
    }

    /// <summary>
    /// Percent-decodes one fragment field the way the browser reads it.
    /// </summary>
    /// <remarks>
    /// The browser side parses this with <c>URLSearchParams</c>, which decodes
    /// <c>+</c> as a space as well as the percent escapes. pmtiles-swarm writes
    /// these with PHP's <c>rawurlencode</c>, which spells a space <c>%20</c> and
    /// a plus <c>%2B</c>, so the two readings agree on everything it emits — but
    /// they are made to agree here rather than left to coincide, since a handle
    /// read differently at the two ends is the failure that looks like success.
    ///
    /// Only the outer layer is decoded. A magnet's own parameters stay escaped,
    /// which is what a torrent client expects to be handed.
    /// </remarks>
    private static string Decode(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));
}
