using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxMapGenerator.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FxMapGenerator.App.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task StatusReportsTheVersion()
    {
        await using var host = await TestHost.StartAsync();
        var status = await host.Client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal(Web.AppVersion.Value, status.GetProperty("version").GetString());
    }

    [Fact]
    public async Task UnknownApiPathReturnsErrorEnvelope()
    {
        await using var host = await TestHost.StartAsync();
        var response = await host.Client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ForeignHostHeaderIsRejected()
    {
        await using var host = await TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Host = "evil.example";
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BAD_HOST", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SettingsAreNormalizedAndSaved()
    {
        await using var host = await TestHost.StartAsync();
        var defaults = await host.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("auto", defaults.GetProperty("language").GetString());
        Assert.Equal("system", defaults.GetProperty("theme").GetString());
        Assert.Equal(0, defaults.GetProperty("recentProjects").GetArrayLength());

        var recent = new List<string> { @"C:\a.fxmapgen.json", " ", @"c:\A.fxmapgen.json" };
        recent.AddRange(Enumerable.Range(0, 20).Select(i => $@"C:\p{i}.fxmapgen.json"));
        var response = await host.Client.PutAsJsonAsync("/api/settings", new { recentProjects = recent, language = "fr", theme = "dark" });
        response.EnsureSuccessStatusCode();
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("auto", saved.GetProperty("language").GetString());   // unknown language -> auto
        Assert.Equal("dark", saved.GetProperty("theme").GetString());
        var list = saved.GetProperty("recentProjects").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(AppSettings.MaxRecentProjects, list.Count);           // blanks and duplicates dropped, capped
        Assert.Equal(@"C:\a.fxmapgen.json", list[0]);
        Assert.Equal(@"C:\p0.fxmapgen.json", list[1]);

        var onDisk = File.ReadAllText(Path.Combine(host.DataDirectory, SettingsStore.FileName));
        Assert.Contains("\"theme\": \"dark\"", onDisk);

        // the job list's width: the default when not given, kept in its range
        Assert.Equal(AppSettings.DefaultJobListWidth, saved.GetProperty("jobListWidth").GetInt32());
        var wide = await (await host.Client.PutAsJsonAsync("/api/settings", new { jobListWidth = 5000 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AppSettings.MaxJobListWidth, wide.GetProperty("jobListWidth").GetInt32());
        var zero = await (await host.Client.PutAsJsonAsync("/api/settings", new { jobListWidth = 0 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AppSettings.DefaultJobListWidth, zero.GetProperty("jobListWidth").GetInt32());
        var some = await (await host.Client.PutAsJsonAsync("/api/settings", new { jobListWidth = 520 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(520, some.GetProperty("jobListWidth").GetInt32());
    }

    [Fact]
    public async Task GuideAnswersAreKeptAndOthersDropped()
    {
        await using var host = await TestHost.StartAsync();
        var defaults = await host.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Empty(defaults.GetProperty("guides").EnumerateObject());   // no answers yet: every screen asks

        var guides = new Dictionary<string, string?>
        {
            ["start"] = "seen", ["roads"] = "never",
            ["poi"] = "later", ["styles"] = null, [""] = "seen", ["not an id"] = "never", [new string('a', 41)] = "seen",
        };
        var saved = await (await host.Client.PutAsJsonAsync("/api/settings", new { guides })).Content.ReadFromJsonAsync<JsonElement>();
        var kept = saved.GetProperty("guides").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(new Dictionary<string, string?> { ["start"] = "seen", ["roads"] = "never" }, kept);

        // written to the file and read back as they were
        var onDisk = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.DataDirectory, SettingsStore.FileName))).RootElement;
        Assert.Equal("never", onDisk.GetProperty("guides").GetProperty("roads").GetString());
        var store = new SettingsStore(host.DataDirectory, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
        store.Load();
        Assert.Equal(kept, store.Current.Guides.ToDictionary(g => g.Key, g => (string?)g.Value));

        // settings without answers (or with none at all) are read as no answers
        var none = await (await host.Client.PutAsJsonAsync("/api/settings", new { guides = (object?)null })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(none.GetProperty("guides").EnumerateObject());
    }

    [Fact]
    public async Task DiagnosticsListTheBundledLibrariesWithTheNativeSkiaLoaded()
    {
        await using var host = await TestHost.StartAsync();
        var d = await host.Client.GetFromJsonAsync<JsonElement>("/api/diagnostics");
        Assert.Equal(host.DataDirectory, d.GetProperty("dataDirectory").GetString());
        var libs = d.GetProperty("libraries").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("version").GetString()!);
        Assert.DoesNotContain("not loaded", libs["Skia (native)"]);
        Assert.Equal(Libraries.GtaToolkitCommit, libs["gta-toolkit"]);
        Assert.True(libs.ContainsKey("BCnEncoder.NET"));
    }

    [Fact]
    public async Task NoticesAreEmbedded()
    {
        await using var host = await TestHost.StartAsync();
        var text = await host.Client.GetStringAsync("/api/notices");
        Assert.Contains("gta-toolkit", text);
        Assert.Contains("SkiaSharp", text);
        // the libraries the screens bundle, with their license texts
        Assert.Contains("Copyright (c) Meta Platforms, Inc. and affiliates.", text);
        Assert.Contains("Copyright (c) 2019 Paul Henschel", text);
        Assert.Contains("Copyright (c) Kamran Ahmed", text);
    }

    [Fact]
    public async Task SpaFallbackServesIndexForScreenPathsButNotForApi()
    {
        var web = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", "web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "index.html"), "<!doctype html><title>test-index</title>");
        try
        {
            await using var host = await TestHost.StartAsync(o => o.WebRootDirectory = web);
            Assert.Contains("test-index", await host.Client.GetStringAsync("/project/some/screen"));
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/nothing-here")).StatusCode);
        }
        finally
        {
            try { Directory.Delete(web, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task QuitStopsTheApplication()
    {
        await using var host = await TestHost.StartAsync();
        var lifetime = host.App.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource();
        lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        (await host.Client.PostAsync("/api/quit", null)).EnsureSuccessStatusCode();
        Assert.Same(stopping.Task, await Task.WhenAny(stopping.Task, Task.Delay(5000)));
    }
}
