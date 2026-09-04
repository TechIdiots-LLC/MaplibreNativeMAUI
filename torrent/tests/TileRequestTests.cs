using Xunit;

namespace MapLibreNative.Maui.Torrent.Tests;

/// <summary>
/// Recognising a tile URL.
/// </summary>
/// <remarks>
/// This runs for every resource the map requests on Android, where the provider
/// sits beneath OnlineFileSource and sees all traffic. So it has to be cheap,
/// and above all it has to say no to things that merely look similar — claiming
/// a sprite or a font would break the map in a way that looks like a network
/// fault.
/// </remarks>
public class TileRequestTests
{
    private const string Hash = "913d671f3a28c5b8d605e28cf6bf01e293d36e86";

    [Theory]
    [InlineData("https://maps.example.org/archives/{0}/5/16/11.pbf", 5, 16, 11)]
    [InlineData("https://maps.example.org/archives/{0}/0/0/0.mvt", 0, 0, 0)]
    [InlineData("http://127.0.0.1:8090/archives/{0}/14/8192/5461.png", 14, 8192, 5461)]
    [InlineData("https://x/deeply/nested/path/archives/{0}/3/4/5.webp", 3, 4, 5)]
    public void RecognisesATileUrl(string template, int z, int x, int y)
    {
        string url = string.Format(template, Hash);

        Assert.True(TileRequest.TryParse(url, out TileRequest request));
        Assert.Equal(Hash, request.InfoHash);
        Assert.Equal(z, request.Z);
        Assert.Equal(x, request.X);
        Assert.Equal(y, request.Y);
    }

    [Fact]
    public void IgnoresAQueryStringAndFragment()
    {
        Assert.True(TileRequest.TryParse(
            $"https://x/archives/{Hash}/5/16/11.pbf?access_token=abc#frag",
            out TileRequest request));
        Assert.Equal(11, request.Y);
    }

    [Fact]
    public void LowercasesTheInfoHashSoLookupsMatch()
    {
        Assert.True(TileRequest.TryParse(
            $"https://x/archives/{Hash.ToUpperInvariant()}/1/2/3.pbf",
            out TileRequest request));
        Assert.Equal(Hash, request.InfoHash);
    }

    [Fact]
    public void AcceptsAV2InfoHash()
    {
        // BitTorrent v2 hashes are 64 hex characters rather than 40.
        string v2 = new('a', 64);
        Assert.True(TileRequest.TryParse(
            $"https://x/archives/{v2}/1/2/3.pbf", out TileRequest request));
        Assert.Equal(v2, request.InfoHash);
    }

    [Theory]
    // Things the map really does fetch, which must not be claimed.
    [InlineData("https://maps.example.org/style.json")]
    [InlineData("https://maps.example.org/sprites/sprite@2x.png")]
    [InlineData("https://maps.example.org/fonts/Open%20Sans/0-255.pbf")]
    [InlineData("https://api.example.org/v4/tiles/5/16/11.pbf")]
    [InlineData("https://maps.example.org/archives/planet/5/16/11.pbf")]
    [InlineData("https://maps.example.org/archives/tiles.json")]
    // Structurally wrong in ways that could otherwise parse by accident.
    [InlineData("https://x/archives/deadbeef/5/16/11.pbf")]
    [InlineData("https://x/archives/5/16/11.pbf")]
    [InlineData("")]
    [InlineData("not a url at all")]
    public void RejectsAnythingThatIsNotATileUrl(string url)
    {
        Assert.False(TileRequest.TryParse(url, out _));
    }

    [Fact]
    public void RejectsNonNumericCoordinates()
    {
        Assert.False(TileRequest.TryParse(
            $"https://x/archives/{Hash}/z/x/y.pbf", out _));
    }

    [Fact]
    public void RejectsNegativeCoordinates()
    {
        // NumberStyles.None means a leading sign is not a number, so this is
        // rejected at parse rather than reaching the archive as a bad tile.
        Assert.False(TileRequest.TryParse(
            $"https://x/archives/{Hash}/5/-1/11.pbf", out _));
    }

    [Fact]
    public void RejectsAUrlWithNoExtension()
    {
        Assert.False(TileRequest.TryParse(
            $"https://x/archives/{Hash}/5/16/11", out _));
    }

    [Fact]
    public void RejectsAHashOfTheWrongLength()
    {
        Assert.False(TileRequest.TryParse(
            $"https://x/archives/{Hash[..39]}/5/16/11.pbf", out _));
        Assert.False(TileRequest.TryParse(
            $"https://x/archives/{Hash}aa/5/16/11.pbf", out _));
    }
}
