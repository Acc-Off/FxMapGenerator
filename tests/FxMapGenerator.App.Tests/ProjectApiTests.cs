using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;
using Microsoft.Extensions.DependencyInjection;

namespace FxMapGenerator.App.Tests;

public sealed class ProjectApiTests
{
    static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    static string Code(JsonElement e) => e.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task NewOpenEditAndClose()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.GetAsync("/api/project")).StatusCode);

        var file = Path.Combine(host.DataDirectory, "p", "server");
        var created = await host.Client.PostAsJsonAsync("/api/project/new", new { path = file, name = "My server", preset = "qbcore" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var dto = await Json(created);
        Assert.Equal(file + ".fxmapgen.json", dto.GetProperty("path").GetString());
        Assert.Equal("My server", dto.GetProperty("name").GetString());
        Assert.Equal("qbcore", dto.GetProperty("file").GetProperty("server").GetProperty("preset").GetString());
        Assert.Equal(1045, dto.GetProperty("rangeBlocks").GetInt32());
        var frame = dto.GetProperty("frame");
        Assert.Equal((0, 0, 32, 48), (frame.GetProperty("bx0").GetInt32(), frame.GetProperty("by0").GetInt32(), frame.GetProperty("cols").GetInt32(), frame.GetProperty("rows").GetInt32()));
        Assert.Equal((-4140.0, 8400.0, 4860.0, -5100.0), (frame.GetProperty("west").GetDouble(), frame.GetProperty("north").GetDouble(), frame.GetProperty("east").GetDouble(), frame.GetProperty("south").GetDouble()));
        Assert.Equal("EXISTS", Code(await Json(await host.Client.PostAsJsonAsync("/api/project/new", new { path = file }))));

        var recent = await host.Client.GetFromJsonAsync<JsonElement>("/api/projects/recent");
        Assert.Equal("My server", recent[0].GetProperty("name").GetString());

        // range: take out a default block, put in a sea block, then back to the default
        var edit = await host.Client.PatchAsJsonAsync("/api/project", new { range = new { exclude = new[] { "z8_48_8" }, include = new[] { "z8_0_0" } } });
        var edited = await Json(edit);
        Assert.Equal(1045, edited.GetProperty("rangeBlocks").GetInt32());
        Assert.Equal("z8_0_0", edited.GetProperty("file").GetProperty("range").GetProperty("add")[0].GetString());
        var blocks = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/blocks");
        var range = blocks.GetProperty("range").GetString()!;
        Assert.Equal(32 * 48, range.Length);
        Assert.Equal('W', range[0]);                                                  // z8_0_0, added (open sea counts as water)
        Assert.Equal('.', range[BlockId.Parse("z8_48_8").By * 32 + BlockId.Parse("z8_48_8").Bx]);
        Assert.Equal('.', blocks.GetProperty("defaultRange").GetString()![0]);
        Assert.Equal(0, (await Json(await host.Client.PatchAsJsonAsync("/api/project", new { range = new { reset = true } })))
            .GetProperty("file").GetProperty("range").GetProperty("add").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PatchAsJsonAsync("/api/project", new { range = new { include = new[] { "z8_1_1" } } })).StatusCode);

        var maps = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { minimapMap = "satellite", parallel = 3 }));
        Assert.Equal("satellite", maps.GetProperty("file").GetProperty("minimap").GetProperty("map").GetString());
        Assert.Equal("map", maps.GetProperty("file").GetProperty("minimap").GetProperty("outside").GetString());
        var clear = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { minimapOutside = "transparent" }));
        Assert.Equal("transparent", clear.GetProperty("file").GetProperty("minimap").GetProperty("outside").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PatchAsJsonAsync("/api/project", new { minimapOutside = "grey" })).StatusCode);
        var off = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { satellite = false }));
        Assert.Equal(JsonValueKind.Null, off.GetProperty("file").GetProperty("minimap").GetProperty("map").ValueKind);   // follows the map

        var plan = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/plan?workers=2");
        Assert.Equal(Math.Min(2, Environment.ProcessorCount), plan.GetProperty("table").GetProperty("workers").GetInt32());
        Assert.Equal(0, plan.GetProperty("runnable").GetArrayLength());               // no satellite: nothing this version runs

        await host.Client.PatchAsJsonAsync("/api/project", new { satellite = true });
        var ready = (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/plan")).GetProperty("ready");
        Assert.Equal((0, 0), (ready.GetProperty("ortho").GetInt32(), ready.GetProperty("lowZoom.satellite").GetInt32()));   // nothing captured: nothing to run yet
        var checks = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/checks");
        var ids = checks.EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "capture", "game", "render", "preset", "disk" }, ids);    // nothing captured yet: the visit is left
        Assert.False(checks[0].GetProperty("ok").GetBoolean());                       // the capture resource is not on the server yet
        Assert.Equal(JsonValueKind.Null, checks[1].GetProperty("ok").ValueKind);     // no pre-check yet
        // with a map chosen for the minimap, the game's files are needed too: the minimap resource reads the interior maps
        await host.Client.PatchAsJsonAsync("/api/project", new { minimapMap = "satellite" });
        ids = (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/checks")).EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "capture", "game", "render", "preset", "gameFiles", "disk" }, ids);

        await host.Client.PostAsync("/api/project/close", null);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.GetAsync("/api/project")).StatusCode);
        var reopened = await host.Client.PostAsJsonAsync("/api/project/open", new { path = file + ".fxmapgen.json" });
        Assert.Equal(3, (await Json(reopened)).GetProperty("file").GetProperty("parallel").GetInt32());
        await host.Client.PostAsJsonAsync("/api/project/open", new { path = (file + ".fxmapgen.json").Replace('\\', '/') });
        Assert.Single((await host.Client.GetFromJsonAsync<JsonElement>("/api/projects/recent")).EnumerateArray());   // the same file, however written
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync("/api/project/open", new { path = file + "-none.fxmapgen.json" })).StatusCode);
    }

    [Fact]
    public async Task TheGameFilesCheckFollowsTheFoldersFound()
    {
        var gta = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
        var keys = Path.Combine(gta, "keys");
        Directory.CreateDirectory(keys);
        try
        {
            await using var host = await TestHost.StartAsync(o => { o.GtaFolderOverride = gta; o.KeysFolderOverride = keys; });
            await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(host.DataDirectory, "p", "atlas") });
            await host.Client.PatchAsJsonAsync("/api/project", new { satellite = false, atlas = new { enabled = true } });
            async Task<JsonElement> Check() => (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/checks"))
                .EnumerateArray().Single(c => c.GetProperty("id").GetString() == "gameFiles");

            var c = await Check();
            Assert.False(c.GetProperty("ok").GetBoolean());                           // no GTA5.exe, no key files
            Assert.Equal(("app", "0"), (c.GetProperty("values").GetProperty("gtaSource").GetString(), c.GetProperty("values").GetProperty("gtaOk").GetString()));
            Assert.Contains("gtav_aes_key.dat", c.GetProperty("values").GetProperty("keysMissing").GetString());

            File.WriteAllBytes(Path.Combine(gta, "GTA5.exe"), [0]);
            foreach (var f in FxMapGenerator.GameData.Gta.GtaKeys.RequiredFiles) File.WriteAllBytes(Path.Combine(keys, f), [0]);
            c = await Check();
            Assert.True(c.GetProperty("ok").GetBoolean());
            Assert.Equal((keys, "app"), (c.GetProperty("values").GetProperty("keys").GetString(), c.GetProperty("values").GetProperty("keysSource").GetString()));
        }
        finally
        {
            try { Directory.Delete(gta, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TheSettingsScreenEditsTheServerTheConsoleAndThePostalCodes()
    {
        await using var host = await TestHost.StartAsync();
        var dir = Path.Combine(host.DataDirectory, "p");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(dir, "server"), preset = "qbox" });
        static string?[] Names(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()).ToArray();

        var project = await host.Client.GetFromJsonAsync<JsonElement>("/api/project");
        var presetList = Names(project.GetProperty("presetStopResources"));
        Assert.NotEmpty(presetList);
        Assert.Equal(presetList, Names(project.GetProperty("stopResources")));
        Assert.Equal(JsonValueKind.Null, project.GetProperty("file").GetProperty("server").GetProperty("stopResources").ValueKind);

        // the resources stopped during the visit: the project's own list, then back to the preset's; a bad name is refused
        var own = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { stopResources = new { list = new[] { "hud", " chat ", "hud" }, preset = false } }));
        Assert.Equal(new[] { "hud", "chat" }, Names(own.GetProperty("stopResources")));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PatchAsJsonAsync("/api/project", new { stopResources = new { list = new[] { "a b" }, preset = false } })).StatusCode);
        var back = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { stopResources = new { preset = true } }));
        Assert.Equal(JsonValueKind.Null, back.GetProperty("file").GetProperty("server").GetProperty("stopResources").ValueKind);
        Assert.Equal(presetList, Names(back.GetProperty("stopResources")));

        // the console, and the server's own resources (inside the project's folder they are kept relative)
        var console = (await Json(await host.Client.PatchAsJsonAsync("/api/project", new { console = new { host = " 127.0.0.2 ", port = 29201 } }))).GetProperty("file").GetProperty("console");
        Assert.Equal(("127.0.0.2", 29201), (console.GetProperty("host").GetString(), console.GetProperty("port").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PatchAsJsonAsync("/api/project", new { console = new { host = "x", port = 0 } })).StatusCode);
        var gameFiles = (await Json(await host.Client.PatchAsJsonAsync("/api/project", new { serverResources = new[] { Path.Combine(dir, "maps"), @"C:\elsewhere\roads.zip" } })))
            .GetProperty("file").GetProperty("gameFiles");
        Assert.Equal(new[] { "maps", @"C:\elsewhere\roads.zip" }, Names(gameFiles.GetProperty("serverResources")));
        Assert.False(gameFiles.TryGetProperty("gtaFolder", out _));                   // the GTA V and key folders are the app's

        // the postal codes of an atlas: an address not fetched yet is fine, a file is read, a missing file is not
        await host.Client.PatchAsJsonAsync("/api/project", new { atlas = new { enabled = true } });
        async Task<JsonElement> Check(string id) => (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/checks"))
            .EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        var postals = await Check("postals");
        Assert.True(postals.GetProperty("ok").GetBoolean());
        Assert.Equal(("default", "notFetched"), (postals.GetProperty("values").GetProperty("kind").GetString(), postals.GetProperty("values").GetProperty("state").GetString()));
        File.WriteAllText(Path.Combine(dir, "codes.json"), """[{"code": "101", "x": 10.5, "y": -20}]""");
        var file = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { postals = Path.Combine(dir, "codes.json") }));
        Assert.Equal("codes.json", file.GetProperty("file").GetProperty("postals").GetString());
        postals = await Check("postals");
        Assert.Equal(("file", "file", "1"), (postals.GetProperty("values").GetProperty("kind").GetString(), postals.GetProperty("values").GetProperty("state").GetString(),
            postals.GetProperty("values").GetProperty("count").GetString()));
        await host.Client.PatchAsJsonAsync("/api/project", new { postals = Path.Combine(dir, "none.json") });
        Assert.False((await Check("postals")).GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await Json(await host.Client.PatchAsJsonAsync("/api/project", new { postals = "" }))).GetProperty("file").GetProperty("postals").ValueKind);

        // the fonts the atlas styles use, with the maps that use them
        Assert.Contains("atlas-postalcodemap-en", (await Check("fonts")).GetProperty("values").GetProperty("fonts").GetString());
    }

    [Fact]
    public async Task CellsAddedAroundTheMapGiveBlocksAndTilesWithNegativeNumbers()
    {
        await using var host = await TestHost.StartAsync();
        var file = Path.Combine(host.DataDirectory, "wide.fxmapgen.json");
        var p = Project.Create(file, "Wide");
        p.File.Range.ExtraCells = new ExtraCellsSetting { Top = 1, Left = 1 };
        p.Save();
        var opened = await Json(await host.Client.PostAsJsonAsync("/api/project/open", new { path = file }));
        var frame = opened.GetProperty("frame");
        Assert.Equal((-8, -8, 40, 56), (frame.GetProperty("bx0").GetInt32(), frame.GetProperty("by0").GetInt32(), frame.GetProperty("cols").GetInt32(), frame.GetProperty("rows").GetInt32()));
        Assert.Equal((-6390.0, 10650.0), (frame.GetProperty("west").GetDouble(), frame.GetProperty("north").GetDouble()));

        // a block north-west of the standard frame goes into the range; one off the frame does not
        var edited = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { range = new { include = new[] { "z8_-4_-4" } } }));
        Assert.Equal("z8_-4_-4", edited.GetProperty("file").GetProperty("range").GetProperty("add")[0].GetString());
        Assert.Equal(1, (await Json(await host.Client.PatchAsJsonAsync("/api/project", new { range = new { include = new[] { "z8_-36_0" } } })))
            .GetProperty("file").GetProperty("range").GetProperty("add").GetArrayLength());
        var blocks = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/blocks");
        Assert.Equal((-8, -8, 40, 56), (blocks.GetProperty("bx0").GetInt32(), blocks.GetProperty("by0").GetInt32(), blocks.GetProperty("cols").GetInt32(), blocks.GetProperty("rows").GetInt32()));
        var range = blocks.GetProperty("range").GetString()!;
        Assert.Equal(40 * 56, range.Length);
        Assert.Equal('W', range[(-1 + 8) * 40 + (-1 + 8)]);                           // z8_-4_-4 = block (-1, -1)
        Assert.Equal('L', range[(BlockId.Parse("z8_60_128").By + 8) * 40 + BlockId.Parse("z8_60_128").Bx + 8]);   // a land block of the default range
        Assert.Contains("cell_-1_-1", blocks.GetProperty("cells").EnumerateArray().Select(c => c.GetString()));

        // the tiles of the work folder north-west of the origin
        var tile = Path.Combine(host.DataDirectory, "tiles", "satellite", "8", "-4", "-4.png");
        Directory.CreateDirectory(Path.GetDirectoryName(tile)!);
        await File.WriteAllBytesAsync(tile, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/project/tiles/satellite/8/-4/-4.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/tiles/satellite/8/-4/-5.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/tiles/satellite/8/-4/--4.png")).StatusCode);
    }

    [Fact]
    public async Task TheSettingsAddCellsAroundTheMapAndReadCayoPericosRoads()
    {
        await using var host = await TestHost.StartAsync();
        var file = Path.Combine(host.DataDirectory, "f.fxmapgen.json");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file });
        static (int, int, int, int) Sides(JsonElement e) => (e.GetProperty("top").GetInt32(), e.GetProperty("bottom").GetInt32(), e.GetProperty("left").GetInt32(), e.GetProperty("right").GetInt32());
        static (int, int, int, int) Blocks(JsonElement p) => (p.GetProperty("frame").GetProperty("bx0").GetInt32(), p.GetProperty("frame").GetProperty("by0").GetInt32(),
            p.GetProperty("frame").GetProperty("cols").GetInt32(), p.GetProperty("frame").GetProperty("rows").GetInt32());
        static JsonElement Cells(JsonElement p) => p.GetProperty("file").GetProperty("range").GetProperty("extraCells");
        Task<HttpResponseMessage> Patch(object body) => host.Client.PatchAsJsonAsync("/api/project", body);

        var project = await host.Client.GetFromJsonAsync<JsonElement>("/api/project");
        Assert.Equal(JsonValueKind.Null, Cells(project).ValueKind);
        Assert.Equal((0, 0, 0, 0), Sides(project.GetProperty("frameNeeded")));
        Assert.Equal(0, project.GetProperty("rangeOutside").GetInt32());

        // on with nothing added: the standard frame still
        var on = await Json(await Patch(new { extraCells = new { on = true } }));
        Assert.Equal((0, 0, 0, 0), Sides(Cells(on)));
        Assert.Equal((0, 0, 32, 48), Blocks(on));

        // a cell below and one to the right (Cayo Perico); past the limits or below 0 is refused and the file keeps its cells
        var wide = await Json(await Patch(new { extraCells = new { on = true, bottom = 1, right = 1 } }));
        Assert.Equal((0, 1, 0, 1), Sides(Cells(wide)));
        Assert.Equal((0, 0, 40, 56), Blocks(wide));
        var tooMany = await Patch(new { extraCells = new { on = true, top = 1, bottom = 2 } });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("INVALID", Code(await Json(tooMany)));
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new { extraCells = new { on = true, left = 3, right = 2 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new { extraCells = new { on = true, top = -1 } })).StatusCode);
        var kept = Project.Load(file).File.Range.ExtraCells!;
        Assert.Equal((0, 1, 0, 1), (kept.Top, kept.Bottom, kept.Left, kept.Right));

        // a block of the range in the added cells: the frame cannot lose it (a smaller frame or none) until it is out of the range
        var added = await Json(await Patch(new { range = new { include = new[] { "z8_156_220" } } }));
        Assert.Equal((0, 1, 0, 1), Sides(added.GetProperty("frameNeeded")));
        Assert.Equal(1, added.GetProperty("rangeOutside").GetInt32());
        foreach (var smaller in new object[] { new { on = true, bottom = 1 }, new { on = true, right = 1 }, new { on = false } })
        {
            var refused = await Patch(new { extraCells = smaller });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("1 block(s) of the range would lie outside", (await Json(refused)).GetProperty("error").GetProperty("message").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, (await Patch(new { extraCells = new { on = true, top = 1, bottom = 1, right = 1 } })).StatusCode);   // a larger one is fine
        await Patch(new { range = new { exclude = new[] { "z8_156_220" } } });
        var off = await Json(await Patch(new { extraCells = new { on = false } }));
        Assert.Equal(JsonValueKind.Null, Cells(off).ValueKind);
        Assert.Equal((0, 0, 0, 0), Sides(off.GetProperty("frameNeeded")));

        // the island's roads
        Assert.False(off.GetProperty("file").GetProperty("cayoPerico").GetBoolean());
        Assert.True((await Json(await Patch(new { cayoPerico = true }))).GetProperty("file").GetProperty("cayoPerico").GetBoolean());
        Assert.True(Project.Load(file).File.CayoPerico);
    }

    [Fact]
    public async Task RangePresetsGoInOnceTheFrameHoldsThem()
    {
        await using var host = await TestHost.StartAsync();
        var file = Path.Combine(host.DataDirectory, "rp.fxmapgen.json");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file });
        static (int, int, int, int) Sides(JsonElement e) => (e.GetProperty("top").GetInt32(), e.GetProperty("bottom").GetInt32(), e.GetProperty("left").GetInt32(), e.GetProperty("right").GetInt32());
        static JsonElement Preset(JsonElement p, string id) => p.GetProperty("rangePresets").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        static (int, int, int, int, int) Counts(JsonElement x) => (x.GetProperty("land").GetInt32(), x.GetProperty("water").GetInt32(),
            x.GetProperty("missingLand").GetInt32(), x.GetProperty("missingWater").GetInt32(), x.GetProperty("missingOutside").GetInt32());
        Task<HttpResponseMessage> Patch(object body) => host.Client.PatchAsJsonAsync("/api/project", body);

        var project = await host.Client.GetFromJsonAsync<JsonElement>("/api/project");
        Assert.Equal(new[] { "cayoPerico", "roxwood" }, project.GetProperty("rangePresets").EnumerateArray().Select(x => x.GetProperty("id").GetString()));
        var cayo = Preset(project, "cayoPerico");
        Assert.Equal((31, 87, 31, 87, 88), Counts(cayo));
        Assert.Equal((0, 1, 0, 1), Sides(cayo.GetProperty("needs")));
        Assert.Equal((3172.5, -3412.5, 6266.25, -6787.5), (cayo.GetProperty("west").GetDouble(), cayo.GetProperty("north").GetDouble(),
            cayo.GetProperty("east").GetDouble(), cayo.GetProperty("south").GetDouble()));
        var rox = Preset(project, "roxwood");
        Assert.Equal((138, 103, 97, 78, 62), Counts(rox));
        Assert.Equal((1, 0, 0, 0), Sides(rox.GetProperty("needs")));

        // outside the frame: refused, nothing changes
        var refused = await Patch(new { range = new { preset = "cayoPerico" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("lie outside the map's frame", (await Json(refused)).GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(Project.Load(file).File.Range.Add);

        // the frame widened and the preset in, in one change; its blocks count as land and water by the preset
        var added = await Json(await Patch(new { extraCells = new { on = true, bottom = 1, right = 1 }, range = new { preset = "cayoPerico" } }));
        Assert.Equal((1163, 705, 458), (added.GetProperty("rangeBlocks").GetInt32(), added.GetProperty("rangeLand").GetInt32(), added.GetProperty("rangeWater").GetInt32()));
        Assert.Equal((31, 87, 0, 0, 0), Counts(Preset(added, "cayoPerico")));
        Assert.Equal(118, added.GetProperty("file").GetProperty("range").GetProperty("add").GetArrayLength());
        var blocks = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/blocks");
        var land = BlockId.Parse("z8_124_180");
        Assert.Equal('L', blocks.GetProperty("range").GetString()![(land.By - blocks.GetProperty("by0").GetInt32()) * blocks.GetProperty("cols").GetInt32() + land.Bx - blocks.GetProperty("bx0").GetInt32()]);

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new { range = new { preset = "atlantis" } })).StatusCode);
    }

    [Fact]
    public async Task APresetWideningTheFrameWaitsForTheGameFiles()
    {
        using var release = new ManualResetEventSlim();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IBuildStages>(new HoldBuild(release, "gameFiles")));
        var file = Path.Combine(host.DataDirectory, "rw.fxmapgen.json");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file });
        await host.Client.PatchAsJsonAsync("/api/project", new { extraCells = new { on = true } });
        await host.Client.PostAsJsonAsync("/api/jobs", new { project = file });

        var locked = await host.Client.PatchAsJsonAsync("/api/project", new { extraCells = new { on = true, bottom = 1, right = 1 }, range = new { preset = "cayoPerico" } });
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Equal("LOCKED", Code(await Json(locked)));
        Assert.Empty(Project.Load(file).File.Range.Add);

        release.Set();
        await host.App.Services.GetRequiredService<JobManager>().WaitAsync();
        var project = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { extraCells = new { on = true, bottom = 1, right = 1 }, range = new { preset = "cayoPerico" } }));
        Assert.Equal(1163, project.GetProperty("rangeBlocks").GetInt32());
    }

    [Fact]
    public async Task TheFrameAndCayoPericoWaitForTheStepsReadingThem()
    {
        using var release = new ManualResetEventSlim();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IBuildStages>(new HoldBuild(release, "gameFiles")));
        var file = Path.Combine(host.DataDirectory, "g.fxmapgen.json");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file });
        await host.Client.PostAsJsonAsync("/api/jobs", new { project = file });

        // the game files read the frame (their areas) and the Cayo Perico choice
        foreach (var edit in new object[] { new { extraCells = new { on = true, bottom = 1 } }, new { cayoPerico = true } })
        {
            var locked = await host.Client.PatchAsJsonAsync("/api/project", edit);
            Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
            Assert.Equal("LOCKED", Code(await Json(locked)));
        }
        // switched on with nothing added the frame stays as it is, and the game files do not read the range
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { extraCells = new { on = true } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { range = new { exclude = new[] { "z8_48_8" } } })).StatusCode);

        release.Set();
        await host.App.Services.GetRequiredService<JobManager>().WaitAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { cayoPerico = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { extraCells = new { on = true, bottom = 1 } })).StatusCode);
    }

    [Fact]
    public async Task CayoPericoAlsoWaitsForTheLabels()
    {
        using var release = new ManualResetEventSlim();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IBuildStages>(new HoldBuild(release, "mapData.labels")));
        var file = Path.Combine(host.DataDirectory, "c.fxmapgen.json");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file });
        await host.Client.PostAsJsonAsync("/api/jobs", new { project = file });

        // the labels place the island's zone name with the choice; they read neither the server's resource folders nor the frame
        var locked = await host.Client.PatchAsJsonAsync("/api/project", new { cayoPerico = true });
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Equal("LOCKED", Code(await Json(locked)));
        Assert.False(Project.Load(file).File.CayoPerico);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { serverResources = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { extraCells = new { on = true, bottom = 1 } })).StatusCode);

        release.Set();
        await host.App.Services.GetRequiredService<JobManager>().WaitAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { cayoPerico = true })).StatusCode);
        Assert.True(Project.Load(file).File.CayoPerico);
    }

    [Fact]
    public async Task TilesComeFromTheWorkFolder()
    {
        await using var host = await TestHost.StartAsync();
        var file = Path.Combine(host.DataDirectory, "t.fxmapgen.json");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file });
        var tile = Path.Combine(host.DataDirectory, "tiles", "satellite", "3", "1", "2.png");
        Directory.CreateDirectory(Path.GetDirectoryName(tile)!);
        await File.WriteAllBytesAsync(tile, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var ok = await host.Client.GetAsync("/api/project/tiles/satellite/3/1/2.png?p=k&v=1");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/png", ok.Content.Headers.ContentType!.MediaType);
        // the same address serves whichever project is open and tiles change while a run goes on: always ask again
        Assert.True(ok.Headers.CacheControl!.NoCache);
        var tag = ok.Headers.ETag!;
        using (var again = new HttpRequestMessage(HttpMethod.Get, "/api/project/tiles/satellite/3/1/2.png?p=k&v=2"))
        {
            again.Headers.IfNoneMatch.Add(tag);
            Assert.Equal(HttpStatusCode.NotModified, (await host.Client.SendAsync(again)).StatusCode);
        }
        await File.WriteAllBytesAsync(tile, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D });
        using (var changed = new HttpRequestMessage(HttpMethod.Get, "/api/project/tiles/satellite/3/1/2.png"))
        {
            changed.Headers.IfNoneMatch.Add(tag);
            Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(changed)).StatusCode);
        }
        var missing = await host.Client.GetAsync("/api/project/tiles/satellite/3/1/3.png");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.True(missing.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/tiles/nothing/3/1/2.png")).StatusCode);
    }

    [Fact]
    public async Task TheMapBeforeComesFromTheKeptTilesAndTheChangesAreListed()
    {
        await using var host = await TestHost.StartAsync();
        var file = Path.Combine(host.DataDirectory, "b.fxmapgen.json");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file });
        Assert.Equal(0, (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/before")).GetProperty("maps").GetArrayLength());

        // a z8 tile written over (the first bytes kept) and one written the first time (nothing kept)
        var tiles = FxMapGenerator.Core.Satellite.TileStore.Keeping(new WorkFolder(host.DataDirectory), "satellite");
        tiles.WriteBytes(8, 10, 20, [1, 2, 3]);
        tiles.WriteBytes(8, 10, 20, [4, 5, 6]);
        tiles.WriteBytes(8, 11, 20, [7, 8, 9]);
        var before = await (await host.Client.GetAsync("/api/project/before-tiles/satellite/8/10/20.png")).Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 1, 2, 3 }, before);
        Assert.Equal(new byte[] { 4, 5, 6 }, await (await host.Client.GetAsync("/api/project/tiles/satellite/8/10/20.png")).Content.ReadAsByteArrayAsync());
        Assert.Equal(new byte[] { 7, 8, 9 }, await (await host.Client.GetAsync("/api/project/before-tiles/satellite/8/11/20.png")).Content.ReadAsByteArrayAsync());   // not written over: as it is
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/before-tiles/satellite/8/12/20.png")).StatusCode);

        var maps = (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/before")).GetProperty("maps");
        var sat = Assert.Single(maps.EnumerateArray());
        Assert.Equal(("satellite", 1), (sat.GetProperty("map").GetString(), sat.GetProperty("tiles").GetInt32()));
        Assert.Equal(new[] { 10, 20 }, sat.GetProperty("z8").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(1, sat.GetProperty("areas")[0].GetProperty("tiles").GetInt32());
    }

    /// <summary>A stage of a to-do row (by default the ortho, which reads the range) held running until released.</summary>
    sealed class HoldStage(ManualResetEventSlim release, string row = "ortho") : Stage
    {
        public override string Id => "hold";
        public override string Row => row;
        public override IReadOnlyList<string> Prepare(StageContext ctx) => new[] { "z8_48_8" };
        public override void Run(UnitContext ctx) => release.Wait(ctx.Token);
        public override int CountReady(Project project, StateStore state) => 1;
    }

    sealed class HoldBuild(ManualResetEventSlim release, string row = "ortho") : IBuildStages
    {
        public BuildPlan For(Project project, StateStore state, BuildStages.Options options) => new(new Stage[] { new HoldStage(release, row) }, Array.Empty<string>());
    }

    [Fact]
    public async Task BlocksOfTheRangeAreMarkedForRetake()
    {
        await using var host = await TestHost.StartAsync();
        var file = Path.Combine(host.DataDirectory, "t.fxmapgen.json");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file });
        static int At(string name) => BlockId.Parse(name).By * 32 + BlockId.Parse(name).Bx;

        var marked = await host.Client.PostAsJsonAsync("/api/project/retake", new { blocks = new[] { "z8_48_8", "z8_52_8" } });
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        Assert.Equal(file, (await Json(marked)).GetProperty("path").GetString());
        var retake = (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/blocks")).GetProperty("retake").GetString()!;
        Assert.Equal(2, retake.Count(c => c == '1'));
        Assert.Equal('1', retake[At("z8_52_8")]);

        await host.Client.PostAsJsonAsync("/api/project/retake", new { blocks = new[] { "z8_52_8" }, retake = false });
        retake = (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/blocks")).GetProperty("retake").GetString()!;
        Assert.Equal(('1', '0'), (retake[At("z8_48_8")], retake[At("z8_52_8")]));

        var outside = await host.Client.PostAsJsonAsync("/api/project/retake", new { blocks = new[] { "z8_0_0" } });   // open sea, not in the range
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Equal("INVALID", Code(await Json(outside)));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/project/retake", new { blocks = new[] { "z8_1_1" } })).StatusCode);

        await host.Client.PostAsJsonAsync("/api/project/retake", new { all = true });
        retake = (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/blocks")).GetProperty("retake").GetString()!;
        Assert.DoesNotContain('1', retake);
        Assert.Empty(StateStore.Open(new WorkFolder(host.DataDirectory)).MarkedForRetake());
    }

    [Fact]
    public async Task InputsARunningStageReadsCannotBeEdited()
    {
        using var release = new ManualResetEventSlim();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IBuildStages>(new HoldBuild(release)));
        var file = Path.Combine(host.DataDirectory, "l.fxmapgen.json");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file });
        await host.Client.PostAsJsonAsync("/api/jobs", new { project = file });

        var range = await host.Client.PatchAsJsonAsync("/api/project", new { range = new { exclude = new[] { "z8_48_8" } } });
        Assert.Equal(HttpStatusCode.Conflict, range.StatusCode);
        Assert.Equal("LOCKED", Code(await Json(range)));
        var retake = await host.Client.PostAsJsonAsync("/api/project/retake", new { blocks = new[] { "z8_48_8" } });   // the visit reads the marks with the range
        Assert.Equal(HttpStatusCode.Conflict, retake.StatusCode);
        Assert.Equal("LOCKED", Code(await Json(retake)));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { minimapMap = "satellite" })).StatusCode);   // not read by the run
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { extraCells = new { on = true, bottom = 1 } })).StatusCode);   // the frame is not the range

        // the stage finds its units on the run's own thread, a moment after the start: wait for them
        var units = await host.Client.GetFromJsonAsync<JsonElement>("/api/jobs/current/units");
        for (int i = 0; i < 250 && units[0].GetProperty("targets").GetArrayLength() == 0; i++)
        {
            await Task.Delay(20);
            units = await host.Client.GetFromJsonAsync<JsonElement>("/api/jobs/current/units");
        }
        Assert.Equal("z8_48_8", units[0].GetProperty("targets")[0].GetString());

        release.Set();
        await host.App.Services.GetRequiredService<JobManager>().WaitAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PatchAsJsonAsync("/api/project", new { range = new { exclude = new[] { "z8_48_8" } } })).StatusCode);
    }
}
