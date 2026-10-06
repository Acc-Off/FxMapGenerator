using System.Globalization;
using System.Text.Json;
using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Landcover;

public sealed class LandcoverTests
{
    static readonly DateTime T0 = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
    static Materials M => Materials.Default;

    /// <summary>
    /// A synthetic block of n x n cells of 1 m: every cell grass at a height unless painted. The height grid of the
    /// block is the higher of the ground hit and the water surface, as the game gives it.
    /// </summary>
    sealed class Synth
    {
        public readonly BlockId Id;
        public readonly int N;
        public readonly string?[] Mat;
        public readonly float[] Z, Water, Probe;
        public readonly bool[] OnRoad;
        public float[]? Canopy;

        public Synth(string block, int n, float z = 10, string ground = "GRASS")
        {
            Id = BlockId.Parse(block);
            N = n;
            Mat = Enumerable.Repeat<string?>(ground, n * n).ToArray();
            Z = Enumerable.Repeat(z, n * n).ToArray();
            Water = Enumerable.Repeat(float.NaN, n * n).ToArray();
            Probe = Enumerable.Repeat(float.NaN, n * n).ToArray();
            OnRoad = new bool[n * n];
        }

        /// <summary>Paints rows r0.. and columns c0.. (h x w cells).</summary>
        public Synth Paint(int r0, int c0, int h, int w, string? mat = "", float? z = null, float? water = null, float? probe = null, bool? onRoad = null)
        {
            for (int r = r0; r < r0 + h; r++)
                for (int c = c0; c < c0 + w; c++)
                {
                    int i = r * N + c;
                    if (mat != "") Mat[i] = mat;
                    if (z is { } zz) Z[i] = zz;
                    if (water is { } ww) Water[i] = ww;
                    if (probe is { } pp) Probe[i] = pp;
                    if (onRoad is { } o) OnRoad[i] = o;
                }
            return this;
        }

        public (double X0, double Y0) Corner => (Id.Rect.X0, Id.Rect.Y0);

        /// <summary>The block's scan lines: the ground pass (with the canopy probe when set) and the road pass.</summary>
        public List<string> Lines()
        {
            static string F(float v) => float.IsNaN(v) ? "x" : v.ToString("0.00", CultureInfo.InvariantCulture);
            var (x0, y0) = Corner;
            var names = Mat.Where(m => m is not null).Distinct().ToList();
            string head = string.Format(CultureInfo.InvariantCulture, "tx={0} ty={1} x0={2:0.0000} y0={3:0.0000} size=281.2500", Id.Tx, Id.Ty, x0, y0);
            var lines = new List<string> { $"[fxmapgen] MSCAN BEGIN v=1 kind=mat z=8 {head} step=1.000 n={N} flags=1 fol=1 chunks=1 pflags=128"
                + (Canopy is null ? "" : " pflags2=256") };
            for (int i = 0; i < names.Count; i++) lines.Add($"[fxmapgen] MSCAN dict mat {i + 1} {M.Names.First(kv => kv.Value == names[i]).Key}");
            void Rows(string kind, Func<int, string> cell)
            {
                for (int r = 0; r < N; r++)
                    lines.Add($"[fxmapgen] MSCAN {kind} j={r} k=0 " + string.Join(' ', Enumerable.Range(0, N).Select(c => cell(r * N + c))));
            }
            Rows("mat", i => Mat[i] is { } m ? (names.IndexOf(m) + 1).ToString(CultureInfo.InvariantCulture) : "0");
            Rows("hz", i => Mat[i] is null ? "x" : F(Z[i]));
            Rows("water", i => F(Water[i]));
            Rows("fol", i => F(Probe[i]));
            if (Canopy is { } canopy) Rows("fol2", i => F(canopy[i]));
            lines.Add($"[fxmapgen] MSCAN END kind=mat n={N}");
            lines.Add($"[fxmapgen] MSCAN BEGIN v=1 kind=road z=8 {head} step=4.000 n={N / 4} pstep=1.000 pn={N}");
            Rows("onroad", i => OnRoad[i] ? "1" : "0");
            lines.Add($"[fxmapgen] MSCAN END kind=road n={N / 4}");
            return lines;
        }

        public ScanFile Scan() => ScanFile.Parse(Lines());

        public void WriteScan(string path) => File.WriteAllLines(path, Lines());

        public float[] Heights() => Enumerable.Range(0, N * N).Select(i => float.IsNaN(Water[i]) ? Z[i] : Math.Max(Z[i], Water[i])).ToArray();

        public BlockSurface Surface() => BlockSurface.From(Id, Scan(), new Grid<float>(N, N, Heights()), M);

        /// <summary>The height grid file as the visit writes it.</summary>
        public void WriteHeights(string path)
        {
            var (x0, y0) = Corner;
            var h = Heights();
            var lines = new List<string> { string.Format(CultureInfo.InvariantCulture, "HMAP BEGIN z=8 tx={0} ty={1} x0={2:0.0000} y0={3:0.0000} size=281.2500 step=1.000 n={4}", Id.Tx, Id.Ty, x0, y0, N) };
            for (int r = 0; r < N; r++)
                lines.Add($"HMAP j={r} k=0 " + string.Join(' ', Enumerable.Range(0, N).Select(c => h[r * N + c].ToString("0.0", CultureInfo.InvariantCulture))));
            lines.Add("HMAP END n=" + N);
            File.WriteAllLines(path, lines);
        }
    }

    static readonly GarageRule.Roads NoRoads = GarageRule.Roads.Of([]);

    static LandcoverBuilder.Result Build(Synth s, GarageRule.Roads? roads = null) =>
        LandcoverBuilder.Compute(s.Surface(), new Dictionary<(int, int), BlockSurface>(), roads ?? NoRoads, M);

    static GroundClass At(Grid<byte> g, int r, int c) => (GroundClass)g[r, c];

    // ------------------------------------------------------------------ ground classes and water

    [Fact]
    public void OpenWaterLeavesOutPiersAndSandNearItIsBeach()
    {
        // sea in columns 0-24 (sea bed of sand 4 m down), a pier across it, sand ashore to column 51, grass beyond
        var s = new Synth("z8_60_132", 60, z: 2, ground: "SAND_LOOSE")
            .Paint(0, 0, 60, 25, z: -4, water: 0)
            .Paint(20, 5, 8, 20, mat: "CONCRETE", z: 3)
            .Paint(0, 52, 60, 8, mat: "GRASS")
            .Paint(10, 56, 1, 1, probe: 2.0f)                     // one cell of the water-collision probe: too small
            .Paint(2, 52, 5, 8, mat: "WATER", probe: 2.0f)        // a pond of 40 cells found by the probe
            .Paint(40, 57, 1, 1, water: 2.04f);                    // a water height just over the ground on one cell: too small
        var r = Build(s);
        Assert.True(r.Water[50, 10]);
        Assert.False(r.Water[23, 15]);                             // the pier stands above the water
        Assert.True(r.Water[4, 55]);
        Assert.False(r.Water[10, 56]);
        Assert.False(r.Water[40, 57]);
        Assert.Equal(GroundClass.Sand, At(r.Surface, 50, 35));
        Assert.Equal(GroundClass.Beach, At(r.Landcover, 50, 35));  // 11 m from the water
        Assert.Equal(GroundClass.Beach, At(r.Landcover, 50, 43));  // 19 m
        Assert.Equal(GroundClass.Sand, At(r.Landcover, 50, 47));   // 23 m
        Assert.Equal(GroundClass.Grass, At(r.Landcover, 50, 56));
        Assert.Equal(GroundClass.Urban, At(r.Landcover, 23, 15));
        Assert.Equal(GroundClass.WaterMaterial, At(r.Surface, 4, 55));
        Assert.Null(r.Canopy);
    }

    [Fact]
    public void GroundOfTheDefaultMaterialIsAClassOfItsOwn()
    {
        // grass with a slope of the DEFAULT material (terrain the game gives no material of its own), a small patch of
        // it and a paved yard: the large piece keeps its class, the small one takes the grass around it, paving is urban
        var s = new Synth("z8_60_132", 60)
            .Paint(5, 5, 20, 20, mat: "DEFAULT")
            .Paint(40, 10, 5, 5, mat: "DEFAULT")                   // 25 m², under the smallest piece kept
            .Paint(35, 35, 15, 15, mat: "CONCRETE");
        var r = Build(s);
        Assert.Equal(GroundClass.DefaultMaterial, At(r.Surface, 15, 15));
        Assert.Equal(GroundClass.DefaultMaterial, At(r.Landcover, 15, 15));
        Assert.Equal(GroundClass.DefaultMaterial, At(r.Surface, 42, 12));
        Assert.Equal(GroundClass.Grass, At(r.Landcover, 42, 12));
        Assert.Equal(GroundClass.Urban, At(r.Landcover, 42, 42));
        Assert.False(r.Buildings[15, 15]);                         // level with the ground: no building
    }

    [Fact]
    public void AFiveByFiveVoteKeepsTheLowerClassOnATie()
    {
        var g = new Grid<byte>(5, 5);
        Array.Fill(g.Data, (byte)2);
        g[2, 2] = 3;
        Assert.All(LandcoverBuilder.Majority(g, 5, 10).Data, v => Assert.Equal(2, v));
        // one row, mirrored at the edges: the middle cell (class 1) sees two cells of 2 and two of 3
        var row = new Grid<byte>(5, 1, [2, 2, 1, 3, 3]);
        Assert.Equal(2, LandcoverBuilder.Majority(row, 5, 10)[0, 2]);
        // no class: 0 only where nothing else is within the window
        var dot = new Grid<byte>(9, 1, [0, 0, 0, 0, 0, 0, 0, 0, 5]);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 5, 5, 5 }, LandcoverBuilder.Majority(dot, 5, 10).Data);
    }

    [Fact]
    public void SmallPiecesTakeTheCommonestClassAroundThem()
    {
        var g = new Grid<byte>(12, 8);
        Array.Fill(g.Data, (byte)2);
        for (int r = 0; r < 8; r++) for (int c = 6; c < 12; c++) g[r, c] = 3;
        g[3, 2] = 5; g[3, 3] = 5;                                  // inside the 2s
        g[6, 5] = 7;                                               // on the border: 2s and 3s around, more 2s
        var o = LandcoverBuilder.MergeSmall(g, 4, 10);
        Assert.Equal(2, o[3, 2]);
        Assert.Equal(2, o[3, 3]);
        Assert.Equal(2, o[6, 5]);
        Assert.Equal(3, o[0, 11]);                                 // big pieces stay
    }

    // ------------------------------------------------------------------ buildings

    [Fact]
    public void BuildingsStandAboveTheTerrainButNotRoadsRocksOrPlanters()
    {
        var s = new Synth("z8_60_132", 60, z: 10)
            .Paint(5, 5, 12, 12, mat: "CONCRETE", z: 25)                    // a building
            .Paint(5, 30, 12, 12, mat: "TARMAC", z: 25, onRoad: true)       // a raised road deck
            .Paint(30, 5, 8, 8, mat: "ROCK", z: 18)                         // a boulder
            .Paint(30, 30, 16, 16, mat: "CONCRETE", z: 30)                  // a roof with a planter of grass
            .Paint(36, 36, 4, 4, mat: "GRASS", z: 30.3f);
        var r = Build(s);
        Assert.True(r.Buildings[10, 10]);
        Assert.False(r.Buildings[10, 35]);
        Assert.False(r.Buildings[33, 8]);
        Assert.True(r.Buildings[37, 37]);                                   // the planter is part of the roof
        Assert.True(r.Buildings[32, 32]);
        Assert.Equal(20, r.HeightAboveTerrain[32, 32]);                     // 30 m on ground at 10 m
        Assert.Equal(0, r.HeightAboveTerrain[55, 55]);
        Assert.False(r.Buildings[55, 55]);
        Assert.Equal(0, r.GarageCells);
    }

    [Fact]
    public void ASingleGroundSampleOnAnEaveDoesNotMakeTheRoofGround()
    {
        // a roof 4 m up, joined to the terrain by an eave 2 m up on one side (steps under the wall rise); a grass sample
        // on the roof by the eave: one cell does not carry the ground onto the roof, a group of two does
        Synth Roof() => new Synth("z8_60_132", 60, z: 10)
            .Paint(20, 20, 12, 12, mat: "CONCRETE", z: 14)
            .Paint(32, 20, 1, 12, mat: "CONCRETE", z: 12);
        var one = Build(Roof().Paint(31, 25, 1, 1, mat: "GRASS"));
        Assert.True(one.Buildings[25, 25]);
        Assert.Equal(4, one.HeightAboveTerrain[25, 25]);
        var two = Build(Roof().Paint(31, 25, 1, 2, mat: "GRASS"));
        Assert.False(two.Buildings[25, 25]);
        Assert.Equal(0, two.HeightAboveTerrain[25, 25]);
    }

    [Fact]
    public void ARaisedTarmacDeckIsAParkingStructureUnlessUnderAStreetOrOnWater()
    {
        Synth Deck(float ground = 0) => new Synth("z8_60_132", 60, z: ground).Paint(10, 10, 20, 20, mat: "TARMAC", z: 8);
        var plain = Build(Deck());
        Assert.True(plain.Buildings[20, 20]);
        Assert.Equal(400, plain.GarageCells);

        // a street 12 m wide across the deck (its line drawn in the world's coordinates)
        var s = Deck();
        var (x0, y0) = s.Corner;
        var street = new RoadGraphFile.Road(RoadClass.Street, 12, false, 0, false, 80, 1, 1, [new P2(x0 - 10, y0 - 20), new P2(x0 + 70, y0 - 20)]);
        var viaduct = Build(s, GarageRule.Roads.Of([street]));
        Assert.False(viaduct.Buildings[20, 20]);
        var track = street with { Class = RoadClass.Track };            // other classes do not count
        Assert.True(Build(s, GarageRule.Roads.Of([track])).Buildings[20, 20]);

        // a deck in open water: a ship
        var ship = Deck(ground: -5);
        ship.Paint(0, 0, 60, 60, water: 0).Paint(10, 10, 20, 20, water: float.NaN);
        Assert.False(Build(ship).Buildings[20, 20]);
    }

    [Fact]
    public void ARockFaceAmongWildGroundIsNotABuildingButOneWithWallsOrAmongPavingIs()
    {
        // a mound of the default material (a rock prop without a material name) rising 1 m per cell from the ground at
        // 10 m: steeper than the walk, so not ground, and more than 3 m above the terrain from its fourth step on
        Synth Mound(string ground)
        {
            var s = new Synth("z8_60_132", 60, z: 10, ground: ground);
            for (int k = 0; k < 15; k++) s.Paint(15 + k, 15 + k, 30 - 2 * k, 30 - 2 * k, mat: "DEFAULT", z: 11 + k);
            return s;
        }
        var wild = Build(Mound("GRASS"));
        Assert.True(wild.HeightAboveTerrain[30, 30] > LandcoverBuilder.BuildingHeight);
        Assert.False(wild.Buildings[30, 30]);
        Assert.Equal(24 * 24, wild.RockCells);                      // the steps above 13 m, taken off whole
        var paved = Build(Mound("TARMAC"));                          // paving around it
        Assert.True(paved.Buildings[30, 30]);
        Assert.Equal(0, paved.RockCells);
        var box = Build(new Synth("z8_60_132", 60, z: 10).Paint(15, 15, 30, 30, mat: "DEFAULT", z: 25));   // walls all round
        Assert.True(box.Buildings[30, 30]);
        Assert.Equal(0, box.RockCells);
    }

    [Fact]
    public void ARaisedDeckWithGroundAtItsLevelOrWaterNextToItIsNotAParkingStructure()
    {
        Synth Deck() => new Synth("z8_60_132", 60, z: 0).Paint(10, 10, 20, 20, mat: "TARMAC", z: 8);
        // a slope of grass 2 m below the deck beside it (a quay's land side, a road on a hill)
        var hill = Build(Deck().Paint(0, 30, 60, 30, z: 6));
        Assert.False(hill.Buildings[20, 20]);
        Assert.InRange(hill.GarageOffCells, 1, 400);                 // all the parking rule took (the hill lifts its reference at the deck's edge)
        Assert.Equal(0, hill.GarageCells);
        // open water along one side, under half of the parking rule's own ring (a pier)
        var pier = Build(Deck().Paint(30, 0, 30, 60, z: -5, water: 0));
        Assert.False(pier.Buildings[20, 20]);
        Assert.Equal(400, pier.GarageOffCells);
        // the deck alone (ground far below all round) stays
        Assert.Equal(400, Build(Deck()).GarageCells);
    }

    [Fact]
    public void CanopyComesFromTheCanopyProbeWellAboveTheGround()
    {
        var s = new Synth("z8_60_132", 40, z: 10);
        s.Canopy = Enumerable.Repeat(float.NaN, 40 * 40).ToArray();
        for (int r = 10; r < 14; r++) for (int c = 10; c < 14; c++) s.Canopy[r * 40 + c] = 16;    // a crown 6 m up
        s.Canopy[30 * 40 + 30] = 11;                                                             // a bush 1 m up
        var r0 = Build(s);
        Assert.NotNull(r0.Canopy);
        Assert.True(r0.Canopy![11, 11]);
        Assert.True(r0.Canopy[8, 11]);                  // grown by 2 m
        Assert.False(r0.Canopy[30, 30]);
    }

    // ------------------------------------------------------------------ the file and the stage

    [Fact]
    public void TheFileHoldsTheLayersAndTheClassNames()
    {
        using var tmp = new TempFolder();
        var s = new Synth("z8_60_132", 30).Paint(0, 0, 30, 10, water: 12);
        var surface = s.Surface();
        var r = LandcoverBuilder.Compute(surface, new Dictionary<(int, int), BlockSurface>(), NoRoads, M);
        var path = LandcoverFile.PathOf(tmp.Path, s.Id);
        LandcoverFile.Write(path, surface, r);
        var f = GridFile.Load(path);
        Assert.Equal(new[] { "landcover", "surface", "water", "buildings", "heightAboveTerrain" }, f.Names.ToArray());
        Assert.Equal("z8_60_132", (string)f.Meta["block"]!);
        Assert.Equal(s.Corner.X0, (double)f.Meta["x0"]!);
        Assert.Equal(GroundClasses.Names, f.Meta["classes"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(r.Water.Data, f.Get<bool>("water").Data);
        Assert.Equal(GroundClass.Grass, (GroundClass)f.Get<byte>("landcover")[15, 25]);
    }

    static Project StageProject(TempFolder tmp, params string[] blocks)
    {
        var project = Project.Create(tmp.File("t.fxmapgen.json"));
        project.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
        project.File.Range = new RangeSetting { Base = "none", Add = blocks.ToList() };
        project.Save();
        return project;
    }

    /// <summary>The road graph as its stage leaves it (no roads), made at <paramref name="at"/>.</summary>
    static void RoadGraphDone(Project project, StateStore state, DateTime at)
    {
        var folder = new WorkFolder(project.WorkFolderPath);
        Directory.CreateDirectory(folder.Data);
        File.WriteAllText(Path.Combine(folder.Data, RoadGraphFile.Roads), "{\"roads\":[]}");
        File.WriteAllText(Path.Combine(folder.Data, RoadGraphStage.Record), JsonSerializer.Serialize(
            new RoadGraphStage.RoadsRecord(project.Range.Keys.Select(b => b.Name).ToList(), 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0), Project.Json));
        state.SetStageDone(StageKeys.RoadGraph, StageKeys.World, at);
    }

    [Fact]
    public async Task TheStageWaitsForNeighboursAndMakesABlockAgainWhenAnInputIsNewer()
    {
        using var tmp = new TempFolder();
        var project = StageProject(tmp, "z8_60_132", "z8_64_132", "z8_68_132");
        var folder = new WorkFolder(project.WorkFolderPath);
        var state = StateStore.Open(folder);
        Directory.CreateDirectory(folder.Capture);
        Directory.CreateDirectory(folder.Scan);
        foreach (var name in new[] { "z8_60_132", "z8_64_132" })
        {
            var s = new Synth(name, 40).Paint(5, 5, 10, 10, mat: "CONCRETE", z: 30);
            s.WriteHeights(folder.CaptureHeights(s.Id));
            s.WriteScan(folder.ScanFile(s.Id));
            state.SetItems(new[] { BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads }.Select(i => (s.Id, i, T0)));
        }
        RoadGraphDone(project, state, T0.AddMinutes(1));
        var a = BlockId.Parse("z8_60_132");
        var (ready, waiting) = LandcoverStage.Left(project, state);
        Assert.Equal(new[] { a }, ready);                                            // its neighbour in the range has its data
        Assert.Equal(new[] { "z8_64_132", "z8_68_132" }, waiting.Select(b => b.Name));  // one waits for a neighbour, one for itself
        Assert.Equal(3, Planner.Build(project, state, 1, 1).Rows.Single(r => r.Id == "mapData").Children.Single(c => c.Id == "mapData.landcover").Remaining);

        var end = await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 2, Processors = 2, Memory = new FakeMemory(), State = state,
            Stages = (_, _) => new BuildPlan([new LandcoverStage()], []),
        }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        var f = GridFile.Load(LandcoverFile.PathOf(folder.Data, a));
        Assert.True(f.Get<bool>("buildings")[10, 10]);
        Assert.False(File.Exists(LandcoverFile.PathOf(folder.Data, BlockId.Parse("z8_64_132"))));
        var done = state.StageDone(StageKeys.Landcover, a.Name)!.Value;
        var roadGraph = state.StageDone(StageKeys.RoadGraph, StageKeys.World);
        Assert.False(LandcoverStage.IsStale(folder, state, a, roadGraph));

        state.SetItem(BlockId.Parse("z8_64_132"), BlockItem.ScanGround, done.AddMinutes(1));
        Assert.True(LandcoverStage.IsStale(folder, state, a, roadGraph));           // a neighbour's newer ground scan
        state.SetStageDone(StageKeys.Landcover, a.Name, done.AddMinutes(2));
        Assert.False(LandcoverStage.IsStale(folder, state, a, roadGraph));
        Assert.True(LandcoverStage.IsStale(folder, state, a, done.AddMinutes(3)));   // a newer road graph
        state.SetItem(a, BlockItem.ScanRoads, done.AddMinutes(4));
        Assert.True(LandcoverStage.IsStale(folder, state, a, roadGraph));           // its own newer road scan
        state.SetStageDone(StageKeys.Landcover, a.Name, done.AddMinutes(5));
        File.Delete(LandcoverFile.PathOf(folder.Data, a));
        Assert.True(LandcoverStage.IsStale(folder, state, a, roadGraph));           // the file is gone
    }

    [Fact]
    public async Task WithQualityTheLandcoverTakesTheHigherOfTheScanAndTheHeightData()
    {
        using var tmp = new TempFolder();
        var project = StageProject(tmp, "z8_60_132");
        project.File.Maps = new MapsSetting { Satellite = true, Roadmap = true };     // quality: the default for these maps
        project.Save();
        Assert.Equal(new[] { BlockItem.Height, BlockItem.ScanGround }, SurfaceHeights.LandcoverItems(project));
        var folder = new WorkFolder(project.WorkFolderPath);
        var state = StateStore.Open(folder);
        Directory.CreateDirectory(folder.Capture);
        Directory.CreateDirectory(folder.Scan);
        // a concrete yard the scan hits at the ground's height; the height data has a container on it, 6 m up
        var scan = new Synth("z8_60_132", 40).Paint(10, 10, 12, 12, mat: "CONCRETE");
        scan.WriteScan(folder.ScanFile(scan.Id));
        new Synth("z8_60_132", 40).Paint(10, 10, 12, 12, z: 16).WriteHeights(folder.CaptureHeights(scan.Id));
        state.SetItems(new[] { BlockItem.Shot, BlockItem.ScanGround, BlockItem.ScanRoads }.Select(i => (scan.Id, i, T0)));
        RoadGraphDone(project, state, T0.AddMinutes(1));
        Assert.Equal(new[] { scan.Id }, LandcoverStage.Left(project, state).Waiting);     // no height data yet
        state.SetItem(scan.Id, BlockItem.Height, T0);
        Assert.Equal(new[] { scan.Id }, LandcoverStage.Left(project, state).Ready);

        var end = await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = state,
            Stages = (_, _) => new BuildPlan([new LandcoverStage()], []),
        }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        var f = GridFile.Load(LandcoverFile.PathOf(folder.Data, scan.Id));
        Assert.True(f.Get<bool>("buildings")[15, 15]);
        Assert.Equal(6, f.Get<float>("heightAboveTerrain")[15, 15]);
        Assert.Equal("both", LandcoverStage.ReadRecord(folder)!.Heights);
        Assert.Empty(LandcoverStage.Left(project, state).Ready);

        // the ground scan alone from now on (balance): made again, though no input is newer
        project.File.HeightQuality = SurfaceHeights.Balance;
        project.Save();
        Assert.Equal(new[] { scan.Id }, LandcoverStage.Left(project, state).Ready);
        end = await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = state,
            Stages = (_, _) => new BuildPlan([new LandcoverStage()], []),
        }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        f = GridFile.Load(LandcoverFile.PathOf(folder.Data, scan.Id));
        Assert.False(f.Get<bool>("buildings")[15, 15]);
        Assert.Equal("scan", LandcoverStage.ReadRecord(folder)!.Heights);
        Assert.Empty(LandcoverStage.Left(project, state).Ready);
    }

    [Fact]
    public async Task ARoadGraphMadeAgainWithTheSameContentsMakesNoBlockAgain()
    {
        using var tmp = new TempFolder();
        var project = StageProject(tmp, "z8_60_132");
        var folder = new WorkFolder(project.WorkFolderPath);
        var state = StateStore.Open(folder);
        Directory.CreateDirectory(folder.Capture);
        Directory.CreateDirectory(folder.Scan);
        var s = new Synth("z8_60_132", 40).Paint(5, 5, 10, 10, mat: "CONCRETE", z: 30);
        s.WriteHeights(folder.CaptureHeights(s.Id));
        s.WriteScan(folder.ScanFile(s.Id));
        state.SetItems(new[] { BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads }.Select(i => (s.Id, i, T0)));
        RoadGraphDone(project, state, T0.AddMinutes(1));
        async Task Run() => Assert.Equal(JobState.Done, (await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = state,
            Stages = (_, _) => new BuildPlan([new LandcoverStage()], []),
        }).RunAsync()).State);
        await Run();
        Assert.Equal(LandcoverStage.RoadGraphDigest(folder), LandcoverStage.ReadRecord(folder)!.RoadGraph);
        var done = state.StageDone(StageKeys.Landcover, s.Id.Name)!.Value;

        // the road graph made again with the same contents: newer, but no block is left
        RoadGraphDone(project, state, done.AddMinutes(1));
        Assert.Empty(LandcoverStage.Left(project, state).Ready);
        await Run();
        Assert.Equal(done, state.StageDone(StageKeys.Landcover, s.Id.Name));

        // other contents: every block again, and the record follows
        File.WriteAllText(Path.Combine(folder.Data, RoadGraphFile.Roads), "{\"roads\": []}");
        state.SetStageDone(StageKeys.RoadGraph, StageKeys.World, done.AddMinutes(2));
        Assert.Equal(new[] { s.Id }, LandcoverStage.Left(project, state).Ready);
        await Run();
        var again = state.StageDone(StageKeys.Landcover, s.Id.Name)!.Value;
        Assert.True(again > done);
        Assert.Equal(LandcoverStage.RoadGraphDigest(folder), LandcoverStage.ReadRecord(folder)!.RoadGraph);

        // a record without the digest (made before it was kept): a road graph newer than a block counts as another one
        LandcoverStage.WriteRecord(folder, LandcoverStage.ReadRecord(folder)!.Heights);
        state.SetStageDone(StageKeys.RoadGraph, StageKeys.World, again.AddSeconds(-1));
        Assert.Empty(LandcoverStage.Left(project, state).Ready);
        state.SetStageDone(StageKeys.RoadGraph, StageKeys.World, again.AddMinutes(1));
        Assert.Equal(new[] { s.Id }, LandcoverStage.Left(project, state).Ready);
    }

    [Fact]
    public void AllBlocksAreLeftWhileTheRoadGraphIsToBeMade()
    {
        using var tmp = new TempFolder();
        var project = StageProject(tmp, "z8_60_132", "z8_64_132");
        var state = StateStore.Open(new WorkFolder(project.WorkFolderPath));
        state.SetItems(project.Range.Keys.SelectMany(b => new[] { BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads }.Select(i => (b, i, T0))));
        Row(project, state, out var row);
        Assert.Equal(2, row.Remaining);                   // the road graph was never made: everything after it is left
        Assert.Equal(2, new LandcoverStage().CountReady(project, state));
    }

    static void Row(Project project, StateStore state, out TodoRow row) =>
        row = Planner.Build(project, state, 1, 1).Rows.Single(r => r.Id == "mapData").Children.Single(c => c.Id == "mapData.landcover");
}
