using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxMapGenerator.Core.Tests;

namespace FxMapGenerator.App.Tests;

public sealed class CaptureApiTests
{
    static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task TheExeHandsOutTheCaptureResourceAsAFolderOrAZip()
    {
        await using var host = await TestHost.StartAsync();
        var info = await host.Client.GetFromJsonAsync<JsonElement>("/api/capture-resource");
        Assert.Equal(("fxmapgen-capture", "0.1.0"), (info.GetProperty("name").GetString(), info.GetProperty("version").GetString()));

        // the zip: one folder named as the resource, with its manifest and scripts
        var zip = await host.Client.GetAsync("/api/capture-resource/zip");
        Assert.Equal("application/zip", zip.Content.Headers.ContentType?.MediaType);
        Assert.Equal("fxmapgen-capture.zip", zip.Content.Headers.ContentDisposition?.FileNameStar ?? zip.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using (var archive = new ZipArchive(await zip.Content.ReadAsStreamAsync()))
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("fxmapgen-capture/fxmanifest.lua", names);
            Assert.Contains("fxmapgen-capture/client/scan.lua", names);
            Assert.All(names, n => Assert.StartsWith("fxmapgen-capture/", n));
        }

        // the folder: written into the chosen folder; one already there is only replaced when asked
        var resources = Path.Combine(host.DataDirectory, "server", "resources");
        Directory.CreateDirectory(resources);
        Assert.Equal(JsonValueKind.Null, (await host.Client.GetFromJsonAsync<JsonElement>($"/api/capture-resource/found?folder={Uri.EscapeDataString(resources)}")).GetProperty("found").ValueKind);
        var saved = await Json(await host.Client.PostAsJsonAsync("/api/capture-resource/save", new { folder = resources }));
        Assert.Equal(Path.Combine(resources, "fxmapgen-capture"), saved.GetProperty("written").GetString());
        Assert.True(File.Exists(Path.Combine(resources, "fxmapgen-capture", "client", "scan.lua")));
        Assert.Equal("0.1.0", (await host.Client.GetFromJsonAsync<JsonElement>($"/api/capture-resource/found?folder={Uri.EscapeDataString(resources)}")).GetProperty("found").GetString());
        File.WriteAllText(Path.Combine(resources, "fxmapgen-capture", "old.lua"), "--");
        var again = await host.Client.PostAsJsonAsync("/api/capture-resource/save", new { folder = resources });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("EXISTS", (await Json(again)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/capture-resource/save", new { folder = resources, replace = true })).StatusCode);
        Assert.False(File.Exists(Path.Combine(resources, "fxmapgen-capture", "old.lua")));   // replaced as a whole
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/capture-resource/save", new { folder = Path.Combine(resources, "none") })).StatusCode);
    }

    [Fact]
    public async Task TheZipHoldsEveryFileOfTheResourceFolderAsItIs()
    {
        // every file of resource/fxmapgen-capture, README.ja.md too: a name with a language in it is no resource of that
        // language here, it goes into the exe with the others
        var folder = RepoFiles.Path("resource", "fxmapgen-capture");
        var expected = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllBytes);
        Assert.Contains("README.ja.md", expected.Keys);

        await using var host = await TestHost.StartAsync();
        using var archive = new ZipArchive(await (await host.Client.GetAsync("/api/capture-resource/zip")).Content.ReadAsStreamAsync());
        var got = new Dictionary<string, byte[]>();
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            using var s = entry.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            got[entry.FullName["fxmapgen-capture/".Length..]] = m.ToArray();
        }
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), got.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected) Assert.True(bytes.AsSpan().SequenceEqual(got[path]), path);
    }

    [Fact]
    public async Task TheProjectKnowsTheResourceIsOnTheServerWhenSavedThereOrToldSo()
    {
        await using var host = await TestHost.StartAsync();
        await host.Client.PostAsJsonAsync("/api/project/new", new { path = Path.Combine(host.DataDirectory, "p", "server") });
        async Task<JsonElement> Capture() => (await host.Client.GetFromJsonAsync<JsonElement>("/api/project/checks"))
            .EnumerateArray().Single(c => c.GetProperty("id").GetString() == "capture");
        Assert.False((await Capture()).GetProperty("ok").GetBoolean());

        var resources = Path.Combine(host.DataDirectory, "server", "resources");
        Directory.CreateDirectory(resources);
        var saved = await Json(await host.Client.PostAsJsonAsync("/api/capture-resource/save", new { folder = resources }));
        Assert.Equal("saved", saved.GetProperty("placed").GetProperty("how").GetString());
        var c = await Capture();
        Assert.True(c.GetProperty("ok").GetBoolean());
        Assert.Equal(("saved", Path.Combine(resources, "fxmapgen-capture")), (c.GetProperty("values").GetProperty("how").GetString(), c.GetProperty("values").GetProperty("folder").GetString()));

        // taken back, then said by the user (put there some other way)
        await host.Client.PostAsJsonAsync("/api/project/capture-placed", new { placed = false });
        Assert.False((await Capture()).GetProperty("ok").GetBoolean());
        await host.Client.PostAsJsonAsync("/api/project/capture-placed", new { placed = true });
        c = await Capture();
        Assert.True(c.GetProperty("ok").GetBoolean());
        Assert.Equal("user", c.GetProperty("values").GetProperty("how").GetString());
    }
}
