using System.Text;
using System.Text.Json;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.State;

/// <summary>The five data items a visit can take per block.</summary>
public enum BlockItem { Shot, Height, ScanGround, ScanRoads, ScanCanopy }

public static class BlockItems
{
    public static readonly IReadOnlyList<BlockItem> All = Enum.GetValues<BlockItem>();

    /// <summary>Name in state files and the API: shot, height, scanGround, scanRoads, scanCanopy.</summary>
    public static string Key(this BlockItem item) => JsonNamingPolicy.CamelCase.ConvertName(item.ToString());

    public static bool TryParse(string key, out BlockItem item)
    {
        foreach (var i in All)
            if (i.Key() == key) { item = i; return true; }
        item = default;
        return false;
    }
}

/// <summary>
/// What the work folder holds, kept in <c>state/blocks.json</c> (per block: the time each item was taken, and the retake
/// mark) and <c>state/stages.json</c> (per stage and unit: the time its output was written). A block marked for retake
/// counts as having no items until its next visit clears the mark. Thread-safe; every change is saved at once.
/// </summary>
public sealed class StateStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    readonly object _sync = new();
    readonly WorkFolder _folder;
    readonly Dictionary<string, BlockRecord> _blocks;
    readonly Dictionary<string, Dictionary<string, DateTime>> _stages;

    StateStore(WorkFolder folder, Dictionary<string, BlockRecord> blocks, Dictionary<string, Dictionary<string, DateTime>> stages)
    {
        _folder = folder;
        _blocks = blocks;
        _stages = stages;
    }

    public string BlocksPath => Path.Combine(_folder.State, "blocks.json");
    public string StagesPath => Path.Combine(_folder.State, "stages.json");

    /// <summary>Reads the state of a work folder; missing files mean an empty (new) work folder.</summary>
    public static StateStore Open(WorkFolder folder)
    {
        var blocks = Read<Dictionary<string, BlockRecord>>(Path.Combine(folder.State, "blocks.json")) ?? new();
        var stages = Read<Dictionary<string, Dictionary<string, DateTime>>>(Path.Combine(folder.State, "stages.json")) ?? new();
        return new StateStore(folder, blocks, stages);
    }

    static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Json); }
        catch (JsonException ex) { throw new InvalidDataException($"{path} is damaged: {ex.Message}"); }
    }

    /// <summary>True when the block has the item and is not marked for retake.</summary>
    public bool Has(BlockId block, BlockItem item) => ItemTime(block, item) is not null;

    /// <summary>When the item was taken, or null when missing or the block is marked for retake.</summary>
    public DateTime? ItemTime(BlockId block, BlockItem item)
    {
        lock (_sync)
        {
            if (!_blocks.TryGetValue(block.Name, out var r) || r.Retake) return null;
            return r.Items.TryGetValue(item.Key(), out var t) ? t : null;
        }
    }

    public bool IsMarkedForRetake(BlockId block)
    {
        lock (_sync) return _blocks.TryGetValue(block.Name, out var r) && r.Retake;
    }

    /// <summary>The blocks marked for retake, by name.</summary>
    public IReadOnlyList<BlockId> MarkedForRetake()
    {
        lock (_sync) return _blocks.Where(kv => kv.Value.Retake).Select(kv => BlockId.Parse(kv.Key)).OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
    }

    public void SetItem(BlockId block, BlockItem item, DateTime takenUtc) => SetItems(new[] { (block, item, takenUtc) });

    public void SetItems(IEnumerable<(BlockId Block, BlockItem Item, DateTime TakenUtc)> items)
    {
        lock (_sync)
        {
            foreach (var (block, item, t) in items)
            {
                if (!_blocks.TryGetValue(block.Name, out var r)) _blocks[block.Name] = r = new BlockRecord();
                r.Items[item.Key()] = t.ToUniversalTime();
                r.Captures.Remove(item.Key());            // set from outside a visit (import): the capture is not known
            }
            SaveBlocks();
        }
    }

    /// <summary>
    /// A visit took these items of the block (and <paramref name="dropped"/> are gone: a ground scan taken again without the
    /// canopy probe). On a block marked for retake the items from before go (they are stale: items not taken now count as
    /// missing again) and the mark is cleared. <paramref name="capture"/> = the resource's capture number (what its capture
    /// takes), kept per item so that a later change of the capture can tell the blocks it affects; null = not known.
    /// </summary>
    public void TakeItems(BlockId block, IEnumerable<BlockItem> items, DateTime takenUtc, IEnumerable<BlockItem>? dropped = null, int? capture = null)
    {
        lock (_sync)
        {
            if (!_blocks.TryGetValue(block.Name, out var r)) _blocks[block.Name] = r = new BlockRecord();
            if (r.Retake)
            {
                r.Items.Clear();
                r.Captures.Clear();
                r.Retake = false;
            }
            foreach (var item in dropped ?? [])
            {
                r.Items.Remove(item.Key());
                r.Captures.Remove(item.Key());
            }
            foreach (var item in items)
            {
                r.Items[item.Key()] = takenUtc.ToUniversalTime();
                if (capture is { } c) r.Captures[item.Key()] = c;
                else r.Captures.Remove(item.Key());
            }
            SaveBlocks();
        }
    }

    /// <summary>The resource's capture number the item was taken with; null when missing, not known (imported data) or the
    /// block is marked for retake.</summary>
    public int? ItemCapture(BlockId block, BlockItem item)
    {
        lock (_sync)
        {
            if (!_blocks.TryGetValue(block.Name, out var r) || r.Retake || !r.Items.ContainsKey(item.Key())) return null;
            return r.Captures.TryGetValue(item.Key(), out var c) ? c : null;
        }
    }

    /// <summary>Marks blocks so that all their items count as missing (a partial update after a map change).</summary>
    public void MarkForRetake(IEnumerable<BlockId> blocks, bool retake = true)
    {
        lock (_sync)
        {
            foreach (var b in blocks)
            {
                if (!_blocks.TryGetValue(b.Name, out var r))
                {
                    if (!retake) continue;
                    _blocks[b.Name] = r = new BlockRecord();
                }
                r.Retake = retake;
            }
            SaveBlocks();
        }
    }

    /// <summary>When the stage last wrote the output of this unit (a block name, cell name, set id or "world").</summary>
    public DateTime? StageDone(string stage, string unit)
    {
        lock (_sync) return _stages.TryGetValue(stage, out var units) && units.TryGetValue(unit, out var t) ? t : null;
    }

    public void SetStageDone(string stage, IEnumerable<string> units, DateTime doneUtc)
    {
        lock (_sync)
        {
            if (!_stages.TryGetValue(stage, out var map)) _stages[stage] = map = new Dictionary<string, DateTime>();
            foreach (var u in units) map[u] = doneUtc.ToUniversalTime();
            SaveStages();
        }
    }

    public void SetStageDone(string stage, string unit, DateTime doneUtc) => SetStageDone(stage, new[] { unit }, doneUtc);

    /// <summary>Forgets the records of a stage (all units), so every unit counts as not done.</summary>
    public void ClearStage(string stage)
    {
        lock (_sync)
        {
            if (!_stages.Remove(stage)) return;
            SaveStages();
        }
    }

    /// <summary>The newest record of a stage over the given units, or null when none has one.</summary>
    public DateTime? NewestStageDone(string stage, IEnumerable<string> units)
    {
        lock (_sync)
        {
            if (!_stages.TryGetValue(stage, out var map)) return null;
            DateTime? newest = null;
            foreach (var u in units)
                if (map.TryGetValue(u, out var t) && (newest is null || t > newest)) newest = t;
            return newest;
        }
    }

    void SaveBlocks() => Write(BlocksPath, _blocks);
    void SaveStages() => Write(StagesPath, _stages);

    static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json), Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    public sealed class BlockRecord
    {
        /// <summary>Item key -> time taken (UTC).</summary>
        public Dictionary<string, DateTime> Items { get; set; } = new();
        /// <summary>Item key -> the resource's capture number it was taken with (items without one: not known).</summary>
        public Dictionary<string, int> Captures { get; set; } = new();
        public bool Retake { get; set; }
    }
}
