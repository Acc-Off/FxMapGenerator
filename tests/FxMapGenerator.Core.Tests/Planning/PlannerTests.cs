using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Planning;

public sealed class PlannerTests
{
    static readonly DateTime T0 = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    sealed class Setup : IDisposable
    {
        readonly TempFolder _tmp = new();
        public Project Project { get; }
        public StateStore State { get; }
        public IReadOnlyList<BlockId> Blocks => Project.Range.Keys.ToList();

        public Setup(params string[] maps)
        {
            Project = Project.Create(_tmp.File("t.fxmapgen.json"));
            Project.File.Maps = new MapsSetting { Satellite = false };
            foreach (var id in maps)
            {
                var m = MapSet.Parse(id);
                if (m.Kind == MapKind.Satellite) Project.File.Maps.Satellite = true;
                else if (m.Kind == MapKind.Roadmap) Project.File.Maps.Roadmap = true;
                else
                {
                    // every style in every language (English always)
                    var atlas = Project.File.Maps.Atlas;
                    atlas.Enabled = true;
                    if (!atlas.Styles.Contains(m.Preset!)) atlas.Styles.Add(m.Preset!);
                    if (!atlas.Languages.Contains(m.Language!)) atlas.Languages.Add(m.Language!);
                }
            }
            State = StateStore.Open(new WorkFolder(Project.WorkFolderPath));
        }

        public void Take(DateTime at, params BlockItem[] items) => State.SetItems(Blocks.SelectMany(b => items.Select(i => (b, i, at))));

        /// <summary>The game files as the game-files stage leaves them (their contents do not matter to the planner).</summary>
        public void GameFilesWritten()
        {
            var game = new WorkFolder(Project.WorkFolderPath).Game;
            Directory.CreateDirectory(game);
            foreach (var f in new[] { GameFilesOutput.Paths, GameFilesOutput.Names, GameFilesOutput.Record })
                File.WriteAllText(Path.Combine(game, f), "{}");
        }

        /// <summary>The road graph's files, built from the range.</summary>
        public void RoadGraphWritten()
        {
            var data = new WorkFolder(Project.WorkFolderPath).Data;
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, RoadGraphFile.Roads), "{}");
            File.WriteAllText(Path.Combine(data, RoadGraphStage.Record), System.Text.Json.JsonSerializer.Serialize(
                new RoadGraphStage.RoadsRecord(Blocks.Select(b => b.Name).ToList(), 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0), Project.Json));
        }

        /// <summary>The road shapes' files, made from the range (their contents do not matter to the planner).</summary>
        public void RoadShapesWritten()
        {
            var data = new WorkFolder(Project.WorkFolderPath).Data;
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, RoadShapesFile.FileName), "");
            File.WriteAllText(Path.Combine(data, RoadsStage.Record), System.Text.Json.JsonSerializer.Serialize(
                new RoadsStage.ShapesRecord(Blocks.Select(b => b.Name).ToList(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0, 0, 0), Project.Json));
        }

        /// <summary>The zone grid and the regions record, made from the range without a region field (their contents do not matter to the planner).</summary>
        public void RegionsWritten()
        {
            var data = new WorkFolder(Project.WorkFolderPath).Data;
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, FxMapGenerator.Core.Regions.ZoneGrid.FileName), "");
            File.WriteAllText(Path.Combine(data, FxMapGenerator.Core.Regions.RegionsStage.Record), System.Text.Json.JsonSerializer.Serialize(
                new FxMapGenerator.Core.Regions.RegionsStage.RegionsRecord(Blocks.Select(b => b.Name).ToList(), 0, 0), Project.Json));
        }

        /// <summary>Every cell's data files and record, made for the project's styles with <paramref name="heights"/> (default: the
        /// landcover's; their contents do not matter to the planner); <paramref name="inputs"/>: the record holds the digests of
        /// the blocks' files at hand.</summary>
        public void CellDataWritten(string? heights = null, bool inputs = false)
        {
            heights ??= SurfaceHeights.NameOf(SurfaceHeights.LandcoverItems(Project));
            var folder = new WorkFolder(Project.WorkFolderPath);
            var (sets, shades, grounds) = FxMapGenerator.Core.Cells.CellPrepStage.Needs(FxMapGenerator.Core.Cells.CellPrepStage.Styles(Project));
            foreach (var cell in CellPlan.For(Blocks))
            {
                var dir = FxMapGenerator.Core.Cells.CellFiles.Folder(folder, cell.Id);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, FxMapGenerator.Core.Cells.CellFiles.Layers), "{}");
                foreach (var shade in shades) File.WriteAllText(Path.Combine(dir, FxMapGenerator.Core.Cells.CellFiles.ShadeName(shade)), "");
                foreach (var id in grounds.Keys) File.WriteAllText(Path.Combine(dir, FxMapGenerator.Core.Cells.CellFiles.GroundName(id)), "");
                var read = inputs ? cell.All.ToDictionary(b => b.Name, b => FxMapGenerator.Core.Cells.CellPrepStage.InputsOf(folder, b, SurfaceHeights.LandcoverItems(Project))) : null;
                FxMapGenerator.Core.Cells.CellFiles.WriteRecord(Path.Combine(dir, FxMapGenerator.Core.Cells.CellFiles.Record),
                    new FxMapGenerator.Core.Cells.CellRecord(cell.All.Select(b => b.Name).ToList(), cell.All.ToDictionary(b => b.Name, _ => heights), sets, shades, grounds, 0, 0, 0, read));
            }
        }

        /// <summary>The labels of a language made (a postal code file as the project's source, its copy and record, the files and the record).</summary>
        public void LabelsWritten(string language, DateTime at)
        {
            var folder = new WorkFolder(Project.WorkFolderPath);
            Directory.CreateDirectory(folder.Data);
            var src = Path.Combine(Project.Folder, "postals.json");
            File.WriteAllText(src, "[]");
            Project.File.Postals = "postals.json";
            File.WriteAllText(Path.Combine(folder.Data, FxMapGenerator.Core.Labels.PostalCodes.FileName), "[]");
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("[]")));
            File.WriteAllText(Path.Combine(folder.Data, FxMapGenerator.Core.Labels.LabelsStage.PostalsRecord), System.Text.Json.JsonSerializer.Serialize(
                new FxMapGenerator.Core.Labels.LabelsStage.PostalsCopy(Project.ResolvePath("postals.json"), sha, 0, at), Project.Json));
            var files = FxMapGenerator.Core.Labels.LabelsStage.Sections(Project, language).Select(sec =>
            {
                File.WriteAllText(Path.Combine(folder.Data, FxMapGenerator.Core.Labels.LabelsFile.FileName(language, sec.Key)), "{}");
                return new FxMapGenerator.Core.Labels.LabelsStage.LabelsKey(sec.Key, sec.Maps, 0, 0, 0, 0);
            }).ToList();
            File.WriteAllText(FxMapGenerator.Core.Labels.LabelsStage.RecordPath(folder, language), System.Text.Json.JsonSerializer.Serialize(
                new FxMapGenerator.Core.Labels.LabelsStage.LabelsRecord(language, files, sha, FxMapGenerator.Core.Labels.LabelsStage.PoiSha(Project), [], 0, CayoPerico: Project.File.CayoPerico), Project.Json));
            State.SetStageDone(StageKeys.Labels, language, at);
        }

        /// <summary>A landcover file for every block of the range (their contents do not matter to the planner).</summary>
        public void LandcoverWritten()
        {
            var data = new WorkFolder(Project.WorkFolderPath).Data;
            Directory.CreateDirectory(Path.Combine(data, LandcoverFile.Folder));
            foreach (var b in Blocks) File.WriteAllText(LandcoverFile.PathOf(data, b), "");
            LandcoverStage.WriteRecord(new WorkFolder(Project.WorkFolderPath), SurfaceHeights.NameOf(SurfaceHeights.LandcoverItems(Project)));
        }

        /// <summary>The records of every cell of a map drawn from the data at hand (the map's style record and each cell's).</summary>
        public void CellsDrawn(MapSet map)
        {
            var folder = new WorkFolder(Project.WorkFolderPath);
            var pois = FxMapGenerator.Core.Render.CellDrawStage.Current(Project, map).Pois!;
            foreach (var c in CellPlan.For(Blocks))
                FxMapGenerator.Core.Render.CellDrawStage.WriteCellRecord(folder, map, Project.StyleOf(map), c, null, [], pois);
            FxMapGenerator.Core.Render.CellDrawStage.WriteRecord(Project, map);
        }

        public TodoTable Plan(ParallelLevel level = ParallelLevel.Normal) => Planner.Build(Project, State, level.Workers(24), 24);

        public void Dispose() => _tmp.Dispose();
    }

    static TodoRow Row(TodoTable t, string id) => t.Rows.SelectMany(r => r.Children.Prepend(r)).Single(r => r.Id == id);

    static double Mid(TodoRow r) => (r.Low + r.High) / 2;

    [Fact]
    public void SatelliteOnlyFromScratch()
    {
        using var s = new Setup("satellite");
        var t = s.Plan();
        var visit = Row(t, "visit");
        Assert.Equal(1045, visit.Remaining);
        Assert.Equal((674, 371), (Row(t, "visit.shot").Land, Row(t, "visit.shot").Water));
        Assert.Equal(1045, Row(t, "visit.height").Remaining);
        foreach (var id in new[] { "visit.scanGround", "visit.scanRoads", "visit.scanCanopy", "gameFiles", "mapData", "cells", "ytd" })
            Assert.Equal("notNeeded", Row(t, id).Reason);
        // the whole-world capture took 36.5 min
        Assert.InRange(Mid(visit) / 60, 34, 46);
        Assert.Equal(1045, Row(t, "ortho").Remaining);
        Assert.True(Row(t, "precheck").Needed);
        Assert.True(Row(t, "gameSetup").Needed);                // by hand, before the pre-check; no time of its own
        Assert.Equal((true, 0.0), (Row(t, "gameSetup").ByHand, Row(t, "gameSetup").High));
        Assert.Equal(new[] { "gameSetup", "precheck", "visit" }, t.Rows.Take(3).Select(r => r.Id).ToArray());
        Assert.Equal(12, t.Workers);
    }

    [Fact]
    public void SatelliteAndAtlasFromScratch()
    {
        using var s = new Setup("satellite", "atlas-postalcodemap-en");
        var t = s.Plan();
        Assert.Equal(1045, Row(t, "visit.scanGround").Remaining);
        Assert.Equal(1045, Row(t, "visit.scanRoads").Remaining);
        Assert.Equal("notNeeded", Row(t, "visit.scanCanopy").Reason);   // preset C draws no canopy
        Assert.Equal(new[] { "satellite", "atlas-postalcodemap-en" }, Row(t, "visit.height").For);   // quality (the default): the photos, the landcover and the cells take the higher heights
        Assert.InRange(Mid(Row(t, "visit")) / 3600, 2.0, 3.0);
        // the whole map with the satellite map, atlases and the road map at quality took 7,775 s in a measured run
        Assert.InRange(7775.3, Row(t, "visit").Low, Row(t, "visit").High);
        Assert.Equal(23, Row(t, "cells").Remaining);
        Assert.Equal(1, Row(t, "mapData.labels").Remaining);
        Assert.Equal(1, Row(t, "gameFiles").Remaining);
    }

    [Fact]
    public void TheScansRightAfterTheCameraAreQuickerThanAlone()
    {
        using var alone = new Setup("roadmap");                   // no satellite map, balance: the scans alone, no camera
        alone.Project.File.HeightQuality = SurfaceHeights.Balance;
        using var after = new Setup("satellite", "roadmap");      // the shot and the height data first
        var t = Timings.Default;
        double Expected(string key) => alone.Project.Range.Values.Sum(c => t.VisitSeconds(key, c)) * t.VisitSpread.Low;
        Assert.Equal(Expected("scanGroundAlone"), Row(alone.Plan(), "visit.scanGround").Low, 6);
        Assert.Equal(Expected("scanRoadsAlone"), Row(alone.Plan(), "visit.scanRoads").Low, 6);
        Assert.Equal(Expected("scanGround"), Row(after.Plan(), "visit.scanGround").Low, 6);
        Assert.Equal(Expected("scanRoads"), Row(after.Plan(), "visit.scanRoads").Low, 6);
        Assert.True(t.VisitSeconds("scanGroundAlone", BlockClass.Land) > t.VisitSeconds("scanGround", BlockClass.Land));
        // shots already taken: the scans that are left come alone
        after.Take(T0, BlockItem.Shot, BlockItem.Height);
        Assert.Equal(Expected("scanGroundAlone"), Row(after.Plan(), "visit.scanGround").Low, 6);
        // the road map alone at quality (its default): the camera takes the height data first and the scans follow it
        using var quality = new Setup("roadmap");
        Assert.Equal(Expected("scanGround"), Row(quality.Plan(), "visit.scanGround").Low, 6);
        Assert.Equal((quality.Project.Range.Values.Sum(c => t.VisitSeconds("height", c) + t.VisitSeconds("cameraMove", c))
            + Planner.Groups(quality.Project.Range.Keys.ToHashSet()) * t.FarMove) * t.VisitSpread.Low, Row(quality.Plan(), "visit.height").Low, 6);
    }

    [Fact]
    public void TheCameraMoveCountsOncePerBlockWithItsFirstCameraItem()
    {
        using var s = new Setup("satellite", "roadmap");                 // quality: the shot, the height data and the scans
        var t = Timings.Default;
        double Sum(params string[] keys) => s.Project.Range.Values.Sum(c => keys.Sum(k => t.VisitSeconds(k, c))) * t.VisitSpread.Low;
        double far = Planner.Groups(s.Project.Range.Keys.ToHashSet()) * t.FarMove * t.VisitSpread.Low;   // the range's groups
        Assert.Equal(Sum("shot", "cameraMove") + far, Row(s.Plan(), "visit.shot").Low, 6);
        Assert.Equal(Sum("height"), Row(s.Plan(), "visit.height").Low, 6);
        // the shots taken and the height data left: the camera goes to each block for the height data
        s.Take(T0, BlockItem.Shot);
        Assert.Equal(Sum("height", "cameraMove") + far, Row(s.Plan(), "visit.height").Low, 6);
    }

    [Fact]
    public void AMoveToABlockNotNextToTheLastOneCountsOncePerGroup()
    {
        using var s = new Setup("satellite");
        s.Take(T0, BlockItem.Shot, BlockItem.Height);
        var t = Timings.Default;
        var blocks = s.Blocks.OrderBy(b => b).ToList();
        // three neighbours in a row (one group) and a block far from them (another group)
        var first = blocks.First(b => s.Project.Range.ContainsKey(new BlockId(b.Bx + 1, b.By)) && s.Project.Range.ContainsKey(new BlockId(b.Bx + 2, b.By)));
        var marked = new List<BlockId> { first, new(first.Bx + 1, first.By), new(first.Bx + 2, first.By) };
        marked.Add(blocks.Last(b => Math.Abs(b.Bx - first.Bx) > 3 || Math.Abs(b.By - first.By) > 3));
        s.State.MarkForRetake(marked);
        Assert.Equal(2, Planner.Groups(marked.ToHashSet()));
        double expected = (marked.Sum(b => t.VisitSeconds("shot", s.Project.Range[b]) + t.VisitSeconds("cameraMove", s.Project.Range[b])) + 2 * t.FarMove)
            * t.VisitSpread.Low;
        Assert.Equal(expected, Row(s.Plan(), "visit.shot").Low, 6);
        // a block at a corner is a neighbour too
        Assert.Equal(1, Planner.Groups(new HashSet<BlockId> { new(3, 3), new(4, 4) }));
        Assert.Equal(2, Planner.Groups(new HashSet<BlockId> { new(3, 3), new(5, 3) }));
    }

    [Fact]
    public void TheOrthoStepCountsTheScaleMeasurementAndLeavesTheShotsOfTheVisitToIt()
    {
        using var s = new Setup("satellite");
        var t = Timings.Default;
        double Mid(TodoRow r) => (r.Low + r.High) / 2 / ((t.StageSpread.Low + t.StageSpread.High) / 2);
        // nothing taken yet, 12 workers: the visit orthorectifies what it shoots, the step measures the scale correction
        Assert.Equal(t.ScalePerBlock * 1045 / 12, Mid(Row(s.Plan(), "ortho")), 6);
        // one worker: no helper beside the visit, every block in the step
        var one = Planner.Build(s.Project, s.State, 1, 24);
        Assert.Equal(t.ScalePerBlock * 1045 + t.OrthoPerBlock * 1045, Mid(Row(one, "ortho")), 6);
        // shots taken (a build from captures): the blocks in the step; a measured correction: no measurement
        s.Take(T0, BlockItem.Shot, BlockItem.Height);
        Assert.Equal(t.ScalePerBlock * 1045 / 12 + Planner.Lpt(Enumerable.Repeat(t.OrthoPerBlock, 1045), 12), Mid(Row(s.Plan(), "ortho")), 6);
        new ScaleStore(new WorkFolder(s.Project.WorkFolderPath)).Set(2, new ScaleStore.Entry(0.99667, 0.98868, 1892, T0));
        Assert.Equal(Planner.Lpt(Enumerable.Repeat(t.OrthoPerBlock, 1045), 12), Mid(Row(s.Plan(), "ortho")), 6);
        // the lower zooms: one worker's time over the workers
        Assert.Equal(t.LowZoom(MapKind.Satellite) / 12, Mid(Row(s.Plan(), "lowZoom.satellite")), 6);
    }

    [Fact]
    public void OtherServerResourcesAreExpectedToMakeEveryBlocksLandcoverAgain()
    {
        using var s = new Setup("atlas-postalcodemap-en");
        s.Take(T0, BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads);
        s.GameFilesWritten();
        var folder = new WorkFolder(s.Project.WorkFolderPath);
        File.WriteAllText(Path.Combine(folder.Game, GameFilesOutput.Record), System.Text.Json.JsonSerializer.Serialize(
            new FxMapGenerator.Core.GameFiles.GameFilesStage.SourcesRecord("g", "k", [], 0, [], 0, 0, 0, 0, [], [], [], new Dictionary<string, int>(), 0, 0, 0, 0, 0, 0, 0), Project.Json));
        Assert.False(FxMapGenerator.Core.GameFiles.GameFilesStage.ServerResourcesChanged(s.Project));
        s.Project.File.GameFiles.ServerResources = ["maps/tstudio_zmapdata"];
        Assert.True(FxMapGenerator.Core.GameFiles.GameFilesStage.ServerResourcesChanged(s.Project));
        Assert.Equal(1045, Row(s.Plan(), "mapData.landcover").Remaining);
    }

    [Fact]
    public void TheParallelLevelChangesOnlyTheStagesWithoutTheGame()
    {
        using var s = new Setup("satellite", "atlas-postalcodemap-en");
        var full = s.Plan(ParallelLevel.Full);
        var light = s.Plan(ParallelLevel.Light);
        Assert.Equal(Row(full, "visit").Low, Row(light, "visit").Low);
        Assert.True(Row(light, "ortho").Low > Row(full, "ortho").Low * 2);
        Assert.True(Row(light, "cells").Low > Row(full, "cells").Low);
        Assert.Equal((24, 7), (full.Workers, light.Workers));
    }

    [Fact]
    public void TakenItemsAndStageRecordsAreNotCountedAndRetakeCountsAsMissing()
    {
        using var s = new Setup("satellite");
        s.Take(T0, BlockItem.Shot, BlockItem.Height);
        var t = s.Plan();
        Assert.Equal(0, Row(t, "visit").Remaining);
        Assert.Equal("noVisit", Row(t, "precheck").Reason);
        Assert.Equal("noVisit", Row(t, "gameSetup").Reason);
        Assert.Equal(0, t.GameHigh);
        Assert.Equal(1045, Row(t, "ortho").Remaining);         // no ortho record yet

        s.State.SetStageDone(StageKeys.Ortho, s.Blocks.Select(b => b.Name), T0.AddHours(1));
        s.State.SetStageDone(StageKeys.LowZoom, "satellite", T0.AddHours(1));
        t = s.Plan();
        Assert.Equal(0, Row(t, "ortho").Remaining);
        Assert.Equal(0, Row(t, "lowZoom").Remaining);
        Assert.Equal(0, t.High);

        var retake = new[] { BlockId.Parse("z8_48_8"), BlockId.Parse("z8_52_8"), BlockId.Parse("z8_56_16") };
        s.State.MarkForRetake(retake);
        t = s.Plan();
        Assert.Equal(3, Row(t, "visit").Remaining);
        Assert.Equal(retake.Select(b => b.Name).OrderBy(n => n), Row(t, "visit.shot").Targets.OrderBy(n => n));
        Assert.Equal(3, Row(t, "ortho").Remaining);
        Assert.Equal(1, Row(t, "lowZoom.satellite").Remaining);

        // a newer shot than the ortho output also makes the block dirty
        s.State.MarkForRetake(retake, retake: false);
        s.State.SetItem(BlockId.Parse("z8_60_8"), BlockItem.Shot, T0.AddHours(2));
        t = s.Plan();
        Assert.Equal(new[] { "z8_60_8" }, Row(t, "ortho").Targets);
    }

    [Fact]
    public void AddingJapaneseLeavesOnlyItsLabelsAndDrawing()
    {
        using var s = new Setup("atlas-postalcodemap-en", "atlas-postalcodemap-ja");
        s.Take(T0, BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads);
        var done = T0.AddHours(1);
        foreach (var k in new[] { StageKeys.GameFiles, StageKeys.RoadGraph, StageKeys.Regions, StageKeys.Roads })
            s.State.SetStageDone(k, StageKeys.World, done);
        s.GameFilesWritten();
        s.RoadGraphWritten();
        s.LandcoverWritten();
        s.RoadShapesWritten();
        s.RegionsWritten();
        s.CellDataWritten();
        s.State.SetStageDone(StageKeys.Landcover, s.Blocks.Select(b => b.Name), done);
        s.LabelsWritten("en", done);
        var cells = CellPlan.For(s.Blocks);
        s.State.SetStageDone(StageKeys.CellPrep, cells.Select(c => c.Id.Name), done);
        s.State.SetStageDone(StageKeys.Cells, cells.Select(c => StageKeys.CellUnit("atlas-postalcodemap-en", c.Id)), done);
        s.CellsDrawn(MapSet.Parse("atlas-postalcodemap-en"));
        s.State.SetStageDone(StageKeys.LowZoom, "atlas-postalcodemap-en", done);
        File.WriteAllText(Path.Combine(new WorkFolder(s.Project.WorkFolderPath).Data, "sea-atlas-postalcodemap-en.txt"),
            FxMapGenerator.Core.Styles.MapStyle.Builtin("postalcodemap").OpenSeaText);

        var t = s.Plan();
        Assert.Equal(0, Row(t, "visit").Remaining);
        Assert.Equal(0, Row(t, "gameFiles").Remaining);
        Assert.Equal(new[] { "ja" }, Row(t, "mapData.labels").Targets);
        Assert.Equal(0, Row(t, "cells.prep").Remaining);
        Assert.Equal(0, Row(t, "cells.atlas-postalcodemap-en").Remaining);
        Assert.Equal(23, Row(t, "cells.atlas-postalcodemap-ja").Remaining);
        // the 23 drawings, each taking the time of its cell's land and water blocks, on 12 workers
        var cellsRow = Row(t, "cells");
        var range = s.Project.Range;
        var drawings = Planner.Lpt(cells.Select(c => c.All.Sum(b => Timings.Default.CellDraw(MapKind.Atlas, range[b]))), 12);
        Assert.Equal(drawings, Mid(cellsRow) / ((Timings.Default.StageSpread.Low + Timings.Default.StageSpread.High) / 2), 6);
        Assert.Equal(1, Row(t, "lowZoom").Remaining);
    }

    [Fact]
    public void AMapOfAProjectStyleExpectsTheCellDataButDrawsOnlyItself()
    {
        using var s = new Setup("atlas-postalcodemap-en");
        AllDone(s);
        // a style of the project with another background and light: its ground picture and shading are new in the cells'
        // data, which leaves the files the bundled map reads as they are
        FxMapGenerator.Core.Styles.ProjectStyles.Write(s.Project, new FxMapGenerator.Core.Styles.UserStyle("dusk", "Dusk", AtlasPresets.PostalCodeMap,
            new System.Text.Json.Nodes.JsonObject { ["background"] = "#102030", ["shade"] = new System.Text.Json.Nodes.JsonObject { ["altitude"] = 30 } }));
        s.Project.File.Maps.Atlas.Styles.Add("dusk");
        var t = s.Plan();
        Assert.Equal(23, Row(t, "cells.prep").Remaining);
        Assert.Equal(23, Row(t, "cells.atlas-dusk-en").Remaining);
        Assert.Equal(0, Row(t, "cells.atlas-postalcodemap-en").Remaining);
        Assert.Equal(1, Row(t, "lowZoom").Remaining);
        Assert.Equal(0, Row(t, "ytd").Remaining);
        // a layer set of its own (other sea bands) changes the layers every map reads
        FxMapGenerator.Core.Styles.ProjectStyles.Write(s.Project, new FxMapGenerator.Core.Styles.UserStyle("dusk", "Dusk", AtlasPresets.PostalCodeMap,
            new System.Text.Json.Nodes.JsonObject { ["sea"] = new System.Text.Json.Nodes.JsonObject { ["rockMinArea"] = 100 } }));
        Assert.Equal(23, Row(s.Plan(), "cells.atlas-postalcodemap-en").Remaining);
    }

    /// <summary>Every step of an atlas map with its minimap done.</summary>
    static (MapSet Map, IReadOnlyList<Cell> Cells) AllDone(Setup s)
    {
        s.Project.File.Minimap.Map = "atlas-postalcodemap-en";
        s.Take(T0, BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads);
        var done = T0.AddHours(1);
        foreach (var k in new[] { StageKeys.GameFiles, StageKeys.RoadGraph, StageKeys.Regions, StageKeys.Roads })
            s.State.SetStageDone(k, StageKeys.World, done);
        s.GameFilesWritten();
        s.RoadGraphWritten();
        s.LandcoverWritten();
        s.RoadShapesWritten();
        s.RegionsWritten();
        s.CellDataWritten();
        s.State.SetStageDone(StageKeys.Landcover, s.Blocks.Select(b => b.Name), done);
        s.LabelsWritten("en", done);
        var map = MapSet.Parse("atlas-postalcodemap-en");
        var cells = CellPlan.For(s.Blocks);
        s.State.SetStageDone(StageKeys.CellPrep, cells.Select(c => c.Id.Name), done);
        s.State.SetStageDone(StageKeys.Cells, cells.Select(c => StageKeys.CellUnit(map.Id, c.Id)), done);
        s.CellsDrawn(map);
        s.State.SetStageDone(StageKeys.LowZoom, map.Id, done);
        File.WriteAllText(Path.Combine(new WorkFolder(s.Project.WorkFolderPath).Data, "sea-atlas-postalcodemap-en.txt"),
            FxMapGenerator.Core.Styles.MapStyle.Builtin("postalcodemap").OpenSeaText);
        s.State.SetStageDone(StageKeys.Ytd, MinimapSheets.All.Select(x => StageKeys.YtdUnit(map.Id, x.Name)).Append(StageKeys.YtdUnit(map.Id, MinimapLod.Unit)),
            done.AddMinutes(1));
        MinimapStage.WriteRecord(s.Project, map.Id);
        Assert.Equal(0, s.Plan().High);
        return (map, cells);
    }

    [Fact]
    public void APartialUpdateExpectsTheCellsAndSheetsOfItsBlocks()
    {
        using var s = new Setup("atlas-postalcodemap-en");
        var (map, cells) = AllDone(s);

        // one block taken again, inside a cell: its landcover and its neighbours', the cells holding them, their sheet
        var cell = cells.First(c => c.Core.Count == 64);
        var b = cell.Core[27];
        s.State.MarkForRetake([b]);
        var t = s.Plan();
        Assert.Equal(new[] { b.Name }, Row(t, "visit").Targets);
        Assert.Equal(9, Row(t, "mapData.landcover").Remaining);
        Assert.Equal(new[] { cell.Id.Name }, Row(t, "cells.prep").Targets);
        Assert.Equal(new[] { cell.Id.Name }, Row(t, "cells." + map.Id).Targets);
        Assert.Equal(1, Row(t, "lowZoom").Remaining);
        Assert.Equal(new[] { "sheet_" + MinimapSheets.OfCell(cell.Id).Name }, Row(t, "ytd").Targets);

        // other server resources too: every block's landcover is expected (their road data change the road graph), the cells
        // still those of the block (the others whose part changes are known once the steps of the whole map ran)
        File.WriteAllText(Path.Combine(new WorkFolder(s.Project.WorkFolderPath).Game, GameFilesOutput.Record), System.Text.Json.JsonSerializer.Serialize(
            new GameFilesStage.SourcesRecord("g", "k", [], 0, [], 0, 0, 0, 0, [], [], [], new Dictionary<string, int>(), 0, 0, 0, 0, 0, 0, 0), Project.Json));
        s.Project.File.GameFiles.ServerResources = ["maps/tstudio_zmapdata"];
        t = s.Plan();
        Assert.Equal(1045, Row(t, "mapData.landcover").Remaining);
        Assert.Equal(new[] { cell.Id.Name }, Row(t, "cells.prep").Targets);
        Assert.Equal(new[] { cell.Id.Name }, Row(t, "cells." + map.Id).Targets);
        Assert.Equal(new[] { "sheet_" + MinimapSheets.OfCell(cell.Id).Name }, Row(t, "ytd").Targets);
    }

    [Fact]
    public void SavedRoadEditsExpectTheRoadStepsEveryLandcoverAndTheCellsOfTheirBlocks()
    {
        using var s = new Setup("atlas-postalcodemap-en");
        var (map, cells) = AllDone(s);
        var cell = cells.First(c => c.Core.Count == 64);
        var b = cell.Core[27];
        var (cx, cy) = b.Center;
        var edits = new FxMapGenerator.Core.RoadEdits.RoadEditSet(
            [new FxMapGenerator.Core.RoadEdits.NodeEdit("added:1", new FxMapGenerator.Core.RoadEdits.NodeValues(cx, cy, 0, 0, false, false, false, false))], []);
        FxMapGenerator.Core.RoadEdits.RoadEditsFile.Write(s.Project.ResolvePath("road-edits.json"), edits);
        s.Project.File.RoadEdits = "road-edits.json";
        var t = s.Plan();
        Assert.Equal(1, Row(t, "mapData.roadGraph").Remaining);
        Assert.Equal(1045, Row(t, "mapData.landcover").Remaining);
        Assert.Equal(1, Row(t, "mapData.roads").Remaining);
        Assert.Equal(1, Row(t, "mapData.labels").Remaining);
        Assert.Equal(new[] { cell.Id.Name }, Row(t, "cells.prep").Targets);
        Assert.Equal(new[] { cell.Id.Name }, Row(t, "cells." + map.Id).Targets);
        Assert.Equal(1, Row(t, "lowZoom").Remaining);
        Assert.Equal(new[] { "sheet_" + MinimapSheets.OfCell(cell.Id).Name }, Row(t, "ytd").Targets);
    }

    [Fact]
    public void TheCellDataIsMadeAgainWhenItsHeightsAreNotTheLandcoversAnyMore()
    {
        using var s = new Setup("satellite", "atlas-postalcodemap-en");      // quality (the default): both heights
        s.Take(T0, BlockItem.Shot, BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads);
        var cells = CellPlan.For(s.Blocks);
        s.State.SetStageDone(StageKeys.CellPrep, cells.Select(c => c.Id.Name), T0.AddHours(1));
        bool Stale(Cell c) => FxMapGenerator.Core.Cells.CellPrepStage.IsStale(s.Project, s.State, c);
        s.CellDataWritten("scan");                                           // made from the ground scan alone
        Assert.All(cells, c => Assert.True(Stale(c)));
        s.CellDataWritten();
        Assert.All(cells, c => Assert.False(Stale(c)));
        s.Project.File.HeightQuality = SurfaceHeights.Balance;              // the ground scan alone from now on
        Assert.All(cells, c => Assert.True(Stale(c)));
    }

    [Fact]
    public void ANewerInputCountsForTheCellDataOnlyWhenItsContentsChanged()
    {
        using var s = new Setup("atlas-postalcodemap-en");
        s.Take(T0, BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads);
        s.LandcoverWritten();
        s.CellDataWritten(inputs: true);
        var cells = CellPlan.For(s.Blocks);
        s.State.SetStageDone(StageKeys.CellPrep, cells.Select(c => c.Id.Name), T0.AddHours(1));
        bool Stale(Cell c) => FxMapGenerator.Core.Cells.CellPrepStage.IsStale(s.Project, s.State, c);
        var cell = cells.First(c => c.Core.Count == 64);
        var b = cell.Core[27];                                                  // inside the cell, in no other cell's margin
        var far = cells.First(c => !c.All.Contains(b));
        Assert.False(Stale(cell));
        s.State.SetStageDone(StageKeys.Landcover, b.Name, T0.AddHours(2));       // made again the same
        Assert.False(Stale(cell));
        File.WriteAllText(LandcoverFile.PathOf(new WorkFolder(s.Project.WorkFolderPath).Data, b), "other");
        Assert.True(Stale(cell));
        Assert.False(Stale(far));
        s.CellDataWritten();                                                    // a record without the digests: a newer input counts
        Assert.True(Stale(cell));
        Assert.False(Stale(far));
    }

    [Fact]
    public void RoadShapesMadeAgainDrawOnlyTheCellsWhosePartChanged()
    {
        using var s = new Setup("atlas-postalcodemap-en");
        s.Take(T0, BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads);
        var done = T0.AddHours(1);
        var map = MapSet.Parse("atlas-postalcodemap-en");
        var cells = CellPlan.For(s.Blocks);
        s.CellDataWritten();
        s.State.SetStageDone(StageKeys.CellPrep, cells.Select(c => c.Id.Name), done);
        s.LabelsWritten("en", done);
        s.State.SetStageDone(StageKeys.Cells, cells.Select(c => StageKeys.CellUnit(map.Id, c.Id)), done);
        s.CellsDrawn(map);                                                      // drawn without road shapes: their digest ""
        bool Stale(Cell c) => FxMapGenerator.Core.Render.CellDrawStage.IsStale(s.Project, s.State, map, c);
        Assert.All(cells, c => Assert.False(Stale(c)));

        // the road shapes made again: the same part for every cell but one
        var changed = cells[5];
        var data = new WorkFolder(s.Project.WorkFolderPath).Data;
        File.WriteAllText(Path.Combine(data, RoadsStage.Record), System.Text.Json.JsonSerializer.Serialize(
            new RoadsStage.ShapesRecord(s.Blocks.Select(b => b.Name).ToList(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), 0, 0, 0, 0, 0, 0,
                cells.ToDictionary(c => c.Id.Name, c => c == changed ? "other" : "")), Project.Json));
        s.State.SetStageDone(StageKeys.Roads, StageKeys.World, done.AddHours(1));
        Assert.Equal(new[] { changed.Id }, cells.Where(Stale).Select(c => c.Id));
        // the labels made again without a record of the cells' parts: every cell
        s.LabelsWritten("en", done.AddHours(2));
        Assert.All(cells, c => Assert.True(Stale(c)));
    }

    [Fact]
    public void MinimapAddsItsSheets()
    {
        using var s = new Setup("satellite");
        s.Project.File.Minimap.Map = "satellite";
        var t = s.Plan();
        Assert.Equal(MinimapSheets.All.Count + 1, Row(t, "ytd").Remaining);                 // and the small whole map
        Assert.Equal(new[] { "satellite" }, Row(t, "ytd").For);
        Assert.Equal(MinimapSheets.All.Select(x => "sheet_" + x.Name), Row(t, "ytd").Targets);
    }

    [Fact]
    public void MinimapSheetsAreLeftUntilMadeAfterTheLowerZoomsAndPerSize()
    {
        using var s = new Setup("satellite");
        s.Project.File.Minimap.Map = "satellite";
        s.Take(T0, BlockItem.Shot, BlockItem.Height);
        s.State.SetStageDone(StageKeys.Ortho, s.Blocks.Select(b => b.Name), T0.AddHours(1));
        s.State.SetStageDone(StageKeys.LowZoom, "satellite", T0.AddHours(2));
        Assert.Equal(7, Row(s.Plan(), "ytd").Remaining);                        // six sheets and the small whole map
        s.State.SetStageDone(StageKeys.Ytd, MinimapSheets.All.Take(4).Select(x => StageKeys.YtdUnit("satellite", x.Name)), T0.AddHours(3));
        MinimapStage.WriteRecord(s.Project, "satellite");
        Assert.Equal(new[] { "sheet_2_0", "sheet_2_1" }, Row(s.Plan(), "ytd").Targets);
        s.State.SetStageDone(StageKeys.LowZoom, "satellite", T0.AddHours(4));    // newer tiles: all again
        Assert.Equal(7, Row(s.Plan(), "ytd").Remaining);
        s.State.SetStageDone(StageKeys.Ytd, MinimapSheets.All.Select(x => StageKeys.YtdUnit("satellite", x.Name)).Append(StageKeys.YtdUnit("satellite", MinimapLod.Unit)), T0.AddHours(5));
        var done = Row(s.Plan(), "ytd");
        Assert.Equal((0, "done"), (done.Remaining, done.Reason));
        s.Project.File.Minimap.Outside = MinimapOutside.Transparent;          // the other outside: all again
        Assert.Equal(6, Row(s.Plan(), "ytd").Remaining);
    }

    [Fact]
    public void LongestFirstSchedule()
    {
        Assert.Equal(2, Planner.Lpt(Enumerable.Repeat(1.0, 23), 12));
        Assert.Equal(8, Planner.Lpt(new[] { 3.0, 5, 3, 3 }, 2));
        Assert.Equal(0, Planner.Lpt(Array.Empty<double>(), 4));
        Assert.Equal(6, Planner.Lpt(new[] { 1.0, 2, 3 }, 1));
    }
}
