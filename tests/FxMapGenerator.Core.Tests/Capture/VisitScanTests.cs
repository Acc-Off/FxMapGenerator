using System.Globalization;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.Tests.Satellite;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Capture;

/// <summary>The visit with the scans (the atlas and the road map), and the height quality: which heights are taken and read.</summary>
public sealed class VisitScanTests
{
    static readonly BlockId[] Blocks = new[] { "z8_60_132", "z8_64_132" }.Select(BlockId.Parse).ToArray();

    static readonly VisitStage.Options Fast = new()
    {
        ResourceVersion = "0.1.0",
        ConnectTimeout = TimeSpan.FromSeconds(1),
        TileTimeout = TimeSpan.FromMilliseconds(400),
        HmapTimeout = TimeSpan.FromSeconds(3),
        ScanTimeout = TimeSpan.FromSeconds(5),
        SettleBeforeShot = TimeSpan.Zero,
        ShotRetryWait = TimeSpan.FromMilliseconds(1),
        NotificationWait = TimeSpan.FromMilliseconds(20),
        RetryWait = TimeSpan.FromMilliseconds(1),
        StopAtEnd = false,                                                 // the tests of stopping the resource set it
    };

    static Project AtlasProject(TempFolder tmp, string quality = SurfaceHeights.Balance, bool satellite = false)
    {
        var p = Project.Create(tmp.File("p.fxmapgen.json"));
        p.File.Maps = new MapsSetting { Satellite = satellite, Atlas = new AtlasSetting { Enabled = true } };
        p.File.Range = new RangeSetting { Base = "none", Add = Blocks.Select(b => b.Name).ToList() };
        p.File.HeightQuality = quality;
        p.Save();
        return p;
    }

    static Task<JobSnapshot> Visit(Project p, FakeGame game) => JobRunner.Create(new JobSetup
    {
        ProjectPath = p.FilePath, Workers = 2, Processors = 2, Memory = new FakeMemory(),
        Stages = (_, _) => new BuildPlan([new VisitStage(game, Fast)], []),
    }).RunAsync();

    [Fact]
    public async Task AQualityVisitTakesTheShotTheHeightDataAndBothScans()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        var p = AtlasProject(tmp, SurfaceHeights.Quality, satellite: true);
        Assert.Equal(new[] { BlockItem.Shot, BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads }, VisitStage.ItemsFor(p));
        var end = await Visit(p, game);
        Assert.Equal(JobState.Done, end.State);
        var sent = game.Received;
        Assert.Equal(new[] { "fxmapgen tile 8 64 132 2 1.06 1500", "fxmapgen hmap 1", "fxmapgen scan ground 8 64 132", "fxmapgen scan roads 8 64 132",
            "fxmapgen tile 8 60 132 2 1.06 1500", "fxmapgen hmap 1", "fxmapgen scan ground 8 60 132", "fxmapgen scan roads 8 60 132" },
            sent.SkipWhile(c => c != "fxmapgen env on").Skip(1).TakeWhile(c => c != "fxmapgen env off"));
        var folder = new WorkFolder(p.WorkFolderPath);
        var state = StateStore.Open(folder);
        foreach (var b in Blocks)
        {
            Assert.Equal((true, true, true, true, false), (state.Has(b, BlockItem.Height), state.Has(b, BlockItem.Shot), state.Has(b, BlockItem.ScanGround),
                state.Has(b, BlockItem.ScanRoads), state.Has(b, BlockItem.ScanCanopy)));
            var scan = ScanFile.Read(folder.ScanFile(b));
            Assert.True(scan.HasGround && scan.HasRoads, b.Name);
            Assert.Equal((282, 71, 282), (scan.N, scan.RoadN, scan.OnRoadN));
            Assert.Equal((b.Tx, b.Ty), (scan.Tx, scan.Ty));
            Assert.All(scan.Material, m => Assert.Equal(282940568u, m));             // tarmac
            Assert.Equal("Fake St", scan.StreetNames.Values.Single());
            Assert.StartsWith("MSCAN BEGIN v=1 kind=mat seq=", File.ReadLines(folder.ScanFile(b)).First());
        }
        // the transcript keeps the scans' BEGIN / END / DONE, not their rows
        var log = File.ReadAllText(Path.Combine(end.RunFolder, "console.log"));
        Assert.Contains("MSCAN BEGIN v=1 kind=road", log);
        Assert.Contains("MSCAN DONE", log);
        Assert.DoesNotContain("MSCAN hz j=", log);
    }

    [Fact]
    public async Task AnAtlasWithoutTheSatelliteMapNeedsNoCamera()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { Size = null };                          // no window: no shots, no height data
        var p = AtlasProject(tmp);
        Assert.Equal(new[] { BlockItem.ScanGround, BlockItem.ScanRoads }, VisitStage.ItemsFor(p));
        var end = await Visit(p, game);
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(new[] { "fxmapgen scan ground 8 64 132", "fxmapgen scan roads 8 64 132", "fxmapgen scan ground 8 60 132", "fxmapgen scan roads 8 60 132" },
            game.Received.SkipWhile(c => c != "fxmapgen env on").Skip(1).TakeWhile(c => c != "fxmapgen env off"));
        var state = StateStore.Open(new WorkFolder(p.WorkFolderPath));
        Assert.All(Blocks, b => Assert.False(state.Has(b, BlockItem.Height)));
        // the heights of the block: the scan's hits (40 m), as a height grid
        var h = SurfaceHeights.Read(SurfaceHeights.PathOf(new WorkFolder(p.WorkFolderPath), Blocks[0], BlockItem.ScanGround));
        Assert.Equal(282, h.N);
        Assert.All(h.Values, v => Assert.Equal(40.0, v));
    }

    [Fact]
    public async Task AScanCutOffIsTakenAgainAndTakingOneScanAgainKeepsTheOther()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { Size = null };
        game.ScanAborts["z8_60_132"] = 1;
        var p = AtlasProject(tmp);
        var end = await Visit(p, game);
        Assert.Equal(JobState.Done, end.State);
        var log = File.ReadAllText(Path.Combine(end.RunFolder, "run.log"));
        Assert.Contains("visit z8_60_132: try 2 (the ground scan was cut off (MSCAN ABORT))", log);
        Assert.Contains("visit z8_60_132: done on try 2", log);

        // the road scan of one block is missing again: only it is taken, the ground scan stays in the file
        var folder = new WorkFolder(p.WorkFolderPath);
        var state = StateStore.Open(folder);
        var b = Blocks[0];
        var groundBefore = File.ReadLines(folder.ScanFile(b)).Where(l => ScanLines.KindOf(l) == ScanKind.Ground).ToList();
        state.TakeItems(b, [], DateTime.UtcNow, dropped: [BlockItem.ScanRoads]);
        var mark = game.Received.Count;
        Assert.Equal(JobState.Done, (await Visit(p, game)).State);
        Assert.Equal(new[] { "fxmapgen scan roads 8 60 132" }, game.Received.Skip(mark).Where(c => c.StartsWith("fxmapgen scan", StringComparison.Ordinal)));
        var lines = File.ReadAllLines(folder.ScanFile(b));
        Assert.Equal(groundBefore, lines.Where(l => ScanLines.KindOf(l) == ScanKind.Ground));
        Assert.True(ScanFile.Read(folder.ScanFile(b)).HasRoads);
        Assert.True(StateStore.Open(folder).Has(b, BlockItem.ScanRoads));
    }

    [Fact]
    public async Task AScanLineTheGameCutIsTakenAgain()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame { Size = null };
        game.ScanCuts["z8_60_132"] = 1;
        var p = AtlasProject(tmp);
        var end = await Visit(p, game);
        Assert.Equal(JobState.Done, end.State);
        var log = File.ReadAllText(Path.Combine(end.RunFolder, "run.log"));
        Assert.Contains("visit z8_60_132: try 2 (the ground scan came incomplete: a line could not be read (", log);
        Assert.Contains("visit z8_60_132: done on try 2", log);
        var scan = ScanFile.Read(new WorkFolder(p.WorkFolderPath).ScanFile(BlockId.Parse("z8_60_132")));
        Assert.All(scan.HitZ, z => Assert.Equal(40f, z));
    }

    [Fact]
    public void AnAtlasBuildVisitsThenMakesTheMapDataInOrder()
    {
        using var tmp = new TempFolder();
        using var game = new FakeGame();
        var p = AtlasProject(tmp);
        var state = StateStore.Open(new WorkFolder(p.WorkFolderPath));
        var build = BuildStages.For(p, state, new BuildStages.Options(Game: game));
        Assert.Equal(new[] { "visit", "gameFiles", "roadGraph", "landcover", "regions", "labels", "roads", "cellPrep", "cells.atlas-postalcodemap-en", "lowZoom.atlas-postalcodemap-en" }, build.Stages.Select(s => s.Id));
        Assert.DoesNotContain(build.Notes, n => n.StartsWith("visit:", StringComparison.Ordinal));
        Assert.Contains("visit.scanGround", build.Stages[0].Rows);
        // once every block has its items, the visit is left out
        state.SetItems(Blocks.SelectMany(b => new[] { BlockItem.ScanGround, BlockItem.ScanRoads }.Select(i => (b, i, DateTime.UtcNow))));
        Assert.Equal("gameFiles", BuildStages.For(p, state, new BuildStages.Options(Game: game)).Stages[0].Id);
    }

    [Fact]
    public void ScanLinesAreSortedIntoTheGroundAndTheRoadScan()
    {
        Assert.Equal(ScanKind.Ground, ScanLines.KindOf("[mapscan] MSCAN BEGIN v=1 kind=mat z=8 tx=60 ty=132"));
        Assert.Equal(ScanKind.Roads, ScanLines.KindOf("[fxmapgen] MSCAN BEGIN v=1 kind=road seq=2 block=z8_60_132"));
        Assert.Equal(ScanKind.Ground, ScanLines.KindOf("MSCAN dict mat 3 282940568"));
        Assert.Equal(ScanKind.Roads, ScanLines.KindOf("MSCAN dict zone 1 LEGSQU Legion Square"));
        Assert.Equal(ScanKind.Ground, ScanLines.KindOf("MSCAN fol2 j=3 k=0 x*282"));
        Assert.Equal(ScanKind.Ground, ScanLines.KindOf("MSCAN chunk 1,0 surf=40.0 wait=900"));
        Assert.Equal(ScanKind.Roads, ScanLines.KindOf("[mapscan] MSCAN edge 1 2 3 4 5 6 1 1 5.0 0 0 0 0 0"));
        Assert.Equal(ScanKind.Roads, ScanLines.KindOf("[mapscan] MSCAN DONE z=8 tx=60 ty=132 ms=9000 abort=false"));   // a scan of both kinds at once ("all")
        Assert.Equal(ScanKind.Ground, ScanLines.KindOf("MSCAN DONE seq=1 kind=ground block=z8_60_132 ms=5000"));
        Assert.Null(ScanLines.KindOf("MSCAN ABORT seq=1 kind=ground block=z8_60_132"));
        Assert.Null(ScanLines.KindOf("HMAP j=0 k=0 1.0"));
        Assert.Equal(new[] { "MSCAN BEGIN v=1 kind=mat", "MSCAN BEGIN v=1 kind=road" },
            ScanLines.Merge(["MSCAN BEGIN v=1 kind=mat", "MSCAN BEGIN v=1 kind=road x"], null, ["MSCAN BEGIN v=1 kind=road"]));
    }

    [Fact]
    public void TheHeightQualityDecidesTheItemsOfEveryMap()
    {
        using var tmp = new TempFolder();
        var p = AtlasProject(tmp, SurfaceHeights.Balance, satellite: true);
        IReadOnlyList<BlockItem> Items(bool satellite, bool atlas, string quality)
        {
            p.File.Maps = new MapsSetting { Satellite = satellite, Atlas = new AtlasSetting { Enabled = atlas } };
            p.File.HeightQuality = quality;
            Assert.Empty(p.Validate());
            return VisitStage.ItemsFor(p);
        }
        Assert.Equal(new[] { BlockItem.Shot, BlockItem.Height }, Items(true, false, SurfaceHeights.Speed));
        Assert.Equal(new[] { BlockItem.Shot, BlockItem.ScanGround }, Items(true, false, SurfaceHeights.Balance));      // no road scan for the satellite map
        Assert.Equal(new[] { BlockItem.Shot, BlockItem.Height, BlockItem.ScanGround }, Items(true, false, SurfaceHeights.Quality));
        Assert.Equal(new[] { BlockItem.Height, BlockItem.ScanGround }, SurfaceHeights.PhotoItems(p));        // the photos placed with the higher of the two
        Assert.Equal(new[] { BlockItem.Shot, BlockItem.ScanGround, BlockItem.ScanRoads }, Items(true, true, SurfaceHeights.Balance));
        Assert.Equal(new[] { BlockItem.Shot, BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads }, Items(true, true, SurfaceHeights.Quality));
        Assert.Equal(new[] { BlockItem.ScanGround, BlockItem.ScanRoads }, Items(false, true, SurfaceHeights.Balance));

        // the planner's rows: with balance the height data is not needed; the ground scan serves both maps
        Items(true, true, SurfaceHeights.Balance);
        var state = StateStore.Open(new WorkFolder(p.WorkFolderPath));
        TodoRow Row(string id) => Planner.Build(p, state, 1, 1).Rows.SelectMany(r => r.Children.Prepend(r)).Single(r => r.Id == id);
        Assert.Equal("notNeeded", Row("visit.height").Reason);
        Assert.Equal(new[] { "satellite", "atlas-postalcodemap-en" }, Row("visit.scanGround").For);

        // what does not go together is refused with the reason
        p.File.HeightQuality = SurfaceHeights.Speed;
        Assert.Contains("height quality 'speed' does not go with an atlas or road map (their ground scan gives the heights)", p.Validate());
        p.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
        p.File.HeightQuality = SurfaceHeights.Quality;                                          // the height data serves the landcover too
        Assert.DoesNotContain(p.Validate(), e => e.StartsWith("height quality", StringComparison.Ordinal));
        p.File.HeightQuality = "lidar";
        Assert.Contains("height quality 'lidar' (speed, balance or quality)", p.Validate());

        // the defaults, and what a change of the maps keeps
        Assert.Equal((SurfaceHeights.Speed, SurfaceHeights.Quality, SurfaceHeights.Quality),
            (SurfaceHeights.DefaultFor(true, false), SurfaceHeights.DefaultFor(true, true), SurfaceHeights.DefaultFor(false, true)));
        Assert.Null(SurfaceHeights.Keep(SurfaceHeights.Speed, true, true));                                 // an atlas added: the default
        Assert.Equal(SurfaceHeights.Quality, SurfaceHeights.Keep(SurfaceHeights.Quality, false, true));    // the satellite map taken away: still goes
        Assert.Equal(SurfaceHeights.Balance, SurfaceHeights.Keep(SurfaceHeights.Balance, true, false));   // still goes: kept
        // not chosen (null): the default for the maps, which follows them
        p.File.HeightQuality = null;
        p.File.Maps = new MapsSetting { Satellite = true };
        Assert.Equal(SurfaceHeights.Speed, SurfaceHeights.QualityOf(p.File));
        p.File.Maps.Roadmap = true;
        Assert.Equal(SurfaceHeights.Quality, SurfaceHeights.QualityOf(p.File));
        p.File.Maps.Satellite = false;
        Assert.Equal(SurfaceHeights.Quality, SurfaceHeights.QualityOf(p.File));
        Assert.Empty(p.Validate());

        // with balance the landcover (and the cells' shade and contours) take the ground scan: it waits for the scans only
        p.File.HeightQuality = SurfaceHeights.Balance;
        state.SetItems(Blocks.SelectMany(b => new[] { (b, BlockItem.ScanGround, DateTime.UtcNow), (b, BlockItem.ScanRoads, DateTime.UtcNow) }));
        Assert.Equal(new[] { BlockItem.ScanGround }, SurfaceHeights.LandcoverItems(p));
        Assert.Equal(2, LandcoverStage.Left(p, state).Ready.Count);
        // with quality both, per point the higher: it waits for the height data too
        p.File.Maps.Satellite = true;
        p.File.HeightQuality = SurfaceHeights.Quality;
        Assert.Equal(new[] { BlockItem.Height, BlockItem.ScanGround }, SurfaceHeights.LandcoverItems(p));
        Assert.Equal(2, LandcoverStage.Left(p, state).Waiting.Count);
        state.SetItems(Blocks.Select(b => (b, BlockItem.Height, DateTime.UtcNow)));
        Assert.Equal(2, LandcoverStage.Left(p, state).Ready.Count);
        Assert.Equal(("both", "scan", "grid"), (SurfaceHeights.NameOf(SurfaceHeights.LandcoverItems(p)), SurfaceHeights.NameOf([BlockItem.ScanGround]), SurfaceHeights.NameOf([BlockItem.Height])));
    }

    [Fact]
    public void TwoHeightFilesGiveTheHigherPerPoint()
    {
        using var tmp = new TempFolder();
        string Scan(string name, string hz, string water)
        {
            var path = tmp.File(name + ".txt");
            File.WriteAllLines(path, [
                "MSCAN BEGIN v=1 kind=mat z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=281.250 n=2 flags=1 fol=1",
                "MSCAN dict mat 1 282940568",
                "MSCAN mat j=0 k=0 1*2", "MSCAN hz j=0 k=0 " + hz, "MSCAN water j=0 k=0 " + water, "MSCAN fol j=0 k=0 x*2",
                "MSCAN mat j=1 k=0 1*2", "MSCAN hz j=1 k=0 x*2", "MSCAN water j=1 k=0 .*2", "MSCAN fol j=1 k=0 x*2",
            ]);
            return path;
        }
        var a = Scan("a", "10.0 4.0", ". .");
        var b = Scan("b", "8.0 x", ". 6.5");
        var h = SurfaceHeights.Read([a, b]);
        Assert.Equal(new[] { 10.0, 6.5 }, h.Values[..2]);                    // the higher; a point without a value takes the other's
        Assert.All(h.Values[2..], v => Assert.True(double.IsNaN(v)));        // none in either
        Assert.Equal(h.Values, SurfaceHeights.Read([b, a]).Values);
    }

    [Fact]
    public void TheScansSurfaceIsTheHigherOfTheHitAndTheWater()
    {
        var s = ScanFile.Parse([
            "MSCAN BEGIN v=1 kind=mat z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=140.625 n=3 flags=1 fol=1",
            "MSCAN dict mat 1 282940568",
            "MSCAN mat j=0 k=0 1 1 0", "MSCAN hz j=0 k=0 10.0 2.0 x", "MSCAN water j=0 k=0 . 5.0 7.5", "MSCAN fol j=0 k=0 x*3",
            "MSCAN mat j=1 k=0 0*3", "MSCAN hz j=1 k=0 x*3", "MSCAN water j=1 k=0 .*3", "MSCAN fol j=1 k=0 x*3",
            "MSCAN mat j=2 k=0 1*3", "MSCAN hz j=2 k=0 -3.5*3", "MSCAN water j=2 k=0 .*3", "MSCAN fol j=2 k=0 x*3",
        ]);
        var h = SurfaceHeights.FromScan(s);
        Assert.Equal((3, 78.75, -881.25, 140.625), (h.N, h.X0, h.Y0, h.Step));
        Assert.Equal(new[] { 10.0, 5.0, 7.5 }, h.Values[..3]);
        Assert.All(h.Values[3..6], v => Assert.True(double.IsNaN(v)));
        Assert.Equal(-3.5, h.Values[8]);
    }

    [Fact]
    public async Task TheOrthorectificationReadsTheHeightsOfTheQuality()
    {
        using var tmp = new TempFolder();
        async Task<Dictionary<string, byte[]>> Tiles(string name, string heights)
        {
            var p = Project.Create(tmp.File($"{name}/p.fxmapgen.json"));
            p.File.Maps = new MapsSetting { Satellite = true };
            p.File.Range = new RangeSetting { Base = "none", Add = Blocks.Select(b => b.Name).ToList() };
            p.File.HeightQuality = heights;
            p.Save();
            var folder = new WorkFolder(p.WorkFolderPath);
            Directory.CreateDirectory(folder.Capture);
            Directory.CreateDirectory(folder.Scan);
            var state = StateStore.Open(folder);
            foreach (var b in Blocks)
            {
                SyntheticCapture.Write(folder.Capture, b, ground: 12.5);
                File.WriteAllLines(folder.ScanFile(b), ScanOfHeights(folder.CaptureHeights(b)));
                if (heights == SurfaceHeights.Balance) File.Delete(folder.CaptureHeights(b));        // not taken
                state.SetItems(SurfaceHeights.PhotoItems(p).Prepend(BlockItem.Shot).Select(i => (b, i, DateTime.UtcNow)));
            }
            var end = await JobRunner.Create(new JobSetup
            {
                ProjectPath = p.FilePath, Workers = 2, Processors = 2, Memory = new FakeMemory(), State = state,
                Stages = (_, _) => new BuildPlan([new OrthoStage(new OrthoStage.Options((1, 1)))], []),
            }).RunAsync();
            Assert.Equal(JobState.Done, end.State);
            var root = folder.Tiles(MapSet.Satellite.Id);
            return Directory.GetFiles(root, "*.png", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes);
        }
        var grid = await Tiles("speed", SurfaceHeights.Speed);
        foreach (var q in new[] { SurfaceHeights.Balance, SurfaceHeights.Quality })
        {
            var other = await Tiles(q, q);
            Assert.Equal(grid.Keys.Order(), other.Keys.Order());
            Assert.All(grid, kv => Assert.True(kv.Value.AsSpan().SequenceEqual(other[kv.Key]), q + " " + kv.Key));
        }
    }

    /// <summary>A ground scan whose hits are the heights of a height grid file (no water).</summary>
    static IEnumerable<string> ScanOfHeights(string hmap)
    {
        var h = HeightGrid.Read(hmap);
        var inv = CultureInfo.InvariantCulture;
        yield return string.Format(inv, "MSCAN BEGIN v=1 kind=mat z=8 tx={0} ty={1} x0={2:F4} y0={3:F4} size={4:F4} step={5:F3} n={6} flags=1 fol=1",
            h.Tx, h.Ty, h.X0, h.Y0, h.Size, h.Step, h.N);
        yield return "MSCAN dict mat 1 282940568";
        for (int j = 0; j < h.N; j++)
        {
            yield return $"MSCAN mat j={j} k=0 1*{h.N}";
            yield return $"MSCAN hz j={j} k=0 " + string.Join(' ', Enumerable.Range(0, h.N).Select(i => h.Values[j * h.N + i].ToString("0.0", inv)));
            yield return $"MSCAN water j={j} k=0 .*{h.N}";
            yield return $"MSCAN fol j={j} k=0 x*{h.N}";
        }
        yield return "MSCAN END kind=mat";
    }
}
