using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.World;

public sealed class RangePresetsTests
{
    static int Distance(BlockId a, BlockId b) => Math.Max(Math.Abs(a.Bx - b.Bx), Math.Abs(a.By - b.By));

    /// <summary>The blocks within two blocks (diagonals too) of any of the land blocks that are not land, inside a frame.</summary>
    static HashSet<BlockId> Ring(IReadOnlyCollection<BlockId> land, IReadOnlySet<BlockId> otherLand, MapFrame frame)
    {
        var ring = new HashSet<BlockId>();
        foreach (var l in land)
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    var b = new BlockId(l.Bx + dx, l.By + dy);
                    if (frame.Contains(b) && !land.Contains(b) && !otherLand.Contains(b)) ring.Add(b);
                }
        return ring;
    }

    [Fact]
    public void TheDefaultRangeIsItsLandTheWaterWithinTwoBlocksAndTheShallowSeaBeyond()
    {
        var land = DefaultRange.Blocks.Where(kv => kv.Value == BlockClass.Land).Select(kv => kv.Key).ToHashSet();
        var water = DefaultRange.Blocks.Where(kv => kv.Value == BlockClass.Water).Select(kv => kv.Key).ToHashSet();
        var ring = Ring(land, new HashSet<BlockId>(), MapFrame.Standard);
        Assert.Equal(312, ring.Count);
        Assert.Subset(water, ring);
        // beyond the ring: the blocks whose sea is shallower than 200 m, where the bundled styles' sea bands end (56,
        // counted from a capture of the whole coast), and the aircraft carrier's three south-east of Los Santos
        var beyond = water.Except(ring).ToHashSet();
        Assert.Equal(59, beyond.Count);
        Assert.All(beyond, b => Assert.True(MapFrame.Standard.Contains(b)));
        var carrier = new[] { "z8_100_180", "z8_100_184", "z8_100_188" }.Select(BlockId.Parse).ToHashSet();
        Assert.Subset(beyond, carrier);
        // the shallow sea continues the ring: each of its blocks touches the ring or another block beyond it
        Assert.All(beyond.Except(carrier), b => Assert.Contains(water, w => w != b && Distance(w, b) == 1));
    }

    [Theory]
    [InlineData("cayoPerico", 31, 87, 0, 1, 0, 1, 118, 14)]
    [InlineData("roxwood", 138, 103, 1, 0, 0, 0, 175, 0)]
    public void PresetsAreLandAndTheWaterWithinTwoBlocksInsideTheFrameTheLandNeeds(string id, int land, int water, int top, int bottom, int left, int right, int own, int beyond)
    {
        var p = RangePresets.Find(id)!;
        var landBlocks = p.Blocks.Where(kv => kv.Value == BlockClass.Land).Select(kv => kv.Key).ToHashSet();
        var waterBlocks = p.Blocks.Where(kv => kv.Value == BlockClass.Water).Select(kv => kv.Key).ToHashSet();
        Assert.Equal((land, water), (landBlocks.Count, waterBlocks.Count));
        Assert.Equal(new MapFrame(top, bottom, left, right), p.Frame);
        Assert.Equal(p.Frame, MapFrame.Holding(landBlocks));           // the water never asks for more cells than the land
        Assert.Equal(own, p.Own.Count);
        Assert.All(p.Own, b => Assert.False(DefaultRange.Blocks.ContainsKey(b)));
        // the default range's land stays out of the ring (it is land already)
        var defaultLand = DefaultRange.Blocks.Where(kv => kv.Value == BlockClass.Land).Select(kv => kv.Key).ToHashSet();
        var ring = Ring(landBlocks, defaultLand, p.Frame);
        Assert.Subset(waterBlocks, ring);
        Assert.All(ring, w => Assert.Contains(landBlocks, l => Distance(l, w) <= 2));
        // beyond the ring (Cayo Perico): the blocks whose sea is shallower than 200 m, as the default range's, inside the
        // same frame and joined to the preset's water
        var far = waterBlocks.Except(ring).ToHashSet();
        Assert.Equal(beyond, far.Count);
        Assert.All(far, b => Assert.True(p.Frame.Contains(b)));
        Assert.All(far, b => Assert.Contains(waterBlocks, w => w != b && Distance(w, b) == 1));
    }

    [Fact]
    public void BlocksTakeTheirClassFromThePresetsTheRangeUses()
    {
        var defaultOnly = RangePresets.Classify(DefaultRange.Blocks.Keys);
        Assert.Equal(DefaultRange.Blocks.OrderBy(kv => kv.Key), defaultOnly);

        // Cayo Perico lies outside the default range: its blocks always take its classes, added one by one or not
        var cayoLand = BlockId.Parse("z8_124_180");
        var cayoWater = BlockId.Parse("z8_108_176");
        var both = RangePresets.Classify([cayoLand, cayoWater]);
        Assert.Equal((BlockClass.Land, BlockClass.Water), (both[cayoLand], both[cayoWater]));

        // Roxwood makes 36 water blocks of the default range land, only in a range that holds a block of its own
        var roxwood = RangePresets.Find("roxwood")!;
        var over = roxwood.Blocks.Where(kv => kv.Value == BlockClass.Land && DefaultRange.Blocks.TryGetValue(kv.Key, out var c) && c == BlockClass.Water)
            .Select(kv => kv.Key).ToList();
        Assert.Equal(36, over.Count);
        Assert.Contains(BlockId.Parse("z8_48_8"), over);                  // the default range's first water block
        var withRoxwood = RangePresets.Classify(DefaultRange.Blocks.Keys.Append(roxwood.Own[0]));
        Assert.All(over, b => Assert.Equal(BlockClass.Land, withRoxwood[b]));
        Assert.All(over, b => Assert.Equal(BlockClass.Water, defaultOnly[b]));

        // in no table: open sea
        Assert.Equal(BlockClass.Water, RangePresets.Classify([BlockId.Parse("z8_0_0")])[BlockId.Parse("z8_0_0")]);
    }

    [Fact]
    public void APresetGoesIntoTheRangeOnceTheFrameHoldsIt()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("p.fxmapgen.json"));
        var outside = Assert.Throws<ProjectException>(() => p.AddPreset("cayoPerico"));
        Assert.Equal("INVALID", outside.Code);
        Assert.Contains("88 block(s) of the preset 'cayoPerico' lie outside the map's frame", outside.Message, StringComparison.Ordinal);
        Assert.Contains("top 0, bottom 1, left 0, right 1", outside.Message, StringComparison.Ordinal);
        Assert.Equal(1045, p.Range.Count);                                   // nothing went in

        p.File.Range.ExtraCells = new ExtraCellsSetting { Bottom = 1, Right = 1 };
        Assert.Equal(118, p.AddPreset("cayoPerico"));
        Assert.Equal(0, p.AddPreset("cayoPerico"));                         // in already
        var r = p.Range;
        Assert.Equal((1163, 705, 458), (r.Count, r.Values.Count(c => c == BlockClass.Land), r.Values.Count(c => c == BlockClass.Water)));
        Assert.Empty(p.Validate());

        p.File.Range.ExtraCells = new ExtraCellsSetting { Top = 1, Bottom = 1, Right = 1 };
        Assert.Equal(175, p.AddPreset("roxwood"));
        r = p.Range;
        Assert.Equal((1338, 705 + 97 + 36, 458 + 78 - 36), (r.Count, r.Values.Count(c => c == BlockClass.Land), r.Values.Count(c => c == BlockClass.Water)));

        Assert.Equal("INVALID", Assert.Throws<ProjectException>(() => p.AddPreset("atlantis")).Code);
        p.ResetRange();
        Assert.Equal(1045, p.Range.Count);
    }
}
