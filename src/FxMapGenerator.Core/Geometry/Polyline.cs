namespace FxMapGenerator.Core.Geometry;

/// <summary>A point in world metres (x east, y north).</summary>
public readonly record struct P2(double X, double Y);

/// <summary>A station along a polyline: the point, the left normal (unit), and its arc position.</summary>
public readonly record struct Station(double X, double Y, double Nx, double Ny, double S);

/// <summary>Polyline helpers: arc lengths, stations along the line, simplification and corner cutting.</summary>
public static class Polyline
{
    /// <summary>Arc position of every vertex (0 at the first).</summary>
    public static double[] CumLen(IReadOnlyList<P2> pts)
    {
        var c = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++) c[i] = c[i - 1] + Num.Hypot(pts[i].X - pts[i - 1].X, pts[i].Y - pts[i - 1].Y);
        return c;
    }

    /// <summary>Length of the polyline (the vertex distances added in order).</summary>
    public static double Length(IReadOnlyList<P2> pts)
    {
        double l = 0;
        for (int i = 1; i < pts.Count; i++) l += Num.Hypot(pts[i].X - pts[i - 1].X, pts[i].Y - pts[i - 1].Y);
        return l;
    }

    /// <summary>
    /// Stations every <paramref name="every"/> m from <paramref name="skip"/> m to the length minus
    /// <paramref name="skip"/>, the point interpolated on its segment (the first segment that holds it).
    /// </summary>
    public static List<Station> Stations(IReadOnlyList<P2> pts, double every, double skip)
    {
        var cum = CumLen(pts);
        var o = new List<Station>();
        if (pts.Count == 0) return o;
        for (double s = skip; s <= cum[^1] - skip; s += every)
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (!(cum[i] <= s && s <= cum[i + 1] && cum[i + 1] > cum[i])) continue;
                double d = cum[i + 1] - cum[i];
                double t = (s - cum[i]) / d;
                double dx = (pts[i + 1].X - pts[i].X) / d, dy = (pts[i + 1].Y - pts[i].Y) / d;
                o.Add(new Station(pts[i].X + t * (pts[i + 1].X - pts[i].X), pts[i].Y + t * (pts[i + 1].Y - pts[i].Y), -dy, dx, s));
                break;
            }
        return o;
    }

    /// <summary>
    /// Douglas-Peucker with tolerance <paramref name="eps"/> m: a loop (ends closer than eps, at least 4 points) is
    /// halved first; the farthest point wins, the first of equals.
    /// </summary>
    public static List<P2> Rdp(IReadOnlyList<P2> pts, double eps)
    {
        if (pts.Count < 3) return pts.ToList();
        if (Num.Hypot(pts[0].X - pts[^1].X, pts[0].Y - pts[^1].Y) < eps && pts.Count >= 4)
        {
            int m = pts.Count / 2;
            var a = Rdp(Slice(pts, 0, m + 1), eps);
            a.RemoveAt(a.Count - 1);
            a.AddRange(Rdp(Slice(pts, m, pts.Count), eps));
            return a;
        }
        var (p0, p1) = (pts[0], pts[^1]);
        double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
        double len = Num.Hypot(dx, dy);
        double best = -1.0;
        int bi = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            double x = pts[i].X, y = pts[i].Y;
            double d = len > 0 ? Math.Abs((x - p0.X) * dy - (y - p0.Y) * dx) / len : Num.Hypot(x - p0.X, y - p0.Y);
            if (d > best) (best, bi) = (d, i);
        }
        if (best > eps)
        {
            var a = Rdp(Slice(pts, 0, bi + 1), eps);
            a.RemoveAt(a.Count - 1);
            a.AddRange(Rdp(Slice(pts, bi, pts.Count), eps));
            return a;
        }
        return [p0, p1];
    }

    /// <summary>
    /// Corner cutting at the gentle vertices only: a vertex turning between 1 and <paramref name="maxTurn"/> degrees is
    /// replaced by the points a quarter of the way along its two segments; sharper corners and straight vertices stay.
    /// </summary>
    public static List<P2> Chaikin(IReadOnlyList<P2> input, int iterations = 2, double maxTurn = 60.0)
    {
        var pts = input.ToList();
        for (int it = 0; it < iterations; it++)
        {
            if (pts.Count < 3) break;
            var o = new List<P2>(pts.Count * 2) { pts[0] };
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var (a, b, c) = (pts[i - 1], pts[i], pts[i + 1]);
                double d1 = Num.Hypot(b.X - a.X, b.Y - a.Y), d2 = Num.Hypot(c.X - b.X, c.Y - b.Y);
                if (d1 == 0) d1 = 1;
                if (d2 == 0) d2 = 1;
                double cos = ((b.X - a.X) * (c.X - b.X) + (b.Y - a.Y) * (c.Y - b.Y)) / (d1 * d2);
                double turn = Num.Degrees(Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))));
                if (turn > maxTurn || turn < 1.0) o.Add(b);
                else
                {
                    o.Add(new P2(b.X - 0.25 * (b.X - a.X), b.Y - 0.25 * (b.Y - a.Y)));
                    o.Add(new P2(b.X + 0.25 * (c.X - b.X), b.Y + 0.25 * (c.Y - b.Y)));
                }
            }
            o.Add(pts[^1]);
            pts = o;
        }
        return pts;
    }

    static List<P2> Slice(IReadOnlyList<P2> pts, int from, int to)
    {
        var o = new List<P2>(to - from);
        for (int i = from; i < to; i++) o.Add(pts[i]);
        return o;
    }
}
