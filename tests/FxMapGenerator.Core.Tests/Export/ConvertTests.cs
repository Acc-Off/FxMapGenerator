using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.Tests.Imaging;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.World;
using FxMapGenerator.GameData.Textures;

namespace FxMapGenerator.Core.Tests.Export;

/// <summary>The conversion of an edited picture: a PNG of the whole frame becomes web tiles and a minimap resource.</summary>
public sealed class ConvertTests
{
    const int W = 8192, H = 12288;                                 // the standard frame at zoom 6
    static readonly (byte R, byte G, byte B, byte A) Ground = (40, 80, 120, 255), Red = (200, 30, 20, 255), Green = (20, 180, 40, 90);

    /// <summary>
    /// The pixel of the test picture: a ground colour, and in some of its tiles of zoom 6 a ramp (0, 0), nothing at all
    /// (a square of 8 x 8 tiles from (16, 8)), red (3, 40) and see-through green (15, 47).
    /// </summary>
    static (byte R, byte G, byte B, byte A) At(int x, int y) => (x >> 8, y >> 8) switch
    {
        (0, 0) => ((byte)x, (byte)y, 7, 255),
        (>= 16 and < 24, >= 8 and < 16) => (0, 0, 0, 0),
        (3, 40) => Red,
        (15, 47) => Green,
        _ => Ground,
    };

    static void WritePicture(string path)
    {
        var plain = new byte[W * 4];
        for (int x = 0; x < W; x++) (plain[4 * x], plain[4 * x + 1], plain[4 * x + 2], plain[4 * x + 3]) = Ground;
        var row = new byte[W * 4];
        TestPng.WriteRgba(path, W, H, y =>
        {
            if ((y >> 8) is not (0 or (>= 8 and < 16) or 40 or 47)) return plain;
            for (int x = 0; x < W; x++) (row[4 * x], row[4 * x + 1], row[4 * x + 2], row[4 * x + 3]) = At(x, y);
            return row;
        });
    }

    static Project RoadMapProject(TempFolder tmp, Action<ProjectFile>? change = null)
    {
        var project = Project.Create(tmp.File("p.fxmapgen.json"), "Test server");
        project.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
        project.File.Range.Base = "none";
        change?.Invoke(project.File);
        project.Save();
        return project;
    }

    static async Task<JobSnapshot> Run(Project project, IReadOnlyList<Stage> stages) => await JobRunner.Create(new JobSetup
    {
        ProjectPath = project.FilePath,
        Workers = 4,
        Processors = 4,
        Memory = new FakeMemory(),
        Stages = (_, _) => new BuildPlan(stages, []),
    }).RunAsync();

    static string[] Files(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void APictureIsTheFrameAtZoom6Or7OrItIsRefused()
    {
        using var tmp = new TempFolder();
        var standard = MapFrame.Standard;
        var wide = new MapFrame(0, 1, 1, 0);                        // a cell below, a cell to the left
        Assert.Equal((8192, 12288), EditedPicture.Size(standard, 6));
        Assert.Equal((16384, 24576), EditedPicture.Size(standard, 7));
        Assert.Equal((10240, 14336), EditedPicture.Size(wide, 6));
        Assert.Equal(6, EditedPicture.ZoomOf(standard, 8192, 12288));
        Assert.Equal(7, EditedPicture.ZoomOf(standard, 16384, 24576));
        Assert.Null(EditedPicture.ZoomOf(standard, 12288, 8192));
        Assert.Null(EditedPicture.ZoomOf(wide, 8192, 12288));
        Assert.Equal(7, EditedPicture.ZoomOf(wide, 20480, 28672));

        byte[] Row(int y) => new byte[64 * 4];
        var small = tmp.File("small.png");
        TestPng.Write(small, 64, 32, 6, 8, Row);
        var look = EditedPicture.Look(small, standard);
        Assert.Equal((64, 32, (int?)null, "BAD_SIZE"), (look.Width, look.Height, look.Zoom, look.Problem));
        Assert.Contains("is 64 x 32 px: the map takes 8192 x 12288 px (zoom 6) or 16384 x 24576 px (zoom 7)", EditedPicture.Sentence(look, standard));
        Assert.Equal("NOT_FOUND", EditedPicture.Look(tmp.File("missing.png"), standard).Problem);
        var text = tmp.File("text.png");
        File.WriteAllText(text, "no picture");
        Assert.Equal("NOT_PNG", EditedPicture.Look(text, standard).Problem);
        var interlaced = tmp.File("interlaced.png");
        TestPng.Write(interlaced, 64, 32, 6, 8, Row, interlaced: true);
        Assert.Equal("INTERLACED", EditedPicture.Look(interlaced, standard).Problem);
        Assert.Contains("write it without interlacing", EditedPicture.Sentence(EditedPicture.Look(interlaced, standard), standard));
    }

    [Fact]
    public void ChoicesThatCannotBeConvertedSaySo()
    {
        using var tmp = new TempFolder();
        var project = RoadMapProject(tmp);
        var fits = new PictureInfo(tmp.File("edited.png"), W, H, 6, null);
        var o = new ConvertOptions(tmp.File("out"), fits.File, "roadmap", Tiles: true, Minimap: true);
        string[] Codes(ConvertOptions options, PictureInfo? picture = null, MinimapGameFiles? game = null) =>
            ConvertStages.Check(project, options, picture ?? fits, game ?? TestGame.Given()).Select(p => p.Code).ToArray();

        Assert.Empty(Codes(o));
        Assert.Equal(new[] { "NOTHING" }, Codes(o with { Tiles = false, Minimap = false }));
        Assert.Equal(new[] { "BAD_MAP" }, Codes(o with { Map = "satellite" }));          // a map the project does not make
        Assert.Equal(new[] { "BAD_MAP" }, Codes(o with { Map = "nonsense" }));
        Assert.Equal(new[] { "BAD_PICTURE" }, Codes(o with { Picture = "" }));
        Assert.Equal(new[] { "BAD_PICTURE" }, Codes(o, new PictureInfo(fits.File, 64, 32, null, "BAD_SIZE")));
        Assert.Equal(new[] { "BAD_PICTURE" }, ConvertStages.Check(project, o, game: TestGame.Given()).Select(p => p.Code));   // looked at here: not there
        Assert.Equal(new[] { "BAD_NAME" }, Codes(o with { ResourceName = "My Map" }));
        Assert.Empty(Codes(o with { ResourceName = "My Map", Minimap = false }));           // no resource: its name is not looked at
        Assert.Equal(new[] { "BAD_URL" }, Codes(o with { BaseUrl = "ftp://x" }));
        Assert.Equal(new[] { "NO_FOLDER" }, Codes(o with { Folder = " " }));
        Directory.CreateDirectory(o.Folder);
        File.WriteAllText(Path.Combine(o.Folder, "mine.txt"), "someone else's file");
        Assert.Equal(new[] { "NOT_EMPTY" }, Codes(o));
        File.Delete(Path.Combine(o.Folder, "mine.txt"));

        // the resource carries the game's interior maps (and, for a project that reads Cayo Perico's roads, the island
        // map), read from this PC's game
        MinimapGameFiles noGame = TestGame.NoGame(tmp.Path), noKeys = TestGame.NoKeys(tmp.Path);
        Assert.Equal(new[] { "MINIMAP_NO_GTA" }, Codes(o, game: noGame));
        Assert.Equal(new[] { "MINIMAP_NO_KEYS" }, Codes(o, game: noKeys));
        Assert.Empty(Codes(o with { Minimap = false }, game: noGame));
        project.File.CayoPerico = true;
        Assert.Equal(new[] { "MINIMAP_NO_GTA" }, Codes(o, game: noGame));
        Assert.Empty(Codes(o));
    }

    [Fact]
    public void TilesAreWrittenOutAsAnExportNumbersThem()
    {
        using var tmp = new TempFolder();
        var tile = new byte[256 * 256 * 4];
        for (int i = 0; i < tile.Length; i += 4) (tile[i], tile[i + 1], tile[i + 2], tile[i + 3]) = (9, 99, 199, 255);
        var png = TileStore.EncodePng(tile, 256, 256);

        // the standard frame: the numbers stay, zooms above the picture's are not written
        var from = new TileStore(tmp.File("work"));
        foreach (var (z, x, y) in new[] { (6, 3, 40), (6, 31, 47), (5, 1, 20), (0, 0, 0), (7, 6, 80) }) from.WriteBytes(z, x, y, png);
        var (n, bytes) = ConvertFilesStage.CopyTiles(from.Root, tmp.File("out"), MapFrame.Standard, 6);
        Assert.Equal((4, 4L * png.Length), (n, bytes));
        Assert.Equal(new[] { "0/0/0.png", "5/1/20.png", "6/3/40.png", "6/31/47.png" }, Files(tmp.File("out")));

        // a cell added to the left and one above: the numbers start at the frame's north-west corner (a cell is 8 tiles
        // of zoom 6, one of zoom 3), and zooms 2 to 0 are made again from the tiles written at zoom 3
        var frame = new MapFrame(1, 0, 1, 0);
        var moved = new TileStore(tmp.File("work2"));
        foreach (var (z, x, y) in new[] { (6, -8, -8), (6, 3, 40), (3, -1, -1), (3, 0, 0), (2, -1, -1), (0, -1, -1) }) moved.WriteBytes(z, x, y, png);
        (n, _) = ConvertFilesStage.CopyTiles(moved.Root, tmp.File("out2"), frame, 6);
        var files = Files(tmp.File("out2"));
        Assert.Contains("6/0/0.png", files);
        Assert.Contains("6/11/48.png", files);
        Assert.Contains("3/0/0.png", files);
        Assert.Contains("3/1/1.png", files);
        Assert.Contains("2/0/0.png", files);                        // made from 3/0/0 .. 3/1/1
        Assert.Contains("0/0/0.png", files);
        Assert.DoesNotContain(files, f => f.Contains('-'));
        Assert.Equal(files.Length, n);
    }

    [Fact]
    public async Task APictureBecomesWebTilesAndAMinimapResourceOfItsOwn()
    {
        using var tmp = new TempFolder();
        // two blocks in the range (the ramp's and the red one's); outside it the minimap is left transparent
        var project = RoadMapProject(tmp, f =>
        {
            f.Range.Add = ["z8_0_0", "z8_12_160"];
            f.Minimap.Outside = MinimapOutside.Transparent;
        });
        var picture = tmp.File("edited.png");
        WritePicture(picture);
        var look = EditedPicture.Look(picture, project.Frame);
        Assert.Equal((W, H, 6, (string?)null), (look.Width, look.Height, look.Zoom, look.Problem));

        // an export folder with a web part written before
        string output = tmp.File("out"), today = DateTime.Now.ToString("yyyyMMdd");
        Directory.CreateDirectory(Path.Combine(output, "web"));
        new ExportRecord(DateTime.UtcNow, "9.9.8", project.FilePath, null, []).Write(output);
        File.WriteAllText(Path.Combine(output, "web", "keep.txt"), "the export's own web part");

        var o = new ConvertOptions(output, picture, "roadmap", Tiles: true, Minimap: true, BaseUrl: "https://maps.example.net/gta/");
        Assert.Empty(ConvertStages.Check(project, o, game: TestGame.Given()));
        var end = await Run(project, ConvertStages.For(o, "9.9.9", TestGame.Given()));
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(new[] { ("convert.tiles", 1, 1), ("convert.textures", 7, 7), ("convert.files", 2, 2) }, end.Stages.Select(s => (s.Id, s.Total, s.Done)));
        Assert.False(Directory.Exists(Path.Combine(output, ".convert")));
        Assert.Equal("the export's own web part", File.ReadAllText(Path.Combine(output, "web", "keep.txt")));

        // the web tiles: every tile of the frame from zoom 6 down, the picture's own pixels at zoom 6
        var web = Path.Combine(output, "web-edited", "roadmap");
        var files = Files(web);
        Assert.Equal(2049, files.Count(f => f.StartsWith("tiles/roadmap/", StringComparison.Ordinal)));
        Assert.DoesNotContain(files, f => f.StartsWith("tiles/roadmap/7/", StringComparison.Ordinal));
        foreach (var f in new[] { "index.html", "leaflet/leaflet.js", "leaflet/leaflet.css", "leaflet/LICENSE.txt", "lb-phone.lua", "README.txt", "CREDITS.txt", "tiles/roadmap/0/0/0.png" })
            Assert.Contains(f, files);
        var written = new TileStore(Path.Combine(web, "tiles", "roadmap"));
        foreach (var (tx, ty) in new[] { (0, 0), (20, 10), (3, 40), (15, 47), (31, 47), (8, 8) })
        {
            var tile = written.Read(6, tx, ty)!;
            for (int y = 0; y < 256; y += 5)
                for (int x = 0; x < 256; x += 3)
                {
                    int i = 4 * (y * 256 + x);
                    Assert.Equal(At(tx * 256 + x, ty * 256 + y), (tile[i], tile[i + 1], tile[i + 2], tile[i + 3]));
                }
        }
        var sea = MapStyle.Builtin("roadmap").OpenSea;
        var html = File.ReadAllText(Path.Combine(web, "index.html"));
        Assert.Contains("\"id\":\"roadmap\"", html);
        Assert.Contains("\"maxZoom\":6", html);
        Assert.Contains($"\"background\":\"{sea}\"", html);
        var lb = File.ReadAllText(Path.Combine(web, "lb-phone.lua"));
        Assert.Contains("url         = \"https://maps.example.net/gta/tiles/{layer}/{z}/{x}/{y}.png\"", lb);
        Assert.Contains("resolution  = { 8192, 12288 }", lb);
        Assert.Contains("zoom        = { default = 3, max = 6, min = 0 }", lb);
        Assert.Contains($"{{ name = \"roadmap\", background = \"{sea}\" }}", lb);
        Assert.Contains("zoom 0-6", File.ReadAllText(Path.Combine(web, "README.txt")));
        Assert.Contains("Road map", File.ReadAllText(Path.Combine(web, "CREDITS.txt")));

        // the minimap resource: as an export's, its pictures the picture's
        var res = Path.Combine(output, "fxmapgen-minimap-" + today);
        var rfiles = Files(res);
        Assert.Equal(13, rfiles.Count(f => f.StartsWith("stream/minimap_", StringComparison.Ordinal) && f.EndsWith(".ytd", StringComparison.Ordinal)));
        Assert.Equal(65, rfiles.Count(f => f.EndsWith(".ydd", StringComparison.Ordinal)));
        foreach (var f in new[] { "fxmanifest.lua", "config.lua", "interiors.lua", "zoom.lua", "README.txt", "README.ja.txt", "CREDITS.txt", "client.lua" }) Assert.Contains(f, rfiles);
        Assert.Contains("Road map", File.ReadAllText(Path.Combine(res, "fxmanifest.lua")));
        Assert.Equal(InteriorMaps.Lua(TestGame.Interiors, "9.9.9"), File.ReadAllText(Path.Combine(res, "interiors.lua")));
        byte[] Pixels(string name, int size)
        {
            var tex = Assert.Single(YtdFile.Read(Path.Combine(res, "stream", name + ".ytd")));
            Assert.Equal((name, size, size, 1), ((string)tex.Name!, (int)tex.Width, (int)tex.Height, (int)tex.Levels));
            return YtdFile.Decompress(tex.Data!.FullData, size, size, tex.Format);
        }
        static void Near(byte[] px, int size, int x, int y, (int R, int G, int B, int A) want, int tolerance)
        {
            int i = 4 * (y * size + x);
            Assert.InRange(px[i], want.R - tolerance, want.R + tolerance);
            Assert.InRange(px[i + 1], want.G - tolerance, want.G + tolerance);
            Assert.InRange(px[i + 2], want.B - tolerance, want.B + tolerance);
            Assert.InRange(px[i + 3], want.A - tolerance, want.A + tolerance);
        }
        // sheet 2_0 is the tiles x 0..15, y 32..47 of zoom 6: the red tile is in the range, the green one and the ground are not
        var sheet = Pixels("minimap_sea_2_0", 4096);
        Near(sheet, 4096, 3 * 256 + 128, 8 * 256 + 128, Red, 8);
        Assert.Equal(0, sheet[4 * ((15 * 256 + 128) * 4096 + 15 * 256 + 128) + 3]);
        Assert.Equal(0, sheet[4 * (100 * 4096 + 100) + 3]);
        Near(Pixels("minimap_2_0", 4096), 4096, 3 * 256 + 128, 8 * 256 + 128, Red, 8);
        // sheet 0_0: the ramp's tile, pixel for pixel
        var first = Pixels("minimap_sea_0_0", 4096);
        Near(first, 4096, 100, 50, (100, 50, 7, 255), 8);
        Near(first, 4096, 200, 220, (200, 220, 7, 255), 8);
        Assert.Equal(0, first[4 * (50 * 4096 + 300) + 3]);         // the tile beside it is outside the range
        // the small whole map takes the whole picture (the range does not cut it), over the road map's open sea
        var lod = Pixels("minimap_lod_128", 128);
        Assert.All(Enumerable.Range(0, 128 * 128), i => Assert.Equal(255, lod[4 * i + 3]));
        Near(lod, 128, 60, 60, (Ground.R, Ground.G, Ground.B, 255), 8);
        // the square with nothing in it is x 64..95, y 21..42 of the small map: the sea shows there
        Near(lod, 128, 80, 32, (sea.R, sea.G, sea.B, 255), 8);

        // the folder's record has both, and what it had
        var record = ExportRecord.Read(output)!;
        Assert.Null(record.Web);
        var edited = Assert.Single(record.WebEdited!);
        Assert.Equal(("roadmap", "roadmap", "edited.png", 6, 2049, true), (edited.Name, edited.Map, edited.Picture, edited.Zoom, edited.Tiles, edited.Complete));
        var resource = Assert.Single(record.Resources);
        Assert.Equal(("fxmapgen-minimap-" + today, "roadmap", 4096, true), (resource.Name, resource.Map, resource.Size, resource.FromPicture));
        Assert.Equal("9.9.9", record.Version);

        // the web tiles alone, again: the folder is made again, the resource and its entry stay
        File.WriteAllText(Path.Combine(web, "stale.txt"), "of the conversion before");
        end = await Run(project, ConvertStages.For(o with { Minimap = false }, "9.9.9"));
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(new[] { "convert.tiles", "convert.files" }, end.Stages.Select(s => s.Id));
        Assert.False(File.Exists(Path.Combine(web, "stale.txt")));
        Assert.Equal(2049, Files(web).Count(f => f.StartsWith("tiles/roadmap/", StringComparison.Ordinal)));
        record = ExportRecord.Read(output)!;
        Assert.Single(record.WebEdited!);
        Assert.True(Assert.Single(record.Resources).FromPicture);

        // an export of the project's own tiles afterwards keeps the conversion's outputs and their entries
        var folder = new WorkFolder(tmp.Path);
        new TileStore(folder.Tiles("roadmap")).Write(6, 3, 40, new byte[256 * 256 * 4]);
        StateStore.Open(folder).SetStageDone(StageKeys.LowZoom, "roadmap", DateTime.UtcNow);
        end = await Run(project, ExportStages.For(new ExportOptions(output, ["roadmap"], false, null, false), "9.9.9"));
        Assert.Equal(JobState.Done, end.State);
        Assert.True(File.Exists(Path.Combine(output, "web", "tiles", "roadmap", "6", "3", "40.png")));
        Assert.False(File.Exists(Path.Combine(output, "web", "keep.txt")));       // the export's web part is written fresh
        Assert.True(File.Exists(Path.Combine(web, "index.html")));
        record = ExportRecord.Read(output)!;
        Assert.NotNull(record.Web);
        Assert.Equal("roadmap", Assert.Single(record.WebEdited!).Name);
        Assert.True(Assert.Single(record.Resources).FromPicture);

        // with the folder of the conversion's web tiles gone, its entry goes at the next writing
        Directory.Delete(web, recursive: true);
        Assert.Empty(ExportRecord.Read(output)!.WebEditedIn(output));
    }

    [Fact]
    public async Task TheTexturesAreTheOnesTheMinimapStepMakesOfTheSameTiles()
    {
        using var tmp = new TempFolder();
        // a frame with Cayo Perico's cells, so that there are textures beyond the standard frame too
        var frame = RangePresets.Find("cayoPerico")!.Frame;
        var project = RoadMapProject(tmp, f =>
        {
            f.Range.ExtraCells = new ExtraCellsSetting { Top = frame.CellsTop, Bottom = frame.CellsBottom, Left = frame.CellsLeft, Right = frame.CellsRight };
            f.Range.Add = ["z8_0_0", "z8_12_160"];
            f.Minimap.Map = "roadmap";
            f.Minimap.Outside = MinimapOutside.Transparent;
        });
        project.AddPreset("cayoPerico");
        project.Save();
        var folder = new WorkFolder(tmp.Path);
        var tiles = new TileStore(folder.Tiles("roadmap"));
        byte[] Tile(byte r, byte g, byte b, byte a)
        {
            var t = new byte[256 * 256 * 4];
            for (int i = 0; i < t.Length; i += 4) (t[i], t[i + 1], t[i + 2], t[i + 3]) = ((byte)(r + i / 4 % 256 / 8), g, b, a);
            return t;
        }
        var island = MinimapExtraTiles.Cells(project.Range.Keys)[0];
        tiles.Write(6, 0, 0, Tile(10, 200, 30, 255));
        tiles.Write(6, 3, 40, Tile(200, 30, 20, 255));
        tiles.Write(6, 15, 47, Tile(20, 180, 40, 90));              // outside the range
        tiles.Write(6, island.C * 8 + 2, island.R * 8 + 1, Tile(90, 90, 200, 255));
        tiles.Write(2, 0, 0, Tile(50, 60, 70, 255));
        tiles.Write(2, 1, 2, Tile(5, 6, 7, 128));
        StateStore.Open(folder).SetStageDone(StageKeys.LowZoom, "roadmap", DateTime.UtcNow);
        var end = await Run(project, [new MinimapStage(MapSet.Roadmap)]);
        Assert.Equal(JobState.Done, end.State);

        // the same tiles as a conversion's first step leaves them
        var o = new ConvertOptions(tmp.File("out"), tmp.File("edited.png"), "roadmap", Tiles: false, Minimap: true);
        foreach (var file in Directory.GetFiles(tiles.Root, "*.png", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(ConvertWork.Tiles(o.Folder), Path.GetRelativePath(tiles.Root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        File.WriteAllText(Path.Combine(ConvertWork.Root(o.Folder), ConvertWork.Made), "6");
        end = await Run(project, [new ConvertTexturesStage(o)]);
        Assert.Equal(JobState.Done, end.State);

        var stepMade = Files(folder.Minimap("roadmap"));
        Assert.Equal(13 + MinimapExtraTiles.Cells(project.Range.Keys).Count, stepMade.Length);
        Assert.Equal(stepMade, Files(ConvertWork.Textures(o.Folder)));
        foreach (var name in stepMade)
            Assert.Equal(File.ReadAllBytes(Path.Combine(folder.Minimap("roadmap"), name)), File.ReadAllBytes(Path.Combine(ConvertWork.Textures(o.Folder), name)));
    }

    [Fact]
    public async Task APictureThatDoesNotFitStopsTheConversionBeforeAnythingIsWritten()
    {
        using var tmp = new TempFolder();
        var project = RoadMapProject(tmp);
        var picture = tmp.File("small.png");
        TestPng.Write(picture, 64, 32, 6, 8, _ => new byte[64 * 4]);
        var o = new ConvertOptions(tmp.File("out"), picture, "roadmap", Tiles: true, Minimap: true);
        var end = await Run(project, ConvertStages.For(o, "9.9.9", TestGame.Given()));
        Assert.Equal(JobState.Failed, end.State);
        Assert.Contains("is 64 x 32 px", end.Error);
        Assert.False(Directory.Exists(Path.Combine(o.Folder, "web-edited")));
        Assert.False(Directory.Exists(Path.Combine(o.Folder, ".convert")));
    }
}
