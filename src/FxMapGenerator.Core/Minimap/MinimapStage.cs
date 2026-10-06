using System.Text.Json;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Minimap;

/// <summary>
/// The minimap's texture dictionaries for one map, one unit per 4500 m sheet of <see cref="MinimapSheets.Size"/> px: the
/// sheet is put together from the map's tiles, compressed as DXT5 (<c>minimap_sea_r_c.ytd</c>) and as DXT1 with 1-bit
/// alpha (<c>minimap_r_c.ytd</c>; every pixel that is not transparent is opaque there, see-through water too), one mip
/// level, into <c>ytd/&lt;map&gt;/4096/</c>; one more unit for the small whole map under the radar
/// (<see cref="MinimapLod"/>, <c>minimap_lod_128.ytd</c>, made again when its zoom 2 tiles changed); and one unit per cell
/// beyond the standard frame holding blocks of the range (<see cref="MinimapExtraTiles"/>, DXT5, drawn by Extra Map Tiles,
/// made again when its tiles changed); <see cref="MinimapTextures"/> makes the files. One level only: with a smaller
/// level in the file, the game's "normal" texture quality draws that one and the map blurs when zoomed in. The
/// compression runs in bands of block rows on the free workers. Outside the range, the sheets keep the map's tiles or
/// are left transparent (the project's <c>minimap.outside</c>). A sheet is left when it was never made, the tiles it is
/// made of changed since (<c>data/minimap-&lt;map&gt;-&lt;sheet&gt;.json</c> holds their digest), or the sheets were made with
/// the other outside.
/// </summary>
public sealed class MinimapStage(MapSet map) : Stage
{
    public MapSet Map { get; } = map;
    IReadOnlySet<BlockId>? _only;
    bool _waiting;

    public override string Id => "ytd";
    public override string Row => "ytd";
    public override string? RecordKey => StageKeys.Ytd;
    public override string UnitName => "sheet";
    /// <summary>The sheet as RGBA and both compressed copies (88 MB), with room to spare.</summary>
    public override long MemoryPerUnit => 128L << 20;

    /// <summary>The unit (and its record) of a sheet: <c>&lt;map&gt;@4096/&lt;r&gt;_&lt;c&gt;</c>.</summary>
    public string UnitOf(SheetId sheet) => StageKeys.YtdUnit(Map.Id, sheet.Name);

    /// <summary>The unit (and its record) of the small whole map: <c>&lt;map&gt;@4096/lod</c>.</summary>
    public string LodUnit => StageKeys.YtdUnit(Map.Id, MinimapLod.Unit);

    /// <summary>The unit (and its record) of a cell beyond the standard frame: <c>&lt;map&gt;@4096/cell_&lt;r&gt;_&lt;c&gt;</c>.</summary>
    public string ExtraUnit(CellId cell) => StageKeys.YtdUnit(Map.Id, cell.Name);

    /// <summary><c>data/minimap-&lt;map&gt;.json</c>: what every sheet of the map was made with (written when all are made).</summary>
    public sealed record MinimapRecord(string Outside);

    static string RecordPath(WorkFolder folder, string map) => Path.Combine(folder.Data, $"minimap-{map}.json");

    static MinimapRecord? ReadRecord(WorkFolder folder, string map)
    {
        var p = RecordPath(folder, map);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<MinimapRecord>(File.ReadAllText(p), Project.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// True when the sheet was never made, the map's lower zooms are missing, the sheets were not all made with the
    /// project's outside, or the map was made again since the sheet (its lower zooms are newer) and the tiles the sheet is
    /// made of changed (<see cref="MinimapSheets.SourceDigest"/> against the sheet's record; no record counts as changed).
    /// </summary>
    public static bool IsStale(Project project, StateStore state, string map, SheetId sheet)
    {
        var low = state.StageDone(StageKeys.LowZoom, map);
        var done = state.StageDone(StageKeys.Ytd, StageKeys.YtdUnit(map, sheet.Name));
        if (low is null || done is null) return true;
        var folder = new WorkFolder(project.WorkFolderPath);
        if (ReadRecord(folder, map) != new MinimapRecord(project.File.Minimap.Outside)) return true;
        if (done >= low) return false;
        return ReadSheetRecord(folder, map, sheet) is not { } had || had.Source != MinimapSheets.SourceDigest(new TileStore(folder.Tiles(map)), sheet);
    }

    /// <summary>
    /// True when the small whole map was never made, the map's lower zooms are missing, or they were made since and its
    /// zoom 2 tiles changed (<see cref="MinimapLod.SourceDigest"/> against its record <c>data/minimap-&lt;map&gt;-lod.json</c>).
    /// </summary>
    public static bool IsLodStale(Project project, StateStore state, string map)
    {
        var low = state.StageDone(StageKeys.LowZoom, map);
        var done = state.StageDone(StageKeys.Ytd, StageKeys.YtdUnit(map, MinimapLod.Unit));
        if (low is null || done is null) return true;
        if (done >= low) return false;
        var folder = new WorkFolder(project.WorkFolderPath);
        return ReadRecordAt(LodRecordPath(folder, map)) is not { } had || had.Source != MinimapLod.SourceDigest(new TileStore(folder.Tiles(map)));
    }

    static string LodRecordPath(WorkFolder folder, string map) => Path.Combine(folder.Data, $"minimap-{map}-lod.json");

    /// <summary>
    /// True when a cell's texture beyond the standard frame was never made, the map's lower zooms are missing, the sheets
    /// were not all made with the project's outside, or the map was made again since and the cell's tiles changed
    /// (<see cref="MinimapExtraTiles.SourceDigest"/> against its record <c>data/minimap-&lt;map&gt;-cell_&lt;r&gt;_&lt;c&gt;.json</c>).
    /// </summary>
    public static bool IsExtraStale(Project project, StateStore state, string map, CellId cell)
    {
        var low = state.StageDone(StageKeys.LowZoom, map);
        var done = state.StageDone(StageKeys.Ytd, StageKeys.YtdUnit(map, cell.Name));
        if (low is null || done is null) return true;
        var folder = new WorkFolder(project.WorkFolderPath);
        if (ReadRecord(folder, map) != new MinimapRecord(project.File.Minimap.Outside)) return true;
        if (done >= low) return false;
        return ReadRecordAt(ExtraRecordPath(folder, map, cell)) is not { } had || had.Source != MinimapExtraTiles.SourceDigest(new TileStore(folder.Tiles(map)), cell);
    }

    static string ExtraRecordPath(WorkFolder folder, string map, CellId cell) => Path.Combine(folder.Data, $"minimap-{map}-{cell.Name}.json");

    /// <summary><c>data/minimap-&lt;map&gt;-&lt;sheet&gt;.json</c>: the digest of the tiles the sheet was made of.</summary>
    public sealed record SheetRecord(string Source);

    static SheetRecord? ReadRecordAt(string p)
    {
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<SheetRecord>(File.ReadAllText(p), Project.Json); }
        catch (JsonException) { return null; }
    }

    static string SheetRecordPath(WorkFolder folder, string map, SheetId sheet) => Path.Combine(folder.Data, $"minimap-{map}-{sheet.Name}.json");

    static SheetRecord? ReadSheetRecord(WorkFolder folder, string map, SheetId sheet)
    {
        var p = SheetRecordPath(folder, map, sheet);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<SheetRecord>(File.ReadAllText(p), Project.Json); }
        catch (JsonException) { return null; }
    }

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        _waiting = ctx.State.StageDone(StageKeys.LowZoom, Map.Id) is null;
        if (_waiting)
        {
            ctx.Log($"minimap: the {Map.Id} map has no lower zooms yet");
            return Array.Empty<string>();
        }
        var outside = ctx.Project.File.Minimap.Outside;
        _only = outside == MinimapOutside.Transparent ? ctx.Project.Range.Keys.ToHashSet() : null;
        var left = MinimapSheets.All.Where(s => IsStale(ctx.Project, ctx.State, Map.Id, s)).ToList();
        // a run stopped half-way must not leave the record of the other outside next to sheets made with this one
        if (left.Count > 0 && ReadRecord(ctx.Folder, Map.Id) is { } rec && rec.Outside != outside) File.Delete(RecordPath(ctx.Folder, Map.Id));
        bool lod = IsLodStale(ctx.Project, ctx.State, Map.Id);
        var extra = MinimapExtraTiles.Cells(ctx.Project.Range.Keys).Where(c => IsExtraStale(ctx.Project, ctx.State, Map.Id, c)).ToList();
        ctx.Log($"minimap: {Map.Id}, {left.Count} of {MinimapSheets.All.Count} sheets to make{(lod ? " and the small whole map" : "")}"
            + $"{(extra.Count > 0 ? $", {extra.Count} tiles beyond the standard map" : "")} ({ctx.Folder.Minimap(Map.Id)})");
        var units = left.Select(UnitOf).ToList();
        if (lod) units.Add(LodUnit);
        units.AddRange(extra.Select(ExtraUnit));
        return units;
    }

    public override int CountReady(Project project, StateStore state) =>
        state.StageDone(StageKeys.LowZoom, Map.Id) is null ? 0
            : MinimapSheets.All.Count(s => IsStale(project, state, Map.Id, s)) + (IsLodStale(project, state, Map.Id) ? 1 : 0)
              + MinimapExtraTiles.Cells(project.Range.Keys).Count(c => IsExtraStale(project, state, Map.Id, c));

    public override void Run(UnitContext ctx)
    {
        if (ctx.Unit == LodUnit)
        {
            MakeLod(ctx);
            return;
        }
        if (CellId.TryParse(ctx.Unit[(ctx.Unit.LastIndexOf('/') + 1)..], out var cell))
        {
            MakeExtra(ctx, cell);
            return;
        }
        var sheet = SheetId.TryParse(ctx.Unit[(ctx.Unit.LastIndexOf('/') + 1)..], out var s) ? s : throw new ArgumentException($"not a sheet unit: {ctx.Unit}");
        var tiles = new TileStore(ctx.Folder.Tiles(Map.Id));
        var source = MinimapSheets.SourceDigest(tiles, sheet);
        MinimapTextures.WriteSheet(ctx.Folder.Minimap(Map.Id), tiles, sheet, _only, ctx.Parallel, ctx.Report);
        GameFiles.GameFilesOutput.WriteAtomically(SheetRecordPath(ctx.Folder, Map.Id, sheet), fs => JsonSerializer.Serialize(fs, new SheetRecord(source), Project.Json));
    }

    /// <summary>The small whole map from the map's zoom 2 tiles (<see cref="MinimapLod"/>), over the open sea of its style.</summary>
    void MakeLod(UnitContext ctx)
    {
        var tiles = new TileStore(ctx.Folder.Tiles(Map.Id));
        var source = MinimapLod.SourceDigest(tiles);
        MinimapTextures.WriteLod(ctx.Folder.Minimap(Map.Id), tiles, MinimapTextures.Under(ctx.Project, Map), ctx.Report);
        GameFiles.GameFilesOutput.WriteAtomically(LodRecordPath(ctx.Folder, Map.Id), fs => JsonSerializer.Serialize(fs, new SheetRecord(source), Project.Json));
    }

    /// <summary>A cell's texture beyond the standard frame (<see cref="MinimapExtraTiles"/>), DXT5 with one level.</summary>
    void MakeExtra(UnitContext ctx, CellId cell)
    {
        var tiles = new TileStore(ctx.Folder.Tiles(Map.Id));
        var source = MinimapExtraTiles.SourceDigest(tiles, cell);
        MinimapTextures.WriteExtra(ctx.Folder.Minimap(Map.Id), tiles, cell, _only, ctx.Parallel, ctx.Report);
        GameFiles.GameFilesOutput.WriteAtomically(ExtraRecordPath(ctx.Folder, Map.Id, cell), fs => JsonSerializer.Serialize(fs, new SheetRecord(source), Project.Json));
    }

    public override void Finish(StageContext ctx, StageEnd end)
    {
        if (!end.Complete || _waiting) return;              // the record says what every sheet of the map was made with
        WriteRecord(ctx.Project, Map.Id);
    }

    /// <summary>Records that the map's sheets are made with the project's outside.</summary>
    public static void WriteRecord(Project project, string map)
    {
        var path = RecordPath(new WorkFolder(project.WorkFolderPath), map);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        GameFiles.GameFilesOutput.WriteAtomically(path, fs => JsonSerializer.Serialize(fs, new MinimapRecord(project.File.Minimap.Outside), Project.Json));
    }
}
