using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Cells;

/// <summary>
/// A cell's blocks (its own and the range blocks in the ring around it) as one regular block grid. Its 1 m grid has a
/// node per metre from (Gx0, Gy0), the grid's north-west corner moved by at most 0.5 m onto the world-wide lattice of
/// fractional part 0.5: node (r, c) stands for the cell [x - 0.5, x + 0.5] around (Gx0 + c, Gy0 - r), so the grids of
/// neighbouring cells line up. The pictures are drawn in the block rectangle (X0, Y0, X0 + W, Y0 - H).
/// </summary>
public sealed class CellArea
{
    public CellId Id { get; }
    public IReadOnlyList<BlockId> Blocks { get; }
    public int Tx0 { get; }
    public int Ty0 { get; }
    public int Nbx { get; }
    public int Nby { get; }
    public double X0 { get; }
    public double Y0 { get; }
    /// <summary>The block rectangle's width and height (m).</summary>
    public double RectW => Nbx * WorldGrid.BlockSize;
    public double RectH => Nby * WorldGrid.BlockSize;
    /// <summary>Nodes of the 1 m grid across and down (the rectangle's size rounded half to even).</summary>
    public int W { get; }
    public int H { get; }
    public double Gx0 { get; }
    public double Gy0 { get; }
    readonly bool[] _present;

    public CellArea(Cell cell) : this(cell.Id, cell.All.ToList()) { }

    public CellArea(CellId id, IReadOnlyList<BlockId> blocks)
    {
        if (blocks.Count == 0) throw new ArgumentException("a cell without blocks");
        Id = id;
        Blocks = blocks.OrderBy(b => b).ToList();
        Tx0 = blocks.Min(b => b.Tx);
        Ty0 = blocks.Min(b => b.Ty);
        Nbx = (blocks.Max(b => b.Tx) - Tx0) / 4 + 1;
        Nby = (blocks.Max(b => b.Ty) - Ty0) / 4 + 1;
        X0 = WorldGrid.Left + Tx0 * WorldGrid.TileSize;
        Y0 = WorldGrid.Top - Ty0 * WorldGrid.TileSize;
        W = (int)Math.Round(RectW, MidpointRounding.ToEven);
        H = (int)Math.Round(RectH, MidpointRounding.ToEven);
        Gx0 = ScanArea.Lattice(X0);
        Gy0 = ScanArea.Lattice(Y0);
        _present = new bool[Nbx * Nby];
        foreach (var b in blocks) _present[(b.Ty - Ty0) / 4 * Nbx + (b.Tx - Tx0) / 4] = true;
    }

    CellArea(int w, int h) : this(new CellId(0, 0), [new BlockId(0, 0)])
    {
        W = w;
        H = h;
    }

    /// <summary>An area of one block whose 1 m grid is <paramref name="w"/> x <paramref name="h"/> nodes (for tests of the grid steps).</summary>
    internal static CellArea ForGrid(int w, int h) => new(w, h);

    public bool Present(int bx, int by) => _present[by * Nbx + bx];

    /// <summary>The block (column, row in this grid) and the sample of an n x n grid every step m nearest to a world point (a tie rounds up); Ok = inside a block of the cell.</summary>
    public (int Bx, int By, int Row, int Col, bool Ok) Index(double x, double y, double step, int n)
    {
        int ix = (int)Math.Floor((x - X0) / WorldGrid.BlockSize);
        int iy = (int)Math.Floor((Y0 - y) / WorldGrid.BlockSize);
        bool ok = ix >= 0 && ix < Nbx && iy >= 0 && iy < Nby;
        int ixc = Math.Clamp(ix, 0, Nbx - 1), iyc = Math.Clamp(iy, 0, Nby - 1);
        ok &= _present[iyc * Nbx + ixc];
        int c = Math.Clamp((int)Math.Floor((x - (X0 + ixc * WorldGrid.BlockSize)) / step + 0.5), 0, n - 1);
        int r = Math.Clamp((int)Math.Floor((Y0 - iyc * WorldGrid.BlockSize - y) / step + 0.5), 0, n - 1);
        return (ixc, iyc, r, c, ok);
    }

    /// <summary>The block of a block-grid position (column, row).</summary>
    public BlockId BlockAt(int bx, int by) => new(Tx0 / 4 + bx, Ty0 / 4 + by);

    /// <summary>The block rectangle: west, north, east, south.</summary>
    public (double X0, double Y0, double X1, double Y1) Rect => (X0, Y0, X0 + RectW, Y0 - RectH);

    /// <summary>The 1 m grid's rectangle as the nodes give it (Gx0, Gy0, Gx0 + W, Gy0 - H).</summary>
    public (double X0, double Y0, double X1, double Y1) GridRect => (Gx0, Gy0, Gx0 + W, Gy0 - H);
}
