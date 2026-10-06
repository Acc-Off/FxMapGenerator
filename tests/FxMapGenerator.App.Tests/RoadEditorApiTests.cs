using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.State;
using Microsoft.Extensions.DependencyInjection;

namespace FxMapGenerator.App.Tests;

/// <summary>The road editor's API: whether it can be used, the path data for the screen, a node's and link's values, saving the edits, the road tiles.</summary>
public sealed class RoadEditorApiTests
{
    static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    static string Code(JsonElement e) => e.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>Three nodes in a row (the middle one a junction), a two-way link and a one-way link, and two street names.</summary>
    static void WritePaths(string workFolder)
    {
        var game = Path.Combine(workFolder, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "paths.json"), """
            {"areas": [1],
             "nodes": {
              "1:0": {"x": 0, "y": 0, "z": 10, "street": 11, "junction": false, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false, "speed": "normal"},
              "1:1": {"x": 50, "y": 0, "z": 10, "street": 11, "junction": true, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false, "junctionArea": [45, -5, 55, 5], "speed": "normal"},
              "1:2": {"x": 100, "y": 0, "z": 12.5, "street": 0, "junction": false, "highway": true, "tunnel": false, "unpaved": false, "switchedOff": true, "speed": "fast"}},
             "links": [
              {"from": "1:0", "to": "1:1", "lanesForward": 1, "lanesBack": 1, "narrow": false, "dontUseForNavigation": false, "shortcut": false, "laneOffset": 0, "length": 50},
              {"from": "1:1", "to": "1:0", "lanesForward": 1, "lanesBack": 1, "narrow": false, "dontUseForNavigation": false, "shortcut": false, "laneOffset": 0, "length": 50},
              {"from": "1:1", "to": "1:2", "lanesForward": 2, "lanesBack": 0, "narrow": true, "dontUseForNavigation": false, "shortcut": false, "laneOffset": 0, "length": 50},
              {"from": "1:2", "to": "1:1", "lanesForward": 0, "lanesBack": 2, "narrow": true, "dontUseForNavigation": false, "shortcut": true, "laneOffset": 0, "length": 50}]}
            """);
        File.WriteAllText(Path.Combine(game, "names.json"), """
            {"streets": {"11": {"en": "Fake St", "ja": "フェイク通り"}, "12": {"en": "Other Rd"}}, "zones": {}}
            """);
    }

    [Fact]
    public async Task TheEditorSaysWhyItCannotBeUsed()
    {
        await using var host = await TestHost.StartAsync();
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(host.DataDirectory, "p", "sat"), satellite = true });
        var s = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/road-editor");
        Assert.Equal("noRoadMaps", s.GetProperty("unavailable").GetString());
        await host.Client.PatchAsJsonAsync("/api/project", new { roadmap = true });
        s = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/road-editor");
        Assert.Equal("noGameFiles", s.GetProperty("unavailable").GetString());
        Assert.Equal(new[] { "roadmap" }, s.GetProperty("maps").EnumerateArray().Select(m => m.GetString()));
        var paths = await host.Client.GetAsync("/api/project/road-editor/paths");
        Assert.Equal(HttpStatusCode.Conflict, paths.StatusCode);
        Assert.Equal("NO_GAME_FILES", Code(await Json(paths)));
        // no road shapes made yet: no tiles
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/road-editor/shapes/roadmap/8/10/20.png")).StatusCode);
    }

    [Fact]
    public async Task ThePathDataGoesToTheScreenAndTheEditsAreSaved()
    {
        await using var host = await TestHost.StartAsync();
        var folder = Path.Combine(host.DataDirectory, "p");
        var created = await Json(await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(folder, "roads"), satellite = false, roadmap = true, language = "ja" }));
        var projectFile = created.GetProperty("path").GetString()!;
        WritePaths(folder);

        // a new project's edits: a copy of the bundled ones beside the project file, the groups named in the screen's language
        var s = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/road-editor");
        Assert.Equal(JsonValueKind.Null, s.GetProperty("unavailable").ValueKind);
        Assert.Equal("road-edits.json", s.GetProperty("file").GetString());
        Assert.Equal(RoadEditsFile.Bundled.Nodes.Count, s.GetProperty("edits").GetProperty("nodes").EnumerateObject().Count());
        Assert.Equal("サンディ海岸飛行場", s.GetProperty("edits").GetProperty("groups").GetProperty("sandyShoresAirfield").GetString());
        var copy = File.ReadAllText(Path.Combine(folder, "road-edits.json"));
        Assert.Equal(RoadEditsFile.Format(RoadEditsFile.Copied(RoadEditsFile.Bundled, "ja")), copy);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("shapesVersion").ValueKind);
        // the bundled edits as the app carries them, for the screen to take groups in
        Assert.Equal(RoadEditsFile.BundledBytes, await host.Client.GetByteArrayAsync("/api/project/road-editor/bundled"));

        // the path data as columns: one link a connection (the way its first record goes), flags, names
        var p = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/road-editor/paths");
        Assert.Equal(new[] { "1:0", "1:1", "1:2" }, p.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(new[] { 0, 1, 2 + 16 }, p.GetProperty("flags").EnumerateArray().Select(v => v.GetInt32()));
        Assert.False(p.TryGetProperty("junctions", out _));
        int[] Ints(string name) => p.GetProperty(name).EnumerateArray().Select(v => v.GetInt32()).ToArray();
        Assert.Equal(new[] { 0, 1 }, Ints("linkA"));
        Assert.Equal(new[] { 1, 2 }, Ints("linkB"));
        Assert.Equal(new[] { 1, 2 }, Ints("lanesForward"));
        Assert.Equal(new[] { 1, 0 }, Ints("lanesBack"));
        Assert.Equal(new[] { 0, 1 + 4 }, p.GetProperty("linkFlags").EnumerateArray().Select(v => v.GetInt32()));   // narrow, and a lane change on either record
        var streets = p.GetProperty("streets").EnumerateArray().ToList();
        Assert.Equal(new[] { "Fake St", "Other Rd" }, streets.Select(x => x.GetProperty("en").GetString()));
        Assert.Equal("フェイク通り", streets[0].GetProperty("ja").GetString());

        // every value of a node and of a link's records, as the file has them
        var node = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/road-editor/node?key=1:2");
        Assert.Equal(("fast", 12.5), (node.GetProperty("values").GetProperty("speed").GetString(), node.GetProperty("values").GetProperty("z").GetDouble()));
        Assert.Equal(JsonValueKind.Null, node.GetProperty("street").ValueKind);
        node = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/road-editor/node?key=1:0");
        Assert.Equal("Fake St", node.GetProperty("street").GetProperty("en").GetString());
        var link = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/road-editor/link?from=1:2&to=1:1");
        Assert.Equal(2, link.GetProperty("records").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/road-editor/node?key=9:9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/road-editor/link?from=1:0&to=1:2")).StatusCode);
        // no ground scan there
        var ground = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/road-editor/ground?x=10&y=10");
        Assert.False(ground.GetProperty("scanned").GetBoolean());
        // no landcover yet (the capture is not over): the preview of the road shapes is provisional, and its tiles are drawn
        var preview = await host.Client.PostAsync("/api/project/road-editor/preview", new StringContent("{\"format\": 1, \"nodes\": {}, \"links\": []}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var made = await Json(preview);
        Assert.True(made.GetProperty("provisional").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/api/project/road-editor/shapes/roadmap/8/10/20.png?v={made.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/road-editor/shapes/roadmap/8/10/20.png?v=preview-99")).StatusCode);

        // saving writes the project's edits file
        const string edits = """
            {"format": 1,
             "nodes": {"added:1": {"x": 50, "y": 40, "z": 11, "street": 0, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false},
                       "1:1": {"original": {"x": 50, "y": 0, "z": 10}},
                       "1:0": {"hidden": true, "original": {"x": 0, "y": 0, "z": 10}}},
             "links": [{"from": "1:1", "to": "added:1", "lanesForward": 1, "lanesBack": 1, "narrow": false, "width": 12}]}
            """;
        var saved = await host.Client.PutAsync("/api/project/road-editor/edits", new StringContent(edits, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var status = await Json(saved);
        Assert.Equal("road-edits.json", status.GetProperty("file").GetString());
        Assert.Equal(3, status.GetProperty("edits").GetProperty("nodes").EnumerateObject().Count());
        Assert.Empty(status.GetProperty("notApplied").EnumerateArray());
        Assert.True(File.Exists(Path.Combine(folder, "road-edits.json")));
        Assert.Equal("road-edits.json", Project.Load(projectFile).File.RoadEdits);
        Assert.Equal("road-edits.json", (await host.Client.GetFromJsonAsync<JsonElement>("/api/project")).GetProperty("file").GetProperty("roadEdits").GetString());

        // edits that do not match the game's data any more are listed, with the reason
        var moved = edits.Replace("\"1:1\": {\"original\": {\"x\": 50,", "\"1:1\": {\"original\": {\"x\": 51,");
        status = await Json(await host.Client.PutAsync("/api/project/road-editor/edits", new StringContent(moved, Encoding.UTF8, "application/json")));
        var reasons = status.GetProperty("notApplied").EnumerateArray().Select(n => (n.GetProperty("kind").GetString(), n.GetProperty("key").GetString(), n.GetProperty("reason").GetString())).ToList();
        Assert.Equal(new[] { ("node", "1:1", "nodeChanged"), ("link", "1:1 added:1", "endNotApplied") }, reasons);

        // the street names the edits add are saved with them, the Japanese name written as it is
        var named = edits.Replace("{\"format\": 1,", "{\"format\": 1, \"streets\": {\"4001\": {\"en\": \"Island Rd\", \"ja\": \"島通り\"}},")
            .Replace("\"street\": 0, \"highway\"", "\"street\": 4001, \"highway\"");
        status = await Json(await host.Client.PutAsync("/api/project/road-editor/edits", new StringContent(named, Encoding.UTF8, "application/json")));
        Assert.Equal("島通り", status.GetProperty("edits").GetProperty("streets").GetProperty("4001").GetProperty("ja").GetString());
        Assert.Contains("\"4001\": {\"en\": \"Island Rd\", \"ja\": \"島通り\"}", File.ReadAllText(Path.Combine(folder, "road-edits.json")));

        // unusable edits are refused with every problem named, and nothing is written
        var before = File.ReadAllText(Path.Combine(folder, "road-edits.json"));
        var bad = await host.Client.PutAsync("/api/project/road-editor/edits", new StringContent("""{"format": 1, "nodes": {"added:1": {"x": 1}}, "links": [{"from": "added:1", "to": "added:2"}]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var message = (await Json(bad)).GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("an added node needs y, z", message);
        Assert.Contains("node added:2 is not in the nodes", message);
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, "road-edits.json")));
    }

    [Fact]
    public async Task ANewEditsFileTakesAFreeName()
    {
        await using var host = await TestHost.StartAsync();
        var folder = Path.Combine(host.DataDirectory, "p");
        var created = await Json(await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(folder, "roads"), satellite = false, roadmap = true }));
        WritePaths(folder);
        // a project without an edits file (its roadEdits taken out by hand): the first save makes one beside the project
        // file under a free name, and the project names it
        var project = Project.Load(created.GetProperty("path").GetString()!);
        project.File.RoadEdits = null;
        project.Save();
        File.WriteAllText(Path.Combine(folder, "road-edits.json"), "someone else's file");
        var saved = await host.Client.PutAsync("/api/project/road-editor/edits", new StringContent("""{"format": 1, "nodes": {}, "links": []}""", Encoding.UTF8, "application/json"));
        Assert.Equal("road-edits-2.json", (await Json(saved)).GetProperty("file").GetString());
        Assert.Equal("road-edits-2.json", Project.Load(project.FilePath).File.RoadEdits);
        Assert.Equal("someone else's file", File.ReadAllText(Path.Combine(folder, "road-edits.json")));
        // a file named by the project that is not there is a problem to show
        File.Delete(Path.Combine(folder, "road-edits-2.json"));
        var s = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/road-editor");
        Assert.Contains("not found", s.GetProperty("problem").GetString());
    }

    sealed class HoldRoads(ManualResetEventSlim release, ManualResetEventSlim started) : Stage
    {
        public override string Id => "hold";
        public override string Row => "mapData.roads";
        public override IReadOnlyList<string> Prepare(StageContext ctx) => new[] { "world" };
        public override void Run(UnitContext ctx)
        {
            started.Set();
            release.Wait(ctx.Token);
        }
        public override int CountReady(Project project, StateStore state) => 1;
    }

    sealed class HoldBuild(ManualResetEventSlim release, ManualResetEventSlim started) : IBuildStages
    {
        public BuildPlan For(Project project, StateStore state, BuildStages.Options options) => new(new Stage[] { new HoldRoads(release, started) }, Array.Empty<string>());
    }

    [Fact]
    public async Task EditsCannotBeSavedWhileTheRoadShapesAreMade()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IBuildStages>(new HoldBuild(release, started)));
        var folder = Path.Combine(host.DataDirectory, "p");
        var created = await Json(await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(folder, "roads"), satellite = false, roadmap = true }));
        WritePaths(folder);
        await host.Client.PostAsJsonAsync("/api/jobs", new { project = created.GetProperty("path").GetString() });
        // the run holds the edits from the start of the step that reads them, not from its own start: until then they can be saved
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        var empty = new StringContent("""{"format": 1, "nodes": {}, "links": []}""", Encoding.UTF8, "application/json");
        var locked = await host.Client.PutAsync("/api/project/road-editor/edits", empty);
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Equal("LOCKED", Code(await Json(locked)));
        Assert.Equal(RoadEditsFile.Format(RoadEditsFile.Copied(RoadEditsFile.Bundled, "en")), File.ReadAllText(Path.Combine(folder, "road-edits.json")));
        // nor are the game files read again while a step that reads them runs
        var reading = await host.Client.PostAsync("/api/project/road-editor/game-files", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, reading.StatusCode);
        Assert.Equal("LOCKED", Code(await Json(reading)));
        release.Set();
        await host.App.Services.GetRequiredService<JobManager>().WaitAsync();
        var saved = await host.Client.PutAsync("/api/project/road-editor/edits", new StringContent("""{"format": 1, "nodes": {}, "links": []}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }
}
