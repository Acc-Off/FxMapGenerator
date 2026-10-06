using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxMapGenerator.App.Cli;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Web;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using Microsoft.Extensions.DependencyInjection;

namespace FxMapGenerator.App.Tests;

public sealed class ExportApiTests
{
    static readonly DateTime T0 = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A project with two satellite tiles and the minimap dictionaries (stand-ins) made.</summary>
    static string NewProject(string folder)
    {
        var p = Project.Create(Path.Combine(folder, "proj", "exp.fxmapgen.json"), "Export test");
        p.File.Range.Base = "none";
        p.File.Minimap.Map = "satellite";
        p.Save();
        var work = new WorkFolder(p.WorkFolderPath);
        var tiles = new TileStore(work.Tiles("satellite"));
        var png = TileStore.EncodePng(new byte[256 * 256 * 4], 256, 256);
        tiles.WriteBytes(8, 10, 20, png);
        tiles.WriteBytes(0, 0, 0, png);
        var ytd = work.Minimap("satellite");
        Directory.CreateDirectory(ytd);
        foreach (var s in MinimapSheets.All)
            foreach (var n in new[] { s.SeaTexture, s.Texture }) File.WriteAllBytes(Path.Combine(ytd, n + ".ytd"), [1]);
        File.WriteAllBytes(Path.Combine(ytd, MinimapLod.Texture + ".ytd"), [1]);
        var state = StateStore.Open(work);
        state.SetStageDone(StageKeys.LowZoom, "satellite", T0);
        state.SetStageDone(StageKeys.Ytd, MinimapSheets.All.Select(s => StageKeys.YtdUnit("satellite", s.Name)).Append(StageKeys.YtdUnit("satellite", MinimapLod.Unit)),
            T0.AddMinutes(1));
        MinimapStage.WriteRecord(p, "satellite");
        return p.FilePath;
    }

    static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task InfoCheckExportAndTheChoicesAreKept()
    {
        await using var host = await TestHost.StartAsync();
        var file = NewProject(host.DataDirectory);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/project/open", new { path = file })).StatusCode);

        var info = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export");
        Assert.Equal("satellite", info.GetProperty("maps")[0].GetProperty("map").GetString());
        Assert.Equal(2, info.GetProperty("maps")[0].GetProperty("tiles").GetInt32());
        Assert.True(info.GetProperty("minimap").GetProperty("ready").GetBoolean());
        var folder = Path.Combine(Path.GetDirectoryName(file)!, "export");
        Assert.Equal(folder, info.GetProperty("options").GetProperty("folder").GetString());
        Assert.StartsWith(ExportOptions.ResourcePrefix, info.GetProperty("options").GetProperty("resourceName").GetString());
        Assert.Equal(0, info.GetProperty("problems").GetArrayLength());

        var bad = await host.Client.PostAsJsonAsync("/api/project/export/check", new { baseUrl = "not a url", maps = Array.Empty<string>(), minimap = false });
        var codes = (await Json(bad)).GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Equal(new[] { "NOTHING", "BAD_URL" }, codes);
        var refused = await host.Client.PostAsJsonAsync("/api/project/export", new { maps = Array.Empty<string>(), minimap = false });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("EXPORT_NOTHING", (await Json(refused)).GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(8, info.GetProperty("options").GetProperty("maxZoom").GetInt32());
        Assert.Equal(1, info.GetProperty("maps")[0].GetProperty("tilesPerZoom")[8].GetInt32());
        var zoom = await Json(await host.Client.PostAsJsonAsync("/api/project/export/check", new { maxZoom = 5 }));
        Assert.Equal("BAD_ZOOM", zoom.GetProperty("problems")[0].GetProperty("code").GetString());

        var started = await host.Client.PostAsJsonAsync("/api/project/export", new { folder = "out", zip = true, baseUrl = "https://maps.example.net", resourceName = "my-minimap-v2", maxZoom = 7 });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var jobs = host.App.Services.GetRequiredService<JobManager>();
        await jobs.WaitAsync();
        Assert.Equal("done", (await host.Client.GetFromJsonAsync<JsonElement>("/api/jobs/current")).GetProperty("state").GetString());
        var outDir = Path.Combine(Path.GetDirectoryName(file)!, "out");
        Assert.True(File.Exists(Path.Combine(outDir, "web.zip")));
        Assert.True(File.Exists(Path.Combine(outDir, "my-minimap-v2", "fxmanifest.lua")));
        // the resource carries the game's interior maps (here the ones the host gives in place of a game)
        Assert.Equal(InteriorMaps.Lua(TestHost.Interiors, AppVersion.Value), File.ReadAllText(Path.Combine(outDir, "my-minimap-v2", "interiors.lua")));

        // kept for the next time: the folder (relative to the project file), zip, the address, the zoom
        var saved = Project.Load(file).File.Export;
        Assert.Equal(("out", true, "https://maps.example.net", true, 7), (saved.Folder, saved.Zip, saved.BaseUrl, saved.Minimap, saved.MaxZoom));
        var again = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export");
        Assert.Equal(outDir, again.GetProperty("options").GetProperty("folder").GetString());
        Assert.Equal(7, again.GetProperty("options").GetProperty("maxZoom").GetInt32());
        Assert.Equal(7, again.GetProperty("last").GetProperty("web").GetProperty("maxZoom").GetInt32());
        Assert.Equal("my-minimap-v2", again.GetProperty("last").GetProperty("resources")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task WithoutGtaVOrTheKeysTheMinimapResourceIsRefused()
    {
        // a PC's own folders: no GTA V there, then a folder that passes for it and no keys
        var scratch = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
        var gta = Path.Combine(scratch, "gta");
        Directory.CreateDirectory(gta);
        try
        {
            await using var host = await TestHost.StartAsync(o => { o.MinimapGameFiles = null; o.GtaFolderOverride = gta; o.KeysFolderOverride = Path.Combine(scratch, "nokeys"); });
            var file = NewProject(host.DataDirectory);
            Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/project/open", new { path = file })).StatusCode);

            var info = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export");
            Assert.Equal("gta", info.GetProperty("minimap").GetProperty("game").GetString());
            Assert.Equal(new[] { "MINIMAP_NO_GTA" }, info.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()));
            var refused = await host.Client.PostAsJsonAsync("/api/project/export", new { folder = "out" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("EXPORT_MINIMAP_NO_GTA", (await Json(refused)).GetProperty("error").GetProperty("code").GetString());
            // the web tiles alone need no game
            var tiles = await Json(await host.Client.PostAsJsonAsync("/api/project/export/check", new { minimap = false }));
            Assert.Equal(0, tiles.GetProperty("problems").GetArrayLength());

            File.WriteAllBytes(Path.Combine(gta, "GTA5.exe"), [0]);
            info = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export");
            Assert.Equal("keys", info.GetProperty("minimap").GetProperty("game").GetString());
            Assert.Equal(new[] { "MINIMAP_NO_KEYS" }, info.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()));
            // a conversion that writes the resource says the same
            var convert = await Json(await host.Client.PostAsJsonAsync("/api/project/convert/check", new { file = Path.Combine(scratch, "none.png") }));
            Assert.Contains("MINIMAP_NO_KEYS", convert.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ALayeredFileIsWrittenOnlyWhenTheExportAsksForIt()
    {
        await using var host = await TestHost.StartAsync();
        var file = NewProject(host.DataDirectory);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/project/open", new { path = file })).StatusCode);

        // what can be written in layers, the frame's blocks, and nothing chosen
        var info = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export");
        var map = info.GetProperty("editable").GetProperty("maps")[0];
        Assert.Equal(("satellite", true), (map.GetProperty("map").GetString(), map.GetProperty("ready").GetBoolean()));
        Assert.Equal((32, 48), (info.GetProperty("editable").GetProperty("blocksX").GetInt32(), info.GetProperty("editable").GetProperty("blocksY").GetInt32()));
        var options = info.GetProperty("options");
        Assert.Equal((0, 6, "psd"), (options.GetProperty("editableMaps").GetArrayLength(), options.GetProperty("editableZoom").GetInt32(), options.GetProperty("editableFormats")[0].GetString()));

        var bad = await Json(await host.Client.PostAsJsonAsync("/api/project/export/check", new { editableMaps = new[] { "satellite", "roadmap" }, editableZoom = 9 }));
        Assert.Equal(new[] { "EDITABLE_NOT_READY", "BAD_EDITABLE" }, bad.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()));
        Assert.Equal(2, bad.GetProperty("options").GetProperty("editableMaps").GetArrayLength());
        // a layered file alone is something to write
        var alone = await Json(await host.Client.PostAsJsonAsync("/api/project/export/check", new { maps = Array.Empty<string>(), minimap = false, editableMaps = new[] { "satellite" } }));
        Assert.Equal(0, alone.GetProperty("problems").GetArrayLength());

        var started = await host.Client.PostAsJsonAsync("/api/project/export",
            new { maps = Array.Empty<string>(), minimap = false, editableMaps = new[] { "satellite" }, editableZoom = 7, language = "ja" });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var jobs = host.App.Services.GetRequiredService<JobManager>();
        await jobs.WaitAsync();
        var job = await host.Client.GetFromJsonAsync<JsonElement>("/api/jobs/current");
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal(new[] { "export", "export.files" }, job.GetProperty("stages").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        var folder = Path.Combine(Path.GetDirectoryName(file)!, "export");
        var psd = Path.Combine(folder, "editable", "satellite-z7.psd");
        Assert.True(File.Exists(psd));
        Assert.Equal("8BPS"u8.ToArray(), File.ReadAllBytes(psd)[..4]);

        // the zoom level is kept, the maps are not: the next export writes no layered file unless it asks again
        Assert.Equal(7, Project.Load(file).File.Export.Editable.Zoom);
        var again = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export");
        Assert.Equal((0, 7), (again.GetProperty("options").GetProperty("editableMaps").GetArrayLength(), again.GetProperty("options").GetProperty("editableZoom").GetInt32()));
        var written = again.GetProperty("last").GetProperty("editable")[0];
        Assert.Equal(("satellite-z7.psd", "satellite", 7, 512 * 32, 512 * 48, "衛星地図"),
            (written.GetProperty("file").GetString(), written.GetProperty("map").GetString(), written.GetProperty("zoom").GetInt32(),
                written.GetProperty("width").GetInt32(), written.GetProperty("height").GetInt32(), written.GetProperty("layers")[0].GetString()));
        File.Delete(psd);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/project/export", new { maps = new[] { "satellite" }, minimap = false })).StatusCode);
        await jobs.WaitAsync();
        Assert.False(File.Exists(psd));
        // the record no longer lists the file that is gone
        Assert.Equal(0, (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export")).GetProperty("last").GetProperty("editable").GetArrayLength());

        // the formats: none chosen for a map is refused; both at once write a PSD file and an SVG file with its pictures
        var none = new { maps = Array.Empty<string>(), minimap = false, editableMaps = new[] { "satellite" }, editableFormats = Array.Empty<string>() };
        var unchosen = await Json(await host.Client.PostAsJsonAsync("/api/project/export/check", none));
        Assert.Equal(new[] { "NO_EDITABLE_FORMAT" }, unchosen.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()));
        Assert.Equal(0, unchosen.GetProperty("options").GetProperty("editableFormats").GetArrayLength());
        var refused = await host.Client.PostAsJsonAsync("/api/project/export", none);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("EXPORT_NO_EDITABLE_FORMAT", (await Json(refused)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/project/export",
            new { maps = Array.Empty<string>(), minimap = false, editableMaps = new[] { "satellite" }, editableZoom = 6, editableFormats = new[] { "psd", "svg" } })).StatusCode);
        await jobs.WaitAsync();
        Assert.Equal("done", (await host.Client.GetFromJsonAsync<JsonElement>("/api/jobs/current")).GetProperty("state").GetString());
        var editable = Path.Combine(folder, "editable");
        Assert.True(File.Exists(Path.Combine(editable, "satellite-z6.psd")));
        Assert.StartsWith("<?xml", File.ReadAllText(Path.Combine(editable, "satellite-z6.svg")));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(Path.Combine(editable, "satellite-z6-svg", "satellite.png"))[..4]);
        // the formats are kept with the zoom level, and the record tells the files apart by their format
        Assert.Equal(new[] { "psd", "svg" }, Project.Load(file).File.Export.Editable.Formats);
        var kept = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export");
        Assert.Equal(new[] { "psd", "svg" }, kept.GetProperty("options").GetProperty("editableFormats").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(new[] { ("psd", "satellite-z6.psd"), ("svg", "satellite-z6.svg") },
            kept.GetProperty("last").GetProperty("editable").EnumerateArray().Select(e => (e.GetProperty("format").GetString()!, e.GetProperty("file").GetString()!)).Order());
    }

    [Fact]
    public void TheCommandLineExportsWithTheSavedOrGivenChoices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var file = NewProject(dir);
            var o = new StringWriter();
            var e = new StringWriter();
            var outDir = Path.Combine(dir, "cli-out");
            Assert.Equal(0, CliCommands.Run(["export", file, "--out", outDir, "--no-minimap", "--workers", "2"], o, e));
            Assert.True(File.Exists(Path.Combine(outDir, "web", "tiles", "satellite", "8", "10", "20.png")));
            Assert.False(Directory.EnumerateDirectories(outDir, "fxmapgen-minimap-*").Any());
            Assert.Contains("export written", o.ToString());

            Assert.Equal(0, CliCommands.Run(["export", file, "--out", outDir, "--no-minimap", "--max-zoom", "7"], new StringWriter(), e));
            Assert.False(Directory.Exists(Path.Combine(outDir, "web", "tiles", "satellite", "8")));
            Assert.True(File.Exists(Path.Combine(outDir, "web", "tiles", "satellite", "0", "0", "0.png")));
            Assert.Equal(64, CliCommands.Run(["export", file, "--out", outDir, "--max-zoom", "high"], new StringWriter(), new StringWriter()));

            // a layered file for the maps of --editable, and only then
            var psd = Path.Combine(outDir, "editable", "satellite-z6.psd");
            Assert.False(File.Exists(psd));
            Assert.Equal(0, CliCommands.Run(["export", file, "--out", outDir, "--no-minimap", "--maps", "none", "--editable", "satellite"], new StringWriter(), e));
            Assert.True(File.Exists(psd));
            Assert.True(Directory.Exists(Path.Combine(outDir, "web")));           // an export that writes no web tiles leaves the earlier ones
            File.Delete(psd);
            Assert.Equal(0, CliCommands.Run(["export", file, "--out", outDir, "--no-minimap"], new StringWriter(), e));
            Assert.False(File.Exists(psd));
            // the formats of --editable-format: an SVG file with the folder of its pictures, or both
            var svg = Path.Combine(outDir, "editable", "satellite-z6.svg");
            Assert.Equal(0, CliCommands.Run(["export", file, "--out", outDir, "--no-minimap", "--maps", "none", "--editable", "satellite", "--editable-format", "svg"], new StringWriter(), e));
            Assert.True(File.Exists(svg));
            Assert.True(File.Exists(Path.Combine(outDir, "editable", "satellite-z6-svg", "satellite.png")));
            Assert.False(File.Exists(psd));
            File.Delete(svg);
            Assert.Equal(0, CliCommands.Run(["export", file, "--out", outDir, "--no-minimap", "--maps", "none", "--editable", "satellite", "--editable-format", "psd,svg"], new StringWriter(), e));
            Assert.True(File.Exists(svg) && File.Exists(psd));
            var unknown = new StringWriter();
            Assert.Equal(65, CliCommands.Run(["export", file, "--out", outDir, "--no-minimap", "--maps", "none", "--editable", "satellite", "--editable-format", "tiff"], unknown, e));
            Assert.Contains("'tiff' is not a format of the editable files (psd, svg)", unknown.ToString());
            Assert.Equal(64, CliCommands.Run(["export", file, "--out", outDir, "--editable-zoom", "7"], new StringWriter(), new StringWriter()));
            Assert.Equal(64, CliCommands.Run(["export", file, "--out", outDir, "--editable", "satellite", "--editable-zoom", "fine"], new StringWriter(), new StringWriter()));
            var notReady = new StringWriter();
            Assert.Equal(65, CliCommands.Run(["export", file, "--out", outDir, "--no-minimap", "--editable", "roadmap", "--editable-zoom", "5"], notReady, e));
            Assert.Contains("cannot be written in layers", notReady.ToString());
            Assert.Contains("zoom level 5", notReady.ToString());

            File.WriteAllText(Path.Combine(dir, "other.txt"), "x");
            var refused = new StringWriter();
            Assert.Equal(65, CliCommands.Run(["export", file, "--out", dir, "--maps", "none"], refused, e));
            Assert.Contains("cannot export", refused.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
