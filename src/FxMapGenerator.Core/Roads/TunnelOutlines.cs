using FxMapGenerator.Core.Geometry;

namespace FxMapGenerator.Core.Roads;

/// <summary>The outline of one tunnel group: closed rings (x, y pairs, the first point repeated at the end).</summary>
public sealed record TunnelGroup(IReadOnlyList<double[]> Rings);

/// <summary>
/// The outline of the union of the tunnel pieces' sub-segments (strokes of their width with round ends) per connected
/// tunnel group: painted on a 0.5 m raster (a cell is inside when its centre is), traced along the cell edges' midpoints
/// (marching squares; cells of the tunnel meeting only at a corner stay apart) and simplified to 0.15 m. One path per
/// group, so a dashed edge runs on around corners and junctions.
/// </summary>
public static class TunnelOutlines
{
    public const double Resolution = 0.5, Simplify = 0.15;

    public static List<TunnelGroup> Build(IReadOnlyList<(string U, string V)> keys, IReadOnlyList<(int[] Links, List<double[]> Segments)> pieces)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Root(string x)
        {
            if (!parent.ContainsKey(x)) parent[x] = x;
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        foreach (var (links, _) in pieces)
            foreach (var i in links)
            {
                string ra = Root(keys[i].U), rb = Root(keys[i].V);
                if (ra != rb) parent[ra] = rb;
            }
        var groups = new List<List<double[]>>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (links, segs) in pieces)
        {
            var r = Root(keys[links[0]].U);
            if (!index.TryGetValue(r, out var g)) { index[r] = g = groups.Count; groups.Add(new List<double[]>()); }
            groups[g].AddRange(segs);
        }
        var o = new List<TunnelGroup>();
        foreach (var segs in groups)
        {
            var rings = Outline(segs);
            if (rings.Count > 0) o.Add(new TunnelGroup(rings));
        }
        return o;
    }

    /// <summary>The traced outline rings of a set of round-capped strokes (x0, y0, x1, y1, width).</summary>
    public static List<double[]> Outline(IReadOnlyList<double[]> segs)
    {
        const double res = Resolution;
        double wmax = segs.Max(s => s[4]);
        double pad = wmax / 2 + 2.0;
        double x0 = segs.Min(s => Math.Min(s[0], s[2])) - pad, x1 = segs.Max(s => Math.Max(s[0], s[2])) + pad;
        double y0 = segs.Max(s => Math.Max(s[1], s[3])) + pad, y1 = segs.Min(s => Math.Min(s[1], s[3])) - pad;
        int W = (int)Math.Ceiling((x1 - x0) / res), H = (int)Math.Ceiling((y0 - y1) / res);
        // one cell of margin all round (the trace then closes every ring)
        int PW = W + 2, PH = H + 2;
        var m = new bool[PW * PH];
        foreach (var s in segs)
        {
            double hw = s[4] / 2;
            double ax = s[0], ay = s[1], bx = s[2], by = s[3];
            double dx = bx - ax, dy = by - ay, dd = dx * dx + dy * dy;
            int c0 = Math.Max(0, (int)Math.Floor((Math.Min(ax, bx) - hw - x0) / res) - 1), c1 = Math.Min(W - 1, (int)Math.Ceiling((Math.Max(ax, bx) + hw - x0) / res) + 1);
            int r0 = Math.Max(0, (int)Math.Floor((y0 - Math.Max(ay, by) - hw) / res) - 1), r1 = Math.Min(H - 1, (int)Math.Ceiling((y0 - Math.Min(ay, by) + hw) / res) + 1);
            for (int r = r0; r <= r1; r++)
            {
                double py = y0 - (r + 0.5) * res;
                for (int c = c0; c <= c1; c++)
                {
                    int k = (r + 1) * PW + c + 1;
                    if (m[k]) continue;
                    double px = x0 + (c + 0.5) * res;
                    double t = dd > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / dd, 0, 1) : 0;
                    double ex = px - (ax + t * dx), ey = py - (ay + t * dy);
                    if (ex * ex + ey * ey <= hw * hw) m[k] = true;
                }
            }
        }
        var rings = Trace(m, PW, PH);
        var o = new List<double[]>();
        foreach (var ring in rings)
        {
            // cell (row, col) of the padded raster -> world: x = x0 + (col - 1 + 0.5) res, y = y0 - (row - 1 + 0.5) res
            var pts = ring.Select(p => new P2(x0 + (p.C - 1 + 0.5) * res, y0 - (p.R - 1 + 0.5) * res)).ToList();
            var simple = Polyline.Rdp(pts, Simplify);
            if (simple.Count < 4) continue;
            var a = new double[2 * simple.Count];
            for (int i = 0; i < simple.Count; i++) { a[2 * i] = simple[i].X; a[2 * i + 1] = simple[i].Y; }
            o.Add(a);
        }
        return o;
    }

    /// <summary>
    /// Marching squares at level 0.5 on a 0 / 1 raster whose border cells are 0: closed rings of (row, col) points on the
    /// midpoints between cell centres, the inside on the left. Where two inside cells meet only at a corner, they stay
    /// apart (the outside is 8-connected).
    /// </summary>
    static List<List<(double R, double C)>> Trace(bool[] m, int w, int h)
    {
        // directed edges keyed by their start point (doubled coordinates), inside on the left
        var next = new Dictionary<(int, int), List<(int, int)>>();
        void Edge((int, int) a, (int, int) b)
        {
            if (!next.TryGetValue(a, out var l)) next[a] = l = new List<(int, int)>();
            l.Add(b);
        }
        bool In(int r, int c) => m[r * w + c];
        for (int r = 0; r < h - 1; r++)
            for (int c = 0; c < w - 1; c++)
            {
                bool ul = In(r, c), ur = In(r, c + 1), ll = In(r + 1, c), lr = In(r + 1, c + 1);
                int code = (ul ? 1 : 0) | (ur ? 2 : 0) | (lr ? 4 : 0) | (ll ? 8 : 0);
                if (code == 0 || code == 15) continue;
                // midpoints (doubled): top (2r, 2c+1), right (2r+1, 2c+2), bottom (2r+2, 2c+1), left (2r+1, 2c)
                var T = (2 * r, 2 * c + 1);
                var R = (2 * r + 1, 2 * c + 2);
                var B = (2 * r + 2, 2 * c + 1);
                var L = (2 * r + 1, 2 * c);
                // rows grow downward: with the inside on the left of travel in (x = col, y = -row), walk so that inside is left
                switch (code)
                {
                    case 1: Edge(L, T); break;
                    case 2: Edge(T, R); break;
                    case 3: Edge(L, R); break;
                    case 4: Edge(R, B); break;
                    case 5: Edge(L, T); Edge(R, B); break;      // saddle: the two inside cells stay apart
                    case 6: Edge(T, B); break;
                    case 7: Edge(L, B); break;
                    case 8: Edge(B, L); break;
                    case 9: Edge(B, T); break;
                    case 10: Edge(T, R); Edge(B, L); break;     // saddle
                    case 11: Edge(B, R); break;
                    case 12: Edge(R, L); break;
                    case 13: Edge(R, T); break;
                    case 14: Edge(T, L); break;
                }
            }
        var rings = new List<List<(double, double)>>();
        var starts = next.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2).ToList();
        foreach (var s in starts)
        {
            while (next.TryGetValue(s, out var ls) && ls.Count > 0)
            {
                var ring = new List<(double, double)> { (s.Item1 / 2.0, s.Item2 / 2.0) };
                var cur = s;
                while (true)
                {
                    var l = next[cur];
                    var nx = l[^1];
                    l.RemoveAt(l.Count - 1);
                    ring.Add((nx.Item1 / 2.0, nx.Item2 / 2.0));
                    cur = nx;
                    if (cur == s) break;
                    if (!next.TryGetValue(cur, out var l2) || l2.Count == 0) break;
                }
                if (ring.Count >= 4) rings.Add(ring);
            }
        }
        return rings;
    }
}
