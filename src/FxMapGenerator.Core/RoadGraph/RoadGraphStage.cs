using System.Diagnostics;
using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.RoadGraph;

/// <summary>
/// The road graph of the whole range (<see cref="RoadGraphBuilder"/>), from the path graph of the game files with the
/// project's road edits applied (<see cref="RoadEditsFile.PathsOf"/>) and the on-road samples of the road scans:
/// <c>data/roads.json</c>, and <c>data/roads-record.json</c> (the blocks it was built from, what it holds and what the
/// edits did). Runs after the game files, and again when they or a road scan are newer, the range changed or the edits'
/// contents changed.
/// </summary>
public sealed class RoadGraphStage : Stage
{
    public const string Record = "roads-record.json";

    public override string Id => "roadGraph";
    public override string Row => "mapData.roadGraph";
    public override string? RecordKey => StageKeys.RoadGraph;
    public override string UnitName => "world";
    public override long MemoryPerUnit => 2L << 30;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        int waiting = GameFilesStage.WithoutRoadScan(ctx.Project, ctx.State);
        if (waiting > 0)
        {
            ctx.Log($"road graph: waiting for the road scans ({waiting} blocks of the range have none)");
            return [];
        }
        if (GameFilesStage.IsStale(ctx.Project, ctx.State))
        {
            ctx.Log("road graph: waiting for the game files (the path graph is not read yet, or older than the scans)");
            return [];
        }
        if (IsStale(ctx.Project, ctx.State)) return [StageKeys.World];
        ctx.Log("road graph: up to date");
        return [];
    }

    public override int CountReady(Project project, StateStore state) => GameFilesStage.WithoutRoadScan(project, state) == 0 && IsStale(project, state) ? 1 : 0;

    /// <summary>
    /// True when the graph was never built, the game files or a road scan of the range are newer than it, it was built
    /// from other blocks than the range's or with other road edits (their contents), or a file is gone.
    /// </summary>
    public static bool IsStale(Project project, StateStore state)
    {
        var done = state.StageDone(StageKeys.RoadGraph, StageKeys.World);
        if (done is null) return true;
        var folder = new WorkFolder(project.WorkFolderPath);
        if (!File.Exists(Path.Combine(folder.Data, RoadGraphFile.Roads))) return true;
        if (state.StageDone(StageKeys.GameFiles, StageKeys.World) is { } g && g > done) return true;
        foreach (var b in project.Range.Keys)
            if (state.ItemTime(b, BlockItem.ScanRoads) is { } t && t > done) return true;
        var record = ReadRecord(folder);
        return record is null || !record.Blocks.Order(StringComparer.Ordinal).SequenceEqual(project.Range.Keys.Select(b => b.Name).Order(StringComparer.Ordinal))
            || record.RoadEdits?.Digest != RoadEditsFile.DigestOf(project);
    }

    /// <summary>True when the graph was built before with other road edits (their contents) than the project has now.</summary>
    public static bool EditsChanged(Project project) =>
        ReadRecord(new WorkFolder(project.WorkFolderPath)) is { } record && record.RoadEdits?.Digest != RoadEditsFile.DigestOf(project);

    /// <summary>
    /// The blocks of the range where the project's road edits differ from those the graph was built with (per-block
    /// digests, <see cref="RoadEditSet.BlockDigests"/>): their cells are expected to be drawn again.
    /// </summary>
    public static IReadOnlyList<BlockId> EditedBlocks(Project project)
    {
        var made = ReadRecord(new WorkFolder(project.WorkFolderPath))?.RoadEdits?.Blocks ?? new Dictionary<string, string>();
        var now = RoadEditsFile.BlockDigestsOf(project);
        var range = project.Range;
        return made.Keys.Union(now.Keys).Where(b => made.GetValueOrDefault(b) != now.GetValueOrDefault(b))
            .Select(BlockId.Parse).Where(range.ContainsKey).Order().ToList();
    }

    public override void Run(UnitContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var blocks = ctx.Project.Range.Keys.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        ctx.Report("scans", 0);
        var scans = new ScanFile[blocks.Count];
        ctx.Parallel.ForEach(Enumerable.Range(0, blocks.Count).ToList(), i => scans[i] = ScanFile.Read(ctx.Folder.ScanFile(blocks[i])));
        var area = new ScanArea(scans);
        double tScans = sw.Elapsed.TotalSeconds;

        ctx.Report("paths", 0.1);
        var (paths, edits) = RoadEditsFile.PathsOf(ctx.Project, ctx.Folder);
        if (edits is not null) LogEdits(ctx.Log, "road graph", edits);
        var net = PathNet.Of(paths, area.X0, area.Y0, area.X0 + area.W, area.Y0 - area.H);
        paths = null!;
        var r = RoadGraphBuilder.Build(net, area, ctx.Parallel, report: (phase, f) => ctx.Report(phase, 0.1 + 0.85 * f));
        ctx.Report("write", 0.95);
        RoadGraphFile.Write(Path.Combine(ctx.Folder.Data, RoadGraphFile.Roads), r.Roads);
        var classes = Enum.GetValues<RoadClass>().Select(c =>
        {
            var rs = r.Roads.Where(x => x.Class == c).ToList();
            var measured = rs.Where(x => x.WidthSamples >= 3).Select(x => x.Width).ToList();
            return new ClassSummary(c.Name(), rs.Count, Math.Round(rs.Sum(x => x.Length)), measured.Count == 0 ? null : Math.Round(Num.Median(measured), 1),
                rs.Count == 0 ? 0 : Math.Round(100.0 * rs.Count(x => x.Attr.OneWay) / rs.Count));
        }).Where(c => c.Roads > 0).ToList();
        var record = new RoadsRecord(blocks.Select(b => b.Name).ToList(), r.Nodes, r.Links, r.Chains, r.Bundled, r.HeightRejected.Count,
            r.HeightRejected.Values.Sum(), r.ClassSmoothed, r.Dropped, r.Roads.Count, classes, Math.Round(sw.Elapsed.TotalSeconds, 1), edits);
        GameFilesOutput.WriteAtomically(Path.Combine(ctx.Folder.Data, Record), fs => JsonSerializer.Serialize(fs, record, Project.Json));
        ctx.Log($"road graph: {r.Roads.Count} roads from {r.Links} links of {r.Nodes} nodes ({r.Chains} chains, {r.Bundled} onto street bundles,"
            + $" {r.HeightRejected.Count} chain pairs of one street kept apart by height, {r.ClassSmoothed} class-smoothed, {r.Dropped} stubs dropped);"
            + $" {blocks.Count} scans read in {tScans:0.0} s, {sw.Elapsed.TotalSeconds:0.0} s in all");
        foreach (var c in classes)
            ctx.Log($"  {c.Class,-8} {c.Roads,5} roads {c.LengthM,8:0} m, measured width median {(c.MedianWidth is { } w ? $"{w:0.0} m" : "-")}, one-way {c.OneWayPercent:0} %");
    }

    /// <summary>Logs what the road edits did, and every edit not applied (at most 50) with the reason.</summary>
    public static void LogEdits(Action<string> log, string stage, RoadEditsSummary edits)
    {
        log($"{stage}: {edits.Describe()}");
        foreach (var n in edits.NotApplied.Take(50)) log($"  not applied: {n.Kind} {n.Key} ({n.Reason})");
        if (edits.NotApplied.Count > 50) log($"  ... and {edits.NotApplied.Count - 50} more (the record lists them all)");
    }

    /// <summary>Per class: roads, length (m), median measured width (m, roads with 3 or more measurements), share of one-way roads.</summary>
    public sealed record ClassSummary(string Class, int Roads, double LengthM, double? MedianWidth, double OneWayPercent);

    /// <summary><c>data/roads-record.json</c>: the blocks the graph was built from, what it holds, and what the road edits did (null without edits).</summary>
    public sealed record RoadsRecord(IReadOnlyList<string> Blocks, int Nodes, int Links, int Chains, int Bundled, int HeightSplitPairs,
        int HeightSplitStations, int ClassSmoothed, int Dropped, int Roads, IReadOnlyList<ClassSummary> Classes, double Seconds,
        RoadEditsSummary? RoadEdits = null);

    public static RoadsRecord? ReadRecord(WorkFolder folder)
    {
        var path = Path.Combine(folder.Data, Record);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RoadsRecord>(File.ReadAllText(path), Project.Json); }
        catch (JsonException) { return null; }
    }
}
