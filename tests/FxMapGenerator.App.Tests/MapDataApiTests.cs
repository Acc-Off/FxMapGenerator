using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.App.Tests;

/// <summary>The screens of the atlas and the road map: choosing them, the game files' folders, the prerequisites, a clicked point.</summary>
public sealed class MapDataApiTests
{
    static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task TheAtlasAndTheRoadMapAreChosenLikeTheSatelliteMap()
    {
        await using var host = await TestHost.StartAsync();
        var created = await Json(await host.Client.PostAsJsonAsync("/api/project/new",
            new { path = Path.Combine(host.DataDirectory, "p", "maps"), satellite = false, atlas = true, roadmap = true }));
        var maps = created.GetProperty("file").GetProperty("maps");
        Assert.Equal((false, true, true), (maps.GetProperty("satellite").GetBoolean(), maps.GetProperty("atlas").GetProperty("enabled").GetBoolean(),
            maps.GetProperty("roadmap").GetBoolean()));
        var off = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { roadmap = false }));
        Assert.False(off.GetProperty("file").GetProperty("maps").GetProperty("roadmap").GetBoolean());
        Assert.Equal(new[] { "atlas-postalcodemap-en" }, off.GetProperty("maps").EnumerateArray().Select(m => m.GetString()));

        // the prerequisites of the atlas: the game files and the server's own road data (optional, none set: fine)
        var checks = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/checks");
        var server = checks.EnumerateArray().Single(c => c.GetProperty("id").GetString() == "serverResources");
        Assert.True(server.GetProperty("ok").GetBoolean());
        Assert.Equal("0", server.GetProperty("values").GetProperty("count").GetString());
        Assert.Contains(checks.EnumerateArray(), c => c.GetProperty("id").GetString() == "gameFiles");

        // the job list shows the game files with their two parts
        var plan = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/plan?workers=2");
        var gameFiles = plan.GetProperty("table").GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "gameFiles");
        Assert.Equal(new[] { "gameFiles.paths", "gameFiles.names" }, gameFiles.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task TheHeightQualityFollowsTheMapsUnlessChosen()
    {
        await using var host = await TestHost.StartAsync();
        static string? Quality(JsonElement project) => project.GetProperty("file").GetProperty("heightQuality") is { ValueKind: JsonValueKind.String } q ? q.GetString() : null;
        async Task<JsonElement> Patch(object edit) => await Json(await host.Client.PatchAsJsonAsync("/api/project", edit));
        var created = await Json(await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(host.DataDirectory, "p", "quality"), satellite = true }));
        Assert.Null(Quality(created));                                                   // not chosen: the default for the maps
        Assert.Equal("balance", Quality(await Patch(new { heightQuality = "balance" })));
        Assert.Equal("balance", Quality(await Patch(new { roadmap = true })));             // still goes with a road map: kept
        var bad = await host.Client.PatchAsJsonAsync("/api/project", new { heightQuality = "speed" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("does not go with an atlas or road map", await bad.Content.ReadAsStringAsync());
        Assert.Equal("quality", Quality(await Patch(new { heightQuality = "quality" })));
        Assert.Equal("quality", Quality(await Patch(new { satellite = false })));            // quality goes with a road map alone: kept
        Assert.Equal("balance", Quality(await Patch(new { satellite = true, heightQuality = "balance" })));
        Assert.Null(Quality(await Patch(new { heightQuality = "" })));                       // "" = the default again
    }

    [Fact]
    public async Task TheSettingsDialogSeesWhatTheFoldersGive()
    {
        var gta = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
        var keys = Path.Combine(gta, "keys");
        Directory.CreateDirectory(keys);
        try
        {
            await using var host = await TestHost.StartAsync();
            async Task<JsonElement> Status() => await host.Client.GetFromJsonAsync<JsonElement>(
                $"/api/settings/gamefiles?gta={Uri.EscapeDataString(gta)}&keys={Uri.EscapeDataString(keys)}");
            var s = await Status();
            Assert.Equal((false, "notGta", false), (s.GetProperty("gtaFound").GetBoolean(), s.GetProperty("gtaProblem").GetString(), s.GetProperty("keysFound").GetBoolean()));
            var tried = s.GetProperty("keysTried").EnumerateArray().Single();          // the folder given is the only one tried
            Assert.Equal((keys, "app"), (tried.GetProperty("folder").GetString(), tried.GetProperty("source").GetString()));
            Assert.Contains("gtav_aes_key.dat", tried.GetProperty("missing").EnumerateArray().Select(m => m.GetString()));

            File.WriteAllBytes(Path.Combine(gta, "GTA5.exe"), [0]);
            foreach (var f in FxMapGenerator.GameData.Gta.GtaKeys.RequiredFiles) File.WriteAllBytes(Path.Combine(keys, f), [0]);
            s = await Status();
            Assert.Equal((true, "app", true, "app"), (s.GetProperty("gtaFound").GetBoolean(), s.GetProperty("gtaSource").GetString(),
                s.GetProperty("keysFound").GetBoolean(), s.GetProperty("keysSource").GetString()));
            Assert.Equal(JsonValueKind.Null, s.GetProperty("gtaProblem").ValueKind);
            Assert.False(s.GetProperty("gtaFromCommandLine").GetBoolean());

            // saved with the settings, the prerequisites of a project take them
            var settings = await host.Client.GetFromJsonAsync<JsonElement>("/api/settings");
            var put = JsonSerializer.Deserialize<Dictionary<string, object?>>(settings.GetRawText())!;
            put["gtaFolder"] = gta;
            put["keysFolder"] = keys;
            Assert.Equal(HttpStatusCode.OK, (await host.Client.PutAsJsonAsync("/api/settings", put)).StatusCode);
            await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(host.DataDirectory, "p", "roads"), satellite = false, roadmap = true });
            var c = (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/checks")).EnumerateArray().Single(x => x.GetProperty("id").GetString() == "gameFiles");
            Assert.True(c.GetProperty("ok").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(gta, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AClickedPointShowsTheScanAndTheLandcover()
    {
        await using var host = await TestHost.StartAsync();
        var file = Path.Combine(host.DataDirectory, "p", "point");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = file, satellite = false, roadmap = true });
        var folder = new WorkFolder(Path.Combine(host.DataDirectory, "p"));
        var b = BlockId.Parse("z8_60_132");
        var (x0, y0, _, _) = b.Rect;
        Directory.CreateDirectory(folder.Scan);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            string.Format(inv, "MSCAN BEGIN v=1 kind=mat seq=1 block={0} z=8 tx=60 ty=132 x0={1:F4} y0={2:F4} size=281.2500 step=1.000 n=282 flags=1 fol=1 chunks=2 pflags=128", b.Name, x0, y0),
            "MSCAN dict mat 1 282940568",
        };
        for (int j = 0; j < 282; j++)
            lines.AddRange([$"MSCAN mat j={j} k=0 1*282", $"MSCAN hz j={j} k=0 40.0*282", $"MSCAN water j={j} k=0 .*282", $"MSCAN fol j={j} k=0 x*282"]);
        lines.Add(string.Format(inv, "MSCAN BEGIN v=1 kind=road seq=2 block={0} z=8 tx=60 ty=132 x0={1:F4} y0={2:F4} size=281.2500 step=4.000 n=71 pstep=1.000 pn=282", b.Name, x0, y0));
        lines.AddRange(["MSCAN dict street 1 -1234567 Fake St", "MSCAN dict zone 1 LEGSQU Legion Square"]);
        for (int j = 0; j < 71; j++) lines.AddRange([$"MSCAN street j={j} k=0 1*71", $"MSCAN zone j={j} k=0 1*71"]);
        for (int j = 0; j < 282; j++) lines.Add($"MSCAN onroad j={j} k=0 {(j < 10 ? 1 : 0)}*282");
        File.WriteAllLines(folder.ScanFile(b), lines);
        // a landcover whose north-west quarter is a building on urban ground
        var f = new GridFile();
        f.Meta["x0"] = x0; f.Meta["y0"] = y0; f.Meta["step"] = 1.0;
        f.Meta["classes"] = new System.Text.Json.Nodes.JsonArray(GroundClasses.Names.Select(n => (System.Text.Json.Nodes.JsonNode)n).ToArray());
        f.Add("landcover", Grid<byte>.Filled(282, 282, (byte)GroundClasses.Names.ToList().IndexOf("urban")));
        var bld = new Grid<bool>(282, 282);
        for (int r = 0; r < 141; r++) for (int c = 0; c < 141; c++) bld[r, c] = true;
        f.Add("buildings", bld);
        Directory.CreateDirectory(Path.Combine(folder.Data, LandcoverFile.Folder));
        f.Save(LandcoverFile.PathOf(folder.Data, b));

        var p = await host.Client.GetFromJsonAsync<JsonElement>(string.Format(inv, "/api/project/point?x={0}&y={1}", x0 + 5, y0 - 5));
        Assert.Equal(b.Name, p.GetProperty("block").GetString());
        Assert.Equal(("TARMAC", "tarmac", 40.0), (p.GetProperty("material").GetString(), p.GetProperty("materialClass").GetString(), p.GetProperty("height").GetDouble()));
        Assert.Equal(JsonValueKind.Null, p.GetProperty("water").ValueKind);
        Assert.Equal(("LEGSQU", "Legion Square", "Fake St"), (p.GetProperty("zone").GetString(), p.GetProperty("zoneName").GetString(), p.GetProperty("street").GetString()));
        Assert.True(p.GetProperty("onRoad").GetBoolean());
        Assert.Equal(("urban", true), (p.GetProperty("landcover").GetString(), p.GetProperty("building").GetBoolean()));
        var south = await host.Client.GetFromJsonAsync<JsonElement>(string.Format(inv, "/api/project/point?x={0}&y={1}", x0 + 200, y0 - 200));
        Assert.Equal((false, false), (south.GetProperty("onRoad").GetBoolean(), south.GetProperty("building").GetBoolean()));
        // a block without a scan
        var none = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/point?x=-3000&y=7000");
        Assert.False(none.GetProperty("scanned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("material").ValueKind);
    }
}
