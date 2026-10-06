using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Satellite;

public sealed class SatelliteJobTests
{
    static readonly DateTime T0 = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static readonly BlockId[] Blocks = new[] { "z8_60_132", "z8_64_132", "z8_60_136", "z8_64_136" }.Select(BlockId.Parse).ToArray();

    /// <summary>Four synthetic captures (with a scale error to measure), copied into a project per case.</summary>
    sealed class Captures : IDisposable
    {
        readonly TempFolder _tmp = new();
        readonly string _source;

        public Captures()
        {
            _source = _tmp.File("source");
            Directory.CreateDirectory(_source);
            foreach (var b in Blocks) SyntheticCapture.Write(_source, b, 1 / 1.003, 1 / 1.012);
        }

        /// <summary>A project whose range is the four blocks, with their shots and height grids in its work folder.</summary>
        public string NewProject(string name)
        {
            var dir = _tmp.File(name);
            var p = Project.Create(Path.Combine(dir, "p.fxmapgen.json"));
            p.File.Range.Base = "none";
            p.File.Range.Add = Blocks.Select(b => b.Name).ToList();
            p.Save();
            var folder = new WorkFolder(dir);
            Directory.CreateDirectory(folder.Capture);
            foreach (var f in Directory.GetFiles(_source)) File.Copy(f, Path.Combine(folder.Capture, Path.GetFileName(f)));
            StateStore.Open(folder).SetItems(Blocks.SelectMany(b => new[] { (b, BlockItem.Shot, T0), (b, BlockItem.Height, T0) }));
            return p.FilePath;
        }

        public void Dispose() => _tmp.Dispose();
    }

    static JobRunner Runner(string project, (double, double)? scale = null, bool stopAfterFirst = false)
    {
        var runner = JobRunner.Create(new JobSetup
        {
            ProjectPath = project,
            Workers = 2,
            Processors = 2,
            Memory = new FakeMemory(),
            Stages = (_, _) => new BuildPlan(new Stage[] { new OrthoStage(new OrthoStage.Options(scale)) }, Array.Empty<string>()),
        });
        if (stopAfterFirst)
            runner.Changed += () => { if (runner.Snapshot().Stages[0].Done >= 1) runner.RequestStop(StopMode.Boundary); };
        return runner;
    }

    static Dictionary<string, byte[]> Tiles(string project)
    {
        var root = Path.Combine(Path.GetDirectoryName(project)!, "tiles", "satellite");
        return Directory.GetFiles(root, "*.png", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes);
    }

    [Fact]
    public async Task AStoppedRunContinuesWithTheBlocksLeftAndEndsWithTheSameTiles()
    {
        using var caps = new Captures();
        var straight = caps.NewProject("straight");
        Assert.Equal(JobState.Done, (await Runner(straight).RunAsync()).State);

        var resumed = caps.NewProject("resumed");
        var first = await Runner(resumed, stopAfterFirst: true).RunAsync();
        Assert.Equal(JobState.Stopped, first.State);
        int done = first.Stages[0].Done;
        Assert.InRange(done, 1, 2);                                               // 2 workers: at most both finish
        var rest = await Runner(resumed).RunAsync();
        Assert.Equal(JobState.Done, rest.State);
        Assert.Equal(4 - done, rest.Stages[0].Total);                             // only the blocks left

        var a = Tiles(straight);
        var b = Tiles(resumed);
        Assert.Equal(64, a.Count);
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        Assert.All(a, kv => Assert.Equal(kv.Value, b[kv.Key]));
    }

    [Fact]
    public async Task ANewScaleRedoesEveryBlockOnceEvenWhenStoppedHalfWay()
    {
        using var caps = new Captures();
        var project = caps.NewProject("p");
        Assert.Equal(JobState.Done, (await Runner(project, (0.997, 0.988)).RunAsync()).State);

        var first = await Runner(project, (0.99, 0.98), stopAfterFirst: true).RunAsync();
        Assert.Equal(4, first.Stages[0].Total);                                   // another correction: all blocks
        var rest = await Runner(project, (0.99, 0.98)).RunAsync();
        Assert.Equal(4 - first.Stages[0].Done, rest.Stages[0].Total);             // continued, not started over
        Assert.Equal(0, (await Runner(project, (0.99, 0.98)).RunAsync()).Stages[0].Total);
    }

    [Fact]
    public async Task ABlockThatCannotBeReadFailsAloneAndTheOthersAreDone()
    {
        using var caps = new Captures();
        var project = caps.NewProject("p");
        var hmap = Path.Combine(Path.GetDirectoryName(project)!, "capture", Blocks[1].Name + ".hmap");
        var good = File.ReadAllBytes(hmap);
        File.WriteAllBytes(hmap, good[..200]);                                   // cut short: "incomplete height grid"

        var end = await Runner(project).RunAsync();                              // the scale is measured from the other three
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal((4, 3, 1), (end.Stages[0].Total, end.Stages[0].Done, end.Stages[0].Failed));
        Assert.Equal(Blocks[1].Name, end.Failures.Single().Unit);
        Assert.Contains("incomplete height grid", end.Failures.Single().Message);

        File.WriteAllBytes(hmap, good);
        var again = await Runner(project).RunAsync();
        Assert.Equal((1, 1), (again.Stages[0].Total, again.Stages[0].Done));    // only the failed block
    }

    [Fact]
    public void LowZoomsAreLeftWhenAnOrthoRecordIsNewer()
    {
        using var caps = new Captures();
        var project = Project.Load(caps.NewProject("p"));
        var state = StateStore.Open(new WorkFolder(project.WorkFolderPath));
        Assert.True(SatelliteLowZoomStage.IsStale(state, Blocks));                // never made
        state.SetStageDone(StageKeys.Ortho, Blocks.Select(b => b.Name), T0.AddHours(1));
        state.SetStageDone(StageKeys.LowZoom, "satellite", T0.AddHours(1));
        Assert.False(SatelliteLowZoomStage.IsStale(state, Blocks));
        var plan = Planner.Build(project, state, 4, 8);
        Assert.Equal(0, plan.Rows.Single(r => r.Id == "lowZoom").Remaining);

        // the ortho finished but the run stopped before the lower zooms
        state.SetStageDone(StageKeys.Ortho, Blocks[0].Name, T0.AddHours(2));
        Assert.True(SatelliteLowZoomStage.IsStale(state, Blocks));
        plan = Planner.Build(project, state, 4, 8);
        Assert.Equal(0, plan.Rows.Single(r => r.Id == "ortho").Remaining);
        Assert.Equal(1, plan.Rows.Single(r => r.Id == "lowZoom").Remaining);
    }
}
