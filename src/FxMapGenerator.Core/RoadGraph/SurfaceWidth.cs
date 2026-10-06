using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Scan;

namespace FxMapGenerator.Core.RoadGraph;

/// <summary>
/// Road widths from the scan's on-road samples: a profile across the road every 4 m, and the run of on-road samples
/// around the centre in it.
/// </summary>
public static class SurfaceWidth
{
    /// <summary>A run of on-road samples: its edges in m from the profile centre, found off the centre, reaching an end of the profile.</summary>
    public readonly record struct Run(double Left, double Right, bool OffCentre, bool Open);

    /// <summary>
    /// Offsets of a profile of <paramref name="half"/> m to each side every <paramref name="dt"/> m (as numpy's
    /// <c>arange(-half, half + dt / 2, dt)</c>).
    /// </summary>
    public static double[] Offsets(double half, double dt)
    {
        double start = -half;
        int n = (int)Math.Ceiling((half + dt / 2 - start) / dt);
        var t = new double[n];
        if (n == 0) return t;
        t[0] = start;
        if (n > 1) t[1] = start + dt;
        double delta = t.Length > 1 ? t[1] - start : dt;
        for (int i = 2; i < n; i++) t[i] = start + i * delta;
        return t;
    }

    /// <summary>
    /// The run of true samples that holds sample <paramref name="c0"/>, or else the nearest true sample within
    /// <paramref name="search"/> m (the lower of two equally near); null when there is none. Edges are half a sample
    /// outside the outermost true samples.
    /// </summary>
    public static Run? RunAround(ReadOnlySpan<bool> p, int c0, double dt, double half, double search = 8.0)
    {
        int c = c0;
        bool off = false;
        if (!p[c0])
        {
            int j = -1;
            for (int i = 0; i < p.Length; i++)
                if (p[i] && (j < 0 || Math.Abs(i - c0) < Math.Abs(j - c0))) j = i;
            if (j < 0 || Math.Abs(j - c0) * dt > search) return null;
            (c, off) = (j, true);
        }
        int kL = 0;
        while (c - kL >= 0 && p[c - kL]) kL++;
        bool openL = c - kL < 0;
        int kR = 0;
        while (c + kR < p.Length && p[c + kR]) kR++;
        bool openR = c + kR >= p.Length;
        return new Run((c - kL + 0.5 - c0) * dt, (c + kR - 0.5 - c0) * dt, off, openL || openR);
    }

    /// <summary>
    /// Stations every <paramref name="every"/> m, at least <paramref name="skip"/> m from both ends (the point by the
    /// segment direction times the distance into the segment).
    /// </summary>
    public static List<Station> Stations(IReadOnlyList<P2> pts, double every = 4.0, double skip = 15.0)
    {
        var segs = new List<(double S0, double D, P2 P, P2 Q)>();
        double len = 0;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double d = Num.Hypot(pts[i + 1].X - pts[i].X, pts[i + 1].Y - pts[i].Y);
            segs.Add((len, d, pts[i], pts[i + 1]));
            len += d;
        }
        var o = new List<Station>();
        for (double s = skip; s <= len - skip; s += every)
            foreach (var (s0, d, p, q) in segs)
            {
                if (!(s0 <= s && s <= s0 + d && d > 0)) continue;
                double dx = (q.X - p.X) / d, dy = (q.Y - p.Y) / d;
                o.Add(new Station(p.X + dx * (s - s0), p.Y + dy * (s - s0), -dy, dx, s));
                break;
            }
        return o;
    }

    /// <summary>
    /// Median width of the on-road run across the line at its stations (profiles of 25 m to each side every 0.25 m;
    /// runs found off the centre line or reaching a profile end do not count). A station on a segment with a fixed width
    /// (<paramref name="fixedWidths"/>, one per segment, NaN = none) takes that width instead. NaN with fewer than 3 widths.
    /// </summary>
    public static (double Width, int Count) Measure(ScanArea area, IReadOnlyList<P2> pts, IReadOnlyList<double>? fixedWidths = null, double half = 25.0, double dt = 0.25)
    {
        var ts = Offsets(half, dt);
        int c0 = ts.Length / 2;
        var prof = new bool[ts.Length];
        var ws = new List<double>();
        var cum = fixedWidths is null ? null : Polyline.CumLen(pts);
        foreach (var st in Stations(pts))
        {
            if (cum is not null && fixedWidths![SegmentAt(cum, st.S)] is var fw && double.IsFinite(fw)) { ws.Add(fw); continue; }
            for (int i = 0; i < ts.Length; i++) prof[i] = area.OnRoad(st.X + st.Nx * ts[i], st.Y + st.Ny * ts[i]);
            if (RunAround(prof, c0, dt, half) is { OffCentre: false, Open: false } r) ws.Add(r.Right - r.Left);
        }
        return (ws.Count >= 3 ? Num.Median(ws) : double.NaN, ws.Count);
    }

    /// <summary>The segment a station at <paramref name="s"/> m lies on, as <see cref="Stations"/> finds it (the first that holds it).</summary>
    static int SegmentAt(IReadOnlyList<double> cum, double s)
    {
        for (int i = 0; i < cum.Count - 1; i++)
            if (cum[i] <= s && s <= cum[i + 1] && cum[i + 1] > cum[i]) return i;
        return cum.Count - 2;
    }
}
