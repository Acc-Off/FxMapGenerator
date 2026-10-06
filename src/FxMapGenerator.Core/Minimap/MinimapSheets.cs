using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Minimap;

/// <summary>One 4500 m sheet of the minimap: row 0 is the north, column 0 the west.</summary>
public readonly record struct SheetId(int R, int C) : IComparable<SheetId>
{
    public string Name => $"{R}_{C}";

    /// <summary>The texture of the sheet with the sea (DXT5).</summary>
    public string SeaTexture => $"minimap_sea_{R}_{C}";

    /// <summary>The texture of the sheet without the sea (DXT1 with 1-bit alpha; the same picture).</summary>
    public string Texture => $"minimap_{R}_{C}";

    /// <summary>West, north, east and south edges in metres.</summary>
    public (double X0, double Y0, double X1, double Y1) Rect =>
        (WorldGrid.Left + C * MinimapSheets.SheetMetres, WorldGrid.Top - R * MinimapSheets.SheetMetres,
         WorldGrid.Left + (C + 1) * MinimapSheets.SheetMetres, WorldGrid.Top - (R + 1) * MinimapSheets.SheetMetres);

    public int CompareTo(SheetId other) => R != other.R ? R.CompareTo(other.R) : C.CompareTo(other.C);

    public override string ToString() => Name;

    public static bool TryParse(string? s, out SheetId sheet)
    {
        sheet = default;
        var parts = s?.Split('_');
        if (parts is not { Length: 2 } || !int.TryParse(parts[0], out var r) || !int.TryParse(parts[1], out var c)) return false;
        sheet = new SheetId(r, c);
        return r >= 0 && r < MinimapSheets.Rows && c >= 0 && c < MinimapSheets.Cols;
    }

    /// <summary>The sheet that contains the point, or null off the map.</summary>
    public static SheetId? At(double x, double y)
    {
        int c = (int)Math.Floor((x - WorldGrid.Left) / MinimapSheets.SheetMetres), r = (int)Math.Floor((WorldGrid.Top - y) / MinimapSheets.SheetMetres);
        return r >= 0 && r < MinimapSheets.Rows && c >= 0 && c < MinimapSheets.Cols ? new SheetId(r, c) : null;
    }
}

/// <summary>
/// The in-game minimap's pictures: the map frame in 2 x 3 square sheets of 4500 m, the layout the game's minimap uses.
/// A sheet is 4096 px, made of 16 x 16 web tiles of zoom 6. (Twice that, 8192 px from zoom 7, looked no different in the
/// game, and FiveM warns that such DXT5 textures of 64 MiB are oversized and can keep models from loading.)
/// </summary>
public static class MinimapSheets
{
    public const int Rows = 3, Cols = 2;
    public const double SheetMetres = 4500;
    /// <summary>Pixels of a sheet's side.</summary>
    public const int Size = 4096;
    /// <summary>The tile zoom a sheet is made of (<see cref="Size"/> / 256 = 16 tiles a side).</summary>
    public const int Zoom = 6;

    public static IReadOnlyList<SheetId> All { get; } =
        Enumerable.Range(0, Rows).SelectMany(r => Enumerable.Range(0, Cols).Select(c => new SheetId(r, c))).ToList();

    /// <summary>The sheet a cell lies in (a sheet is 16 blocks a side, a cell 8: 2 x 2 cells).</summary>
    public static SheetId OfCell(CellId cell) => new(WorldGrid.FloorDiv(cell.R, 2), WorldGrid.FloorDiv(cell.C, 2));

    /// <summary>The digest of the tiles a sheet is made of (<see cref="Compose"/>; a missing tile counts as missing), so a
    /// sheet is made again only when they changed.</summary>
    public static string SourceDigest(TileStore tiles, SheetId sheet)
    {
        const int n = Size / TileStore.TileSize;
        var paths = new List<string>(n * n);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++) paths.Add(tiles.PathOf(Zoom, n * sheet.C + i, n * sheet.R + j));
        return State.ContentDigest.Of(string.Join(",", paths.Select(p => State.ContentDigest.OfFile(p) ?? "-")));
    }

    /// <summary>
    /// The sheet as straight RGBA, rows from the top, from the tiles of <see cref="Zoom"/> (a tile of that zoom is one
    /// block). A missing tile stays transparent, and so does a block outside <paramref name="only"/> when given. Rows of
    /// tiles are read side by side.
    /// </summary>
    public static byte[] Compose(TileStore tiles, SheetId sheet, IParallelRunner parallel, IReadOnlySet<BlockId>? only = null)
    {
        const int size = Size, z = Zoom, n = size / TileStore.TileSize;
        var rgba = new byte[size * size * 4];
        parallel.ForEach(Enumerable.Range(0, n).ToList(), j =>
        {
            for (int i = 0; i < n; i++)
            {
                if (only is not null && !only.Contains(new BlockId(n * sheet.C + i, n * sheet.R + j))) continue;
                var tile = tiles.Read(z, n * sheet.C + i, n * sheet.R + j);
                if (tile is null) continue;
                for (int row = 0; row < TileStore.TileSize; row++)
                    Buffer.BlockCopy(tile, row * TileStore.TileSize * 4, rgba, ((j * TileStore.TileSize + row) * size + i * TileStore.TileSize) * 4, TileStore.TileSize * 4);
            }
        });
        return rgba;
    }
}
