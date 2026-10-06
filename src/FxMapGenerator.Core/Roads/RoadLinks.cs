using FxMapGenerator.Core.Geometry;

namespace FxMapGenerator.Core.Roads;

/// <summary>Road classes of the drawn links: road (atlas white), highway (yellow), unpaved track (a brown line).</summary>
public static class ShapeClass
{
    public const int Road = 0, Highway = 1, Track = 2;
}

/// <summary>A drawn link: its two ends (x, y, z), width, class, whether it is in a tunnel, whether it was a junction connector.</summary>
public struct LinkRow
{
    public double X0, Y0, Z0, X1, Y1, Z1, Width;
    public int Class;
    public bool Tunnel, DontUseForNavigation;
}

/// <summary>
/// The links drawn as road shapes, as wide as their
/// lanes (a track 5 m; a width the road edits give wins over both). Every drawable
/// link, except the junction connectors at a junction (a node with a junction record and 3+ links): there the arriving
/// road is extended instead - towards the road going on beyond the junction, as one link through the junction when two
/// arriving roads continue each other, to the junction node when the road beyond starts there, to the next junction when
/// a connector between two junctions lies beyond. Then forks (a road splitting into or merging from two roads) push their
/// branches apart and start them side by side, and pairs of links going on straight through a node are marked.
/// </summary>
public sealed class RoadLinks
{
    public const double TrackWidth = 5.0;
    public const double ForkMax = 60.0, SplitMax = 90.0, ForkWalk = 60.0, ContMax = 30.0, ContSame = 60.0, Taper = 30.0;

    public required RoadNet Net { get; init; }
    public Dictionary<string, RoadNode> N => Net.Nodes;
    public List<(string U, string V)> Keys { get; } = new();
    public List<LinkRow> Rows { get; } = new();
    /// <summary>Nodes laid out as forks (a piece ends there, the branches go on).</summary>
    public HashSet<string> Forks { get; } = new(StringComparer.Ordinal);
    /// <summary>(link, node) -> the link that goes on straight through that node.</summary>
    public Dictionary<(int, string), int> Continuation { get; } = new();
    /// <summary>Junction squares by node (the nodes with a junction record).</summary>
    public required Dictionary<string, double[]> Junctions { get; init; }
    /// <summary>
    /// Nodes without a junction record where a link the road edits add meets two or more other drawn links (a road added
    /// in the middle of another, or at an added node): their corners are rounded like a junction's, in a square of
    /// <see cref="JoinSquare"/> m around the node (<see cref="RoadShapes"/>).
    /// </summary>
    public Dictionary<string, double[]> Joins { get; } = new(StringComparer.Ordinal);

    /// <summary>Half the side of the square a join's corners are rounded in (m).</summary>
    public const double JoinSquare = 10.0;

    public int Extended, Aimed, Through, Reached, WithoutArriving, ForkCount, ForkMoved, ContinuationPairs;
    public double ExtensionLength;
    public Dictionary<string, int> Modes { get; } = new(StringComparer.Ordinal);

    public static bool IsExtension(string key) => key.StartsWith("ext|", StringComparison.Ordinal);

    /// <summary>The junction node a connector went to, from an extension's key <c>ext|p|q|s</c>.</summary>
    public static string ExtensionTarget(string key)
    {
        var p = key.Split('|');
        return p[3] == p[1] ? p[2] : p[1];
    }

    static double Acos(double c) => Math.Acos(Math.Max(-1.0, Math.Min(1.0, c)));

    static double AngleDeg(double c) => Num.Degrees(Acos(c));

    public static RoadLinks Build(RoadNet g, Dictionary<string, double[]> junctions, HashSet<(string, string)> highways,
        Dictionary<(string, string), bool> unpaved)
    {
        var L = new RoadLinks { Net = g, Junctions = junctions };
        var N = g.Nodes;
        var dns = new List<(string, string)>();
        foreach (var k in g.Drawable.Order(RoadNet.KeyOrder))
        {
            var e = g.Links[k];
            var na = N[k.Item1];
            var nb = N[k.Item2];
            bool dn = e.Records.Any(r => r.DontUseForNavigation);
            bool off = unpaved[k];
            int cls = off ? ShapeClass.Track : highways.Contains(k) ? ShapeClass.Highway : ShapeClass.Road;
            bool tun = na.Tunnel && nb.Tunnel;
            if (dn)
            {
                string mode = (junctions.ContainsKey(k.Item1) && g.Degree(k.Item1) >= 3) || (junctions.ContainsKey(k.Item2) && g.Degree(k.Item2) >= 3) ? "extend" : "draw";
                L.Modes[mode] = L.Modes.GetValueOrDefault(mode) + 1;
                if (mode == "extend") { dns.Add(k); continue; }
            }
            double w = e.Width ?? (off ? TrackWidth : RoadNet.LaneWidthOf(e));
            L.Keys.Add(k);
            L.Rows.Add(new LinkRow { X0 = na.X, Y0 = na.Y, Z0 = na.Z, X1 = nb.X, Y1 = nb.Y, Z1 = nb.Z, Width = w, Class = cls, Tunnel = tun, DontUseForNavigation = dn });
        }
        foreach (var k in g.Drawable.Order(RoadNet.KeyOrder))
        {
            if (!g.Links[k].Added) continue;
            foreach (var n in new[] { k.Item1, k.Item2 })
                if (!junctions.ContainsKey(n) && g.Degree(n) >= 3 && !L.Joins.ContainsKey(n))
                    L.Joins[n] = [N[n].X - JoinSquare, N[n].Y - JoinSquare, N[n].X + JoinSquare, N[n].Y + JoinSquare];
        }
        if (dns.Count > 0) L.Extend(dns);
        L.ForkLanes();
        L.Continuations();
        return L;
    }

    (double X, double Y) Pn(string n) => (N[n].X, N[n].Y);

    sealed record Plan(string T, (double X, double Y) DIn, int I, string? Mc);

    void Extend(List<(string, string)> dns)
    {
        var g = Net;
        var at = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < Keys.Count; i++)
        {
            foreach (var n in new[] { Keys[i].U, Keys[i].V })
            {
                if (!at.TryGetValue(n, out var l)) at[n] = l = new List<int>();
                l.Add(i);
            }
        }
        var dnsSet = dns.ToHashSet();
        var plan = new List<(((string, string) K, string S) Key, Plan P)>();
        var planIndex = new Dictionary<((string, string), string), Plan>();
        foreach (var (p, q) in dns)
        {
            foreach (var (s, t) in new[] { (p, q), (q, p) })
            {
                if (N[s].Junction || !at.TryGetValue(s, out var arriving) || arriving.Count == 0) continue;
                var ps = Pn(s);
                var pt = Pn(t);
                double vx = pt.X - ps.X, vy = pt.Y - ps.Y;
                double lv = Num.NpHypot(vx, vy);
                if (lv < 1e-6) continue;
                (double Turn, (double X, double Y) DIn, int I)? best = null;
                foreach (var i in arriving)
                {
                    var r = Rows[i];
                    var pm = Keys[i].V == s ? (r.X0, r.Y0) : (r.X1, r.Y1);
                    double dx = ps.X - pm.Item1, dy = ps.Y - pm.Item2;
                    double ld = Num.NpHypot(dx, dy);
                    if (ld < 1e-6) continue;
                    dx /= ld;
                    dy /= ld;
                    double turn = AngleDeg((dx * vx + dy * vy) / lv);
                    if (best is null || turn < best.Value.Turn) best = (turn, (dx, dy), i);
                }
                if (best is null) continue;
                var dIn = best.Value.DIn;
                string? mc = null;
                if (Junctions.ContainsKey(t))
                {
                    var (fin, fout) = g.LanesFrom(s, t);
                    string st0 = g.Street(N[s]);
                    (int Same, int LaneDiff, double Ang, string M)? cbest = null;
                    foreach (var m in g.Neighbours(t))
                    {
                        if (m == s) continue;
                        var pmm = Pn(m);
                        double wx = pmm.X - pt.X, wy = pmm.Y - pt.Y;
                        double lm = Num.NpHypot(wx, wy);
                        if (lm < 1e-6) continue;
                        double ang = AngleDeg((dIn.X * wx + dIn.Y * wy) / lm);
                        bool same = st0.Length > 0 && g.Street(N[m]) == st0;
                        if (ang > (same ? ContSame : ContMax)) continue;
                        var (lo, li) = g.LanesFrom(t, m);
                        if ((fin > 0 && lo == 0) || (fout > 0 && li == 0)) continue;
                        int laneDiff = (fin > 0 ? lo : li) == Math.Max(fin, fout) ? 0 : 1;
                        var c = (same ? 0 : 1, laneDiff, ang, m);
                        if (cbest is null || Less(c, cbest.Value)) cbest = c;
                    }
                    mc = cbest?.M;
                }
                var key = ((p, q), s);
                var pl = new Plan(t, dIn, best.Value.I, mc);
                plan.Add((key, pl));
                planIndex[key] = pl;
            }
        }
        var done = new HashSet<((string, string), string)>();
        foreach (var ((pq, s), pl) in plan)
        {
            if (done.Contains((pq, s))) continue;
            var (t, dIn, i, mc) = (pl.T, pl.DIn, pl.I, pl.Mc);
            var ps = Pn(s);
            var pt = Pn(t);
            (string, string)? k2 = mc is null ? null : RoadNet.Key(t, mc);
            Plan? back = k2 is { } kk2 && dnsSet.Contains(kk2) ? planIndex.GetValueOrDefault((kk2, mc!)) : null;
            if (back is not null && back.T == t && (back.Mc == s || back.Mc is null))
            {
                done.Add((pq, s));
                done.Add((k2!.Value, mc!));
                int j2 = back.I;
                var pm = Pn(mc!);
                int cls = Rows[i].Class == Rows[j2].Class ? Rows[i].Class : ShapeClass.Road;
                double dx = pm.X - ps.X, dy = pm.Y - ps.Y;
                double f = Math.Min(0.95, Math.Max(0.05, ((pt.X - ps.X) * dx + (pt.Y - ps.Y) * dy) / Math.Max(dx * dx + dy * dy, 1e-9)));
                double xx = ps.X + f * dx, xy = ps.Y + f * dy;
                string xn = $"{t}|thr|{s}";
                double zx = N[s].Z + f * (N[mc!].Z - N[s].Z);
                var nn = N[t].Copy();
                nn.X = xx; nn.Y = xy; nn.Z = zx;
                N[xn] = nn;
                double wt = (Rows[i].Width + Rows[j2].Width) / 2;
                bool tun = Rows[i].Tunnel && Rows[j2].Tunnel;
                Keys.Add((s, xn));
                Rows.Add(new LinkRow { X0 = ps.X, Y0 = ps.Y, Z0 = N[s].Z, X1 = xx, Y1 = xy, Z1 = zx, Width = wt, Class = cls, Tunnel = tun, DontUseForNavigation = true });
                Keys.Add((xn, mc!));
                Rows.Add(new LinkRow { X0 = xx, Y0 = xy, Z0 = zx, X1 = pm.X, Y1 = pm.Y, Z1 = N[mc!].Z, Width = wt, Class = cls, Tunnel = tun, DontUseForNavigation = true });
                Through++;
                ExtensionLength += Num.NpHypot(pm.X - ps.X, pm.Y - ps.Y);
                continue;
            }
            done.Add((pq, s));
            var ri = Rows[i];
            if (mc is not null && dnsSet.Contains(k2!.Value) && back is null && N[t].Junction && N[mc].Junction)
            {
                var pm = Pn(mc);
                Keys.Add((s, mc));
                Rows.Add(new LinkRow { X0 = ps.X, Y0 = ps.Y, Z0 = N[s].Z, X1 = pm.X, Y1 = pm.Y, Z1 = N[mc].Z, Width = ri.Width, Class = ri.Class, Tunnel = ri.Tunnel, DontUseForNavigation = true });
                Reached++;
                ExtensionLength += Num.NpHypot(pm.X - ps.X, pm.Y - ps.Y);
                continue;
            }
            if (mc is not null && !dnsSet.Contains(k2!.Value))
            {
                Keys.Add((s, t));
                Rows.Add(new LinkRow { X0 = ps.X, Y0 = ps.Y, Z0 = N[s].Z, X1 = pt.X, Y1 = pt.Y, Z1 = N[t].Z, Width = ri.Width, Class = ri.Class, Tunnel = ri.Tunnel, DontUseForNavigation = true });
                Reached++;
                ExtensionLength += Num.NpHypot(pt.X - ps.X, pt.Y - ps.Y);
                continue;
            }
            double vx = pt.X - ps.X, vy = pt.Y - ps.Y;
            double lv = Num.NpHypot(vx, vy);
            var u = dIn;
            if (mc is not null)
            {
                var pm = Pn(mc);
                double ux = pm.X - ps.X, uy = pm.Y - ps.Y;
                double nu = Num.NpHypot(ux, uy) + 1e-9;
                u = (ux / nu, uy / nu);
                Aimed++;
            }
            double ln = Math.Min(lv, u.X * vx + u.Y * vy);
            if (ln < 1.0) continue;
            double zs = N[s].Z, zt = N[t].Z;
            double qx = ps.X + u.X * ln, qy = ps.Y + u.Y * ln;
            Keys.Add((s, $"ext|{pq.Item1}|{pq.Item2}|{s}"));
            Rows.Add(new LinkRow { X0 = ps.X, Y0 = ps.Y, Z0 = zs, X1 = qx, Y1 = qy, Z1 = zs + (zt - zs) * ln / lv, Width = ri.Width, Class = ri.Class, Tunnel = ri.Tunnel, DontUseForNavigation = true });
            Extended++;
            ExtensionLength += ln;
        }
        var madeFor = done.Select(d => d.Item1).ToHashSet();
        WithoutArriving = dns.Count(pq => !madeFor.Contains(pq));
    }

    static bool Less((int, int, double, string) a, (int, int, double, string) b)
    {
        if (a.Item1 != b.Item1) return a.Item1 < b.Item1;
        if (a.Item2 != b.Item2) return a.Item2 < b.Item2;
        if (a.Item3 != b.Item3) return a.Item3 < b.Item3;
        return string.CompareOrdinal(a.Item4, b.Item4) < 0;
    }

    // ------------------------------------------------------------------------------------------------ forks

    void SetEnd(int i, bool start, double x, double y)
    {
        var r = Rows[i];
        if (start) { r.X0 = x; r.Y0 = y; }
        else { r.X1 = x; r.Y1 = y; }
        Rows[i] = r;
    }

    /// <summary>
    /// At a node where a one-way trunk forks into (or merges from) two one-way branches of the same flow within 60 deg,
    /// or a two-way road splits into two opposite one-way roads (within 90 deg; also at a junction with more links when
    /// the branches carry the trunk's lanes and the two-way road does not go on straight into another two-way road): the
    /// branch ends sit side by side, their outer span centred on the trunk, and the branch nodes are pushed apart (down
    /// each branch through 2-link nodes, at most 60 m) where they would overlap. When the branches together are wider
    /// than the trunk the straighter one keeps its line; when narrower, the trunk narrows to them over its last 30 m.
    /// </summary>
    void ForkLanes()
    {
        var g = Net;
        var at = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var extFrom = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var orig = Keys.ToList();
        List<int> At(string n) => at.TryGetValue(n, out var l) ? l : (at[n] = new List<int>());
        for (int i = 0; i < Keys.Count; i++)
        {
            var (u, v) = Keys[i];
            if (IsExtension(v))
            {
                if (!extFrom.TryGetValue(u, out var l)) extFrom[u] = l = new List<int>();
                l.Add(i);
                continue;
            }
            At(u).Add(i);
            At(v).Add(i);
        }
        string OtherEnd(int i, string n) => orig[i].Item1 == n ? orig[i].Item2 : orig[i].Item1;
        var moved = new HashSet<string>(StringComparer.Ordinal);
        int nFork = 0;
        double cosForkMax = Math.Cos(Num.Radians(ForkMax)), cosSplitMax = Math.Cos(Num.Radians(SplitMax));

        List<(string M, int I, double Dist)> Walk(string t, int i0)
        {
            var o = new List<(string, int, double)>();
            string n = t;
            int i = i0;
            double dist = 0.0;
            while (true)
            {
                string m = OtherEnd(i, n);
                dist += Num.Hypot(N[m].X - N[n].X, N[m].Y - N[n].Y);
                o.Add((m, i, dist));
                var am = At(m);
                if (dist > ForkWalk || am.Count != 2 || moved.Contains(m)) return o;
                i = am[0] == i ? am[1] : am[0];
                n = m;
            }
        }
        (double X, double Y) Unit(double x, double y)
        {
            double d = Num.NpHypot(x, y) + 1e-9;
            return (x / d, y / d);
        }

        foreach (var (t, iiAll) in at.ToList())
        {
            var ii = iiAll;
            if (ii.Count < 3 || N[t].Unpaved || moved.Contains(t) || t.Contains('|')) continue;
            if (ii.Count > 3)
            {
                var real = ii.Where(i => g.Links.ContainsKey(RoadNet.Key(orig[i].Item1, orig[i].Item2))).ToList();
                var flx = real.ToDictionary(i => i, i => g.LanesFrom(t, OtherEnd(i, t)));
                var dirs = real.ToDictionary(i => i, i => Unit(N[OtherEnd(i, t)].X - N[t].X, N[OtherEnd(i, t)].Y - N[t].Y));
                var tws = real.Where(i => flx[i].Out > 0 && flx[i].In > 0).ToList();
                var ows = real.Where(i => (flx[i].Out > 0) != (flx[i].In > 0)).ToList();
                (double Dev, List<int> Ii)? best = null;
                for (int x1 = 0; x1 < ows.Count; x1++)
                    for (int x2 = x1 + 1; x2 < ows.Count; x2++)
                    {
                        int b1 = ows[x1], b2 = ows[x2];
                        if ((flx[b1].Out > 0) == (flx[b2].Out > 0) || dirs[b1].X * dirs[b2].X + dirs[b1].Y * dirs[b2].Y < cosForkMax) continue;
                        var mid = Unit(dirs[b1].X + dirs[b2].X, dirs[b1].Y + dirs[b2].Y);
                        foreach (var tr in tws)
                        {
                            if (!(flx[tr].In == flx[b1].Out + flx[b2].Out && flx[tr].Out == flx[b1].In + flx[b2].In)) continue;
                            double dev = AngleDeg(-dirs[tr].X * mid.X + -dirs[tr].Y * mid.Y);
                            if (tws.Any(o => o != tr && AngleDeg(-dirs[tr].X * dirs[o].X + -dirs[tr].Y * dirs[o].Y) <= ContMax)) continue;
                            if (dev <= ContMax && (best is null || dev < best.Value.Dev)) best = (dev, [tr, b1, b2]);
                        }
                    }
                if (best is null) continue;
                ii = best.Value.Ii;
            }
            if (ii.Any(i => !g.Links.ContainsKey(RoadNet.Key(orig[i].Item1, orig[i].Item2)))) continue;
            var fl = ii.ToDictionary(i => i, i => g.LanesFrom(t, OtherEnd(i, t)));
            var twoWay = ii.Where(i => fl[i].Out > 0 && fl[i].In > 0).ToList();
            if (twoWay.Count > 1) continue;
            foreach (var tr in ii)
            {
                var br = ii.Where(i => i != tr).ToList();
                if (twoWay.Count > 0)
                {
                    if (tr != twoWay[0] || !br.All(i => (fl[i].Out > 0) != (fl[i].In > 0)) || (fl[br[0]].Out > 0) == (fl[br[1]].Out > 0)) continue;
                }
                else if (!((fl[tr].In > 0 && br.All(i => fl[i].Out > 0)) || (fl[tr].Out > 0 && br.All(i => fl[i].In > 0)))) continue;
                var P = (X: N[t].X, Y: N[t].Y);
                var vb = br.Select(i => Unit(N[OtherEnd(i, t)].X - P.X, N[OtherEnd(i, t)].Y - P.Y)).ToList();
                double vbDot = vb[0].X * vb[1].X + vb[0].Y * vb[1].Y;
                if (vbDot < (twoWay.Count > 0 ? cosSplitMax : cosForkMax)) continue;
                var q = (X: N[OtherEnd(tr, t)].X, Y: N[OtherEnd(tr, t)].Y);
                var d = Unit(P.X - q.X, P.Y - q.Y);
                var nl = (X: -d.Y, Y: d.X);
                var ws = br.Select(i => Rows[i].Width).ToList();
                double wt = Rows[tr].Width;
                double sepFull = (ws[0] + ws[1]) / 2;
                double sep0 = Math.Min(sepFull, Math.Max(0.0, wt - sepFull));
                var W = br.Select(i => Walk(t, i)).ToList();
                var prof = new List<(List<double> Al, List<double> Lat)>();
                foreach (var wk in W)
                {
                    var al = new List<double> { 0.0 };
                    var lat = new List<double> { 0.0 };
                    foreach (var (m, _, _) in wk)
                    {
                        double mx = N[m].X - P.X, my = N[m].Y - P.Y;
                        al.Add(mx * d.X + my * d.Y);
                        lat.Add(mx * nl.X + my * nl.Y);
                    }
                    prof.Add((al, lat));
                }
                if (twoWay.Count > 0 && vbDot < cosForkMax)
                {
                    double minFirst = 0.0;
                    bool any = false;
                    foreach (var pr in prof)
                        if (pr.Al.Count > 1) { minFirst = any ? Math.Min(minFirst, pr.Al[1]) : pr.Al[1]; any = true; }
                    if ((any ? minFirst : 0.0) < 1.0) continue;
                }
                double LatAt(int b, double s)
                {
                    int guess = 0;
                    return Num.NpInterp(s, prof[b].Al, prof[b].Lat, ref guess);
                }
                double minLast = prof[0].Al[^1];
                for (int b = 1; b < prof.Count; b++) if (prof[b].Al[^1] < minLast) minLast = prof[b].Al[^1];
                double refS = Math.Min(15.0, Math.Max(1.0, minLast));
                int left = LatAt(0, refS) >= LatAt(1, refS) ? 0 : 1;
                if (Math.Abs(LatAt(0, refS) - LatAt(1, refS)) < 0.2)
                    left = vb[0].X * nl.X + vb[0].Y * nl.Y >= vb[1].X * nl.X + vb[1].Y * nl.Y ? 0 : 1;
                var share = new[] { 0.5, 0.5 };
                if (ws[0] + ws[1] > wt + 0.5)
                {
                    int keep = vb[0].X * d.X + vb[0].Y * d.Y >= vb[1].X * d.X + vb[1].Y * d.Y ? 0 : 1;
                    share = [0.0, 0.0];
                    share[1 - keep] = 1.0;
                }
                double Push(double s)
                {
                    double want = sep0 + (sepFull - sep0) * Math.Min(1.0, s / Math.Max(Taper, 1e-6));
                    double have = LatAt(left, s) - LatAt(1 - left, s);
                    return Math.Max(0.0, want - Math.Max(0.0, have));
                }
                double wl = ws[left], wr = ws[1 - left];
                double cLeft = (sep0 + (wr - wl) / 2) / 2;
                if (sep0 + sepFull < wt - 0.5) N[t].ForkWidth = sep0 + sepFull;
                N[t].ForkTrunk = tr;
                var start = new double[2];
                start[left] = cLeft;
                start[1 - left] = cLeft - sep0;
                var qn = q;
                for (int j = 0; j < br.Count; j++)
                {
                    int i = br[j];
                    double sign = j == left ? 1.0 : -1.0;
                    double off0 = start[j];
                    double qx = P.X + nl.X * off0, qy = P.Y + nl.Y * off0;
                    string pn = $"{t}|{OtherEnd(i, t)}";
                    var nn = N[t].Copy();
                    nn.X = qx; nn.Y = qy;
                    nn.Phantom = (qn.X + nl.X * off0, qn.Y + nl.Y * off0, N[t].Z);
                    N[pn] = nn;
                    if (orig[i].Item1 == t) { Keys[i] = (pn, Keys[i].V); SetEnd(i, true, qx, qy); }
                    else { Keys[i] = (Keys[i].U, pn); SetEnd(i, false, qx, qy); }
                    double L1 = prof[j].Al.Count > 1 ? Math.Max(prof[j].Al[1], 1e-6) : 1e-6;
                    for (int w = 0; w < W[j].Count && w + 1 < prof[j].Al.Count; w++)
                    {
                        string m = W[j][w].M;
                        double al = prof[j].Al[w + 1];
                        double off = off0 * Math.Max(0.0, 1.0 - al / L1) + sign * share[j] * Push(al);
                        if (Math.Abs(off) < 1e-9 || moved.Contains(m) || At(m).Count != 2) break;
                        double nx = N[m].X + nl.X * off, ny = N[m].Y + nl.Y * off;
                        foreach (var k in At(m).Concat(extFrom.TryGetValue(m, out var ef) ? ef : []))
                            SetEnd(k, orig[k].Item1 == m, nx, ny);
                        var mm = N[m].Copy();
                        mm.X = nx; mm.Y = ny;
                        N[m] = mm;
                        moved.Add(m);
                    }
                }
                moved.Add(t);
                Forks.Add(t);
                nFork++;
                break;
            }
        }
        ForkCount = nFork;
        ForkMoved = moved.Count - nFork;
    }

    // ------------------------------------------------------------------------------------------------ continuations

    /// <summary>
    /// At every node with 3+ drawn links (not a fork node), the links that go on straight through it are paired - within
    /// 30 deg, or 60 deg with the same street name at their far nodes; flows going on, the same tunnel state, no track
    /// with a road; the same name first, then the same kind (two-way with two-way), then the straightest.
    /// </summary>
    void Continuations()
    {
        var g = Net;
        Continuation.Clear();
        var at = new Dictionary<string, List<(int I, int E)>>(StringComparer.Ordinal);
        for (int i = 0; i < Keys.Count; i++)
        {
            var (u, v) = Keys[i];
            if (!at.TryGetValue(u, out var lu)) at[u] = lu = new List<(int, int)>();
            lu.Add((i, 0));
            if (!IsExtension(v))
            {
                if (!at.TryGetValue(v, out var lv)) at[v] = lv = new List<(int, int)>();
                lv.Add((i, 1));
            }
        }
        int nPairs = 0;
        foreach (var (n, ee) in at)
        {
            if (ee.Count < 3 || Forks.Contains(n) || n.Contains('|')) continue;
            var info = new List<(int I, double Dx, double Dy, string Name, (int Out, int In)? Fl, int Cls, bool Tun)>();
            foreach (var (i, e) in ee)
            {
                var r = Rows[i];
                double dx = e == 0 ? r.X1 - r.X0 : r.X0 - r.X1, dy = e == 0 ? r.Y1 - r.Y0 : r.Y0 - r.Y1;
                double ln = Num.NpHypot(dx, dy);
                if (ln < 1e-6) continue;
                string far = e == 0 ? Keys[i].V : Keys[i].U;
                string? fb = IsExtension(far) ? null : far.Split('|')[0];
                string nm = fb is not null && fb.Length > 0 && N.ContainsKey(fb) && !far.Contains('|') ? g.Street(N[fb]) : "";
                (int, int)? flw = fb is not null && fb.Length > 0 && g.Links.ContainsKey(RoadNet.Key(n, fb)) ? g.LanesFrom(n, fb) : null;
                info.Add((i, dx / ln, dy / ln, nm, flw, r.Class, r.Tunnel));
            }
            var cand = new List<(int Same, int KindDiff, double Ang, int I1, int I2)>();
            for (int x = 0; x < info.Count; x++)
                for (int y = x + 1; y < info.Count; y++)
                {
                    var a = info[x];
                    var b = info[y];
                    if (a.I == b.I || a.Tun != b.Tun || (a.Cls == ShapeClass.Track) != (b.Cls == ShapeClass.Track)) continue;
                    if (a.Fl is { } f1 && b.Fl is { } f2 && !((f1.In > 0 && f2.Out > 0) || (f1.Out > 0 && f2.In > 0))) continue;
                    double ang = AngleDeg(-a.Dx * b.Dx + -a.Dy * b.Dy);
                    bool same = a.Name.Length > 0 && a.Name == b.Name;
                    int kindDiff = a.Fl is { } g1 && b.Fl is { } g2 && ((g1.Out > 0 && g1.In > 0) != (g2.Out > 0 && g2.In > 0)) ? 1 : 0;
                    if (ang <= (same ? ContSame : ContMax)) cand.Add((same ? 0 : 1, kindDiff, ang, a.I, b.I));
                }
            cand.Sort();
            var used = new HashSet<int>();
            foreach (var (_, _, _, i1, i2) in cand)
            {
                if (used.Contains(i1) || used.Contains(i2)) continue;
                used.Add(i1);
                used.Add(i2);
                Continuation[(i1, n)] = i2;
                Continuation[(i2, n)] = i1;
                nPairs++;
            }
        }
        ContinuationPairs = nPairs;
    }
}
