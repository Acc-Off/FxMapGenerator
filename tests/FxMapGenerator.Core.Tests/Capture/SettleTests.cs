using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Capture;

/// <summary>
/// The settle measurement against the fake game (frames every 40 ms for 0.6 s instead of every 0.2 s for 3 s), and how
/// the pre-check hands the wait to the visit.
/// </summary>
[Collection(nameof(Timing))]
public sealed class SettleTests
{
    internal static readonly SettleProbe.Options Quick = new() { Every = TimeSpan.FromMilliseconds(40), For = TimeSpan.FromMilliseconds(600) };

    static async Task<SettleMeasurement> Measure(FakeGame game, (double X, double Y)? player = null, string? folder = null)
    {
        await using var link = new GameLink(game.OpenConsole("127.0.0.1", 29200));
        Assert.True(await link.ConnectAsync(TimeSpan.FromSeconds(1), default));
        Assert.True(await link.EnvOnAsync(default));
        var (x, y) = player ?? game.Player;
        return await new SettleProbe(link, game.Window, Quick).RunAsync(x, y, folder, default);
    }

    static string[] Tiles(FakeGame game) => game.Received.Where(c => c.StartsWith("fxmapgen tile ")).ToArray();

    [Fact]
    public async Task TheTimeTheTownStaysCoarseAfterTheStreamingIsFound()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { HonourQuiet = true, CoarseMs = 250 };
        var m = await Measure(game, folder: tmp.Path);

        // the player stands in Los Santos: Paleto Bay, its first two blocks, READY as soon as the streaming ended
        Assert.True(m.Ok);
        Assert.Equal("north", m.District);
        Assert.Equal(new[] { "fxmapgen tile 8 52 28 2 1.06 0", "fxmapgen tile 8 56 28 2 1.06 0" }, Tiles(game));
        Assert.Equal(new[] { "z8_52_28", "z8_56_28" }, m.Blocks.Select(b => b.Block));
        foreach (var b in m.Blocks)
        {
            Assert.Null(b.Problem);
            Assert.InRange(b.StableMs!.Value, 200, 360);
            Assert.Equal(60.0, b.Fps);
            // before that time the frames differ from the last one, from it on they do not
            Assert.All(b.Samples.Where(s => s.Ms < b.StableMs), s => Assert.True(s.Changed > 0.01, $"{s.Ms} ms: {s.Changed}"));
            Assert.All(b.Samples.Where(s => s.Ms >= b.StableMs), s => Assert.Equal(0.0, s.Changed));
            Assert.InRange(b.Samples.Count, 16, 16);
        }
        Assert.Equal(m.Blocks.Max(b => b.StableMs), m.StableMs);
        Assert.Equal((int)Math.Ceiling(1.5 * m.StableMs!.Value / 10.0) * 10, m.WaitMs);
        Assert.Equal(60.0, m.Fps);

        // the first and the last frame of each block: the part of the frame the map uses
        var area = FrameChecks.MapArea(1920, 1080, 1.06);
        var first = Images.LoadRgba(Path.Combine(tmp.Path, "settle-z8_52_28-first.png"), out int w, out int h);
        Assert.Equal((area.Width, area.Height), (w, h));
        var last = Images.LoadRgba(Path.Combine(tmp.Path, "settle-z8_52_28-last.png"), out _, out _);
        Assert.True(FrameChecks.Changed(new Frame(w, h, first), new Frame(w, h, last), 24) > 0.01);
    }

    [Fact]
    public async Task AFewPixelsThatKeepChangingDoNotCount()
    {
        using var game = new FakeGame { HonourQuiet = true, Speckle = true };
        var m = await Measure(game);
        Assert.True(m.Ok);
        foreach (var b in m.Blocks)
        {
            Assert.Equal(b.Samples[0].Ms, b.StableMs);
            Assert.All(b.Samples.Take(b.Samples.Count - 1), s => Assert.InRange(s.Changed!.Value, 1e-6, 0.0005));
        }
        Assert.InRange(m.WaitMs, 0, 60);
    }

    [Fact]
    public async Task ABlockThatNeverStopsChangingOrHasWaterMakesWayForTheNextOne()
    {
        using var game = new FakeGame { HonourQuiet = true };
        game.NeverStill.Add("z8_52_28");
        game.Water["z8_56_28"] = 2;
        var m = await Measure(game);
        Assert.True(m.Ok);
        Assert.Equal(new[] { ("z8_52_28", "unsettled"), ("z8_56_28", "water"), ("z8_52_32", (string?)null), ("z8_60_28", null) },
            m.Blocks.Select(b => (b.Block, b.Problem)));
        Assert.Empty(m.Blocks[1].Samples);                                  // no frames over water
        Assert.All(m.Blocks[0].Samples.Take(15), s => Assert.True(s.Changed > 0.0005));
        Assert.Equal(4, Tiles(game).Length);
    }

    [Fact]
    public async Task WithoutTwoMeasuredBlocksTheWaitStaysTheDefault()
    {
        using var game = new FakeGame { HonourQuiet = true };
        foreach (var b in SettleProbe.Districts[0].Blocks) game.NeverStill.Add(b.Name);
        var m = await Measure(game);
        Assert.False(m.Ok);
        Assert.Null(m.StableMs);
        Assert.Equal(SettleProbe.DefaultWaitMs, m.WaitMs);
        Assert.All(m.Blocks, b => Assert.Equal("unsettled", b.Problem));
        Assert.Equal(60.0, m.Fps);                                        // from every READY
    }

    [Fact]
    public async Task ABlockWithoutReadyIsReportedWithTheReason()
    {
        using var game = new FakeGame { HonourQuiet = true };
        game.Fault("z8_52_28", TileFault.Unsettled);
        var m = await Measure(game);
        Assert.True(m.Ok);
        Assert.Equal(("tile", "the scene did not settle in time (scene 400 ms, collision 200 ms, settle 15000 ms)"), (m.Blocks[0].Problem, m.Blocks[0].Reason));
        Assert.Equal(new[] { "z8_56_28", "z8_52_32" }, m.Blocks.Where(b => b.StableMs is not null).Select(b => b.Block));
    }

    [Fact]
    public void TheDistrictFartherFromThePlayerIsUsed()
    {
        Assert.Equal("north", SettleProbe.Farther(200, -900).Id);           // Legion Square
        Assert.Equal("south", SettleProbe.Farther(-150, 6300).Id);          // Paleto Bay
        Assert.Equal("south", SettleProbe.Farther(1700, 3600).Id);          // Sandy Shores: 3.2 km from Paleto Bay, 4.5 km from downtown
        Assert.Equal("north", SettleProbe.Farther(null, null).Id);
        // the candidates are dry built-up blocks next to each other
        foreach (var d in SettleProbe.Districts)
            for (int i = 1; i < d.Blocks.Count; i++)
                Assert.True(Math.Abs(d.Blocks[i].Bx - d.Blocks[0].Bx) <= 2 && Math.Abs(d.Blocks[i].By - d.Blocks[0].By) <= 1, d.Blocks[i].Name);
    }

    [Fact]
    public async Task ThePlayerInTheNorthMeasuresDowntown()
    {
        using var game = new FakeGame { HonourQuiet = true, Player = (-150, 6300) };
        var m = await Measure(game);
        Assert.Equal("south", m.District);
        Assert.Equal(new[] { "fxmapgen tile 8 60 124 2 1.06 0", "fxmapgen tile 8 60 128 2 1.06 0" }, Tiles(game));
    }

    // ------------------------------------------------------------------ pre-check -> visit

    static readonly Precheck.Options PrecheckFast = new()
    {
        ResourceVersion = "0.1.0",
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
        TileTimeout = TimeSpan.FromSeconds(2),
        SettleBeforeShot = TimeSpan.Zero,
        ShotRetryWait = TimeSpan.FromMilliseconds(1),
        Settle = Quick,
    };

    static readonly VisitStage.Options VisitFast = new()
    {
        ResourceVersion = "0.1.0",
        ConnectTimeout = TimeSpan.FromSeconds(1),
        TileTimeout = TimeSpan.FromSeconds(4),                              // READY comes after the wait
        HmapTimeout = TimeSpan.FromSeconds(3),
        SettleBeforeShot = TimeSpan.Zero,
        ShotRetryWait = TimeSpan.FromMilliseconds(1),
        RetryWait = TimeSpan.FromMilliseconds(1),
        StopAtEnd = false,                                                 // the tests of stopping the resource set it
    };

    static Project NewProject(TempFolder tmp)
    {
        var p = Project.Create(tmp.File("p.fxmapgen.json"));
        p.File.Range.Base = "none";
        p.File.Range.Add = ["z8_60_132"];
        p.Save();
        return p;
    }

    static async Task<(JobSnapshot End, string Tile)> Visit(Project project, FakeGame game)
    {
        var end = await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath,
            Workers = 1,
            Processors = 1,
            Memory = new FakeMemory(),
            Stages = (_, _) => new BuildPlan([new VisitStage(game, VisitFast)], []),
        }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        return (end, game.Received.Last(c => c.StartsWith("fxmapgen tile ")));
    }

    [Fact]
    public async Task ThePrecheckKeepsTheMeasuredWaitForTheVisit()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { HonourQuiet = true, CoarseMs = 150 };
        var project = NewProject(tmp);
        var report = await new Precheck(game, PrecheckFast).RunAsync(project);

        var item = report.Items.Single(i => i.Id == "settle");
        Assert.True(item.Ok, item.Message);
        Assert.Equal(("north", "z8_52_28 z8_56_28", "60", "600"), (item.Values["district"], item.Values["blocks"], item.Values["fps"], item.Values["forMs"]));
        var m = report.Settle!;
        Assert.Equal(m.WaitMs.ToString(), item.Values["waitMs"]);
        Assert.InRange(m.StableMs!.Value, 40, 400);                          // the time itself: TheTimeTheTownStaysCoarse...
        // the measurement ran in the capture environment of the test shot, after it and before env off
        var sent = game.Received.ToList();
        int shot = sent.IndexOf("fxmapgen tile 8 0 0 2 1.06 1500"), first = sent.IndexOf("fxmapgen tile 8 52 28 2 1.06 0"), off = sent.IndexOf("fxmapgen env off");
        Assert.True(shot >= 0 && shot < first && first < off, string.Join(" | ", sent));
        Assert.True(File.Exists(Path.Combine(report.Folder, "settle-z8_56_28-last.png")));
        Assert.Contains("(settle z8_52_28: pixels changed from the last frame by more than 8/24/48 levels: ", File.ReadAllText(Path.Combine(report.Folder, "console.log")));

        var stored = new SettleStore(new WorkFolder(project.WorkFolderPath)).Load();
        Assert.Equal(m.WaitMs, stored!.WaitMs);
        Assert.Equal(m.Blocks.Select(b => b.StableMs), stored.Blocks.Select(b => b.StableMs));
        Assert.Equal(m.WaitMs, PrecheckReport.Latest(Path.Combine(project.WorkFolderPath, "logs"))!.Settle!.WaitMs);

        // the visit has the resource wait that long
        var (end, tile) = await Visit(project, game);
        Assert.Equal($"fxmapgen tile 8 60 132 2 1.06 {m.WaitMs}", tile);
        Assert.Contains(FormattableString.Invariant($"visit: settle wait {m.WaitMs / 1000.0:0.00} s (measured "), File.ReadAllText(Path.Combine(end.RunFolder, "run.log")));
    }

    [Fact]
    public async Task AFailedMeasurementInANewWorkFolderLeavesNoFile()
    {
        // no state/ folder yet: there is nothing to remove
        using var tmp = new TempFolder();
        using var game = new FakeGame { HonourQuiet = true };
        foreach (var b in SettleProbe.Districts[0].Blocks) game.NeverStill.Add(b.Name);
        var project = NewProject(tmp);
        Assert.False(Directory.Exists(Path.Combine(project.WorkFolderPath, "state")));
        var report = await new Precheck(game, PrecheckFast).RunAsync(project);
        Assert.Equal(false, report.Items.Single(i => i.Id == "settle").Ok);
        Assert.Null(new SettleStore(new WorkFolder(project.WorkFolderPath)).Load());
        Assert.False(game.Env);                                             // the game was put back
    }

    [Fact]
    public async Task AFailedMeasurementLeavesTheVisitTheDefault()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { HonourQuiet = true };
        var project = NewProject(tmp);
        Assert.True((await new Precheck(game, PrecheckFast).RunAsync(project)).Ok);
        Assert.NotNull(new SettleStore(new WorkFolder(project.WorkFolderPath)).Load());

        foreach (var b in SettleProbe.Districts[0].Blocks) game.NeverStill.Add(b.Name);
        var report = await new Precheck(game, PrecheckFast).RunAsync(project);
        var item = report.Items.Single(i => i.Id == "settle");
        Assert.False(item.Ok);
        Assert.Equal(new[] { "unsettled" }, item.Codes);
        Assert.Contains("the visit waits the default 1.5 s", item.Message);
        Assert.Equal(("", "1500"), (item.Values["stableMs"], item.Values["waitMs"]));
        Assert.False(report.Ok);
        Assert.Null(new SettleStore(new WorkFolder(project.WorkFolderPath)).Load());   // the earlier measurement is gone

        var (end, tile) = await Visit(project, game);
        Assert.Equal("fxmapgen tile 8 60 132 2 1.06 1500", tile);
        Assert.Contains("visit: settle wait 1.50 s (the default: no measurement; the pre-check measures it)", File.ReadAllText(Path.Combine(end.RunFolder, "run.log")));
    }
}
