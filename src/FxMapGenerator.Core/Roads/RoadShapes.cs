using FxMapGenerator.Core.Geometry;

namespace FxMapGenerator.Core.Roads;

/// <summary>A closed ring or an open line as x, y pairs one after the other.</summary>
public sealed record RoadRibbon(int Class, double[] Casing, double[] Fill);
public sealed record CornerPatch(int Class, double[] Ring);
/// <summary>A rounded junction corner: the region filled with the road colour, the arc drawn with the casing, the seam along the road edges.</summary>
public sealed record JunctionCorner(int Class, double[] Region, double[] Arc, double[] Seam);
public sealed record RaisedRun(int Level, int Class, double[] Casing, double[] Fill);

/// <summary>
/// The road shapes to draw, from the drawn links and their levels (ribbon outlines):
/// pieces = runs of links through nodes with 2 drawn links (or a straight-on pair; not through a fork node), one track /
/// tunnel state each; the centreline a centripetal Catmull-Rom curve through the piece's nodes sampled every 2 m or less;
/// the width the links' widths averaged over 30 m along the piece. Level 0 = one ribbon per piece and class (casing and
/// fill; real dead ends rounded); levels 1, 2, ... = each run of samples at that level or higher as a ribbon; where 2+
/// piece ends stop at a node, their corners joined into a patch; junction corners rounded with 6 m from the drawn edges
/// (also where a link the road edits add meets other links: <see cref="RoadLinks.Joins"/>);
/// unpaved tracks as short segments (drawn as lines); tunnels as the outlines of their groups.
/// </summary>
public sealed class RoadShapes
{
    public const double Sample = 2.0, Taper = 30.0, Radius = 6.0, Casing = 0.6, DeadR = 0.5, PatchNear = 3.0, EdgeExt = 15.0, ArmMax = 25.0, EndReach = 40.0;
    public const double TrackWidth = RoadLinks.TrackWidth;

    public List<int[]> Pieces { get; } = new();
    public int TunnelPieces, SubSegments;
    /// <summary>Segments of the unpaved tracks (x0, y0, x1, y1 per segment), every level.</summary>
    public List<double> Tracks { get; } = new();
    public List<RoadRibbon> Ground { get; } = new();
    public List<RaisedRun> Raised { get; } = new();
    public List<CornerPatch> Patches { get; } = new();
    public List<JunctionCorner> Corners { get; } = new();
    public int JunctionsRounded, CornersSkipped;
    /// <summary>Tunnel pieces: their links and sub-segments (x0, y0, x1, y1, width).</summary>
    public List<(int[] Links, List<double[]> Segments)> TunnelSegments { get; } = new();

    readonly RoadLinks _l;
    readonly int[] _level;
    readonly Dictionary<(int, int), int> _raised;
    readonly Dictionary<string, RoadNode> N;
    readonly Dictionary<string, double[]> _jsq;
    /// <summary>The nodes whose corners are rounded, with their squares: the junction squares and the joins of added links.</summary>
    readonly Dictionary<string, double[]> _round;
    readonly List<(string J, double[] P, double[] W, int Cls, bool Raised)> _ends = new();

    RoadShapes(RoadLinks links, int[] level, Dictionary<(int, int), int> raised)
    {
        _l = links;
        _level = level;
        _raised = raised;
        N = links.N;
        _jsq = links.Junctions;
        _round = links.Joins.Count == 0 ? _jsq : _jsq.Concat(links.Joins).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    public static RoadShapes Build(RoadLinks links, int[] level, Dictionary<(int, int), int> raised, CancellationToken token = default)
    {
        var s = new RoadShapes(links, level, raised);
        s.Run(token);
        return s;
    }

    static bool IsExt(string k) => RoadLinks.IsExtension(k);
    static string Base(string k) => k.Split('|')[0];

    // ------------------------------------------------------------------------------------------------ pieces

    List<(int[] Links, double[] P, string[] Nodes)> BuildPieces()
    {
        var keys = _l.Keys;
        var rows = _l.Rows;
        var at = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var pos = new Dictionary<string, (double, double, double)>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++)
        {
            var (u, v) = keys[i];
            foreach (var n in new[] { u, v })
            {
                if (!at.TryGetValue(n, out var l)) at[n] = l = new List<int>();
                l.Add(i);
            }
            pos[u] = (rows[i].X0, rows[i].Y0, rows[i].Z0);
            pos[v] = (rows[i].X1, rows[i].Y1, rows[i].Z1);
        }
        var seen = new bool[keys.Count];
        string Other(int i, string n) => keys[i].U == n ? keys[i].V : keys[i].U;
        int? Step(int i, string n)
        {
            if (_l.Forks.Contains(n)) return null;
            int j;
            var an = at[n];
            if (an.Count == 2) j = an[1] == i ? an[0] : an[1];
            else if (_l.Continuation.TryGetValue((i, n), out var c)) j = c;
            else return null;
            if (j == i || seen[j] || (rows[j].Class == ShapeClass.Track) != (rows[i].Class == ShapeClass.Track) || rows[j].Tunnel != rows[i].Tunnel) return null;
            return j;
        }
        var pieces = new List<(int[], double[], string[])>();
        for (int i0 = 0; i0 < keys.Count; i0++)
        {
            if (seen[i0]) continue;
            seen[i0] = true;
            var walks = new List<(List<int> Links, List<string> Nodes)>();
            foreach (var n0 in new[] { keys[i0].V, keys[i0].U })
            {
                var ls = new List<int>();
                var ns = new List<string>();
                int i = i0;
                string n = n0;
                while (true)
                {
                    var j = Step(i, n);
                    if (j is null) break;
                    seen[j.Value] = true;
                    var n2 = Other(j.Value, n);
                    ls.Add(j.Value);
                    ns.Add(n2);
                    i = j.Value;
                    n = n2;
                }
                walks.Add((ls, ns));
            }
            var (fl, fn) = walks[0];
            var (bl, bn) = walks[1];
            var links = Enumerable.Reverse(bl).Append(i0).Concat(fl).ToArray();
            var nodes = Enumerable.Reverse(bn).Append(keys[i0].U).Append(keys[i0].V).Concat(fn).ToArray();
            var P = new double[nodes.Length * 3];
            for (int k = 0; k < nodes.Length; k++)
            {
                var (x, y, z) = pos[nodes[k]];
                P[3 * k] = x; P[3 * k + 1] = y; P[3 * k + 2] = z;
            }
            pieces.Add((links, P, nodes));
        }
        return pieces;
    }

    // ------------------------------------------------------------------------------------------------ curves and ribbons

    /// <summary>
    /// Centripetal Catmull-Rom through the points (x, y, z per point): one span per pair of neighbours, k + 1 points each
    /// (both ends included, k = ceil(length / step)); pre / post = the points before / after the ends (default: straight on).
    /// Two points, or a span shorter than 0.5 m, are straight.
    /// </summary>
    public static List<double[]> Curve(double[] P, double step, (double X, double Y, double Z)? pre, (double X, double Y, double Z)? post)
    {
        int n = P.Length / 3;
        var ext = new double[(n + 2) * 3];
        for (int c = 0; c < 3; c++)
        {
            ext[c] = pre is { } a ? (c == 0 ? a.X : c == 1 ? a.Y : a.Z) : 2 * P[c] - P[3 + c];
            ext[(n + 1) * 3 + c] = post is { } b ? (c == 0 ? b.X : c == 1 ? b.Y : b.Z) : 2 * P[(n - 1) * 3 + c] - P[(n - 2) * 3 + c];
        }
        Array.Copy(P, 0, ext, 3, P.Length);
        var o = new List<double[]>();
        for (int i = 0; i < n - 1; i++)
        {
            int i0 = i * 3, i1 = (i + 1) * 3, i2 = (i + 2) * 3, i3 = (i + 3) * 3;
            double L = Num.NpHypot(ext[i2] - ext[i1], ext[i2 + 1] - ext[i1 + 1]);
            int k = Math.Max(1, (int)Math.Ceiling(L / step));
            var C = new double[(k + 1) * 3];
            if (n == 2 || L < 0.5)
            {
                var t = Num.Linspace(0.0, 1.0, k + 1);
                for (int j = 0; j <= k; j++)
                    for (int c = 0; c < 3; c++)
                        C[j * 3 + c] = ext[i1 + c] + t[j] * (ext[i2 + c] - ext[i1 + c]);
                o.Add(C);
                continue;
            }
            double t1 = Math.Pow(Math.Max(Num.NpHypot(ext[i1] - ext[i0], ext[i1 + 1] - ext[i0 + 1]), 1e-3), 0.5);
            double t2 = t1 + Math.Pow(Math.Max(L, 1e-3), 0.5);
            double t3 = t2 + Math.Pow(Math.Max(Num.NpHypot(ext[i3] - ext[i2], ext[i3 + 1] - ext[i2 + 1]), 1e-3), 0.5);
            var tt = Num.Linspace(t1, t2, k + 1);
            for (int j = 0; j <= k; j++)
            {
                double t = tt[j];
                for (int c = 0; c < 3; c++)
                {
                    double p0 = ext[i0 + c], p1 = ext[i1 + c], p2 = ext[i2 + c], p3 = ext[i3 + c];
                    double A1 = (t1 - t) / t1 * p0 + t / t1 * p1;
                    double A2 = (t2 - t) / (t2 - t1) * p1 + (t - t1) / (t2 - t1) * p2;
                    double A3 = (t3 - t) / (t3 - t2) * p2 + (t - t2) / (t3 - t2) * p3;
                    double B1 = (t2 - t) / t2 * A1 + t / t2 * A2;
                    double B2 = (t3 - t) / (t3 - t1) * A2 + (t - t1) / (t3 - t1) * A3;
                    C[j * 3 + c] = (t2 - t) / (t2 - t1) * B1 + (t - t1) / (t2 - t1) * B2;
                }
            }
            o.Add(C);
        }
        return o;
    }

    /// <summary>
    /// A closed ribbon of widths w (per point) along the line xy (x, y pairs): left side forward, right side back; the
    /// vertex normals the mean of the neighbouring segment normals (flush ends); d0 / d1 = the direction the cut at the
    /// first / last point is square to.
    /// </summary>
    public static double[] Ribbon(double[] xy, double[] w, (double X, double Y)? d0, (double X, double Y)? d1)
    {
        int n = xy.Length / 2;
        var nx = new double[n - 1];
        var ny = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = xy[2 * i + 2] - xy[2 * i], dy = xy[2 * i + 3] - xy[2 * i + 1];
            double L = Num.NpHypot(dx, dy);
            if (L == 0) L = 1e-9;
            nx[i] = -dy / L;
            ny[i] = dx / L;
        }
        var vx = new double[n];
        var vy = new double[n];
        vx[0] = nx[0]; vy[0] = ny[0];
        for (int i = 1; i < n - 1; i++)
        {
            vx[i] = (nx[i - 1] + nx[i]) / 2;
            vy[i] = (ny[i - 1] + ny[i]) / 2;
        }
        vx[n - 1] = nx[n - 2]; vy[n - 1] = ny[n - 2];
        foreach (var (k, dd) in new[] { (0, d0), (n - 1, d1) })
        {
            if (dd is not { } d) continue;
            double h = Num.NpHypot(d.X, d.Y);
            if (h > 1e-9) { vx[k] = -d.Y / h; vy[k] = d.X / h; }
        }
        var o = new double[4 * n];
        for (int i = 0; i < n; i++)
        {
            double ln = Num.NpHypot(vx[i], vy[i]);
            if (ln < 1e-6) ln = 1.0;
            double ux = vx[i] / ln, uy = vy[i] / ln;
            double hw = w[i] / 2;
            o[2 * i] = xy[2 * i] + ux * hw;
            o[2 * i + 1] = xy[2 * i + 1] + uy * hw;
            int r = 2 * n - 1 - i;
            o[2 * r] = xy[2 * i] - ux * hw;
            o[2 * r + 1] = xy[2 * i + 1] - uy * hw;
        }
        return o;
    }

    /// <summary>
    /// <see cref="Ribbon"/>; at a capped end (a real dead end) the ribbon runs <paramref name="ext"/> m further (the
    /// casing then shows across the tip) and its two corners are rounded with radius r.
    /// </summary>
    public static double[] CappedRibbon(double[] xy, double[] w, bool cap0, bool cap1, double ext, double r, (double X, double Y)? d0, (double X, double Y)? d1)
    {
        if ((cap0 || cap1) && ext > 0 && xy.Length >= 4)
        {
            var lx = xy.ToList();
            var lw = w.ToList();
            if (cap0)
            {
                double dx = lx[0] - lx[2], dy = lx[1] - lx[3];
                double nn = Num.NpHypot(dx, dy) + 1e-9;
                lx.InsertRange(0, [lx[0] + dx / nn * ext, lx[1] + dy / nn * ext]);
                lw.Insert(0, lw[0]);
            }
            if (cap1)
            {
                int m = lx.Count;
                double dx = lx[m - 2] - lx[m - 4], dy = lx[m - 1] - lx[m - 3];
                double nn = Num.NpHypot(dx, dy) + 1e-9;
                lx.AddRange([lx[m - 2] + dx / nn * ext, lx[m - 1] + dy / nn * ext]);
                lw.Add(lw[^1]);
            }
            xy = lx.ToArray();
            w = lw.ToArray();
        }
        var poly = Ribbon(xy, w, cap0 ? null : d0, cap1 ? null : d1);
        if (r <= 0 || !(cap0 || cap1)) return poly;
        int n = xy.Length / 2, pn = poly.Length / 2;
        var corners = new HashSet<int>();
        if (cap0) { corners.Add(0); corners.Add(2 * n - 1); }
        if (cap1) { corners.Add(n - 1); corners.Add(n); }
        var o = new List<double>();
        for (int k = 0; k < pn; k++)
        {
            if (!corners.Contains(k)) { o.Add(poly[2 * k]); o.Add(poly[2 * k + 1]); continue; }
            int kp = (k - 1 + pn) % pn, kn = (k + 1) % pn;
            double Vx = poly[2 * k], Vy = poly[2 * k + 1];
            double u1x = poly[2 * kp] - Vx, u1y = poly[2 * kp + 1] - Vy;
            double u2x = poly[2 * kn] - Vx, u2y = poly[2 * kn + 1] - Vy;
            double l1 = Num.NpHypot(u1x, u1y), l2 = Num.NpHypot(u2x, u2y);
            double rr = Math.Min(r, Math.Min(0.45 * l1, 0.45 * l2));
            if (rr <= 1e-3) { o.Add(Vx); o.Add(Vy); continue; }
            u1x /= l1; u1y /= l1; u2x /= l2; u2y /= l2;
            double T1x = Vx + u1x * rr, T1y = Vy + u1y * rr, T2x = Vx + u2x * rr, T2y = Vy + u2y * rr;
            double Cx = Vx + (u1x + u2x) * rr, Cy = Vy + (u1y + u2y) * rr;
            double a1 = Math.Atan2(T1y - Cy, T1x - Cx), a2 = Math.Atan2(T2y - Cy, T2x - Cx);
            double da = Num.PyMod(a2 - a1 + Math.PI, 2 * Math.PI) - Math.PI;
            foreach (var t in Num.Linspace(0, 1, 5))
            {
                o.Add(Cx + rr * Math.Cos(a1 + da * t));
                o.Add(Cy + rr * Math.Sin(a1 + da * t));
            }
        }
        return o.ToArray();
    }

    // ------------------------------------------------------------------------------------------------ the shapes

    int[] SpanLevels(double[] C, int i)
    {
        var rows = _l.Rows;
        int m = C.Length / 3;
        var lvs = new int[m - 1];
        Array.Fill(lvs, _level[i]);
        bool r0 = _raised.TryGetValue((i, 0), out var v0), r1 = _raised.TryGetValue((i, 1), out var v1);
        if (_raised.Count == 0 || !(r0 || r1)) return lvs;
        var d = new double[m];
        for (int j = 1; j < m; j++) d[j] = d[j - 1] + Num.NpHypot(C[3 * j] - C[3 * j - 3], C[3 * j + 1] - C[3 * j - 2]);
        bool fwd = Num.NpHypot(C[0] - rows[i].X0, C[1] - rows[i].Y0) < 1e-3 || Num.NpHypot(C[3 * (m - 1)] - rows[i].X1, C[3 * (m - 1) + 1] - rows[i].Y1) < 1e-3;
        var dm = new double[m - 1];
        for (int j = 0; j < m - 1; j++) dm[j] = (d[j] + d[j + 1]) / 2;
        foreach (var (e, fromStart) in fwd ? new[] { (0, true), (1, false) } : new[] { (1, true), (0, false) })
        {
            if (!_raised.TryGetValue((i, e), out var lv)) continue;
            for (int j = 0; j < m - 1; j++)
            {
                double dist = fromStart ? dm[j] : d[m - 1] - dm[j];
                if (dist < RoadLevels.HLen) lvs[j] = Math.Max(lvs[j], lv);
            }
        }
        return lvs;
    }

    void Run(CancellationToken token)
    {
        var keys = _l.Keys;
        var rows = _l.Rows;
        var pieces = BuildPieces();
        var degRows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (u, v) in keys)
        {
            degRows[u] = degRows.GetValueOrDefault(u) + 1;
            degRows[v] = degRows.GetValueOrDefault(v) + 1;
        }
        var extTargets = keys.Where(k => IsExt(k.V)).Select(k => RoadLinks.ExtensionTarget(k.V)).ToHashSet(StringComparer.Ordinal);
        bool Dead(string n) => !IsExt(n) && !n.Contains('|') && degRows.GetValueOrDefault(n) == 1 && N.ContainsKey(n) && !extTargets.Contains(n) && !_jsq.ContainsKey(n);
        var pend = new Dictionary<string, List<((double X, double Y) Pt, (double X, double Y) Dir, double W, int Cls)>>(StringComparer.Ordinal);

        foreach (var (links, P, pnodes) in pieces)
        {
            token.ThrowIfCancellationRequested();
            Pieces.Add(links);
            var spans = Curve(P, Sample, N.GetValueOrDefault(pnodes[0])?.Phantom, N.GetValueOrDefault(pnodes[^1])?.Phantom);
            int ns = spans.Count;
            var spanLen = new double[ns];
            for (int si = 0; si < ns; si++)
            {
                var C = spans[si];
                int m = C.Length / 3;
                var h = new double[m - 1];
                for (int j = 0; j < m - 1; j++) h[j] = Num.NpHypot(C[3 * j + 3] - C[3 * j], C[3 * j + 4] - C[3 * j + 1]);
                spanLen[si] = Num.NpSum(h);
            }
            var bounds = new double[ns + 1];
            for (int si = 0; si < ns; si++) bounds[si + 1] = bounds[si] + spanLen[si];
            var Wc = new double[ns + 1];
            for (int si = 0; si < ns; si++) Wc[si + 1] = Wc[si] + rows[links[si]].Width * spanLen[si];
            double total = bounds[ns];
            bool tunnel = rows[links[0]].Tunnel;
            var segs = new List<double[]>();          // x0 y0 x1 y1 w cls level
            var xyL = new List<double>();
            var wL = new List<double>();
            var lvL = new List<int>();
            var clvL = new List<int>();
            var nidx = new List<int> { 0 };
            for (int si = 0; si < ns; si++)
            {
                var C = spans[si];
                int m = C.Length / 3;
                var s = new double[m];
                double acc = 0;
                s[0] = bounds[si] + 0.0;
                for (int j = 1; j < m; j++)
                {
                    acc += Num.NpHypot(C[3 * j] - C[3 * j - 3], C[3 * j + 1] - C[3 * j - 2]);
                    s[j] = bounds[si] + acc;
                }
                var w = new double[m];
                int i = links[si];
                if (rows[i].Class < ShapeClass.Track)
                {
                    int g1 = 0, g2 = 0;
                    for (int j = 0; j < m; j++)
                    {
                        double lo = Math.Min(Math.Max(s[j] - Taper / 2, 0), total), hi = Math.Min(Math.Max(s[j] + Taper / 2, 0), total);
                        double sp = hi - lo;
                        double val = (Num.NpInterp(hi, bounds, Wc, ref g1) - Num.NpInterp(lo, bounds, Wc, ref g2)) / Math.Max(sp, 1e-6);
                        w[j] = sp > 1e-6 ? val : rows[i].Width;
                    }
                }
                else Array.Fill(w, TrackWidth);
                var lvs = SpanLevels(C, i);
                for (int j = 0; j < m - 1; j++)
                    segs.Add([C[3 * j], C[3 * j + 1], C[3 * j + 3], C[3 * j + 4], (w[j] + w[j + 1]) / 2, rows[i].Class, lvs[j]]);
                nidx.Add(nidx[^1] + m - 1);
                for (int j = si == 0 ? 0 : 1; j < m; j++) { xyL.Add(C[3 * j]); xyL.Add(C[3 * j + 1]); wL.Add(w[j]); }
                lvL.AddRange(lvs);
                for (int j = 0; j < m - 1; j++) clvL.Add(rows[i].Class);
            }
            if (tunnel)
            {
                TunnelPieces++;
                TunnelSegments.Add((links, segs.Select(x => x[..5]).ToList()));
                foreach (var e in new[] { pnodes[0], pnodes[^1] })
                {
                    var J = IsExt(e) ? RoadLinks.ExtensionTarget(e) : Base(e);
                    if (_round.ContainsKey(J)) _ends.Add((J, [], [], 0, true));
                }
                continue;
            }
            SubSegments += segs.Count;
            foreach (var sg in segs)
                if ((int)sg[5] == ShapeClass.Track) Tracks.AddRange([sg[0], sg[1], sg[2], sg[3]]);
            var xy = xyL.ToArray();
            var wv = wL.ToArray();
            var lv = lvL.ToArray();
            var clv = clvL.ToArray();
            int npts = xy.Length / 2;
            foreach (var (e, rev) in new[] { (pnodes[0], false), (pnodes[^1], true) })
            {
                double? fw = _l.Forks.Contains(e) ? N.GetValueOrDefault(e)?.ForkWidth : null;
                if (fw is not null && (rev ? links[^1] : links[0]) != N[e].ForkTrunk) fw = null;
                if (fw is null || npts < 2) continue;
                var cs = CumLen(xy);
                double wEnd = wv[rev ? npts - 1 : 0];
                for (int j = 0; j < npts; j++)
                {
                    double de = rev ? cs[npts - 1] - cs[j] : cs[j];
                    double f = Math.Min(Math.Max(1.0 - de / Math.Max(Taper, 1e-6), 0.0), 1.0);
                    wv[j] = wv[j] + (fw.Value - wEnd) * f;
                }
            }
            int cls = rows[links[0]].Class;
            int ClAt(int ix) => clv.Length > 0 ? clv[Math.Min(Math.Max(ix, 0), clv.Length - 1)] : cls;
            var ph0 = N.GetValueOrDefault(pnodes[0])?.Phantom;
            var ph1 = N.GetValueOrDefault(pnodes[^1])?.Phantom;
            (double X, double Y)? dir0 = ph0 is { } a0 ? (xy[0] - a0.X, xy[1] - a0.Y) : null;
            (double X, double Y)? dir1 = ph1 is { } a1 ? (a1.X - xy[2 * npts - 2], a1.Y - xy[2 * npts - 1]) : null;
            if (_round.Count > 0 && cls < ShapeClass.Track && npts >= 2)
            {
                var cum = CumLen(xy);
                foreach (var (e, fwd) in new[] { (pnodes[0], true), (pnodes[^1], false) })
                {
                    var J = IsExt(e) ? RoadLinks.ExtensionTarget(e) : Base(e);
                    if (!_round.ContainsKey(J)) continue;
                    if (fwd)
                    {
                        var sel = Enumerable.Range(0, npts).Where(j => cum[j] <= EndReach).ToList();
                        _ends.Add((J, Pick(xy, sel), sel.Select(j => wv[j]).ToArray(), ClAt(0), lv[0] > 0));
                    }
                    else
                    {
                        var sel = Enumerable.Range(0, npts).Where(j => cum[j] >= cum[npts - 1] - EndReach).Reverse().ToList();
                        _ends.Add((J, Pick(xy, sel), sel.Select(j => wv[j]).ToArray(), ClAt(clv.Length - 1), lv[^1] > 0));
                    }
                }
                for (int jn = 1; jn < pnodes.Length - 1; jn++)
                {
                    var J = Base(pnodes[jn]);
                    if (!_round.ContainsKey(J)) continue;
                    int ix = nidx[jn];
                    bool raisedAt = lv[Math.Min(ix, lv.Length - 1)] > 0 || lv[Math.Max(ix - 1, 0)] > 0;
                    var sel = Enumerable.Range(0, npts).Where(j => cum[j] >= cum[ix] && cum[j] <= cum[ix] + EndReach).ToList();
                    _ends.Add((J, Pick(xy, sel), sel.Select(j => wv[j]).ToArray(), ClAt(ix), raisedAt));
                    sel = Enumerable.Range(0, npts).Where(j => cum[j] <= cum[ix] && cum[j] >= cum[ix] - EndReach).Reverse().ToList();
                    _ends.Add((J, Pick(xy, sel), sel.Select(j => wv[j]).ToArray(), ClAt(ix - 1), raisedAt));
                }
            }
            List<(int Cls, int J, int E)> ClassRuns(Func<int, bool> on)
            {
                var o = new List<(int, int, int)>();
                int j = 0;
                while (j < clv.Length)
                {
                    if (!on(j)) { j++; continue; }
                    int e = j;
                    while (e < clv.Length && on(e) && clv[e] == clv[j]) e++;
                    o.Add((clv[j], j, e));
                    j = e;
                }
                return o;
            }
            int lvMax = lv.Length > 0 ? lv.Max() : 0;
            for (int k = 1; k < (lv.Length > 0 ? lvMax + 1 : 1); k++)
                foreach (var (rc, j, e) in ClassRuns(x => lv[x] >= k))
                {
                    var sxy = xy[(2 * j)..(2 * e + 2)];
                    var swd = wv[j..(e + 1)];
                    var e0 = j == 0 ? dir0 : null;
                    var e1 = e == clv.Length ? dir1 : null;
                    var cas = Ribbon(sxy, swd.Select(x => x + (rc < ShapeClass.Track ? 2 * Casing : 0.0)).ToArray(), e0, e1);
                    var fil = Ribbon(sxy, swd, e0, e1);
                    Raised.Add(new RaisedRun(k, rc, cas, fil));
                }
            if (cls < ShapeClass.Track)
            {
                var cr = ClassRuns(_ => true);
                for (int ri = 0; ri < cr.Count; ri++)
                {
                    var (rc, j, e) = cr[ri];
                    var sxy = xy[(2 * j)..(2 * e + 2)];
                    var swd = wv[j..(e + 1)];
                    bool cap0 = ri == 0 && Dead(pnodes[0]);
                    bool cap1 = ri == cr.Count - 1 && Dead(pnodes[^1]);
                    var e0 = ri == 0 ? dir0 : null;
                    var e1 = ri == cr.Count - 1 ? dir1 : null;
                    var cas = CappedRibbon(sxy, swd.Select(x => x + 2 * Casing).ToArray(), cap0, cap1, Casing, DeadR + Casing, e0, e1);
                    var fil = CappedRibbon(sxy, swd, cap0, cap1, 0.0, DeadR, e0, e1);
                    Ground.Add(new RoadRibbon(rc, cas, fil));
                }
                foreach (var (e, first) in new[] { (pnodes[0], true), (pnodes[^1], false) })
                {
                    int ia = first ? 0 : npts - 1, ib = first ? Math.Min(1, npts - 1) : Math.Max(npts - 2, 0);
                    double wvv = first ? wv[0] : wv[npts - 1];
                    int cv = first ? ClAt(0) : ClAt(clv.Length - 1);
                    if (Dead(e) || (e.Contains('|') && !IsExt(e) && !e.Contains("|thr|", StringComparison.Ordinal))) continue;
                    var J = IsExt(e) ? RoadLinks.ExtensionTarget(e) : Base(e);
                    double dvx = xy[2 * ib] - xy[2 * ia], dvy = xy[2 * ib + 1] - xy[2 * ia + 1];
                    double dl = Num.NpHypot(dvx, dvy);
                    if (dl < 1e-6 || (e == pnodes[0] ? lv[0] > 0 : lv[^1] > 0)) continue;
                    if (!pend.TryGetValue(J, out var pl)) pend[J] = pl = new();
                    pl.Add(((xy[2 * ia], xy[2 * ia + 1]), (dvx / dl, dvy / dl), wvv, cv));
                }
            }
        }
        EdgeFillets();
        foreach (var (J, ee0) in pend)
        {
            if (!N.TryGetValue(J, out var nj)) continue;
            double c0x = nj.X, c0y = nj.Y;
            var ee = ee0.Where(x => Num.NpHypot(x.Pt.X - c0x, x.Pt.Y - c0y) <= x.W / 2 + PatchNear).ToList();
            if (ee.Count < 2) continue;
            var pts = new List<(double X, double Y)>();
            foreach (var (pt, dv, wv, _) in ee)
            {
                double nrx = -dv.Y, nry = dv.X;
                pts.Add((pt.X + nrx * wv / 2, pt.Y + nry * wv / 2));
                pts.Add((pt.X - nrx * wv / 2, pt.Y - nry * wv / 2));
            }
            var ang = pts.Select(p => Math.Atan2(p.Y - c0y, p.X - c0x)).ToArray();
            var order = ArgSortQuick(ang);
            var ring = new double[2 * pts.Count];
            for (int k = 0; k < order.Length; k++) { ring[2 * k] = pts[order[k]].X; ring[2 * k + 1] = pts[order[k]].Y; }
            Patches.Add(new CornerPatch(ee.All(x => x.Cls == ShapeClass.Highway) ? ShapeClass.Highway : ShapeClass.Road, ring));
        }
    }

    static double[] Pick(double[] xy, List<int> sel)
    {
        var o = new double[2 * sel.Count];
        for (int k = 0; k < sel.Count; k++) { o[2 * k] = xy[2 * sel[k]]; o[2 * k + 1] = xy[2 * sel[k] + 1]; }
        return o;
    }

    static double[] CumLen(double[] xy)
    {
        int n = xy.Length / 2;
        var c = new double[n];
        for (int j = 1; j < n; j++) c[j] = c[j - 1] + Num.NpHypot(xy[2 * j] - xy[2 * j - 2], xy[2 * j + 1] - xy[2 * j - 1]);
        return c;
    }

    /// <summary>numpy's default argsort order (for the few points of a patch; ties are rare and fall in index order).</summary>
    static int[] ArgSortQuick(double[] v)
    {
        var idx = Enumerable.Range(0, v.Length).ToArray();
        Array.Sort(idx, (a, b) => { int c = v[a].CompareTo(v[b]); return c != 0 ? c : a.CompareTo(b); });
        return idx;
    }

    // ------------------------------------------------------------------------------------------------ junction corners

    static List<(double X, double Y, int I, double S, int J, double T)> SegX(double[] P, double[] Q)
    {
        var o = new List<(double, double, int, double, int, double)>();
        int np = P.Length / 2, nq = Q.Length / 2;
        for (int i = 0; i < np - 1; i++)
        {
            double ax = P[2 * i], ay = P[2 * i + 1];
            double rx = P[2 * i + 2] - ax, ry = P[2 * i + 3] - ay;
            for (int j = 0; j < nq - 1; j++)
            {
                double cx = Q[2 * j], cy = Q[2 * j + 1];
                double sx = Q[2 * j + 2] - cx, sy = Q[2 * j + 3] - cy;
                double den = rx * sy - ry * sx;
                if (Math.Abs(den) < 1e-12) continue;
                double wx = cx - ax, wy = cy - ay;
                double s = (wx * sy - wy * sx) / den;
                double t = (wx * ry - wy * rx) / den;
                if (0 <= s && s <= 1 && 0 <= t && t <= 1) o.Add((ax + s * rx, ay + s * ry, i, s, j, t));
            }
        }
        return o;
    }

    sealed class EndRd
    {
        public required double[] P, Nx, Ny, W;
        public int Cls;
        public double DirX, DirY, EndX, EndY;
    }

    /// <summary>
    /// Junction corners rounded from the drawn edges: at a node with a junction record (or a join of an added link, with
    /// the square around it: <see cref="RoadLinks.Joins"/>) and 3+ ground-level ends (none
    /// raised, no tunnel), the ends sorted by direction away from the node and grouped into arms (neighbours less than
    /// 25 deg apart); between neighbouring arms, the outer left edge of one and the outer right edge of the next (the
    /// drawn edges, extended 15 m straight past the end) meet at a corner, and the 6 m circle touching both rounds it.
    /// Skipped: an opening outside 25..170 deg, no corner or centre found, a corner more than 10 m outside the junction square.
    /// </summary>
    void EdgeFillets()
    {
        var at = new Dictionary<string, List<(string J, double[] P, double[] W, int Cls, bool Raised)>>(StringComparer.Ordinal);
        foreach (var e in _ends)
        {
            if (!at.TryGetValue(e.J, out var l)) at[e.J] = l = new();
            l.Add(e);
        }
        double twoPi = 2 * Math.PI, armMax = Num.Radians(ArmMax), lo = Num.Radians(25), hi = Num.Radians(170);
        foreach (var (J, ee) in at)
        {
            if (ee.Count < 3 || ee.Any(e => e.Raised)) continue;
            _round.TryGetValue(J, out var sq);
            var rd = new List<EndRd>();
            foreach (var (_, P, W, cls, _) in ee)
            {
                int n = P.Length / 2;
                if (n < 2) continue;
                var tx = new double[n];
                var ty = new double[n];
                for (int j = 0; j < n - 1; j++)
                {
                    double dx = P[2 * j + 2] - P[2 * j], dy = P[2 * j + 3] - P[2 * j + 1];
                    double L = Num.NpHypot(dx, dy);
                    if (L < 1e-9) L = 1e-9;
                    tx[j] = dx / L;
                    ty[j] = dy / L;
                }
                tx[n - 1] = tx[n - 2]; ty[n - 1] = ty[n - 2];
                var P2 = new double[2 * (n + 1)];
                P2[0] = P[0] - tx[0] * EdgeExt;
                P2[1] = P[1] - ty[0] * EdgeExt;
                Array.Copy(P, 0, P2, 2, P.Length);
                var nx = new double[n + 1];
                var ny = new double[n + 1];
                nx[0] = -ty[0]; ny[0] = tx[0];
                for (int j = 0; j < n; j++) { nx[j + 1] = -ty[j]; ny[j + 1] = tx[j]; }
                var W2 = new double[n + 1];
                W2[0] = W[0];
                Array.Copy(W, 0, W2, 1, n);
                rd.Add(new EndRd { P = P2, Nx = nx, Ny = ny, W = W2, Cls = cls, DirX = tx[0], DirY = ty[0], EndX = P[0], EndY = P[1] });
            }
            if (rd.Count < 3) continue;
            rd = rd.OrderBy(r => Math.Atan2(r.DirY, r.DirX)).ToList();
            var ang = rd.Select(r => Math.Atan2(r.DirY, r.DirX)).ToArray();
            var cut = new List<int>();
            for (int k = 0; k < rd.Count; k++)
                if (Num.PyMod(ang[(k + 1) % rd.Count] - ang[k], twoPi) >= armMax) cut.Add(k);
            if (cut.Count < 2) continue;
            var arms = new List<List<EndRd>>();
            for (int j = 0; j < cut.Count; j++)
            {
                int k0 = cut[j], k1 = cut[(j + 1) % cut.Count];
                int cnt = ((k1 - k0) % rd.Count + rd.Count) % rd.Count;
                arms.Add(Enumerable.Range(0, cnt).Select(m => rd[(k0 + 1 + m) % rd.Count]).ToList());
            }
            JunctionsRounded++;
            EndRd Outer(List<EndRd> arm, int side)
            {
                double dmx = 0, dmy = 0;
                foreach (var r in arm) { dmx += r.DirX; dmy += r.DirY; }
                double dn = Num.NpHypot(dmx, dmy) + 1e-9;
                dmx /= dn; dmy /= dn;
                double nmx = -dmy, nmy = dmx;
                int best = 0;
                double bv = 0;
                for (int k = 0; k < arm.Count; k++)
                {
                    var r = arm[k];
                    double ox = r.EndX + side * r.Nx[1] * r.W[1] / 2, oy = r.EndY + side * r.Ny[1] * r.W[1] / 2;
                    double v = ox * nmx + oy * nmy;
                    if (k == 0 || (side > 0 ? v > bv : v < bv)) { bv = v; best = k; }
                }
                return arm[best];
            }
            for (int k = 0; k < arms.Count; k++)
            {
                var A = Outer(arms[k], +1);
                var B = Outer(arms[(k + 1) % arms.Count], -1);
                double th = Num.PyMod(Math.Atan2(B.DirY, B.DirX) - Math.Atan2(A.DirY, A.DirX), twoPi);
                if (th < lo || th > hi) continue;
                var Ae = EdgePlus(A, 0.0);
                var Be = EdgeMinus(B, 0.0);
                var Ao = EdgePlus(A, Radius);
                var Bo = EdgeMinus(B, Radius);
                var xc = SegX(Ae, Be);
                var xo = SegX(Ao, Bo);
                if (xc.Count == 0 || xo.Count == 0) { CornersSkipped++; continue; }
                var c1 = MinBy(xc);
                var o1 = MinBy(xo);
                if (o1.I + o1.S <= c1.I + c1.S || o1.J + o1.T <= c1.J + c1.T) { CornersSkipped++; continue; }
                if (sq is not null)
                {
                    double dx = Math.Max(Math.Max(sq[0] - c1.X, 0.0), c1.X - sq[2]);
                    double dy = Math.Max(Math.Max(sq[1] - c1.Y, 0.0), c1.Y - sq[3]);
                    if (Num.Hypot(dx, dy) > Radius + 4.0) { CornersSkipped++; continue; }
                }
                int ja = o1.I, kb = o1.J;
                double T1x = Ae[2 * ja] + o1.S * (Ae[2 * ja + 2] - Ae[2 * ja]), T1y = Ae[2 * ja + 1] + o1.S * (Ae[2 * ja + 3] - Ae[2 * ja + 1]);
                double T2x = Be[2 * kb] + o1.T * (Be[2 * kb + 2] - Be[2 * kb]), T2y = Be[2 * kb + 1] + o1.T * (Be[2 * kb + 3] - Be[2 * kb + 1]);
                var pa = new List<(double, double)> { (c1.X, c1.Y) };
                for (int i = c1.I + 1; i <= ja; i++) pa.Add((Ae[2 * i], Ae[2 * i + 1]));
                pa.Add((T1x, T1y));
                var pb = new List<(double, double)> { (T2x, T2y) };
                for (int i = kb; i > c1.J; i--) pb.Add((Be[2 * i], Be[2 * i + 1]));
                pb.Add((c1.X, c1.Y));
                double Ox = o1.X, Oy = o1.Y;
                double a1 = Math.Atan2(T1y - Oy, T1x - Ox), a2 = Math.Atan2(T2y - Oy, T2x - Ox);
                double da = Num.PyMod(a2 - a1 + Math.PI, twoPi) - Math.PI;
                int m = Math.Max(4, (int)(Math.Abs(da) * Radius / 0.5));
                var ts = Num.Linspace(0, 1, m + 1);
                var arc = new double[2 * (m + 1)];
                for (int j = 0; j <= m; j++)
                {
                    arc[2 * j] = Ox + Radius * Math.Cos(a1 + da * ts[j]);
                    arc[2 * j + 1] = Oy + Radius * Math.Sin(a1 + da * ts[j]);
                }
                var poly = new List<double>();
                foreach (var (x, y) in pa) { poly.Add(x); poly.Add(y); }
                for (int j = 1; j < m; j++) { poly.Add(arc[2 * j]); poly.Add(arc[2 * j + 1]); }
                foreach (var (x, y) in pb) { poly.Add(x); poly.Add(y); }
                var seam = new List<double>();
                for (int j = pa.Count - 1; j >= 0; j--) { seam.Add(pa[j].Item1); seam.Add(pa[j].Item2); }
                for (int j = 1; j < pb.Count; j++) { seam.Add(pb[j].Item1); seam.Add(pb[j].Item2); }
                int kk = A.Cls == ShapeClass.Highway && B.Cls == ShapeClass.Highway ? ShapeClass.Highway : ShapeClass.Road;
                Corners.Add(new JunctionCorner(kk, poly.ToArray(), arc, seam.ToArray()));
            }
        }

        static double[] EdgePlus(EndRd r, double extra)
        {
            var o = new double[r.P.Length];
            for (int j = 0; j < r.P.Length / 2; j++)
            {
                o[2 * j] = r.P[2 * j] + r.Nx[j] * (r.W[j] / 2 + extra);
                o[2 * j + 1] = r.P[2 * j + 1] + r.Ny[j] * (r.W[j] / 2 + extra);
            }
            return o;
        }
        static double[] EdgeMinus(EndRd r, double extra)
        {
            var o = new double[r.P.Length];
            for (int j = 0; j < r.P.Length / 2; j++)
            {
                o[2 * j] = r.P[2 * j] - r.Nx[j] * (r.W[j] / 2 + extra);
                o[2 * j + 1] = r.P[2 * j + 1] - r.Ny[j] * (r.W[j] / 2 + extra);
            }
            return o;
        }
        static (double X, double Y, int I, double S, int J, double T) MinBy(List<(double X, double Y, int I, double S, int J, double T)> xs)
        {
            var best = xs[0];
            double bk = best.I + best.S + best.J + best.T;
            foreach (var z in xs)
            {
                double k = z.I + z.S + z.J + z.T;
                if (k < bk) { bk = k; best = z; }
            }
            return best;
        }
    }
}
