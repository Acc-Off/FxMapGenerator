using System.Globalization;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Minimap;

/// <summary>
/// The minimap beyond the game's 2 x 3 sheets: every cell of the project's frame outside the standard frame that holds
/// blocks of the range becomes a texture of its own (2048 px for its 2250 m, the sheets' density) that the bundled Extra
/// Map Tiles draws in the game's map by its north-west corner at half a sheet's scale. The cells line up with the sheets
/// (a frame grows by whole cells), so a cell is either inside the standard frame (in a sheet) or wholly outside.
/// </summary>
public static class MinimapExtraTiles
{
    /// <summary>Pixels of a tile's side.</summary>
    public const int Size = MinimapSheets.Size / 2;

    /// <summary>The cells outside the standard frame holding blocks of the range, row by row.</summary>
    public static IReadOnlyList<CellId> Cells(IEnumerable<BlockId> range) =>
        range.Select(b => b.Cell).Where(c => c.R < 0 || c.R >= WorldGrid.CellsY || c.C < 0 || c.C >= WorldGrid.CellsX).Distinct().Order().ToList();

    static string Number(int v) => v < 0 ? "m" + (-v).ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The texture (and its dictionary) of a cell: <c>fxmapgen_extra_&lt;row&gt;_&lt;col&gt;</c>, a minus written as <c>m</c>.</summary>
    public static string Texture(CellId cell) => $"fxmapgen_extra_{Number(cell.R)}_{Number(cell.C)}";

    /// <summary>A cell's north-west corner (game metres).</summary>
    public static (double X, double Y) Corner(CellId cell) =>
        (WorldGrid.Left + cell.C * WorldGrid.CellSize, WorldGrid.Top - cell.R * WorldGrid.CellSize);

    /// <summary>The digest of the zoom 6 tiles a cell's texture is made of (a missing tile counts as missing).</summary>
    public static string SourceDigest(TileStore tiles, CellId cell)
    {
        const int n = WorldGrid.CellBlocks;
        var paths = new List<string>(n * n);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++) paths.Add(tiles.PathOf(MinimapSheets.Zoom, n * cell.C + i, n * cell.R + j));
        return State.ContentDigest.Of(string.Join(",", paths.Select(p => State.ContentDigest.OfFile(p) ?? "-")));
    }

    /// <summary>
    /// The cell's texture as straight RGBA from its 8 x 8 zoom 6 tiles (a missing tile transparent, and so is a block
    /// outside <paramref name="only"/> when given), as <see cref="MinimapSheets.Compose"/> makes a sheet.
    /// </summary>
    public static byte[] Compose(TileStore tiles, CellId cell, IParallelRunner parallel, IReadOnlySet<BlockId>? only = null)
    {
        const int size = Size, n = WorldGrid.CellBlocks, t = TileStore.TileSize;
        var rgba = new byte[size * size * 4];
        parallel.ForEach(Enumerable.Range(0, n).ToList(), j =>
        {
            for (int i = 0; i < n; i++)
            {
                int bx = n * cell.C + i, by = n * cell.R + j;
                if (only is not null && !only.Contains(new BlockId(bx, by))) continue;
                var tile = tiles.Read(MinimapSheets.Zoom, bx, by);
                if (tile is null) continue;
                for (int row = 0; row < t; row++)
                    Buffer.BlockCopy(tile, row * t * 4, rgba, ((j * t + row) * size + i * t) * 4, t * 4);
            }
        });
        return rgba;
    }
}
