using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;

namespace FxMapGenerator.Core.Vectors;

/// <summary>A closed outline in world metres (x east, y north), the first point not repeated at the end.</summary>
/// <param name="Area">The enclosed area (m², rounded to 0.1).</param>
/// <param name="Points">x, y pairs (m, rounded to 0.01).</param>
public sealed record Ring(double Area, double[] Points)
{
    public int Count => Points.Length / 2;
}

/// <summary>Outlines of the cells of a mask that are on, as the map layers draw them.</summary>
public static class Rings
{
    /// <summary>
    /// The adjusted potrace polygons (<see cref="PotracePolygon"/>) of the mask, cell (r, c) covering x0 + c .. x0 + c + 1
    /// and y0 - r .. y0 - r - 1: repeated points (within 1e-6 m) and a closing point dropped, rings of fewer than three
    /// points dropped; the area from the unrounded points (shoelace, summed in order), then the points rounded to 0.01 m.
    /// Outlines and holes come in the order they are found; drawn with the even-odd rule they give the mask back.
    /// </summary>
    public static List<Ring> Trace(Grid<bool> mask, double x0, double y0)
    {
        var o = new List<Ring>();
        var pts = new List<(double X, double Y)>();
        foreach (var path in PotracePolygon.Decompose(mask.Data, mask.Width, mask.Height))
        {
            pts.Clear();
            foreach (var (x, y) in PotracePolygon.Polygon(path))
            {
                var q = (X: x0 + x, Y: y0 - y);
                if (pts.Count == 0 || Num.Hypot(q.X - pts[^1].X, q.Y - pts[^1].Y) > 1e-6) pts.Add(q);
            }
            if (pts.Count >= 2 && Num.Hypot(pts[0].X - pts[^1].X, pts[0].Y - pts[^1].Y) < 1e-6) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) continue;
            double sum = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            var flat = new double[pts.Count * 2];
            for (int i = 0; i < pts.Count; i++)
            {
                flat[2 * i] = Num.Round(pts[i].X, 2);
                flat[2 * i + 1] = Num.Round(pts[i].Y, 2);
            }
            o.Add(new Ring(Num.Round(0.5 * Math.Abs(sum), 1), flat));
        }
        return o;
    }
}
