using System.Text.Json;

namespace FxMapGenerator.Core.World;

/// <summary>
/// A range preset: the blocks of a map beyond the standard GTA V map with their class, made as the default range is made
/// (its land blocks and the water within two blocks of them, cut at the frame the land needs; Cayo Perico's also the
/// blocks beyond with sea shallower than 200 m, as the default range).
/// </summary>
public sealed class RangePreset
{
    internal RangePreset(string id, IReadOnlyDictionary<BlockId, BlockClass> blocks)
    {
        Id = id;
        Blocks = blocks;
        Frame = MapFrame.Holding(blocks.Keys);
        Own = blocks.Keys.Where(b => !DefaultRange.Blocks.ContainsKey(b)).OrderBy(b => b).ToList();
    }

    public string Id { get; }

    /// <summary>The preset's blocks with their class.</summary>
    public IReadOnlyDictionary<BlockId, BlockClass> Blocks { get; }

    /// <summary>The fewest cells the frame needs on each side to hold every block of the preset.</summary>
    public MapFrame Frame { get; }

    /// <summary>The preset's blocks the default range does not have: a range holding one of them uses the preset's classes.</summary>
    public IReadOnlyList<BlockId> Own { get; }
}

/// <summary>
/// The bundled range presets (data/range-presets.json) the plan map adds to a range at once: Cayo Perico (the land of the
/// game's own map of the island) and Roxwood (an estimate from a minimap resource's pictures).
/// </summary>
public static class RangePresets
{
    static readonly Lazy<IReadOnlyList<RangePreset>> Table = new(Load);

    /// <summary>The presets in the file's order.</summary>
    public static IReadOnlyList<RangePreset> All => Table.Value;

    public static RangePreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The class of each block of a range, row by row: from the presets the range uses (it holds one of their blocks the
    /// default range does not have; a later preset wins where two list a block), else the default range's, else water.
    /// A range of the default range's blocks alone keeps the default range's classes.
    /// </summary>
    public static SortedDictionary<BlockId, BlockClass> Classify(IEnumerable<BlockId> blocks)
    {
        var set = blocks as IReadOnlySet<BlockId> ?? blocks.ToHashSet();
        var used = All.Where(p => p.Own.Any(set.Contains)).ToList();
        var map = new SortedDictionary<BlockId, BlockClass>();
        foreach (var b in set)
        {
            var cls = DefaultRange.ClassOf(b);
            foreach (var p in used)
                if (p.Blocks.TryGetValue(b, out var c)) cls = c;
            map[b] = cls;
        }
        return map;
    }

    static IReadOnlyList<RangePreset> Load()
    {
        using var doc = JsonDocument.Parse(EmbeddedData.Open("range-presets.json"));
        var list = new List<RangePreset>();
        foreach (var p in doc.RootElement.GetProperty("presets").EnumerateArray())
        {
            var blocks = new SortedDictionary<BlockId, BlockClass>();
            foreach (var (name, cls) in new[] { ("land", BlockClass.Land), ("water", BlockClass.Water) })
                foreach (var e in p.GetProperty(name).EnumerateArray())
                    blocks.Add(BlockId.Parse(e.GetString()!), cls);
            list.Add(new RangePreset(p.GetProperty("id").GetString()!, blocks));
        }
        return list;
    }
}
