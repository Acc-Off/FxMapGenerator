using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.World;
using FxMapGenerator.GameData.Archives;
using FxMapGenerator.GameData.Gta;
using FxMapGenerator.GameData.Scaleform;

namespace FxMapGenerator.Core.Export;

/// <summary>
/// The Cayo Perico island map: the Scaleform file the game draws the island with on the minimap and the pause map
/// (<c>int3232302352.gfx</c>; 3232302352 is the joaat of <c>h4_fake_islandx</c>, the interior the island's scripts show
/// every frame). The exported resource carries the user's own game file with its four shapes drawing nothing, so the
/// island shows as the map's own pictures beneath it. The game's file is read from the user's game when the export
/// writes it and never goes anywhere else.
/// </summary>
public static class IslandMap
{
    public const string FileName = "int3232302352.gfx";

    /// <summary>Where the game keeps it (the only copy; later archives would win).</summary>
    public const string GamePath = @"update\update.rpf\x64\patch\data\cdimages\scaleform_minimap.rpf\int3232302352.gfx";

    /// <summary>Its shapes: the shallows (1), the land with its roads and runway (3), two small faces (5, 7).</summary>
    public static readonly IReadOnlySet<int> Shapes = new HashSet<int> { 1, 3, 5, 7 };

    /// <summary>The island map with nothing drawn, from the game of <paramref name="where"/> (its GTA V and key folders).</summary>
    /// <exception cref="InvalidOperationException">No GTA V or no keys, or the game has no such file.</exception>
    public static byte[] Make(GameFilesLocation where, CancellationToken token = default)
    {
        if (!where.GtaFound) throw new InvalidOperationException("the island map is read from GTA V's files, and GTA V was not found on this PC");
        if (!where.KeysFound) throw new InvalidOperationException("the island map is read from GTA V's files, and the RPF keys were not found");
        GtaKeys.Install(where.KeysFolder!);
        using var ix = RpfIndex.Open(where.GtaFolder!, n => n.Equals(FileName, StringComparison.OrdinalIgnoreCase), token);
        var entry = ix.Entries.Where(e => e.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase)).MaxBy(e => e.Order)
            ?? throw new InvalidOperationException($"the game has no {FileName} ({GamePath})");
        return GfxFile.EmptyShapes(RpfIndex.Read(entry.File), Shapes);
    }

    /// <summary>
    /// The land blocks of Cayo Perico (the range preset's) the range does not hold: with the island map drawing nothing,
    /// the pause map shows no island where the map has none.
    /// </summary>
    public static int LandMissing(IReadOnlyDictionary<BlockId, BlockClass> range) =>
        RangePresets.Find("cayoPerico") is { } cayo ? cayo.Blocks.Count(kv => kv.Value == BlockClass.Land && !range.ContainsKey(kv.Key)) : 0;
}
