using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.Tests.Satellite;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Capture;

/// <summary>The visit stage against the fake game, through the job runner like a real run.</summary>
public sealed class VisitTests
{
    static readonly BlockId[] Blocks = new[] { "z8_60_132", "z8_64_132", "z8_60_136", "z8_64_136" }.Select(BlockId.Parse).ToArray();

    /// <summary>Short waits: the fake game answers in milliseconds.</summary>
    static readonly VisitStage.Options Fast = new()
    {
        ResourceVersion = "0.1.0",
        ConnectTimeout = TimeSpan.FromSeconds(1),
        TileTimeout = TimeSpan.FromMilliseconds(400),
        HmapTimeout = TimeSpan.FromSeconds(3),
        SettleBeforeShot = TimeSpan.Zero,
        ShotRetryWait = TimeSpan.FromMilliseconds(1),
        NotificationWait = TimeSpan.FromMilliseconds(20),
        RetryWait = TimeSpan.FromMilliseconds(1),
        StopAtEnd = false,                                                 // the tests of stopping the resource set it
    };

    static string NewProject(TempFolder tmp, string name, IEnumerable<BlockId>? blocks = null)
    {
        var p = Project.Create(Path.Combine(tmp.File(name), "p.fxmapgen.json"));
        p.File.Range.Base = "none";
        p.File.Range.Add = (blocks ?? Blocks).Select(b => b.Name).ToList();
        p.Save();
        return p.FilePath;
    }

    static JobRunner Runner(string project, FakeGame game, bool ortho = false, VisitStage.Options? options = null) => JobRunner.Create(new JobSetup
    {
        ProjectPath = project,
        Workers = 2,
        Processors = 2,
        Memory = new FakeMemory(),
        Stages = (_, _) => new BuildPlan(ortho
            ? new Stage[] { new VisitStage(game, options ?? Fast), new OrthoStage() }
            : new Stage[] { new VisitStage(game, options ?? Fast) }, Array.Empty<string>()),
    });

    static WorkFolder Folder(string project) => new(Path.GetDirectoryName(project)!);

    static Dictionary<string, byte[]> Tiles(string project)
    {
        var root = Path.Combine(Path.GetDirectoryName(project)!, "tiles", "satellite");
        return Directory.GetFiles(root, "*.png", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes);
    }

    [Fact]
    public async Task AVisitThenOrthoGivesTheSameTilesAsTheSameCapturesWrittenDirectly()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame(1 / 1.003, 1 / 1.012);

        var visited = NewProject(tmp, "visited");
        var end = await Runner(visited, game, ortho: true).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal((4, 4), (end.Stages[0].Total, end.Stages[0].Done));

        var folder = Folder(visited);
        // the resource answered with this program's version: the mark that it is on the server
        var placed = ResourcePlacement.Read(folder);
        Assert.Equal(("0.1.0", ResourcePlacement.Answered), (placed?.Version, placed?.How));
        var state = StateStore.Open(folder);
        foreach (var b in Blocks)
        {
            Assert.True(state.Has(b, BlockItem.Shot) && state.Has(b, BlockItem.Height), b.Name);
            var cam = CameraLine.Read(folder.CaptureCamera(b));
            Assert.Equal(b.Center, (cam.X, cam.Y));
            Assert.Equal(282, HeightGrid.Read(folder.CaptureHeights(b)).N);
        }

        // the same frames written as capture files directly (no beacon), made into tiles the same way
        var direct = NewProject(tmp, "direct");
        var dfolder = Folder(direct);
        Directory.CreateDirectory(dfolder.Capture);
        foreach (var b in Blocks) SyntheticCapture.Write(dfolder.Capture, b, 1 / 1.003, 1 / 1.012);
        StateStore.Open(dfolder).SetItems(Blocks.SelectMany(b => new[] { (b, BlockItem.Shot, DateTime.UtcNow), (b, BlockItem.Height, DateTime.UtcNow) }));
        var reference = JobRunner.Create(new JobSetup
        {
            ProjectPath = direct, Workers = 2, Processors = 2, Memory = new FakeMemory(),
            Stages = (_, _) => new BuildPlan(new Stage[] { new OrthoStage() }, Array.Empty<string>()),
        });
        Assert.Equal(JobState.Done, (await reference.RunAsync()).State);

        var a = Tiles(visited);
        var b2 = Tiles(direct);
        Assert.Equal(b2.Keys.Order(), a.Keys.Order());
        Assert.All(a, kv => Assert.True(kv.Value.AsSpan().SequenceEqual(b2[kv.Key]), kv.Key));
    }

    [Fact]
    public async Task TheVisitStopsOnlyRunningResourcesAndPutsEverythingBackAfterwards()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        game.Resources["qbx_density"] = "stopped";                   // not running: left alone
        var project = NewProject(tmp, "p", Blocks.Take(2));
        var end = await Runner(project, game).RunAsync();
        Assert.Equal(JobState.Done, end.State);

        var sent = game.Received;
        Assert.Equal(new[] { "fxmapgen hello", "fxmapgen res qbx_hud chat Renewed-Weathersync qbx_density",
            "stop qbx_hud", "stop chat", "stop Renewed-Weathersync", "cl_drawPerf 0", "fxmapgen env on" }, sent.Take(7));
        // row 33 is odd: east to west
        Assert.Equal(new[] { "fxmapgen tile 8 64 132 2 1.06 1500", "fxmapgen hmap 1", "fxmapgen tile 8 60 132 2 1.06 1500", "fxmapgen hmap 1" }, sent.Skip(7).Take(4));
        Assert.Equal(new[] { "fxmapgen env off", "ensure Renewed-Weathersync", "ensure chat", "ensure qbx_hud" }, sent.Skip(11));
        Assert.All(new[] { "qbx_hud", "chat", "Renewed-Weathersync" }, n => Assert.Equal("started", game.Resources[n]));
        Assert.Equal("stopped", game.Resources["qbx_density"]);
        Assert.Equal("0", game.DrawPerf);
        Assert.False(game.Env);

        var run = end.RunFolder;
        Assert.Equal(new[] { "qbx_hud", "chat", "Renewed-Weathersync" }, File.ReadAllLines(Path.Combine(run, "stopped-resources.txt")));
        var transcript = File.ReadAllText(Path.Combine(run, "console.log"));
        Assert.Contains("> fxmapgen tile 8 60 132 2 1.06 1500", transcript);
        Assert.Contains("[fxmapgen] READY seq=1 ", transcript);
        Assert.Contains("[fxmapgen] HMAP BEGIN", transcript);
        Assert.DoesNotContain("HMAP j=", transcript);                    // the grid rows are in the .hmap files
        Assert.Contains("Stopping resource qbx_hud", transcript);
        Assert.Contains("[fxmapgen] REFILL ok=1 via=qbx_core", transcript);
    }

    [Fact]
    public async Task BlocksGoRowByRowEachRowTheOtherWay()
    {
        var blocks = new[] { "z8_60_128", "z8_64_128", "z8_68_128", "z8_60_132", "z8_64_132", "z8_68_132" }.Select(BlockId.Parse);
        Assert.Equal(new[] { "z8_60_128", "z8_64_128", "z8_68_128", "z8_68_132", "z8_64_132", "z8_60_132" },
            VisitStage.Order(blocks.Reverse()).Select(b => b.Name));
        // by 32 is even: west to east; by 33 odd: east to west (neighbouring blocks keep the streaming warm)
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        var project = NewProject(tmp, "p", blocks);
        await Runner(project, game).RunAsync();
        Assert.Equal(new[] { "60 128", "64 128", "68 128", "68 132", "64 132", "60 132" },
            game.Received.Where(c => c.StartsWith("fxmapgen tile")).Select(c => string.Join(' ', c.Split(' ')[3..5])));
    }

    [Fact]
    public async Task BlackStaleAndNotifiedFramesAreTakenAgainWithinTheBlock()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        // z8_64_132 is visited first, then z8_60_132 gets a black frame, the frame of z8_64_132 and a notification twice
        game.Fault("z8_60_132", ShotFault.Black, ShotFault.Stale, ShotFault.Notification, ShotFault.Notification);
        var project = NewProject(tmp, "p", Blocks.Take(2));
        var end = await Runner(project, game).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(2, game.Received.Count(c => c.StartsWith("fxmapgen tile")));   // one each: the frame was taken again
        var png = Images.LoadRgba(Folder(project).CapturePng(Blocks[0]), out _, out _);
        var frame = new Frame(1920, 1080, png);
        Assert.Equal((true, 2), FrameChecks.Beacon(frame));                 // ready, of the second request: not the stale one
        Assert.False(FrameChecks.NotificationBand(frame));
        Assert.True(png.AsSpan().SequenceEqual(ExpectedFrame(Blocks[0], 2)));
    }

    /// <summary>The frame the fake game shows over the block for request <paramref name="seq"/>.</summary>
    static byte[] ExpectedFrame(BlockId b, int seq)
    {
        var rgba = SyntheticCapture.Render(b);
        FakeGame.Beacon(rgba, true, seq);
        return rgba;
    }

    [Fact]
    public async Task AFailedTryIsRepeatedAndALateReadyOfItIsNotTakenForTheNext()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        game.Fault("z8_64_132", TileFault.LateReady);                         // the first block visited
        game.Fault("z8_60_132", TileFault.Silent, TileFault.Unsettled);
        game.Fault("z8_60_136", TileFault.HmapAbort);
        var project = NewProject(tmp, "p", Blocks.Take(3));
        var end = await Runner(project, game).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(3, end.Stages[0].Done);
        var tiles = game.Received.Where(c => c.StartsWith("fxmapgen tile")).ToList();
        Assert.Equal(2 + 3 + 2, tiles.Count);                                  // late READY: 2 tries, silent + unsettled: 3, abort: 2
        // the first block's frame is of its second request (seq 2), not of the late READY of the first
        var cam = File.ReadAllText(Folder(project).CaptureCamera(BlockId.Parse("z8_64_132")));
        Assert.StartsWith("[fxmapgen] READY seq=2 ", cam);
        var log = File.ReadAllText(Path.Combine(end.RunFolder, "run.log"));
        Assert.Contains("visit z8_64_132: try 2 (no READY within 0.4 s)", log);
        Assert.Contains("visit z8_60_132: try 3 (the scene did not settle in time", log);
        Assert.Contains("visit z8_60_136: try 2 (the height grid was cut off (HMAP ABORT))", log);
    }

    [Fact]
    public async Task WhenEveryBlockIsTakenTheCaptureResourceIsStoppedAndTheRunSaysTheGameIsDone()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        var project = NewProject(tmp, "p", Blocks.Take(2));
        var end = await Runner(project, game, options: Fast with { StopAtEnd = true, StopCheck = TimeSpan.FromMilliseconds(300) }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        var sent = game.Received;
        // after the resources are started again: stop, then it answers no more
        Assert.Equal(new[] { "ensure qbx_hud", "stop fxmapgen-capture", "fxmapgen hello" }, sent.TakeLast(3));
        Assert.False(game.CaptureRunning);
        var done = Assert.Single(end.Announcements);
        Assert.Equal(("gameDone", "fxmapgen-capture", "1"), (done.Key, done.Values["resource"], done.Values["stopped"]));
        Assert.Contains("chat", done.Values["restarted"].Split(','));
        // the stop is kept in the work folder: a connection check made before it holds no longer
        var folder = new WorkFolder(Path.GetDirectoryName(project)!);
        var stop = ResourceStop.Read(folder)!;
        Assert.Equal("fxmapgen-capture", stop.Resource);
        Assert.InRange(stop.AtUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
        var logs = Directory.CreateDirectory(Path.Combine(folder.Logs, "precheck-20260101-000000")).FullName;
        void Report(DateTime at) => File.WriteAllText(Path.Combine(logs, "report.json"),
            System.Text.Json.JsonSerializer.Serialize(new PrecheckReport(at, logs, [], [], false), PrecheckReport.Json));
        Report(stop.AtUtc.AddSeconds(-30));
        Assert.DoesNotContain("resourceStoppedUtc", File.ReadAllText(Path.Combine(logs, "report.json")));   // set when read, never written
        Assert.Equal(stop.AtUtc, PrecheckReport.LatestFor(folder)!.ResourceStoppedUtc);
        Assert.Null(PrecheckReport.Latest(folder.Logs)!.ResourceStoppedUtc);
        Report(stop.AtUtc.AddSeconds(30));                                                                   // a check made after the stop holds
        Assert.Null(PrecheckReport.LatestFor(folder)!.ResourceStoppedUtc);
    }

    [Fact]
    public async Task WithoutThePermissionTheCaptureResourceKeepsRunningAndTheRunSaysSo()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { CanStopCapture = false };
        var project = NewProject(tmp, "p", Blocks.Take(1));
        var end = await Runner(project, game, options: Fast with { StopAtEnd = true, StopCheck = TimeSpan.FromMilliseconds(300) }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        Assert.True(game.CaptureRunning);
        Assert.Equal("0", Assert.Single(end.Announcements).Values["stopped"]);
        Assert.Null(ResourceStop.Read(new WorkFolder(Path.GetDirectoryName(project)!)));        // it still answers: nothing was stopped
    }

    [Fact]
    public async Task ARunThatLeavesABlockLeavesTheCaptureResourceRunningForTheRest()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        game.Fault("z8_64_132", TileFault.Unsettled, TileFault.Unsettled, TileFault.Unsettled);
        var project = NewProject(tmp, "p", Blocks.Take(2));
        var options = Fast with { StopAtEnd = true, StopCheck = TimeSpan.FromMilliseconds(300) };
        var end = await Runner(project, game, options: options).RunAsync();
        Assert.Equal(1, end.Stages[0].Failed);
        Assert.True(game.CaptureRunning);
        Assert.DoesNotContain("stop fxmapgen-capture", game.Received);
        Assert.Empty(end.Announcements);
        Assert.Null(ResourceStop.Read(new WorkFolder(Path.GetDirectoryName(project)!)));

        var again = await Runner(project, game, options: options).RunAsync();   // the rest: now every block is taken
        Assert.Equal(JobState.Done, again.State);
        Assert.False(game.CaptureRunning);
        Assert.NotNull(ResourceStop.Read(new WorkFolder(Path.GetDirectoryName(project)!)));
    }

    [Fact]
    public async Task ABlockThatKeepsFailingFailsAloneAndTheNextRunTakesItAgain()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        game.Fault("z8_64_132", TileFault.Unsettled, TileFault.Unsettled, TileFault.Unsettled);
        var project = NewProject(tmp, "p", Blocks.Take(3));
        var end = await Runner(project, game).RunAsync();
        Assert.Equal((2, 1), (end.Stages[0].Done, end.Stages[0].Failed));
        var failure = Assert.Single(end.Failures);
        Assert.Equal("z8_64_132", failure.Unit);
        Assert.StartsWith("the scene did not settle in time", failure.Message);

        var again = await Runner(project, game).RunAsync();               // the fault is used up
        Assert.Equal(JobState.Done, again.State);
        Assert.Equal((1, 1), (again.Stages[0].Total, again.Stages[0].Done));
    }

    [Fact]
    public async Task ARefusalStopsTheRunWithTheReasonAndTidiesUp()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        game.Fault("z8_60_132", TileFault.Refused);                           // the second block visited
        var project = NewProject(tmp, "p");
        var end = await Runner(project, game).RunAsync();
        Assert.Equal(JobState.Stopped, end.State);
        Assert.Equal("the server would not clear the world: 2 players are on", end.StopReason);
        Assert.Equal((1, 1), (end.Stages[0].Done, end.Stages[0].Failed));
        Assert.Equal(2, game.Received.Count(c => c.StartsWith("fxmapgen tile")));    // no try after the refusal, no next block
        Assert.Contains("fxmapgen env off", game.Received);
        Assert.False(game.Env);
    }

    [Fact]
    public async Task AGameWindowThatChangesSizeStopsTheRun()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        game.Fault("z8_60_132", ShotFault.Resize);                              // the second block visited
        var project = NewProject(tmp, "p");
        var end = await Runner(project, game).RunAsync();
        Assert.Equal(JobState.Stopped, end.State);
        Assert.Equal("the game window changed to 1280 x 720", end.StopReason);
        Assert.Equal((1, 1), (end.Stages[0].Done, end.Stages[0].Failed));
        Assert.Contains("fxmapgen env off", game.Received);
    }

    [Theory]
    [InlineData("version", "the resource on the server is version 0.0.9, this program 0.1.0")]
    [InlineData("server", "the resource's server side did not answer")]
    [InlineData("ace", "this player may not clear the world")]
    [InlineData("players", "3 players are on the server")]
    [InlineData("window", "the game window's drawing area is 2560 x 1440")]
    [InlineData("console", "no connection to the game console at 127.0.0.1:29200")]
    public async Task TheVisitDoesNotStartWhenTheGameIsNotReadyForIt(string what, string message)
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        switch (what)
        {
            case "version": game.Version = "0.0.9"; break;
            case "server": game.ServerAnswers = false; break;
            case "ace": game.Ace = false; break;
            case "players": game.Players = 3; break;
            case "window": game.Size = (2560, 1440); break;
            case "console": game.Connectable = false; break;
        }
        var project = NewProject(tmp, "p", Blocks.Take(1));
        var end = await Runner(project, game).RunAsync();
        Assert.Equal(JobState.Failed, end.State);
        Assert.Contains(message, end.Error);
        Assert.DoesNotContain(game.Received, c => c.StartsWith("fxmapgen tile") || c.StartsWith("stop "));
    }

    [Fact]
    public async Task StopNowMidVisitStillPutsTheEnvironmentOffAndTheNextRunContinues()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { TileMs = 60 };
        var project = NewProject(tmp, "p");
        var runner = Runner(project, game);
        runner.Changed += () => { if (runner.Snapshot().Stages[0].Done >= 1) runner.RequestStop(StopMode.Now); };
        var end = await runner.RunAsync();
        Assert.Equal(JobState.Stopped, end.State);
        int done = end.Stages[0].Done;
        Assert.InRange(done, 1, 2);
        Assert.Equal("fxmapgen env off", game.Received.SkipWhile(c => c != "fxmapgen env off").First());
        Assert.False(game.Env);
        Assert.All(new[] { "qbx_hud", "chat", "Renewed-Weathersync", "qbx_density" }, n => Assert.Equal("started", game.Resources[n]));

        var rest = await Runner(project, game).RunAsync();
        Assert.Equal(JobState.Done, rest.State);
        Assert.Equal(4 - done, rest.Stages[0].Total);
        var state = StateStore.Open(Folder(project));
        Assert.All(Blocks, b => Assert.True(state.Has(b, BlockItem.Shot)));
    }

    [Fact]
    public async Task ABlockMarkedForRetakeLosesItsOldItemsWhenVisited()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        var project = NewProject(tmp, "p", Blocks.Take(1));
        var state = StateStore.Open(Folder(project));
        var old = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        state.SetItems(new[] { (Blocks[0], BlockItem.Shot, old), (Blocks[0], BlockItem.Height, old), (Blocks[0], BlockItem.ScanGround, old) });
        state.MarkForRetake(new[] { Blocks[0] });
        Assert.Equal(1, new VisitStage(game).CountReady(Project.Load(project), state));

        await Runner(project, game).RunAsync();
        var after = StateStore.Open(Folder(project));
        Assert.False(after.IsMarkedForRetake(Blocks[0]));
        Assert.True(after.ItemTime(Blocks[0], BlockItem.Shot) > old);
        Assert.False(after.Has(Blocks[0], BlockItem.ScanGround));           // stale: to be scanned again
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(null)]
    public async Task EveryItemTakenKeepsTheCaptureNumberTheResourceSaid(int? capture)
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { CaptureNumber = capture };
        var project = NewProject(tmp, "p", Blocks.Take(2));
        var state = StateStore.Open(Folder(project));
        var old = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        state.SetItems(new[] { (Blocks[1], BlockItem.Shot, old), (Blocks[1], BlockItem.Height, old) });   // imported: not known

        await Runner(project, game).RunAsync();
        var after = StateStore.Open(Folder(project));
        Assert.Equal(capture, after.ItemCapture(Blocks[0], BlockItem.Shot));
        Assert.Equal(capture, after.ItemCapture(Blocks[0], BlockItem.Height));
        Assert.Null(after.ItemCapture(Blocks[1], BlockItem.Shot));          // not visited again
        Assert.Null(after.ItemCapture(Blocks[0], BlockItem.ScanGround));    // not taken
        after.MarkForRetake(new[] { Blocks[0] });
        Assert.Null(after.ItemCapture(Blocks[0], BlockItem.Shot));
    }
}
