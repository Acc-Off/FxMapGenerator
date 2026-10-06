using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;

namespace FxMapGenerator.App.Tests;

/// <summary>The POI screen's API: the icons, the project's points and POI styles read and saved, samples, PNG icons, points read from files.</summary>
public sealed class PoiApiTests
{
    static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    static string Code(JsonElement e) => e.GetProperty("error").GetProperty("code").GetString()!;

    static async Task<(TestHost Host, string Folder)> Start()
    {
        var host = await TestHost.StartAsync();
        var folder = Path.Combine(host.DataDirectory, "p");
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(folder, "poi-test"), satellite = false, atlas = true });
        return (host, folder);
    }

    /// <summary>A PNG of 8 x 8 opaque pixels of a grey.</summary>
    static void Png(string path, byte grey)
    {
        var rgba = new byte[8 * 8 * 4];
        Array.Fill(rgba, grey);
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        Core.Imaging.Images.SavePng(path, rgba, 8, 8);
    }

    [Fact]
    public async Task TheBundledPointsAreEditedIntoAFolderOfTheProject()
    {
        var (host, folder) = await Start();
        await using var _host = host;
        // the icons as bundled
        Assert.Equal(MdiIcons.Bytes, await host.Client.GetByteArrayAsync("/api/poi/icons"));

        var start = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/poi");
        Assert.Equal(JsonValueKind.Null, start.GetProperty("folder").ValueKind);
        Assert.Equal(JsonValueKind.Null, start.GetProperty("problem").ValueKind);
        Assert.Equal(new[] { "colored-dots", "highway-markers" }, start.GetProperty("set").GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("path").GetString()));
        Assert.Equal(new[] { "colored-dots", "highway-markers" }, start.GetProperty("bundledGroups").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal(new[] { "highway-marker", "colored-dot", "facility" }, start.GetProperty("bundledStyles").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal("Bahnschrift", start.GetProperty("fonts").GetProperty("en").GetString());
        // the bundled names in English and Japanese (the screen shows them in its language); the markers' labels
        var markers = start.GetProperty("set").GetProperty("groups")[1];
        Assert.Equal(("Highway One markers", "Highway One マーカー"), (markers.GetProperty("name").GetProperty("en").GetString(), markers.GetProperty("name").GetProperty("ja").GetString()));
        Assert.Equal(("A", ""), (markers.GetProperty("points")[0].GetProperty("label").GetProperty("en").GetString(), markers.GetProperty("points")[0].GetProperty("name").GetString()));

        // saving the bundled points as they are makes no folder
        var set = JsonNode.Parse(start.GetProperty("set").GetRawText())!.AsObject();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PutAsJsonAsync("/api/project/poi", set)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(folder, "poi")));

        // a group of our own with a style of our own: the folder starts from the bundled files, their names in the screen's
        // language from now on; the styles file is made
        set["folders"]!.AsArray().Add(new JsonObject { ["path"] = "店", ["name"] = "店" });
        set["groups"]!.AsArray().Add(new JsonObject
        {
            ["path"] = "店/コンビニ", ["style"] = "shop",
            ["points"] = new JsonArray(new JsonObject { ["id"] = "1", ["name"] = "駅前の店", ["label"] = new JsonObject { ["en"] = "24/7", ["ja"] = "24/7" }, ["x"] = 25, ["y"] = -1350 }),
        });
        set["styles"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "shop", ["name"] = "店", ["look"] = "icon", ["icon"] = "store", ["color"] = "#303060", ["size"] = 20, ["showLabel"] = true,
        });
        var saved = await host.Client.PutAsJsonAsync("/api/project/poi?language=ja", set);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var after = await Json(saved);
        Assert.Equal(("poi", "poi-styles.json"), (after.GetProperty("folder").GetString(), after.GetProperty("stylesFile").GetString()));
        Assert.Equal("Highway One マーカー", after.GetProperty("set").GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("path").GetString() == "highway-markers").GetProperty("name").GetString());
        var project = Project.Load(Path.Combine(folder, "poi-test.fxmapgen.json"));
        Assert.Equal(("poi", "poi-styles.json"), (project.File.Poi, project.File.PoiStyles));
        var data = PoiData.Of(project);
        Assert.Equal((ItemName.Of("Highway One マーカー"), ItemName.Of("色付きの丸")), (data.Groups.Single(g => g.Path == "highway-markers").Name, data.Groups.Single(g => g.Path == "colored-dots").Name));
        Assert.Equal(PoiData.Default.Resolve().Select(p => p.Drawn()), data.Resolve().Where(p => p.Point.Group != "店/コンビニ").Select(p => p.Drawn()));
        var shop = data.Resolve().Single(p => p.Point.Group == "店/コンビニ");
        Assert.Equal(("shop", "icon", "store", true, "駅前の店"), (shop.Style.Id, shop.Style.Look, shop.Style.Icon, shop.Style.ShowLabel, shop.Point.Name));
        Assert.Equal(ItemName.Of("店"), data.Folders.Single(f => f.Path == "店").Name);

        // what cannot be read is refused with every reason, and nothing is written
        set["groups"]!.AsArray().Add(new JsonObject { ["path"] = "店/_", ["points"] = new JsonArray() });
        set["styles"]![0]!["icon"] = "no-such-icon";
        var bad = await host.Client.PutAsJsonAsync("/api/project/poi", set);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var error = await Json(bad);
        Assert.Equal("INVALID", Code(error));
        Assert.Contains("'_' is the name of a folder's settings", error.GetProperty("error").GetProperty("message").GetString());
        set["groups"]!.AsArray().RemoveAt(set["groups"]!.AsArray().Count - 1);
        bad = await host.Client.PutAsJsonAsync("/api/project/poi", set);
        Assert.Contains("no MDI icon 'no-such-icon'", (await Json(bad)).GetProperty("error").GetProperty("message").GetString());
        Assert.Contains("\"store\"", File.ReadAllText(Path.Combine(folder, "poi-styles.json")));
    }

    [Fact]
    public async Task APngIsCopiedBesideTheStylesAndASampleIsDrawn()
    {
        var (host, folder) = await Start();
        await using var _host = host;
        var outside = Path.Combine(host.DataDirectory, "garage.png");
        Png(outside, 0);
        Task<HttpResponseMessage> Send(string name, byte[] bytes) => host.Client.PostAsync($"/api/project/poi/images?name={Uri.EscapeDataString(name)}", new ByteArrayContent(bytes));
        var copied = await Send("garage.png", File.ReadAllBytes(outside));
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
        Assert.Equal("poi-icons/garage.png", (await Json(copied)).GetProperty("image").GetString());
        Assert.True(File.Exists(Path.Combine(folder, "poi-icons", "garage.png")));
        // the same picture again keeps its name; another picture of that name gets another
        Assert.Equal("poi-icons/garage.png", (await Json(await Send("garage.png", File.ReadAllBytes(outside)))).GetProperty("image").GetString());
        Png(outside, 128);
        Assert.Equal("poi-icons/garage-2.png", (await Json(await Send("garage.png", File.ReadAllBytes(outside)))).GetProperty("image").GetString());
        var fake = await Send("fake.png", "not a picture"u8.ToArray());
        Assert.Equal(("INVALID", HttpStatusCode.BadRequest), (Code(await Json(fake)), fake.StatusCode));

        // a sample of a style with that PNG and its label beside, at zoom 8 and 6
        var style = new { id = "garage", name = "Garage", look = "icon", image = "poi-icons/garage.png", color = "#000000", size = 30, showLabel = true };
        var z8 = await host.Client.PostAsJsonAsync("/api/project/poi/sample", new { style, label = new { en = "Garage" }, language = "en", zoom = 8 });
        Assert.Equal(("image/png", HttpStatusCode.OK), (z8.Content.Headers.ContentType?.MediaType, z8.StatusCode));
        var rgba = Core.Imaging.Images.DecodeRgba(await z8.Content.ReadAsByteArrayAsync(), out int w8, out int h8);
        Assert.True(w8 > h8 && rgba.Length == w8 * h8 * 4);
        var z6 = await host.Client.PostAsJsonAsync("/api/project/poi/sample", new { style, label = new { en = "Garage" }, language = "en", zoom = 6 });
        Core.Imaging.Images.DecodeRgba(await z6.Content.ReadAsByteArrayAsync(), out int w6, out _);
        Assert.InRange(w6, w8 / 4, w8 / 4 + 2);
        var broken = await host.Client.PostAsJsonAsync("/api/project/poi/sample", new { style = new { id = "x", name = "X", look = "icon", icon = "nothing", color = "#000000", size = 30 } });
        Assert.Equal("INVALID", Code(await Json(broken)));
    }

    [Fact]
    public async Task PointsAreReadFromACsvFile()
    {
        var (host, _) = await Start();
        await using var _host = host;
        var read = await Json(await host.Client.PostAsJsonAsync("/api/project/poi/import", new { name = "shops.csv", text = "label,labelJa,name,x,y\nClinic,診療所,Pillbox,10,20\nFar,,,99999,0\n" }));
        Assert.Equal("shops", read.GetProperty("name").GetString());
        var point = read.GetProperty("points").EnumerateArray().Single();
        Assert.Equal(("Clinic", "診療所", "Pillbox", 10.0), (point.GetProperty("label").GetProperty("en").GetString(), point.GetProperty("label").GetProperty("ja").GetString(),
            point.GetProperty("name").GetString(), point.GetProperty("x").GetDouble()));
        var skipped = read.GetProperty("skipped").EnumerateArray().Single();
        var reason = skipped.GetProperty("reasons").EnumerateArray().Single();
        Assert.Equal((3, "offMap", "99999, 0"), (skipped.GetProperty("line").GetInt32(), reason.GetProperty("code").GetString(), reason.GetProperty("value").GetString()));
        // not a CSV: read as JSON
        var json = await Json(await host.Client.PostAsJsonAsync("/api/project/poi/import", new { name = "list.json", text = """[{"label": "A", "x": 1, "y": 2}]""" }));
        Assert.Equal(("list", 1), (json.GetProperty("name").GetString(), json.GetProperty("points").GetArrayLength()));
    }
}
