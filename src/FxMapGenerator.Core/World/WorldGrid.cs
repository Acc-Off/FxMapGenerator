using System.Globalization;
using System.Text.RegularExpressions;

namespace FxMapGenerator.Core.World;

/// <summary>
/// The map's grid, the same as the postal-code map tiles: game metres, x to the east, y to the north. The standard frame
/// covers x -4140..4860 and y 8400..-5100; a project may add whole cells around it (<see cref="MapFrame"/>), but the origin
/// of every number stays this frame's north-west corner. Captures and scans work on blocks of 4 x 4 z8 tiles (281.25 m);
/// drawing works on cells of 8 x 8 blocks plus a one-block margin.
/// </summary>
public static class WorldGrid
{
    /// <summary>The standard frame's edges; <see cref="Left"/> and <see cref="Top"/> are the origin of the block, cell and tile numbers.</summary>
    public const double Left = -4140, Top = 8400, Right = 4860, Bottom = -5100;
    /// <summary>Zoom level of the blocks: a block is 4 x 4 tiles of this level.</summary>
    public const int Zoom = 8;
    /// <summary>Metres per z8 tile (256 px, so 3.64 px per metre).</summary>
    public const double TileSize = 70.3125;
    public const double BlockSize = 4 * TileSize;
    /// <summary>Blocks of the standard frame.</summary>
    public const int BlocksX = 32, BlocksY = 48;
    public const int CellBlocks = 8, CellMargin = 1;
    /// <summary>Metres of a cell's side (2250).</summary>
    public const double CellSize = CellBlocks * BlockSize;
    /// <summary>Cells of the standard frame.</summary>
    public const int CellsX = BlocksX / CellBlocks, CellsY = BlocksY / CellBlocks;
    /// <summary>
    /// The most cells a project adds above and below together, and left and right together: the frame then fits the one
    /// tile of zoom 0 (18000 m) of the web tiles written out.
    /// </summary>
    public const int MaxCellsVertical = 2, MaxCellsHorizontal = 4;

    /// <summary>Integer division rounded down (toward minus infinity), as the grid's numbers need west and north of the origin.</summary>
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        return a % b != 0 && (a < 0) != (b < 0) ? q - 1 : q;
    }
}

/// <summary>
/// A project's map frame: the standard frame of <see cref="WorldGrid"/> with whole cells added above, below, to the left
/// and to the right. The origin stays the standard frame's north-west corner, so the blocks, cells and tiles added above
/// or to the left of it have negative numbers.
/// </summary>
public readonly record struct MapFrame(int CellsTop, int CellsBottom, int CellsLeft, int CellsRight)
{
    /// <summary>The standard frame: nothing added.</summary>
    public static MapFrame Standard => default;

    /// <summary>The frame every project's frame lies in: the most cells added on every side at once.</summary>
    public static MapFrame Outer => new(WorldGrid.MaxCellsVertical, WorldGrid.MaxCellsVertical, WorldGrid.MaxCellsHorizontal, WorldGrid.MaxCellsHorizontal);

    public bool IsStandard => this == Standard;

    /// <summary>
    /// The smallest frame holding the blocks: on each side the fewest cells that bring them all inside (the standard frame
    /// when they all lie in it). Blocks far off the map give more cells than a frame may add (<see cref="Problems"/>).
    /// </summary>
    public static MapFrame Holding(IEnumerable<BlockId> blocks)
    {
        static int Cells(int blocksOut) => (blocksOut + WorldGrid.CellBlocks - 1) / WorldGrid.CellBlocks;
        int top = 0, bottom = 0, left = 0, right = 0;
        foreach (var b in blocks)
        {
            if (b.By < 0) top = Math.Max(top, Cells(-b.By));
            if (b.By >= WorldGrid.BlocksY) bottom = Math.Max(bottom, Cells(b.By - WorldGrid.BlocksY + 1));
            if (b.Bx < 0) left = Math.Max(left, Cells(-b.Bx));
            if (b.Bx >= WorldGrid.BlocksX) right = Math.Max(right, Cells(b.Bx - WorldGrid.BlocksX + 1));
        }
        return new MapFrame(top, bottom, left, right);
    }

    /// <summary>What makes the cells added unusable, in plain sentences (none: fine).</summary>
    public IEnumerable<string> Problems()
    {
        if (CellsTop < 0 || CellsBottom < 0 || CellsLeft < 0 || CellsRight < 0) yield return "the cells added around the map must be 0 or more";
        else
        {
            if (CellsTop + CellsBottom > WorldGrid.MaxCellsVertical) yield return $"at most {WorldGrid.MaxCellsVertical} cells can be added above and below the map together";
            if (CellsLeft + CellsRight > WorldGrid.MaxCellsHorizontal) yield return $"at most {WorldGrid.MaxCellsHorizontal} cells can be added left and right of the map together";
        }
    }

    /// <summary>The first block column and row (negative when cells are added to the left or above).</summary>
    public int Bx0 => -CellsLeft * WorldGrid.CellBlocks;
    public int By0 => -CellsTop * WorldGrid.CellBlocks;
    public int BlocksX => WorldGrid.BlocksX + (CellsLeft + CellsRight) * WorldGrid.CellBlocks;
    public int BlocksY => WorldGrid.BlocksY + (CellsTop + CellsBottom) * WorldGrid.CellBlocks;
    /// <summary>The block column and row just past the last ones.</summary>
    public int Bx1 => Bx0 + BlocksX;
    public int By1 => By0 + BlocksY;

    /// <summary>West, north, east and south edges in metres.</summary>
    public double X0 => WorldGrid.Left + Bx0 * WorldGrid.BlockSize;
    public double Y0 => WorldGrid.Top - By0 * WorldGrid.BlockSize;
    public double X1 => WorldGrid.Left + Bx1 * WorldGrid.BlockSize;
    public double Y1 => WorldGrid.Top - By1 * WorldGrid.BlockSize;

    public bool Contains(BlockId b) => b.Bx >= Bx0 && b.Bx < Bx1 && b.By >= By0 && b.By < By1;

    /// <summary>A point (metres) inside the frame or on its edge.</summary>
    public bool Contains(double x, double y) => x >= X0 && x <= X1 && y <= Y0 && y >= Y1;

    /// <summary>
    /// The tiles of a zoom level (at most <see cref="WorldGrid.Zoom"/>) the frame covers: columns <c>X0</c> to <c>X1 - 1</c>,
    /// rows <c>Y0</c> to <c>Y1 - 1</c> (a tile only partly inside counts).
    /// </summary>
    public (int X0, int Y0, int X1, int Y1) Tiles(int z)
    {
        int k = WorldGrid.Zoom - z, d = (1 << k) - 1;
        return (4 * Bx0 >> k, 4 * By0 >> k, (4 * Bx1 + d) >> k, (4 * By1 + d) >> k);
    }

    /// <summary>The frame's blocks, row by row from the north-west corner.</summary>
    public IEnumerable<BlockId> Blocks
    {
        get
        {
            for (int by = By0; by < By1; by++)
                for (int bx = Bx0; bx < Bx1; bx++) yield return new BlockId(bx, by);
        }
    }

    /// <summary>The frame's cells, row by row.</summary>
    public IEnumerable<CellId> Cells
    {
        get
        {
            for (int r = -CellsTop; r < WorldGrid.CellsY + CellsBottom; r++)
                for (int c = -CellsLeft; c < WorldGrid.CellsX + CellsRight; c++) yield return new CellId(r, c);
        }
    }

    /// <summary>The blocks of the frame that the rectangle (metres, corners in any order) touches, row by row.</summary>
    public IReadOnlyList<BlockId> Touching(double x0, double y0, double x1, double y1)
    {
        var a = BlockId.At(Math.Min(x0, x1), Math.Max(y0, y1));
        var b = BlockId.At(Math.Max(x0, x1), Math.Min(y0, y1));
        var list = new List<BlockId>();
        for (int by = Math.Max(By0, a.By); by <= Math.Min(By1 - 1, b.By); by++)
            for (int bx = Math.Max(Bx0, a.Bx); bx <= Math.Min(Bx1 - 1, b.Bx); bx++)
                list.Add(new BlockId(bx, by));
        return list;
    }
}

/// <summary>
/// One z8 block, named <c>z8_&lt;tx&gt;_&lt;ty&gt;</c> after its top-left tile (tx = 4 bx, ty = 4 by). The numbers are
/// negative west and north of the standard frame (<see cref="MapFrame"/>).
/// </summary>
public readonly partial record struct BlockId(int Bx, int By) : IComparable<BlockId>
{
    public int Tx => Bx * 4;
    public int Ty => By * 4;
    public string Name => string.Create(CultureInfo.InvariantCulture, $"z8_{Tx}_{Ty}");

    /// <summary>West, north, east and south edges in metres.</summary>
    public (double X0, double Y0, double X1, double Y1) Rect =>
        (WorldGrid.Left + Bx * WorldGrid.BlockSize, WorldGrid.Top - By * WorldGrid.BlockSize,
         WorldGrid.Left + (Bx + 1) * WorldGrid.BlockSize, WorldGrid.Top - (By + 1) * WorldGrid.BlockSize);

    public (double X, double Y) Center =>
        (WorldGrid.Left + (Bx + 0.5) * WorldGrid.BlockSize, WorldGrid.Top - (By + 0.5) * WorldGrid.BlockSize);

    public CellId Cell => new(WorldGrid.FloorDiv(By, WorldGrid.CellBlocks), WorldGrid.FloorDiv(Bx, WorldGrid.CellBlocks));

    /// <summary>The block that contains the point (it may lie off a project's frame; check <see cref="MapFrame.Contains(BlockId)"/>).</summary>
    public static BlockId At(double x, double y) =>
        new((int)Math.Floor((x - WorldGrid.Left) / WorldGrid.BlockSize), (int)Math.Floor((WorldGrid.Top - y) / WorldGrid.BlockSize));

    /// <summary>A block name of a block in <see cref="MapFrame.Outer"/> (whether it is in a project's frame is the project's to check).</summary>
    public static bool TryParse(string? s, out BlockId block)
    {
        block = default;
        var m = s is null ? null : NameRegex().Match(s.Trim());
        if (m is null || !m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int tx)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int ty)) return false;
        if (tx % 4 != 0 || ty % 4 != 0) return false;
        block = new BlockId(tx / 4, ty / 4);
        return MapFrame.Outer.Contains(block);
    }

    public static BlockId Parse(string s) => TryParse(s, out var b) ? b : throw new FormatException($"not a block name: '{s}'");

    /// <summary>Row by row from the north-west corner.</summary>
    public int CompareTo(BlockId other) => By != other.By ? By.CompareTo(other.By) : Bx.CompareTo(other.Bx);

    public override string ToString() => Name;

    [GeneratedRegex(@"^z8_(-?\d+)_(-?\d+)$")]
    private static partial Regex NameRegex();
}

/// <summary>One drawing cell, named <c>cell_&lt;row&gt;_&lt;col&gt;</c>: blocks by 8r..8r+7 and bx 8c..8c+7 (negative west and north of the standard frame).</summary>
public readonly record struct CellId(int R, int C) : IComparable<CellId>
{
    public string Name => string.Create(CultureInfo.InvariantCulture, $"cell_{R}_{C}");
    public int CompareTo(CellId other) => R != other.R ? R.CompareTo(other.R) : C.CompareTo(other.C);
    public override string ToString() => Name;

    /// <summary>A cell name of a cell in <see cref="MapFrame.Outer"/>.</summary>
    public static bool TryParse(string? s, out CellId cell)
    {
        cell = default;
        var parts = s?.Split('_');
        if (parts is not { Length: 3 } || parts[0] != "cell"
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r)
            || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var c)) return false;
        cell = new CellId(r, c);
        var outer = MapFrame.Outer;
        return r >= -outer.CellsTop && r < WorldGrid.CellsY + outer.CellsBottom && c >= -outer.CellsLeft && c < WorldGrid.CellsX + outer.CellsRight;
    }
}

/// <summary>A cell of a range: its own (core) blocks and the range blocks in the one-block ring around it.</summary>
public sealed record Cell(CellId Id, IReadOnlyList<BlockId> Core, IReadOnlyList<BlockId> Margin)
{
    public IEnumerable<BlockId> All => Core.Concat(Margin);
}

public static class CellPlan
{
    /// <summary>The cells that own at least one block of the range. Blocks outside the range stay out of every cell.</summary>
    public static IReadOnlyList<Cell> For(IEnumerable<BlockId> range)
    {
        var have = range.ToHashSet();
        var cells = new List<Cell>();
        foreach (var group in have.GroupBy(b => b.Cell).OrderBy(g => g.Key))
        {
            var id = group.Key;
            int bx0 = id.C * WorldGrid.CellBlocks, by0 = id.R * WorldGrid.CellBlocks;
            int bx1 = bx0 + WorldGrid.CellBlocks - 1, by1 = by0 + WorldGrid.CellBlocks - 1;
            var margin = new List<BlockId>();
            for (int by = by0 - WorldGrid.CellMargin; by <= by1 + WorldGrid.CellMargin; by++)
                for (int bx = bx0 - WorldGrid.CellMargin; bx <= bx1 + WorldGrid.CellMargin; bx++)
                {
                    var b = new BlockId(bx, by);
                    bool inside = bx >= bx0 && bx <= bx1 && by >= by0 && by <= by1;
                    if (!inside && have.Contains(b)) margin.Add(b);
                }
            cells.Add(new Cell(id, group.OrderBy(b => b).ToList(), margin));
        }
        return cells;
    }
}
