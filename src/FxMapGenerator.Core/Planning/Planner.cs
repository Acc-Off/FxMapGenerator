using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;


namespace FxMapGenerator.Core.Planning;

/// <summary>Names of the stage records in <c>state/stages.json</c> and their units.</summary>
public static class StageKeys
{
    public const string Ortho = "ortho";            // unit: block
    public const string GameFiles = "gameFiles";    // unit: world
    public const string RoadGraph = "roadGraph";    // unit: world
    public const string Landcover = "landcover";    // unit: block
    public const string Regions = "regions";        // unit: world
    public const string Labels = "labels";          // unit: language
    public const string Roads = "roads";            // unit: world
    public const string CellPrep = "cellPrep";      // unit: cell
    public const string Cells = "cells";            // unit: <set>/<cell>
    public const string LowZoom = "lowZoom";        // unit: set
    public const string Ytd = "ytd";                // unit: <set>@<size>/<sheet>
    public const string World = "world";

    public static string CellUnit(string set, CellId cell) => $"{set}/{cell.Name}";
    public static string YtdUnit(string set, string sheet) => $"{set}@{MinimapSheets.Size}/{sheet}";
}

/// <summary>
/// Builds the to-do table: from the maps to build, the range and what the work folder already holds, which data items
/// and stages are left and how long they take. New project, resume, adding a map, a changed style and a partial
/// update are all the same calculation. A unit's output counts as done when its record is newer than its inputs.
/// </summary>
public static class Planner
{
    /// <param name="workers">Workers of the stages without the game (limited to <paramref name="processors"/>).</param>
    public static TodoTable Build(Project project, StateStore state, int workers, int processors, Timings? timings = null)
    {
        var t = timings ?? Timings.Default;
        var maps = project.Maps;
        var range = project.Range;
        var cells = CellPlan.For(range.Keys);
        var frame = project.Frame;
        double frameShare = (double)frame.BlocksX * frame.BlocksY / (WorldGrid.BlocksX * WorldGrid.BlocksY);
        workers = Math.Clamp(workers, 1, Math.Max(1, processors));
        var cellMaps = maps.Where(m => m.IsCellMap).ToList();
        bool satellite = maps.Contains(MapSet.Satellite);

        // ---- visit: the data items missing per block
        var purposes = BlockItems.All.ToDictionary(i => i, i => maps.Where(m => NeedsItem(project, m, i)).Select(m => m.Id).ToList());
        var photoItems = SurfaceHeights.PhotoItems(project);
        var missing = BlockItems.All.ToDictionary(i => i, i => purposes[i].Count == 0
            ? new List<BlockId>()
            : range.Keys.Where(b => !state.Has(b, i)).ToList());
        var visitBlocks = range.Keys.Where(b => BlockItems.All.Any(i => purposes[i].Count > 0 && !state.Has(b, i))).ToList();
        bool anyVisit = visitBlocks.Count > 0;
        bool anyItemNeeded = purposes.Values.Any(p => p.Count > 0);

        var visitChildren = new List<TodoRow>
        {
            Fixed("visit.prepare", anyItemNeeded, anyVisit ? 1 : 0, "once", new List<string>(), anyVisit ? t.Prepare : (0, 0), game: true),
        };
        // a move to a block that is not next to the last one takes longer (the new place streams in): once per group of
        // neighbouring blocks visited with the camera, with the first camera item (the scans alone were measured on
        // scattered blocks, so they hold it already)
        double farMoves = Groups(missing[BlockItem.Shot].Concat(missing[BlockItem.Height]).ToHashSet()) * t.FarMove;
        foreach (var item in BlockItems.All)
        {
            double sum = 0;
            foreach (var b in missing[item])
            {
                var cls = range[b];
                // The scans come quicker right after the camera (a shot or the height data): it has streamed the block.
                bool tile = missing[BlockItem.Shot].Contains(b) || missing[BlockItem.Height].Contains(b);
                string Scan(BlockItem i) => tile ? i.Key() : i.Key() + "Alone";
                // The canopy ray rides along with the ground scan; alone it costs a ground pass of its own.
                sum += item == BlockItem.ScanCanopy && state.Has(b, BlockItem.ScanGround)
                    ? t.VisitSeconds(Scan(BlockItem.ScanGround), cls)
                    : item is BlockItem.ScanGround or BlockItem.ScanRoads ? t.VisitSeconds(Scan(item), cls)
                    : t.VisitSeconds(item.Key(), cls);
                // the camera's move to the block counts once, with its first camera item (the shot, else the height data)
                if (item == BlockItem.Shot || (item == BlockItem.Height && !missing[BlockItem.Shot].Contains(b)))
                    sum += t.VisitSeconds("cameraMove", cls);
            }
            if (item == BlockItem.Shot ? missing[item].Count > 0 : item == BlockItem.Height && missing[BlockItem.Shot].Count == 0)
                sum += farMoves;
            visitChildren.Add(BlockRow("visit." + item.Key(), purposes[item], missing[item], range, sum, t.VisitSpread, game: true));
        }
        visitChildren.Add(Fixed("visit.cleanup", anyItemNeeded, anyVisit ? 1 : 0, "once", new List<string>(), anyVisit ? t.Cleanup : (0, 0), game: true));
        var visit = Parent("visit", anyItemNeeded, visitBlocks.Count, "block", range, visitBlocks, maps.Select(m => m.Id).ToList(), visitChildren, game: true);

        // work by hand before the visit: getting FiveM ready before it starts (the capture resource on the server, the graphics
        // settings; the screen says whether it is done), then the check once connected (its time is the estimate's)
        var gameSetup = new TodoRow("gameSetup", anyVisit, anyVisit ? null : "noVisit", anyVisit ? 1 : 0, "once", null, null,
            maps.Select(m => m.Id).ToList(), 0, 0, true, true, Array.Empty<string>(), Array.Empty<TodoRow>());
        var precheck = new TodoRow("precheck", anyVisit, anyVisit ? null : "noVisit", anyVisit ? 1 : 0, "once", null, null,
            maps.Select(m => m.Id).ToList(), anyVisit ? t.Precheck.Low : 0, anyVisit ? t.Precheck.High : 0, true, true, Array.Empty<string>(), Array.Empty<TodoRow>());

        // ---- ortho + satellite tiles (per block)
        var orthoLeft = !satellite ? new List<BlockId>() : range.Keys.Where(b => OrthoStage.Stale(state, b, photoItems)).ToList();
        // without a measured scale correction yet, the step first measures it from the overlap of every captured block
        // (the range's blocks once the visit took them), the pairs spread over the workers
        double scaleSecs = orthoLeft.Count > 0 && !new ScaleStore(new WorkFolder(project.WorkFolderPath)).Measured()
            ? t.ScalePerBlock * range.Count / workers : 0;
        // with more than one worker the visit orthorectifies each block it shoots right away (a helper beside it): the step
        // after it only checks the scale correction for those, so their time is the visit's
        var shotBeside = workers >= 2 ? missing[BlockItem.Shot].ToHashSet() : [];
        var ortho = BlockRow("ortho", satellite ? new List<string> { MapSet.Satellite.Id } : new List<string>(), orthoLeft, range,
            Lpt(orthoLeft.Where(b => !shotBeside.Contains(b)).Select(_ => t.OrthoPerBlock), workers) + scaleSecs, t.StageSpread, game: false);

        // ---- game files, map data
        var cellIds = cellMaps.Select(m => m.Id).ToList();
        bool cellsNeeded = cellMaps.Count > 0;
        bool gameFilesDirty = cellsNeeded && GameFilesStage.IsStale(project, state);
        // one step, shown with its two parts: the path graph (ynd and junction records) and the name tables (about half each)
        var gameFilesParts = new List<TodoRow>
        {
            Fixed("gameFiles.paths", cellsNeeded, gameFilesDirty ? 1 : 0, "world", cellIds, gameFilesDirty ? Spread(t.GameFiles / 2, t.StageSpread) : (0, 0), game: false),
            Fixed("gameFiles.names", cellsNeeded, gameFilesDirty ? 1 : 0, "world", cellIds, gameFilesDirty ? Spread(t.GameFiles / 2, t.StageSpread) : (0, 0), game: false),
        };
        var gameFiles = Parent("gameFiles", cellsNeeded, gameFilesDirty ? 1 : 0, "world", range, Array.Empty<BlockId>(), cellIds, gameFilesParts, game: false, unitLess: true);

        // the road graph reads the game files' path graph, the road edits and the road scans
        bool roadGraphDirty = cellsNeeded && (gameFilesDirty || RoadGraphStage.IsStale(project, state));
        // the landcover reads each block's and its neighbours' scans (its heights too), and the road graph: every block when the
        // road graph's contents changed, which is known once it is made again. A partial update expects its blocks and their
        // neighbours; other server resources (their road data) or other road edits are expected to change the road graph, so
        // every block.
        bool editsChanged = cellsNeeded && RoadGraphStage.EditsChanged(project);
        var landcoverLeft = !cellsNeeded ? new List<BlockId>()
            : GameFilesStage.ServerResourcesChanged(project) || editsChanged ? range.Keys.OrderBy(b => b).ToList()
            : LandcoverStage.Left(project, state) is var (ready, waiting) ? ready.Concat(waiting).OrderBy(b => b).ToList() : [];
        bool upstreamDirty = gameFilesDirty || landcoverLeft.Count > 0;
        bool regionsDirty = cellsNeeded && (upstreamDirty || Regions.RegionsStage.IsStale(project, state));
        // the road shapes read the game files' path graph and names and every block's landcover (made again after them)
        bool roadsDirty = cellsNeeded && (upstreamDirty || roadGraphDirty || RoadsStage.IsStale(project, state));
        var atlasLanguages = maps.Where(m => m.Kind == MapKind.Atlas).Select(m => m.Language!).Distinct().ToList();
        var labelsLeft = atlasLanguages.Where(l => roadGraphDirty || regionsDirty || Labels.LabelsStage.IsStale(project, state, l)).ToList();

        var mapDataChildren = new List<TodoRow>
        {
            Fixed("mapData.roadGraph", cellsNeeded, roadGraphDirty ? 1 : 0, "world", cellIds, roadGraphDirty ? Spread(t.RoadGraph, t.StageSpread) : (0, 0), game: false),
            BlockRow("mapData.landcover", cellIds, landcoverLeft, range, Lpt(landcoverLeft.Select(_ => t.LandcoverPerBlock), workers), t.StageSpread, game: false),
            Fixed("mapData.regions", cellsNeeded, regionsDirty ? 1 : 0, "world", cellIds, regionsDirty ? Spread(t.Regions, t.StageSpread) : (0, 0), game: false),
            Fixed("mapData.labels", atlasLanguages.Count > 0, labelsLeft.Count, "language",
                maps.Where(m => m.Kind == MapKind.Atlas).Select(m => m.Id).ToList(),
                Spread(Lpt(labelsLeft.Select(_ => t.LabelsPerLanguage), workers), t.StageSpread), game: false, targets: labelsLeft),
            Fixed("mapData.roads", cellsNeeded, roadsDirty ? 1 : 0, "world", cellIds, roadsDirty ? Spread(t.Roads, t.StageSpread) : (0, 0), game: false),
        };
        var mapData = Parent("mapData", cellsNeeded, mapDataChildren.Count(c => c.Remaining > 0), "step", range, Array.Empty<BlockId>(), cellIds, mapDataChildren, game: false, unitLess: true);

        // ---- cells: shared preparation (vectors, shade, ground) once per cell, then one drawing per map; both take about
        // the time of the cell's blocks (its margin blocks too), a land block and a water block each their own
        // Before the steps of the whole map run again (landcover, region colours, labels, road shapes), a cell counts when
        // its blocks' landcover is left for their own data (a retake, and the neighbours reading it) or its blocks hold road
        // edits that changed; the cells whose part of the other changes (another road graph, road shapes, labels) are known
        // once those are made (the stages compare digests).
        var landcoverDirtySet = new HashSet<BlockId>();
        if (cellsNeeded)
        {
            var (ownReady, ownWaiting) = LandcoverStage.Left(project, state, withRoadGraph: false);
            landcoverDirtySet.UnionWith(ownReady.Concat(ownWaiting));
            if (editsChanged) landcoverDirtySet.UnionWith(RoadGraphStage.EditedBlocks(project));
        }
        var prepLeft = new List<CellId>();
        var setLeft = cellMaps.ToDictionary(m => m.Id, _ => new List<CellId>());
        var cellUnits = new List<double>();
        double prepWork = 0;
        var setWork = cellMaps.ToDictionary(m => m.Id, _ => 0.0);
        var drawNow = cellMaps.ToDictionary(m => m.Id, m => Render.CellDrawStage.Current(project, m));
        var styleOf = cellMaps.ToDictionary(m => m.Id, project.StyleOf);
        var drawRecorded = cellMaps.ToDictionary(m => m.Id, m => Render.CellDrawStage.Recorded(project, m));
        foreach (var cell in cells)
        {
            if (!cellsNeeded) break;
            // the cell's data made again reaches only the maps whose files it changes (another style's shading or ground
            // picture leaves a map as it is: the drawing compares the files it reads)
            var changes = cell.All.Any(landcoverDirtySet.Contains) ? new HashSet<string> { Cells.CellPrepStage.Everything } : Cells.CellPrepStage.Changes(project, state, cell);
            bool prep = changes is not null;
            double unit = 0;
            var classes = cell.All.Select(b => range[b]).ToList();
            if (prep) { double s = classes.Sum(t.CellPrep); prepLeft.Add(cell.Id); unit += s; prepWork += s; }
            foreach (var m in cellMaps)
            {
                var drawn = state.StageDone(StageKeys.Cells, StageKeys.CellUnit(m.Id, cell.Id));
                bool dirty = (changes is not null && Cells.CellPrepStage.Reaches(changes, styleOf[m.Id])) || drawn is null
                    || Render.CellDrawStage.IsStale(project, state, m, cell, drawNow[m.Id], drawRecorded[m.Id]);
                if (!dirty) continue;
                setLeft[m.Id].Add(cell.Id);
                double d = classes.Sum(c => t.CellDraw(m.Kind, c));
                unit += d;
                setWork[m.Id] += d;
            }
            if (unit > 0) cellUnits.Add(unit);
        }
        double cellsTime = Lpt(cellUnits, workers), cellsWork = cellUnits.Sum();
        double Share(double work) => cellsWork > 0 ? cellsTime * work / cellsWork : 0;
        var cellChildren = new List<TodoRow>
        {
            CellRow("cells.prep", cellIds, prepLeft, Share(prepWork), t.StageSpread),
        };
        cellChildren.AddRange(cellMaps.Select(m => CellRow("cells." + m.Id, new List<string> { m.Id }, setLeft[m.Id], Share(setWork[m.Id]), t.StageSpread)));
        var cellTargets = prepLeft.Concat(setLeft.Values.SelectMany(v => v)).Distinct().OrderBy(c => c).Select(c => c.Name).ToList();
        var cellsRow = new TodoRow("cells", cellsNeeded, cellsNeeded ? (cellTargets.Count == 0 ? "done" : null) : "notNeeded", cellTargets.Count, "cell", null, null,
            cellIds, cellChildren.Sum(c => c.Low), cellChildren.Sum(c => c.High), false, false, cellTargets, cellChildren);

        // ---- low zooms (per map): left when the map's units are left, or one of them was made after the low zooms
        var lowZoomChildren = maps.Select(m =>
        {
            bool upstream = m.Kind == MapKind.Satellite ? orthoLeft.Count > 0 : setLeft[m.Id].Count > 0;
            var lowZoomDone = state.StageDone(StageKeys.LowZoom, m.Id);
            var newestUnit = m.Kind == MapKind.Satellite
                ? state.NewestStageDone(StageKeys.Ortho, range.Keys.Select(b => b.Name))
                : state.NewestStageDone(StageKeys.Cells, cells.Select(c => StageKeys.CellUnit(m.Id, c.Id)));
            bool dirty = upstream || lowZoomDone is null || newestUnit > lowZoomDone
                || (m.Kind != MapKind.Satellite ? Render.CellLowZoomStage.IsStale(project, state, m) : Satellite.SatelliteLowZoomStage.FrameChanged(project));
            // one worker's time (measured on the standard frame, growing with the frame's area), the step spreading its tiles over the workers
            double secs = t.LowZoom(m.Kind) * frameShare / workers;
            return Fixed("lowZoom." + m.Id, true, dirty ? 1 : 0, "set", new List<string> { m.Id }, dirty ? Spread(secs, t.StageSpread) : (0, 0), game: false);
        }).ToList();
        var lowZoom = Parent("lowZoom", maps.Count > 0, lowZoomChildren.Sum(c => c.Remaining), "set", range, Array.Empty<BlockId>(),
            maps.Select(m => m.Id).ToList(), lowZoomChildren, game: false, unitLess: true);

        // ---- minimap sheets: left when never made, or their tiles changed: those of the cells left (a sheet is 2 x 2 cells),
        // the whole satellite map's when its tiles are left, or tiles made since the sheet
        var mm = project.File.Minimap.Map;
        var sheetsOfCells = mm is null ? new HashSet<SheetId>()
            : mm == MapSet.Satellite.Id ? (orthoLeft.Count > 0 ? MinimapSheets.All.ToHashSet() : new HashSet<SheetId>())
            : setLeft.TryGetValue(mm, out var mmCells) ? mmCells.Select(MinimapSheets.OfCell).ToHashSet() : new HashSet<SheetId>();
        var sheetsLeft = mm is null ? new List<SheetId>()
            : MinimapSheets.All.Where(s => sheetsOfCells.Contains(s) || MinimapStage.IsStale(project, state, mm, s)).ToList();
        // the small whole map (a second at most) with the sheets: left when any sheet is, or its zoom 2 tiles changed
        bool lodLeft = mm is not null && (sheetsOfCells.Count > 0 || MinimapStage.IsLodStale(project, state, mm));
        // the tiles beyond the standard frame: those of the cells left, or whose tiles changed
        var cellsLeft = mm is null ? new HashSet<CellId>()
            : mm == MapSet.Satellite.Id ? (orthoLeft.Count > 0 ? MinimapExtraTiles.Cells(range.Keys).ToHashSet() : new HashSet<CellId>())
            : setLeft.TryGetValue(mm, out var mmCells2) ? mmCells2.ToHashSet() : new HashSet<CellId>();
        var extraLeft = mm is null ? new List<CellId>()
            : MinimapExtraTiles.Cells(range.Keys).Where(c => cellsLeft.Contains(c) || MinimapStage.IsExtraStale(project, state, mm, c)).ToList();
        // the sheets side by side, each with its bands on the workers left over (six sheets on 12 workers take one sheet's time)
        double ytdSecs = Lpt(sheetsLeft.Select(_ => t.YtdPerSheet), workers);
        var ytd = Fixed("ytd", mm is not null, sheetsLeft.Count + (lodLeft ? 1 : 0) + extraLeft.Count, "sheet", mm is null ? new List<string>() : new List<string> { mm },
            Spread(ytdSecs, t.StageSpread), game: false, targets: sheetsLeft.Select(s => "sheet_" + s.Name).ToList());

        var rows = new List<TodoRow> { gameSetup, precheck, visit, ortho, gameFiles, mapData, cellsRow, lowZoom, ytd };
        var needed = rows.Where(r => r.Needed).ToList();
        var game = needed.Where(r => r.UsesGame).ToList();
        return new TodoTable(rows, needed.Sum(r => r.Low), needed.Sum(r => r.High), game.Sum(r => r.Low), game.Sum(r => r.High),
            workers, processors, range.Count, range.Values.Count(c => c == BlockClass.Land), range.Values.Count(c => c == BlockClass.Water), cells.Count);
    }

    /// <summary>
    /// Which data items a map of the project needs with its height quality: the satellite map its shot and the height items
    /// its photos are placed with (<see cref="SurfaceHeights.PhotoItems"/>); an atlas or road map its scans and the height
    /// items of the landcover (<see cref="SurfaceHeights.LandcoverItems"/>: the height data too with quality).
    /// </summary>
    public static bool NeedsItem(Project project, MapSet map, BlockItem item) => item switch
    {
        BlockItem.Height or BlockItem.ScanGround when map.Kind == MapKind.Satellite => SurfaceHeights.PhotoItems(project).Contains(item),
        BlockItem.Height => SurfaceHeights.LandcoverItems(project).Contains(item),
        _ => NeedsItem(map, item),
    };

    /// <summary>Which data items a map needs at most (satellite = shot + height; atlas / road map = height + scans); <see cref="NeedsItem(Project, MapSet, BlockItem)"/> narrows it by the height quality.</summary>
    public static bool NeedsItem(MapSet map, BlockItem item) => item switch
    {
        BlockItem.Shot => map.Kind == MapKind.Satellite,
        BlockItem.Height => true,
        BlockItem.ScanGround or BlockItem.ScanRoads => map.IsCellMap,
        _ => map.NeedsCanopy,
    };

    /// <summary>
    /// Wall time of independent units on <paramref name="workers"/> workers: longest first, each onto the least busy
    /// worker. Equal units come out as whole rounds (23 cells on 12 workers = 2 rounds).
    /// </summary>
    public static double Lpt(IEnumerable<double> units, int workers)
    {
        var list = units.Where(u => u > 0).OrderByDescending(u => u).ToList();
        if (list.Count == 0) return 0;
        var load = new PriorityQueue<int, double>();
        for (int i = 0; i < Math.Max(1, workers); i++) load.Enqueue(i, 0);
        double makespan = 0;
        foreach (var u in list)
        {
            load.TryDequeue(out var w, out var busy);
            busy += u;
            makespan = Math.Max(makespan, busy);
            load.Enqueue(w, busy);
        }
        return makespan;
    }

    /// <summary>How many groups the blocks make, a block joining a group through any of its 8 neighbours.</summary>
    public static int Groups(IReadOnlySet<BlockId> blocks)
    {
        var seen = new HashSet<BlockId>();
        var todo = new Stack<BlockId>();
        int groups = 0;
        foreach (var start in blocks)
        {
            if (!seen.Add(start)) continue;
            groups++;
            todo.Push(start);
            while (todo.TryPop(out var b))
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var n = new BlockId(b.Bx + dx, b.By + dy);
                        if (blocks.Contains(n) && seen.Add(n)) todo.Push(n);
                    }
        }
        return groups;
    }


    static (double, double) Spread(double seconds, (double Low, double High) spread) => (seconds * spread.Low, seconds * spread.High);

    static TodoRow BlockRow(string id, List<string> purposes, List<BlockId> left, IReadOnlyDictionary<BlockId, BlockClass> range, double seconds, (double Low, double High) spread, bool game)
    {
        bool needed = purposes.Count > 0;
        int land = left.Count(b => range[b] == BlockClass.Land);
        return new TodoRow(id, needed, !needed ? "notNeeded" : left.Count == 0 ? "done" : null, left.Count, "block", land, left.Count - land, purposes,
            seconds * spread.Low, seconds * spread.High, game, false, left.Select(b => b.Name).ToList(), Array.Empty<TodoRow>());
    }

    static TodoRow CellRow(string id, List<string> purposes, List<CellId> left, double seconds, (double Low, double High) spread) =>
        new(id, purposes.Count > 0, purposes.Count == 0 ? "notNeeded" : left.Count == 0 ? "done" : null, left.Count, "cell", null, null, purposes,
            seconds * spread.Low, seconds * spread.High, false, false, left.Select(c => c.Name).ToList(), Array.Empty<TodoRow>());

    static TodoRow Fixed(string id, bool needed, int remaining, string unit, List<string> purposes, (double Low, double High) seconds, bool game, IReadOnlyList<string>? targets = null) =>
        new(id, needed, !needed ? "notNeeded" : remaining == 0 ? "done" : null, needed ? remaining : 0, unit, null, null, purposes,
            needed ? seconds.Low : 0, needed ? seconds.High : 0, game, false, targets ?? Array.Empty<string>(), Array.Empty<TodoRow>());

    static TodoRow Parent(string id, bool needed, int remaining, string unit, IReadOnlyDictionary<BlockId, BlockClass> range, IReadOnlyList<BlockId> blocks,
        List<string> purposes, List<TodoRow> children, bool game, bool unitLess = false)
    {
        int land = blocks.Count(b => range[b] == BlockClass.Land);
        var live = children.Where(c => c.Needed).ToList();
        return new TodoRow(id, needed, !needed ? "notNeeded" : remaining == 0 ? "done" : null, needed ? remaining : 0, unit,
            unitLess ? null : land, unitLess ? null : blocks.Count - land, purposes,
            live.Sum(c => c.Low), live.Sum(c => c.High), game, false, blocks.Select(b => b.Name).ToList(), children);
    }
}
