using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.Vectors;
using static FxMapGenerator.Core.Tests.Render.LayerTests;

namespace FxMapGenerator.Core.Tests.Export;

/// <summary>A block's layers at zoom 7 and 6: put over each other they are the block's tile of that zoom.</summary>
public sealed class LayeredBlockTests
{
    const int Px = CellPainter.BlockPx;

    /// <summary>
    /// A block with everything the layers hold: shaded ground, water that is half see-through, clear and opaque, a
    /// building, a road, labels and a point of interest.
    /// </summary>
    static CellPainter Scene(out MapStyle st)
    {
        st = With("postalcodemap", s =>
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
            new("water", "water", null, 26000, [new Ring(26000, Square(-4100.3, 8360.2, -3900.7, 8230.4))], null),
            new("sea", "sand", sea, 400, [new Ring(400, Square(-4090.3, 8350.2, -4070.7, 8330.4))], null) { Band = 0 },
            new("sea", "sand", sea, 400, [new Ring(400, Square(-4060.3, 8350.2, -4040.7, 8330.4))], null) { Band = 1 },
            new("sea", "sand", sea, 400, [new Ring(400, Square(-4030.3, 8350.2, -4010.7, 8330.4))], null) { Band = 2 },
            new("building", "grey", CellLayers.Sets.Buildings(st), 400, [new Ring(400, Square(-3895, 8395, -3875, 8375))], null),
        };
        var labels = new List<PlacedLabel>
        {
            new("postal", "1234", -4120, 8140, 0, 24, "Bahnschrift", "bold", 0),
            new("zone", "DOWNTOWN", -4000, 8385, 0, 30, "Bahnschrift", "normal", 4),
            new("street", "Main St", -4000, 8200, 0, 14, "Bahnschrift", "normal", 0,
                [new("M", -4010, 8200, 0, 9), new("a", -4000, 8200, 0, 8), new("i", -3992, 8200, 0, 4), new("n", -3986, 8200, 0, 8)]),
        };
        var dot = new PoiStyle("dot", ItemName.Of("Dot"), "dot", new Rgb(0xd0, 0x20, 0x20), 40, "bold", new Rgb(255, 255, 255), 2, null, false, null);
        var poi = new ResolvedPoi(new PoiPoint("1", "g", "", new Dictionary<string, string> { ["en"] = "A" }, -3880, 8140, null, null, null, null, null, null),
            dot, PoiShow.Default, dot.Color, dot.Size, true, false);
        return new CellPainter(new CellDrawInput
        {
            Info = OneBlock(), Layers = layers, Style = st, Shade = Slope(st.Shade!.Strength), Roads = OneRoad(), Labels = labels, Pois = [poi], Language = "en",
        });
    }

    static byte[]?[] Alone(byte[]?[] parts, int zoom) => parts.Select(p => p is null ? null : EditableLayersStage.Shrink(p, zoom)).ToArray();

    /// <summary>The pixels of two pictures that are more than <paramref name="steps"/> apart (as <see cref="Render.LayerTests.Apart"/> counts).</summary>
    static int Further(byte[] a, byte[] b, int steps)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int d = Math.Abs(a[i + 3] - b[i + 3]);
            for (int c = 0; c < 3; c++) d = Math.Max(d, (int)Math.Ceiling(Math.Abs(a[i + c] * a[i + 3] - b[i + c] * b[i + 3]) / 255.0));
            if (d > steps) n++;
        }
        return n;
    }

    static IEnumerable<MapLayer> Plain => MapLayers.All.Where(l => l != MapLayer.Ground && MapLayers.Blend(l) == LayerBlend.Normal);

    [Theory]
    [InlineData(7)]
    [InlineData(6)]
    public void TheSmallLayersPutTogetherAreTheBlocksTile(int zoom)
    {
        using var painter = Scene(out _);
        int side = EditableChoice.BlockPx(zoom);
        var tile = EditableLayersStage.Shrink(painter.DrawBlock(0, 0), zoom)!;
        var parts = painter.DrawLayers(0, 0, Px, Px, out var shaded);
        Assert.NotNull(shaded);
        var small = LayeredBlock.Shrink(parts, shaded, zoom);
        foreach (var l in MapLayers.All) Assert.Equal(parts[(int)l] is null, small[(int)l] is null);
        Assert.All(small, p => Assert.True(p is null || p.Length == side * side * 4));
        // to the slack, the rounding of the layers and what the layers at zoom 8 are apart from the drawing, but for
        // the few pixels beside shapes that would take more opacity than the limit
        int slack = (int)LayeredBlock.Slack.Default.Beside;
        var together = Compose(small, side, side);
        Assert.InRange(Further(together, tile, slack + 3), 0, side * side / 1000);
        Assert.InRange(Apart(together, tile), 0, 24);
        // without the limit, every pixel
        var whole = LayeredBlock.Shrink(parts, shaded, zoom, LayeredBlock.Slack.Default with { BesideOpacity = 1 });
        Assert.InRange(Apart(Compose(whole, side, side), tile), 0, slack + 3);
        // every layer shrunk alone is not that: the water's edge over the ground it cuts, the contrast along the shapes
        Assert.True(Apart(Compose(Alone(parts, zoom), side, side), tile) > 4 * slack);
        // with no slack but the rounding, the pictures are as close as the layers at zoom 8 are to the drawing
        var exact = LayeredBlock.Shrink(parts, shaded, zoom, new LayeredBlock.Slack(0.5, 0.5));
        Assert.InRange(Apart(Compose(exact, side, side), tile), 0, 4);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(6)]
    public void ALayerKeepsItsShapes(int zoom)
    {
        using var painter = Scene(out var st);
        int side = EditableChoice.BlockPx(zoom), f = Px / side;
        var parts = painter.DrawLayers(0, 0, Px, Px, out var shaded);
        var small = LayeredBlock.Shrink(parts, shaded, zoom);
        var alone = Alone(parts, zoom);
        int Small(int at) => (at / 4 / Px / f * side + at / 4 % Px / f) * 4;
        // inside a shape a layer holds its paint: the building, the road, the opaque band of the sea
        foreach (var (layer, at) in new[] { (MapLayer.Buildings, At(-3885, 8385)), (MapLayer.Roads, At(-4000, 8200.5)), (MapLayer.Water, At(-4020, 8340)) })
        {
            int i = Small(at);
            Assert.Equal(255, small[(int)layer]![i + 3]);
            for (int c = 0; c < 3; c++) Assert.InRange(small[(int)layer]![i + c], parts[(int)layer]![at + c] - 3, parts[(int)layer]![at + c] + 3);
        }
        var grey = st.Paint.Buildings["grey"];
        int b = Small(At(-3885, 8385));
        Assert.InRange(small[(int)MapLayer.Buildings]![b], grey.R - 3, grey.R + 3);
        // where it is solid alone it stays solid, and far from its shapes it has nothing
        foreach (var layer in Plain)
        {
            if (small[(int)layer] is not { } made) continue;
            var own = alone[(int)layer] ?? new byte[made.Length];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int i = (y * side + x) * 4;
                    if (own[i + 3] == 255) Assert.True(made[i + 3] == 255, $"{layer} at ({x}, {y})");
                    if (made[i + 3] == 0) continue;
                    bool near = false;
                    for (int j = Math.Max(0, y - 6); j <= Math.Min(side - 1, y + 6) && !near; j++)
                        for (int k = Math.Max(0, x - 6); k <= Math.Min(side - 1, x + 6) && !near; k++) near = own[(j * side + k) * 4 + 3] != 0;
                    Assert.True(near, $"{layer} at ({x}, {y})");
                }
        }
    }

    [Fact]
    public void TheSlackBesideTheShapesSaysWhereALayerGetsPixelsOfItsOwn()
    {
        using var painter = Scene(out _);
        const int zoom = 6;
        int side = EditableChoice.BlockPx(zoom);
        var tile = EditableLayersStage.Shrink(painter.DrawBlock(0, 0), zoom)!;
        var parts = painter.DrawLayers(0, 0, Px, Px, out var shaded);
        var alone = Alone(parts, zoom);
        int Beside(byte[]?[] small) => Plain.Sum(l => small[(int)l] is not { } made ? 0
            : Enumerable.Range(0, side * side).Count(i => made[i * 4 + 3] != 0 && (alone[(int)l] is not { } own || own[i * 4 + 3] == 0)));
        // with no slack every difference beside a shape gets a pixel; with all the slack none does, and the picture is
        // off there by what the shrinking adds along the edges
        var exact = LayeredBlock.Shrink(parts, shaded, zoom, new LayeredBlock.Slack(0.5, 0.5));
        var never = LayeredBlock.Shrink(parts, shaded, zoom, new LayeredBlock.Slack(3, 255));
        Assert.True(Beside(exact) > 0);
        Assert.Equal(0, Beside(never));
        Assert.True(Apart(Compose(never, side, side), tile) > Apart(Compose(exact, side, side), tile));
        var small = LayeredBlock.Shrink(parts, shaded, zoom);
        Assert.InRange(Beside(small), 1, Beside(exact));
        // and such a pixel is a quarter opaque at most (over what the layer has there alone, next to nothing)
        foreach (var l in Plain)
        {
            if (small[(int)l] is not { } made) continue;
            var own = alone[(int)l] ?? new byte[made.Length];
            Assert.All(Enumerable.Range(0, side * side), i => Assert.True(own[i * 4 + 3] > 8 || made[i * 4 + 3] <= own[i * 4 + 3] + 64));
        }
    }

    [Fact]
    public void TheSmallShadingIsOneGreyWhereOneFactorDoes()
    {
        using var painter = Scene(out _);
        const int zoom = 6;
        int side = EditableChoice.BlockPx(zoom);
        var parts = painter.DrawLayers(0, 0, Px, Px, out var shaded);
        var small = LayeredBlock.Shrink(parts, shaded, zoom);
        var dark = small[(int)MapLayer.ShadeDark]!;
        var light = small[(int)MapLayer.ShadeLight]!;
        // plain ground in the west (darkened) and in the east (lightened): the grey of the layer at zoom 8 there
        int west = (125 * side + 3) * 4, east = (125 * side + 252) * 4;
        Assert.Equal((255, 0), (dark[west + 3], light[west + 3]));
        Assert.Equal((0, 255), (dark[east + 3], light[east + 3]));
        Assert.True(dark[west] == dark[west + 1] && dark[west] == dark[west + 2]);
        Assert.True(light[east] == light[east + 1] && light[east] == light[east + 2]);
        Assert.InRange(dark[west], parts[(int)MapLayer.ShadeDark]![(502 * Px + 14) * 4] - 3, parts[(int)MapLayer.ShadeDark]![(502 * Px + 14) * 4] + 3);
        Assert.InRange(light[east], parts[(int)MapLayer.ShadeLight]![(502 * Px + 1010) * 4] - 3, parts[(int)MapLayer.ShadeLight]![(502 * Px + 1010) * 4] + 3);
        // few pixels need a factor per colour
        int coloured = Enumerable.Range(0, side * side).Count(i => dark[i * 4 + 3] != 0 && (dark[i * 4] != dark[i * 4 + 1] || dark[i * 4] != dark[i * 4 + 2]));
        Assert.InRange(coloured, 0, side * side / 20);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(6)]
    public void AMapWithoutShadingOrSeeThroughWaterDoesTheSame(int zoom)
    {
        var st = MapStyle.Builtin("roadmap");
        var layers = new List<CellLayer>
        {
            new("ground", "grass", CellLayers.Sets.Ground(st), 10000, [new Ring(10000, Square(-4100, 8360, -4000, 8260))], null),
            new("water", "water", null, 400, [new Ring(400, Square(-4090.3, 8350.2, -4070.7, 8330.4))], null),
            new("building", "grey", CellLayers.Sets.Buildings(st), 100, [new Ring(100, Square(-3950, 8360, -3940, 8350))], null),
        };
        using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = layers, Style = st, Roads = OneRoad() });
        int side = EditableChoice.BlockPx(zoom);
        var tile = EditableLayersStage.Shrink(painter.DrawBlock(0, 0), zoom)!;
        var parts = painter.DrawLayers(0, 0, Px, Px, out var shaded);
        Assert.Null(shaded);
        var small = LayeredBlock.Shrink(parts, shaded, zoom);
        Assert.Equal(new[] { MapLayer.Ground, MapLayer.Water, MapLayer.Buildings, MapLayer.Roads }, MapLayers.All.Where(l => small[(int)l] is not null));
        Assert.All(Enumerable.Range(0, side * side), i => Assert.Equal(255, small[(int)MapLayer.Ground]![i * 4 + 3]));
        Assert.InRange(Apart(Compose(small, side, side), tile), 0, (int)LayeredBlock.Slack.Default.Beside + 2);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(6)]
    public void ThePicturesOfAnSvgFileNeedNoClipping(int zoom)
    {
        using var painter = Scene(out _);
        int side = EditableChoice.BlockPx(zoom);
        var parts = painter.DrawLayers(0, 0, Px, Px, out var shaded);
        // only the layers that are pictures in every format: the ones after them are not made, the others are as ever
        var small = LayeredBlock.Shrink(parts, shaded, zoom, last: MapLayers.LastPicture);
        var all = LayeredBlock.Shrink(parts, shaded, zoom);
        foreach (var l in MapLayers.All)
        {
            if (l <= MapLayers.LastPicture) Assert.Equal(all[(int)l], small[(int)l]);
            else Assert.Null(small[(int)l]);
        }
        Assert.NotNull(all[(int)MapLayer.Roads]);
        Assert.Equal(MapLayer.Rail, MapLayers.LastPicture);

        // the scene's water is see-through: the ground is cut, and some pixels it covers only in part
        var ground = small[(int)MapLayer.Ground]!;
        Assert.Contains(Enumerable.Range(0, side * side), i => ground[i * 4 + 3] is > 0 and < 255);
        var clipped = Compose(small, side, side);
        // put together by a program that knows no clipping, the shading makes those pixels opaque
        Assert.True(Apart(Compose(small, side, side, clip: false), clipped) > 30);
        // the pictures an SVG file gets: the shading only where the ground is whole, and in the ground's colour elsewhere
        var pictures = small.Select(p => (byte[]?)p?.Clone()).ToArray();
        byte[]? dark = pictures[(int)MapLayer.ShadeDark], light = pictures[(int)MapLayer.ShadeLight];
        Assert.NotNull(dark);
        Assert.NotNull(light);
        EditableFilesStage.ShadeInGround(pictures[(int)MapLayer.Ground], dark, light);
        EditableFilesStage.KeepToWholeGround(dark, ground);
        EditableFilesStage.KeepToWholeGround(light, ground);
        Assert.InRange(Apart(Compose(pictures, side, side, clip: false), clipped), 0, 1);
        // the ground keeps its opacity, and a pixel it covers whole its colour; the shading left is the shading there
        var shadedGround = pictures[(int)MapLayer.Ground]!;
        for (int i = 0; i < ground.Length; i += 4)
        {
            Assert.Equal(ground[i + 3], shadedGround[i + 3]);
            if (ground[i + 3] is 255 or 0) Assert.Equal((ground[i], ground[i + 1], ground[i + 2]), (shadedGround[i], shadedGround[i + 1], shadedGround[i + 2]));
            if (ground[i + 3] == 255) Assert.Equal(small[(int)MapLayer.ShadeDark]![i + 3], dark![i + 3]);
            else Assert.Equal((0, 0), (dark![i + 3], light![i + 3]));
        }
        // no shading layers: nothing changes
        var alone = (byte[])ground.Clone();
        EditableFilesStage.ShadeInGround(alone, null, null);
        Assert.Equal(ground, alone);
    }
}
