using System.Diagnostics;
using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Roads;

/// <summary>
/// The road shapes of the whole map (<see cref="RoadShapes"/>): from the path graph of the game files with the project's
/// road edits applied (<see cref="RoadEditsFile.PathsOf"/>), their names and the water and surface layers of the range's
/// landcover, <c>data/road-shapes.grid</c> and <c>data/road-shapes-record.json</c> (the blocks it was made from, what it
/// holds and what the edits did). Runs after the landcover, and again when the game files or a block's landcover are
/// newer, the range changed or the edits' contents changed.
/// </summary>
public sealed class RoadsStage : Stage
{
    public const string Record = "road-shapes-record.json";

    public override string Id => "roads";
    public override string Row => "mapData.roads";
    public override string? RecordKey => StageKeys.Roads;
    public override string UnitName => "world";
    public override long MemoryPerUnit => 2L << 30;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        if (Waiting(ctx.Project, ctx.State) is { } why)
        {
            ctx.Log("roads: waiting for " + why);
            return [];
        }
        if (IsStale(ctx.Project, ctx.State)) return [StageKeys.World];
        ctx.Log("roads: up to date");
        return [];
    }

    public override int CountReady(Project project, StateStore state) => Waiting(project, state) is null && IsStale(project, state) ? 1 : 0;

    /// <summary>What the stage waits for (the game files, the road graph, the landcover), or null.</summary>
    public static string? Waiting(Project project, StateStore state)
    {
        if (GameFilesStage.IsStale(project, state)) return "the game files (the path graph is not read yet, or older than the scans)";
        if (RoadGraphStage.IsStale(project, state)) return "the road graph (the landcover is made again after it)";
        var (ready, waiting) = LandcoverStage.Left(project, state);
        if (ready.Count + waiting.Count > 0) return $"the landcover ({ready.Count + waiting.Count} blocks of the range are not made yet)";
        return null;
    }

    /// <summary>
    /// True when the shapes were never made, the game files or a block's landcover are newer than them, they were made
    /// from other blocks than the range's or with other road edits (their contents), or a file is gone.
    /// </summary>
    public static bool IsStale(Project project, StateStore state)
    {
        if (state.StageDone(StageKeys.Roads, StageKeys.World) is not { } done) return true;
        var folder = new WorkFolder(project.WorkFolderPath);
        if (!File.Exists(Path.Combine(folder.Data, RoadShapesFile.FileName))) return true;
        if (state.StageDone(StageKeys.GameFiles, StageKeys.World) is { } g && g > done) return true;
        foreach (var b in project.Range.Keys)
            if (state.StageDone(StageKeys.Landcover, b.Name) is { } t && t > done) return true;
        var record = ReadRecord(folder);
        return record is null || !record.Blocks.Order(StringComparer.Ordinal).SequenceEqual(project.Range.Keys.Select(b => b.Name).Order(StringComparer.Ordinal))
            || record.RoadEdits?.Digest != RoadEditsFile.DigestOf(project);
    }

    public override void Run(UnitContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var blocks = ctx.Project.Range.Keys.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        ctx.Report("read", 0);
        var (paths, edits) = RoadEditsFile.PathsOf(ctx.Project, ctx.Folder);
        if (edits is not null) RoadGraphStage.LogEdits(ctx.Log, "roads", edits);
        var world = RoadNet.WorldGrids(ctx.Folder.Data, blocks, ctx.Project.Frame, ctx.Parallel, ctx.Token);
        double tRead = sw.Elapsed.TotalSeconds;
        var (g, drawable, boats, highways, unpaved, links, levels, shapes, tunnels) = Make(paths, Path.Combine(ctx.Folder.Game, GameFilesOutput.Names), world, ctx.Token, ctx.Report);
        paths = null!;
        world = null!;
        ctx.Report("write", 0.95);
        RoadShapesFile.Write(Path.Combine(ctx.Folder.Data, RoadShapesFile.FileName), shapes, tunnels);
        // what each cell's drawing takes of the shapes, as the drawing reads them: a cell is drawn again only when its part changed
        var written = RoadShapesFile.Read(Path.Combine(ctx.Folder.Data, RoadShapesFile.FileName));
        var cells = CellPlan.For(ctx.Project.Range.Keys).ToDictionary(c => c.Id.Name, c => Render.CellInputs.RoadsDigest(written, new Cells.CellArea(c).Rect));
        double unpavedKm = Math.Round(g.Drawable.Where(k => unpaved[k]).Sum(g.Length) / 1000, 1);
        var record = new ShapesRecord(blocks.Select(b => b.Name).ToList(), drawable, boats.Dropped.Sum(d => d.Run.Count), highways.Count, Math.Round(unpavedKm, 1),
            links.Keys.Count, links.Extended, links.Through, links.Reached, links.ForkCount, links.ContinuationPairs, levels.Over,
            shapes.Pieces.Count, shapes.TunnelPieces, shapes.Ground.Count,
            shapes.Raised.GroupBy(r => r.Level).OrderBy(x => x.Key).ToDictionary(x => x.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), x => x.Count()),
            shapes.Patches.Count, shapes.Corners.Count, shapes.Tracks.Count / 4, tunnels.Count, tunnels.Sum(t => t.Rings.Count), Math.Round(sw.Elapsed.TotalSeconds, 1), cells, edits);
        GameFilesOutput.WriteAtomically(Path.Combine(ctx.Folder.Data, Record), fs => JsonSerializer.Serialize(fs, record, Project.Json));
        ctx.Log($"roads: {links.Keys.Count} links drawn of {drawable} ({record.BoatLinks} boat-route links left out, {highways.Count} highway, {unpavedKm:0.0} km unpaved;"
            + $" {links.Extended} junction connectors extended, {links.Through} through links, {links.Reached} reaching the next road, {links.ForkCount} forks),"
            + $" {levels.Over} links over another road; {shapes.Pieces.Count} pieces ({shapes.TunnelPieces} in tunnels), {shapes.Ground.Count} ground ribbons,"
            + $" {shapes.Raised.Count} raised runs, {shapes.Patches.Count} corner patches, {shapes.Corners.Count} junction corners, {tunnels.Count} tunnel groups;"
            + $" {blocks.Count} landcover blocks read in {tRead:0.0} s, {sw.Elapsed.TotalSeconds:0.0} s in all");
    }

    /// <summary>What <see cref="Make"/> made, with the counts the record keeps.</summary>
    public sealed record Made(RoadNet Graph, int DrawableLinks, RoadNet.BoatResult Boats, HashSet<(string, string)> Highways, Dictionary<(string, string), bool> Unpaved,
        RoadLinks Links, RoadLevels.Result Levels, RoadShapes Shapes, List<TunnelGroup> Tunnels);

    /// <summary>
    /// The road shapes of a path file (with the road edits applied) and the names file, over the water and surface
    /// layers of the landcover (<see cref="RoadNet.WorldGrids"/>, only read): the graph, boat routes left out, highways,
    /// unpaved links from the ground's material, the links and their levels, the shapes and the tunnels' outlines. The
    /// step and the road editor's preview both make them with this.
    /// </summary>
    public static Made Make(PathFile paths, string namesJson, RoadNet.WorldRaster world, CancellationToken token = default, Action<string, double>? report = null)
    {
        var g = RoadNet.Of(paths, namesJson);
        int drawable = g.Drawable.Count;
        report?.Invoke("graph", 0.3);
        var boats = g.DropBoatRoutes(world);
        var highways = g.Highways();
        var unpaved = g.UnpavedLinks(g.MaterialVotes(world, highways), highways);
        token.ThrowIfCancellationRequested();
        report?.Invoke("links", 0.45);
        var links = RoadLinks.Build(g, g.Junctions, highways, unpaved);
        report?.Invoke("levels", 0.55);
        var levels = RoadLevels.Compute(links.Keys, links.Rows, token);
        report?.Invoke("shapes", 0.7);
        var shapes = RoadShapes.Build(links, levels.Level, levels.Raised, token);
        report?.Invoke("tunnels", 0.85);
        var tunnels = TunnelOutlines.Build(links.Keys, shapes.TunnelSegments);
        return new Made(g, drawable, boats, highways, unpaved, links, levels, shapes, tunnels);
    }

    /// <summary><c>data/road-shapes-record.json</c>: the blocks the shapes were made from, what they hold, per cell of the
    /// range the digest of the shapes its drawing takes (<see cref="Render.CellInputs.RoadsDigest"/>), and what the road
    /// edits did (null without edits).</summary>
    public sealed record ShapesRecord(IReadOnlyList<string> Blocks, int DrawableLinks, int BoatLinks, int HighwayLinks, double UnpavedKm,
        int DrawnLinks, int Extended, int Through, int Reached, int Forks, int ContinuationPairs, int LinksOver,
        int Pieces, int TunnelPieces, int GroundRibbons, IReadOnlyDictionary<string, int> RaisedRuns, int CornerPatches, int JunctionCorners,
        int TrackSegments, int TunnelGroups, int TunnelRings, double Seconds, IReadOnlyDictionary<string, string>? Cells = null,
        RoadEditsSummary? RoadEdits = null);

    public static ShapesRecord? ReadRecord(WorkFolder folder)
    {
        var path = Path.Combine(folder.Data, Record);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ShapesRecord>(File.ReadAllText(path), Project.Json); }
        catch (JsonException) { return null; }
    }
}
