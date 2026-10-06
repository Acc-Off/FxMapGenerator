using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Regions;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Regions;

public sealed class RegionsTests
{
    [Fact]
    public void TheBundledStylesRead()
    {
        foreach (var id in MapStyle.BuiltIn)
        {
            var s = MapStyle.Builtin(id);
            Assert.Equal(id, s.Id);
            Assert.Equal(s.Sea.Bands.Count + 1, s.Paint.Sea.Sand.Count);
            Assert.Equal(11, s.GroundPaints.Count);
            Assert.Equal("ground", s.GroundPaints[(int)FxMapGenerator.Core.Landcover.GroundClass.DefaultMaterial]);   // the DEFAULT material: the base ground's colour
        }
        var pcm = MapStyle.Builtin("postalcodemap");
        var reg = MapStyle.Builtin("regional");
        var road = MapStyle.Builtin("roadmap");
        Assert.Equal("blend", pcm.GroundRaster!.Mode);
        Assert.Equal("regions", reg.GroundRaster!.Mode);
        Assert.Equal(["paved", "dirt", "sand", "rock", "snow"], reg.GroundRaster.Tones.Select(t => t.Name));
        Assert.Equal(["urban", "defaultMaterial"], GroundRasterStyle.ToneClasses[0].Classes);   // the paved tone covers the DEFAULT material too
        Assert.Contains("defaultMaterial", pcm.GroundRaster.Natural);                           // and the blend takes it among the natural kinds
        Assert.Null(road.GroundRaster);
        Assert.Null(road.Shade);
        Assert.Null(road.Labels);
        Assert.Equal(20, road.Contours!.Interval);
        Assert.Null(road.Paint.Road.Casing);
        Assert.Equal((0.5, 0.8), (pcm.Shade!.Strength, reg.Shade!.Strength));
        Assert.Equal("grey", pcm.Buildings.KeyOf("DOWNT", pcm.Regions));
        Assert.Equal("greygreen", pcm.Buildings.KeyOf("", pcm.Regions));             // no zone: the default
    }

    [Fact]
    public void TheLandOfAWindowIsThatPartOfTheWholeFramesLand()
    {
        using var tmp = new TempFolder();
        var blocks = new[] { new BlockId(3, 5), new BlockId(4, 5) };
        foreach (var b in blocks)
        {
            const int n = 282;
            var rnd = new Random(b.Bx);
            var lc = new Grid<byte>(n, n);
            var water = new Grid<bool>(n, n);
            for (int i = 0; i < n * n; i++)
            {
                lc.Data[i] = (byte)rnd.Next(0, 10);
                water.Data[i] = rnd.Next(0, 5) == 0;
            }
            var f = new GridFile();
            f.Meta["x0"] = WorldGrid.Left + b.Tx * WorldGrid.TileSize;
            f.Meta["y0"] = WorldGrid.Top - b.Ty * WorldGrid.TileSize;
            f.Add("landcover", lc);
            f.Add("water", water);
            var path = LandcoverFile.PathOf(tmp.Path, b);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            f.Save(path);
        }
        var whole = RegionField.Land4(tmp.Path, blocks, MapFrame.Standard);
        // a window over both blocks and beyond them
        int c0 = 200, r0 = 330, w = 180, h = 120;
        var part = RegionField.Land4(tmp.Path, blocks, c0, r0, w, h);
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                Assert.Equal(whole[r0 + j, c0 + i], part[j, i]);
        Assert.Contains(true, part.Data);
        Assert.Contains(false, part.Data);
    }

    [Fact]
    public void AStyleWithMistakesNamesThemAll()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(ReadBundled("postalcodemap"))!.AsObject();
        json["background"] = "blue";
        json["sea"]!["bands"] = new System.Text.Json.Nodes.JsonArray(5, 2);
        json["regions"]!["sea"] = "ocean";
        var ex = Assert.Throws<StyleException>(() => MapStyle.Parse(json.ToJsonString(), "test.json"));
        Assert.Contains("'background': 'blue' is not a colour", ex.Message);
        Assert.Contains("sea.bands must increase", ex.Message);
        Assert.Contains("regions.sea 'ocean'", ex.Message);
        Assert.Contains("paint.sea: 3 colours", ex.Message);
    }

    static string ReadBundled(string id)
    {
        using var s = FxMapGenerator.Core.World.EmbeddedData.Open($"styles/{id}.json");
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void TheRegionFieldMixesTheRegionsOverTheLand()
    {
        // a 6 x 5 grid: zones AAA / BBB / CCC -> regions a / b / c, blur 6 m (1.5 cells)
        var zones = new Grid<ushort>(6, 5, [0, 1, 1, 0, 0, 2, 0, 1, 1, 0, 2, 2, 3, 3, 0, 0, 2, 2, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var land = new Grid<bool>(6, 5, new[] { 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }.Select(v => v == 1).ToArray());
        var style = new RegionsStyle(6.0, "b", [("a", Rgb.Parse("#102030")), ("b", Rgb.Parse("#c0d0e0")), ("c", Rgb.Parse("#ff0080"))],
            new Dictionary<string, string> { ["AAA"] = "a", ["BBB"] = "b", ["CCC"] = "c" });
        var f = RegionField.Compute(zones, ["", "AAA", "BBB", "CCC"], land, style);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 1, 2, 0, 0, 0, 1, 1, 2, 2, 2, 1, 1, 1, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 1, 1 }, f.Region.Data);
        Assert.Equal([101.05232238769531f, 94.25011444091797f, 95.57219696044922f, 121.58331298828125f, 160.633056640625f, 181.75942993164062f], f.Red.Data[..6]);
        Assert.Equal([5.841620922088623f, 15.516121864318848f, 44.254581451416016f, 100.52108764648438f, 161.8223114013672f, 193.4564666748047f], f.Green.Data[24..]);
        Assert.Equal([105.7457046508789f, 108.7401123046875f, 123.73079681396484f, 157.8838348388672f, 195.52976989746094f, 214.66888427734375f], f.Blue.Data[12..18]);
    }
}
