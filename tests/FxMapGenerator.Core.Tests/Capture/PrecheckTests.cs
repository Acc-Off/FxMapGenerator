using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Projects;

namespace FxMapGenerator.Core.Tests.Capture;

public sealed class PrecheckTests
{
    static readonly Precheck.Options Fast = new()
    {
        ResourceVersion = "0.1.0",
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
        TileTimeout = TimeSpan.FromSeconds(2),
        SettleBeforeShot = TimeSpan.Zero,
        ShotRetryWait = TimeSpan.FromMilliseconds(1),
        Settle = SettleTests.Quick,
    };

    static Project NewProject(TempFolder tmp)
    {
        var p = Project.Create(tmp.File("p.fxmapgen.json"));
        p.Save();
        return p;
    }

    static Dictionary<string, bool?> Results(PrecheckReport r) => r.Items.ToDictionary(i => i.Id, i => i.Ok);

    [Fact]
    public async Task WithoutShotsOnlyTheConnectionTheResourceAndTheResourcesStatesAreChecked()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { Size = null };                          // no window at all: not needed
        var p = Project.Create(tmp.File("p.fxmapgen.json"));
        p.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
        p.Save();
        var report = await new Precheck(game, Fast).RunAsync(p);
        Assert.True(report.Ok, string.Join("; ", report.Items.Select(i => $"{i.Id} {i.Ok} {i.Message}")));
        Assert.Equal(new[] { "console", "resource", "window", "resources", "testShot", "environment", "settle" }, report.Items.Select(i => i.Id));
        foreach (var id in new[] { "window", "testShot", "environment", "settle" })
        {
            var item = report.Items.Single(i => i.Id == id);
            Assert.Null(item.Ok);
            Assert.Equal(new[] { "notNeeded" }, item.Codes);
        }
        // weather, time and NPCs only show in the photos: the game is left as it is (read at once after env on, the NPCs
        // of a town are still there)
        Assert.DoesNotContain(game.Received, c => c.StartsWith("fxmapgen tile", StringComparison.Ordinal) || c.StartsWith("cl_drawPerf", StringComparison.Ordinal)
            || c.StartsWith("fxmapgen env", StringComparison.Ordinal) || c.StartsWith("stop ", StringComparison.Ordinal) || c.StartsWith("ensure ", StringComparison.Ordinal));
        Assert.False(game.Env);
        Assert.All(game.Resources.Values, s => Assert.Equal("started", s));
    }

    [Fact]
    public async Task AllFineTakesTheTestShotAndLeavesTheGameAsItWas()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        var report = await new Precheck(game, Fast).RunAsync(NewProject(tmp));
        Assert.True(report.Ok, string.Join("; ", report.Items.Select(i => $"{i.Id} {i.Ok} {i.Message}")));
        Assert.Equal(new[] { "console", "resource", "window", "resources", "testShot", "environment", "settle" }, report.Items.Select(i => i.Id));
        Assert.All(report.Items, i => Assert.Empty(i.Codes!));
        Assert.Empty(report.Boxes);
        Assert.Equal("0.1.0", report.Items.Single(i => i.Id == "resource").Values["version"]);
        Assert.Equal("started", report.Items.Single(i => i.Id == "resources").Values["qbx_hud"]);

        // the shot over open sea, as the visit would take it
        Assert.Contains("fxmapgen tile 8 0 0 2 1.06 1500", game.Received);
        var png = Images.LoadRgba(Path.Combine(report.Folder, "shot.png"), out int w, out int h);
        Assert.Equal((1920, 1080), (w, h));
        Assert.Empty(FrameChecks.OverSea(new Frame(w, h, png)));
        Assert.True(File.Exists(Path.Combine(report.Folder, "shot-marked.png")));
        Assert.Contains("[fxmapgen] READY", File.ReadAllText(Path.Combine(report.Folder, "console.log")));

        // back as found
        Assert.False(game.Env);
        Assert.Equal(new[] { "fxmapgen env off", "ensure qbx_density", "ensure Renewed-Weathersync", "ensure chat", "ensure qbx_hud" }, game.Received.TakeLast(5));
        Assert.All(game.Resources.Values, s => Assert.Equal("started", s));

        var latest = PrecheckReport.Latest(Path.Combine(tmp.Path, "logs"));
        Assert.NotNull(latest);
        Assert.Equal(report.Folder, latest.Folder);
        Assert.True(latest.Ok);
    }

    [Fact]
    public async Task SomethingLeftOnTheScreenIsBoxed()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { Hud = (900, 950, 300, 80) };
        var report = await new Precheck(game, Fast).RunAsync(NewProject(tmp));
        var shot = report.Items.Single(i => i.Id == "testShot");
        Assert.False(shot.Ok);
        Assert.Contains("add the resource that draws it to the resources to stop", shot.Message);
        Assert.Equal(new[] { "boxes" }, shot.Codes);
        Assert.Equal(("1", "0"), (shot.Values["boxes"], shot.Values["outside"]));
        var box = Assert.Single(report.Boxes);
        Assert.True(box.InMap);
        Assert.True(box.X <= 900 && box.Y <= 950 && box.X + box.Width >= 1200 && box.Y + box.Height >= 1030, box.ToString());
        var marked = Images.LoadRgba(Path.Combine(report.Folder, "shot-marked.png"), out _, out _);
        int o = ((box.Y - 3) * 1920 + box.X + 10) * 4;
        Assert.Equal((255, 0, 0), (marked[o], marked[o + 1], marked[o + 2]));
        Assert.False(game.Env);
    }

    [Fact]
    public async Task WhatStandsOutAtTheSidesIsShownButDoesNotFail()
    {
        // FiveM's own watermark sits in the top right corner, far off the part of the frame the map uses
        using var tmp = new TempFolder();
        using var game = new FakeGame { Hud = (1760, 4, 160, 28) };
        var report = await new Precheck(game, Fast).RunAsync(NewProject(tmp));
        var shot = report.Items.Single(i => i.Id == "testShot");
        Assert.True(shot.Ok, shot.Message);
        Assert.Empty(shot.Codes!);
        Assert.Equal(("0", "1"), (shot.Values["boxes"], shot.Values["outside"]));
        var box = Assert.Single(report.Boxes);
        Assert.False(box.InMap);
        Assert.True(report.Ok);
        var marked = Images.LoadRgba(Path.Combine(report.Folder, "shot-marked.png"), out _, out _);
        int o = ((box.Y + box.Height + 2) * 1920 + box.X + 10) * 4;  // the outline under the box: amber
        Assert.Equal((255, 176, 0), (marked[o], marked[o + 1], marked[o + 2]));
        int line = (500 * 1920 + FrameChecks.MapArea(1920, 1080, 1.06).X) * 4;  // the white line around the part the map uses
        Assert.Equal((255, 255, 255), (marked[line], marked[line + 1], marked[line + 2]));
    }

    [Fact]
    public async Task WeatherTimeAndNpcsAreJudgedFromTheResourcesStatus()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { Weather = "RAIN", Hour = 18, NearPeds = 4 };
        var report = await new Precheck(game, Fast).RunAsync(NewProject(tmp));
        var env = report.Items.Single(i => i.Id == "environment");
        Assert.False(env.Ok);
        Assert.Contains("the weather is RAIN", env.Message);
        Assert.Contains("the time is 18:00", env.Message);
        Assert.Contains("4 NPCs", env.Message);
        Assert.Equal(new[] { "weather", "time", "npcs" }, env.Codes);
        Assert.Equal(("RAIN", "18", "4"), (env.Values["weather"], env.Values["hour"], env.Values["nearPeds"]));
    }

    [Fact]
    public async Task WithoutTheGameOnlyTheConnectionIsReported()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { Connectable = false };
        var report = await new Precheck(game, Fast).RunAsync(NewProject(tmp));
        var item = Assert.Single(report.Items);
        Assert.Equal(("console", false), (item.Id, item.Ok));
        Assert.Contains("start FiveM and join the server", item.Message);
        Assert.Equal(new[] { "noConsole" }, item.Codes);
        Assert.Empty(game.Received);
    }

    [Fact]
    public async Task AnotherPlayerOnSkipsTheTestShotAndTouchesNothing()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { Players = 2 };
        var report = await new Precheck(game, Fast).RunAsync(NewProject(tmp));
        Assert.Equal(false, Results(report)["resource"]);
        Assert.Equal(new[] { "players" }, report.Items.Single(i => i.Id == "resource").Codes);
        Assert.False(Results(report).ContainsKey("testShot"));
        Assert.DoesNotContain(game.Received, c => c.StartsWith("stop ") || c.StartsWith("fxmapgen env") || c.StartsWith("fxmapgen tile"));
    }
}
