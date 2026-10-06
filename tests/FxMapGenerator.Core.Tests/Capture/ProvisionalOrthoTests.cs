using System.Globalization;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.Tests.Satellite;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Capture;

/// <summary>The satellite map made while the visit goes on (provisional scale correction), confirmed after it.</summary>
public sealed class ProvisionalOrthoTests
{
    static readonly BlockId[] Blocks = new[] { "z8_60_132", "z8_64_132", "z8_60_136", "z8_64_136" }.Select(BlockId.Parse).ToArray();

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

    static string NewProject(TempFolder tmp, string name)
    {
        var p = Project.Create(Path.Combine(tmp.File(name), "p.fxmapgen.json"));
        p.File.Range.Base = "none";
        p.File.Range.Add = Blocks.Select(b => b.Name).ToList();
        p.Save();
        return p.FilePath;
    }

    /// <summary>
    /// Visit (with the follower when given) and ortho, like a build of the satellite map. The lower zooms after them are
    /// left out: they rebuild every parent from the z8 tiles whoever made them (and paint the sea over the whole map, slow).
    /// </summary>
    static JobRunner Runner(string project, FakeGame? game, int workers, IVisitFollower? follower, (double, double)? scale = null) => JobRunner.Create(new JobSetup
    {
        ProjectPath = project,
        Workers = workers,
        Processors = 4,
        Memory = new FakeMemory(),
        Stages = (_, _) => new BuildPlan(
            (game is null ? Array.Empty<Stage>() : new Stage[] { new VisitStage(game, Fast) { Follower = follower } })
                .Append(new OrthoStage(new OrthoStage.Options(scale))).ToArray(),
            Array.Empty<string>()),
    });

    static WorkFolder Folder(string project) => new(Path.GetDirectoryName(project)!);

    static string TileRoot(string project) => Path.Combine(Path.GetDirectoryName(project)!, "tiles", "satellite");

    /// <summary>The z8 tiles by path.</summary>
    static Dictionary<string, byte[]> Tiles(string project)
    {
        var root = TileRoot(project);
        return Directory.GetFiles(Path.Combine(root, "8"), "*.png", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes);
    }

    /// <summary>The same frames written as capture files directly, made into tiles by the ortho stage alone.</summary>
    static async Task<string> Reference(TempFolder tmp, double qx, double qy, (double, double)? scale)
    {
        var direct = NewProject(tmp, "direct");
        var folder = Folder(direct);
        Directory.CreateDirectory(folder.Capture);
        foreach (var b in Blocks) SyntheticCapture.Write(folder.Capture, b, qx, qy);
        StateStore.Open(folder).SetItems(Blocks.SelectMany(b => new[] { (b, BlockItem.Shot, DateTime.UtcNow), (b, BlockItem.Height, DateTime.UtcNow) }));
        Assert.Equal(JobState.Done, (await Runner(direct, null, 2, null, scale).RunAsync()).State);
        return direct;
    }

    static string[] UnitsCsv(JobSnapshot end) => File.ReadAllLines(Path.Combine(end.RunFolder, "units.csv")).Skip(1).ToArray();

    [Fact]
    public async Task BlocksAreOrthorectifiedBesideTheVisitAndKeptWhenTheMeasuredScaleAgrees()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();                                     // a camera without a scale error
        var project = NewProject(tmp, "visited");
        var log = new List<string>();
        var runner = Runner(project, game, 2, new ProvisionalOrtho(defaultScale: (1.0, 1.0)));
        runner.Logged += l => { lock (log) log.Add(l); };
        var end = await runner.RunAsync();
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal((4, 4), (end.Stages[0].Total, end.Stages[0].Done));

        // every block was orthorectified beside the visit (while it went on); the ortho step measured, agreed, kept them
        var beside = UnitsCsv(end).Where(l => l.StartsWith("ortho.provisional,", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, beside.Count);
        Assert.All(beside, l => Assert.Equal("done", l.Split(',')[4]));
        Assert.Equal(0, end.Stages[1].Total);
        Assert.Contains(log, l => l.Contains("4 of 4 blocks orthorectified beside the visit", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("which agrees with the correction the tiles were made with", StringComparison.Ordinal));
        var store = new ScaleStore(Folder(project));
        Assert.Equal((1.0, 1.0), (store.TilesScale()!.Qx, store.TilesScale()!.Qy));
        Assert.Equal((1.0, 1.0), (store.Get(2.0)!.Qx, store.Get(2.0)!.Qy));  // the project's correction from now on

        // the same z8 tiles as the ortho stage makes from the same frames with that correction
        var reference = await Reference(tmp, 1, 1, (1.0, 1.0));
        var a = Tiles(project);
        var b = Tiles(reference);
        Assert.Equal(64, a.Count);
        Assert.Equal(b.Keys.Order(), a.Keys.Order());
        Assert.All(a, kv => Assert.True(kv.Value.AsSpan().SequenceEqual(b[kv.Key]), kv.Key));
        // and the tiles above the blocks, so every zoom of the map shows them (z7: 2 x 2 per block, then one per level)
        var root = TileRoot(project);
        Assert.Equal(16, Directory.GetFiles(Path.Combine(root, "7"), "*.png", SearchOption.AllDirectories).Length);
        Assert.All(Enumerable.Range(0, 7), z => Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, z.ToString()), "*.png", SearchOption.AllDirectories)));
        Assert.True(File.Exists(Path.Combine(root, "0", "0", "0.png")));
        var plan = Planner.Build(Project.Load(project), StateStore.Open(Folder(project)), 2, 4);
        Assert.Equal(0, plan.Rows.Single(r => r.Id == "ortho").Remaining);
        Assert.Equal(1, plan.Rows.Single(r => r.Id == "lowZoom").Remaining);  // the lower zooms are made whole after the capture
    }

    [Fact]
    public async Task AMeasuredScaleThatDisagreesMakesEveryBlockAgain()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame(1 / 1.003, 1 / 1.012);                // the camera's scale is 0.3 % / 1.2 % off the provisional one
        var project = NewProject(tmp, "visited");
        var end = await Runner(project, game, 2, new ProvisionalOrtho(defaultScale: (1.0, 1.0))).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(4, UnitsCsv(end).Count(l => l.StartsWith("ortho.provisional,", StringComparison.Ordinal) && l.EndsWith(",done,2", StringComparison.Ordinal)));
        Assert.Equal((4, 4), (end.Stages[1].Total, end.Stages[1].Done));   // all made again with the measured correction
        var q = new ScaleStore(Folder(project)).TilesScale()!;
        Assert.Equal(1 / 1.003, q.Qx, 3);
        Assert.Equal(1 / 1.012, q.Qy, 3);

        var reference = await Reference(tmp, 1 / 1.003, 1 / 1.012, null);
        var a = Tiles(project);
        var b = Tiles(reference);
        Assert.Equal(b.Keys.Order(), a.Keys.Order());
        Assert.All(a, kv => Assert.True(kv.Value.AsSpan().SequenceEqual(b[kv.Key]), kv.Key));
    }

    [Fact]
    public async Task WithOneWorkerNothingRunsBesideTheVisit()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        var project = NewProject(tmp, "p");
        var end = await Runner(project, game, 1, new ProvisionalOrtho(defaultScale: (1.0, 1.0))).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal((4, 4), (end.Stages[1].Total, end.Stages[1].Done));   // the ortho step did them all
        Assert.DoesNotContain(UnitsCsv(end), l => l.StartsWith("ortho.provisional,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheGameStillTakesOneBlockAtATimeWhileAHelperWorksBesideIt()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { TileMs = 30 };
        var project = NewProject(tmp, "p");
        var end = await Runner(project, game, 4, new ProvisionalOrtho(defaultScale: (1.0, 1.0))).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        var visits = UnitsCsv(end).Where(l => l.StartsWith("visit,", StringComparison.Ordinal))
            .Select(l => l.Split(','))
            .Select(p => (Start: DateTime.Parse(p[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), Seconds: double.Parse(p[3], CultureInfo.InvariantCulture), Workers: int.Parse(p[5], CultureInfo.InvariantCulture)))
            .OrderBy(v => v.Start).ToList();
        Assert.Equal(4, visits.Count);
        for (int i = 1; i < visits.Count; i++)
            Assert.True(visits[i].Start >= visits[i - 1].Start.AddSeconds(visits[i - 1].Seconds), $"visit {i} started before visit {i - 1} ended");
        Assert.All(visits, v => Assert.Equal(2, v.Workers));                // the game's block and one helper, not the level of 4
    }

    [Fact]
    public async Task StopNowMidVisitLeavesTheBlocksNotOrthorectifiedToTheNextRun()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { TileMs = 60 };
        var project = NewProject(tmp, "p");
        var runner = Runner(project, game, 2, new ProvisionalOrtho(defaultScale: (1.0, 1.0)));
        runner.Changed += () => { if (runner.Snapshot().Stages[0].Done >= 2) runner.RequestStop(StopMode.Now); };
        var end = await runner.RunAsync();
        Assert.Equal(JobState.Stopped, end.State);
        Assert.False(game.Env);

        var state = StateStore.Open(Folder(project));
        var taken = Blocks.Count(b => state.Has(b, BlockItem.Shot));
        var made = Blocks.Count(b => state.StageDone(StageKeys.Ortho, b.Name) is not null);
        Assert.InRange(made, 0, taken);

        using var again = new FakeGame();
        var rest = await Runner(project, again, 2, new ProvisionalOrtho(defaultScale: (1.0, 1.0))).RunAsync();
        Assert.Equal(JobState.Done, rest.State);
        Assert.Equal(4 - taken, rest.Stages[0].Total);                        // the visit continues
        Assert.Equal(0, Planner.Build(Project.Load(project), StateStore.Open(Folder(project)), 2, 4).Rows.Single(r => r.Id == "ortho").Remaining);
    }

    [Fact]
    public void TwoCorrectionsAgreeWhenTheyMoveABlockEdgeByLessThanATenthOfAPixel()
    {
        Assert.Equal(1.953e-4, ScaleStore.AgreeLimit, 7);
        Assert.True(ScaleStore.Agrees((0.99667, 0.98868), (0.99670, 0.98875)));      // two measurements of the whole range: 1 cm apart
        Assert.False(ScaleStore.Agrees((0.99667, 0.98868), (0.99755, 0.98853)));     // 24 pairs: 12 cm
        Assert.False(ScaleStore.Agrees((1, 1), (1, 1.0002)));
        Assert.Equal((0.99667, 0.98868), ScaleStore.Default(2.0));
        Assert.Null(ScaleStore.Default(3.0));
    }

    [Fact]
    public async Task WithNothingToMeasureTheOrthoStepUsesTheDefault()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(Path.Combine(tmp.File("one"), "p.fxmapgen.json"));
        p.File.Range.Base = "none";
        p.File.Range.Add = [Blocks[0].Name];
        p.Save();
        var folder = Folder(p.FilePath);
        Directory.CreateDirectory(folder.Capture);
        SyntheticCapture.Write(folder.Capture, Blocks[0]);
        StateStore.Open(folder).SetItems([(Blocks[0], BlockItem.Shot, DateTime.UtcNow), (Blocks[0], BlockItem.Height, DateTime.UtcNow)]);
        Assert.Equal(JobState.Done, (await Runner(p.FilePath, null, 2, null).RunAsync()).State);
        var store = new ScaleStore(folder);
        Assert.Equal(ScaleStore.Default(2.0), (store.TilesScale()!.Qx, store.TilesScale()!.Qy));
        Assert.Null(store.Get(2.0));                                            // not measured: measured again next time
    }
}
