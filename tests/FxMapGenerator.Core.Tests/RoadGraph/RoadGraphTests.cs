using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.RoadGraph;

public sealed class RoadGraphTests
{
    static readonly DateTime T0 = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ numbers and lines

    [Fact]
    public void NumbersComeOutAsCPythonAndNumpyComputeThem()
    {
        Assert.Equal(5.0, Num.Hypot(3, -4));
        Assert.Equal(0.0, Num.Hypot(0, 0));
        Assert.Equal(double.PositiveInfinity, Num.Hypot(double.NaN, double.PositiveInfinity));
        Assert.Equal(0.12, Num.Round(0.125, 2));        // an exact tie goes to the even digit
        Assert.Equal(2.67, Num.Round(2.675, 2));        // 2.675 is just below the tie
        Assert.Equal(-0.0, Num.Round(-0.001, 2));
        Assert.True(double.IsNegative(Num.Round(-0.001, 2)));
        Assert.Equal(20996.1, Num.Round(20996.06, 1));
        Assert.Equal(2, Num.RoundToInt(2.5));
        Assert.Equal(double.NaN, Num.Median([1.0, double.NaN, 3.0]));
        Assert.Equal(2.5, Num.Median([4.0, 1.0, 2.0, 3.0]));
        Assert.Equal(7.0, Num.PyMax(7.0, double.NaN));
        double[] xp = [0, 5, 5, 10], fp = [0, 1, 3, 4];
        Assert.Equal(3.0, Num.Interp(5, xp, fp));       // on repeated points: the last one
        Assert.Equal(0.0, Num.Interp(-1, xp, fp));
        Assert.Equal(4.0, Num.Interp(11, xp, fp));
        Assert.Equal(3.5, Num.Interp(7.5, xp, fp));
        var m = Num.MovingMean([0, 0, 11, 0, 0], 1);   // ends extended with their own value
        double[] expect = [0.0, 11.0 / 3, 11.0 / 3, 11.0 / 3, 0.0];
        for (int i = 0; i < m.Length; i++) Assert.Equal(expect[i], m[i], 12);
    }

    [Fact]
    public void LinesAreSimplifiedAndGentleCornersCut()
    {
        var wobble = new List<P2> { new(0, 0), new(10, 0.5), new(20, -0.4), new(30, 0) };
        Assert.Equal(new List<P2> { new(0, 0), new(30, 0) }, Polyline.Rdp(wobble, 1.5));
        var corner = new List<P2> { new(0, 0), new(10, 0), new(10, 10) };      // 90 degrees: stays
        Assert.Equal(corner, Polyline.Rdp(corner, 1.5));
        Assert.Equal(corner, Polyline.Chaikin(corner));
        var bend = new List<P2> { new(0, 0), new(20, 0), new(40, 8) };         // about 22 degrees: cut twice
        var cut = Polyline.Chaikin(bend);
        Assert.Equal(6, cut.Count);
        Assert.Equal(bend[0], cut[0]);
        Assert.Equal(bend[^1], cut[^1]);
        var loop = new List<P2> { new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0.5) };   // ends closer than 1.5 m: halved first
        Assert.Equal(5, Polyline.Rdp(loop, 1.5).Count);
        var st = Polyline.Stations([new(0, 0), new(10, 0)], 4.0, 1.0);
        Assert.Equal(new[] { 1.0, 5.0, 9.0 }, st.Select(s => s.X).ToArray());
        Assert.All(st, s => Assert.Equal((0.0, 1.0), (s.Nx, s.Ny)));
    }

    [Fact]
    public void RunsAroundTheCentreHaveEdgesHalfASampleOut()
    {
        var p = new bool[21];
        for (int i = 8; i <= 14; i++) p[i] = true;
        Assert.Equal(new SurfaceWidth.Run(-0.625, 1.125, false, false), SurfaceWidth.RunAround(p, 10, 0.25, 2.5));
        var off = SurfaceWidth.RunAround(p, 4, 0.25, 2.5)!.Value;                  // the nearest on-road sample, 1 m away
        Assert.True(off.OffCentre);
        Assert.Equal((0.875, 2.625), (off.Left, off.Right));
        Assert.Null(SurfaceWidth.RunAround(p, 4, 0.25, 2.5, search: 0.5));
        Array.Fill(p, true);
        Assert.True(SurfaceWidth.RunAround(p, 10, 0.25, 2.5)!.Value.Open);
        Assert.Equal(201, SurfaceWidth.Offsets(25, 0.25).Length);
        Assert.Equal(-25.0, SurfaceWidth.Offsets(25, 0.25)[0]);
        Assert.Equal(25.0, SurfaceWidth.Offsets(25, 0.25)[^1]);
    }

    // ------------------------------------------------------------------ chains

    static PathNet.Node N(string key, double x, double y, uint street = 0, bool junction = false, bool switchedOff = false) =>
        new(key, x, y, 0, street, junction, false, false, switchedOff);

    [Fact]
    public void ChainsGoStraightThroughAJunctionHub()
    {
        // a crossing of two streets at a junction node; every connection two-way
        var nodes = new List<PathNet.Node>
        {
            N("1:0", 0, 0, junction: true), N("1:1", 0, 20, 1), N("1:2", 0, -20, 1), N("1:3", 20, 0, 2), N("1:4", -20, 0, 2), N("1:5", 0, 40, 1),
        };
        var net = PathNet.Of(nodes, [("1:1", "1:0", 1, 1), ("1:0", "1:3", 1, 1), ("1:2", "1:0", 1, 1), ("1:4", "1:0", 1, 1), ("1:5", "1:1", 1, 1)]);
        var chains = Chains.Build(net);
        Assert.Equal(2, chains.Count);
        string Keys(IEnumerable<int> c) => string.Join(" ", c.Select(i => net.Nodes[i].Key));
        Assert.Equal("1:2 1:0 1:1 1:5", Keys(chains.Single(c => c.Contains(1))));
        Assert.Contains(chains, c => Keys(c) is "1:3 1:0 1:4" or "1:4 1:0 1:3");
        Assert.True(net.IsHub(0));
        Assert.Equal(3, Chains.Straighten(net, chains.Single(c => c.Contains(1))).Count);     // the hub is not on the line
        var a = Chains.Attributes(net, chains.Single(c => c.Contains(1)));
        Assert.Equal((60.0, 1u, true, false), (a.Length, a.Street, a.Named, a.OneWay));
    }

    [Fact]
    public void TwoDifferentStreetsDoNotContinueIntoEachOtherAtAHub()
    {
        // a T: the through street is 1, the side street 2 meets it at 30 degrees off straight
        var nodes = new List<PathNet.Node> { N("1:0", 0, 0, junction: true), N("1:1", -20, 0, 1), N("1:2", 20, 0, 1), N("1:3", 17.3, -10, 2) };
        var net = PathNet.Of(nodes, [("1:1", "1:0", 1, 1), ("1:0", "1:2", 1, 1), ("1:0", "1:3", 1, 1)]);
        var chains = Chains.Build(net);
        Assert.Equal(2, chains.Count);
        Assert.Contains(chains, c => c.Count == 3 && c.Contains(1) && c.Contains(2));
    }

    // ------------------------------------------------------------------ the whole build on a synthetic block

    /// <summary>Road pass of block z8_60_132 (x0 78.75, y0 -881.25) with a north-south on-road band of 25 m, columns 100-124 (x 178.75-202.75).</summary>
    static ScanFile BandScan()
    {
        var lines = new List<string>
        {
            "[fxmapgen] MSCAN BEGIN v=1 kind=road z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=4.000 n=71 pstep=1.000 pn=282 ring=0",
        };
        for (int j = 0; j < 282; j++) lines.Add($"[fxmapgen] MSCAN onroad j={j} k=0 0*100 1*25 0*157");
        lines.Add("[fxmapgen] MSCAN END kind=road n=71");
        return ScanFile.Parse(lines);
    }

    /// <summary>
    /// A divided street 1234 on the band: two one-way chains of 11 nodes, 12 m apart (x 184.75 southbound, 196.75
    /// northbound), and a 60 m unnamed, switched-off side road off the west chain (a parking aisle).
    /// </summary>
    static PathNet DividedStreet()
    {
        var nodes = new List<PathNet.Node>();
        var links = new List<(string, string, int, int)>();
        for (int i = 0; i <= 10; i++) nodes.Add(N($"1:{i}", 184.75, -921.25 - 20 * i, i == 5 ? 0u : 1234u));
        for (int i = 0; i <= 10; i++) nodes.Add(N($"1:{20 + i}", 196.75, -921.25 - 20 * i, 1234));
        for (int i = 1; i <= 3; i++) nodes.Add(N($"1:{40 + i}", 184.75 - 20 * i, -1021.25, switchedOff: true));
        for (int i = 0; i < 10; i++) links.Add(($"1:{i}", $"1:{i + 1}", 2, 0));
        for (int i = 0; i < 10; i++) links.Add(($"1:{21 + i}", $"1:{20 + i}", 2, 0));
        links.Add(("1:5", "1:41", 1, 1));
        links.Add(("1:41", "1:42", 1, 1));
        links.Add(("1:42", "1:43", 1, 1));
        return PathNet.Of(nodes, links);
    }

    [Fact]
    public void TheTwoCarriagewaysOfAStreetBecomeOneMajorRoadMeasuredAcrossTheBand()
    {
        var area = new ScanArea([BandScan()]);
        var r = RoadGraphBuilder.Build(DividedStreet(), area, new FixedParallel(2));
        Assert.Equal((3, 2), (r.Roads.Count, r.Bundled));
        var carriageways = r.Roads.Where(x => x.Class == RoadClass.Major).ToList();
        Assert.Equal(2, carriageways.Count);
        foreach (var c in carriageways)
        {
            Assert.True(c.Bundle);
            Assert.Equal(25.0, c.Width);                                        // the band, measured once across both
            Assert.Equal(2, c.Points.Count);                                       // straight: two points
            Assert.All(c.Points, p => Assert.InRange(p.X, 190.25, 191.25));        // on the midline (190.75)
        }
        var aisle = Assert.Single(r.Roads, x => x.Class == RoadClass.Parking);
        Assert.Equal((4.0, 60.0, false), (aisle.Width, aisle.Length, aisle.Bundle));
    }

    [Fact]
    public void ChainsOfOneStreetAtDifferentHeightsStayApart()
    {
        var net = DividedStreet();
        var raised = PathNet.Of(net.Nodes.Select((n, i) => i is >= 11 and <= 21 ? n with { Z = 8 } : n).ToList(),      // the east chain 8 m up
            net.Links.Select(l => (net.Nodes[l.A].Key, net.Nodes[l.B].Key, l.LanesAb, l.LanesBa)));
        var r = RoadGraphBuilder.Build(raised, new ScanArea([BandScan()]), new FixedParallel(1));
        Assert.Equal(0, r.Bundled);
        Assert.Single(r.HeightRejected);
        Assert.Equal(2, r.Roads.Count(x => x.Class == RoadClass.Major && !x.Bundle));   // each on its own line, 25 m wide
    }

    [Fact]
    public void TheFileHoldsEachRoadsClassWidthAndLine()
    {
        using var tmp = new TempFolder();
        var r = RoadGraphBuilder.Build(DividedStreet(), new ScanArea([BandScan()]), new FixedParallel(1));
        var path = tmp.File("roads.json");
        RoadGraphFile.Write(path, r.Roads);
        var text = File.ReadAllText(path);
        Assert.StartsWith("{\"roads\":[{\"class\":\"major\",\"width\":25,\"divided\":true,\"street\":1234,\"oneWay\":true,\"length\":200,\"lanes\":[2,0],\"points\":[[", text);
        var back = RoadGraphFile.Read(path);
        Assert.Equal(r.Roads.Select(x => x.Class), back.Select(x => x.Class));
        Assert.Equal(-1121.25, back[0].Points[^1].Y);
        Assert.Equal((25.0, 200.0), (back[0].Width, back[0].Length));
        Assert.Equal((1.0, 1.0), (back[2].LanesForward, back[2].LanesBack));
    }

    // ------------------------------------------------------------------ the stage

    [Fact]
    public void TheGraphIsBuiltAgainAfterNewerGameFilesOrScansOrAnotherRange()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("t.fxmapgen.json"));
        project.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
        project.File.Range = new RangeSetting { Base = "none", Add = ["z8_60_132", "z8_64_132"] };
        var folder = new WorkFolder(project.WorkFolderPath);
        var state = StateStore.Open(folder);
        Assert.True(RoadGraphStage.IsStale(project, state));                                    // never built

        Directory.CreateDirectory(folder.Game);
        foreach (var f in new[] { GameFilesOutput.Paths, GameFilesOutput.Names })
            File.WriteAllText(Path.Combine(folder.Game, f), "{}");
        File.WriteAllText(Path.Combine(folder.Game, GameFilesOutput.Record), JsonSerializer.Serialize(
            new GameFilesStage.SourcesRecord("gta", "keys", [], 0, [], 0, 0, 0, 0, [], [], [], new Dictionary<string, int>(), 0, 0, 0, 0, 0, 0, 0), Project.Json));
        Directory.CreateDirectory(folder.Data);
        File.WriteAllText(Path.Combine(folder.Data, RoadGraphFile.Roads), "{}");
        void Record(params string[] blocks) => File.WriteAllText(Path.Combine(folder.Data, RoadGraphStage.Record),
            JsonSerializer.Serialize(new RoadGraphStage.RoadsRecord(blocks, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0), Project.Json));
        Record("z8_64_132", "z8_60_132");
        state.SetItem(BlockId.Parse("z8_60_132"), BlockItem.ScanRoads, T0.AddMinutes(-10));
        state.SetStageDone(StageKeys.GameFiles, StageKeys.World, T0.AddMinutes(-5));
        state.SetStageDone(StageKeys.RoadGraph, StageKeys.World, T0);
        Assert.False(RoadGraphStage.IsStale(project, state));
        Assert.Equal(0, Planner.Build(project, state, 1, 1).Rows.Single(r => r.Id == "mapData").Children.Single(c => c.Id == "mapData.roadGraph").Remaining);

        state.SetStageDone(StageKeys.GameFiles, StageKeys.World, T0.AddMinutes(5));
        Assert.True(RoadGraphStage.IsStale(project, state));                                    // the game files were read again
        state.SetStageDone(StageKeys.RoadGraph, StageKeys.World, T0.AddMinutes(10));
        Assert.False(RoadGraphStage.IsStale(project, state));
        state.SetItem(BlockId.Parse("z8_64_132"), BlockItem.ScanRoads, T0.AddMinutes(15));
        Assert.True(RoadGraphStage.IsStale(project, state));                                    // a newer road scan
        state.SetStageDone(StageKeys.RoadGraph, StageKeys.World, T0.AddMinutes(20));
        Assert.False(RoadGraphStage.IsStale(project, state));
        project.File.Range = new RangeSetting { Base = "none", Add = ["z8_60_132"] };
        Assert.True(RoadGraphStage.IsStale(project, state));                                    // built from other blocks
        Record("z8_60_132");
        Assert.False(RoadGraphStage.IsStale(project, state));
        File.Delete(Path.Combine(folder.Data, RoadGraphFile.Roads));
        Assert.True(RoadGraphStage.IsStale(project, state));                                    // the file is gone
    }

    [Fact]
    public void TheStageWaitsForTheRoadScansAndTheGameFiles()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("t.fxmapgen.json"));
        project.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
        project.File.Range = new RangeSetting { Base = "none", Add = ["z8_60_132"] };
        var folder = new WorkFolder(project.WorkFolderPath);
        var state = StateStore.Open(folder);
        var build = BuildStages.For(project, state);
        Assert.Equal(new[] { "gameFiles", "roadGraph", "landcover", "regions", "roads", "cellPrep", "cells.roadmap", "lowZoom.roadmap" }, build.Stages.Select(s => s.Id).ToArray());
        var stage = new RoadGraphStage();
        Assert.Equal(0, stage.CountReady(project, state));                                      // no road scan yet
        state.SetItem(BlockId.Parse("z8_60_132"), BlockItem.ScanRoads, T0);
        Assert.Equal(1, stage.CountReady(project, state));
    }
}
