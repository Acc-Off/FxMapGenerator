using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Render;

/// <summary>
/// The open sea and the lower zooms of an atlas or road map (one unit, the map): the z8 tiles of the project's frame
/// outside the range painted with the style's open sea (<see cref="MapStyle.OpenSea"/> with its opacity, see-through when the
/// style's deepest band is), then every tile of z7 to z0 made
/// again from the four below it, as the satellite map does (<see cref="LowZooms"/>). Runs after the map's cells are drawn,
/// and again when a cell is drawn after it, the open sea's colour changed or the frame changed.
/// </summary>
public sealed class CellLowZoomStage(MapSet map) : Stage
{
    public MapSet Map { get; } = map;

    public override string Id => "lowZoom." + Map.Id;
    public override string Row => "lowZoom." + Map.Id;
    public override string? RecordKey => StageKeys.LowZoom;
    public override string UnitName => "set";
    public override long MemoryPerUnit => 64L << 20;

    /// <summary>
    /// True when the lower zooms were never made, a cell was drawn after them, the sea's colour is not the style's or they
    /// were made for another frame.
    /// </summary>
    public static bool IsStale(Project project, StateStore state, MapSet map)
    {
        if (state.StageDone(StageKeys.LowZoom, map.Id) is not { } done) return true;
        var cells = CellPlan.For(project.Range.Keys).Select(c => StageKeys.CellUnit(map.Id, c.Id));
        if (state.NewestStageDone(StageKeys.Cells, cells) is { } newest && newest > done) return true;
        var folder = new WorkFolder(project.WorkFolderPath);
        var sea = SeaPath(folder, map);
        return !File.Exists(sea) || File.ReadAllText(sea).Trim() != project.StyleOf(map).OpenSeaText
            || LowZooms.Recorded(folder, map.Id) != project.Frame;
    }

    /// <summary>The colour the outside tiles were painted with (and its opacity when not 1): <c>data/sea-&lt;map&gt;.txt</c>.</summary>
    static string SeaPath(WorkFolder folder, MapSet map) => Path.Combine(folder.Data, $"sea-{map.Id}.txt");

    static bool Drawn(Project project, StateStore state, MapSet map) =>
        CellPlan.For(project.Range.Keys).All(c => state.StageDone(StageKeys.Cells, StageKeys.CellUnit(map.Id, c.Id)) is not null);

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        if (!Drawn(ctx.Project, ctx.State, Map))
        {
            ctx.Log($"{Map.Id}: sea and lower zooms: waiting for the cells");
            return [];
        }
        if (IsStale(ctx.Project, ctx.State, Map)) return [Map.Id];
        ctx.Log($"{Map.Id}: sea and lower zooms: up to date");
        return [];
    }

    public override int CountReady(Project project, StateStore state) => Drawn(project, state, Map) && IsStale(project, state, Map) ? 1 : 0;

    public override void Run(UnitContext ctx)
    {
        var tiles = TileStore.Keeping(ctx.Folder, Map.Id);
        var style = ctx.Project.StyleOf(Map);
        var sea = style.OpenSea;
        byte alpha = style.OpenSeaAlpha;
        ctx.Report("sea", 0);
        var flat = new byte[TileStore.TileSize * TileStore.TileSize * 4];
        for (int i = 0; i < flat.Length; i += 4) (flat[i], flat[i + 1], flat[i + 2], flat[i + 3]) = (sea.R, sea.G, sea.B, alpha);
        var seaPng = TileStore.EncodePng(flat, TileStore.TileSize, TileStore.TileSize);
        var frame = ctx.Project.Frame;
        int removed = LowZooms.Prepare(ctx.Folder, Map.Id, tiles, frame);
        if (removed > 0) ctx.Log($"{Map.Id}: {removed} tiles outside the map's frame removed");
        var seaTiles = LowZooms.SeaTiles(frame, ctx.Project.Range.Keys.ToHashSet());
        ctx.Parallel.ForEach(seaTiles, t => tiles.WriteBytes(WorldGrid.Zoom, t.X, t.Y, seaPng));
        int parents = LowZooms.BuildParents(tiles, frame, ctx.Parallel, (s, f) => ctx.Report(s, f), ctx.Token);
        File.WriteAllText(SeaPath(ctx.Folder, Map), style.OpenSeaText + "\n");
        LowZooms.Record(ctx.Folder, Map.Id, frame);
        ctx.Log($"{Map.Id}: sea {style.OpenSeaText} on {seaTiles.Count} tiles, {parents} tiles at z7..z0");
    }
}
