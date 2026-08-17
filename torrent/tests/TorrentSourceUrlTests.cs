using Xunit;

namespace MapLibreNative.Maui.Torrent.Tests;

/// <summary>
/// The fragment convention, read the way the browser reads it.
/// </summary>
/// <remarks>
/// The cases mirror <c>archivesFromStyle</c> in pmtiles-swarm's browser module,
/// because the two ends have to agree character for character: a handle read
/// differently at one end does not fail, it quietly builds an HTTP source
/// instead, which reads correctly and looks exactly like success.
///
/// The hosts, infohash and key here are the same invented ones the TileJSON
/// fixtures use. Nothing in this file reaches the network, and no real
/// deployment is named — a test that hardcodes somebody's infrastructure both
/// ties the suite to it staying up and publishes where it is.
/// </remarks>
public class TorrentSourceUrlTests
{
    private const string Alias = "https://maps.example.org/latest/planet/tiles.json";

    private const string Magnet =
        "magnet:?xt=urn:btih:913d671f3a28c5b8d605e28cf6bf01e293d36e86" +
        "&xs=urn:btpk:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +
        "&dn=planet&s=planet" +
        "&tr=udp%3A%2F%2Ftracker.example.org%3A1337%2Fannounce";

    private const string TorrentUrl =
        "https://maps.example.org/archives/913d671f3a28c5b8d605e28cf6bf01e293d36e86/archive.torrent";

    private static string Combined() =>
        $"{Alias}#torrent={Uri.EscapeDataString(TorrentUrl)}&magnet={Uri.EscapeDataString(Magnet)}";

    [Fact]
    public void ReadsBothHandlesFromTheFragment()
    {
        TorrentSourceUrl parsed = TorrentSourceUrl.Parse(Combined());

        Assert.Equal(Alias, parsed.TileJsonUrl);
        Assert.Equal(TorrentUrl, parsed.TorrentUrl);
        Assert.Equal(Magnet, parsed.Magnet);
        Assert.True(parsed.HasHandles);
    }

    [Fact]
    public void LeavesTheMagnetsOwnEscapesAlone()
    {
        // Only the outer layer is decoded. A tracker list decoded twice stops
        // being a magnet a client can parse.
        TorrentSourceUrl parsed = TorrentSourceUrl.Parse(Combined());

        Assert.Contains("tr=udp%3A%2F%2Ftracker.example.org", parsed.Magnet);
        Assert.DoesNotContain("tr=udp://", parsed.Magnet);
    }

    [Fact]
    public void APlainUrlIsNotAnError()
    {
        TorrentSourceUrl parsed = TorrentSourceUrl.Parse(Alias);

        Assert.Equal(Alias, parsed.TileJsonUrl);
        Assert.Null(parsed.TorrentUrl);
        Assert.Null(parsed.Magnet);
        Assert.False(parsed.HasHandles);
    }

    [Fact]
    public void AFragmentAboutSomethingElseIsIgnored()
    {
        TorrentSourceUrl parsed = TorrentSourceUrl.Parse($"{Alias}#map=3/40/-100");

        Assert.Equal(Alias, parsed.TileJsonUrl);
        Assert.False(parsed.HasHandles);
    }

    [Fact]
    public void AMagnetAloneIsAUsableHandleHere()
    {
        // Where the browser requires torrent=, because WebTorrent has no DHT to
        // ask for the metadata a magnet omits. MonoTorrent has one.
        TorrentSourceUrl parsed = TorrentSourceUrl.Parse(
            $"{Alias}#magnet={Uri.EscapeDataString(Magnet)}");

        Assert.Null(parsed.TorrentUrl);
        Assert.Equal(Magnet, parsed.Magnet);
        Assert.True(parsed.HasHandles);
    }

    [Fact]
    public void SplitsOnTheFirstHashOnly()
    {
        // A second # belongs to whatever value it lands in, not to the URL.
        TorrentSourceUrl parsed = TorrentSourceUrl.Parse(
            $"{Alias}#torrent={Uri.EscapeDataString("https://maps.example.org/a%23b.torrent")}");

        Assert.Equal(Alias, parsed.TileJsonUrl);
        Assert.Equal("https://maps.example.org/a%23b.torrent", parsed.TorrentUrl);
    }

    [Fact]
    public void DecodesPlusAsSpaceTheWayUrlSearchParamsDoes()
    {
        TorrentSourceUrl parsed = TorrentSourceUrl.Parse(
            $"{Alias}#torrent=https://maps.example.org/one+two.torrent");

        Assert.Equal("https://maps.example.org/one two.torrent", parsed.TorrentUrl);
    }

    [Fact]
    public void EmptyAndMalformedFieldsAreSkipped()
    {
        TorrentSourceUrl parsed = TorrentSourceUrl.Parse(
            $"{Alias}#torrent=&magnet={Uri.EscapeDataString(Magnet)}&novalue");

        Assert.Null(parsed.TorrentUrl);
        Assert.Equal(Magnet, parsed.Magnet);
    }

    [Fact]
    public void TheClaimPrefixNeverCarriesAFragment()
    {
        // What Register() slices to claim tile URLs. A fragment left on the end
        // would produce a prefix no tile URL can ever match.
        TorrentSourceUrl parsed = TorrentSourceUrl.Parse(Combined());

        Assert.DoesNotContain("#", parsed.TileJsonUrl);
        Assert.EndsWith("/tiles.json", parsed.TileJsonUrl);
    }
}
