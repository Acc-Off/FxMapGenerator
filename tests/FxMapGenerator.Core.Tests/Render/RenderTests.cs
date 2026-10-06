using System.Text.Json;
using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.Vectors;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Render;

public sealed class RenderTests
{
    [Fact]
    public void TheShadingIsEnlargedAndAppliedAsSciPyAndNumpyDid()
    {
        var light = new Grid<byte>(4, 3, [10, 200, 30, 90, 250, 0, 128, 64, 5, 17, 99, 255]);
        var shade = new ShadeLayer(light, 0.6, 0.8, CellPainter.Ppm, 0, 0);
        // the enlarged grid is 11 x 15 (scipy.ndimage.zoom, order 1); two of its pixels on a grey of 200:
        // f = 1 + 0.8 (0.39887956 - 0.6) / 0.6 = 0.73183942 -> 146, and 0.25098041 -> 0.53464055 -> 106
        var rgba = new byte[16 * 12 * 4];
        Array.Fill(rgba, (byte)200);
        shade.Apply(rgba, 16 * 4, 16, 12, 0, 0);
        Assert.Equal(146, rgba[(1 * 16 + 2) * 4]);
        Assert.Equal(106, rgba[(5 * 16 + 7) * 4 + 1]);
        Assert.Equal(200, rgba[(5 * 16 + 7) * 4 + 3]);                          // the alpha is left alone
        Assert.Equal(200, rgba[(11 * 16 + 15) * 4]);                            // outside the enlarged grid: flat, no change
    }

    /// <summary>A cell of one block at the map's north-west corner.</summary>
    static CellLayerFileInfo OneBlock() =>
        new("cell_0_0", (WorldGrid.Left, WorldGrid.Top, WorldGrid.Left + WorldGrid.BlockSize, WorldGrid.Top - WorldGrid.BlockSize),
            WorldGrid.Left + 0.5, WorldGrid.Top - 0.5, 281, 281, [new BlockId(0, 0)]);

    static double[] Square(double x0, double y0, double x1, double y1) => [x0, y0, x1, y0, x1, y1, x0, y1];

    static RoadShapesFile.Contents OneRoad() =>
        new([], [new RoadRibbon(0, Square(-4130, 8206, -3870, 8194), Square(-4130, 8205, -3870, 8195))], [], [], [], [], 3.0);

    static (byte R, byte G, byte B) At(byte[] rgba, double x, double y)
    {
        int px = (int)((x - WorldGrid.Left) * CellPainter.Ppm), py = (int)((WorldGrid.Top - y) * CellPainter.Ppm);
        int i = (py * CellPainter.BlockPx + px) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    static (byte, byte, byte) C(Rgb c) => (c.R, c.G, c.B);

    [Fact]
    public void ABlockIsDrawnLayerByLayerThenTheRoads()
    {
        var st = MapStyle.Builtin("roadmap");
        var layers = new List<CellLayer>
        {
            new("ground", "grass", CellLayers.Sets.Ground(st), 10000, [new Ring(10000, Square(-4100, 8360, -4000, 8260))], null),
            new("water", "water", null, 400, [new Ring(400, Square(-4090, 8350, -4070, 8330))], null),
            new("building", "grey", CellLayers.Sets.Buildings(st), 100, [new Ring(100, Square(-3950, 8360, -3940, 8350))], null),
            new("building", "grey", "buildings:another", 100, [new Ring(100, Square(-3930, 8360, -3920, 8350))], null),   // another style's
        };
        using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = layers, Style = st, Roads = OneRoad() });
        Assert.Equal((1, 1), (painter.BlocksX, painter.BlocksY));
        var px = painter.DrawBlock(0, 0);
        Assert.Equal(CellPainter.BlockPx * CellPainter.BlockPx * 4, px.Length);
        Assert.Equal(C(st.Background), At(px, -4130, 8390));
        Assert.Equal(C(st.Paint.Ground["grass"]), At(px, -4050, 8300));
        Assert.Equal(C(st.Paint.Water), At(px, -4080, 8340));                    // over the ground
        Assert.Equal(C(st.Paint.Buildings["grey"]), At(px, -3945, 8355));
        Assert.Equal(C(st.Background), At(px, -3925, 8355));                     // a layer of another style's values is not drawn
        Assert.Equal(C(st.Paint.Road.Fill), At(px, -4050, 8200));                // the road over everything
        Assert.All(Enumerable.Range(0, px.Length / 4), i => Assert.Equal(255, px[i * 4 + 3]));
    }

    static List<CellLayer> SomeLayers(MapStyle st) =>
    [
        new("ground", "grass", CellLayers.Sets.Ground(st), 10000, [new Ring(10000, Square(-4100, 8360, -4000, 8260))], null),
        new("water", "water", null, 400, [new Ring(400, Square(-4090, 8350, -4070, 8330))], null),
        new("building", "grey", CellLayers.Sets.Buildings(st), 100, [new Ring(100, Square(-3950, 8360, -3940, 8350))], null),
    ];

    [Fact]
    public void PiecesDrawnOnManyThreadsAreTheBlockDrawnAtOnce()
    {
        var st = MapStyle.Builtin("roadmap");
        using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = SomeLayers(st), Style = st, Roads = OneRoad() });
        var whole = painter.DrawBlock(0, 0);
        var pieces = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, k => pieces[k] = painter.DrawArea(k % 4 * 256, k / 4 * 256, 256, 256));
        for (int k = 0; k < 16; k++)
            Assert.Equal(FxMapGenerator.Core.Satellite.TileStore.Crop(whole, CellPainter.BlockPx, k % 4 * 256, k / 4 * 256, 256, 256), pieces[k]);
    }

    [Fact]
    public void APixelTellsWhatPaintsIt()
    {
        var st = MapStyle.Builtin("roadmap");
        using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = SomeLayers(st), Style = st, Roads = OneRoad() });
        (Rgb, string) Pick(double x, double y)
        {
            var (colour, steps) = painter.Pick((int)((x - WorldGrid.Left) * CellPainter.Ppm), (int)((WorldGrid.Top - y) * CellPainter.Ppm));
            Assert.All(steps, s => Assert.InRange(s.Cover, 0.001, 1));
            return (colour, string.Join(" ", steps.Select(s => s.Tag.Kind + (s.Tag.Class >= 0 ? s.Tag.Class.ToString() : ""))));
        }
        Assert.Equal((st.Background, "background"), Pick(-4130, 8390));
        Assert.Equal((st.Paint.Ground["grass"], "background groundLayer"), Pick(-4050, 8300));
        Assert.Equal((st.Paint.Water, "background groundLayer water"), Pick(-4080, 8340));
        Assert.Equal((st.Paint.Buildings["grey"], "background building"), Pick(-3945, 8355));
        // the road: its casing (when the style has one) under its fill
        Assert.Equal((st.Paint.Road.Fill, "background" + (st.Paint.Road.Casing is null ? "" : " casing0") + " road0"), Pick(-4050, 8200));
    }

    [Fact]
    public void AnIconIsDrawnOverItsCircleWithItsLabelBeside()
    {
        using var tmp = new TempFolder();
        var st = MapStyle.Builtin("postalcodemap");
        var label = new Dictionary<string, string> { ["en"] = "Clinic" };
        ResolvedPoi Poi(PoiStyle s, double x, double y) =>
            new(new PoiPoint("1", "g", "", label, x, y, null, null, null, null, null, null), s, PoiShow.Default, s.Color, s.Size, true, false);
        var clinic = new PoiStyle("clinic", ItemName.Of("Clinic"), "icon", new Rgb(0xd0, 0x20, 0x20), 40, "bold", new Rgb(255, 255, 255), 2, new Rgb(0x20, 0x40, 0xff), true, "hospital-box");
        var quiet = clinic with { ShowLabel = false };
        FxMapGenerator.Core.Tests.Poi.PoiTests.Png(tmp.File("black.png"), 20, 10, 0);
        var bytes = File.ReadAllBytes(tmp.File("black.png"));
        var picture = new PoiStyle("pic", ItemName.Of("Picture"), "icon", new Rgb(0, 0, 0), 20, "normal", null, 0, null, false, null, "black.png", null,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), 2, tmp.File("black.png"));
        byte[] Draw(params ResolvedPoi[] pois)
        {
            using var painter = new CellPainter(new CellDrawInput { Info = OneBlock(), Layers = [], Style = st, Pois = pois, Language = "en" });
            return painter.DrawBlock(0, 0);
        }
        var px = Draw(Poi(clinic, -4000, 8300), Poi(picture, -4100, 8300));
        // inside the icon (its rounded box, off its cross): the point's colour; between the icon and the circle's edge: the circle
        Assert.Equal((0xd0, 0x20, 0x20), At(px, -4000 - 12.5, 8300 + 12.5));
        Assert.Equal((0x20, 0x40, 0xff), At(px, -4000 + 25, 8300));
        // the PNG as it is (black), 20 m high and twice as wide
        Assert.Equal((0, 0, 0), At(px, -4100 + 18, 8300 - 4));
        Assert.Equal(C(st.Background), At(px, -4100 + 22, 8300));
        // the name beside: ink east of the circle, none there without it
        int Inked(byte[] rgba)
        {
            int n = 0;
            for (double x = -4000 + 40; x < -4000 + 120; x += 0.5)
                for (double y = 8300 - 10; y < 8300 + 10; y += 0.5)
                    if (At(rgba, x, y) != C(st.Background)) n++;
            return n;
        }
        Assert.True(Inked(px) > 100);
        Assert.Equal(0, Inked(Draw(Poi(quiet, -4000, 8300))));
    }

    [Fact]
    public void RoadTilesDrawTheRoadsAloneAsTheCellsDo()
    {
        var st = MapStyle.Builtin("roadmap");
        var index = new RoadShapesIndex(OneRoad());
        // the z8 tile at the map's north-west corner holds the ribbon's western end (x -4130.., y 8194..8206)
        var png = RoadTiles.Png(index, st, 8, 0, 2);
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal((256, 256), (bmp.Width, bmp.Height));
        var (x0, y0, x1, y1) = RoadTiles.Rect(8, 0, 2);
        Assert.Equal((WorldGrid.Left, WorldGrid.Top - 2 * WorldGrid.TileSize), (x0, y0));
        Assert.Equal(WorldGrid.TileSize, x1 - x0);
        SkiaSharp.SKColor Pixel(double x, double y) => bmp.GetPixel((int)((x - x0) * 256 / WorldGrid.TileSize), (int)((y0 - y) * 256 / WorldGrid.TileSize));
        var fill = Pixel(-4100, 8200);
        Assert.Equal((st.Paint.Road.Fill.R, st.Paint.Road.Fill.G, st.Paint.Road.Fill.B, (byte)255), (fill.Red, fill.Green, fill.Blue, fill.Alpha));
        Assert.Equal(0, Pixel(-4100, 8191).Alpha);                                     // no road: transparent
        // a tile far from it is empty
        using var empty = SkiaSharp.SKBitmap.Decode(RoadTiles.Png(index, st, 8, 60, 100));
        Assert.All(Enumerable.Range(0, 256 * 256), i => Assert.Equal(0, empty.GetPixel(i % 256, i / 256).Alpha));
    }

    [Fact]
    public void TheIndexPicksTheShapesACellWouldTake()
    {
        var roads = new RoadShapesFile.Contents(
            [-4000, 8000, -3990, 8010, 0, 0, 10, 0],
            [new RoadRibbon(0, Square(-4130, 8206, -3870, 8194), Square(-4130, 8205, -3870, 8195)), new RoadRibbon(1, Square(100, 100, 200, 90), Square(100, 99, 200, 91))],
            [new RaisedRun(1, 0, Square(0, 20, 50, 10), Square(0, 19, 50, 11))],
            [new CornerPatch(0, Square(-4000, 8100, -3990, 8090))],
            [new JunctionCorner(1, Square(95, 95, 105, 85), [95, 95, 105, 95], [])],
            [new TunnelGroup([Square(-2000, -1000, -1900, -1100)])],
            3.0);
        var index = new RoadShapesIndex(roads);
        foreach (var r in new[] { (-4140.0, 8400.0, -3858.75, 8118.75), (-100.0, 200.0, 300.0, -100.0), (-2050.0, -950.0, -1950.0, -1050.0), (1000.0, 1000.0, 1100.0, 900.0) })
        {
            var want = CellInputs.RoadsOf(roads, r);
            var got = index.In(r, CellInputs.RoadPad);
            Assert.Equal(want.Tracks, got.Tracks);
            Assert.Equal(want.Ground, got.Ground);
            Assert.Equal(want.Patches, got.Patches);
            Assert.Equal(want.Corners, got.Corners);
            Assert.Equal(want.Raised, got.Raised);
            Assert.Equal(want.Tunnels, got.Tunnels);
        }
    }

    [Fact]
    public async Task TheStageWritesTheCellsTilesAndTheZoomsAboveThem()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("t.fxmapgen.json"));
        project.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
        project.File.Range = new RangeSetting { Base = "none", Add = ["z8_0_0"] };
        project.Save();
        var folder = new WorkFolder(project.WorkFolderPath);
        var cellDir = CellFiles.Folder(folder, new CellId(0, 0));
        Directory.CreateDirectory(cellDir);
        var info = OneBlock();
        File.WriteAllText(Path.Combine(cellDir, CellFiles.Layers),
            $$"""{"format":1,"cell":"cell_0_0","rect":[{{info.Rect.X0}},{{info.Rect.Y0}},{{info.Rect.X1}},{{info.Rect.Y1}}],"grid":{"x0":{{info.GridX0}},"y0":{{info.GridY0}},"width":281,"height":281},"blocks":["z8_0_0"],"layers":[]}""");
        Directory.CreateDirectory(folder.Data);
        var shapes = new GridFile();
        shapes.Meta["trackWidth"] = 3.0;
        shapes.Add("tracks", new Grid<double>(4, 0));
        foreach (var name in new[] { "ground.class", "raised.level", "raised.class", "cornerPatches.class", "junctionCorners.class" }) shapes.Add(name, new Grid<byte>(1, 0));
        foreach (var name in new[] { "ground.casing", "ground.fill", "raised.casing", "raised.fill", "cornerPatches.ring", "junctionCorners.region", "junctionCorners.arc", "junctionCorners.seam", "tunnels.ring" })
        {
            shapes.Add(name + "Start", new Grid<int>(1, 1, [0]));
            shapes.Add(name, new Grid<double>(2, 0));
        }
        shapes.Add("tunnels.group", new Grid<int>(1, 0));
        shapes.Save(Path.Combine(folder.Data, RoadShapesFile.FileName));
        var state = StateStore.Open(folder);
        var map = MapSet.Parse("roadmap");
        Assert.Null(CellDrawStage.Waiting(project, state, map));
        Assert.True(CellDrawStage.IsStale(project, state, map, CellPlan.For(project.Range.Keys).Single()));

        var end = await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = state,
            Stages = (p, _) => new BuildPlan([new CellDrawStage(map)], []),
        }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        var tiles = new TileStore(folder.Tiles("roadmap"));
        Assert.All(from x in Enumerable.Range(0, 4) from y in Enumerable.Range(0, 4) select (x, y), t => Assert.True(tiles.Exists(8, t.x, t.y)));
        Assert.True(tiles.Exists(7, 1, 1) && tiles.Exists(6, 0, 0) && tiles.Exists(5, 0, 0));
        Assert.False(tiles.Exists(4, 0, 0));                                      // the lower zooms are the next step's
        Assert.Equal(C(MapStyle.Builtin("roadmap").Background), (tiles.Read(8, 0, 0)![0], tiles.Read(8, 0, 0)![1], tiles.Read(8, 0, 0)![2]));
        Assert.False(CellDrawStage.IsStale(project, state, map, CellPlan.For(project.Range.Keys).Single()));

        // the lower zooms: the sea outside the range in the style's open sea (its sea's deepest colour), then z7..z0 from the
        // tiles below
        Assert.True(CellLowZoomStage.IsStale(project, state, map));
        var low = await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = state,
            Stages = (p, _) => new BuildPlan([new CellLowZoomStage(map)], []),
        }).RunAsync();
        Assert.Equal(JobState.Done, low.State);
        var sea = MapStyle.Builtin("roadmap").OpenSea;
        Assert.Equal("#45585f", sea.ToString());
        Assert.Equal(C(sea), (tiles.Read(8, 4, 0)![0], tiles.Read(8, 4, 0)![1], tiles.Read(8, 4, 0)![2]));     // block z8_4_0 is not in the range
        Assert.Equal(WorldGrid.BlocksX * 4 * WorldGrid.BlocksY * 4, Directory.GetFiles(Path.Combine(folder.Tiles("roadmap"), "8"), "*.png", SearchOption.AllDirectories).Length);
        Assert.True(tiles.Exists(0, 0, 0) && tiles.Exists(4, 0, 0));
        Assert.False(CellLowZoomStage.IsStale(project, state, map));
        // newer map data of the cell draws it again, and then the lower zooms
        state.SetStageDone(StageKeys.CellPrep, "cell_0_0", DateTime.UtcNow.AddMinutes(1));
        Assert.True(CellDrawStage.IsStale(project, state, map, CellPlan.For(project.Range.Keys).Single()));
    }

    [Fact]
    public void FourSameFlatTilesMakeTheSameFlatParent()
    {
        // Lanczos keeps a flat picture flat, so the parent of four same flat tiles is written as their bytes
        foreach (var (r, g, b) in new[] { (30, 30, 30), (0x7a, 0x9d, 0xbf), (2, 41, 42), (255, 1, 128) })
        {
            var big = new byte[512 * 512 * 4];
            for (int i = 0; i < big.Length; i += 4) (big[i], big[i + 1], big[i + 2], big[i + 3]) = ((byte)r, (byte)g, (byte)b, 255);
            var small = FxMapGenerator.Core.Imaging.Lanczos.Resize(big, 512, 512, 256, 256);
            Assert.All(Enumerable.Range(0, small.Length / 4), i => Assert.Equal((r, g, b, 255), (small[4 * i], small[4 * i + 1], small[4 * i + 2], small[4 * i + 3])));
        }
        using var tmp = new TempFolder();
        var tiles = new TileStore(tmp.Path);
        var flat = new byte[256 * 256 * 4];
        for (int i = 0; i < flat.Length; i += 4) (flat[i], flat[i + 1], flat[i + 2], flat[i + 3]) = (0x1e, 0x1e, 0x1e, 255);
        var png = TileStore.EncodePng(flat, 256, 256);
        foreach (var (x, y) in new[] { (0, 0), (1, 0), (0, 1), (1, 1), (2, 0), (3, 0), (2, 1) }) tiles.WriteBytes(8, x, y, png);
        Assert.True(tiles.BuildParent(7, 0, 0));
        Assert.Equal(png, File.ReadAllBytes(tiles.PathOf(7, 0, 0)));
        Assert.True(tiles.BuildParent(7, 1, 0));                                   // one child missing: drawn the long way
        var p = tiles.Read(7, 1, 0)!;
        Assert.Equal((0x1e, 255), (p[0], p[3]));
        Assert.Equal(0, p[(255 * 256 + 255) * 4 + 3]);                              // the missing quarter is transparent
    }

    [Fact]
    public void TheExportCreditsTheDataTheMapsAreMadeWith()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("t.fxmapgen.json"));
        project.File.Name = "Test City";
        var folder = new WorkFolder(project.WorkFolderPath);
        Directory.CreateDirectory(folder.Data);
        var atlas = MapSet.Parse("atlas-postalcodemap-en");
        var key = project.StyleOf(atlas).Labels!.PlacementKey;
        void Labels(int postal, int routes) => File.WriteAllText(LabelsStage.RecordPath(folder, "en"), JsonSerializer.Serialize(
            new LabelsStage.LabelsRecord("en", [new LabelsStage.LabelsKey(key, [atlas.Id, "atlas-regional-en"], postal, 1, 1, routes)], "", "", [], 0, CayoPerico: false), Project.Json));
        void Postals(string source, string text)
        {
            File.WriteAllText(Path.Combine(folder.Data, PostalCodes.FileName), text);
            File.WriteAllText(Path.Combine(folder.Data, LabelsStage.PostalsRecord), JsonSerializer.Serialize(new LabelsStage.PostalsCopy(source, "", 1, DateTime.UtcNow), Project.Json));
        }
        Labels(10, 3);
        Postals(PostalCodes.DefaultUrl, """[{"code": "1000", "x": 0, "y": 0}]""");

        // everything bundled: the look, the default postal codes, the route numbers, the markers and the dots, each once
        var credits = WebExport.CellMapCredits(project, [atlas, MapSet.Roadmap]);
        Assert.StartsWith("Atlas map (PostalCodeMap style), Road map: made from the world of Grand Theft Auto V", credits);
        Assert.Contains("\"Test City\"", credits);
        var lines = credits.Split('\n');
        foreach (var line in new[] { project.StyleOf(atlas).Credit!, PostalCodes.DefaultCredit, Routes.Credit!, PoiData.Default.Groups[0].Credit!, PoiData.Default.Groups[1].Credit! })
            Assert.Single(lines, l => l == line);
        Assert.Contains("Virus_City", project.StyleOf(atlas).Credit!);
        // the road map alone: no labels, and the bundled points show on the atlas maps only
        Assert.Equal(2, WebExport.CellMapCredits(project, [MapSet.Roadmap]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // another style, no route number placed, a list of the server's own: those lines go
        Labels(10, 0);
        Postals(tmp.File("mine.json"), """[{"code": "1000", "x": 0, "y": 0}]""");
        credits = WebExport.CellMapCredits(project, [MapSet.Parse("atlas-regional-en")]);
        Assert.StartsWith("Atlas map (Regional colors): made from", credits);
        Assert.StartsWith("Atlas map (Regional colors, Japanese): made from", WebExport.CellMapCredits(project, [MapSet.Parse("atlas-regional-ja")]));
        foreach (var gone in new[] { "PostalCodeMap style", "nearest-postal", "GTA Wiki", "Postal codes" }) Assert.DoesNotContain(gone, credits);
        // the server's list with a credit of its own
        Postals(tmp.File("mine.json"), """{"credit": "Postal codes: Test City's own list.", "codes": [{"code": "1000", "x": 0, "y": 0}]}""");
        Assert.Contains("Postal codes: Test City's own list.", WebExport.CellMapCredits(project, [atlas]));

        // points of the project's own: their group's credit where they show, and no more the bundled markers
        Directory.CreateDirectory(tmp.File("poi"));
        File.WriteAllText(tmp.File("poi/shops.json"), """{"style": "facility", "credit": "Shops: Test City's list.", "points": [{"id": "1", "label": {"en": "24/7"}, "x": 25, "y": -1350}]}""");
        project.File.Poi = "poi";
        credits = WebExport.CellMapCredits(project, [atlas]);
        Assert.Contains("Shops: Test City's list.", credits);
        Assert.DoesNotContain("Highway One markers", credits);
        Assert.DoesNotContain("Shops:", WebExport.CellMapCredits(project, [MapSet.Roadmap]));
        Assert.Equal("", WebExport.CellMapCredits(project, [MapSet.Satellite]));
        Assert.DoesNotContain("Material Design Icons", credits);

        // the points drawn with an MDI icon: the icons' credit, once, on the maps that show them; a PNG of one's own says nothing
        project.File.PoiStyles = "poi-styles.json";
        File.WriteAllText(tmp.File("poi-styles.json"), """{"format": 1, "styles": [{"id": "shop", "name": "Shop", "look": "icon", "icon": "store", "color": "#303060", "size": 24}]}""");
        File.WriteAllText(tmp.File("poi/shops.json"), """{"style": "shop", "credit": "Shops: Test City's list.", "points": [{"id": "1", "label": {"en": "24/7"}, "x": 25, "y": -1350}, {"id": "2", "label": {"en": "Ammu"}, "x": 40, "y": -1300}]}""");
        credits = WebExport.CellMapCredits(project, [atlas, MapSet.Parse("atlas-regional-en")]);
        Assert.Single(credits.Split('\n'), l => l == MdiIcons.Credit);
        Assert.StartsWith("Icons: Material Design Icons by Pictogrammers (@mdi/svg 7.4.47)", MdiIcons.Credit);
        Assert.DoesNotContain("Material Design Icons", WebExport.CellMapCredits(project, [MapSet.Roadmap]));
        Directory.CreateDirectory(tmp.File("poi-icons"));
        using (var bitmap = new SkiaSharp.SKBitmap(2, 2))
        using (var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(tmp.File("poi-icons/shop.png"), png.ToArray());
        File.WriteAllText(tmp.File("poi-styles.json"), """{"format": 1, "styles": [{"id": "shop", "name": "Shop", "look": "icon", "image": "poi-icons/shop.png", "color": "#303060", "size": 24}]}""");
        credits = WebExport.CellMapCredits(project, [atlas]);
        Assert.Contains("Shops: Test City's list.", credits);
        Assert.DoesNotContain("Material Design Icons", credits);
        // the viewer's colour behind the tiles is the open sea, as the tiles outside the range have it
        Assert.Equal(MapStyle.Builtin("roadmap").OpenSea.ToString(), WebExport.MapOf(project, new WorkFolder(project.WorkFolderPath), "roadmap").Background);
        Assert.Equal(("#739ebd", "#739ebd"), (MapStyle.Builtin("postalcodemap").OpenSea.ToString(), MapStyle.Builtin("regional").OpenSea.ToString()));
    }
}
