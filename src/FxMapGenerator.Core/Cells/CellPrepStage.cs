using System.Diagnostics;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Regions;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Cells;

/// <summary>
/// The map data of each cell (one unit per cell, <see cref="CellFiles"/>): the 1 m grids of its blocks
/// (<see cref="CellGrids"/>), the vector layers the maps' styles need (<see cref="CellLayers"/>), the hill shading and
/// the ground pictures (<see cref="CellHeights"/>, <see cref="GroundRaster"/>); a shading per set of the styles' shade
/// values, a ground picture per style (made of its own region field when its ground is the region colours). The heights are the landcover's
/// (<see cref="Satellite.SurfaceHeights.LandcoverItems"/>: the ground scan's surface, with quality per point the higher of
/// it and the height data), so the terrain under the buildings is judged and shaded from the same surface. Runs after
/// the landcover (and the region colours when a style takes its ground from them); a cell is made again when one of
/// its blocks' data or landcover, or the part of the region colours its ground reads, changed (newer, with other
/// contents than the cell read), or a map's style needs values the cell was not made with.
/// </summary>
public sealed class CellPrepStage : Stage
{
    Dictionary<string, RegionField> _fields = new();
    IReadOnlyList<MapStyle> _styles = [];
    IReadOnlyList<BlockItem> _items = [BlockItem.ScanGround];
    Dictionary<string, Cell> _cells = new();

    public override string Id => "cellPrep";
    public override string Row => "cells.prep";
    public override string? RecordKey => StageKeys.CellPrep;
    public override string UnitName => "cell";
    public override long MemoryPerUnit => 3L << 30;

    /// <summary>The styles of the project's cell maps, in map order (one per map; repeated styles repeat).</summary>
    public static List<MapStyle> Styles(Project project) => project.Maps.Where(m => m.IsCellMap).Select(project.StyleOf).ToList();

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        if (Waiting(ctx.Project, ctx.State) is { } why)
        {
            ctx.Log("cell data: waiting for " + why);
            return [];
        }
        _styles = Styles(ctx.Project);
        _items = Satellite.SurfaceHeights.LandcoverItems(ctx.Project);
        _cells = CellPlan.For(ctx.Project.Range.Keys).ToDictionary(c => c.Id.Name);
        var left = _cells.Values.Where(c => IsStale(ctx.Project, ctx.State, c)).Select(c => c.Id.Name).ToList();
        if (left.Count == 0)
        {
            ctx.Log("cell data: up to date");
            return [];
        }
        _fields = FieldKeys(_styles).Distinct().ToDictionary(k => k, k => RegionField.Load(Path.Combine(ctx.Folder.Data, RegionField.FileName(k))));
        ctx.Log($"cell data: {left.Count} cells to make");
        return left;
    }

    public override int CountReady(Project project, StateStore state) =>
        Waiting(project, state) is not null ? 0 : CellPlan.For(project.Range.Keys).Count(c => IsStale(project, state, c));

    public static string? Waiting(Project project, StateStore state)
    {
        var (ready, waiting) = LandcoverStage.Left(project, state);
        if (ready.Count + waiting.Count > 0) return $"the landcover ({ready.Count + waiting.Count} blocks of the range are not made yet)";
        if (Styles(project).Any(s => s.GroundRaster?.Mode == "regions") && RegionsStage.IsStale(project, state)) return "the region colours";
        return null;
    }

    /// <summary>The key of the region field a style's ground is made of (<see cref="RegionField.KeyOf"/>), or null (its ground is not the region colours).</summary>
    public static string? FieldKey(MapStyle style) => style.GroundRaster?.Mode == "regions" ? RegionField.KeyOf(style.Source["regions"]!) : null;

    static IEnumerable<string> FieldKeys(IEnumerable<MapStyle> styles) => styles.Select(FieldKey).OfType<string>();

    /// <summary>
    /// What a cell's data must hold for the project's styles: the layer sets, the sets of shade values (each once, a
    /// shading each), the ground values per style.
    /// </summary>
    public static (List<string> Sets, List<JsonNode> Shades, Dictionary<string, JsonNode> Grounds) Needs(IReadOnlyList<MapStyle> styles)
    {
        var sets = new List<string>();
        void Add(string? s) { if (s is not null && !sets.Contains(s)) sets.Add(s); }
        foreach (var s in styles)
        {
            Add(CellLayers.Sets.Ground(s));
            Add(CellLayers.Sets.Canopy(s));
            Add(CellLayers.Sets.Sea(s));
            Add(CellLayers.Sets.Buildings(s));
            Add(CellLayers.Sets.Contours(s));
        }
        var shades = new List<JsonNode>();
        foreach (var s in styles.Where(s => s.Shade is not null))
            if (ShadeValues(s.Shade!) is var v && !shades.Any(h => JsonNode.DeepEquals(h, v))) shades.Add(v);
        var grounds = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var s in styles.Where(s => s.GroundRaster is not null)) grounds[s.Id] = GroundValues(s);
        return (sets, shades, grounds);
    }

    /// <summary>The shade values the light depends on (the strength is applied when drawing).</summary>
    public static JsonNode ShadeValues(ShadeStyle sh) => new JsonObject
    {
        ["blur"] = sh.Blur,
        ["altitude"] = sh.Altitude,
        ["lights"] = new JsonArray(sh.Lights.Select(l => (JsonNode)new JsonArray(l.Azimuth, l.Weight)).ToArray()),
    };

    /// <summary>The style values a ground picture depends on (of a blend, only its method's values).</summary>
    public static JsonNode GroundValues(MapStyle s)
    {
        var o = new JsonObject { ["groundRaster"] = GroundRasterStyle.InUse(s.Source["groundRaster"]!.AsObject()) };
        if (s.GroundRaster!.Mode == "blend")
        {
            o["groundPaints"] = s.Source["groundPaints"]!.DeepClone();
            o["ground"] = s.Source["paint"]!["ground"]!.DeepClone();
            o["water"] = s.Source["paint"]!["water"]!.DeepClone();
        }
        else o["regions"] = s.Source["regions"]!.DeepClone();
        return o;
    }

    /// <summary>
    /// True when the cell's data was never made, a file is gone, an input is newer and its contents differ from what the
    /// cell read (<see cref="CellRecord.Inputs"/>, <see cref="CellRecord.Regions"/>; a record without them counts as
    /// differing), or the styles need values it was not made with (<see cref="Changes"/>).
    /// </summary>
    public static bool IsStale(Project project, StateStore state, Cell cell) => Changes(project, state, cell) is not null;

    /// <summary>What <see cref="Changes"/> gives when the whole of a cell's data changes.</summary>
    public const string Everything = "*";

    /// <summary>
    /// What making the cell's data again would change (null: it is not to be made again): <see cref="Everything"/> (never
    /// made, a file gone, an input changed, a layer set missing: the layers every map reads), else the names of the files
    /// only some styles read: a shading of values not made, a style's ground picture of values not made or of a region
    /// field whose part changed. A map is drawn again only when its files are among them (<see cref="Reaches"/>).
    /// </summary>
    public static IReadOnlySet<string>? Changes(Project project, StateStore state, Cell cell)
    {
        IReadOnlySet<string> all = new HashSet<string> { Everything };
        if (state.StageDone(StageKeys.CellPrep, cell.Id.Name) is not { } done) return all;
        var folder = new WorkFolder(project.WorkFolderPath);
        var items = Satellite.SurfaceHeights.LandcoverItems(project);
        var dir = CellFiles.Folder(folder, cell.Id);
        var record = CellFiles.ReadRecord(Path.Combine(dir, CellFiles.Record));
        if (record is null || !File.Exists(Path.Combine(dir, CellFiles.Layers))) return all;
        if (!record.Blocks.Order(StringComparer.Ordinal).SequenceEqual(cell.All.Select(b => b.Name).Order(StringComparer.Ordinal))) return all;
        foreach (var b in cell.All)
        {
            bool newer = (state.StageDone(StageKeys.Landcover, b.Name) is { } l && l > done)
                || new[] { BlockItem.ScanGround, BlockItem.ScanRoads, BlockItem.Height }.Any(item => state.ItemTime(b, item) is { } t && t > done);
            // a newer input counts only when its contents changed (a landcover made again the same, a block taken again the same)
            if (newer && (record.Inputs is null || !record.Inputs.TryGetValue(b.Name, out var had) || had != InputsOf(folder, b, items))) return all;
            if (record.Heights.GetValueOrDefault(b.Name) != Satellite.SurfaceHeights.NameOf(items)) return all;
        }
        var styles = Styles(project);
        var (sets, shades, grounds) = Needs(styles);
        if (sets.Any(s => !record.Sets.Contains(s))) return all;
        var changes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shade in shades)
            if (!(record.Shades?.Any(h => JsonNode.DeepEquals(h, shade)) ?? false) || !File.Exists(Path.Combine(dir, CellFiles.ShadeName(shade)))) changes.Add(CellFiles.ShadeName(shade));
        foreach (var (id, values) in grounds)
            if (!record.Grounds.TryGetValue(id, out var had) || !JsonNode.DeepEquals(values, had) || !File.Exists(Path.Combine(dir, CellFiles.GroundName(id)))) changes.Add(CellFiles.GroundName(id));
        // newer region colours count only when the part this cell's ground reads of a field changed
        var keys = FieldKeys(styles).Distinct().ToList();
        if (keys.Count > 0 && state.StageDone(StageKeys.Regions, StageKeys.World) is { } r && r > done)
        {
            var fields = RegionsStage.ReadRecord(folder)?.Fields;
            foreach (var key in keys)
                if (record.Regions?.GetValueOrDefault(key) is not { } read || fields?.GetValueOrDefault(key)?.Cells.GetValueOrDefault(cell.Id.Name) != read)
                    foreach (var s in styles.Where(s => FieldKey(s) == key)) changes.Add(CellFiles.GroundName(s.Id));
        }
        return changes.Count == 0 ? null : changes;
    }

    /// <summary>Whether a change of a cell's data (<see cref="Changes"/>) reaches a map drawn with a style (the files it reads, <see cref="OutputFor"/>).</summary>
    public static bool Reaches(IReadOnlySet<string> changes, MapStyle style) =>
        changes.Contains(Everything)
        || (style.Shade is { } sh && changes.Contains(CellFiles.ShadeName(ShadeValues(sh))))
        || (style.GroundRaster is not null && changes.Contains(CellFiles.GroundName(style.Id)));

    public override void Run(UnitContext ctx)
    {
        var cell = _cells[ctx.Unit];
        var r = Make(ctx.Folder, ctx.State, cell, _styles, _fields, ctx.Parallel, (p, f) => ctx.Report(p, f), ctx.Token, _items);
        ctx.Log($"cell data {cell.Id.Name}: {r.Layers} layers, {r.Rings} outlines; heights {r.Heights}; {r.Seconds:0.0} s");
    }

    public sealed record Result(int Layers, int Rings, string Heights, double Seconds);

    /// <summary>
    /// Makes one cell's data and writes its files; the heights from <paramref name="heightItems"/> (the landcover's;
    /// default: the ground scan); <paramref name="fields"/>: the region fields by key (<see cref="FieldKey"/>) of the
    /// styles whose ground is made of them. The shadings no style needs any more are deleted.
    /// </summary>
    public static Result Make(WorkFolder folder, StateStore state, Cell cell, IReadOnlyList<MapStyle> styles, IReadOnlyDictionary<string, RegionField> fields,
        IParallelRunner? parallel = null, Action<string, double>? report = null, CancellationToken token = default, IReadOnlyList<BlockItem>? heightItems = null)
    {
        var sw = Stopwatch.StartNew();
        report?.Invoke("load", 0);
        var area = new CellArea(cell);
        var inputs = new Dictionary<BlockId, CellGrids.BlockInput>();
        var heights = new Dictionary<BlockId, float[]>();
        var heightFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = heightItems ?? [BlockItem.ScanGround];
        int n = 0;
        foreach (var b in area.Blocks)
        {
            token.ThrowIfCancellationRequested();
            var scan = ScanFile.Read(folder.ScanFile(b));
            var lc = GridFile.Load(LandcoverFile.PathOf(folder.Data, b));
            inputs[b] = new CellGrids.BlockInput(lc, scan);
            n = scan.N;
            var hg = Satellite.SurfaceHeights.Of(folder, b, items, scan);
            heights[b] = Cut(hg.Values.Select(v => (float)v).ToArray(), hg.N, n);
            heightFrom[b.Name] = Satellite.SurfaceHeights.NameOf(items);
        }
        report?.Invoke("grids", 0.1);
        var g = CellGrids.Build(area, inputs, Materials.Default);
        inputs.Clear();
        var dir = CellFiles.Folder(folder, cell.Id);
        Directory.CreateDirectory(dir);
        var (sets, shades, grounds) = Needs(styles);
        Grid<float>? terrain = null;
        bool wantContours = styles.Any(s => s.Contours is not null), wantShade = shades.Count > 0;
        if (wantContours || wantShade)
        {
            report?.Invoke("heights", 0.2);
            var dsm = CellHeights.Surface(area, heights, n);
            if (dsm is not null)
            {
                var wide = CellHeights.Wide(dsm);
                if (wantContours) terrain = CellHeights.Terrain(dsm, wide, g.Buildings, g.Landcover);
                if (wantShade)
                {
                    report?.Invoke("shade", 0.3);
                    var zs = CellHeights.ShadeHeights(dsm, wide, g.Buildings, g.Landcover, parallel);
                    foreach (var values in shades)
                    {
                        var (light, flat) = CellHeights.Light(zs, styles.First(s => s.Shade is not null && JsonNode.DeepEquals(ShadeValues(s.Shade), values)).Shade!, parallel);
                        CellFiles.WriteShade(Path.Combine(dir, CellFiles.ShadeName(values)), CellHeights.Quantize(light), (float)flat, values);
                    }
                }
            }
        }
        heights.Clear();
        // the shadings of values no style has any more
        foreach (var path in Directory.EnumerateFiles(dir, "shade*.grid"))
            if (!shades.Any(v => Path.GetFileName(path) == CellFiles.ShadeName(v))) File.Delete(path);
        token.ThrowIfCancellationRequested();
        report?.Invoke("ground", 0.5);
        foreach (var s in styles.Where(s => s.GroundRaster is not null).DistinctBy(s => s.Id))
        {
            var rgba = GroundRaster.Make(g, s, FieldKey(s) is { } key ? fields[key] : null, parallel);
            CellFiles.WriteGround(Path.Combine(dir, CellFiles.GroundName(s.Id)), rgba, area.W, area.H);
        }
        report?.Invoke("layers", 0.6);
        var masks = CellLayers.Masks(g, styles, terrain, Materials.Default, parallel);
        token.ThrowIfCancellationRequested();
        report?.Invoke("outlines", 0.7);
        var layers = CellLayers.Trace(masks, area, parallel);
        CellFiles.WriteLayers(Path.Combine(dir, CellFiles.Layers), area, layers);
        int rings = layers.Sum(l => l.Rings?.Count ?? 0);
        var from = heightFrom.Values.Distinct().ToList();
        var result = new Result(layers.Count, rings, from.Count != 1 ? "mixed" : from[0] switch { "grid" => "from the height data", "scan" => "from the ground scans", _ => "from both" }, Math.Round(sw.Elapsed.TotalSeconds, 1));
        var read = area.Blocks.ToDictionary(b => b.Name, b => InputsOf(folder, b, items));
        var keys = FieldKeys(styles.Where(s => s.GroundRaster is not null)).Distinct().ToList();
        CellFiles.WriteRecord(Path.Combine(dir, CellFiles.Record), new CellRecord(area.Blocks.Select(b => b.Name).ToList(), heightFrom, sets, shades, grounds, layers.Count, rings, result.Seconds,
            read, keys.Count > 0 ? keys.ToDictionary(k => k, k => fields[k].WindowDigest(area)) : null, Outputs(dir)));
        return result;
    }

    /// <summary>The digests of the files a cell's data reads of a block (the height grid only with the height data).</summary>
    public static CellBlockInputs InputsOf(WorkFolder folder, BlockId b, IReadOnlyList<BlockItem> heightItems) =>
        new(ContentDigest.OfFile(LandcoverFile.PathOf(folder.Data, b)), ContentDigest.OfFile(folder.ScanFile(b)),
            heightItems.Contains(BlockItem.Height) ? ContentDigest.OfFile(folder.CaptureHeights(b)) : null);

    /// <summary>The digest of each file a cell's data wrote (the layers, the shadings, the ground pictures) by its name.</summary>
    public static Dictionary<string, string> Outputs(string dir) =>
        new[] { Path.Combine(dir, CellFiles.Layers) }
            .Concat(Directory.EnumerateFiles(dir, "shade-*.grid"))
            .Concat(Directory.EnumerateFiles(dir, "ground-*.png"))
            .Where(File.Exists)
            .ToDictionary(p => Path.GetFileName(p), p => ContentDigest.OfFile(p)!, StringComparer.Ordinal);

    /// <summary>
    /// One digest of the files of a cell's data a map drawn with a style reads (the layers, its shading, its ground
    /// picture), from the cell's record; null when the record lacks one of them. Another style's files changing leave it.
    /// </summary>
    public static string? OutputFor(CellRecord? record, MapStyle style)
    {
        if (record?.Outputs is not { } outputs) return null;
        var names = new List<string> { CellFiles.Layers };
        if (style.Shade is { } sh) names.Add(CellFiles.ShadeName(ShadeValues(sh)));
        if (style.GroundRaster is not null) names.Add(CellFiles.GroundName(style.Id));
        if (names.Any(n => !outputs.ContainsKey(n))) return null;
        return ContentDigest.Of(string.Join('\n', names.Select(n => $"{n}:{outputs[n]}")));
    }

    /// <summary>A block's height grid of <paramref name="from"/> x from values as <paramref name="to"/> x to (cut, or NaN beyond it).</summary>
    internal static float[] Cut(float[] a, int from, int to)
    {
        if (from == to) return a;
        var o = new float[to * to];
        Array.Fill(o, float.NaN);
        for (int r = 0; r < Math.Min(from, to); r++) Array.Copy(a, r * from, o, r * to, Math.Min(from, to));
        return o;
    }
}
