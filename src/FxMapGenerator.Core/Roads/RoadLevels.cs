using FxMapGenerator.Core.Geometry;

namespace FxMapGenerator.Core.Roads;

/// <summary>
/// Grade separations: the level each drawn link is drawn at (0 = on the ground, 1 + the highest level it passes over),
/// and the link ends that take a higher neighbour's level near a node.
/// </summary>
public static class RoadLevels
{
    public const double Casing = 0.6, DzOver = 3.0, OverPad = 2.0, GraphNear = 60.0, HLen = 15.0;

    public sealed record Result(int[] Level, Dictionary<(int Link, int End), int> Raised, int Samples, int Checked, int Pairs, int Near, int Over);

    /// <summary>
    /// Link U is over link L where their ribbons (with casings and 2 m) overlap, U lies 3 m or more higher there (the
    /// heights along both links), they are not joined within 60 m through the graph, and L is no tunnel (a road over a
    /// tunnel stays on the ground). Only samples 3 m or more above the lowest sample within about 22 m are checked.
    /// </summary>
    public static Result Compute(IReadOnlyList<(string U, string V)> keys, IReadOnlyList<LinkRow> rows, CancellationToken token = default)
    {
        int n = rows.Count;
        var seg = new double[n];
        var ns = new int[n];
        long total = 0;
        for (int i = 0; i < n; i++)
        {
            var r = rows[i];
            seg[i] = Num.NpHypot(r.X1 - r.X0, r.Y1 - r.Y0);
            ns[i] = Math.Max(2, (int)Math.Ceiling(seg[i] / 2.0) + 1);
            total += ns[i];
        }
        int S = checked((int)total);
        var idx = new int[S];
        var sx = new double[S];
        var sy = new double[S];
        var sz = new double[S];
        var sw = new double[S];
        int p = 0;
        for (int i = 0; i < n; i++)
        {
            var r = rows[i];
            for (int j = 0; j < ns[i]; j++, p++)
            {
                double t = (double)j / (ns[i] - 1);
                idx[p] = i;
                sx[p] = r.X0 + t * (r.X1 - r.X0);
                sy[p] = r.Y0 + t * (r.Y1 - r.Y0);
                sz[p] = r.Z0 + t * (r.Z1 - r.Z0);
                sw[p] = r.Width;
            }
        }
        // lowest sample per 5 m bucket, then the lowest within 9 x 9 buckets
        const double B = 5.0;
        var bx = new int[S];
        var by = new int[S];
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
        for (int k = 0; k < S; k++)
        {
            bx[k] = (int)Math.Floor(sx[k] / B);
            by[k] = (int)Math.Floor(sy[k] / B);
            bx0 = Math.Min(bx0, bx[k]); bx1 = Math.Max(bx1, bx[k]);
            by0 = Math.Min(by0, by[k]); by1 = Math.Max(by1, by[k]);
        }
        int GW = bx1 - bx0 + 1, GH = by1 - by0 + 1;
        var G = new double[GW * GH];
        Array.Fill(G, double.PositiveInfinity);
        for (int k = 0; k < S; k++)
        {
            int c = (bx[k] - bx0) * GH + (by[k] - by0);
            if (sz[k] < G[c]) G[c] = sz[k];
        }
        var Gm = MinFilter(G, GW, GH, 4);
        var ci = new List<int>();
        for (int k = 0; k < S; k++)
            if (sz[k] - Gm[(bx[k] - bx0) * GH + (by[k] - by0)] >= DzOver && !rows[idx[k]].Tunnel) ci.Add(k);
        token.ThrowIfCancellationRequested();

        // sample pairs over / under
        double wmax = 0;
        foreach (var r in rows) wmax = Math.Max(wmax, r.Width);
        double R = wmax + 2 * Casing + OverPad;
        var buckets = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < S; k++)
        {
            var key = ((int)Math.Floor(sx[k] / R), (int)Math.Floor(sy[k] / R));
            if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<int>();
            l.Add(k);
        }
        var pairs = new HashSet<(int U, int L)>();
        foreach (var I in ci)
        {
            int cx = (int)Math.Floor(sx[I] / R), cy = (int)Math.Floor(sy[I] / R);
            for (int ax = cx - 1; ax <= cx + 1; ax++)
                for (int ay = cy - 1; ay <= cy + 1; ay++)
                {
                    if (!buckets.TryGetValue((ax, ay), out var l)) continue;
                    foreach (var J in l)
                    {
                        if (idx[I] == idx[J] || sz[I] - sz[J] < DzOver || rows[idx[J]].Tunnel) continue;
                        if (Num.NpHypot(sx[I] - sx[J], sy[I] - sy[J]) < (sw[I] + sw[J]) / 2 + 2 * Casing + OverPad)
                            pairs.Add((idx[I], idx[J]));
                    }
                }
        }
        token.ThrowIfCancellationRequested();

        // joined within 60 m through the graph = the same road
        var adj = new Dictionary<string, List<(int I, string Other, double Len)>>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++)
        {
            var (a, b) = keys[i];
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<(int, string, double)>();
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<(int, string, double)>();
            la.Add((i, b, seg[i]));
            lb.Add((i, a, seg[i]));
        }
        var byU = new SortedDictionary<int, List<int>>();
        foreach (var (U, L) in pairs)
        {
            if (!byU.TryGetValue(U, out var l)) byU[U] = l = new List<int>();
            l.Add(L);
        }
        var under = new SortedDictionary<int, HashSet<int>>();
        int nNear = 0;
        foreach (var (U, Ls) in byU)
        {
            var nl = Near(adj, keys[U]);
            foreach (var L in Ls)
            {
                if (nl.Contains(L)) { nNear++; continue; }
                if (!under.TryGetValue(U, out var s)) under[U] = s = new HashSet<int>();
                s.Add(L);
            }
        }
        var zmean = new double[n];
        for (int i = 0; i < n; i++) zmean[i] = (rows[i].Z0 + rows[i].Z1) / 2;
        var level = new int[n];
        var order = under.Keys.OrderBy(u => zmean[u]).ToList();
        for (int it = 0; it < 6; it++)
        {
            bool changed = false;
            foreach (var U in order)
            {
                int v = 1 + under[U].Max(L => level[L]);
                if (v != level[U]) { level[U] = v; changed = true; }
            }
            if (!changed) break;
        }
        var raised = Joins(keys, rows, level);
        return new Result(level, raised, S, ci.Count, under.Values.Sum(s => s.Count), nNear, under.Count);
    }

    /// <summary>The minimum over a (2 h + 1)-square window around each cell, cells outside the grid infinite (<c>scipy.ndimage.minimum_filter</c>, mode constant).</summary>
    static double[] MinFilter(double[] g, int w, int h, int half)
    {
        var tmp = new double[g.Length];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                double m = double.PositiveInfinity;
                for (int d = -half; d <= half; d++)
                {
                    int yy = y + d;
                    if (yy >= 0 && yy < h && g[x * h + yy] < m) m = g[x * h + yy];
                }
                tmp[x * h + y] = m;
            }
        var o = new double[g.Length];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                double m = double.PositiveInfinity;
                for (int d = -half; d <= half; d++)
                {
                    int xx = x + d;
                    if (xx >= 0 && xx < w && tmp[xx * h + y] < m) m = tmp[xx * h + y];
                }
                o[x * h + y] = m;
            }
        return o;
    }

    /// <summary>The links met from the ends of a link within 60 m along the drawn links.</summary>
    static HashSet<int> Near(Dictionary<string, List<(int I, string Other, double Len)>> adj, (string U, string V) key)
    {
        var dist = new HashSet<string>(StringComparer.Ordinal);
        var links = new HashSet<int>();
        var heap = new PriorityQueue<string, (double D, string N)>(Comparer<(double D, string N)>.Create((a, b) =>
        {
            int c = a.D.CompareTo(b.D);
            return c != 0 ? c : string.CompareOrdinal(a.N, b.N);
        }));
        heap.Enqueue(key.U, (0.0, key.U));
        heap.Enqueue(key.V, (0.0, key.V));
        while (heap.TryDequeue(out var node, out var pr))
        {
            if (dist.Contains(node) || pr.D > GraphNear) continue;
            dist.Add(node);
            foreach (var (i, other, ln) in adj[node])
            {
                links.Add(i);
                if (!dist.Contains(other)) heap.Enqueue(other, (pr.D + ln, other));
            }
        }
        return links;
    }

    /// <summary>
    /// At every node, a link whose end there is within 3 m in height of the end of a higher-level link takes that level
    /// over its first 15 m from the node (tunnels excluded): (link, end 0 | 1) -> level.
    /// </summary>
    static Dictionary<(int, int), int> Joins(IReadOnlyList<(string U, string V)> keys, IReadOnlyList<LinkRow> rows, int[] level)
    {
        var ends = new Dictionary<string, List<(int I, int E, double Z)>>(StringComparer.Ordinal);
        void Add(string n, (int, int, double) x)
        {
            if (!ends.TryGetValue(n, out var l)) ends[n] = l = new List<(int, int, double)>();
            l.Add(x);
        }
        for (int i = 0; i < keys.Count; i++)
        {
            if (rows[i].Tunnel) continue;
            var (u, v) = keys[i];
            Add(u.Split('|')[0], (i, 0, rows[i].Z0));
            if (!RoadLinks.IsExtension(v)) Add(v.Split('|')[0], (i, 1, rows[i].Z1));
        }
        var raised = new Dictionary<(int, int), int>();
        foreach (var ee in ends.Values)
        {
            if (ee.Count < 2) continue;
            foreach (var (i, e, z) in ee)
            {
                int best = 0;
                foreach (var (j, _, zj) in ee)
                    if (j != i && Math.Abs(zj - z) < DzOver && level[j] > best) best = level[j];
                if (best > level[i]) raised[(i, e)] = best;
            }
        }
        return raised;
    }
}
