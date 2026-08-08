using System.Globalization;

namespace MapLibreNative.Maui.Torrent;

/// <summary>
/// A tile URL, decomposed.
/// </summary>
public readonly record struct TileRequest(string InfoHash, int Z, int X, int Y)
{
    /// <summary>
    /// Recognises <c>/archives/{infohash}/{z}/{x}/{y}.{ext}</c>.
    /// </summary>
    /// <param name="url">The requested URL.</param>
    /// <param name="request">The decomposed request, when it matched.</param>
    /// <returns>Whether the URL is one this plugin can answer.</returns>
    /// <remarks>
    /// Deliberately hand-parsed rather than a regular expression: this runs
    /// for every resource the map requests on Android, where the provider
    /// sits beneath OnlineFileSource and sees everything.
    /// </remarks>
    public static bool TryParse(string url, out TileRequest request)
    {
        request = default;

        int query = url.IndexOfAny(['?', '#']);
        ReadOnlySpan<char> path = query >= 0 ? url.AsSpan(0, query) : url.AsSpan();

        int dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            return false;
        }

        path = path[..dot];

        if (!TrySplitLast(ref path, out int y) ||
            !TrySplitLast(ref path, out int x) ||
            !TrySplitLast(ref path, out int z))
        {
            return false;
        }

        int slash = path.LastIndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        ReadOnlySpan<char> hash = path[(slash + 1)..];
        if (hash.Length is not (40 or 64))
        {
            return false;
        }

        foreach (char c in hash)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        request = new TileRequest(hash.ToString().ToLowerInvariant(), z, x, y);
        return true;
    }

    /// <summary>Takes the final path segment as an integer.</summary>
    private static bool TrySplitLast(ref ReadOnlySpan<char> path, out int value)
    {
        value = 0;
        int slash = path.LastIndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        if (!int.TryParse(
                path[(slash + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value))
        {
            return false;
        }

        path = path[..slash];
        return true;
    }
}
