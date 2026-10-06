using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.World;

public sealed class WorldGridTests
{
    [Theory]
    [InlineData("z8_48_8", 12, 2)]
    [InlineData("z8_0_0", 0, 0)]
    [InlineData("z8_124_188", 31, 47)]
    public void BlockNamesRoundTrip(string name, int bx, int by)
    {
        var b = BlockId.Parse(name);
        Assert.Equal((bx, by), (b.Bx, b.By));
        Assert.Equal(name, b.Name);
    }

    [Theory]
    [InlineData("z8_-8_-32", -2, -8)]            // west and north of the standard frame: negative numbers
    [InlineData("z8_-128_-64", -32, -16)]        // the north-west corner of the frame every project's lies in
    [InlineData("z8_252_252", 63, 63)]           // its south-east corner
    [InlineData("z8_128_0", 32, 0)]              // east of the standard frame
    public void BlockNamesOutsideTheStandardFrameRoundTrip(string name, int bx, int by)
    {
        var b = BlockId.Parse(name);
        Assert.Equal((bx, by), (b.Bx, b.By));
        Assert.Equal(name, b.Name);
        Assert.Equal(b, BlockId.At(b.Center.X, b.Center.Y));
    }

    [Theory]
    [InlineData("z8_49_8")]      // not a block origin
    [InlineData("z8_-6_0")]      // not a block origin either
    [InlineData("z8_256_0")]     // east of the frame every project's frame lies in
    [InlineData("z8_0_256")]     // south of it
    [InlineData("z8_-132_0")]    // west of it
    [InlineData("z8_0_-68")]     // north of it
    [InlineData("z8_99999999999_0")]
    [InlineData("z8_--4_0")]
    [InlineData("z9_0_0")]
    [InlineData("cell_0_1")]
    [InlineData(null)]
    public void InvalidBlockNamesAreRefused(string? name) => Assert.False(BlockId.TryParse(name, out _));

    [Fact]
    public void BlocksWestAndNorthOfTheOriginBelongToNegativeCells()
    {
        Assert.Equal(new CellId(-1, -1), new BlockId(-1, -1).Cell);
        Assert.Equal(new CellId(-1, 0), new BlockId(0, -8).Cell);
        Assert.Equal(new CellId(0, -1), new BlockId(-8, 7).Cell);
        Assert.Equal(new CellId(-2, -4), new BlockId(-32, -16).Cell);
        Assert.Equal(new CellId(0, 0), new BlockId(7, 7).Cell);
        Assert.Equal("cell_-1_0", new CellId(-1, 0).Name);
        Assert.True(CellId.TryParse("cell_-2_-4", out var c) && c == new CellId(-2, -4));
        Assert.True(CellId.TryParse("cell_7_7", out _));
        Assert.False(CellId.TryParse("cell_-3_0", out _));       // north of the frame every project's lies in
        Assert.False(CellId.TryParse("cell_0_8", out _));        // east of it
        Assert.Equal(new[] { -1, -1, -1, 0, 1, -2 }, new[] { WorldGrid.FloorDiv(-1, 8), WorldGrid.FloorDiv(-8, 8), WorldGrid.FloorDiv(-7, 8), WorldGrid.FloorDiv(7, 8), WorldGrid.FloorDiv(8, 8), WorldGrid.FloorDiv(-9, 8) });
    }

    [Fact]
    public void CellsAcrossTheOriginTakeTheirMarginsFromEachOther()
    {
        // four blocks around the origin, one in each of the four cells that meet there
        var range = new[] { new BlockId(-1, -1), new BlockId(0, -1), new BlockId(-1, 0), new BlockId(0, 0) };
        var cells = CellPlan.For(range);
        Assert.Equal(new[] { "cell_-1_-1", "cell_-1_0", "cell_0_-1", "cell_0_0" }, cells.Select(c => c.Id.Name));
        Assert.All(cells, c => Assert.Single(c.Core));
        Assert.All(cells, c => Assert.Equal(3, c.Margin.Count));
        Assert.Equal(new[] { new BlockId(-1, -1) }, cells[0].Core);
    }

    [Fact]
    public void AFramesBlocksEdgesAndTiles()
    {
        var s = MapFrame.Standard;
        Assert.True(s.IsStandard);
        Assert.Equal((0, 0, 32, 48), (s.Bx0, s.By0, s.BlocksX, s.BlocksY));
        Assert.Equal((-4140.0, 8400.0, 4860.0, -5100.0), (s.X0, s.Y0, s.X1, s.Y1));
        Assert.Equal((0, 0, 128, 192), s.Tiles(8));
        Assert.Equal((0, 0, 1, 1), s.Tiles(0));
        Assert.Equal(1045, DefaultRange.Blocks.Keys.Count(s.Contains));

        var f = new MapFrame(1, 0, 2, 1);       // a cell above, two to the left, one to the right
        Assert.False(f.IsStandard);
        Assert.Equal((-16, -8, 56, 56), (f.Bx0, f.By0, f.BlocksX, f.BlocksY));
        Assert.Equal((-8640.0, 10650.0, 7110.0, -5100.0), (f.X0, f.Y0, f.X1, f.Y1));
        Assert.Equal((-64, -32, 160, 192), f.Tiles(8));
        Assert.Equal((-2, -1, 5, 6), f.Tiles(3));               // a cell is a tile of zoom 3
        Assert.Equal((-1, -1, 3, 3), f.Tiles(2));
        Assert.True(f.Contains(new BlockId(-16, -8)) && f.Contains(new BlockId(39, 47)));
        Assert.False(f.Contains(new BlockId(-17, 0)) || f.Contains(new BlockId(0, -9)) || f.Contains(new BlockId(40, 0)) || f.Contains(new BlockId(0, 48)));
        Assert.Equal(56 * 56, f.Blocks.Count());
        Assert.Equal(new BlockId(-16, -8), f.Blocks.First());
        Assert.Equal(new BlockId(39, 47), f.Blocks.Last());
        Assert.Equal(7 * 7, f.Cells.Count());
        Assert.Equal(new CellId(-1, -2), f.Cells.First());
        Assert.True(f.Contains(-8640, 10650) && !f.Contains(-8641, 10650));
        Assert.Equal(new[] { new BlockId(-1, -1), new BlockId(0, -1) }, f.Touching(-4150, 8410, -4130, 8405));
        Assert.Empty(s.Touching(-4150, 8410, -4130, 8405).Where(b => b.By < 0));
        Assert.Empty(f.Problems());
        Assert.Single(new MapFrame(2, 1, 0, 0).Problems());
        Assert.Single(new MapFrame(0, 0, 3, 2).Problems());
        Assert.Single(new MapFrame(-1, 0, 0, 0).Problems());
        Assert.True(MapFrame.Outer.Contains(new BlockId(-32, -16)) && !MapFrame.Outer.Contains(new BlockId(-33, 0)));
    }

    [Fact]
    public void TheSmallestFrameHoldingBlocksCountsWholeCellsOnEachSide()
    {
        Assert.Equal(MapFrame.Standard, MapFrame.Holding([]));
        Assert.Equal(MapFrame.Standard, MapFrame.Holding(DefaultRange.Blocks.Keys));
        Assert.Equal(MapFrame.Standard, MapFrame.Holding([new BlockId(0, 0), new BlockId(31, 47)]));      // the standard frame's corners
        // a cell is 8 blocks: the first block past an edge takes one cell, the ninth a second
        Assert.Equal(new MapFrame(1, 0, 0, 0), MapFrame.Holding([new BlockId(5, -1)]));
        Assert.Equal(new MapFrame(1, 0, 0, 0), MapFrame.Holding([new BlockId(5, -8)]));
        Assert.Equal(new MapFrame(2, 0, 0, 0), MapFrame.Holding([new BlockId(5, -9)]));
        Assert.Equal(new MapFrame(0, 1, 0, 0), MapFrame.Holding([new BlockId(5, 48)]));
        Assert.Equal(new MapFrame(0, 1, 0, 0), MapFrame.Holding([new BlockId(5, 55)]));
        Assert.Equal(new MapFrame(0, 2, 0, 0), MapFrame.Holding([new BlockId(5, 56)]));
        Assert.Equal(new MapFrame(0, 0, 1, 0), MapFrame.Holding([new BlockId(-8, 5)]));
        Assert.Equal(new MapFrame(0, 0, 2, 0), MapFrame.Holding([new BlockId(-9, 5)]));
        Assert.Equal(new MapFrame(0, 0, 0, 1), MapFrame.Holding([new BlockId(32, 5), new BlockId(39, 5)]));
        // the blocks of the one project with cells up and left: z8_-16_40 and z8_-28_-16
        Assert.Equal(new MapFrame(1, 0, 1, 0), MapFrame.Holding([BlockId.Parse("z8_-16_40"), BlockId.Parse("z8_-28_-16")]));
        // the Cayo Perico corner south-east of the standard frame, and a block beyond what a frame may add
        Assert.Equal(new MapFrame(0, 1, 0, 1), MapFrame.Holding([BlockId.Parse("z8_156_220")]));
        Assert.NotEmpty(MapFrame.Holding([new BlockId(0, 48 + 3 * 8)]).Problems());
    }

    [Fact]
    public void BlockCentreMatchesTheCameraPositionOfACapture()
    {
        // the capture of z8_48_8 reported its camera at x=-624.3750 y=7696.8750
        var (x, y) = BlockId.Parse("z8_48_8").Center;
        Assert.Equal(-624.375, x, 6);
        Assert.Equal(7696.875, y, 6);
        Assert.Equal(BlockId.Parse("z8_48_8"), BlockId.At(x, y));
    }

    [Fact]
    public void DefaultRangeHas674LandAnd371WaterBlocks()
    {
        var r = DefaultRange.Blocks;
        Assert.Equal(1045, r.Count);
        Assert.Equal(674, r.Values.Count(c => c == BlockClass.Land));
        Assert.Equal(371, r.Values.Count(c => c == BlockClass.Water));
        Assert.Equal(BlockClass.Water, DefaultRange.ClassOf(BlockId.Parse("z8_0_0")));   // open sea, outside the table
    }

    [Fact]
    public void CellsOfTheDefaultRange()
    {
        var cells = CellPlan.For(DefaultRange.Blocks.Keys);
        Assert.Equal(23, cells.Count);
        Assert.Equal(1045, cells.Sum(c => c.Core.Count));
        var c01 = cells.Single(c => c.Id.Name == "cell_0_1");
        Assert.Equal(43, c01.Core.Count);
        Assert.Equal(16, c01.Margin.Count);
        Assert.All(c01.Margin, b => Assert.NotEqual(c01.Id, b.Cell));
        Assert.True(CellId.TryParse("cell_0_1", out var id) && id == c01.Id);
    }
}
