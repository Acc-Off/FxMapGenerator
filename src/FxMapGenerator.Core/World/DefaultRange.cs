using System.Text.Json;

namespace FxMapGenerator.Core.World;

public enum BlockClass { Land, Water }

/// <summary>
/// The bundled default range (data/range-default.json): 674 land blocks and 371 water blocks: those within two blocks of
/// the land, those beyond with sea shallower than 200 m (where the sea bands of the bundled PostalCodeMap-like styles end,
/// so that the bands close into rings) and the three blocks of the aircraft carrier south-east of Los Santos. Every other
/// block of the standard map is open sea; a range's blocks take their class from the range presets
/// first (<see cref="RangePresets.Classify"/>).
/// </summary>
public static class DefaultRange
{
    static readonly Lazy<IReadOnlyDictionary<BlockId, BlockClass>> Table = new(Load);

    public static IReadOnlyDictionary<BlockId, BlockClass> Blocks => Table.Value;

    public static BlockClass ClassOf(BlockId block) => Blocks.TryGetValue(block, out var c) ? c : BlockClass.Water;

    static IReadOnlyDictionary<BlockId, BlockClass> Load()
    {
        using var doc = JsonDocument.Parse(EmbeddedData.Open("range-default.json"));
        var map = new SortedDictionary<BlockId, BlockClass>();
        foreach (var (name, cls) in new[] { ("land", BlockClass.Land), ("water", BlockClass.Water) })
            foreach (var e in doc.RootElement.GetProperty(name).EnumerateArray())
                map.Add(BlockId.Parse(e.GetString()!), cls);
        return map;
    }
}

/// <summary>Files of the repository's data/ folder, embedded into the Core assembly as <c>data/&lt;name&gt;</c>.</summary>
public static class EmbeddedData
{
    public static Stream Open(string name) =>
        typeof(EmbeddedData).Assembly.GetManifestResourceStream("data/" + name) ?? throw new FileNotFoundException("embedded data missing: " + name);

    /// <summary>Names (as <see cref="Open"/> takes them) of the embedded files in a folder such as <c>presets/</c>.</summary>
    public static IEnumerable<string> Names(string folder) =>
        typeof(EmbeddedData).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("data/" + folder, StringComparison.Ordinal))
            .Select(n => n["data/".Length..]);
}
