using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Roads;

public sealed class RoadsTests
{
    static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A small path file: nodes and two-way or one-way connections (both directed records written).</summary>
    sealed class Paths
    {
        readonly JsonObject _nodes = new();
        readonly JsonArray _links = new();
        readonly JsonObject _streets = new();

        public Paths Node(string key, double x, double y, double z = 0, bool junction = false, bool highway = false, bool tunnel = false,
            bool unpaved = false, uint street = 0, double[]? area = null)
        {
            var n = new JsonObject
            {
                ["x"] = x, ["y"] = y, ["z"] = z, ["street"] = street, ["junction"] = junction, ["highway"] = highway, ["tunnel"] = tunnel,
                ["unpaved"] = unpaved, ["switchedOff"] = false,
            };
            if (area is not null) n["junctionArea"] = new JsonArray(area.Select(v => (JsonNode)v).ToArray());
            _nodes[key] = n;
            return this;
        }

        public Paths Link(string a, string b, int forward = 1, int back = 1, bool dontUseForNavigation = false, bool narrow = false, double offset = 0)
        {
            JsonObject L(string f, string t, int lf, int lb) => new()
            {
                ["from"] = f, ["to"] = t, ["lanesForward"] = lf, ["lanesBack"] = lb, ["narrow"] = narrow, ["dontUseForNavigation"] = dontUseForNavigation,
                ["shortcut"] = false, ["laneOffset"] = offset, ["gpsBothWays"] = false, ["length"] = 1,
            };
            _links.Add(L(a, b, forward, back));
            _links.Add(L(b, a, back, forward));
            return this;
        }

        public Paths Street(uint hash, string name)
        {
            _streets[hash.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["en"] = name };
            return this;
        }

        public void Write(string gameFolder)
        {
            Directory.CreateDirectory(gameFolder);
            File.WriteAllText(Path.Combine(gameFolder, GameFilesOutput.Paths), new JsonObject { ["areas"] = new JsonArray(), ["nodes"] = _nodes.DeepClone(), ["links"] = _links.DeepClone() }.ToJsonString());
            File.WriteAllText(Path.Combine(gameFolder, GameFilesOutput.Names), new JsonObject { ["streets"] = _streets.DeepClone(), ["zones"] = new JsonObject() }.ToJsonString());
        }

        public RoadNet Net(TempFolder tmp)
        {
            var g = tmp.File("game");
            Write(g);
            return RoadNet.Read(Path.Combine(g, GameFilesOutput.Paths), Path.Combine(g, GameFilesOutput.Names));
        }
    }

    static RoadLinks Links(RoadNet g) => RoadLinks.Build(g, g.Junctions, g.Highways(), g.Drawable.ToDictionary(k => k, _ => false));

    [Fact]
    public void LaneWidthsFollowTheLanesTheOffsetAndNarrowLinks()
    {
        LinkRecord R(int f, int b, bool narrow = false, double off = 0) => new(f, b, narrow, false, false, off);
        Assert.Equal(11.0, RoadNet.LaneWidthOf(new Connection { Ab = R(1, 1), Ba = R(1, 1) }));
        Assert.Equal(5.5, RoadNet.LaneWidthOf(new Connection { Ab = R(1, 1, off: -0.5), Ba = R(1, 1, off: -0.5) }));   // single track
        Assert.Equal(8.0, RoadNet.LaneWidthOf(new Connection { Ab = R(1, 1, narrow: true), Ba = R(1, 1) }));
        Assert.Equal(11.0, RoadNet.LaneWidthOf(new Connection { Ba = R(0, 2) }));                                    // from the other end only
        Assert.Equal(5.5, RoadNet.LaneWidthOf(new Connection { Ab = R(0, 0) }));                                     // at least one lane
    }

    [Fact]
    public void HighwaysByTheFreewayNameTheFlaggedStretchAndTheUnnamedRamp()
    {
        using var tmp = new TempFolder();
        var g = new Paths().Street(1, "Del Perro Fwy").Street(2, "Main St").Street(3, "Side St")
            .Node("1:1", 0, 0, street: 1).Node("1:2", 50, 0, street: 1).Node("1:3", 100, 0, street: 1)
            .Node("1:4", 0, 200, highway: true, street: 2).Node("1:5", 100, 200, highway: true, street: 2)          // flagged, 100 m
            .Node("1:6", 0, 400, highway: true, street: 3).Node("1:7", 30, 400, highway: true, street: 3)           // flagged, 30 m
            .Node("1:8", 0, 600, highway: true).Node("1:9", 20, 600, highway: true)                                 // unnamed ramp
            .Link("1:1", "1:2").Link("1:2", "1:3").Link("1:4", "1:5").Link("1:6", "1:7").Link("1:8", "1:9").Net(tmp);
        var hw = g.Highways();
        Assert.Equal(new[] { ("1:1", "1:2"), ("1:2", "1:3"), ("1:4", "1:5"), ("1:8", "1:9") }, hw.Order(RoadNet.KeyOrder).ToArray());
        Assert.True(RoadNet.IsFreewayName("Great Ocean Hwy") && !RoadNet.IsFreewayName("Route 68"));
    }

    [Fact]
    public void JunctionConnectorsGiveWayToTheArrivingRoadsGoingThrough()
    {
        using var tmp = new TempFolder();
        double[] sq = [-8, -8, 8, 8];
        var g = new Paths()
            .Node("1:1", -100, 0).Node("1:2", -10, 0).Node("1:3", 0, 0, junction: true, area: sq).Node("1:4", 10, 0).Node("1:5", 100, 0)
            .Node("1:6", 0, 100)
            .Link("1:1", "1:2").Link("1:2", "1:3", dontUseForNavigation: true).Link("1:3", "1:4", dontUseForNavigation: true).Link("1:4", "1:5")
            .Link("1:3", "1:6").Net(tmp);
        var L = Links(g);
        Assert.Equal(2, L.Modes["extend"]);
        Assert.Equal(1, L.Through);
        // the two connectors are not drawn; one link through the junction instead, split at the point nearest it
        Assert.DoesNotContain(("1:2", "1:3"), L.Keys);
        Assert.Contains(("1:2", "1:3|thr|1:2"), L.Keys);
        Assert.Contains(("1:3|thr|1:2", "1:4"), L.Keys);
        Assert.Equal((0.0, 0.0), (L.N["1:3|thr|1:2"].X, L.N["1:3|thr|1:2"].Y));
        // the road going on through the junction is one piece; the side road ends there
        var lv = RoadLevels.Compute(L.Keys, L.Rows);
        var s = RoadShapes.Build(L, lv.Level, lv.Raised);
        Assert.Equal(2, s.Pieces.Count);
        Assert.Equal(4, s.Pieces.Max(p => p.Length));
    }

    [Fact]
    public void AForkStartsItsBranchesSideBySideOnTheTrunk()
    {
        using var tmp = new TempFolder();
        double a = 15 * Math.PI / 180;
        var g = new Paths()
            .Node("1:1", -100, 0).Node("1:2", 0, 0)
            .Node("1:3", 100 * Math.Cos(a), 100 * Math.Sin(a)).Node("1:4", 100 * Math.Cos(a), -100 * Math.Sin(a))
            .Link("1:1", "1:2", forward: 2, back: 0).Link("1:2", "1:3", forward: 1, back: 0).Link("1:2", "1:4", forward: 1, back: 0).Net(tmp);
        var L = Links(g);
        Assert.Equal(1, L.ForkCount);
        Assert.Contains("1:2", L.Forks);
        var b1 = L.N["1:2|1:3"];
        var b2 = L.N["1:2|1:4"];
        // the trunk is 11 m wide, each branch 5.5 m: the branch ends 5.5 m apart, their outer span centred on the trunk
        Assert.Equal(5.5, Math.Abs(b1.Y - b2.Y), 6);
        Assert.Equal(0.0, (b1.Y + b2.Y) / 2, 6);
        Assert.Equal(-100.0, b1.Phantom!.Value.X, 6);                                       // the trunk's previous node, shifted alike
        Assert.Equal(2.75, Math.Abs(b1.Phantom.Value.Y), 6);
        Assert.Equal(("1:1", "1:2"), L.Keys[L.N["1:2"].ForkTrunk!.Value]);
    }

    [Fact]
    public void ARoadPassingOverAnotherIsRaised()
    {
        using var tmp = new TempFolder();
        var g = new Paths()
            .Node("1:1", -100, 0).Node("1:2", 100, 0)
            .Node("1:3", 0, -100, z: 10).Node("1:4", 0, 100, z: 10)
            .Link("1:1", "1:2").Link("1:3", "1:4").Net(tmp);
        var L = Links(g);
        var lv = RoadLevels.Compute(L.Keys, L.Rows);
        Assert.Equal(new[] { 0, 1 }, lv.Level);
        var s = RoadShapes.Build(L, lv.Level, lv.Raised);
        Assert.Equal(2, s.Ground.Count);
        Assert.Equal(1, s.Raised.Single().Level);
    }

    [Fact]
    public void AStraightDeadEndRoadIsOneRibbonWithRoundedTips()
    {
        using var tmp = new TempFolder();
        var g = new Paths().Node("1:1", 0, 0).Node("1:2", 100, 0).Link("1:1", "1:2").Net(tmp);
        var L = Links(g);
        var lv = RoadLevels.Compute(L.Keys, L.Rows);
        var s = RoadShapes.Build(L, lv.Level, lv.Raised);
        var r = s.Ground.Single();
        Assert.Equal(ShapeClass.Road, r.Class);
        static (double, double, double, double) Box(double[] p) =>
            (p.Where((_, i) => i % 2 == 0).Min(), p.Where((_, i) => i % 2 == 0).Max(), p.Where((_, i) => i % 2 == 1).Min(), p.Where((_, i) => i % 2 == 1).Max());
        var (x0, x1, y0, y1) = Box(r.Fill);
        Assert.Equal((0.0, 100.0, -5.5, 5.5), (Math.Round(x0, 6), Math.Round(x1, 6), Math.Round(y0, 6), Math.Round(y1, 6)));
        // the casing runs 0.6 m further past the dead ends and 0.6 m wider on each side
        (x0, x1, y0, y1) = Box(r.Casing);
        Assert.Equal((-0.6, 100.6, -6.1, 6.1), (Math.Round(x0, 6), Math.Round(x1, 6), Math.Round(y0, 6), Math.Round(y1, 6)));
        // 4 rounded corners of 5 points instead of the 4 corner points
        Assert.Equal(51 * 2 + 4 * 4, r.Fill.Length / 2);
    }

    [Fact]
    public void TheCurvePassesThroughTheNodes()
    {
        double[] P = [0, 0, 0, 10, 5, 1, 20, 0, 2];
        var spans = RoadShapes.Curve(P, 2.0, null, null);
        Assert.Equal(2, spans.Count);
        Assert.Equal([0, 0, 0], spans[0][..3]);
        Assert.Equal([10, 5, 1], spans[0][^3..]);
        Assert.Equal([10, 5, 1], spans[1][..3]);
        Assert.Equal([20, 0, 2], spans[1][^3..]);
        Assert.True(spans[0][3 * 3 + 1] > 5.0 * 3 / 6);     // bows outward (a curve, not the chord)
    }

    [Fact]
    public void ATunnelOutlineIsTheStrokeWithRoundEnds()
    {
        var rings = TunnelOutlines.Outline([[0, 0, 20, 0, 10]]);
        var ring = rings.Single();
        double area = 0;
        for (int i = 0; i < ring.Length / 2 - 1; i++) area += ring[2 * i] * ring[2 * i + 3] - ring[2 * i + 2] * ring[2 * i + 1];
        Assert.InRange(Math.Abs(area / 2), 0.97 * (200 + Math.PI * 25), 1.03 * (200 + Math.PI * 25));
        Assert.Equal((ring[0], ring[1]), (ring[^2], ring[^1]));                              // closed
        // two strokes far apart: two rings
        Assert.Equal(2, TunnelOutlines.Outline([[0, 0, 20, 0, 10], [0, 50, 20, 50, 10]]).Count);
    }

    // ------------------------------------------------------------------------------------------------ the stage

    static Project StageProject(TempFolder tmp)
    {
        var project = Project.Create(tmp.File("t.fxmapgen.json"));
        project.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
        project.File.Range = new RangeSetting { Base = "none", Add = ["z8_60_132"] };
        project.Save();
        return project;
    }

    /// <summary>The block's landcover: water in the rectangle <paramref name="wet"/>, grass in <paramref name="grass"/>, tarmac elsewhere.</summary>
    static void Landcover(WorkFolder folder, BlockId b, (double X0, double X1, double Y0, double Y1) wet, (double X0, double X1, double Y0, double Y1) grass)
    {
        var (bx, by) = (WorldGrid.Left + b.Tx * WorldGrid.TileSize, WorldGrid.Top - b.Ty * WorldGrid.TileSize);
        int n = 283;
        var water = new Grid<bool>(n, n);
        var surface = Grid<byte>.Filled(n, n, 1);
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                double x = bx + c, y = by - r;
                if (x >= wet.X0 && x <= wet.X1 && y >= wet.Y0 && y <= wet.Y1) { water[r, c] = true; surface[r, c] = 9; }
                if (x >= grass.X0 && x <= grass.X1 && y >= grass.Y0 && y <= grass.Y1) surface[r, c] = 2;
            }
        var f = new GridFile();
        f.Meta["block"] = b.Name;
        f.Meta["x0"] = bx;
        f.Meta["y0"] = by;
        f.Meta["step"] = 1.0;
        f.Add("water", water);
        f.Add("surface", surface);
        Directory.CreateDirectory(Path.Combine(folder.Data, LandcoverFile.Folder));
        f.Save(LandcoverFile.PathOf(folder.Data, b));
    }

    [Fact]
    public async Task TheStageWaitsForItsInputsDropsBoatRoutesFindsTracksAndIsMadeAgainWhenTheyAreNewer()
    {
        using var tmp = new TempFolder();
        var project = StageProject(tmp);
        var folder = new WorkFolder(project.WorkFolderPath);
        var state = StateStore.Open(folder);
        var b = BlockId.Parse("z8_60_132");
        double cx = WorldGrid.Left + b.Tx * WorldGrid.TileSize + 140, cy = WorldGrid.Top - b.Ty * WorldGrid.TileSize - 140;
        // a road on tarmac, a road over grass (a track) and a boat route over water, apart from each other
        new Paths()
            .Node("1:1", cx - 100, cy + 80).Node("1:2", cx + 100, cy + 80)
            .Node("1:3", cx - 100, cy).Node("1:4", cx + 100, cy)
            .Node("1:5", cx - 100, cy - 80).Node("1:6", cx + 100, cy - 80)
            .Link("1:1", "1:2").Link("1:3", "1:4").Link("1:5", "1:6").Write(folder.Game);
        Landcover(folder, b, wet: (cx - 120, cx + 120, cy - 100, cy - 60), grass: (cx - 120, cx + 120, cy - 10, cy + 10));
        var stage = new RoadsStage();
        Assert.Contains("game files", RoadsStage.Waiting(project, state));
        state.SetItems(new[] { BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads }.Select(i => (b, i, T0)));
        state.SetStageDone(StageKeys.GameFiles, StageKeys.World, T0.AddMinutes(1));
        Directory.CreateDirectory(folder.Data);
        File.WriteAllText(Path.Combine(folder.Data, RoadGraphFile.Roads), "{\"roads\":[]}");
        File.WriteAllText(Path.Combine(folder.Data, RoadGraphStage.Record), JsonSerializer.Serialize(
            new RoadGraphStage.RoadsRecord([b.Name], 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0), Project.Json));
        state.SetStageDone(StageKeys.RoadGraph, StageKeys.World, T0.AddMinutes(2));
        Assert.Contains("landcover", RoadsStage.Waiting(project, state));
        state.SetStageDone(StageKeys.Landcover, b.Name, T0.AddMinutes(3));
        LandcoverStage.WriteRecord(folder, SurfaceHeights.NameOf(SurfaceHeights.LandcoverItems(project)));
        Assert.Null(RoadsStage.Waiting(project, state));
        Assert.Equal(1, stage.CountReady(project, state));
        Assert.Equal(1, Planner.Build(project, state, 1, 1).Rows.Single(r => r.Id == "mapData").Children.Single(c => c.Id == "mapData.roads").Remaining);

        var end = await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 2, Processors = 2, Memory = new FakeMemory(), State = state,
            Stages = (_, _) => new BuildPlan([stage], []),
        }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        var rec = RoadsStage.ReadRecord(folder)!;
        Assert.Equal((3, 1, 2), (rec.DrawableLinks, rec.BoatLinks, rec.DrawnLinks));
        Assert.Equal(0.2, rec.UnpavedKm);
        var c = RoadShapesFile.Read(Path.Combine(folder.Data, RoadShapesFile.FileName));
        Assert.Single(c.Ground);                                            // the tarmac road
        Assert.Equal(100, c.Tracks.Length / 4);                             // the grass road: 200 m of 2 m segments
        Assert.Equal(5.0, c.TrackWidth);
        // the road editor's preview makes the same shapes from the same inputs, without files
        var world = RoadNet.WorldGrids(folder.Data, [b], project.Frame);
        var made = RoadsStage.Make(FxMapGenerator.Core.GameFiles.PathFile.Read(Path.Combine(folder.Game, "paths.json")), Path.Combine(folder.Game, "names.json"), world);
        Assert.Equal(JsonSerializer.Serialize(c), JsonSerializer.Serialize(RoadShapesFile.ContentsOf(made.Shapes, made.Tunnels)));
        Assert.Equal((rec.DrawableLinks, rec.BoatLinks), (made.DrawableLinks, made.Boats.Dropped.Sum(d => d.Run.Count)));
        // before the block's landcover is made (the preview while the capture runs) the block is land without a class, not
        // the open sea: no road of it is left out as a boat route (as open sea, they all would be)
        var paths = FxMapGenerator.Core.GameFiles.PathFile.Read(Path.Combine(folder.Game, "paths.json"));
        var early = RoadsStage.Make(paths, Path.Combine(folder.Game, "names.json"), RoadNet.WorldGrids(folder.Data, [], project.Frame, notMade: [b]));
        Assert.Empty(early.Boats.Dropped);
        var sea = RoadsStage.Make(paths, Path.Combine(folder.Game, "names.json"), RoadNet.WorldGrids(folder.Data, [], project.Frame));
        Assert.NotEmpty(sea.Boats.Dropped);
        Assert.False(RoadsStage.IsStale(project, state));
        Assert.Equal(0, Planner.Build(project, state, 1, 1).Rows.Single(r => r.Id == "mapData").Children.Single(c => c.Id == "mapData.roads").Remaining);

        var done = state.StageDone(StageKeys.Roads, StageKeys.World)!.Value;
        state.SetStageDone(StageKeys.Landcover, b.Name, done.AddMinutes(1));
        Assert.True(RoadsStage.IsStale(project, state));                    // a newer landcover
        state.SetStageDone(StageKeys.Roads, StageKeys.World, done.AddMinutes(2));
        Assert.False(RoadsStage.IsStale(project, state));
        state.SetStageDone(StageKeys.GameFiles, StageKeys.World, done.AddMinutes(3));
        Assert.True(RoadsStage.IsStale(project, state));                    // newer game files
    }

    [Fact]
    public void TheShapesFileKeepsEveryPoint()
    {
        using var tmp = new TempFolder();
        var g = new Paths()
            .Node("1:1", -100, 0).Node("1:2", 100, 0).Node("1:3", 0, -100, z: 10).Node("1:4", 0, 100, z: 10)
            .Node("1:5", 0, 300, tunnel: true).Node("1:6", 100, 300, tunnel: true)
            .Link("1:1", "1:2").Link("1:3", "1:4").Link("1:5", "1:6").Net(tmp);
        var L = Links(g);
        var lv = RoadLevels.Compute(L.Keys, L.Rows);
        var s = RoadShapes.Build(L, lv.Level, lv.Raised);
        var tunnels = TunnelOutlines.Build(L.Keys, s.TunnelSegments);
        var path = tmp.File(RoadShapesFile.FileName);
        RoadShapesFile.Write(path, s, tunnels);
        var back = RoadShapesFile.Read(path);
        Assert.Equal(JsonSerializer.Serialize(back), JsonSerializer.Serialize(RoadShapesFile.ContentsOf(s, tunnels)));   // the same without the file
        Assert.Equal(s.Ground.Select(r => r.Casing.Concat(r.Fill)).SelectMany(x => x), back.Ground.Select(r => r.Casing.Concat(r.Fill)).SelectMany(x => x));
        Assert.Equal(s.Raised.Select(r => (r.Level, r.Class, r.Fill.Length)), back.Raised.Select(r => (r.Level, r.Class, r.Fill.Length)));
        Assert.Equal(tunnels.Single().Rings.Single(), back.Tunnels.Single().Rings.Single());
        Assert.Empty(back.Patches);
        Assert.Empty(back.Tracks);
    }
}
