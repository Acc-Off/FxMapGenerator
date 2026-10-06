using System.IO.Compression;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;
using FxMapGenerator.Core.Tests.Jobs;

namespace FxMapGenerator.Core.Tests.Export;

public sealed class ExportTests
{
    static readonly DateTime T0 = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A project with a few satellite tiles on several zoom levels and the minimap dictionaries (stand-ins) made.</summary>
    sealed class Setup : IDisposable
    {
        readonly TempFolder _tmp = new();
        public Project Project { get; }
        public WorkFolder Folder { get; }
        public StateStore State { get; }
        public string Out => _tmp.File("out");
        public string Scratch => _tmp.Path;
        public int Tiles { get; }

        public Setup()
        {
            Project = Project.Create(_tmp.File("p.fxmapgen.json"), "Test \"server\"");
            Project.File.Range.Base = "none";
            Project.File.Minimap.Map = "satellite";
            Project.Save();
            Folder = new WorkFolder(_tmp.Path);
            var tiles = new TileStore(Folder.Tiles("satellite"));
            var px = new byte[256 * 256 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 4; px[i + 1] = 43; px[i + 2] = 45; px[i + 3] = 255; }
            var png = TileStore.EncodePng(px, 256, 256);
            foreach (var (z, x, y) in new[] { (0, 0, 0), (6, 3, 40), (7, 20, 90), (8, 0, 0), (8, 17, 3), (8, 127, 191), (8, 40, 180) })
                tiles.WriteBytes(z, x, y, png);
            Tiles = 7;
            var ytd = Folder.Minimap("satellite");
            Directory.CreateDirectory(ytd);
            foreach (var s in MinimapSheets.All)
                foreach (var n in new[] { s.SeaTexture, s.Texture }) File.WriteAllBytes(Path.Combine(ytd, n + ".ytd"), [1, 2, 3, (byte)s.R, (byte)s.C]);
            File.WriteAllBytes(Path.Combine(ytd, MinimapLod.Texture + ".ytd"), [1, 2, 3, 9, 9]);
            State = StateStore.Open(Folder);
            State.SetStageDone(StageKeys.LowZoom, "satellite", T0);
            State.SetStageDone(StageKeys.Ytd, MinimapSheets.All.Select(s => StageKeys.YtdUnit("satellite", s.Name)).Append(StageKeys.YtdUnit("satellite", MinimapLod.Unit)),
                T0.AddMinutes(1));
            MinimapStage.WriteRecord(Project, "satellite");
        }

        public ExportOptions Options(bool zip = false, string? baseUrl = null, bool minimap = true) =>
            new(Out, ["satellite"], zip, baseUrl, minimap);

        /// <summary>Runs an export; what its minimap resource takes from the game's files is given (no game is read).</summary>
        /// <param name="islandMap">The island map for a project reading Cayo Perico's roads.</param>
        public async Task<JobSnapshot> Run(ExportOptions o, byte[]? islandMap = null) => await JobRunner.Create(new JobSetup
        {
            ProjectPath = Project.FilePath,
            Workers = 3,
            Processors = 3,
            Memory = new FakeMemory(),
            Stages = (_, _) => new BuildPlan([new ExportStage(o, "9.9.9", TestGame.Given(islandMap))], []),
        }).RunAsync();

        public string TempFile(string name) => _tmp.File(name);

        public void Dispose() => _tmp.Dispose();
    }

    static string[] Files(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void TheInventoryCountsTilesAndMinimapSheets()
    {
        using var s = new Setup();
        var inv = ExportInventory.Of(s.Project, s.State, TestGame.Given());
        var sat = Assert.Single(inv.Maps);
        Assert.Equal(("satellite", 7, true), (sat.Map, sat.Tiles, sat.UpToDate));
        Assert.Equal(new[] { 0, 6, 7, 8 }, sat.Zooms);
        Assert.True(inv.Minimap.Ready);
        Assert.Empty(inv.Check(s.Options()));

        s.State.SetStageDone(StageKeys.LowZoom, "satellite", T0.AddHours(1));    // newer tiles than the dictionaries
        var stale = ExportInventory.Of(s.Project, s.State, TestGame.Given());
        Assert.Equal(0, stale.Minimap.Made);
        Assert.Equal(new[] { "MINIMAP_NOT_READY" }, stale.Check(s.Options()).Select(p => p.Code));
        Assert.Empty(stale.Check(s.Options(minimap: false)));
    }

    /// <summary>The setup's project with Cayo Perico: the frame the preset needs, its blocks, its roads read.</summary>
    static IReadOnlyList<CellId> WithCayoPerico(Setup s)
    {
        var frame = RangePresets.Find("cayoPerico")!.Frame;
        s.Project.File.Range.ExtraCells = new ExtraCellsSetting { Top = frame.CellsTop, Bottom = frame.CellsBottom, Left = frame.CellsLeft, Right = frame.CellsRight };
        s.Project.File.CayoPerico = true;
        s.Project.AddPreset("cayoPerico");
        s.Project.Save();
        return MinimapExtraTiles.Cells(s.Project.Range.Keys);
    }

    [Fact]
    public void WithCayoPericoTheInventoryCountsTheTexturesBeyondTheStandardFrameAndChecksTheGame()
    {
        using var s = new Setup();
        var extra = WithCayoPerico(s);
        Assert.Equal(new[] { new CellId(5, 4), new CellId(6, 3), new CellId(6, 4) }, extra);       // east and south of the standard frame
        MinimapGameFiles noGame = TestGame.NoGame(s.Scratch), noKeys = TestGame.NoKeys(s.Scratch);

        var inv = ExportInventory.Of(s.Project, s.State, noGame);
        Assert.Equal((7, 3, 10, true, 0, "gta"), (inv.Minimap.Made, inv.Minimap.Extra, inv.Minimap.Total, inv.Minimap.IslandMap, inv.Minimap.IslandLandMissing, inv.Minimap.Game));
        Assert.Equal(new[] { "MINIMAP_NOT_READY", "MINIMAP_NO_GTA" }, inv.Check(s.Options()).Select(p => p.Code));
        Assert.Equal(new[] { "MINIMAP_NOT_READY", "MINIMAP_NO_KEYS" }, ExportInventory.Of(s.Project, s.State, noKeys).Check(s.Options()).Select(p => p.Code));
        Assert.Empty(inv.Check(s.Options(minimap: false)));

        // made after the tiles: ready
        var ytd = s.Folder.Minimap("satellite");
        foreach (var c in extra) File.WriteAllBytes(Path.Combine(ytd, MinimapExtraTiles.Texture(c) + ".ytd"), [5, (byte)c.R, (byte)c.C]);
        s.State.SetStageDone(StageKeys.Ytd, extra.Select(c => StageKeys.YtdUnit("satellite", c.Name)), T0.AddMinutes(1));
        inv = ExportInventory.Of(s.Project, s.State, noGame);
        Assert.Equal(10, inv.Minimap.Made);
        Assert.Equal(new[] { "MINIMAP_NO_GTA" }, inv.Check(s.Options()).Select(p => p.Code));

        // a land block of the island left out of the range: counted (the export is not stopped by it)
        var land = RangePresets.Find("cayoPerico")!.Blocks.First(kv => kv.Value == BlockClass.Land).Key;
        s.Project.SetInRange([land], include: false);
        Assert.Equal(1, ExportInventory.Of(s.Project, s.State, noGame).Minimap.IslandLandMissing);

        // without the island's roads there is no island map; the resource still takes the interior maps from the game
        s.Project.File.CayoPerico = false;
        inv = ExportInventory.Of(s.Project, s.State, noGame);
        Assert.Equal((false, 0, "gta"), (inv.Minimap.IslandMap, inv.Minimap.IslandLandMissing, inv.Minimap.Game));
        Assert.Equal(new[] { "MINIMAP_NO_GTA" }, inv.Check(s.Options()).Select(p => p.Code));
        Assert.Equal(new[] { "MINIMAP_NO_KEYS" }, ExportInventory.Of(s.Project, s.State, noKeys).Check(s.Options()).Select(p => p.Code));
        Assert.Empty(inv.Check(s.Options(minimap: false)));
        // the parts given in place of a game: nothing is missing
        inv = ExportInventory.Of(s.Project, s.State, TestGame.Given());
        Assert.Null(inv.Minimap.Game);
        Assert.Empty(inv.Check(s.Options()));

        // a project without a minimap reads nothing from the game
        s.Project.File.Minimap.Map = null;
        Assert.Null(ExportInventory.Of(s.Project, s.State, noGame).Minimap.Game);
    }

    [Fact]
    public async Task WithCayoPericoTheResourceCarriesTheTexturesBeyondTheStandardFrameAndTheIslandMap()
    {
        using var s = new Setup();
        var extra = WithCayoPerico(s);
        var ytd = s.Folder.Minimap("satellite");
        foreach (var c in extra) File.WriteAllBytes(Path.Combine(ytd, MinimapExtraTiles.Texture(c) + ".ytd"), [5, (byte)c.R, (byte)c.C]);
        s.State.SetStageDone(StageKeys.Ytd, extra.Select(c => StageKeys.YtdUnit("satellite", c.Name)), T0.AddMinutes(1));
        var end = await s.Run(s.Options() with { Maps = [] }, islandMap: [7, 7, 7]);
        Assert.Equal(JobState.Done, end.State);

        var res = Path.Combine(s.Out, "fxmapgen-minimap-" + DateTime.Now.ToString("yyyyMMdd"));
        foreach (var c in extra)
            Assert.Equal(new byte[] { 5, (byte)c.R, (byte)c.C }, File.ReadAllBytes(Path.Combine(res, "stream", MinimapExtraTiles.Texture(c) + ".ytd")));
        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(Path.Combine(res, "stream", IslandMap.FileName)));
        var manifest = File.ReadAllText(Path.Combine(res, "fxmanifest.lua"));
        Assert.Contains("    'stream/int3232302352.gfx',\n", manifest);
        Assert.Contains("data_file 'SCALEFORM_DLC_FILE' 'stream/int3232302352.gfx'\n", manifest);
        var config = File.ReadAllText(Path.Combine(res, "config.lua"));
        Assert.Contains("config.remove_blur = true\n", config);
        // cell 6_3: its north-west corner is x -4140 + 3 x 2250, y 8400 - 6 x 2250
        Assert.Contains("    ['fxmapgen_extra_6_3'] = {\n        txd = \"fxmapgen_extra_6_3\",\n        txn = \"fxmapgen_extra_6_3\",\n        x = 2610.0,\n        y = -5100.0,\n"
            + "        x_scale = 0.5,\n        y_scale = 0.5,\n", config);
        foreach (var readme in new[] { "README.txt", "README.ja.txt" })
        {
            var text = File.ReadAllText(Path.Combine(res, readme));
            Assert.Contains("CayoPericoMinimap", text);
            // the two lines the forum's island resource calls, as that resource writes them, and the setting for a
            // server that keeps them; no advice to add the lines
            Assert.Contains("    SetRadarAsExteriorThisFrame()\n    SetRadarAsInteriorThisFrame(`h4_fake_islandx`, vec(4700.0, -5145.0), 0, 0)\n", text);
            Assert.Contains("    setr fxmapgen_minimap_fixed_zoom true\n", text);
            Assert.Contains(MinimapResource.IslandResourceAddress, text);
            Assert.DoesNotContain("GetHashKey", text);
        }
        // with the island in the map, the lines are not needed for it
        Assert.Contains("The island shows as this map's own\n  pictures: the two lines are not needed for it.\n", File.ReadAllText(Path.Combine(res, "README.txt")));
        Assert.Contains("島は、このリソースの地図の絵で出ます（2 行は要りません）。\n", File.ReadAllText(Path.Combine(res, "README.ja.txt")));
        // the credits say where the island map's file comes from
        Assert.Contains("stream/int3232302352.gfx): Cayo Perico's island map of Grand Theft Auto V (Rockstar Games)", File.ReadAllText(Path.Combine(res, "CREDITS.txt")));
    }

    [Fact]
    public void TheInventoryCountsTheTilesOfEachZoom()
    {
        using var s = new Setup();
        var sat = Assert.Single(ExportInventory.Of(s.Project, s.State).Maps);
        Assert.Equal(new[] { 1, 0, 0, 0, 0, 0, 1, 1, 4 }, sat.TilesPerZoom);
        Assert.Equal(sat.Bytes, sat.BytesPerZoom.Sum());
        Assert.Equal(3, sat.UpTo(7).Tiles);
        Assert.Equal(2, sat.UpTo(6).Tiles);
        Assert.Equal((sat.Tiles, sat.Bytes), sat.UpTo(8));
    }

    [Fact]
    public async Task AnExportUpToZoom7LeavesOutZoom8AndTellsTheViewerAndLbPhone()
    {
        using var s = new Setup();
        var end = await s.Run(s.Options() with { MaxZoom = 7 });
        Assert.Equal(JobState.Done, end.State);

        var web = Path.Combine(s.Out, "web");
        var tiles = Files(web).Where(f => f.StartsWith("tiles/satellite/", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "tiles/satellite/0/0/0.png", "tiles/satellite/6/3/40.png", "tiles/satellite/7/20/90.png" }, tiles);
        Assert.Contains("\"maxZoom\":7", File.ReadAllText(Path.Combine(web, "index.html")));
        var lb = File.ReadAllText(Path.Combine(web, "lb-phone.lua"));
        Assert.Contains("resolution  = { 16384, 24576 }", lb);     // the same numbers as loaf's map: lb-phone makes zoom 0-7 of it
        Assert.Contains("zoom        = { default = 3, max = 7, min = 0 }", lb);
        var readme = File.ReadAllText(Path.Combine(web, "README.txt"));
        Assert.Contains("zoom 0-7 (0.55 m a pixel at zoom 7)", readme);
        Assert.Contains("The tiles are 3 files.", readme);
        var record = ExportRecord.Read(s.Out)!;
        Assert.Equal((7, 3), (record.Web!.MaxZoom, record.Web.Tiles));

        // as a zip up to zoom 6
        Assert.Equal(JobState.Done, (await s.Run(s.Options(zip: true, minimap: false) with { MaxZoom = 6 })).State);
        using var zip = ZipFile.OpenRead(Path.Combine(s.Out, "web.zip"));
        Assert.Equal(new[] { "tiles/satellite/0/0/0.png", "tiles/satellite/6/3/40.png" },
            zip.Entries.Select(e => e.FullName).Where(e => e.StartsWith("tiles/", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACompleteExportRemovesTheKeptTilesOfTheMapsItWrote()
    {
        using var s = new Setup();
        var other = new byte[256 * 256 * 4];
        for (int i = 0; i < other.Length; i += 4) { other[i] = 200; other[i + 3] = 255; }
        TileStore.Keeping(s.Folder, "satellite").WriteBytes(8, 17, 3, TileStore.EncodePng(other, 256, 256));   // written over since
        Assert.NotNull(BeforeTiles.Of(s.Folder, "satellite"));
        Assert.Equal(JobState.Done, (await s.Run(s.Options(minimap: false))).State);
        Assert.Null(BeforeTiles.Of(s.Folder, "satellite"));                     // what was written out is the next "before"
        Assert.False(Directory.Exists(s.Folder.Before("satellite")));
    }

    [Fact]
    public void ChoicesThatCannotBeWrittenSaySo()
    {
        using var s = new Setup();
        var inv = ExportInventory.Of(s.Project, s.State, TestGame.Given());
        Assert.Equal(new[] { "NOTHING" }, inv.Check(new ExportOptions(s.Out, [], false, null, false)).Select(p => p.Code));
        Assert.Equal(new[] { "NO_TILES" }, inv.Check(new ExportOptions(s.Out, ["roadmap"], false, null, false)).Select(p => p.Code));
        Assert.Equal(new[] { "BAD_URL" }, inv.Check(s.Options(baseUrl: "ftp://x")).Select(p => p.Code));
        Assert.Equal(new[] { "BAD_NAME" }, inv.Check(s.Options() with { ResourceName = "My Map" }).Select(p => p.Code));
        Assert.Equal(new[] { "BAD_ZOOM" }, inv.Check(s.Options() with { MaxZoom = 5 }).Select(p => p.Code));
        Assert.Equal(new[] { "BAD_ZOOM" }, inv.Check(s.Options() with { MaxZoom = 9 }).Select(p => p.Code));
        Directory.CreateDirectory(s.Out);
        File.WriteAllText(Path.Combine(s.Out, "mine.txt"), "someone else's file");
        Assert.Equal(new[] { "NOT_EMPTY" }, inv.Check(s.Options()).Select(p => p.Code));
    }

    [Fact]
    public async Task AFolderExportHasTheTilesViewerLbPhoneExampleAndTheMinimapResource()
    {
        using var s = new Setup();
        var end = await s.Run(s.Options(baseUrl: "https://maps.example.net/gta/"));
        Assert.Equal(JobState.Done, end.State);

        var web = Path.Combine(s.Out, "web");
        var files = Files(web);
        Assert.Equal(s.Tiles, files.Count(f => f.StartsWith("tiles/satellite/", StringComparison.Ordinal)));
        Assert.Contains("tiles/satellite/8/127/191.png", files);
        foreach (var f in new[] { "index.html", "leaflet/leaflet.js", "leaflet/leaflet.css", "leaflet/LICENSE.txt", "lb-phone.lua", "README.txt", "CREDITS.txt" })
            Assert.Contains(f, files);
        Assert.Equal(File.ReadAllBytes(new TileStore(s.Folder.Tiles("satellite")).PathOf(8, 17, 3)), File.ReadAllBytes(Path.Combine(web, "tiles/satellite/8/17/3.png")));

        var html = File.ReadAllText(Path.Combine(web, "index.html"));
        Assert.Contains("\"id\":\"satellite\"", html);
        Assert.Contains("<title>Test \"server\"</title>", html);
        Assert.DoesNotContain("/*CONFIG*/", html);
        var lb = File.ReadAllText(Path.Combine(web, "lb-phone.lua"));
        Assert.Contains("url         = \"https://maps.example.net/gta/tiles/{layer}/{z}/{x}/{y}.png\"", lb);
        Assert.Contains("resolution  = { 32768, 49152 }", lb);
        Assert.Contains("{ name = \"satellite\", background = \"#03292a\" }", lb);   // no water block in the range: the fallback sea
        Assert.Contains("label       = \"Test \\\"server\\\"\"", lb);

        var name = "fxmapgen-minimap-" + DateTime.Now.ToString("yyyyMMdd");
        var res = Path.Combine(s.Out, name);
        var rfiles = Files(res);
        Assert.Equal(13, rfiles.Count(f => f.StartsWith("stream/minimap_", StringComparison.Ordinal) && f.EndsWith(".ytd", StringComparison.Ordinal)));
        Assert.Equal(new byte[] { 1, 2, 3, 9, 9 }, File.ReadAllBytes(Path.Combine(res, "stream/minimap_lod_128.ytd")));   // the small whole map too
        Assert.Equal(65, rfiles.Count(f => f.EndsWith(".ydd", StringComparison.Ordinal)));
        Assert.Equal(new byte[] { 1, 2, 3, 2, 1 }, File.ReadAllBytes(Path.Combine(res, "stream/minimap_sea_2_1.ytd")));
        // Extra Map Tiles 3.0.1 as released, byte for byte, with its MIT license
        foreach (var f in new[] { "client.lua", "exports.lua", "scaleforms.lua", "utils.lua", "stream/minimap_main_map.gfx", "stream/radar_masks.ytd" })
            Assert.Equal(File.ReadAllBytes(RepoFiles.Path("data", "minimap", "extra-map-tiles", f)), File.ReadAllBytes(Path.Combine(res, f)));
        Assert.Equal(File.ReadAllBytes(RepoFiles.Path("data", "minimap", "extra-map-tiles", "LICENSE")), File.ReadAllBytes(Path.Combine(res, "extra-map-tiles-LICENSE.txt")));
        foreach (var f in new[] { "fxmanifest.lua", "config.lua", "interiors.lua", "zoom.lua", "README.txt", "README.ja.txt", "CREDITS.txt" }) Assert.Contains(f, rfiles);
        // the standard frame alone: no textures beyond it, no island map
        Assert.Equal(13 + 65 + 2 + 5 + 7, rfiles.Length);                   // + Extra Map Tiles (2 streamed, 4 scripts, license) + 7 written
        var manifest = File.ReadAllText(Path.Combine(res, "fxmanifest.lua"));
        Assert.Contains($"name '{name}'", manifest);
        // the game's interior maps as given, loaded before the script that reads them
        Assert.Contains("    'interiors.lua',\n    'zoom.lua',\n", manifest);
        Assert.Equal(InteriorMaps.Lua(TestGame.Interiors, "9.9.9"), File.ReadAllText(Path.Combine(res, "interiors.lua")));
        Assert.DoesNotContain("SCALEFORM_DLC_FILE", manifest);
        var config = File.ReadAllText(Path.Combine(res, "config.lua"));
        Assert.Contains("config.remove_blur = false\n", config);
        Assert.Contains("config.tiles = {\n}\n", config);
        var credits = File.ReadAllText(Path.Combine(res, "CREDITS.txt"));
        foreach (var who in new[] { "Virus_City", "Extra Map Tiles 3.0.1", "Alex Licuriceanu", "MIT License" }) Assert.Contains(who, credits);
        foreach (var gone in new[] { "RRixxles", "manups4e", "MINIMAP_LOADER" }) Assert.DoesNotContain(gone, credits);   // the loader it replaced
        Assert.DoesNotContain("island map", credits);   // no island map without Cayo Perico's roads
        Assert.Contains("The list of interior maps (interiors.lua)", credits);
        Assert.Contains("starting FiveM again", File.ReadAllText(Path.Combine(res, "README.txt")));
        Assert.Contains("FiveM を起動し直した後", File.ReadAllText(Path.Combine(res, "README.ja.txt")));
        Assert.DoesNotContain("CayoPericoMinimap", File.ReadAllText(Path.Combine(res, "README.txt")));
        // what shows inside buildings and underground, and what a server with the forum's island resource does: without
        // the island in the map, taking the two lines out takes the game's island map away
        foreach (var readme in new[] { "README.txt", "README.ja.txt" })
        {
            var text = File.ReadAllText(Path.Combine(res, readme));
            Assert.Contains("    SetRadarAsExteriorThisFrame()\n    SetRadarAsInteriorThisFrame(`h4_fake_islandx`, vec(4700.0, -5145.0), 0, 0)\n", text);
            Assert.Contains("    setr fxmapgen_minimap_fixed_zoom true\n", text);
            Assert.Contains("interiors.lua", text);
        }
        Assert.Contains("Inside buildings and underground:\n", File.ReadAllText(Path.Combine(res, "README.txt")));
        Assert.Contains("The game's own island map, which\n  those lines ask for, no longer shows.\n", File.ReadAllText(Path.Combine(res, "README.txt")));
        Assert.Contains("建物の中と地下の地図:\n", File.ReadAllText(Path.Combine(res, "README.ja.txt")));
        Assert.Contains("この 2 行が出していたゲームの島の地図は、出なくなります。\n", File.ReadAllText(Path.Combine(res, "README.ja.txt")));

        var record = ExportRecord.Read(s.Out)!;
        Assert.Equal((s.Tiles, false, true, "9.9.9"), (record.Web!.Tiles, record.Web.Zip, record.Web.Complete, record.Version));
        var resource = Assert.Single(record.Resources);
        Assert.Equal((name, "satellite", 4096), (resource.Name, resource.Map, resource.Size));
    }

    [Fact]
    public async Task CellsAddedAboveOrToTheLeftMoveTheNumbersWrittenOutToTheFramesCorner()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("p.fxmapgen.json"), "Moved");
        project.File.Range.Base = "none";
        project.File.Range.ExtraCells = new ExtraCellsSetting { Top = 1, Left = 1 };
        project.Save();
        var folder = new WorkFolder(tmp.Path);
        var tiles = new TileStore(folder.Tiles("satellite"));
        static byte[] Png(byte v)
        {
            var px = new byte[256 * 256 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255; }
            return TileStore.EncodePng(px, 256, 256);
        }
        // zoom 8: the frame's north-west corner and a tile of the standard frame; zoom 3: the four tiles around the origin
        tiles.WriteBytes(8, -32, -32, Png(10));
        tiles.WriteBytes(8, 17, 3, Png(20));
        foreach (var (x, y, v) in new[] { (-1, -1, 30), (0, -1, 40), (-1, 0, 50), (0, 0, 60) }) tiles.WriteBytes(3, x, y, Png((byte)v));
        tiles.WriteBytes(2, -1, -1, Png(99));      // the work folder's zoom 2 straddles the frame's edge: not written out
        StateStore.Open(folder).SetStageDone(StageKeys.LowZoom, "satellite", T0);
        async Task<JobSnapshot> Run(bool zip) => await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 3, Processors = 3, Memory = new FakeMemory(),
            Stages = (_, _) => new BuildPlan([new ExportStage(new ExportOptions(tmp.File("out"), ["satellite"], zip, null, false), "9.9.9")], []),
        }).RunAsync();

        Assert.Equal(JobState.Done, (await Run(zip: false)).State);
        var web = tmp.File(@"out\web");
        var files = Files(web);
        Assert.Equal(File.ReadAllBytes(tiles.PathOf(8, -32, -32)), File.ReadAllBytes(Path.Combine(web, "tiles/satellite/8/0/0.png")));
        Assert.Equal(File.ReadAllBytes(tiles.PathOf(8, 17, 3)), File.ReadAllBytes(Path.Combine(web, "tiles/satellite/8/49/35.png")));
        Assert.Equal(File.ReadAllBytes(tiles.PathOf(3, -1, -1)), File.ReadAllBytes(Path.Combine(web, "tiles/satellite/3/0/0.png")));
        Assert.Equal(File.ReadAllBytes(tiles.PathOf(3, 0, 0)), File.ReadAllBytes(Path.Combine(web, "tiles/satellite/3/1/1.png")));
        Assert.DoesNotContain(files, f => f.Contains("/-", StringComparison.Ordinal));
        // zoom 2 to 0 made again from the tiles written at zoom 3: the four around the origin are the new zoom-2 tile (0, 0)
        Assert.Equal(new[] { "tiles/satellite/0/0/0.png", "tiles/satellite/1/0/0.png", "tiles/satellite/2/0/0.png" },
            files.Where(f => f.StartsWith("tiles/satellite/0/", StringComparison.Ordinal) || f.StartsWith("tiles/satellite/1/", StringComparison.Ordinal) || f.StartsWith("tiles/satellite/2/", StringComparison.Ordinal)));
        Assert.NotEqual(File.ReadAllBytes(tiles.PathOf(2, -1, -1)), File.ReadAllBytes(Path.Combine(web, "tiles/satellite/2/0/0.png")));
        var lb = File.ReadAllText(Path.Combine(web, "lb-phone.lua"));
        Assert.Contains("topLeft     = { -6390, 10650 }", lb);
        Assert.Contains("bottomRight = { 4860, -5100 }", lb);
        Assert.Contains("resolution  = { 40960, 57344 }", lb);
        var html = File.ReadAllText(Path.Combine(web, "index.html"));
        Assert.Contains("const TILE = 70.3125, LEFT = -6390, TOP = 10650, RIGHT = 4860, BOTTOM = -5100;", html);
        Assert.Contains("numbered from its north-west corner", html);
        var readme = File.ReadAllText(Path.Combine(web, "README.txt"));
        Assert.Contains("x -6390..4860, y 10650..-5100", readme);
        Assert.Contains("with cells of 2250 m added (1 above and 1 to the left)", readme);
        Assert.Contains("The tile numbers start at the frame's north-west corner", readme);
        var written = Files(Path.Combine(web, "tiles"));

        // as one zip: the same tiles under the same names
        Assert.Equal(JobState.Done, (await Run(zip: true)).State);
        using var z = ZipFile.OpenRead(tmp.File(@"out\web.zip"));
        Assert.Equal(written.Select(f => "tiles/" + f), z.Entries.Select(e => e.FullName).Where(n => n.StartsWith("tiles/", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        using (var s = z.GetEntry("tiles/satellite/2/0/0.png")!.Open())
        using (var m = new MemoryStream())
        {
            s.CopyTo(m);
            Assert.NotEqual(File.ReadAllBytes(tiles.PathOf(2, -1, -1)), m.ToArray());
        }
    }

    [Fact]
    public void CellsAddedOnlyBelowAndToTheRightKeepTheNumbersAndTheLoafGrid()
    {
        var f = new MapFrame(0, 1, 0, 1);
        Assert.False(ExportGrid.Renumbers(f));
        Assert.Equal((0, 0), ExportGrid.Shift(f, 8));
        Assert.Equal((160, 224), ExportGrid.Size(f, 8));
        Assert.True(ExportGrid.Copied(f, 0));
        var readme = WebExport.Readme("P", [], 10, "9.9.9", 8, f);
        Assert.Contains("x -4140..7110, y 8400..-7350", readme);
        Assert.Contains("with cells of 2250 m added (1 below and 1 to the right)", readme);
        Assert.Contains("the same grid as the loaf-scripts", readme);
        Assert.Equal(WebExport.Readme("P", [], 10, "9.9.9", 8), WebExport.Readme("P", [], 10, "9.9.9", 8, MapFrame.Standard));
        Assert.Equal(WebExport.ViewerHtml("P", [], "9.9.9"), WebExport.ViewerHtml("P", [], "9.9.9", MapFrame.Standard));
        Assert.Contains("the same grid as the loaf-scripts tiles", WebExport.ViewerHtml("P", [], "9.9.9", f));
        Assert.Contains("LEFT = -4140, TOP = 8400, RIGHT = 7110, BOTTOM = -7350;", WebExport.ViewerHtml("P", [], "9.9.9", f));
        Assert.Contains("resolution  = { 40960, 57344 }", WebExport.LbPhoneExample("P", [], null, 8, f));
        Assert.Contains("resolution  = { 32768, 49152 }", WebExport.LbPhoneExample("P", [], null, 8));
    }

    [Fact]
    public async Task AnotherExportIntoTheSameFolderReplacesTheWebPartAndTakesANewResourceName()
    {
        using var s = new Setup();
        Assert.Equal(JobState.Done, (await s.Run(s.Options())).State);
        Assert.Equal(JobState.Done, (await s.Run(s.Options(zip: true))).State);

        Assert.False(Directory.Exists(Path.Combine(s.Out, "web")));
        using (var zip = ZipFile.OpenRead(Path.Combine(s.Out, "web.zip")))
        {
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Equal(s.Tiles, entries.Count(e => e.StartsWith("tiles/satellite/", StringComparison.Ordinal)));
            Assert.Contains("tiles/satellite/0/0/0.png", entries);
            Assert.Contains("index.html", entries);
            Assert.Contains("lb-phone.lua", entries);
            Assert.Contains("https://example.com/your-map/tiles/{layer}", new StreamReader(zip.GetEntry("lb-phone.lua")!.Open()).ReadToEnd());
        }
        var day = "fxmapgen-minimap-" + DateTime.Now.ToString("yyyyMMdd");
        Assert.True(Directory.Exists(Path.Combine(s.Out, day)));
        Assert.True(Directory.Exists(Path.Combine(s.Out, day + "-2")));
        var record = ExportRecord.Read(s.Out)!;
        Assert.Equal(new[] { day, day + "-2" }, record.Resources.Select(r => r.Name));
        Assert.True(record.Web!.Zip);

        // the minimap alone: the web part stays as it was recorded; a resource folder that is gone drops out
        Directory.Delete(Path.Combine(s.Out, day), recursive: true);
        Assert.Equal(JobState.Done, (await s.Run(new ExportOptions(s.Out, [], false, null, true))).State);
        record = ExportRecord.Read(s.Out)!;
        Assert.Equal(new[] { day + "-2", day }, record.Resources.Select(r => r.Name));
        Assert.Equal((true, s.Tiles), (record.Web!.Zip, record.Web.Tiles));
        Assert.Empty(ExportInventory.Of(s.Project, s.State, TestGame.Given()).Check(s.Options()));   // the folder is an export's: it may be written again
    }
}
