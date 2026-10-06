using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Regions;

/// <summary>
/// The zones and region colours of the whole range, one unit: the zone grid of the road scans (<see cref="ZoneGrid"/>,
/// <c>data/zones.grid</c>; the labels read it) and, per regions section of the maps' styles that take their ground from
/// the region colours, a region field (<see cref="RegionField"/>, <c>data/regions-&lt;key&gt;.grid</c>).
/// <c>data/regions-record.json</c> holds the blocks and the fields with their sections. Runs after the landcover, and
/// again when a block's road scan or landcover is newer, the range or the frame changed, or a style needs a field not made.
/// </summary>
public sealed class RegionsStage : Stage
{
    public const string Record = "regions-record.json";

    public override string Id => "regions";
    public override string Row => "mapData.regions";
    public override string? RecordKey => StageKeys.Regions;
    public override string UnitName => "world";
    public override long MemoryPerUnit => 1L << 30;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        if (Waiting(ctx.Project, ctx.State) is { } why)
        {
            ctx.Log("regions: waiting for " + why);
            return [];
        }
        if (IsStale(ctx.Project, ctx.State)) return [StageKeys.World];
        ctx.Log("regions: up to date");
        return [];
    }

    public override int CountReady(Project project, StateStore state) => Waiting(project, state) is null && IsStale(project, state) ? 1 : 0;

    public static string? Waiting(Project project, StateStore state)
    {
        int noRoads = GameFilesStage.WithoutRoadScan(project, state);
        if (noRoads > 0) return $"the road scans ({noRoads} blocks of the range have none)";
        var (ready, waiting) = LandcoverStage.Left(project, state);
        if (ready.Count + waiting.Count > 0) return $"the landcover ({ready.Count + waiting.Count} blocks of the range are not made yet)";
        return null;
    }

    /// <summary>
    /// The region fields the maps need: per regions section of the styles that take their ground from the region colours
    /// (first map first, each section once) its key (<see cref="RegionField.KeyOf"/>), the style's regions and the section.
    /// </summary>
    public static List<(string Key, RegionsStyle Style, JsonNode Section)> Fields(Project project)
    {
        var o = new List<(string, RegionsStyle, JsonNode)>();
        foreach (var m in project.Maps.Where(m => m.IsCellMap))
        {
            var st = project.StyleOf(m);
            if (st.GroundRaster?.Mode != "regions") continue;
            var section = st.Source["regions"]!;
            var key = RegionField.KeyOf(section);
            if (o.All(f => f.Item1 != key)) o.Add((key, st.Regions, section.DeepClone()));
        }
        return o;
    }

    public static bool IsStale(Project project, StateStore state)
    {
        if (state.StageDone(StageKeys.Regions, StageKeys.World) is not { } done) return true;
        var folder = new WorkFolder(project.WorkFolderPath);
        if (!File.Exists(Path.Combine(folder.Data, ZoneGrid.FileName))) return true;
        foreach (var b in project.Range.Keys)
        {
            if (state.ItemTime(b, BlockItem.ScanRoads) is { } t && t > done) return true;
            if (state.StageDone(StageKeys.Landcover, b.Name) is { } l && l > done) return true;
        }
        var record = ReadRecord(folder);
        if (record is null || !record.Blocks.Order(StringComparer.Ordinal).SequenceEqual(project.Range.Keys.Select(b => b.Name).Order(StringComparer.Ordinal))) return true;
        var frame = project.Frame;
        if (!(record.ExtraCells ?? [0, 0, 0, 0]).SequenceEqual([frame.CellsTop, frame.CellsBottom, frame.CellsLeft, frame.CellsRight])) return true;
        return Fields(project).Any(f => !File.Exists(Path.Combine(folder.Data, RegionField.FileName(f.Key))) || record.Fields?.ContainsKey(f.Key) != true);
    }

    public override void Run(UnitContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var blocks = ctx.Project.Range.Keys.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        ctx.Report("scans", 0);
        var scans = new ScanFile[blocks.Count];
        ctx.Parallel.ForEach(Enumerable.Range(0, blocks.Count).ToList(), i => scans[i] = ScanFile.Read(ctx.Folder.ScanFile(blocks[i])));
        var zones = ZoneGrid.Build(scans);
        scans = [];
        zones.Save(Path.Combine(ctx.Folder.Data, ZoneGrid.FileName));
        double tZones = sw.Elapsed.TotalSeconds;
        var needed = Fields(ctx.Project);
        var fields = new Dictionary<string, RegionsFieldRecord>(StringComparer.Ordinal);
        var said = new List<string>();
        if (needed.Count > 0)
        {
            ctx.Report("land", 0.3);
            var frame = ctx.Project.Frame;
            var (c0, r0, _, _) = RegionField.Extent(frame);
            var land4 = RegionField.Land4(ctx.Folder.Data, blocks, frame, ctx.Parallel);
            var onFrame = RegionField.ZonesOnFrame(zones, frame);
            for (int i = 0; i < needed.Count; i++)
            {
                var (key, style, section) = needed[i];
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Report("field", 0.4 + 0.5 * i / needed.Count);
                var f = RegionField.Compute(onFrame, zones.Codes, land4, style, ctx.Parallel);
                f = new RegionField
                {
                    Red = f.Red, Green = f.Green, Blue = f.Blue, Region = f.Region, Zone = f.Zone, Codes = f.Codes, Params = section,
                    OriginX = WorldGrid.Left + c0 * RegionField.Step, OriginY = WorldGrid.Top - r0 * RegionField.Step,
                };
                f.Save(Path.Combine(ctx.Folder.Data, RegionField.FileName(key)));
                // what each cell's ground reads of the field: a cell is made again only when its part changed
                fields[key] = new RegionsFieldRecord(section, CellPlan.For(ctx.Project.Range.Keys).ToDictionary(c => c.Id.Name, c => f.WindowDigest(new Cells.CellArea(c))));
                said.Add($"{key} (blur {style.Blur:0.#} m)");
            }
        }
        // the fields no map needs any more
        foreach (var path in Directory.EnumerateFiles(ctx.Folder.Data, "regions*.grid"))
            if (!fields.Keys.Any(k => Path.GetFileName(path) == RegionField.FileName(k))) File.Delete(path);
        var extent = RegionField.Extent(ctx.Project.Frame);
        string field = needed.Count == 0 ? "no map takes its ground from the region colours"
            : $"region fields {extent.Width} x {extent.Height} at {RegionField.Step:0} m: {string.Join(", ", said)}";
        var fr = ctx.Project.Frame;
        var record = new RegionsRecord(blocks.Select(b => b.Name).ToList(), zones.Codes.Count - 1, Math.Round(sw.Elapsed.TotalSeconds, 1), fields.Count == 0 ? null : fields,
            fr.IsStandard ? null : [fr.CellsTop, fr.CellsBottom, fr.CellsLeft, fr.CellsRight]);
        GameFilesOutput.WriteAtomically(Path.Combine(ctx.Folder.Data, Record), fs => JsonSerializer.Serialize(fs, record, Project.Json));
        ctx.Log($"regions: zone grid {zones.Zone.Width} x {zones.Zone.Height} at {zones.Step:0} m ({zones.Codes.Count - 1} zones, {tZones:0.0} s); {field}; {sw.Elapsed.TotalSeconds:0.0} s in all");
    }

    /// <summary>
    /// <c>data/regions-record.json</c>: the blocks, the number of zones, the region fields by key (null = none), and the cells
    /// the frame adds (top, bottom, left, right; written only when it adds any).
    /// </summary>
    public sealed record RegionsRecord(IReadOnlyList<string> Blocks, int Zones, double Seconds, IReadOnlyDictionary<string, RegionsFieldRecord>? Fields = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int[]? ExtraCells = null);

    /// <summary>A region field: the regions section it was made with, and per cell of the range the digest of the field's part its ground reads (<see cref="RegionField.WindowDigest"/>).</summary>
    public sealed record RegionsFieldRecord(JsonNode Regions, IReadOnlyDictionary<string, string> Cells);

    public static RegionsRecord? ReadRecord(WorkFolder folder)
    {
        var path = Path.Combine(folder.Data, Record);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RegionsRecord>(File.ReadAllText(path), Project.Json); }
        catch (JsonException) { return null; }
    }
}
