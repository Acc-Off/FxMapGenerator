using System.Text.Json.Nodes;
using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.Vectors;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Render;

/// <summary>A cell's drawing layer by layer: the layers put over each other are the drawing.</summary>
public sealed class LayerTests
{
    const int Px = CellPainter.BlockPx;

    /// <summary>
    /// Layers (straight RGBA, bottom first, null passed over) put over each other as a paint program does it: a layer
    /// over what is under it, the dark shading multiplied with it, the light shading colour-dodged, both clipped to
    /// what is under them (the ground). Without <paramref name="clip"/>, no layer is clipped: what a program does that
    /// knows the blend modes and no clipping (an SVG file's layers).
    /// </summary>
    public static byte[] Compose(IReadOnlyList<byte[]?> layers, int w, int h, bool clip = true)
    {
        var o = new double[w * h * 4];                          // premultiplied, 0..1
        for (int k = 0; k < layers.Count; k++)
        {
            if (layers[k] is not { } px) continue;
            var blend = MapLayers.Blend((MapLayer)k);
            bool clipped = clip && MapLayers.Clipped((MapLayer)k);
            for (int i = 0; i < px.Length; i += 4)
            {
                double a = px[i + 3] / 255.0;
                if (a == 0) continue;
                double under = o[i + 3];
                for (int c = 0; c < 3; c++)
                {
                    double s = px[i + c] / 255.0, b = under > 0 ? o[i + c] / under : 0;
                    double mixed = blend switch
                    {
                        LayerBlend.Multiply => b * s,
                        LayerBlend.ColorDodge => b == 0 ? 0 : s >= 1 ? 1 : Math.Min(1, b / (1 - s)),
                        _ => s,
                    };
                    // a clipped layer changes what is under it and adds nothing; any other layer: the layer where nothing is
                    // under it, the blended colour where something is, what is under it where the layer is clear
                    o[i + c] = clipped ? a * under * mixed + (1 - a) * o[i + c] : a * (1 - under) * s + a * under * mixed + (1 - a) * o[i + c];
                }
                if (!clipped) o[i + 3] = a + under * (1 - a);
            }
        }
        var rgba = new byte[o.Length];
        for (int i = 0; i < o.Length; i += 4)
        {
            double a = o[i + 3];
            if (a <= 0) continue;
            for (int c = 0; c < 3; c++) rgba[i + c] = (byte)Math.Clamp(Math.Round(255 * o[i + c] / a), 0, 255);
            rgba[i + 3] = (byte)Math.Clamp(Math.Round(255 * a), 0, 255);
        }
        return rgba;
    }

    /// <summary>The largest difference of a channel between two pictures, the colours weighted by their opacity (a clear pixel has no colour).</summary>
    internal static int Apart(byte[] a, byte[] b, Func<int, bool>? where = null)
    {
        int worst = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (where is not null && !where(i)) continue;
            worst = Math.Max(worst, Math.Abs(a[i + 3] - b[i + 3]));
            for (int c = 0; c < 3; c++)
                worst = Math.Max(worst, (int)Math.Ceiling(Math.Abs(a[i + c] * a[i + 3] - b[i + c] * b[i + 3]) / 255.0));
        }
        return worst;
    }

    internal static MapStyle With(string id, Action<JsonObject> change)
    {
        var src = MapStyle.Builtin(id).Source.DeepClone().AsObject();
        change(src);
        return MapStyle.Parse(src.ToJsonString(), "test");
    }

    internal static double[] Square(double x0, double y0, double x1, double y1) => [x0, y0, x1, y0, x1, y1, x0, y1];

    internal static CellLayerFileInfo OneBlock() =>
        new("cell_0_0", (WorldGrid.Left, WorldGrid.Top, WorldGrid.Left + WorldGrid.BlockSize, WorldGrid.Top - WorldGrid.BlockSize),
            WorldGrid.Left + 0.5, WorldGrid.Top - 0.5, 281, 281, [new BlockId(0, 0)]);

    internal static RoadShapesFile.Contents OneRoad() =>
        new([], [new RoadRibbon(0, Square(-4130, 8206, -3870, 8194), Square(-4130, 8205, -3870, 8195))], [], [], [], [], 3.0);

    internal static int At(double x, double y) => ((int)((WorldGrid.Top - y) * CellPainter.Ppm) * Px + (int)((x - WorldGrid.Left) * CellPainter.Ppm)) * 4;

    /// <summary>A light that falls from the west edge to the east edge, so the shading darkens one side and lightens the other.</summary>
    internal static ShadeLayer Slope(double strength)
    {
        var light = new Grid<byte>(281, 281);
        for (int r = 0; r < 281; r++)
            for (int c = 0; c < 281; c++) light.Data[r * 281 + c] = (byte)(40 + 200 * c / 280);
        return new ShadeLayer(light, 0.6, strength, CellPainter.Ppm, 0, 0);
    }

    [Fact]
    public void TheLayersOfAnOpaqueMapPutTogetherAreItsDrawing()
    {
        var st = MapStyle.Builtin("roadmap");
        var layers = new List<CellLayer>
        {
            new("ground", "grass", CellLayers.Sets.Ground(st), 10000, [new Ring(10000, Square(-4100, 8360, -4000, 8260))], null),
            new("water", "water", null, 400, [new Ring(400, Square(-4090.3, 8350.2, -4070.7, 8330.4))], null),
            new("building", "grey", CellLayers.Sets.Buildings(st), 100, [new Ring(100, Square(-3950, 8360, -3940, 8350))], null),
        };
        using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = layers, Style = st, Roads = OneRoad() });
        var drawn = painter.DrawBlock(0, 0);
        var parts = painter.DrawLayers(0, 0, Px, Px);
        Assert.Equal(MapLayers.All.Count, parts.Length);
        var have = MapLayers.All.Where(l => parts[(int)l] is not null).ToArray();
        Assert.Equal(new[] { MapLayer.Ground, MapLayer.Water, MapLayer.Buildings, MapLayer.Roads }, have);
        // the ground is whole under the opaque water; each layer holds its own colour only
        var ground = parts[(int)MapLayer.Ground]!;
        Assert.All(Enumerable.Range(0, ground.Length / 4), i => Assert.Equal(255, ground[i * 4 + 3]));
        Assert.Equal((st.Paint.Ground["grass"].R, 255), (ground[At(-4080, 8340)], ground[At(-4080, 8340) + 3]));
        var water = parts[(int)MapLayer.Water]!;
        Assert.Equal((st.Paint.Water.R, 255), (water[At(-4080, 8340)], water[At(-4080, 8340) + 3]));
        Assert.Equal(0, water[At(-4050, 8300) + 3]);
        Assert.Equal(255, parts[(int)MapLayer.Buildings]![At(-3945, 8355) + 3]);
        Assert.Equal(255, parts[(int)MapLayer.Roads]![At(-4050, 8200) + 3]);
        Assert.InRange(Apart(Compose(parts, Px, Px), drawn), 0, 1);

        // a piece of the block is that piece of the layers
        var piece = painter.DrawLayers(256, 512, 256, 256);
        Assert.Equal(FxMapGenerator.Core.Satellite.TileStore.Crop(parts[(int)MapLayer.Roads]!, Px, 256, 512, 256, 256), piece[(int)MapLayer.Roads]);
    }

    [Fact]
    public void TheShadingIsTwoLayersOfItsOwn()
    {
        var st = With("regional", s =>
        {
            var o = s["paint"]!["sea"]!["opacity"]!.AsArray();
            for (int i = 0; i < o.Count; i++) o[i] = 1;
        });
        Assert.False(st.SeeThroughWater);
        var layers = new List<CellLayer>
        {
            new("ground", "grass", CellLayers.Sets.Ground(st), 79000, [new Ring(79000, Square(-4140, 8400, -3858.75, 8118.75))], null),
            new("building", "grey", CellLayers.Sets.Buildings(st), 100, [new Ring(100, Square(-4085, 8345, -4080, 8340))], null),
        };
        var shade = Slope(st.Shade!.Strength);
        using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = layers, Style = st, Shade = shade, Roads = OneRoad() });
        var drawn = painter.DrawBlock(0, 0);
        var parts = painter.DrawLayers(0, 0, Px, Px);
        var dark = parts[(int)MapLayer.ShadeDark]!;
        var light = parts[(int)MapLayer.ShadeLight]!;
        // the west is darkened, the east lightened, each by its own layer; the ground holds its colour without either
        int west = At(-4120, 8250), east = At(-3880, 8250);
        Assert.Equal((255, 0), (dark[west + 3], light[west + 3]));
        Assert.Equal((0, 255), (dark[east + 3], light[east + 3]));
        // the dark layer's grey is the factor the drawing multiplies by
        float f = shade.Factor(70, 500);
        Assert.InRange(f, 0.1f, 0.9f);
        Assert.InRange(dark[(500 * Px + 70) * 4], 255 * f - 1, 255 * f + 1);
        var grass = st.Paint.Ground["grass"];
        var ground = parts[(int)MapLayer.Ground]!;
        Assert.Equal((grass.R, grass.G, grass.B, 255), (ground[west], ground[west + 1], ground[west + 2], ground[west + 3]));
        Assert.True(drawn[west] < grass.R && drawn[east] > grass.R);
        // the shading lies under what is drawn over the ground: the building and the road are not shaded
        Assert.Equal(st.Paint.Buildings["grey"].R, drawn[At(-4083, 8343)]);
        // the drawing drops the fraction of a shaded colour and the layers hold the factor in 8 bits: three steps apart at most
        Assert.InRange(Apart(Compose(parts, Px, Px), drawn), 0, 3);
    }

    [Fact]
    public void SeeThroughWaterCutsTheGroundUnderIt()
    {
        // band 0 half see-through, band 1 clear, band 2 opaque, the water where no band is 0.25
        var st = With("regional", s =>
        {
            var o = s["paint"]!["sea"]!["opacity"]!.AsArray();
            o[0] = 0.5;
            o[1] = 0;
            o[2] = 1;
            s["paint"]!["waterOpacity"] = 0.25;
        });
        string sea = CellLayers.Sets.Sea(st);
        var layers = new List<CellLayer>
        {
            new("ground", "grass", CellLayers.Sets.Ground(st), 79000, [new Ring(79000, Square(-4140, 8400, -3858.75, 8118.75))], null),
            new("water", "water", null, 40000, [new Ring(40000, Square(-4100.3, 8360.2, -3900.7, 8160.4))], null),
            new("sea", "sand", sea, 400, [new Ring(400, Square(-4090.3, 8350.2, -4070.7, 8330.4))], null) { Band = 0 },
            new("sea", "sand", sea, 400, [new Ring(400, Square(-4060.3, 8350.2, -4040.7, 8330.4))], null) { Band = 1 },
            new("sea", "sand", sea, 400, [new Ring(400, Square(-4030.3, 8350.2, -4010.7, 8330.4))], null) { Band = 2 },
            new("building", "grey", CellLayers.Sets.Buildings(st), 100, [new Ring(100, Square(-4085, 8345, -4080, 8340))], null),
        };
        using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = layers, Style = st, Shade = Slope(st.Shade!.Strength), Roads = OneRoad() });
        var drawn = painter.DrawBlock(0, 0);
        var parts = painter.DrawLayers(0, 0, Px, Px);
        var ground = parts[(int)MapLayer.Ground]!;
        var water = parts[(int)MapLayer.Water]!;
        Assert.Equal(255, ground[At(-4130, 8390) + 3]);                         // land
        Assert.Equal((0, 64), (ground[At(-4000, 8300) + 3], water[At(-4000, 8300) + 3]));     // the water where no band is: the ground gone, the water a quarter
        Assert.Equal((0, 128), (ground[At(-4075, 8335) + 3], water[At(-4075, 8335) + 3]));    // band 0
        Assert.Equal((0, 0), (ground[At(-4050, 8340) + 3], water[At(-4050, 8340) + 3]));      // band 1: clear, and nothing under it
        Assert.Equal((255, 255), (ground[At(-4020, 8340) + 3], water[At(-4020, 8340) + 3]));  // band 2: opaque, the ground whole under it
        // the shading holds its factor where the ground is cut too: clipped to the ground, it changes nothing there
        Assert.Equal(255, parts[(int)MapLayer.ShadeDark]![At(-4075, 8335) + 3]);
        Assert.True(MapLayers.Clipped(MapLayer.ShadeDark) && MapLayers.Clipped(MapLayer.ShadeLight) && !MapLayers.Clipped(MapLayer.Water));
        // put together, the layers are the drawing, the pixels the ground covers only partly along the water's edges too
        var together = Compose(parts, Px, Px);
        Assert.InRange(Apart(together, drawn), 0, 3);
        int partly = Enumerable.Range(0, ground.Length / 4).Count(i => ground[i * 4 + 3] is > 0 and < 255);
        Assert.InRange(partly, 1, 4 * 4 * Px);
    }

    [Fact]
    public void LabelsAndPointsGoToTheirOwnLayers()
    {
        var st = MapStyle.Builtin("postalcodemap");
        var labels = new List<PlacedLabel>
        {
            new("postal", "1234", -4100, 8350, 0, 24, "Bahnschrift", "bold", 0),
            new("zone", "DOWNTOWN", -4000, 8300, 0, 30, "Bahnschrift", "normal", 4),
            new("street", "Main St", -4000, 8220, 10, 14, "Bahnschrift", "normal", 0,
                [new("M", -4010, 8220, 10, 9), new("a", -4000, 8221.7, 10, 8), new("i", -3992, 8223.1, 10, 4), new("n", -3986, 8224.2, 10, 8)]),
        };
        var dot = new PoiStyle("dot", ItemName.Of("Dot"), "dot", new Rgb(0xd0, 0x20, 0x20), 40, "bold", new Rgb(255, 255, 255), 2, null, false, null);
        var poi = new ResolvedPoi(new PoiPoint("1", "g", "", new Dictionary<string, string> { ["en"] = "A" }, -3950, 8350, null, null, null, null, null, null),
            dot, PoiShow.Default, dot.Color, dot.Size, true, false);
        var ground = new List<CellLayer>
        {
            new("ground", "grass", CellLayers.Sets.Ground(st), 79000, [new Ring(79000, Square(-4140, 8400, -3858.75, 8118.75))], null),
        };
        using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = ground, Style = st, Labels = labels, Pois = [poi], Language = "en" });
        var drawn = painter.DrawBlock(0, 0);
        var parts = painter.DrawLayers(0, 0, Px, Px);
        foreach (var l in new[] { MapLayer.Postal, MapLayer.Poi, MapLayer.Zones, MapLayer.Streets }) Assert.NotNull(parts[(int)l]);
        Assert.Null(parts[(int)MapLayer.Roads]);
        Assert.Equal(255, parts[(int)MapLayer.Poi]![At(-3950, 8350) + 3]);       // the dot
        Assert.Equal(0, parts[(int)MapLayer.Postal]![At(-3950, 8350) + 3]);
        Assert.InRange(Apart(Compose(parts, Px, Px), drawn), 0, 2);
    }

    [Fact]
    public void EveryLayerHasItsNamesAndEveryStepItsLayer()
    {
        Assert.Equal(13, MapLayers.All.Count);
        Assert.Equal(MapLayers.All.Count, MapLayers.All.Select(l => MapLayers.Name(l)).Distinct().Count());
        Assert.Equal(MapLayers.All.Count, MapLayers.All.Select(l => MapLayers.Name(l, "ja")).Distinct().Count());
        Assert.All(MapLayers.All, l => Assert.Equal(MapLayers.Name(l), MapLayers.Name(l, "en")));
        Assert.All(MapLayers.All, l => Assert.DoesNotContain(MapLayers.Name(l), c => c > 127));
        Assert.Equal(("地面", "陰影（暗い側）", "通り名"), (MapLayers.Name(MapLayer.Ground, "ja"), MapLayers.Name(MapLayer.ShadeDark, "ja"), MapLayers.Name(MapLayer.Streets, "ja")));
        Assert.Equal((LayerBlend.Normal, LayerBlend.Multiply, LayerBlend.ColorDodge),
            (MapLayers.Blend(MapLayer.Ground), MapLayers.Blend(MapLayer.ShadeDark), MapLayers.Blend(MapLayer.ShadeLight)));
        foreach (var kind in new[] { "background", "ground", "groundLayer", "canopy", "water", "sea", "building", "contour", "rail", "tunnel", "track", "casing", "road", "postal", "poi", "zone", "street" })
            Assert.NotNull(MapLayers.Of(new StepTag(kind)));
        Assert.Null(MapLayers.Of(new StepTag("shade")));
    }
}
