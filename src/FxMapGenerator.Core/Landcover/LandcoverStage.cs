using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Landcover;

/// <summary>
/// The landcover, one unit per block (<see cref="LandcoverBuilder"/>): reads the block's scans (ground and roads) and
/// heights (the project's height quality: the ground scan, or with quality the higher of it and the height data per
/// point, <see cref="SurfaceHeights.LandcoverItems"/>), the ground scans and heights of its 8 neighbours that have them
/// (in the range or not), and the road graph; writes <c>data/landcover/&lt;block&gt;.grid</c> (<see cref="LandcoverFile"/>).
/// Runs after the road graph. A block waits while it or a neighbour in the range lacks its data from the game; it is made
/// again when one of those inputs is newer than it, or the file is gone. <c>data/landcover-record.json</c> holds the
/// heights the landcover was made with (<see cref="SurfaceHeights.NameOf"/>) and the digest of the road graph it read;
/// when the height quality asks for other heights (or the record is missing), or the road graph's contents changed (a
/// road graph made again with the same contents does not count), every block is made again.
/// </summary>
public sealed class LandcoverStage : Stage
{
    /// <summary>Block surfaces kept for the neighbours' units (units go in row order): five rows of blocks.</summary>
    const int CacheBlocks = 5 * WorldGrid.BlocksX;

    public const string Record = "landcover-record.json";

    GarageRule.Roads _roads = null!;
    SurfaceCache _cache = null!;
    IReadOnlyList<BlockItem> _items = [BlockItem.ScanGround];
    long _blocks, _cells, _buildings, _water, _garage, _rockOff, _garageOff;

    public override string Id => "landcover";
    public override string Row => "mapData.landcover";
    public override string? RecordKey => StageKeys.Landcover;
    public override string UnitName => "block";
    /// <summary>The (3n)² grids of one block's terrain and parking rule, about 50 MB, with room to spare.</summary>
    public override long MemoryPerUnit => 128L << 20;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        var (ready, waiting) = Left(ctx.Project, ctx.State);
        if (ready.Count + waiting.Count == 0)
        {
            ctx.Log("landcover: up to date");
            return [];
        }
        if (RoadGraphStage.IsStale(ctx.Project, ctx.State))
        {
            ctx.Log("landcover: waiting for the road graph (the parking rule reads it)");
            return [];
        }
        if (waiting.Count > 0)
            ctx.Log($"landcover: {waiting.Count} blocks wait for their own or a neighbour's heights or ground scan");
        _roads = GarageRule.Roads.Read(Path.Combine(ctx.Folder.Data, RoadGraphFile.Roads));
        _items = SurfaceHeights.LandcoverItems(ctx.Project);
        var heights = SurfaceHeights.NameOf(_items);
        var record = ReadRecord(ctx.Folder);
        var graph = RoadGraphDigest(ctx.Folder);
        if (record?.Heights != heights && ready.Count > 0)
        {
            // other heights than the landcover was made with: every block is made again (those waiting when they can)
            ctx.Log($"landcover: made with {record?.Heights ?? "unrecorded"} heights, now {heights}: every block again");
            ctx.State.ClearStage(StageKeys.Landcover);
            WriteRecord(ctx.Folder, heights, graph);
        }
        else if (record is not null && graph is not null && record.RoadGraph != graph)
        {
            // another road graph than the blocks were made with (a road graph made again with the same contents is not):
            // every block again. Without a digest in the record, a road graph newer than a block counts as another one.
            var made = ctx.State.StageDone(StageKeys.RoadGraph, StageKeys.World);
            bool other = record.RoadGraph is not null
                || ctx.Project.Range.Keys.Any(b => ctx.State.StageDone(StageKeys.Landcover, b.Name) is { } d && d < made);
            if (other)
            {
                ctx.Log("landcover: the road graph changed (the parking rule reads it): every block again");
                ctx.State.ClearStage(StageKeys.Landcover);
            }
            WriteRecord(ctx.Folder, heights, graph);
        }
        _cache = new SurfaceCache(ctx.Folder, CacheBlocks, _items);
        _blocks = _cells = _buildings = _water = _garage = _rockOff = _garageOff = 0;
        Directory.CreateDirectory(Path.Combine(ctx.Folder.Data, LandcoverFile.Folder));
        ctx.Log($"landcover: {ready.Count} blocks to make (heights: {SurfaceHeights.NameOf(_items)}; {_roads.Count} road lines for the parking rule)");
        return ready.Select(b => b.Name).ToList();
    }

    public override void Run(UnitContext ctx)
    {
        var b = BlockId.Parse(ctx.Unit);
        ctx.Report("load", 0);
        var centre = _cache.Get(b);
        var around = new Dictionary<(int, int), BlockSurface>();
        foreach (var (di, dj, nb) in Neighbours(b))
            if (Usable(ctx.State, nb, _items)) around[(di, dj)] = _cache.Get(nb);
        ctx.Token.ThrowIfCancellationRequested();
        ctx.Report("landcover", 0.3);
        var r = LandcoverBuilder.Compute(centre, around, _roads, Materials.Default);
        ctx.Report("write", 0.9);
        LandcoverFile.Write(LandcoverFile.PathOf(ctx.Folder.Data, b), centre, r);
        Interlocked.Increment(ref _blocks);
        Interlocked.Add(ref _cells, r.Landcover.Count);
        Interlocked.Add(ref _buildings, r.Buildings.Data.Count(x => x));
        Interlocked.Add(ref _water, r.Water.Data.Count(x => x));
        Interlocked.Add(ref _garage, r.GarageCells);
        Interlocked.Add(ref _rockOff, r.RockCells);
        Interlocked.Add(ref _garageOff, r.GarageOffCells);
    }

    public override void Finish(StageContext ctx, StageEnd end)
    {
        if (_blocks == 0) return;
        double Pct(long x) => 100.0 * x / _cells;
        ctx.Log($"landcover: {_blocks} blocks made; buildings {Pct(_buildings):0.0} % of their cells (parking structures {_garage / 1e6:0.00} km²), water {Pct(_water):0.0} %;"
            + $" taken off: rock faces {_rockOff} cells, parking pieces on a slope or over water {_garageOff} cells");
    }

    public override int CountReady(Project project, StateStore state) => Left(project, state).Ready.Count;

    /// <summary>The 8 blocks around, by (rows south, columns east).</summary>
    static IEnumerable<(int Di, int Dj, BlockId Block)> Neighbours(BlockId b)
    {
        for (int di = -1; di <= 1; di++)
            for (int dj = -1; dj <= 1; dj++)
                if (di != 0 || dj != 0) yield return (di, dj, new BlockId(b.Bx + dj, b.By + di));
    }

    /// <summary>A block the landcover can read around another: it has its height items and a ground scan.</summary>
    static bool Usable(StateStore state, BlockId b, IReadOnlyList<BlockItem> items) => items.All(i => state.Has(b, i)) && state.Has(b, BlockItem.ScanGround);

    /// <summary>
    /// The blocks of the range whose landcover is to be made, in row order: those that can be made now, and those waiting
    /// for their own data (heights, ground and road scans) or a neighbour's in the range (heights and ground scan). With
    /// other heights than the record's, every block that can be made. Without <paramref name="withRoadGraph"/> another road
    /// graph does not count: the blocks whose landcover is expected to change (made again for another road graph alone, a
    /// block comes out the same but near the roads that changed).
    /// </summary>
    public static (List<BlockId> Ready, List<BlockId> Waiting) Left(Project project, StateStore state, bool withRoadGraph = true)
    {
        var range = project.Range;
        var folder = new WorkFolder(project.WorkFolderPath);
        var roadGraph = state.StageDone(StageKeys.RoadGraph, StageKeys.World);
        var items = SurfaceHeights.LandcoverItems(project);
        var record = ReadRecord(folder);
        bool other = record?.Heights != SurfaceHeights.NameOf(items);
        bool? graphChanged = withRoadGraph ? GraphChanged(folder, record) : false;
        var ready = new List<BlockId>();
        var waiting = new List<BlockId>();
        foreach (var b in range.Keys.OrderBy(b => b))
        {
            bool own = Usable(state, b, items) && state.Has(b, BlockItem.ScanRoads);
            if (!own || Neighbours(b).Any(x => range.ContainsKey(x.Block) && !Usable(state, x.Block, items))) { waiting.Add(b); continue; }
            if (other || IsStale(folder, state, b, roadGraph, items, graphChanged)) ready.Add(b);
        }
        return (ready, waiting);
    }

    /// <summary>The digest of the road graph (<c>data/roads.json</c>), or null when there is none.</summary>
    public static string? RoadGraphDigest(WorkFolder folder) => ContentDigest.OfFile(Path.Combine(folder.Data, RoadGraphFile.Roads));

    /// <summary>Whether the road graph differs from the one the landcover was made with; null when the record holds no digest
    /// (then a road graph newer than a block counts as another one) or there is no road graph.</summary>
    public static bool? GraphChanged(WorkFolder folder, LandcoverRecord? record) =>
        record?.RoadGraph is { } made && RoadGraphDigest(folder) is { } now ? made != now : null;

    /// <summary>
    /// True when the block's landcover was never made, its file is gone, the block's height items (default: the ground
    /// scan), ground or road scan, or a neighbour's height items or ground scan are newer than it, or the road graph is
    /// another one (<paramref name="graphChanged"/>; when not known, a road graph newer than the block).
    /// </summary>
    public static bool IsStale(WorkFolder folder, StateStore state, BlockId b, DateTime? roadGraph, IReadOnlyList<BlockItem>? items = null, bool? graphChanged = null)
    {
        items ??= [BlockItem.ScanGround];
        if (state.StageDone(StageKeys.Landcover, b.Name) is not { } done) return true;
        if (!File.Exists(LandcoverFile.PathOf(folder.Data, b))) return true;
        if (graphChanged ?? (roadGraph is { } g && g > done)) return true;
        foreach (var item in items.Append(BlockItem.ScanGround).Append(BlockItem.ScanRoads))
            if (state.ItemTime(b, item) is { } t && t > done) return true;
        foreach (var (_, _, nb) in Neighbours(b))
            if (Usable(state, nb, items) && items.Append(BlockItem.ScanGround).Any(i => state.ItemTime(nb, i) > done)) return true;
        return false;
    }

    /// <summary><c>data/landcover-record.json</c>: the heights the landcover was made with (<c>scan</c>, <c>both</c>) and the
    /// digest of the road graph it read (<see cref="ContentDigest"/>; null in records made before it was kept).</summary>
    public sealed record LandcoverRecord(string Heights, string? RoadGraph = null);

    public static void WriteRecord(WorkFolder folder, string heights, string? roadGraph = null)
    {
        Directory.CreateDirectory(folder.Data);
        GameFilesOutput.WriteAtomically(Path.Combine(folder.Data, Record), fs => JsonSerializer.Serialize(fs, new LandcoverRecord(heights, roadGraph), Project.Json));
    }

    public static LandcoverRecord? ReadRecord(WorkFolder folder)
    {
        var path = Path.Combine(folder.Data, Record);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LandcoverRecord>(File.ReadAllText(path), Project.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Block surfaces read once for a block and its neighbours, the least recently used dropped beyond the capacity.</summary>
    sealed class SurfaceCache(WorkFolder folder, int capacity, IReadOnlyList<BlockItem> items)
    {
        readonly object _lock = new();
        readonly Dictionary<BlockId, (Lazy<BlockSurface> Value, long Used)> _map = new();
        long _clock;

        public BlockSurface Get(BlockId b)
        {
            Lazy<BlockSurface> lazy;
            lock (_lock)
            {
                if (_map.TryGetValue(b, out var e)) lazy = e.Value;
                else
                {
                    lazy = new Lazy<BlockSurface>(() => Load(b), LazyThreadSafetyMode.ExecutionAndPublication);
                    if (_map.Count >= capacity) _map.Remove(_map.MinBy(kv => kv.Value.Used).Key);
                }
                _map[b] = (lazy, ++_clock);
            }
            return lazy.Value;
        }

        BlockSurface Load(BlockId b)
        {
            var scan = ScanFile.Read(folder.ScanFile(b));
            var heights = BlockSurface.HeightsOf(SurfaceHeights.Of(folder, b, items, scan));
            return BlockSurface.From(b, scan, heights, Materials.Default);
        }
    }
}
