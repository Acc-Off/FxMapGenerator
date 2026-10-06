using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;

namespace FxMapGenerator.Core.Labels;

/// <summary>A box a label takes: centre, width and height (m), rotation (degrees, counter-clockwise).</summary>
public readonly record struct LabelBox(double X, double Y, double W, double H, double Rotation);

/// <summary>
/// The space the labels have taken, 1 m a cell over the label frame (x0, y0 = top-left, x1, y1 = bottom-right). A box is
/// set into the cells as a filled polygon (Pillow's scan-line fill, <see cref="PilDraw"/>) inside the window around it;
/// taking a box adds the clearance around it.
/// </summary>
public sealed class Occupancy
{
    readonly double left, top;
    readonly double clearance;
    readonly bool[] cells;

    public int Width { get; }
    public int Height { get; }

    public Occupancy(double x0, double y0, double x1, double y1, double clearance)
    {
        (left, top, this.clearance) = (x0, y0, clearance);
        Width = (int)Math.Round(x1 - x0, MidpointRounding.ToEven);
        Height = (int)Math.Round(y0 - y1, MidpointRounding.ToEven);
        cells = new bool[checked(Width * Height)];
    }

    (double X, double Y)[] Corners(LabelBox b, double pad)
    {
        double w = b.W + 2 * pad, h = b.H + 2 * pad;
        double r = Num.Radians(b.Rotation);
        double c = Math.Cos(r), s = Math.Sin(r);
        var o = new (double, double)[4];
        int i = 0;
        foreach (var (dx, dy) in (ReadOnlySpan<(double, double)>)[(-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2)])
        {
            double px = b.X + dx * c - dy * s, py = b.Y + dx * s + dy * c;
            o[i++] = (px - left, top - py);
        }
        return o;
    }

    /// <summary>The window (rows r0..r1, columns c0..c1) around the boxes and the cells of their union in it.</summary>
    (int R0, int R1, int C0, int C1, Grid<bool>? Mask) Mask(IReadOnlyList<LabelBox> boxes, double pad)
    {
        var polys = boxes.Select(b => Corners(b, pad)).ToList();
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var pl in polys)
            foreach (var (x, y) in pl)
            {
                minX = Num.PyMin(minX, x);
                maxX = Num.PyMax(maxX, x);
                minY = Num.PyMin(minY, y);
                maxY = Num.PyMax(maxY, y);
            }
        int c0 = Math.Max((int)Math.Floor(minX) - 1, 0), c1 = Math.Min((int)Math.Ceiling(maxX) + 2, Width);
        int r0 = Math.Max((int)Math.Floor(minY) - 1, 0), r1 = Math.Min((int)Math.Ceiling(maxY) + 2, Height);
        if (c1 <= c0 || r1 <= r0) return (0, 0, 0, 0, null);
        var m = new Grid<bool>(c1 - c0, r1 - r0);
        foreach (var pl in polys) PilDraw.Polygon(m, pl.Select(p => (p.X - c0, p.Y - r0)).ToList());
        return (r0, r1, c0, c1, m);
    }

    /// <summary>Every corner of the boxes inside the frame.</summary>
    public bool Inside(IReadOnlyList<LabelBox> boxes)
    {
        foreach (var b in boxes)
            foreach (var (x, y) in Corners(b, 0.0))
                if (!(0 <= x && x < Width && 0 <= y && y < Height)) return false;
        return true;
    }

    /// <summary>No cell of the boxes is taken.</summary>
    public bool Free(IReadOnlyList<LabelBox> boxes)
    {
        var (r0, r1, c0, c1, m) = Mask(boxes, 0.0);
        if (m is null) return true;
        for (int r = r0; r < r1; r++)
            for (int c = c0; c < c1; c++)
                if (m[r - r0, c - c0] && cells[r * Width + c]) return false;
        return true;
    }

    /// <summary>Takes the cells of the boxes grown by the clearance.</summary>
    public void Take(IReadOnlyList<LabelBox> boxes)
    {
        var (r0, r1, c0, c1, m) = Mask(boxes, clearance);
        if (m is null) return;
        for (int r = r0; r < r1; r++)
            for (int c = c0; c < c1; c++)
                if (m[r - r0, c - c0]) cells[r * Width + c] = true;
    }

    /// <summary>The taken cells (for figures).</summary>
    public bool this[int row, int col] => cells[row * Width + col];
}
