using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.App.Tests;

/// <summary>The style editor's API: the table, the project's styles (bundled and own), making, renaming, deleting, exporting, importing and sharing them.</summary>
public sealed class StyleApiTests
{
    static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    static string Code(JsonElement e) => e.GetProperty("error").GetProperty("code").GetString()!;

    static async Task<(TestHost Host, string Folder)> Start()
    {
        var host = await TestHost.StartAsync();
        var folder = Path.Combine(host.DataDirectory, "p");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(folder, "styles-test"), satellite = false, atlas = true });
        return (host, folder);
    }

    [Fact]
    public async Task TheTableAndTheBundledStylesAreThere()
    {
        var (host, _) = await Start();
        await using var _host = host;
        Assert.Equal(StyleSchema.Bytes, await host.Client.GetByteArrayAsync("/api/styles/schema"));
        var list = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/styles");
        Assert.Equal(JsonValueKind.Null, list.GetProperty("folder").ValueKind);
        var styles = list.GetProperty("styles").EnumerateArray().ToList();
        Assert.Equal(new[] { "postalcodemap", "regional" }, styles.Select(s => s.GetProperty("id").GetString()));
        Assert.True(styles[0].GetProperty("bundled").GetBoolean());
        Assert.Equal(new[] { "atlas-postalcodemap-en" }, styles[0].GetProperty("maps").EnumerateArray().Select(m => m.GetString()));
        // the zones' names come from the project's game files (none before they are read)
        Assert.Empty(list.GetProperty("zoneNames").EnumerateObject());
        var folder = Path.Combine(host.DataDirectory, "p");
        Directory.CreateDirectory(Path.Combine(folder, "game"));
        File.WriteAllText(Path.Combine(folder, "game", "names.json"), """{"streets": {}, "zones": {"DOWNT": {"en": "Downtown", "ja": "ダウンタウン"}}}""");
        list = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/styles");
        Assert.Equal("ダウンタウン", list.GetProperty("zoneNames").GetProperty("DOWNT").GetProperty("ja").GetString());
        var one = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/styles/regional");
        Assert.Equal("地域色", one.GetProperty("name").GetProperty("ja").GetString());
        Assert.Equal(0.8, one.GetProperty("values").GetProperty("shade").GetProperty("strength").GetDouble());
        Assert.Empty(one.GetProperty("changes").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, one.GetProperty("baseValues").ValueKind);
        // a bundled style's file as the app carries it; no style of another name
        var file = await host.Client.GetAsync("/api/project/styles/regional/file");
        Assert.Equal("regional.json", file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using (var s = Core.World.EmbeddedData.Open("styles/regional.json"))
        using (var m = new MemoryStream())
        {
            s.CopyTo(m);
            Assert.Equal(m.ToArray(), await file.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/styles/nothing")).StatusCode);
    }

    [Fact]
    public async Task AStyleIsMadeFromABundledOneRenamedExportedAndDeleted()
    {
        var (host, folder) = await Start();
        await using var _host = host;
        var made = await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "postalcodemap", id = "night", name = "夜" });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var style = await Json(made);
        Assert.Equal("postalcodemap", style.GetProperty("base").GetString());
        Assert.False(style.GetProperty("bundled").GetBoolean());
        Assert.Empty(style.GetProperty("changes").EnumerateObject());
        Assert.Equal("night", style.GetProperty("values").GetProperty("id").GetString());
        Assert.Equal("#fdfcfc", style.GetProperty("values").GetProperty("paint").GetProperty("roads").GetProperty("road").GetProperty("fill").GetString());
        Assert.Equal("#fdfcfc", style.GetProperty("baseValues").GetProperty("paint").GetProperty("roads").GetProperty("road").GetProperty("fill").GetString());
        // the first style made the folder beside the project file and named it in the project
        Assert.True(File.Exists(Path.Combine(folder, "styles", "night.json")));
        var project = Project.Load(Path.Combine(folder, "styles-test.fxmapgen.json"));
        Assert.Equal("styles", project.File.Styles);
        var list = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/styles");
        Assert.Equal("styles", list.GetProperty("folder").GetString());
        Assert.Equal(new[] { "postalcodemap", "regional", "night" }, list.GetProperty("styles").EnumerateArray().Select(s => s.GetProperty("id").GetString()));

        // ids: taken (the project's, a bundled one's), of the wrong form; a name is needed
        Assert.Equal("ID_TAKEN", Code(await Json(await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "regional", id = "night", name = "N" }))));
        Assert.Equal("ID_TAKEN", Code(await Json(await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "regional", id = "regional", name = "N" }))));
        Assert.Equal("INVALID", Code(await Json(await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "regional", id = "Night-2", name = "N" }))));
        Assert.Equal("INVALID", Code(await Json(await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "regional", id = "night2", name = " " }))));
        Assert.Equal("NOT_FOUND", Code(await Json(await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "nothing", id = "night2", name = "N" }))));

        // a copy of a project style keeps its base and changes
        var file = Path.Combine(folder, "styles", "night.json");
        var changed = UserStyleFile.Read(file) with { Changes = new JsonObject { ["background"] = "#000000" } };
        UserStyleFile.Write(file, changed);
        var copy = await Json(await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "night", id = "night2", name = "夜 2" }));
        Assert.Equal("#000000", copy.GetProperty("changes").GetProperty("background").GetString());
        Assert.Equal("postalcodemap", copy.GetProperty("base").GetString());

        // rename: the name changes, the id stays
        var renamed = await Json(await host.Client.PutAsJsonAsync("/api/project/styles/night2/name", new { name = "深夜" }));
        Assert.Equal("深夜", renamed.GetProperty("name").GetString());
        Assert.Equal("深夜", UserStyleFile.Read(Path.Combine(folder, "styles", "night2.json")).Name);
        Assert.Equal("INVALID", Code(await Json(await host.Client.PutAsJsonAsync("/api/project/styles/night2/name", new { name = "" }))));

        // export: the file as it is
        Assert.Equal(File.ReadAllBytes(file), await host.Client.GetByteArrayAsync("/api/project/styles/night/file"));

        // delete: gone from the folder and the list; a bundled style cannot be
        var afterDelete = await Json(await host.Client.DeleteAsync("/api/project/styles/night2"));
        Assert.Equal(new[] { "postalcodemap", "regional", "night" }, afterDelete.GetProperty("styles").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.False(File.Exists(Path.Combine(folder, "styles", "night2.json")));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync("/api/project/styles/regional")).StatusCode);
    }

    [Fact]
    public async Task SavingKeepsOnlyTheValuesThatDifferFromTheBase()
    {
        var (host, folder) = await Start();
        await using var _host = host;
        await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "postalcodemap", id = "night", name = "夜" });
        var style = await host.Client.GetFromJsonAsync<JsonObject>("/api/project/styles/night");
        var values = style!["values"]!.AsObject();
        values["background"] = "#101010";
        values["paint"]!["roads"]!["road"]!["fill"] = "#ffffff";
        values["shade"]!["strength"] = 0.5;                                 // the base's value: no change
        values["paint"]!["roads"]!["track"]!["width"] = 5;                  // 5.0 in the base: the same value
        values.Remove("credit");                                            // taken away: null in the file
        var saved = await Json(await host.Client.PutAsJsonAsync("/api/project/styles/night", values));
        Assert.Equal("#101010", saved.GetProperty("values").GetProperty("background").GetString());
        var file = UserStyleFile.Read(Path.Combine(folder, "styles", "night.json"));
        Assert.Equal(("night", "夜"), (file.Id, file.Name));
        Assert.Equal("""{"credit":null,"background":"#101010","paint":{"roads":{"road":{"fill":"#ffffff"}}}}""",
            JsonNode.Parse(UserStyleFile.Format(file))!.AsObject().Where(kv => !UserStyleFile.OwnKeys.Contains(kv.Key))
                .Aggregate(new JsonObject(), (o, kv) => { o[kv.Key] = kv.Value?.DeepClone(); return o; }).ToJsonString());
        // every value back to the base's: a file of its own keys only
        await host.Client.PutAsJsonAsync("/api/project/styles/night", style["baseValues"]);
        Assert.Empty(UserStyleFile.Read(Path.Combine(folder, "styles", "night.json")).Changes);

        // a style the maps cannot read is not saved, and says every problem
        values["background"] = "red";
        values["sea"]!["bands"] = new JsonArray(2, 1);
        var bad = await host.Client.PutAsJsonAsync("/api/project/styles/night", values);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var message = (await Json(bad)).GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("'red' is not a colour", message);
        Assert.Contains("sea.bands must increase", message);
        Assert.Empty(UserStyleFile.Read(Path.Combine(folder, "styles", "night.json")).Changes);
        // a bundled style is never saved
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync("/api/project/styles/postalcodemap", style["baseValues"])).StatusCode);
    }

    [Fact]
    public async Task TheFontsOfThisPcAreListed()
    {
        await using var host = await TestHost.StartAsync();
        var fonts = await host.Client.GetFromJsonAsync<string[]>("/api/fonts");
        Assert.NotEmpty(fonts!);
        Assert.Equal(fonts!.Order(StringComparer.OrdinalIgnoreCase), fonts);
        Assert.DoesNotContain(fonts, f => f.StartsWith('@'));
    }

    [Fact]
    public async Task ThePreviewWaitsForTheSampleLandAndNamesMistakesFirst()
    {
        var (host, _) = await Start();
        await using var _host = host;
        // not made yet: the land's frame and the place first shown are there, the pictures are not
        var status = await host.Client.GetFromJsonAsync<JsonElement>("/api/styles/sample");
        Assert.Equal("none", status.GetProperty("state").GetString());
        Assert.Equal(4, status.GetProperty("frame").GetArrayLength());
        Assert.Equal(2, status.GetProperty("place").GetArrayLength());
        Assert.Equal(new[] { 500.0, 1000, 2000, 4000 }, status.GetProperty("sizes").EnumerateArray().Select(v => v.GetDouble()));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/styles/sample/overview.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/project/styles/preview/tiles/none-1/12/18.png")).StatusCode);
        var values = JsonNode.Parse((await host.Client.GetFromJsonAsync<JsonElement>("/api/project/styles/postalcodemap")).GetProperty("values").GetRawText())!.AsObject();
        var early = await host.Client.PostAsJsonAsync("/api/project/styles/preview", new { values, x = -3072, y = 7117, size = 2000, language = "en", side = "right" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("NOT_READY", Code(await Json(early)));
        var pick = await host.Client.PostAsJsonAsync("/api/project/styles/preview/pick", new { values, x = -3072, y = 7117, cx = -3072, cy = 7117, size = 500, language = "en" });
        Assert.Equal("NOT_READY", Code(await Json(pick)));
        // a style the maps cannot read says why, made or not
        values["background"] = "red";
        var refused = await host.Client.PostAsJsonAsync("/api/project/styles/preview", new { values, x = -3072, y = 7117, language = "en", side = "right" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("INVALID", Code(await Json(refused)));
    }

    [Fact]
    public async Task ThePreviewOnTheProjectsDataWaitsForItAndKeepsTheProjectsPlaces()
    {
        var (host, folder) = await Start();
        await using var _host = host;
        // a new project has none of the data: the recommended places are there, none of them can be shown
        var status = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/styles/preview");
        Assert.Equal("none", status.GetProperty("state").GetString());
        Assert.Equal("the road shapes", status.GetProperty("waiting").GetString());
        Assert.Equal(new[] { "Legion Square", "Sandy Shores", "Vinewood Hills", "Mount Chiliad", "Vespucci Beach", "Paleto Forest" },
            status.GetProperty("recommended").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.All(status.GetProperty("recommended").EnumerateArray(), p => Assert.False(p.GetProperty("ready").GetBoolean()));
        var values = JsonNode.Parse((await host.Client.GetFromJsonAsync<JsonElement>("/api/project/styles/postalcodemap")).GetProperty("values").GetRawText())!.AsObject();
        var early = await host.Client.PostAsJsonAsync("/api/project/styles/preview", new { values, x = 195, y = -934, size = 500, language = "en", side = "right", source = "project" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("NOT_READY", Code(await Json(early)));
        // the project's places: the whole list, the names trimmed, kept in the project file
        var saved = await Json(await host.Client.PutAsJsonAsync("/api/project/styles/preview/places", new[] { new { name = " Pier ", x = -1600.4, y = -1100.6 } }));
        Assert.Equal("Pier", saved.GetProperty("places")[0].GetProperty("name").GetString());
        var place = Assert.Single(Project.Load(Path.Combine(folder, "styles-test.fxmapgen.json")).File.PreviewPlaces!);
        Assert.Equal(("Pier", -1600.0, -1101.0), (place.Name, place.X, place.Y));
        Assert.Equal("INVALID", Code(await Json(await host.Client.PutAsJsonAsync("/api/project/styles/preview/places", new[] { new { name = " ", x = 0, y = 0 } }))));
        await host.Client.PutAsJsonAsync("/api/project/styles/preview/places", Array.Empty<object>());
        Assert.Null(Project.Load(Path.Combine(folder, "styles-test.fxmapgen.json")).File.PreviewPlaces);
    }

    [Fact]
    public async Task TheAtlasMapsTakeTheProjectsOwnStyles()
    {
        var (host, folder) = await Start();
        await using var _host = host;
        await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "regional", id = "dusk", name = "夕方" });
        // a style the project does not have is refused
        var bad = await host.Client.PatchAsJsonAsync("/api/project", new { atlas = new { styles = new[] { "nothing" } } });
        Assert.Equal("INVALID", Code(await Json(bad)));
        Assert.Equal("INVALID", Code(await Json(await host.Client.PatchAsJsonAsync("/api/project", new { atlas = new { languages = new[] { "en", "fr" } } }))));
        // every style in every language: English always, Japanese when chosen
        var project = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { atlas = new { styles = new[] { "postalcodemap", "dusk" }, languages = new[] { "ja" } } }));
        Assert.Equal(new[] { "atlas-postalcodemap-en", "atlas-postalcodemap-ja", "atlas-dusk-en", "atlas-dusk-ja" }, project.GetProperty("maps").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(new[] { "en", "ja" }, project.GetProperty("file").GetProperty("maps").GetProperty("atlas").GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("夕方", Assert.Single(project.GetProperty("ownStyles").EnumerateArray()).GetProperty("name").GetString());
        // in use: it cannot be deleted, and its list entry names the maps
        Assert.Equal("IN_USE", Code(await Json(await host.Client.DeleteAsync("/api/project/styles/dusk"))));
        var list = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/styles");
        Assert.Equal(new[] { "atlas-dusk-en", "atlas-dusk-ja" }, list.GetProperty("styles").EnumerateArray().First(s => s.GetProperty("id").GetString() == "dusk")
            .GetProperty("maps").EnumerateArray().Select(m => m.GetString()));
        // the minimap on a Japanese map: Japanese off takes it away with those maps
        await host.Client.PatchAsJsonAsync("/api/project", new { minimapMap = "atlas-dusk-ja" });
        project = await Json(await host.Client.PatchAsJsonAsync("/api/project", new { atlas = new { languages = new[] { "en" } } }));
        Assert.Equal(new[] { "atlas-postalcodemap-en", "atlas-dusk-en" }, project.GetProperty("maps").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(JsonValueKind.Null, project.GetProperty("file").GetProperty("minimap").GetProperty("map").ValueKind);
    }

    [Fact]
    public async Task AStyleFileIsImportedAndSharedStylesGoToOtherProjects()
    {
        var (host, folder) = await Start();
        await using var _host = host;
        var file = UserStyleFile.Format(new UserStyle("dusk", "夕方", AtlasPresets.Regional, new JsonObject { ["shade"] = new JsonObject { ["strength"] = 1.2 } }));
        HttpContent Body(string text) => new StringContent(text, Encoding.UTF8, "application/json");
        var imported = await Json(await host.Client.PostAsync("/api/project/styles/import", Body(file)));
        Assert.Equal(1.2, imported.GetProperty("values").GetProperty("shade").GetProperty("strength").GetDouble());
        Assert.Equal(file, File.ReadAllText(Path.Combine(folder, "styles", "dusk.json")));
        // the same id again is taken; another id takes it in
        Assert.Equal("ID_TAKEN", Code(await Json(await host.Client.PostAsync("/api/project/styles/import", Body(file)))));
        Assert.Equal("dusk2", (await Json(await host.Client.PostAsync("/api/project/styles/import?id=dusk2", Body(file)))).GetProperty("id").GetString());
        // a whole style (a bundled style's file under another id) takes its name in the screen's language
        var whole = JsonNode.Parse(await host.Client.GetStringAsync("/api/project/styles/regional/file"))!.AsObject();
        whole["id"] = "myregional";
        var copied = await Json(await host.Client.PostAsync("/api/project/styles/import?language=ja", Body(whole.ToJsonString())));
        Assert.Equal(("myregional", "地域色", "regional"), (copied.GetProperty("id").GetString(), copied.GetProperty("name").GetString(), copied.GetProperty("base").GetString()));
        // a file with problems says all of them
        var bad = await host.Client.PostAsync("/api/project/styles/import", Body("""{"format": 1, "id": "x", "name": "X", "base": "regional", "background": "red"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("'red' is not a colour", (await Json(bad)).GetProperty("error").GetProperty("message").GetString());

        // shared: copied into the settings folder; again only with overwrite
        var shared = await Json(await host.Client.PostAsync("/api/project/styles/dusk/shared", null));
        Assert.Equal(new[] { "dusk" }, shared.EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "styles", "dusk.json")));
        Assert.Equal("EXISTS", Code(await Json(await host.Client.PostAsync("/api/project/styles/dusk/shared", null))));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync("/api/project/styles/dusk/shared?overwrite=true", null)).StatusCode);

        // another project makes a style from the shared one: a copy of it in its own folder
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(host.DataDirectory, "q", "other"), satellite = false, atlas = true });
        var list = await host.Client.GetFromJsonAsync<JsonElement>("/api/styles/shared");
        Assert.Equal("夕方", list.EnumerateArray().Single().GetProperty("name").GetString());
        var fromShared = await Json(await host.Client.PostAsJsonAsync("/api/project/styles", new { from = "dusk", shared = true, id = "evening", name = "夕暮れ" }));
        Assert.Equal(1.2, fromShared.GetProperty("values").GetProperty("shade").GetProperty("strength").GetDouble());
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "q", "styles", "evening.json")));
    }
}
