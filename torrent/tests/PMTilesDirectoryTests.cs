using MapLibreNative.Maui.Torrent.PMTiles;
using Xunit;

namespace MapLibreNative.Maui.Torrent.Tests;

/// <summary>
/// Tile ordering and directory parsing.
/// </summary>
public class PMTilesDirectoryTests
{
    /// <summary>
    /// Every case the reference JavaScript implementation was asked for.
    /// </summary>
    public static TheoryData<int, int, int, ulong> HilbertReference()
    {
        var data = new TheoryData<int, int, int, ulong>();
        foreach (string line in File.ReadAllLines("hilbert-reference.csv"))
        {
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(',');
            data.Add(
                int.Parse(parts[0]),
                int.Parse(parts[1]),
                int.Parse(parts[2]),
                ulong.Parse(parts[3]));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HilbertReference))]
    public void TileIdMatchesTheReferenceImplementation(
        int z, int x, int y, ulong expected)
    {
        Assert.Equal(expected, PMTilesDirectory.ZxyToTileId(z, x, y));
    }

    [Fact]
    public void TileIdsAreUniqueAndContiguousWithinAZoom()
    {
        // Zoom 4 is 256 tiles. They must occupy exactly one unbroken range,
        // because the next zoom starts where this one ends.
        var ids = new List<ulong>();
        for (int x = 0; x < 16; x++)
        {
            for (int y = 0; y < 16; y++)
            {
                ids.Add(PMTilesDirectory.ZxyToTileId(4, x, y));
            }
        }

        ids.Sort();
        Assert.Equal(256, ids.Distinct().Count());
        Assert.Equal(ids[^1] - ids[0] + 1, (ulong)ids.Count);
    }

    [Fact]
    public void NeighbouringTilesAreAdjacentInTheFileInBothDirections()
    {
        // The property the whole design leans on. Row-major ordering would put
        // horizontal neighbours one apart but vertical ones a full row apart —
        // fine for scanning, bad for a map viewport, which pans both ways.
        // Hilbert order is isotropic: the *median* gap is one tile whichever
        // way you move, so one torrent piece tends to carry a neighbourhood.
        //
        // The mean is not the measure here — it is dragged up by the handful of
        // long jumps where the curve crosses a quadrant boundary — so this
        // asserts on the median, which is what a typical pan actually meets.
        List<long> horizontal = [];
        List<long> vertical = [];

        for (int x = 0; x < 32; x++)
        {
            for (int y = 0; y < 32; y++)
            {
                ulong here = PMTilesDirectory.ZxyToTileId(5, x, y);
                if (x + 1 < 32)
                {
                    horizontal.Add(Math.Abs(
                        (long)PMTilesDirectory.ZxyToTileId(5, x + 1, y) - (long)here));
                }

                if (y + 1 < 32)
                {
                    vertical.Add(Math.Abs(
                        (long)PMTilesDirectory.ZxyToTileId(5, x, y + 1) - (long)here));
                }
            }
        }

        Assert.Equal(1, Median(horizontal));
        Assert.Equal(1, Median(vertical));

        // Row-major would score 32 here, one full row, for every vertical pair.
        Assert.True(Median(vertical) < 32);
    }

    /// <summary>The middle value, which describes a typical pan better than a mean.</summary>
    private static long Median(List<long> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(27, 0, 0)]
    public void RejectsAZoomItCannotAddress(int z, int x, int y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PMTilesDirectory.ZxyToTileId(z, x, y));
    }

    [Theory]
    [InlineData(1, 2, 0)]
    [InlineData(1, 0, 2)]
    [InlineData(0, 1, 0)]
    public void RejectsCoordinatesOutsideTheirZoom(int z, int x, int y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PMTilesDirectory.ZxyToTileId(z, x, y));
    }

    [Fact]
    public void ParsesADirectoryOfContiguousEntries()
    {
        var entries = new[]
        {
            new PMTilesEntry(0, 0, 10, 1),
            new PMTilesEntry(1, 10, 20, 1),
            new PMTilesEntry(2, 30, 5, 1),
        };

        PMTilesEntry[] parsed =
            PMTilesDirectory.Deserialize(Serialize(entries));

        Assert.Equal(entries, parsed);
    }

    [Fact]
    public void ParsesEntriesThatAreNotContiguous()
    {
        // A gap means the offset is stored literally rather than as the
        // "follows the previous one" zero.
        var entries = new[]
        {
            new PMTilesEntry(0, 0, 10, 1),
            new PMTilesEntry(5, 4096, 20, 1),
        };

        Assert.Equal(entries, PMTilesDirectory.Deserialize(Serialize(entries)));
    }

    [Fact]
    public void ParsesAnEmptyDirectory()
    {
        Assert.Empty(PMTilesDirectory.Deserialize(Serialize([])));
    }

    [Fact]
    public void FindsAnExactEntry()
    {
        var entries = new[]
        {
            new PMTilesEntry(0, 0, 10, 1),
            new PMTilesEntry(5, 10, 10, 1),
            new PMTilesEntry(9, 20, 10, 1),
        };

        Assert.Equal(entries[1], PMTilesDirectory.FindTile(entries, 5));
        Assert.Equal(entries[2], PMTilesDirectory.FindTile(entries, 9));
    }

    [Fact]
    public void FindsATileInsideARun()
    {
        // One blob shared by tiles 5 through 7 — how PMTiles stores repeated
        // tiles such as empty ocean without duplicating the bytes.
        var entries = new[]
        {
            new PMTilesEntry(0, 0, 10, 1),
            new PMTilesEntry(5, 10, 10, 3),
        };

        Assert.Equal(entries[1], PMTilesDirectory.FindTile(entries, 6));
        Assert.Equal(entries[1], PMTilesDirectory.FindTile(entries, 7));
        Assert.Null(PMTilesDirectory.FindTile(entries, 8));
    }

    [Fact]
    public void FollowsALeafPointerForAnythingInItsRange()
    {
        // Run length zero means the entry points at a leaf directory, and it
        // claims every tile id from its own up to the next entry.
        var entries = new[]
        {
            new PMTilesEntry(0, 0, 100, 0),
            new PMTilesEntry(1000, 100, 100, 0),
        };

        Assert.Equal(entries[0], PMTilesDirectory.FindTile(entries, 500));
        Assert.Equal(entries[1], PMTilesDirectory.FindTile(entries, 9999));
    }

    [Fact]
    public void ReportsNothingForATileBeforeTheFirstEntry()
    {
        var entries = new[] { new PMTilesEntry(10, 0, 10, 1) };
        Assert.Null(PMTilesDirectory.FindTile(entries, 4));
    }

    [Fact]
    public void RejectsATruncatedDirectory()
    {
        byte[] full = Serialize([new PMTilesEntry(0, 0, 10, 1)]);
        Assert.Throws<InvalidDataException>(
            () => PMTilesDirectory.Deserialize(full.AsSpan(0, full.Length - 2)));
    }

    /// <summary>
    /// Encodes entries the way an archive stores them, so the parser is tested
    /// against the real layout rather than against its own output.
    /// </summary>
    private static byte[] Serialize(PMTilesEntry[] entries)
    {
        var output = new List<byte>();
        WriteVarint(output, (ulong)entries.Length);

        ulong previous = 0;
        foreach (PMTilesEntry entry in entries)
        {
            WriteVarint(output, entry.TileId - previous);
            previous = entry.TileId;
        }

        foreach (PMTilesEntry entry in entries)
        {
            WriteVarint(output, entry.RunLength);
        }

        foreach (PMTilesEntry entry in entries)
        {
            WriteVarint(output, (ulong)entry.Length);
        }

        for (int i = 0; i < entries.Length; i++)
        {
            bool contiguous = i > 0 &&
                entries[i].Offset == entries[i - 1].Offset + entries[i - 1].Length;
            WriteVarint(output, contiguous ? 0 : (ulong)entries[i].Offset + 1);
        }

        return [.. output];
    }

    /// <summary>Appends one base-128 varint.</summary>
    private static void WriteVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }
}
