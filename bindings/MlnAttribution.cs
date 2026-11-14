/**
 * MlnAttribution.cs — Turns the HTML attribution strings sources carry into
 * text runs and links, for the attribution controls on every platform.
 */
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MapLibreNative.Maui;

/// <summary>
/// One run of attribution text. <see cref="Link"/> is the run's http(s) target, or
/// <see langword="null"/> for plain text.
/// </summary>
public readonly record struct MlnAttributionSegment(string Text, Uri? Link);

/// <summary>
/// Parses source attribution HTML, such as
/// <c>&amp;copy; &lt;a href="https://www.openstreetmap.org/copyright"&gt;OpenStreetMap&lt;/a&gt; contributors</c>,
/// into text runs and links.
/// </summary>
public static class MlnAttribution
{
    private static readonly Regex AnchorRe = new(
        @"<a\b[^>]*?href=[""']?([^""'\s>]+)[""']?[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Splits each attribution into text runs and links, joining attributions with
    /// <paramref name="separator"/>.
    /// </summary>
    /// <remarks>
    /// Other tags are dropped, entities decoded and whitespace collapsed as HTML renders
    /// it, so the spaces around a link are kept. Only absolute http and https links become
    /// links. Anything else, such as a relative or <c>javascript:</c> href, keeps its text
    /// as plain text, since attribution comes from styles and TileJSON the app may not
    /// control.
    /// </remarks>
    public static IReadOnlyList<MlnAttributionSegment> Parse(
        IEnumerable<string> attributions, string separator = " | ")
    {
        var result = new List<MlnAttributionSegment>();
        foreach (var html in attributions)
        {
            if (string.IsNullOrWhiteSpace(html)) continue;
            var part = ParseOne(html);
            if (part.Count == 0) continue;
            if (result.Count > 0) result.Add(new MlnAttributionSegment(separator, null));
            result.AddRange(part);
        }
        return result;
    }

    /// <summary>The attributions as plain text, as <see cref="Parse"/> would show them.</summary>
    public static string ToPlainText(IEnumerable<string> attributions, string separator = " | ")
    {
        var sb = new StringBuilder();
        foreach (var s in Parse(attributions, separator)) sb.Append(s.Text);
        return sb.ToString();
    }

    private static List<MlnAttributionSegment> ParseOne(string html)
    {
        var segments = new List<MlnAttributionSegment>();
        int pos = 0;
        foreach (Match m in AnchorRe.Matches(html))
        {
            if (m.Index > pos) AddText(segments, html[pos..m.Index], null);
            AddText(segments, m.Groups[2].Value, WebLink(m.Groups[1].Value));
            pos = m.Index + m.Length;
        }
        if (pos < html.Length) AddText(segments, html[pos..], null);

        // Trim the attribution's own ends, now that the runs inside it keep their spaces.
        while (segments.Count > 0)
        {
            var first = segments[0];
            var t = first.Text.TrimStart();
            if (t.Length > 0) { segments[0] = first with { Text = t }; break; }
            segments.RemoveAt(0);
        }
        while (segments.Count > 0)
        {
            int i = segments.Count - 1;
            var t = segments[i].Text.TrimEnd();
            if (t.Length > 0) { segments[i] = segments[i] with { Text = t }; break; }
            segments.RemoveAt(i);
        }
        return segments;
    }

    private static void AddText(List<MlnAttributionSegment> segments, string html, Uri? link)
    {
        var text = WhitespaceRe.Replace(WebUtility.HtmlDecode(StripTags(html)), " ");
        if (text.Length == 0) return;
        // A space-only run next to a space would double it up.
        if (segments.Count > 0 && segments[^1].Text.EndsWith(' ') && text.StartsWith(' '))
            text = text[1..];
        if (text.Length > 0) segments.Add(new MlnAttributionSegment(text, link));
    }

    private static string StripTags(string html)
    {
        var sb = new StringBuilder(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if      (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag)   sb.Append(c);
        }
        return sb.ToString();
    }

    private static Uri? WebLink(string href)
    {
        href = WebUtility.HtmlDecode(href);
        return Uri.TryCreate(href, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri : null;
    }
}
