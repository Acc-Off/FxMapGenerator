using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Cells;

/// <summary>The parts of a cell's map data on small fixed grids, against fixed expected values.</summary>
public sealed class CellPartsTests
{
    [Fact]
    public void HolesAreFilledFromTheGroundAround()
    {
        float[] z = [1, 2, 3, 4, 5, 6, 7, 2, 3, 4, 5, 6, 7, 8, 3, 4, 50, 50, 7, 8, 9, 4, 5, 50, 50, 8, 9, 10, 5, 6, 7, 8, 9, 10, 11, 6, 7, 8, 9, 10, 11, 12];
        var grid = new Grid<float>(7, 6, z);
        var hole = grid.Map(v => v == 50);
        var est = CellHeights.FillFromAround(grid, hole);
        Assert.Equal([4.885723278091145, 6.0696351572531, 6.012808228242658, 7.196069395139429], Enumerable.Range(0, 42).Where(i => hole.Data[i]).Select(i => est.Data[i]));
        Assert.Equal(1.0, est.Data[0]);                                                // known cells stay
    }

    [Fact]
    public void TheLightOfSeveralLights()
    {
        var z = new Grid<double>(9, 8);
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 9; c++)
                z[r, c] = r * 0.7 + c * 1.3 + Math.Sin(r * 9 + c) * 2;
        var sh = new ShadeStyle(1.0, 45, [(315, 0.4), (270, 0.2), (0, 0.2), (225, 0.2)], 0.5);
        var (light, flat) = CellHeights.Light(z, sh);
        Assert.Equal(0.7071067811865476, flat);
        Assert.Equal([0.7798279094463041, 0.8020748499606883, 0.7945377872269253], light.Data[..3]);
        Assert.Equal(new byte[] { 199, 205, 203, 179, 151, 151, 167, 193, 209, 187, 190, 183, 168, 154, 155, 168, 186, 197, 162, 164, 164, 161, 158, 158,
            160, 164, 165, 157, 160, 160, 159, 158, 158, 159, 160, 158, 156, 158, 158, 159, 160, 160, 160, 158, 155, 156, 156, 158, 161, 163, 164, 161,
            157, 153, 162, 154, 153, 166, 181, 184, 174, 158, 143, 162, 148, 149, 175, 200, 194, 176, 155, 136 }, CellHeights.Quantize(light).Data);
    }

    static CellGrids Grids(int w, int h, byte[] lc)
    {
        return new CellGrids
        {
            Area = CellArea.ForGrid(w, h),
            Landcover = new Grid<byte>(w, h, lc), Water = new Grid<bool>(w, h), Buildings = new Grid<bool>(w, h), Canopy = new Grid<bool>(w, h),
            MaterialClass = new Grid<byte>(w, h), Rail = new Grid<bool>(w, h), Depth = Grid<float>.Filled(w, h, float.NaN),
            Zone = new Grid<ushort>(w, h), ZoneCodes = [""], Ok = Grid<bool>.Filled(w, h, true),
        };
    }

    [Fact]
    public void TheBlendedGroundPicture()
    {
        byte[] lc = [2, 2, 3, 3, 1, 1, 2, 7, 7, 3, 1, 1, 6, 6, 7, 1, 1, 0, 4, 4, 5, 5, 9, 9, 2, 2, 2, 8, 8, 8];
        var g = Grids(6, 5, lc);
        g.Rail[2, 3] = true;
        var rgba = GroundRaster.Make(g, Blend("{}"), null);
        int[][] expected = [[188, 200, 131], [188, 200, 131], [189, 200, 132], [189, 200, 133], [211, 212, 206], [211, 212, 206], [188, 200, 131], [188, 200, 131],
            [189, 200, 132], [189, 201, 133], [211, 212, 206], [211, 212, 206], [188, 200, 131], [188, 200, 131], [189, 200, 132], [211, 212, 206],
            [211, 212, 206], [211, 212, 206], [188, 200, 131], [189, 200, 131], [189, 200, 132], [189, 201, 133], [122, 157, 191], [122, 157, 191],
            [188, 200, 131], [189, 200, 132], [189, 200, 132], [189, 201, 133], [190, 201, 134], [190, 201, 134]];
        for (int i = 0; i < 30; i++)
        {
            Assert.Equal(expected[i], new int[] { rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2] });
            Assert.Equal(255, rgba[4 * i + 3]);
        }
    }

    /// <summary>
    /// The PostalCodeMap style with its ground blended by a plain blur 5 m wide (what these tests' pictures were made
    /// with; the bundled style adds some of the unblurred colours back), the ground raster's values replaced by
    /// <paramref name="values"/>.
    /// </summary>
    static MapStyle Blend(string values)
    {
        var root = MapStyle.Builtin("postalcodemap").Source.DeepClone().AsObject();
        var gr = root["groundRaster"]!.AsObject();
        gr["method"] = "blur";
        gr["detail"] = new System.Text.Json.Nodes.JsonObject { ["width"] = 5, ["amount"] = 0.4 };
        foreach (var (k, v) in System.Text.Json.Nodes.JsonNode.Parse(values)!.AsObject()) gr[k] = v?.DeepClone();
        return MapStyle.Parse(root.ToJsonString(), "test");
    }

    static int[] Rgb(byte[] rgba, int i) => [rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2]];

    [Fact]
    public void TheBlendMethodsAreRead()
    {
        // the bundled style: a blur 10 m wide with a quarter of the unblurred colours added back
        var pcm = MapStyle.Builtin("postalcodemap").GroundRaster!;
        Assert.Equal(("detail", 10.0, 0.25), (pcm.Method, pcm.Width, pcm.Amount));
        var plain = Blend("{}").GroundRaster!;
        Assert.Equal(("blur", 5.0), (plain.Method, plain.Width));
        var s = Blend("""{ "method": "patches", "patches": { "minArea": 25, "edgeWidth": 1.5 } }""").GroundRaster!;
        Assert.Equal(("patches", 25.0, 1.5), (s.Method, s.MinArea, s.Width));
        s = Blend("""{ "method": "detail" }""").GroundRaster!;
        Assert.Equal((5.0, 0.4), (s.Width, s.Amount));
        s = Blend("""{ "method": "brush" }""").GroundRaster!;
        Assert.Equal((3.0, 1.0), (s.Radius, s.Width));
        s = Blend("""{ "method": "none" }""").GroundRaster!;
        Assert.Equal(("none", 0.0), (s.Method, s.Width));
        var e = Assert.Throws<StyleException>(() => Blend("""{ "method": "smudge", "detail": { "width": 5, "amount": 1.5 } }"""));
        Assert.Contains("groundRaster.method 'smudge'", e.Message);
        Assert.Contains("groundRaster.detail.amount: 1.5 is out of range", e.Message);   // the other methods' values are checked too
        e = Assert.Throws<StyleException>(() => Blend("""{ "method": "brush", "brush": null }"""));
        Assert.Contains("'brush' must be an object", e.Message);                         // the chosen method's values are needed
    }

    [Fact]
    public void OnlyTheChosenMethodsValuesAreRecorded()
    {
        var pcm = Blend("{}");
        var a = Blend("""{ "patches": { "minArea": 40, "edgeWidth": 2 } }""");
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(CellPrepStage.GroundValues(pcm), CellPrepStage.GroundValues(a)));
        Assert.Equal(FxMapGenerator.Core.Render.CellDrawStage.DrawingText(pcm), FxMapGenerator.Core.Render.CellDrawStage.DrawingText(a));
        Assert.Equal(["mode", "natural", "method", "blur"], CellPrepStage.GroundValues(pcm)["groundRaster"]!.AsObject().Select(kv => kv.Key));
        var b = Blend("""{ "method": "patches" }""");
        Assert.False(System.Text.Json.Nodes.JsonNode.DeepEquals(CellPrepStage.GroundValues(pcm), CellPrepStage.GroundValues(b)));
        Assert.NotEqual(FxMapGenerator.Core.Render.CellDrawStage.DrawingText(pcm), FxMapGenerator.Core.Render.CellDrawStage.DrawingText(b));
    }

    [Fact]
    public void AGroundPixelTellsTheKindsItMixes()
    {
        // 10 x 8: grass on the west half, dirt on the east half
        var lc = Enumerable.Range(0, 80).Select(i => (byte)(i % 10 < 5 ? 2 : 3)).ToArray();
        var g = Grids(10, 8, lc);
        var none = GroundRaster.Explain(g, Blend("""{ "method": "none" }"""), null, null, 4, 4);
        Assert.Equal([("ground", "grass", 1.0)], none.Select(p => (p.Kind, p.Id, p.Share)));
        Assert.Equal(GroundRaster.ClassColors(Blend("""{ "method": "none" }"""))[2], none[0].Color);
        // blurred: next to the edge both, the node's own kind the larger share
        var blur = GroundRaster.Explain(g, Blend("""{ "blur": { "width": 2 } }"""), null, null, 4, 4);
        Assert.Equal(["grass", "dirt"], blur.Select(p => p.Id));
        Assert.Equal(1.0, blur.Sum(p => p.Share), 6);
        Assert.InRange(blur[0].Share, 0.5, 0.95);
        // far from the edge (beyond the blur's reach) one kind
        var wide = Grids(40, 8, Enumerable.Range(0, 320).Select(i => (byte)(i % 40 < 20 ? 2 : 3)).ToArray());
        Assert.Equal(["grass"], GroundRaster.Explain(wide, Blend("""{ "blur": { "width": 2 } }"""), null, null, 2, 4).Select(p => p.Id));
    }

    [Fact]
    public void NoneKeepsTheMaterialColours()
    {
        byte[] lc = [2, 2, 3, 3, 1, 1, 2, 7, 7, 3, 1, 1, 6, 6, 7, 1, 1, 0, 4, 4, 5, 5, 9, 9, 2, 2, 2, 8, 8, 8];
        var g = Grids(6, 5, lc);
        g.Rail[2, 3] = true;
        var none = GroundRaster.Make(g, Blend("""{ "method": "none" }"""), null);
        Assert.Equal(GroundRaster.Make(g, Blend("""{ "blur": { "width": 0.01 } }"""), null), none);
        Assert.Equal([0x8b, 0xae, 0x4d], Rgb(none, 0));
        Assert.Equal([0xcf, 0xc9, 0x8e], Rgb(none, 2));
    }

    [Fact]
    public void GroundOfTheDefaultMaterialBlendsWhereTheStyleSaysSo()
    {
        // 12 x 8 grass: a block of the DEFAULT material's ground (class 10) inside it, urban ground along the east edge
        var lc = Enumerable.Repeat((byte)2, 96).ToArray();
        for (int y = 2; y < 6; y++) for (int x = 3; x < 7; x++) lc[y * 12 + x] = 10;
        for (int y = 0; y < 8; y++) lc[y * 12 + 11] = 1;
        var g = Grids(12, 8, lc);
        int[] grass = [0x8b, 0xae, 0x4d], ground = [0xd3, 0xd4, 0xce];
        // the bundled style blends it with the natural kinds: its edge is neither the base ground's colour nor the grass's
        var blended = GroundRaster.Make(g, Blend("""{ "blur": { "width": 2 } }"""), null);
        Assert.NotEqual(ground, Rgb(blended, 3 * 12 + 3));
        Assert.NotEqual(grass, Rgb(blended, 3 * 12 + 3));
        Assert.NotEqual(grass, Rgb(blended, 3 * 12 + 2));               // the grass next to it takes some of its colour
        Assert.Equal(ground, Rgb(blended, 3 * 12 + 11));                // urban ground keeps its colour and its edge
        // left out of the kinds blended, it is painted as it is, like urban ground
        var crisp = GroundRaster.Make(g, Blend("""{ "natural": ["grass", "vegetation", "dirt", "sand", "beach", "rock", "snow"], "blur": { "width": 2 } }"""), null);
        Assert.Equal(ground, Rgb(crisp, 3 * 12 + 3));
        Assert.Equal(grass, Rgb(crisp, 3 * 12 + 2));
        Assert.Equal(["ground", "ground"], new[] { 1, 10 }.Select(c => MapStyle.Builtin("postalcodemap").GroundPaints[c]));
    }

    [Fact]
    public void SmallPatchesTakeTheGroundAroundThem()
    {
        // 10 x 8 grass: a 2 x 2 dirt patch inside it, a 4 x 4 dirt patch in the corner, a lone vegetation node (grass's colour)
        var lc = Enumerable.Repeat((byte)2, 80).ToArray();
        foreach (var (x, y) in new[] { (2, 2), (3, 2), (2, 3), (3, 3) }) lc[y * 10 + x] = 3;
        for (int y = 4; y < 8; y++) for (int x = 6; x < 10; x++) lc[y * 10 + x] = 3;
        lc[1 * 10 + 1] = 7;
        var g = Grids(10, 8, lc);
        var rgba = GroundRaster.Make(g, Blend("""{ "method": "patches", "patches": { "minArea": 10, "edgeWidth": 0.01 } }"""), null);
        int[] grass = [0x8b, 0xae, 0x4d], dirt = [0xcf, 0xc9, 0x8e];
        Assert.Equal(grass, Rgb(rgba, 2 * 10 + 2));                     // the small patch is grass now
        Assert.Equal(grass, Rgb(rgba, 3 * 10 + 3));
        Assert.Equal(dirt, Rgb(rgba, 5 * 10 + 7));                      // the big one stays
        Assert.Equal(grass, Rgb(rgba, 1 * 10 + 1));
        var none = GroundRaster.Make(g, Blend("""{ "method": "none" }"""), null);
        Assert.Equal(dirt, Rgb(none, 2 * 10 + 2));                      // without the method it stays
    }

    [Fact]
    public void DetailAddsBackTheUnblurredColours()
    {
        byte[] lc = [2, 2, 3, 3, 1, 1, 2, 7, 7, 3, 1, 1, 6, 6, 7, 1, 1, 0, 4, 4, 5, 5, 9, 9, 2, 2, 2, 8, 8, 8];
        var g = Grids(6, 5, lc);
        Assert.Equal(GroundRaster.Make(g, Blend("{}"), null), GroundRaster.Make(g, Blend("""{ "method": "detail", "detail": { "width": 5, "amount": 0 } }"""), null));
        Assert.Equal(GroundRaster.Make(g, Blend("""{ "method": "none" }"""), null), GroundRaster.Make(g, Blend("""{ "method": "detail", "detail": { "width": 5, "amount": 1 } }"""), null));
    }

    [Fact]
    public void TheBrushKeepsEdgesAndDropsSpecks()
    {
        // 12 x 8: grass on the west half, dirt on the east half, one dirt speck in the grass
        var lc = new byte[96];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 12; x++) lc[y * 12 + x] = (byte)(x < 6 ? 2 : 3);
        lc[3 * 12 + 2] = 3;
        var g = Grids(12, 8, lc);
        var rgba = GroundRaster.Make(g, Blend("""{ "method": "brush", "brush": { "radius": 2, "edgeWidth": 0.01 } }"""), null);
        int[] grass = [0x8b, 0xae, 0x4d], dirt = [0xcf, 0xc9, 0x8e];
        Assert.Equal([147, 177, 84], Rgb(rgba, 3 * 12 + 2));            // the speck is in every square: 8 / 9 grass
        Assert.Equal(grass, Rgb(rgba, 3 * 12 + 1));                     // and gone from the nodes around it
        Assert.Equal(grass, Rgb(rgba, 3 * 12 + 3));
        Assert.Equal(grass, Rgb(rgba, 2 * 12 + 2));
        for (int y = 0; y < 8; y++)
        {
            Assert.Equal(grass, Rgb(rgba, y * 12 + 5));                 // the edge stays where it was
            Assert.Equal(dirt, Rgb(rgba, y * 12 + 6));
        }
    }

    [Fact]
    public void LayersFollowTheStylesValues()
    {
        // 6 x 5: a grass / dirt ground, a pond with a rock bed patch, two buildings in two zones
        byte[] lc = [2, 2, 3, 3, 1, 1, 2, 2, 3, 3, 1, 1, 2, 9, 9, 9, 1, 1, 2, 9, 9, 9, 1, 1, 2, 2, 2, 2, 1, 1];
        var g = Grids(6, 5, lc);
        for (int i = 0; i < 30; i++) g.Water.Data[i] = lc[i] == 9;
        g.Depth[2, 1] = 0.3f; g.Depth[2, 2] = 4f; g.Depth[2, 3] = 60f; g.Depth[3, 1] = 12f; g.Depth[3, 2] = 12f; g.Depth[3, 3] = 12f;
        g.MaterialClass[3, 3] = (byte)Materials.Default.IndexOf("rock");
        g.Buildings[0, 4] = g.Buildings[1, 4] = true;                       // DOWNT: grey
        g.Buildings[3, 5] = g.Buildings[4, 5] = true;                       // SANDY: brown
        var zones = new Grid<ushort>(6, 5);
        zones[0, 4] = zones[1, 4] = 1;
        zones[3, 5] = zones[4, 5] = 2;
        g = new CellGrids
        {
            Area = g.Area, Landcover = g.Landcover, Water = g.Water, Buildings = g.Buildings, Canopy = g.Canopy, MaterialClass = g.MaterialClass,
            Rail = g.Rail, Depth = g.Depth, Zone = zones, ZoneCodes = ["", "DOWNT", "SANDY"], Ok = g.Ok,
        };
        var atlas = MapStyle.Builtin("postalcodemap");
        var road = MapStyle.Builtin("roadmap");
        var masks = CellLayers.Masks(g, [atlas, atlas, road], null, Materials.Default);
        string Sig(CellLayers.Masked m) => $"{m.Kind}/{m.Paint}{(m.Band >= 0 ? "/" + m.Band : "")}:{m.Mask!.Data.Count(x => x)}";
        var atlasSea = masks.Where(m => m.Kind == "sea" && m.Set == CellLayers.Sets.Sea(atlas)).Select(Sig);
        var roadSea = masks.Where(m => m.Kind == "sea" && m.Set == CellLayers.Sets.Sea(road)).Select(Sig);
        Assert.Equal(["ground/ground:10", "ground/grass:10", "ground/dirt:4"], masks.Where(m => m.Kind == "ground").Select(Sig));
        Assert.Equal(["water/water:6"], masks.Where(m => m.Kind == "water").Select(Sig));
        // the atlas bands: 0.3 m (band 2, 0.25..0.5), 4 m (6), 60 m (10), three 12 m cells (8) through the 3 x 3 median;
        // the rock cell is smaller than 200 m², so it stays sand
        Assert.Equal(6, atlasSea.Sum(s => int.Parse(s.Split(':')[1])));
        Assert.All(atlasSea, s => Assert.StartsWith("sea/sand/", s));
        Assert.Equal(6, roadSea.Sum(s => int.Parse(s.Split(':')[1])));
        Assert.Equal(["building/brown:2", "building/grey:2"], masks.Where(m => m.Kind == "building").Select(Sig));
        Assert.Single(masks.Where(m => m.Kind == "building").Select(m => m.Set).Distinct());   // the three styles share one building table
    }
}
