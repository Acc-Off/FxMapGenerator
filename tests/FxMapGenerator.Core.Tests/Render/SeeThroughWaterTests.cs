using System.Text.Json.Nodes;
using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.Vectors;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Render;

/// <summary>Water drawn see-through: the styles' opacities, the drawing's alpha, the open sea, the small whole map.</summary>
public sealed class SeeThroughWaterTests
{
    static MapStyle With(string id, Action<JsonObject> change)
    {
        var src = MapStyle.Builtin(id).Source.DeepClone().AsObject();
        change(src);
        return MapStyle.Parse(src.ToJsonString(), "test");
    }

    [Fact]
    public void TheStylesSayHowOpaqueTheirWaterIs()
    {
        // the sea of the two PostalCodeMap-like styles follows PostalCodeMap's picture: 16 bands down to 200 m, each a
        // little clearer, the sea deeper than that clear
        double[] measured = [1, 0.99, 0.99, 0.99, 0.99, 0.99, 0.98, 0.96, 0.92, 0.9, 0.86, 0.82, 0.78, 0.75, 0.69, 0.65, 0];
        foreach (var id in new[] { "postalcodemap", "regional" })
        {
            var st = MapStyle.Builtin(id);
            Assert.Equal(200, st.Sea.Bands[^1]);
            Assert.Equal(measured, st.Paint.SeaOpacity);
            Assert.Equal(1, st.Paint.WaterOpacity);
            Assert.True(st.SeeThroughWater);
            Assert.Equal(0, st.OpenSeaAlpha);
            Assert.Equal("#739ebd 0", st.OpenSeaText);
        }
        var road = MapStyle.Builtin("roadmap");
        Assert.All(road.Paint.SeaOpacity, o => Assert.Equal(1, o));
        Assert.False(road.SeeThroughWater);
        Assert.Equal(road.OpenSea.ToString(), road.OpenSeaText);                // the records of opaque styles stay as they were
        // none given: opaque
        var bare = With("regional", s => { s["paint"]!.AsObject().Remove("waterOpacity"); s["paint"]!["sea"]!.AsObject().Remove("opacity"); });
        Assert.All(bare.Paint.SeaOpacity, o => Assert.Equal(1, o));
        Assert.Equal((1.0, false), (bare.Paint.WaterOpacity, bare.SeeThroughWater));
        // a list that does not fit the bands, a value off 0 to 1
        var wrong = Assert.Throws<StyleException>(() => With("regional", s => s["paint"]!["sea"]!["opacity"] = new JsonArray(1, 1)));
        Assert.Contains("paint.sea.opacity: 17 opacities needed", wrong.Message, StringComparison.Ordinal);
        Assert.Throws<StyleException>(() => With("regional", s => s["paint"]!["waterOpacity"] = 1.5));
    }

    static double[] Square(double x0, double y0, double x1, double y1) => [x0, y0, x1, y0, x1, y1, x0, y1];

    static CellLayerFileInfo OneBlock() =>
        new("cell_0_0", (WorldGrid.Left, WorldGrid.Top, WorldGrid.Left + WorldGrid.BlockSize, WorldGrid.Top - WorldGrid.BlockSize),
            WorldGrid.Left + 0.5, WorldGrid.Top - 0.5, 281, 281, [new BlockId(0, 0)]);

    static (byte R, byte G, byte B, byte A) At(byte[] rgba, double x, double y)
    {
        int px = (int)((x - WorldGrid.Left) * CellPainter.Ppm), py = (int)((WorldGrid.Top - y) * CellPainter.Ppm);
        int i = (py * CellPainter.BlockPx + px) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    [Fact]
    public void SeeThroughWaterKeepsItsOpacityUnderWhatIsDrawnOverIt()
    {
        // band 0 half see-through, band 1 clear, the water where no band is 0.25
        var st = With("regional", s =>
        {
            var o = s["paint"]!["sea"]!["opacity"]!.AsArray();
            o[0] = 0.5;
            o[1] = 0;
            s["paint"]!["waterOpacity"] = 0.25;
        });
        string sea = CellLayers.Sets.Sea(st);
        var layers = new List<CellLayer>
        {
            new("ground", "grass", CellLayers.Sets.Ground(st), 10000, [new Ring(10000, Square(-4140, 8400, -3858.75, 8118.75))], null),
            new("water", "water", null, 40000, [new Ring(40000, Square(-4100, 8360, -3900, 8160))], null),
            new("sea", "sand", sea, 400, [new Ring(400, Square(-4090, 8350, -4070, 8330))], null) { Band = 0 },
            new("sea", "sand", sea, 400, [new Ring(400, Square(-4060, 8350, -4040, 8330))], null) { Band = 1 },
            new("building", "grey", CellLayers.Sets.Buildings(st), 100, [new Ring(100, Square(-4085, 8345, -4080, 8340))], null),
        };
        var road = new RoadShapesFile.Contents([], [new RoadRibbon(0, Square(-4130, 8206, -3870, 8194), Square(-4130, 8205, -3870, 8195))], [], [], [], [], 3.0);
        using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = layers, Style = st, Roads = road });
        var px = painter.DrawBlock(0, 0);
        var band0 = st.Paint.Sea.Sand[0];
        var (r, g, b, a) = At(px, -4075, 8335);
        Assert.Equal(128, a);                                                   // half see-through, in the band's colour
        Assert.InRange(r, band0.R - 1, band0.R + 1);
        Assert.InRange(g, band0.G - 1, band0.G + 1);
        Assert.InRange(b, band0.B - 1, band0.B + 1);
        Assert.Equal(0, At(px, -4050, 8340).A);                                 // clear
        Assert.Equal(64, At(px, -4000, 8300).A);                                // the water where no band is painted
        Assert.Equal(255, At(px, -4130, 8390).A);                               // the ground
        Assert.Equal((st.Paint.Buildings["grey"].R, 255), (At(px, -4083, 8343).R, At(px, -4083, 8343).A));   // a building over the water
        Assert.Equal(255, At(px, -4000, 8200).A);                               // the road across the water

        // the same drawing with every opacity at 1 is the opaque drawing it always was
        var opaque = With("regional", s =>
        {
            var o = s["paint"]!["sea"]!["opacity"]!.AsArray();
            for (int i = 0; i < o.Count; i++) o[i] = 1;
        });
        using var before = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = layers, Style = opaque, Roads = road });
        var px1 = before.DrawBlock(0, 0);
        Assert.All(Enumerable.Range(0, px1.Length / 4), i => Assert.Equal(255, px1[i * 4 + 3]));
    }

    [Fact]
    public void TheSmallWholeMapIsTheStandardFrameSqueezedOverTheOpenSea()
    {
        using var tmp = new TempFolder();
        var tiles = new TileStore(Path.Combine(tmp.Path, "tiles"));
        // the zoom 2 tiles of the standard frame: 2 x 3; the top-left one red, the one below it clear
        var red = new byte[256 * 256 * 4];
        for (int i = 0; i < red.Length; i += 4) (red[i], red[i + 1], red[i + 2], red[i + 3]) = (220, 20, 30, 255);
        var clear = new byte[256 * 256 * 4];
        tiles.Write(MinimapLod.Zoom, 0, 0, red);
        tiles.Write(MinimapLod.Zoom, 0, 1, clear);
        var lod = MinimapLod.Compose(tiles, new Rgb(10, 60, 120));
        Assert.Equal(128 * 128 * 4, lod.Length);
        (byte, byte, byte, byte) Px(int x, int y) { int i = (y * 128 + x) * 4; return (lod[i], lod[i + 1], lod[i + 2], lod[i + 3]); }
        Assert.Equal((220, 20, 30, 255), Px(10, 10));                          // the red tile: the left half, the top third
        Assert.Equal((10, 60, 120, 255), Px(10, 64));                          // the clear one: the open sea under it
        Assert.Equal((10, 60, 120, 255), Px(100, 120));                        // no tile: the open sea
        Assert.Equal(6, MinimapLod.Tiles.Count());
    }
}
