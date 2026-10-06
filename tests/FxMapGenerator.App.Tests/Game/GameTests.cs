using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using FxMapGenerator.App.Game;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Tests.FxConsole;
using FxMapGenerator.App.Web;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;
using Microsoft.Extensions.DependencyInjection;

namespace FxMapGenerator.App.Tests.Game;

/// <summary>
/// A game played by the console emulator: answers the fxmapgen commands with the resource's lines, over the real socket
/// protocol (PRNT frames, one per line).
/// </summary>
internal static class ScriptedGame
{
    public static int Seq;

    public static IEnumerable<string> Answer(string command)
    {
        var inv = CultureInfo.InvariantCulture;
        var w = command.Split(' ');
        if (w[0] == "stop") return new[] { "Stopping resource " + w[1] };
        if (w[0] != "fxmapgen") return Array.Empty<string>();
        switch (w[1])
        {
            case "hello": return new[] { "[fxmapgen] HELLO proto=1 ver=" + AppVersion.Value + " res=fxmapgen-capture server=" + AppVersion.Value + " ace=1 players=1 build=3258" };
            case "res": return w.Skip(2).Select(n => $"[fxmapgen] RES {n} started").Prepend($"[fxmapgen] RES BEGIN n={w.Length - 2}").Append("[fxmapgen] RES END");
            case "env": return new[] { w[2] == "on" ? "[fxmapgen] ENV on" : "[fxmapgen] ENV off safe=1 x=1.0 y=2.0 z=3.0", };
            case "tile":
                var b = BlockId.Parse($"z8_{w[3]}_{w[4]}");
                return new[]
                {
                    "[fxmapgen] ground probe failed with the ped at z=300",
                    string.Format(inv, "[fxmapgen] READY seq={0} x={1:F4} y={2:F4} gz=12.500 h=8539.785 fov=2.000 margin=1.060 scene=100 coll=0 settle=1440 maxreq=0 water=0 veh=3 peds=4 block={3}",
                        Interlocked.Increment(ref Seq), b.Center.X, b.Center.Y, b.Name),
                };
            case "hmap":
                var lines = new List<string> { "[fxmapgen] HMAP BEGIN seq=1 z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=1.000 n=282 block=z8_60_132" };
                for (int j = 0; j < 282; j++)
                    for (int k = 0; k < 3; k++)
                        lines.Add($"[fxmapgen] HMAP j={j} k={k} " + string.Join(' ', Enumerable.Range(0, k < 2 ? 100 : 82).Select(i => ((j + k * 100 + i) % 50 / 10.0).ToString("0.0", inv))));
                lines.Add("[fxmapgen] HMAP END nohit=0 ms=900");
                return lines;
            default: return Array.Empty<string>();
        }
    }
}

public sealed class GameLinkOverTheSocketTests
{
    [Fact]
    public async Task ATileAndItsHeightGridComeThroughTheConsoleSocket()
    {
        await using var emulator = new FxConsoleEmulator(new FxConsoleEmulatorOptions { Port = 0, IdleTimeout = TimeSpan.Zero, Responder = ScriptedGame.Answer });
        await emulator.StartAsync();
        var transcript = new StringWriter();
        await using var link = new GameLink(new LocalGame(new NoGame()).OpenConsole("127.0.0.1", emulator.Port), transcript);
        Assert.True(await link.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None));

        var hello = await link.HelloAsync(CancellationToken.None);
        Assert.Equal((1, AppVersion.Value, true, 1), (hello!.Proto, hello.Version, hello.Ace, hello.Players));

        var tile = await link.TileAsync(BlockId.Parse("z8_60_132"), 2, 1.06, link.LastSeq, 1500, CancellationToken.None);
        Assert.Equal(TileOutcome.Ready, tile.Outcome);
        Assert.Equal(12.5, tile.Line!.Number("gz"));

        var hmap = await link.HmapAsync(1, CancellationToken.None);
        Assert.Null(hmap.Failure);
        Assert.Equal(1 + 282 * 3 + 1, hmap.Lines!.Count);
        using var tmp = new TempFiles();
        var file = tmp.Write("z8_60_132.hmap", string.Join('\n', hmap.Lines) + "\n");
        var grid = FxMapGenerator.Core.Satellite.HeightGrid.Read(file);
        Assert.Equal((282, 0.0), (grid.N, grid.Missing * 1.0));
        Assert.Equal(1.3, grid.Values[3 * 282 + 10], 6);                          // j=3, i=10: (3 + 10) % 50 / 10
        Assert.Equal(new[] { "fxmapgen hello", "fxmapgen tile 8 60 132 2 1.06 1500", "fxmapgen hmap 1" }, emulator.ReceivedCommands);
        Assert.Contains("[fxmapgen] ground probe failed with the ped at z=300", transcript.ToString());
    }

    [Fact]
    public async Task OnlyOneConsoleOfTheProgramIsOpenAtATime()
    {
        var game = new LocalGame(new NoGame());
        var first = game.OpenConsole("127.0.0.1", 1);
        Assert.Throws<InvalidOperationException>(() => game.OpenConsole("127.0.0.1", 1));
        await first.DisposeAsync();
        await using var second = game.OpenConsole("127.0.0.1", 1);
    }
}

public sealed class GameEndpointsTests
{
    const string LowSettings = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n\r\n<Settings>\r\n  <graphics>\r\n    <TextureQuality value=\"0\" />\r\n" +
        "    <MaxLodScale value=\"0.000000\" />\r\n  </graphics>\r\n  <video>\r\n    <ScreenWidth value=\"1920\" />\r\n  </video>\r\n</Settings>\r\n";

    static async Task<JsonElement> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    [Fact]
    public async Task TheGraphicsSettingsAreComparedPutInAndPutBack()
    {
        await using var host = await TestHost.StartAsync();
        var settings = Path.Combine(host.DataDirectory, "gta5_settings.xml");

        var none = await Json(await host.Client.GetAsync("/api/game/render"));
        Assert.False(none.GetProperty("exists").GetBoolean());
        Assert.Equal(404, (int)(await host.Client.PostAsync("/api/game/render/apply", null)).StatusCode);

        File.WriteAllText(settings, LowSettings);
        var before = await Json(await host.Client.GetAsync("/api/game/render"));
        Assert.Contains(before.GetProperty("differences").EnumerateArray(), d => d.GetProperty("key").GetString() == "TextureQuality");

        var applied = await host.Client.PostAsync("/api/game/render/apply", null);
        Assert.Equal(200, (int)applied.StatusCode);
        var change = await Json(applied);
        Assert.StartsWith("gta5_settings-", change.GetProperty("backup").GetString());
        Assert.Equal(0, change.GetProperty("status").GetProperty("differences").GetArrayLength());
        Assert.Contains("<TextureQuality value=\"2\" />\r\n", File.ReadAllText(settings));

        var restored = await host.Client.PostAsJsonAsync("/api/game/render/restore", new { });
        Assert.Equal(200, (int)restored.StatusCode);
        Assert.Equal(LowSettings, File.ReadAllText(settings));
    }

    [Fact]
    public async Task TheSettingsAreNotChangedWhileFiveMRuns()
    {
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s =>
            s.AddSingleton(new RenderSettingsFile(Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"), "gta5_settings.xml"), Path.GetTempPath(), () => true)));
        var r = await host.Client.PostAsync("/api/game/render/apply", null);
        Assert.Equal(409, (int)r.StatusCode);
        Assert.Equal("FIVEM_RUNNING", (await Json(r)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ThePrecheckTalksToTheGameAndItsReportFeedsTheChecks()
    {
        await using var emulator = new FxConsoleEmulator(new FxConsoleEmulatorOptions { Port = 0, IdleTimeout = TimeSpan.Zero, Responder = ScriptedGame.Answer });
        await emulator.StartAsync();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IGameAccess>(new LocalGame(new NoGame())));
        using var tmp = new TempFiles();
        var project = Project.Create(tmp.Path("p.fxmapgen.json"));
        project.File.Console.Port = emulator.Port;
        project.Save();
        Assert.Equal(200, (int)(await host.Client.PostAsJsonAsync("/api/project/open", new { path = project.FilePath })).StatusCode);
        Assert.Equal(204, (int)(await host.Client.GetAsync("/api/game/precheck")).StatusCode);

        var report = await Json(await host.Client.PostAsync("/api/game/precheck", null));
        var items = report.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i);
        Assert.True(items["console"].GetProperty("ok").GetBoolean());
        Assert.True(items["resource"].GetProperty("ok").GetBoolean());
        Assert.False(items["window"].GetProperty("ok").GetBoolean());            // no game window here
        Assert.Equal("started", items["resources"].GetProperty("values").GetProperty("qbx_hud").GetString());
        Assert.False(items.ContainsKey("testShot"));                              // not with a wrong window
        Assert.DoesNotContain(emulator.ReceivedCommands, c => c.StartsWith("stop ") || c.StartsWith("fxmapgen tile"));

        var latest = await Json(await host.Client.GetAsync("/api/game/precheck"));
        Assert.Equal(report.GetProperty("folder").GetString(), latest.GetProperty("folder").GetString());
        Assert.Contains("[fxmapgen] HELLO proto=1", await host.Client.GetStringAsync("/api/game/precheck/files/console.log"));
        Assert.Equal(404, (int)(await host.Client.GetAsync("/api/game/precheck/files/shot.png")).StatusCode);
        Assert.Equal(404, (int)(await host.Client.GetAsync("/api/game/precheck/files/..%2Fp.fxmapgen.json")).StatusCode);

        var checks = (await Json(await host.Client.GetAsync("/api/project/checks"))).EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c);
        Assert.False(checks["game"].GetProperty("ok").GetBoolean());             // the window is part of the game row
        Assert.Equal(JsonValueKind.Null, checks["render"].GetProperty("ok").ValueKind);   // no settings file in the scratch data folder
        Assert.False(checks["preset"].GetProperty("ok").GetBoolean());           // the test shot was not taken

        // the program stopped the capture resource after the check (a capture ended): the check counts as not made,
        // the report says since when
        Assert.False(latest.TryGetProperty("resourceStoppedUtc", out _));
        var stop = new ResourceStop(DateTime.UtcNow, "fxmapgen-capture");
        stop.Write(new FxMapGenerator.Core.State.WorkFolder(project.WorkFolderPath));
        var stale = await Json(await host.Client.GetAsync("/api/game/precheck"));
        Assert.Equal(stop.AtUtc, stale.GetProperty("resourceStoppedUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(report.GetProperty("folder").GetString(), stale.GetProperty("folder").GetString());
        checks = (await Json(await host.Client.GetAsync("/api/project/checks"))).EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c);
        Assert.Equal(JsonValueKind.Null, checks["game"].GetProperty("ok").ValueKind);
        Assert.Equal(JsonValueKind.Null, checks["preset"].GetProperty("ok").ValueKind);
        Assert.Equal("", checks["game"].GetProperty("values").GetProperty("checkedAt").GetString());

        // checked again: the new report holds
        var again = await Json(await host.Client.PostAsync("/api/game/precheck", null));
        Assert.False(again.TryGetProperty("resourceStoppedUtc", out _));
        Assert.False((await Json(await host.Client.GetAsync("/api/game/precheck"))).TryGetProperty("resourceStoppedUtc", out _));
        checks = (await Json(await host.Client.GetAsync("/api/project/checks"))).EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c);
        Assert.False(checks["game"].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TheCheckTheResourceStartAndARunsWorkInTheGameTakeTheGameInTurn()
    {
        // a game that holds its answer to hello until the test lets it go
        using var hold = new ManualResetEventSlim();
        using var asked = new ManualResetEventSlim();
        IEnumerable<string> Answer(string command)
        {
            if (command == "fxmapgen hello")
            {
                asked.Set();
                hold.Wait(TimeSpan.FromSeconds(30));
            }
            return ScriptedGame.Answer(command);
        }
        await using var emulator = new FxConsoleEmulator(new FxConsoleEmulatorOptions { Port = 0, IdleTimeout = TimeSpan.Zero, Responder = Answer });
        await emulator.StartAsync();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IGameAccess>(new LocalGame(new NoGame())));
        using var tmp = new TempFiles();
        var project = Project.Create(tmp.Path("p.fxmapgen.json"));
        project.File.Console.Port = emulator.Port;
        project.Save();
        // a project with nothing to take in the game: its runs do not need the game
        Directory.CreateDirectory(tmp.Path("e"));
        var empty = Project.Create(Path.Combine(tmp.Path("e"), "e.fxmapgen.json"));
        empty.File.Range = new RangeSetting { Base = "none" };
        empty.Save();
        Assert.Equal(200, (int)(await host.Client.PostAsJsonAsync("/api/project/open", new { path = project.FilePath })).StatusCode);
        var jobs = host.App.Services.GetRequiredService<JobManager>();
        static async Task<string?> Code(HttpResponseMessage r) => (await Json(r)).GetProperty("error").GetProperty("code").GetString();
        Assert.Null(jobs.GameByHand);

        // while the connection check waits for the game
        var check = host.Client.PostAsync("/api/game/precheck", null);
        Assert.True(asked.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(JobManager.Precheck, jobs.GameByHand);
        var start = await host.Client.PostAsync("/api/game/resource/start", null);
        Assert.Equal((409, "GAME_CHECKING"), ((int)start.StatusCode, await Code(start)));
        var second = await host.Client.PostAsync("/api/game/precheck", null);
        Assert.Equal((409, "GAME_CHECKING"), ((int)second.StatusCode, await Code(second)));
        // a run with work in the game does not start, and leaves nothing behind (no run folder, the work folder free)
        var run = await host.Client.PostAsJsonAsync("/api/jobs", new { project = project.FilePath });
        Assert.Equal((409, "GAME_CHECKING"), ((int)run.StatusCode, await Code(run)));
        Assert.Empty(Directory.GetDirectories(Path.Combine(project.WorkFolderPath, "logs"), "run-*"));
        Assert.False(jobs.IsRunning);
        // a run without work in the game starts
        Assert.Equal(202, (int)(await host.Client.PostAsJsonAsync("/api/jobs", new { project = empty.FilePath })).StatusCode);
        await jobs.WaitAsync();
        hold.Set();
        Assert.Equal(200, (int)(await check).StatusCode);
        Assert.Null(jobs.GameByHand);

        // while the capture resource is being started
        asked.Reset();
        hold.Reset();
        var starting = host.Client.PostAsync("/api/game/resource/start", null);
        Assert.True(asked.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(JobManager.ResourceStart, jobs.GameByHand);
        var checking = await host.Client.PostAsync("/api/game/precheck", null);
        Assert.Equal((409, "GAME_STARTING"), ((int)checking.StatusCode, await Code(checking)));
        run = await host.Client.PostAsJsonAsync("/api/jobs", new { project = project.FilePath });
        Assert.Equal((409, "GAME_STARTING"), ((int)run.StatusCode, await Code(run)));
        hold.Set();
        var started = await starting;
        Assert.Equal(200, (int)started.StatusCode);
        Assert.Equal("running", (await Json(started)).GetProperty("state").GetString());
        Assert.Null(jobs.GameByHand);
    }
}

/// <summary>A scratch folder under %TEMP% for a test.</summary>
internal sealed class TempFiles : IDisposable
{
    readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));

    public TempFiles() => Directory.CreateDirectory(_root);

    public string Path(string name) => System.IO.Path.Combine(_root, name);

    public string Write(string name, string text)
    {
        var p = Path(name);
        File.WriteAllText(p, text);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
