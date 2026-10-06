using FxMapGenerator.Core.Grids;

namespace FxMapGenerator.Core.Tests.Grids;

public sealed class GridTests
{
    /// <summary>A bool grid from rows of '1' / '0' (or '#' / '.').</summary>
    static Grid<bool> G(params string[] rows) =>
        new(rows[0].Length, rows.Length, rows.SelectMany(r => r.Select(ch => ch is '1' or '#')).ToArray());

    static string[] Rows(Grid<bool> g) =>
        Enumerable.Range(0, g.Height).Select(r => new string(Enumerable.Range(0, g.Width).Select(c => g[r, c] ? '1' : '0').ToArray())).ToArray();

    [Theory]
    [InlineData(3, new[] { "010", "111", "010" })]
    [InlineData(5, new[] { "00100", "11111", "11111", "11111", "00100" })]
    [InlineData(7, new[] { "0001000", "0111110", "1111111", "1111111", "1111111", "0111110", "0001000" })]
    [InlineData(9, new[] { "000010000", "011111110", "011111110", "111111111", "111111111", "111111111", "011111110", "011111110", "000010000" })]
    public void EllipticKernelsHaveTheirShape(int k, string[] rows)
    {
        var e = Kernel.CvEllipse(k, k);
        Assert.Equal(rows, Rows(new Grid<bool>(k, k, e.Mask)));
        Assert.Equal((k / 2, k / 2), (e.AnchorX, e.AnchorY));
    }

    [Fact]
    public void TheTwoEdgeConventionsOfErosion()
    {
        var all = G("111", "111", "111");
        Assert.Equal(Rows(all), Rows(Morphology.Erode(all, Kernel.Rect(3, 3))));                 // outside never counts
        Assert.Equal(new[] { "000", "010", "000" }, Rows(Morphology.ErodeEdgeOff(all, Kernel.Rect(3, 3))));   // outside is off
        var dot = G("00000", "00100", "00000");
        Assert.Equal(new[] { "01110", "01110", "01110" }, Rows(Morphology.Dilate(dot, Kernel.Rect(3, 3))));
        Assert.Equal(new[] { "01110", "11111", "01110" }, Rows(Morphology.Dilate(dot, Kernel.Cross3(), iterations: 2)));
        // an opening removes what the kernel does not fit into; with the edge off, a band along the edge goes too
        var bar = G("11100", "11100", "11101", "00001");
        Assert.Equal(new[] { "11100", "11100", "11100", "00000" }, Rows(Morphology.Open(bar, Kernel.Rect(3, 3))));
        Assert.Equal(new[] { "11100", "11100", "11100", "00000" }, Rows(Morphology.OpenEdgeOff(bar, Kernel.Rect(3, 3))));   // (1,1) fits either way
        var band = G("1110", "1110", "0000");
        Assert.Equal(new[] { "1110", "1110", "0000" }, Rows(Morphology.Open(band, Kernel.Rect(3, 3))));
        Assert.Equal(new[] { "0000", "0000", "0000" }, Rows(Morphology.OpenEdgeOff(band, Kernel.Rect(3, 3))));
    }

    [Fact]
    public void GreyOpeningRemovesWhatIsNarrowerThanTheKernel()
    {
        var src = new Grid<float>(7, 1, [1, 1, 5, 1, 1, 9, 9]);
        var k = Kernel.Rect(3, 1);
        Assert.Equal(new float[] { 1, 1, 1, 1, 1, 1, 9 }, Morphology.GreyErode(src, k).Data);
        Assert.Equal(new float[] { 1, 1, 1, 1, 1, 9, 9 }, Morphology.GreyOpen(src, k).Data);    // the 1-cell peak goes, the 2-cell plateau at the edge stays
        Assert.Equal(new float[] { 1, 5, 5, 5, 9, 9, 9 }, Morphology.GreyDilate(src, k).Data);
    }

    [Fact]
    public void GreyMorphologyIsTheExtremeUnderTheKernel()
    {
        // centred kernels take the widening way, others the running one: both against a plain look at every cell
        var rnd = new Random(4);
        foreach (var (w, h) in new[] { (37, 23), (2, 9), (1, 6), (40, 3) })
        {
            var src = new Grid<float>(w, h, Enumerable.Range(0, w * h).Select(_ => (float)Math.Round(rnd.NextDouble() * 50, 1)).ToArray());
            foreach (var k in new[] { Kernel.CvEllipse(9, 9), Kernel.CvEllipse(11, 7), Kernel.Rect(5, 3), Kernel.Cross3(), Kernel.FromMask(3, 2, [true, true, false, false, true, true]) })
            {
                Assert.Equal(Plain(src, k, min: true).Data, Morphology.GreyErode(src, k).Data);
                Assert.Equal(Plain(src, k, min: false).Data, Morphology.GreyDilate(src, k).Data);
            }
        }

        static Grid<float> Plain(Grid<float> s, Kernel k, bool min)
        {
            var o = new Grid<float>(s.Width, s.Height);
            for (int y = 0; y < s.Height; y++)
                for (int x = 0; x < s.Width; x++)
                {
                    float v = min ? float.PositiveInfinity : float.NegativeInfinity;
                    for (int ky = 0; ky < k.Height; ky++)
                        for (int kx = 0; kx < k.Width; kx++)
                        {
                            int yy = y + ky - k.AnchorY, xx = x + kx - k.AnchorX;
                            if (!k.Mask[ky * k.Width + kx] || !s.Inside(yy, xx)) continue;
                            v = min ? Math.Min(v, s[yy, xx]) : Math.Max(v, s[yy, xx]);
                        }
                    o[y, x] = v;
                }
            return o;
        }
    }

    [Fact]
    public void ThickLinesAsOpenCvDrawsThem()
    {
        // cv2.polylines(img, [points], False, 1, thickness) on a 16 x 11 grid
        string[] Draw(int t, params (int, int)[] pts)
        {
            var g = new Grid<bool>(16, 11);
            ThickLines.Polyline(g, pts, t);
            return Rows(g).Select(r => r.Replace('1', '#').Replace('0', '.')).ToArray();
        }
        Assert.Equal(new[]
        {
            "................", "................", "................", "..############..", ".##############.", "################",
            ".##############.", "..############..", "................", "................", "................",
        }, Draw(3, (2, 5), (13, 5)));
        Assert.Equal(new[]
        {
            "#####...........", "######..........", "#######.........", "#########.......", ".#########......", "...########.....",
            "....#########...", ".....#########..", ".......#######..", "........#######.", ".........#####..",
        }, Draw(4, (1, 1), (12, 9)));
        Assert.Equal(new[]                                                   // one end outside: cut to the grid widened by the thickness
        {
            "................", "#####...........", "###########.....", "################", "################", "################",
            "################", "################", ".....###########", "...........#####", "................",
        }, Draw(5, (-5, 3), (20, 7)));
        Assert.Equal(new[]                                                   // a segment of no length: the disc alone
        {
            "................", "................", "........#.......", "......#####.....", "......#####.....", ".....#######....",
            "......#####.....", "......#####.....", "........#.......", "................", "................",
        }, Draw(6, (8, 5), (8, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Draw(1, (0, 0), (5, 5)));
    }

    [Fact]
    public void ComponentsAreNumberedByTheirFirstCell()
    {
        var m = G("0011", "1001", "1100");
        var four = Components.Label(m, 4);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0 }, four.Label.Data);
        Assert.Equal(new[] { 0, 3, 3 }, four.Area);
        var eight = Components.Label(G("10", "01"), 8);
        Assert.Equal(1, eight.Count);
        Assert.Equal(2, Components.Label(G("10", "01"), 4).Count);
        Assert.Equal(new[] { "0011", "0001", "0000" }, Rows(Components.KeepAtLeast(G("0011", "0001", "1000"), 2, 4)));
    }

    [Fact]
    public void NearestFeatureKeepsTheFirstOfEquallyNearOnes()
    {
        // (0,1) is 1 from both (0,0) and (0,2): the lower column; (1,0) is 1 from (0,0) and (2,0): the lower row
        var f = G("101", "000", "100");
        var n = DistanceTransform.Nearest(f);
        Assert.Equal(0, n[1]);
        Assert.Equal(0, n[3]);
        Assert.Equal(new[] { 0.0, 1.0, 0.0 }, DistanceTransform.Distance(f).Take(3));
        Assert.Equal(Math.Sqrt(2), DistanceTransform.Distance(f)[4], 12);   // the centre: three features at the same distance
        Assert.Equal(0, n[4]);
        Assert.All(DistanceTransform.Nearest(G("000", "000")), i => Assert.Equal(-1, i));

        var v = new Grid<float>(3, 1, [float.NaN, 2, float.NaN]);
        Assert.Equal(new float[] { 2, 2, 2 }, DistanceTransform.FillNaN(v).Data);
        Assert.All(DistanceTransform.FillNaN(new Grid<float>(2, 1, [float.NaN, float.NaN])).Data, x => Assert.True(float.IsNaN(x)));
    }

    [Fact]
    public void BoxCountsMirrorTheEdge()
    {
        var row = G("10011");
        // 3-wide windows over 1 0 0 1 1, mirrored: [1]1 0 0 1 1[1]; the single row mirrors onto itself 3 times
        Assert.Equal(new[] { 6, 3, 3, 6, 9 }, Filters.BoxCountReflect(row, 3).Data);
        Assert.Equal((0, 1, 3, 0), (Filters.Reflect(-1, 5), Filters.Reflect(-2, 5), Filters.Reflect(6, 5), Filters.Reflect(-3, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Filters.BoxCountReflect(row, 4));
    }

    [Fact]
    public void PointsWithinARadiusCountTheBorderAndComeSorted()
    {
        var ix = new PointIndex([(0, 0), (30, 0), (5, 0), (-5, 0), (0, 25.0001)], 25);
        Assert.Equal(new[] { 0, 2, 3 }, ix.Within(0, 0, 5));
        Assert.Equal(new[] { 0, 1, 2 }, ix.Within(15, 0, 15));
        Assert.Empty(ix.Within(100, 100, 10));
    }

    [Fact]
    public void GridFilesKeepTypesValuesAndMeta()
    {
        using var tmp = new TempFolder();
        var f = new GridFile();
        f.Meta["block"] = "z8_60_132";
        f.Add("lc", new Grid<byte>(2, 2, [1, 2, 3, 4]));
        f.Add("water", G("10", "01"));
        f.Add("ndsm", new Grid<float>(3, 1, [1.5f, float.NaN, -2]));
        f.Add("labels", new Grid<int>(1, 2, [7, -1]));
        f.Save(tmp.File("x.grid"));
        var g = GridFile.Load(tmp.File("x.grid"));
        Assert.Equal("z8_60_132", (string)g.Meta["block"]!);
        Assert.Equal(new[] { "lc", "water", "ndsm", "labels" }, g.Names);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, g.Get<byte>("lc").Data);
        Assert.Equal(new[] { true, false, false, true }, g.Get<bool>("water").Data);
        Assert.True(float.IsNaN(g.Get<float>("ndsm").Data[1]));
        Assert.Equal((1, 2), (g.Get<int>("labels").Width, g.Get<int>("labels").Height));
        Assert.Throws<InvalidDataException>(() => g.Get<int>("lc"));
        File.WriteAllText(tmp.File("bad.grid"), "not a grid");
        Assert.Throws<InvalidDataException>(() => GridFile.Load(tmp.File("bad.grid")));
        Assert.False(File.Exists(tmp.File("x.grid.tmp")));
    }

    [Fact]
    public void CropAndPaste()
    {
        var g = new Grid<int>(3, 3, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        Assert.Equal(new[] { 5, 6, 8, 9 }, g.Crop(1, 1, 2, 2).Data);
        var big = Grid<int>.Filled(4, 4, 0);
        big.Paste(g, 2, -1);                                     // partly outside: clipped
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 0, 0, 5, 6, 0, 0 }, big.Data);
    }
}
