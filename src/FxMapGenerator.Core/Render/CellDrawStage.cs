using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Render;

/// <summary>
/// The drawing of one cell map (atlas or road map), one unit per cell: the cell's map data drawn with the map's style
/// (<see cref="CellPainter"/>), cut into the z8 tiles of the cell's own blocks (the margin blocks are drawn only so the
/// edges match), and the z7, z6 and z5 tiles above them (a cell is 8 x 8 blocks on the grid of the z5 tiles, so no z5
/// tile spans two cells). <c>data/draw-&lt;map&gt;.json</c> holds the style the cells were drawn with (another style draws
/// every cell again), <c>cells/&lt;cell&gt;/draw-&lt;map&gt;.json</c> the digests of what the cell was drawn from; a cell is drawn
/// again when its map data, or the road shapes, labels (atlas) or points of interest it takes changed.
/// </summary>
public sealed class CellDrawStage(MapSet map) : Stage
{
    public MapSet Map { get; } = map;

    public override string Id => "cells." + Map.Id;
    public override string Row => "cells." + Map.Id;
    public override string? RecordKey => StageKeys.Cells;
    public override string UnitName => "cell";
    public override long MemoryPerUnit => 1L << 30;

    Dictionary<string, Cell> _cells = new();
    MapStyle _style = null!;
    RoadShapesFile.Contents? _roads;
    List<PlacedLabel> _labels = [];
    List<ResolvedPoi> _pois = [];
    int _done;
    bool _waiting;

    static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary><c>data/draw-&lt;map&gt;.json</c>: the style the map's cells were drawn with (another style draws every cell again).</summary>
    public sealed record DrawRecord(string Style);

    /// <summary>
    /// <c>cells/&lt;cell&gt;/draw-&lt;map&gt;.json</c>: the digests of what the cell was drawn from (<see cref="CellInputs"/>): the
    /// files of its map data the map reads (<see cref="Cells.CellPrepStage.OutputFor"/>), the road shapes, labels and points of interest it takes. The cell
    /// is drawn again when one of them changed.
    /// </summary>
    public sealed record CellDrawRecord(string? Prep, string Roads, string Labels, string Pois);

    /// <summary>What a map is drawn with now: the style's record and the points of interest the map shows (null when they cannot be read).</summary>
    public sealed record DrawNow(DrawRecord Record, IReadOnlyList<ResolvedPoi>? Pois);

    static string RecordPath(WorkFolder folder, MapSet map) => Path.Combine(folder.Data, $"draw-{map.Id}.json");

    static string CellRecordPath(WorkFolder folder, MapSet map, CellId cell) => Path.Combine(CellFiles.Folder(folder, cell), $"draw-{map.Id}.json");

    /// <summary>The style the map is drawn with now, and the points of interest it shows.</summary>
    public static DrawNow Current(Project project, MapSet map)
    {
        IReadOnlyList<ResolvedPoi>? pois;
        try { pois = PoisOf(project, map); }
        catch (PoiException) { pois = null; }
        return new(new DrawRecord(Sha(DrawingText(project.StyleOf(map)))), pois);
    }

    /// <summary>The points of interest a map shows (visible, and meant for that kind of map).</summary>
    internal static List<ResolvedPoi> PoisOf(Project project, MapSet map) =>
        PoiData.Of(project).Resolve().Where(p => p.Visible && (map.Kind == MapKind.Atlas ? p.Show.Atlas : p.Show.Roadmap)).ToList();

    /// <summary>
    /// The style's text without what the drawing does not use (its name and credit, the values of the ground methods
    /// not chosen), so editing those draws nothing again.
    /// </summary>
    internal static string DrawingText(MapStyle style)
    {
        var o = (System.Text.Json.Nodes.JsonObject)style.Source.DeepClone();
        o.Remove("name");
        o.Remove("credit");
        if (o["groundRaster"] is System.Text.Json.Nodes.JsonObject g) o["groundRaster"] = GroundRasterStyle.InUse(g);
        return o.ToJsonString();
    }

    /// <summary>What the map's cells were drawn with last (null: never all of them).</summary>
    public static DrawRecord? Recorded(Project project, MapSet map) => ReadRecord(new WorkFolder(project.WorkFolderPath), map);

    static DrawRecord? ReadRecord(WorkFolder folder, MapSet map)
    {
        var p = RecordPath(folder, map);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<DrawRecord>(File.ReadAllText(p), Project.Json); }
        catch (JsonException) { return null; }
    }

    static CellDrawRecord? ReadCellRecord(WorkFolder folder, MapSet map, CellId cell)
    {
        var p = CellRecordPath(folder, map, cell);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<CellDrawRecord>(File.ReadAllText(p), Project.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// True when the cell's drawing of the map is missing, the map's style changed, or what the cell takes changed: its map
    /// data, road shapes, labels (atlas) or points of interest (<see cref="CellDrawRecord"/>; each compared only
    /// when its step ran after the drawing, a cell without that record counts as changed). Road shapes or labels made
    /// again the same around the cell draw nothing again.
    /// </summary>
    public static bool IsStale(Project project, StateStore state, MapSet map, Cell cell, DrawNow? current = null, DrawRecord? recorded = null)
    {
        var id = cell.Id;
        if (state.StageDone(StageKeys.Cells, StageKeys.CellUnit(map.Id, id)) is not { } drawn) return true;
        var folder = new WorkFolder(project.WorkFolderPath);
        var now = current ?? Current(project, map);
        if ((recorded ?? ReadRecord(folder, map)) != now.Record) return true;
        if (ReadCellRecord(folder, map, id) is not { } had) return true;
        var rect = new CellArea(cell).Rect;
        bool After(string key, string unit) => state.StageDone(key, unit) is { } t && t > drawn;
        if (After(StageKeys.CellPrep, id.Name)
            && (had.Prep is null || Cells.CellPrepStage.OutputFor(CellFiles.ReadRecord(Path.Combine(CellFiles.Folder(folder, id), CellFiles.Record)), project.StyleOf(map)) != had.Prep)) return true;
        if (After(StageKeys.Roads, StageKeys.World) && RoadsStage.ReadRecord(folder)?.Cells?.GetValueOrDefault(id.Name) != had.Roads) return true;
        if (map.Kind == MapKind.Atlas && After(StageKeys.Labels, map.Language!) && LabelsDigestNow(project, map, id) != had.Labels) return true;
        return now.Pois is null || CellInputs.PoisDigest(now.Pois, rect) != had.Pois;
    }

    /// <summary>The digest of the labels a cell of an atlas map takes, from the labels record (null when not recorded).</summary>
    static string? LabelsDigestNow(Project project, MapSet map, CellId cell)
    {
        if (project.StyleOf(map).Labels is not { } ls) return CellInputs.LabelsDigest([], (0, 0, 0, 0));
        var rec = LabelsStage.Record(project, map.Language!);
        return rec?.Files.FirstOrDefault(f => f.Key == ls.PlacementKey)?.Cells?.GetValueOrDefault(cell.Name);
    }

    /// <summary>What the drawing still waits for, or null.</summary>
    public static string? Waiting(Project project, StateStore state, MapSet map)
    {
        var folder = new WorkFolder(project.WorkFolderPath);
        if (!File.Exists(Path.Combine(folder.Data, RoadShapesFile.FileName))) return "the road shapes";
        if (map.Kind == MapKind.Atlas && project.StyleOf(map).Labels is { } ls
            && !File.Exists(Path.Combine(folder.Data, LabelsFile.FileName(map.Language!, ls.PlacementKey)))) return "the labels";
        return null;
    }

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        _waiting = Waiting(ctx.Project, ctx.State, Map) is not null;
        if (Waiting(ctx.Project, ctx.State, Map) is { } why)
        {
            ctx.Log($"{Map.Id}: waiting for {why}");
            return [];
        }
        var current = Current(ctx.Project, Map);
        var recorded = ReadRecord(ctx.Folder, Map);
        _cells = CellPlan.For(ctx.Project.Range.Keys).ToDictionary(c => c.Id.Name);
        var left = _cells.Values.Where(c => IsStale(ctx.Project, ctx.State, Map, c, current, recorded)).Select(c => StageKeys.CellUnit(Map.Id, c.Id)).ToList();
        if (left.Count == 0)
        {
            ctx.Log($"{Map.Id}: up to date");
            return [];
        }
        // a run stopped half-way must not leave the record of the old style next to cells drawn with the new one
        if (recorded is not null && recorded != current.Record) File.Delete(RecordPath(ctx.Folder, Map));
        _style = ctx.Project.StyleOf(Map);
        _roads = RoadShapesFile.Read(Path.Combine(ctx.Folder.Data, RoadShapesFile.FileName));
        _labels = Map.Kind == MapKind.Atlas && _style.Labels is { } ls
            ? LabelsFile.Read(Path.Combine(ctx.Folder.Data, LabelsFile.FileName(Map.Language!, ls.PlacementKey))).Labels
            : [];
        _pois = PoisOf(ctx.Project, Map);
        _done = 0;
        ctx.Log($"{Map.Id}: {left.Count} cells to draw");
        return left;
    }

    public override int CountReady(Project project, StateStore state)
    {
        if (Waiting(project, state, Map) is not null) return 0;
        var current = Current(project, Map);
        var recorded = ReadRecord(new WorkFolder(project.WorkFolderPath), Map);
        return CellPlan.For(project.Range.Keys).Count(c => IsStale(project, state, Map, c, current, recorded));
    }

    public override void Run(UnitContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var cell = _cells[ctx.Unit[(ctx.Unit.IndexOf('/') + 1)..]];
        var (tiles, labels) = DrawCell(ctx.Folder, Map, _style, cell, _roads, _labels, _pois, TileStore.Keeping(ctx.Folder, Map.Id), ctx.Token, ctx.Report);
        WriteCellRecord(ctx.Folder, Map, _style, cell, _roads, _labels, _pois);
        Interlocked.Increment(ref _done);
        ctx.Log($"{Map.Id} {cell.Id.Name}: {cell.Core.Count} blocks, {tiles} tiles, {labels} labels; {sw.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>
    /// Draws a cell of a map from the cell's data in <paramref name="folder"/> and writes the z8 tiles of its own blocks
    /// and the z7..z5 tiles above them into <paramref name="tiles"/>. Returns the z8 tiles written and the labels drawn.
    /// </summary>
    public static (int Tiles, int Labels) DrawCell(WorkFolder folder, MapSet map, MapStyle style, Cell cell, RoadShapesFile.Contents? roads,
        IReadOnlyList<PlacedLabel> allLabels, IReadOnlyList<ResolvedPoi> allPois, TileStore tiles,
        CancellationToken token, Action<string?, double>? report = null)
    {
        report?.Invoke("read", 0);
        using var painter = Painter(folder, map, style, cell, roads, allLabels, allPois, out var origin, out int taken);
        var written = new List<(int X, int Y)>();
        for (int i = 0; i < cell.Core.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            report?.Invoke("draw", 0.05 + 0.85 * i / cell.Core.Count);
            var b = cell.Core[i];
            var rgba = painter.DrawBlock(b.Bx - origin.Bx, b.By - origin.By);
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++)
                {
                    tiles.Write(WorldGrid.Zoom, b.Tx + k, b.Ty + j, TileStore.Crop(rgba, CellPainter.BlockPx, k * TileStore.TileSize, j * TileStore.TileSize, TileStore.TileSize, TileStore.TileSize));
                    written.Add((b.Tx + k, b.Ty + j));
                }
        }
        report?.Invoke("lower zooms", 0.9);
        var level = written;
        for (int z = WorldGrid.Zoom - 1; z >= WorldGrid.Zoom - 3; z--)
        {
            var parents = level.Select(t => (X: t.X >> 1, Y: t.Y >> 1)).Distinct().OrderBy(t => t.X).ThenBy(t => t.Y).ToList();
            foreach (var (x, y) in parents) tiles.BuildParent(z, x, y);
            level = parents;
        }
        return (written.Count, taken);
    }

    /// <summary>
    /// The painter of a cell of a map, made from the cell's data in <paramref name="folder"/> with the road shapes and
    /// the labels and points of interest the cell takes (<paramref name="taken"/>: how many of the two).
    /// <paramref name="origin"/> is the block at the north-west corner of the painter's picture.
    /// </summary>
    public static CellPainter Painter(WorkFolder folder, MapSet map, MapStyle style, Cell cell, RoadShapesFile.Contents? roads,
        IReadOnlyList<PlacedLabel> allLabels, IReadOnlyList<ResolvedPoi> allPois, out BlockId origin, out int taken)
    {
        var dir = CellFiles.Folder(folder, cell.Id);
        var (info, layers) = CellFiles.ReadLayers(Path.Combine(dir, CellFiles.Layers));
        ShadeLayer? shade = null;
        if (style.Shade is { } ss && Path.Combine(dir, CellFiles.ShadeName(Cells.CellPrepStage.ShadeValues(ss))) is var shadePath && File.Exists(shadePath))
        {
            var f = GridFile.Load(shadePath);
            shade = new ShadeLayer(f.Get<byte>("light"), f.Meta["flat"]!.GetValue<double>(), ss.Strength, CellPainter.Ppm,
                (info.GridX0 - info.Rect.X0) * CellPainter.Ppm, (info.Rect.Y0 - info.GridY0) * CellPainter.Ppm);
        }
        (byte[], int, int)? ground = null;
        if (style.GroundRaster is not null)
        {
            var rgba = Images.LoadRgba(Path.Combine(dir, CellFiles.GroundName(style.Id)), out var gw, out var gh);
            ground = (rgba, gw, gh);
        }
        var r = info.Rect;
        var labels = CellInputs.LabelsOf(allLabels, r);
        var pois = CellInputs.PoisOf(allPois, r);
        origin = new BlockId((int)Math.Round((r.X0 - WorldGrid.Left) / WorldGrid.BlockSize), (int)Math.Round((WorldGrid.Top - r.Y0) / WorldGrid.BlockSize));
        taken = labels.Count + pois.Count;
        return new CellPainter(new CellDrawInput
        {
            Info = info, Layers = layers, Style = style, Shade = shade, Ground = ground, Roads = roads,
            Labels = labels, Pois = pois, Language = map.Language ?? "en",
        });
    }

    public override void Finish(StageContext ctx, StageEnd end)
    {
        if (!end.Complete || _waiting) return;              // the record says what every cell of the map was drawn with
        WriteRecord(ctx.Project, Map);
    }

    /// <summary>Records what a cell was drawn from (<see cref="CellDrawRecord"/>), with the same selections the drawing made.</summary>
    public static void WriteCellRecord(WorkFolder folder, MapSet map, MapStyle style, Cell cell, RoadShapesFile.Contents? roads,
        IReadOnlyList<PlacedLabel> labels, IReadOnlyList<ResolvedPoi> pois)
    {
        var rect = new CellArea(cell).Rect;
        var dir = CellFiles.Folder(folder, cell.Id);
        var rec = new CellDrawRecord(Cells.CellPrepStage.OutputFor(CellFiles.ReadRecord(Path.Combine(dir, CellFiles.Record)), style),
            roads is null ? "" : CellInputs.RoadsDigest(roads, rect),
            CellInputs.LabelsDigest(labels, rect), CellInputs.PoisDigest(pois, rect));
        GameFiles.GameFilesOutput.WriteAtomically(CellRecordPath(folder, map, cell.Id), fs => JsonSerializer.Serialize(fs, rec, Project.Json));
    }

    /// <summary>Records that the map's cells are drawn with the current style.</summary>
    public static void WriteRecord(Project project, MapSet map)
    {
        var rec = Current(project, map).Record;
        var path = RecordPath(new WorkFolder(project.WorkFolderPath), map);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        GameFiles.GameFilesOutput.WriteAtomically(path, fs => JsonSerializer.Serialize(fs, rec, Project.Json));
    }
}
