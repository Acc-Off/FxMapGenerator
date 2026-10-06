using System.Diagnostics;
using System.Text.Json;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;

namespace FxMapGenerator.Core.Tests.Jobs;

[Collection(nameof(Timing))]
public sealed class JobRunnerTests
{
    /// <summary>A project with an empty range (the plan is quick) in a scratch folder.</summary>
    sealed class Bench : IDisposable
    {
        readonly TempFolder _tmp = new();
        public string ProjectPath { get; }
        public FakeMemory Memory { get; set; } = new();

        public Bench()
        {
            var p = Project.Create(_tmp.File("t.fxmapgen.json"));
            p.File.Range.Base = "none";
            p.Save();
            ProjectPath = p.FilePath;
        }

        public string Folder => _tmp.Path;

        public JobRunner Runner(ParallelLevel level, int processors, params Stage[] stages) => JobRunner.Create(new JobSetup
        {
            ProjectPath = ProjectPath,
            Workers = level.Workers(processors),
            Processors = processors,
            Memory = Memory,
            Stages = (_, _) => new BuildPlan(stages, Array.Empty<string>()),
        });

        public StateStore State => StateStore.Open(new WorkFolder(_tmp.Path));

        public void Dispose() => _tmp.Dispose();
    }

    static void WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not reached");
            Thread.Sleep(2);
        }
    }

    [Fact]
    public async Task ARunReadsCopiesOfTheProjectsStylesItsMapsUse()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("t.fxmapgen.json"));
        p.File.Range.Base = "none";
        p.File.Maps.Atlas.Enabled = true;
        p.File.Maps.Atlas.Styles = ["dusk"];
        FxMapGenerator.Core.Styles.ProjectStyles.Write(p, new FxMapGenerator.Core.Styles.UserStyle("dusk", "Dusk", AtlasPresets.Regional, new System.Text.Json.Nodes.JsonObject { ["background"] = "#102030" }));
        FxMapGenerator.Core.Styles.ProjectStyles.Write(p, new FxMapGenerator.Core.Styles.UserStyle("spare", "Spare", AtlasPresets.Regional, new System.Text.Json.Nodes.JsonObject()));
        p.Save();
        Project? seen = null;
        var runner = JobRunner.Create(new JobSetup
        {
            ProjectPath = p.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(),
            Stages = (project, _) => { seen = project; return new BuildPlan(Array.Empty<Stage>(), Array.Empty<string>()); },
        });
        // the used style only, and the run reads it though the project's file changes
        Assert.Equal(new[] { "dusk.json" }, Directory.GetFiles(Path.Combine(runner.RunPath, "inputs", "styles")).Select(Path.GetFileName));
        FxMapGenerator.Core.Styles.ProjectStyles.Write(p, new FxMapGenerator.Core.Styles.UserStyle("dusk", "Dusk", AtlasPresets.Regional, new System.Text.Json.Nodes.JsonObject { ["background"] = "#405060" }));
        Assert.Equal("#102030", seen!.StyleOf(seen.Maps.Single(m => m.Kind == MapKind.Atlas)).Background.ToString());
        await runner.RunAsync();
    }

    [Fact]
    public async Task ARunReadsCopiesOfThePointsOfInterest()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("t.fxmapgen.json"));
        p.File.Range.Base = "none";
        Directory.CreateDirectory(tmp.File("poi/shops"));
        File.WriteAllText(tmp.File("poi/shops/_.json"), """{"style": "garage"}""");
        File.WriteAllText(tmp.File("poi/shops/garages.json"), """{"points": [{"id": "1", "label": {"en": "Garage"}, "x": 10, "y": 20}]}""");
        File.WriteAllText(tmp.File("poi/notes.txt"), "not copied");
        Directory.CreateDirectory(tmp.File("poi-icons"));
        FxMapGenerator.Core.Tests.Poi.PoiTests.Png(tmp.File("poi-icons/garage.png"), 8, 8);
        File.WriteAllText(tmp.File("poi-styles.json"), """{"styles": [{"id": "garage", "name": "Garage", "look": "icon", "image": "poi-icons/garage.png", "color": "#000000", "size": 20}]}""");
        p.File.Poi = "poi";
        p.File.PoiStyles = "poi-styles.json";
        p.Save();
        Project? seen = null;
        var runner = JobRunner.Create(new JobSetup
        {
            ProjectPath = p.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(),
            Stages = (project, _) => { seen = project; return new BuildPlan(Array.Empty<Stage>(), Array.Empty<string>()); },
        });
        var inputs = Path.Combine(runner.RunPath, "inputs");
        Assert.True(File.Exists(Path.Combine(inputs, "poi", "shops", "_.json")) && File.Exists(Path.Combine(inputs, "poi", "shops", "garages.json")));
        Assert.False(File.Exists(Path.Combine(inputs, "poi", "notes.txt")));
        Assert.True(File.Exists(Path.Combine(inputs, "poi-styles.json")) && File.Exists(Path.Combine(inputs, "poi-icons", "garage.png")));
        var before = FxMapGenerator.Core.Poi.PoiData.Sha256(seen!);
        // the project's files change while the run goes on: the run keeps reading its copies
        File.WriteAllText(tmp.File("poi/shops/garages.json"), """{"points": [{"id": "1", "label": {"en": "Garage"}, "x": 99, "y": 20}]}""");
        FxMapGenerator.Core.Tests.Poi.PoiTests.Png(tmp.File("poi-icons/garage.png"), 8, 16);
        Assert.Equal(before, FxMapGenerator.Core.Poi.PoiData.Sha256(seen!));
        Assert.Equal(10, FxMapGenerator.Core.Poi.PoiData.Of(seen!).Points.Single().X);
        Assert.NotEqual(before, FxMapGenerator.Core.Poi.PoiData.Sha256(Project.Load(p.FilePath)));
        await runner.RunAsync();
    }

    [Fact]
    public async Task UnitsRunWithinTheLimitAndAreRecorded()
    {
        using var b = new Bench();
        // the first units wait until three are in progress: the units are thread pool tasks, and a pool that is slow to
        // hand out threads would let 40 units of 20 ms pass before three ever ran side by side
        using var three = new ManualResetEventSlim();
        FakeStage stage = null!;
        stage = new FakeStage("a", 40, body: _ =>
        {
            if (stage.Concurrent >= 3) three.Set();
            WaitForThree(three);
            Thread.Sleep(20);
        });
        var runner = b.Runner(ParallelLevel.Light, 10, stage);                    // 3 workers
        var end = await runner.RunAsync();

        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(3, stage.Peak);
        Assert.Equal(new StageEnd(40, 40, 0, false), stage.Ended);
        Assert.Equal(1, stage.CleanupCalls);
        var state = b.State;
        Assert.All(Enumerable.Range(0, 40), i => Assert.NotNull(state.StageDone("fake-a", FakeStage.Name(i))));

        Assert.True(File.Exists(Path.Combine(runner.RunPath, "inputs", "t.fxmapgen.json")));
        var lines = File.ReadAllLines(Path.Combine(runner.RunPath, "units.csv"));
        Assert.Equal(41, lines.Length);
        // the records never show more units at once than the limit (each covers the time the unit held its worker)
        var events = lines.Skip(1).Select(l => l.Split(','))
            .SelectMany(f =>
            {
                var start = DateTime.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                var end = start.AddSeconds(double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture) - 0.00001);
                return new[] { (At: start, Step: 1), (At: end, Step: -1) };
            })
            .OrderBy(e => e.At).ThenBy(e => e.Step).ToList();
        int now = 0, most = 0;
        foreach (var e in events) most = Math.Max(most, now += e.Step);
        Assert.Equal(3, most);
        Assert.Contains("a: done, 40 of 40 done", File.ReadAllText(Path.Combine(runner.RunPath, "run.log")));
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(runner.RunPath, "run.json"))))
            Assert.Equal("done", doc.RootElement.GetProperty("snapshot").GetProperty("state").GetString());
        Assert.Null(WorkFolderLock.Holder(new WorkFolder(b.Folder)));             // the work folder is free again

        var again = new FakeStage("a", 40);
        Assert.Equal(JobState.Done, (await b.Runner(ParallelLevel.Light, 10, again).RunAsync()).State);
        Assert.Empty(again.Ran);
    }

    [Fact]
    public async Task TheLevelCanBeRaisedAndLoweredWhileRunning()
    {
        using var b = new Bench();
        using var hold = new ManualResetEventSlim(true);
        var stage = new FakeStage("a", 2000, body: _ => { hold.Wait(); Thread.Sleep(15); });
        var runner = b.Runner(ParallelLevel.Light, 8, stage);                     // 2 workers
        var task = runner.RunAsync();
        // the peak is read once two units have run side by side: the units are thread pool tasks, and a pool that is slow
        // to hand out threads may let the first ten run one after another
        WaitUntil(() => stage.RanCount >= 10 && stage.Peak >= 2);
        Assert.Equal(2, stage.Peak);

        runner.SetWorkers(8);                                                     // more units start at once
        WaitUntil(() => stage.Concurrent == 8);
        Assert.Equal(8, runner.Snapshot().Workers);

        // hold the running units while lowering, so that more than 2 are surely running at that moment
        hold.Reset();
        Thread.Sleep(100);
        runner.SetWorkers(2);                                                     // running units finish, no new ones until below
        var lowered = runner.Snapshot();
        Assert.Equal(2, lowered.Workers);
        Assert.True(lowered.Retiring > 0, $"retiring {lowered.Retiring}");
        hold.Set();
        Thread.Sleep(100);
        int from = stage.RanCount;
        WaitUntil(() => stage.RanCount >= from + 20);
        var after = stage.AtStartFrom(from);
        Assert.All(after, n => Assert.True(n <= 2, $"{n} at once"));

        runner.RequestStop(StopMode.Boundary);
        var end = await task;
        Assert.Equal(JobState.Stopped, end.State);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(runner.RunPath, "run.json")));
        Assert.Equal(new[] { 2, 8, 2 }, doc.RootElement.GetProperty("workerChanges").EnumerateArray().Select(e => e.GetProperty("workers").GetInt32()));
    }

    [Fact]
    public async Task StopAtABoundaryLetsRunningUnitsFinishAndTheNextRunContinues()
    {
        using var b = new Bench();
        using var release = new ManualResetEventSlim();
        var first = new FakeStage("a", 20, body: _ => release.Wait());
        var second = new FakeStage("b", 5, row: "ytd");
        var runner = b.Runner(ParallelLevel.Light, 10, first, second);            // 3 workers
        var task = runner.RunAsync();
        WaitUntil(() => first.Concurrent == 3);
        runner.RequestStop(StopMode.Boundary);
        Assert.Equal(JobState.Stopping, runner.Snapshot().State);
        release.Set();
        var end = await task;

        Assert.Equal(JobState.Stopped, end.State);
        var a = end.Stages[0];
        Assert.Equal((StageState.Stopped, 20, 3, 0), (a.State, a.Total, a.Done, a.Interrupted));
        Assert.Equal(StageState.Waiting, end.Stages[1].State);
        Assert.Equal(new StageEnd(20, 3, 0, true), first.Ended);
        Assert.Empty(second.Ran);

        var again = new FakeStage("a", 20);
        var second2 = new FakeStage("b", 5, row: "ytd");
        Assert.Equal(JobState.Done, (await b.Runner(ParallelLevel.Light, 10, again, second2).RunAsync()).State);
        Assert.Equal(17, again.Ran.Count);
        Assert.Empty(again.Ran.Intersect(first.Ran));
        Assert.Equal(5, second2.Ran.Count);
    }

    [Fact]
    public async Task StopNowCancelsRunningUnitsRecordsNothingOfThemAndCleansUp()
    {
        using var b = new Bench();
        var first = new FakeStage("a", 20, body: ctx =>
        {
            ctx.Token.WaitHandle.WaitOne();
            ctx.Token.ThrowIfCancellationRequested();
        });
        var runner = b.Runner(ParallelLevel.Light, 10, first, new FakeStage("b", 5, row: "ytd"));
        var task = runner.RunAsync();
        WaitUntil(() => first.Concurrent == 3);
        runner.RequestStop(StopMode.Now);
        var end = await task;

        Assert.Equal(JobState.Stopped, end.State);
        Assert.Equal((StageState.Stopped, 0, 3), (end.Stages[0].State, end.Stages[0].Done, end.Stages[0].Interrupted));
        Assert.Null(first.Ended);                 // no Finish after "stop now"
        Assert.Equal(1, first.CleanupCalls);      // but the clean-up runs
        Assert.Null(b.State.StageDone("fake-a", FakeStage.Name(0)));
        Assert.Equal(3, File.ReadAllLines(Path.Combine(runner.RunPath, "units.csv")).Count(l => l.EndsWith(",interrupted,3")));

        var again = new FakeStage("a", 20);
        Assert.Equal(JobState.Done, (await b.Runner(ParallelLevel.Light, 10, again).RunAsync()).State);
        Assert.Equal(20, again.Ran.Count);
    }

    [Fact]
    public async Task FailedUnitsAreListedAndLeftForTheNextRun()
    {
        using var b = new Bench();
        var stage = new FakeStage("a", 10, body: ctx => { if (ctx.Unit == "u05") throw new IOException("disk full"); });
        var next = new FakeStage("b", 2, row: "ytd");
        var end = await b.Runner(ParallelLevel.Full, 4, stage, next).RunAsync();

        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(1, end.FailureCount);
        Assert.Equal(("a", "u05", "disk full"), (end.Failures[0].Stage, end.Failures[0].Unit, end.Failures[0].Message));
        Assert.Equal(new StageEnd(10, 9, 1, false), stage.Ended);
        Assert.Equal(2, next.Ran.Count);          // the next stage still runs

        var again = new FakeStage("a", 10);
        await b.Runner(ParallelLevel.Full, 4, again).RunAsync();
        Assert.Equal(new[] { "u05" }, again.Ran);
    }

    [Fact]
    public async Task ShortMemoryHoldsBackWorkers()
    {
        using var b = new Bench();
        const long unit = 100L << 20;
        b.Memory = new FakeMemory(WorkerGate.Reserve + unit * 5 / 2);            // room for 2.5 units
        var stage = new FakeStage("a", 30, body: _ => Thread.Sleep(30)) { Memory = unit };
        var runner = b.Runner(ParallelLevel.Full, 8, stage);
        var task = runner.RunAsync();
        WaitUntil(() => runner.Snapshot().MemoryLimitedAt == 2);
        var end = await task;
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(2, stage.Peak);
        Assert.Equal(8, end.Workers);
    }

    [Fact]
    public async Task LocksFollowTheStagesThatAreNotOver()
    {
        using var b = new Bench();
        using var releaseA = new ManualResetEventSlim();
        using var releaseB = new ManualResetEventSlim();
        var a = new FakeStage("a", 2, row: "ortho", body: _ => releaseA.Wait());
        var y = new FakeStage("y", 1, row: "ytd", body: _ => releaseB.Wait());
        var runner = b.Runner(ParallelLevel.Full, 4, a, y);
        var task = runner.RunAsync();

        WaitUntil(() => a.Concurrent == 2);
        Assert.Equal(new[] { "minimap", "range" }, runner.Snapshot().Locks.Select(l => l.Key));
        Assert.Equal(new[] { "ortho" }, runner.LockedBy(InputKeys.Range));
        Assert.Null(runner.LockedBy(InputKeys.Poi));

        releaseA.Set();
        WaitUntil(() => y.Concurrent == 1);
        Assert.Null(runner.LockedBy(InputKeys.Range));
        Assert.Equal(new[] { "ytd" }, runner.LockedBy(InputKeys.Minimap));

        releaseB.Set();
        var end = await task;
        Assert.Empty(end.Locks);
    }

    [Fact]
    public async Task TheRoadEditsAreTakenWhenTheFirstStageReadingThemStarts()
    {
        using var b = new Bench();
        var project = Project.Load(b.ProjectPath);
        var edits = project.ResolvePath("road-edits.json");
        var empty = FxMapGenerator.Core.RoadEdits.RoadEditsFile.Format(FxMapGenerator.Core.RoadEdits.RoadEditSet.Empty);
        File.WriteAllText(edits, empty);
        project.File.RoadEdits = "road-edits.json";
        project.Save();
        using var releaseA = new ManualResetEventSlim();
        using var releaseG = new ManualResetEventSlim();
        using var releaseL = new ManualResetEventSlim();
        using var releaseR = new ManualResetEventSlim();
        string? graphRead = null, roadsRead = null;
        var a = new FakeStage("a", 1, row: "ortho", body: _ => releaseA.Wait());
        var g = new FakeStage("g", 1, row: "mapData.roadGraph", body: ctx => { graphRead = File.ReadAllText(ctx.Project.RoadEditsPath!); releaseG.Wait(); });
        var l = new FakeStage("l", 1, row: "mapData.landcover", body: _ => releaseL.Wait());
        var r = new FakeStage("r", 1, row: "mapData.roads", body: ctx => { roadsRead = File.ReadAllText(ctx.Project.RoadEditsPath!); releaseR.Wait(); });
        var runner = b.Runner(ParallelLevel.Full, 4, a, g, l, r);
        var task = runner.RunAsync();

        // while a stage before them runs (the capture), the edits are not held: what is saved now is what the run builds with
        WaitUntil(() => a.Concurrent == 1);
        Assert.Null(runner.LockedBy(InputKeys.RoadEdits));
        Assert.DoesNotContain(InputKeys.RoadEdits, runner.Snapshot().Locks.Select(k => k.Key));
        File.WriteAllText(edits, empty + "\n");

        // from the start of the first stage reading them to the end of the last, they are held
        releaseA.Set();
        WaitUntil(() => g.Concurrent == 1);
        Assert.Equal(new[] { "mapData.roadGraph", "mapData.roads" }, runner.LockedBy(InputKeys.RoadEdits));
        Assert.Contains(InputKeys.RoadEdits, runner.Snapshot().Locks.Select(k => k.Key));
        File.WriteAllText(edits, empty + "\n\n");                                    // behind the run's back: not read
        releaseG.Set();
        WaitUntil(() => l.Concurrent == 1);
        Assert.Equal(new[] { "mapData.roads" }, runner.LockedBy(InputKeys.RoadEdits));
        releaseL.Set();
        WaitUntil(() => r.Concurrent == 1);
        Assert.Equal(new[] { "mapData.roads" }, runner.LockedBy(InputKeys.RoadEdits));
        releaseR.Set();
        var end = await task;
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(empty + "\n", graphRead);
        Assert.Equal(empty + "\n", roadsRead);
    }

    [Fact]
    public void InputKeysNestAndStagesReadWhatTheSpecSays()
    {
        Assert.True(InputKeys.Overlap("style", "style.regions"));
        Assert.True(InputKeys.Overlap("style.regions", "style"));
        Assert.False(InputKeys.Overlap("style.regions", "style.labels"));
        Assert.False(InputKeys.Overlap("style", "styles"));
        Assert.Equal(new[] { "range" }, StageInputs.For("ortho"));
        Assert.Equal(new[] { "range", "frame" }, StageInputs.For("lowZoom.satellite"));
        Assert.Equal(new[] { "style", "poi", "frame" }, StageInputs.For("lowZoom.atlas-postalcodemap-en"));
        Assert.Equal(StageInputs.For("cells"), StageInputs.For("cells.atlas-postalcodemap-ja"));
        Assert.Equal(new[] { "range", "maps", "server", "console" }, StageInputs.For("visit.shot"));
        Assert.Empty(StageInputs.For("nothing"));
        // the frame: read by the steps working over it (the game files' areas, the region and road rasters, the low
        // zooms), not by those working on the range's blocks (the visit, the ortho, the road graph, the landcover)
        var frame = new[] { "gameFiles", "mapData.regions", "mapData.roads", "lowZoom.satellite", "lowZoom.roadmap" };
        Assert.All(frame, row => Assert.Contains(InputKeys.Frame, StageInputs.For(row)));
        Assert.All(new[] { "visit", "ortho", "mapData.roadGraph", "mapData.landcover", "mapData.labels", "cells", "ytd" },
            row => Assert.DoesNotContain(InputKeys.Frame, StageInputs.For(row)));
        Assert.False(InputKeys.Overlap(InputKeys.Range, InputKeys.Frame));
        // the Cayo Perico choice: the game files take the island's road files with it, the labels the island's zone name;
        // the server's resource folders are the game files' alone
        Assert.All(new[] { "gameFiles", "mapData.labels" }, row => Assert.Contains(InputKeys.CayoPerico, StageInputs.For(row)));
        Assert.All(new[] { "visit", "ortho", "mapData.roadGraph", "mapData.landcover", "mapData.regions", "mapData.roads", "cells", "lowZoom", "ytd" },
            row => Assert.DoesNotContain(InputKeys.CayoPerico, StageInputs.For(row)));
        Assert.Contains(InputKeys.GameFiles, StageInputs.For("gameFiles"));
        Assert.DoesNotContain(InputKeys.GameFiles, StageInputs.For("mapData.labels"));
        Assert.False(InputKeys.Overlap(InputKeys.GameFiles, InputKeys.CayoPerico));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ALoopInsideAUnitUsesTheFreeWorkersWithoutDeadlock(int processors)
    {
        using var b = new Bench();
        using var three = new ManualResetEventSlim(processors == 1);              // one worker has nothing to wait for
        int concurrent = 0, peak = 0;
        var sync = new object();
        var stage = new FakeStage("w", 1, body: ctx => ctx.Parallel.ForEach(Enumerable.Range(0, 120).ToList(), _ =>
        {
            lock (sync)
            {
                peak = Math.Max(peak, ++concurrent);
                if (concurrent >= 3) three.Set();
            }
            WaitForThree(three);
            Thread.Sleep(5);
            lock (sync) concurrent--;
        }));
        var end = await b.Runner(ParallelLevel.Full, processors, stage).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        if (processors == 1) Assert.Equal(1, peak);
        else Assert.InRange(peak, 3, 4);
    }

    /// <summary>
    /// Holds a loop's first items until three of them are in progress at once. The loop's helpers are thread pool tasks,
    /// and on a machine with few processors the pool gives them a thread late: run alone on 4 processors, 40 items of
    /// 5 ms had all started before the first helper did, so a loop that only sleeps may end, or have its level lowered,
    /// before it ever ran side by side. If no third worker comes in 30 s the wait is given up for every later item too,
    /// and the test's bound on the peak fails.
    /// </summary>
    static void WaitForThree(ManualResetEventSlim three)
    {
        if (!three.Wait(TimeSpan.FromSeconds(30))) three.Set();
    }

    [Fact]
    public async Task LoweringTheLevelShrinksALoopFromItsNextItem()
    {
        using var b = new Bench();
        using var three = new ManualResetEventSlim();
        int concurrent = 0, peak = 0;
        var sync = new object();
        var seen = new List<int>();                                               // the items in progress as each item starts
        var stage = new FakeStage("w", 1, body: ctx => ctx.Parallel.ForEach(Enumerable.Range(0, 400).ToList(), _ =>
        {
            lock (sync)
            {
                peak = Math.Max(peak, ++concurrent);
                seen.Add(concurrent);
                if (concurrent >= 3) three.Set();
            }
            WaitForThree(three);                                                  // the level comes down only after the loop ran side by side
            Thread.Sleep(5);
            lock (sync) concurrent--;
        }));
        var runner = b.Runner(ParallelLevel.Full, 4, stage);
        var task = runner.RunAsync();
        WaitUntil(() => { lock (sync) return seen.Count >= 40; }, 60000);
        runner.SetWorkers(1);                                                     // 4 -> 1
        var end = await task;
        Assert.Equal(JobState.Done, end.State);
        Assert.InRange(peak, 3, 4);
        // The items in progress when the level came down end, and no worker beyond the level takes another one: the rest
        // of the loop runs one item at a time. Counted in items, not in time, so that a slow or busy machine does not
        // matter: the last 100 start long after the three items that were in progress have ended.
        Assert.Equal(400, seen.Count);
        Assert.All(seen.Skip(300), n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task TheRunReadsItsOwnCopyOfTheProject()
    {
        using var b = new Bench();
        var runner = b.Runner(ParallelLevel.Full, 2, new FakeStage("a", 1));
        var p = Project.Load(b.ProjectPath);
        p.File.Name = "renamed while running";
        p.Save();
        var end = await runner.RunAsync();
        Assert.Equal("t", end.ProjectName);
        Assert.Contains("\"name\": \"t\"", File.ReadAllText(Path.Combine(runner.RunPath, "inputs", "t.fxmapgen.json")));
    }

    [Fact]
    public async Task ASecondRunOnTheSameWorkFolderIsRefused()
    {
        using var b = new Bench();
        var first = b.Runner(ParallelLevel.Full, 2, new FakeStage("a", 1));
        var ex = Assert.Throws<JobException>(() => b.Runner(ParallelLevel.Full, 2, new FakeStage("a", 1)));
        Assert.Equal("BUSY", ex.Code);
        Assert.Contains($"pid {Environment.ProcessId}", ex.Message);
        Assert.Single(Directory.GetDirectories(Path.Combine(b.Folder, "logs")));   // the refused one left no run folder
        await first.RunAsync();
        Assert.Equal(JobState.Done, (await b.Runner(ParallelLevel.Full, 2, new FakeStage("a", 1)).RunAsync()).State);
    }

    [Fact]
    public async Task TheGateFollowsItsLimit()
    {
        var gate = new WorkerGate(2, new FakeMemory());
        var s1 = await gate.AcquireAsync(default);
        var s2 = await gate.AcquireAsync(default);
        var third = gate.AcquireAsync(default);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);
        gate.SetLimit(3);
        var s3 = await third.WaitAsync(TimeSpan.FromSeconds(5));
        gate.SetLimit(1);
        Assert.Equal(2, gate.Retiring);
        Assert.True(s1.RetireIfOverLimit());
        Assert.True(s2.RetireIfOverLimit());
        Assert.False(s3.RetireIfOverLimit());
        s1.Dispose();                            // already given back: no double release
        Assert.Equal(1, gate.Running);
        s3.Dispose();
        Assert.Equal(0, gate.Running);
    }
}
