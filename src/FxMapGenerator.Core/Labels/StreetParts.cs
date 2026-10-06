using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.RoadGraph;

namespace FxMapGenerator.Core.Labels;

/// <summary>A road of the graph as the street names see it (copied with changes, never changed in place).</summary>
public sealed record StreetChain(RoadClass Class, uint Street, IReadOnlyList<LinePoint> Points, double Length, bool Divided, bool OneWay,
    bool Merged = false, bool Ramp = false)
{
    public bool IsOneWay => Divided || OneWay;
}

/// <summary>A stretch of line a street name may be written along.</summary>
/// <param name="Midline">The middle line of the two carriageways of a divided road.</param>
/// <param name="Shared">The other street whose main line runs alongside (the name goes elsewhere first); null = none.</param>
/// <param name="Ramp">One-way but not a main line of the name: a ramp or a side lane.</param>
public sealed record StreetPart(List<LinePoint> Points, bool Midline, string? Shared, bool Ramp, RoadClass Class);

/// <summary>
/// The lines of one street name: the one-way roads chained by straightness (or, when they never meet end to end, the
/// centre lines of their shape), the main lines paired into carriageway midlines, everything joined end to end, cut at
/// sharp corners, and cut where another street's main line runs alongside.
/// </summary>
public static class StreetParts
{
    const double JoinM = 3.0, MaxTurn = 60.0, PairGap = 80.0, SharedGap = 45.0;

    public static int Rank(RoadClass c) => c switch
    {
        RoadClass.Highway => 4,
        RoadClass.Major => 3,
        RoadClass.Street => 2,
        RoadClass.Minor or RoadClass.Track => 1,
        _ => 0,
    };

    /// <summary>The class of the highest rank (the first of equals).</summary>
    public static RoadClass TopClass(IEnumerable<StreetChain> chains)
    {
        StreetChain? best = null;
        foreach (var c in chains)
            if (best is null || Rank(c.Class) > Rank(best.Class)) best = c;
        return best!.Class;
    }

    static StreetChain TopChain(IEnumerable<StreetChain> chains)
    {
        StreetChain? best = null;
        foreach (var c in chains)
            if (best is null || Rank(c.Class) > Rank(best.Class)) best = c;
        return best!;
    }

    static bool Alongside(double h, double hb) => Math.Min(LabelLines.Turn(h, hb), LabelLines.Turn(h, hb + 180.0)) <= 30.0;

    static List<LinePoint> Reversed(IReadOnlyList<LinePoint> l)
    {
        var o = new List<LinePoint>(l.Count);
        for (int i = l.Count - 1; i >= 0; i--) o.Add(l[i]);
        return o;
    }

    /// <summary>
    /// Chains joined end to end where their ends meet (within <paramref name="joinM"/>) without turning more than
    /// <paramref name="maxTurn"/>; the straightest continuation first. <paramref name="sameFlag"/>: a midline never joins
    /// a single line.
    /// </summary>
    public static List<(List<LinePoint> Line, bool Midline, List<StreetChain> Members)> Merge(IReadOnlyList<StreetChain> chains, double joinM = JoinM,
        double maxTurn = MaxTurn, bool sameFlag = false)
    {
        var keep = new List<StreetChain>();
        var lines = new List<List<LinePoint>>();
        foreach (var c in chains)
        {
            if (c.Points.Count < 2) continue;
            var l = LabelLines.Dedupe(c.Points);
            if (l.Count < 2) continue;
            keep.Add(c);
            lines.Add(l);
        }
        var flags = keep.Select(c => c.Merged).ToList();
        var used = new bool[lines.Count];
        var o = new List<(List<LinePoint>, bool, List<StreetChain>)>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var cur = new List<LinePoint>(lines[i]);
            bool flag = flags[i];
            var members = new List<StreetChain> { keep[i] };
            foreach (bool tail in (ReadOnlySpan<bool>)[true, false])
                while (true)
                {
                    var (p, h) = tail ? (cur[^1], LabelLines.Heading(cur[^2], cur[^1])) : (cur[0], LabelLines.Heading(cur[1], cur[0]));
                    (double T, int J, List<LinePoint> Seg)? best = null;
                    for (int j = 0; j < lines.Count; j++)
                    {
                        if (used[j] || sameFlag && flags[j] != flags[i]) continue;
                        var l = lines[j];
                        foreach (bool flip in (ReadOnlySpan<bool>)[false, true])
                        {
                            var q = flip ? l[^1] : l[0];
                            if (Num.Hypot(q.X - p.X, q.Y - p.Y) > joinM) continue;
                            var seg = flip ? Reversed(l) : l;
                            double h2 = tail ? LabelLines.Heading(seg[0], seg[1]) : LabelLines.Heading(seg[1], seg[0]);
                            double t = LabelLines.Turn(h, h2);
                            if (t <= maxTurn && (best is null || t < best.Value.T)) best = (t, j, seg);
                        }
                    }
                    if (best is not { } b) break;
                    used[b.J] = true;
                    flag = flag || flags[b.J];
                    members.Add(keep[b.J]);
                    if (tail) cur.AddRange(b.Seg.Skip(1));
                    else
                    {
                        var head = Reversed(b.Seg);
                        head.RemoveAt(head.Count - 1);
                        head.AddRange(cur);
                        cur = head;
                    }
                }
            o.Add((cur, flag, members));
        }
        return o;
    }

    /// <summary>
    /// One-way chains chained again by straightness: a chain end touching the inside of another chain cuts it there; at
    /// every meeting point the two pieces that go on most nearly straight are paired; the pairs are walked into lines.
    /// </summary>
    public static List<(List<LinePoint> Line, List<StreetChain> Members)> Rechain(IReadOnlyList<StreetChain> chains, double joinM = JoinM, double maxTurn = MaxTurn)
    {
        var pieces = new List<(List<LinePoint> Pts, StreetChain C)>();
        foreach (var c in chains)
        {
            var pts = LabelLines.Dedupe(c.Points);
            if (pts.Count >= 2) pieces.Add((pts, c));
        }
        // 1. cuts where another piece's end touches the inside
        var ends = pieces.SelectMany(pc => new[] { pc.Pts[0], pc.Pts[^1] }).ToList();
        int n0 = pieces.Count;
        for (int i = 0; i < n0; i++)
        {
            var (pts, c) = pieces[i];
            var cuts = new List<(double S, bool Np)>();
            foreach (var e in ends)
            {
                if (Num.Hypot(e.X - pts[0].X, e.Y - pts[0].Y) <= joinM || Num.Hypot(e.X - pts[^1].X, e.Y - pts[^1].Y) <= joinM) continue;
                var (d, qx, qy, _) = LabelLines.Nearest(e.X, e.Y, pts);
                if (d > joinM) continue;
                var cum = LabelLines.Cum(pts);
                var (s, np) = LabelLines.ArcOf(pts, cum, qx, qy);
                if (joinM < s && s < cum[^1] - joinM) cuts.Add(np ? (Num.NpRound(s, 1), true) : (Num.Round(s, 1), false));
            }
            if (cuts.Count == 0) continue;
            var cm = LabelLines.Cum(pts);
            var segs = new List<List<LinePoint>>();
            // the distinct positions (the first of equal values keeps its kind), in order
            var distinct = new List<(double S, bool Np)>();
            foreach (var cut in cuts)
                if (!distinct.Any(d => d.S == cut.S)) distinct.Add(cut);
            double s0 = 0.0;
            bool np0 = false;
            foreach (var (s, np) in distinct.OrderBy(d => d.S))
            {
                segs.Add(LabelLines.Slice(pts, cm, s0, s, np0, np));
                (s0, np0) = (s, np);
            }
            segs.Add(LabelLines.Slice(pts, cm, s0, cm[^1], np0, false));
            pieces[i] = (segs[0], c);
            foreach (var sg in segs.Skip(1))
                if (sg.Count >= 2) pieces.Add((sg, c));
        }
        pieces = pieces.Where(pc => pc.Pts.Count >= 2).ToList();
        // 2. the ends paired by straightness
        var E = new List<(int I, LinePoint P, double H)>();
        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i].Pts;
            E.Add((i, p[0], LabelLines.Heading(p[1], p[0])));
            E.Add((i, p[^1], LabelLines.Heading(p[^2], p[^1])));
        }
        var cand = new List<(double T, double D, int A, int B)>();
        for (int a = 0; a < E.Count; a++)
            for (int b = a + 1; b < E.Count; b++)
            {
                if (E[a].I == E[b].I) continue;
                double d = Num.Hypot(E[a].P.X - E[b].P.X, E[a].P.Y - E[b].P.Y);
                if (d > joinM) continue;
                double t = LabelLines.Turn(E[a].H, E[b].H + 180.0);
                if (t <= maxTurn) cand.Add((t, d, a, b));
            }
        cand.Sort((x, y) => x.T != y.T ? x.T.CompareTo(y.T) : x.D != y.D ? x.D.CompareTo(y.D) : x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        var mate = new Dictionary<int, int>();
        foreach (var (_, _, a, b) in cand)
        {
            if (mate.ContainsKey(a) || mate.ContainsKey(b)) continue;
            mate[a] = b;
            mate[b] = a;
        }
        // 3. the pairs walked into lines
        var usedP = new bool[pieces.Count];
        var o = new List<(List<LinePoint>, List<StreetChain>)>();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (usedP[i]) continue;
            int cur = i, end = 0;
            var seen = new HashSet<int> { i };
            while (mate.TryGetValue(cur * 2 + end, out int nxt))
            {
                int ni = nxt / 2, ne = nxt % 2;
                if (!seen.Add(ni)) break;
                (cur, end) = (ni, 1 - ne);
            }
            int start = cur, sidx = 1 - end;
            var line = new List<LinePoint>(sidx == 1 ? pieces[start].Pts : Reversed(pieces[start].Pts));
            var members = new List<StreetChain> { pieces[start].C };
            usedP[start] = true;
            (cur, end) = (start, sidx);
            while (mate.TryGetValue(cur * 2 + end, out int nxt))
            {
                int ni = nxt / 2, ne = nxt % 2;
                if (usedP[ni]) break;
                var pts = ne == 0 ? pieces[ni].Pts : Reversed(pieces[ni].Pts);
                line.AddRange(Num.Hypot(pts[0].X - line[^1].X, pts[0].Y - line[^1].Y) > 0.05 ? pts : pts.Skip(1));
                members.Add(pieces[ni].C);
                usedP[ni] = true;
                (cur, end) = (ni, 1 - ne);
            }
            o.Add((LabelLines.Dedupe(line), members));
        }
        return o;
    }

    /// <summary>
    /// Centre lines of a street from the shape of its chains (for boulevards whose one-way pieces never meet end to end):
    /// the chains drawn 2 m a pixel, closed with a disc of <paramref name="closeM"/>, thinned, and cut into segments at
    /// the junction pixels; spurs shorter than 40 m are dropped; every segment smoothed.
    /// </summary>
    public static List<List<LinePoint>> SkeletonLines(IReadOnlyList<StreetChain> chains, double closeM = 12.0)
    {
        const double px = 2.0, pruneM = 40.0;
        double m = closeM * 2 + 10.0;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var c in chains)
            foreach (var p in c.Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        double x0 = minX - m, y1 = minY - m, x1 = maxX + m, y0 = maxY + m;
        int W = (int)((x1 - x0) / px) + 1, H = (int)((y0 - y1) / px) + 1;
        var img = new Grid<bool>(W, H);
        foreach (var c in chains)
            PilDraw.Line(img, c.Points.Select(p => ((p.X - x0) / px, (y0 - p.Y) / px)).ToList(), 2);
        var closed = Morphology.CloseEdgeOff(img, Kernel.Disk(Math.Max(1, (int)Math.Round(closeM / px, MidpointRounding.ToEven))));
        var skel = Skeleton.Zhang(closed);
        var pixels = new List<(int, int)>();
        for (int r = 0; r < H; r++)
            for (int col = 0; col < W; col++)
                if (skel[r, col]) pixels.Add((col, r));
        var on = PixelSet.Of(pixels);
        List<(int X, int Y)> Nb((int X, int Y) p, Func<(int X, int Y), bool> keep)
        {
            var o = new List<(int, int)>(8);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if ((dx != 0 || dy != 0) && on.Contains(p.X + dx, p.Y + dy) && keep((p.X + dx, p.Y + dy))) o.Add((p.X + dx, p.Y + dy));
            return o;
        }
        var junction = PixelSet.Of(on.Items().Where(p => Nb(p, _ => true).Count >= 3).ToList());
        var rest = PixelSet.Difference(on, junction);
        var seen = new HashSet<(int, int)>();
        var segs = new List<(List<(int X, int Y)> Seg, bool J0, bool J1)>();
        foreach (var p in rest.Items().ToList())
        {
            if (seen.Contains(p) || Nb(p, q => rest.Contains(q.X, q.Y)).Count > 1) continue;
            var seg = new List<(int X, int Y)> { p };
            var cur = p;
            seen.Add(p);
            while (true)
            {
                var nxt = Nb(cur, q => rest.Contains(q.X, q.Y) && !seen.Contains(q));
                if (nxt.Count == 0) break;
                cur = nxt[0];
                seg.Add(cur);
                seen.Add(cur);
            }
            var j0 = Nb(seg[0], q => junction.Contains(q.X, q.Y));
            var j1 = Nb(seg[^1], q => junction.Contains(q.X, q.Y));
            if (j0.Count > 0) seg.Insert(0, j0[0]);
            if (j1.Count > 0) seg.Add(j1[0]);
            segs.Add((seg, j0.Count > 0, j1.Count > 0));
        }
        var lines = new List<List<LinePoint>>();
        foreach (var (seg, j0, j1) in segs)
        {
            var world = new List<LinePoint>();
            for (int k = 0; k < seg.Count; k += 2) world.Add(ToWorld(seg[k]));
            world.Add(ToWorld(seg[^1]));
            var pl = LabelLines.Dedupe(world);
            if (pl.Count < 2) continue;
            if ((!j0 || !j1) && LabelLines.Length(pl) < pruneM) continue;
            lines.Add(LabelLines.Smooth(pl, 4.0, 12.0));
        }
        return lines;

        LinePoint ToWorld((int X, int Y) q) => new(x0 + (q.X + 0.5) * px, y0 - (q.Y + 0.5) * px);
    }

    /// <summary>True when the one-way roads of a name never make a long line (the longest joined line under 1 km and under 40 % of them all).</summary>
    public static bool Fragmented(IReadOnlyList<StreetChain> oneway, IReadOnlyList<StreetChain> joined)
    {
        double tot = 0;
        foreach (var c in oneway) tot += c.Length;
        double longest = 0.0;
        foreach (var c in joined) longest = Num.PyMax(longest, c.Length);
        return tot >= 1000.0 && longest < 1000.0 && longest < 0.4 * tot;
    }

    /// <summary>The main lines of a name: its one-way lines of at least 300 m and 30 % of its longest one.</summary>
    public static List<StreetChain> Trunks(IReadOnlyList<StreetChain> chains)
    {
        var div = chains.Where(c => c.IsOneWay && c.Points.Count >= 2).ToList();
        if (div.Count == 0) return [];
        double longest = div[0].Length;
        foreach (var c in div) longest = Num.PyMax(longest, c.Length);
        double thr = Num.PyMax(300.0, 0.3 * longest);
        return div.Where(c => c.Length >= thr).ToList();
    }

    /// <summary>
    /// Carriageways paired by runs: every one-way line sampled every 10 m; a sample is paired where another line runs
    /// alongside (within <paramref name="maxGap"/>, parallel or opposite). The longest line goes first: its paired runs
    /// become a smoothed midline, the partner's samples alongside are used up, the rest comes out as single lines.
    /// Paired runs shorter than min(1 km, half the line) stay single; short single runs inside a midline are bridged with
    /// the line's own points; pieces under 100 m are dropped.
    /// </summary>
    public static List<StreetChain> PairRuns(IReadOnlyList<StreetChain> chains, double maxGap = PairGap)
    {
        const double step = 10.0, runMinM = 1000.0, minPiece = 100.0;
        var div = chains.Where(c => c.IsOneWay && c.Points.Count >= 2).OrderBy(c => -c.Length).ToList();
        var rest = chains.Where(c => !(c.IsOneWay && c.Points.Count >= 2)).ToList();
        var samp = new List<List<LinePoint>>();
        var heads = new List<List<double>>();
        var lines = new List<List<LinePoint>>();
        foreach (var c in div)
        {
            var pts = LabelLines.Dedupe(c.Points);
            var cum = LabelLines.Cum(pts);
            double L = cum[^1];
            var ss = Num.NpArange(0.0, L, step).Append(L);
            var sp = new List<LinePoint>();
            var hs = new List<double>();
            foreach (double t in ss)
            {
                var st = LabelLines.At(pts, cum, t);
                sp.Add(new LinePoint(st.X, st.Y, st.Smoothed));
                hs.Add(st.Heading);
            }
            samp.Add(sp);
            heads.Add(hs);
            lines.Add(pts);
        }
        var consumed = samp.Select(sp => new bool[sp.Count]).ToList();
        var bb = div.Select(Box).ToList();
        bool NearBox(int a, int b) => !(bb[a].X1 + maxGap < bb[b].X0 || bb[b].X1 + maxGap < bb[a].X0 || bb[a].Y1 + maxGap < bb[b].Y0 || bb[b].Y1 + maxGap < bb[a].Y0);
        var lengths = lines.Select(LabelLines.Length).ToList();
        var o = new List<StreetChain>();
        for (int i = 0; i < div.Count; i++)
        {
            var (pts, hs) = (samp[i], heads[i]);
            if (pts.Count < 2) continue;
            double L = lengths[i];
            var others = Enumerable.Range(0, div.Count).Where(j => j != i && samp[j].Count >= 2 && NearBox(i, j)).ToList();
            var near = new List<(int J, int Cnt)>();
            foreach (int j in others)
            {
                double runMinJ = Num.PyMin(runMinM, 0.5 * Num.PyMin(L, lengths[j]));
                int cnt = 0;
                for (int k = 0; k < pts.Count; k++)
                {
                    var (d, _, _, hb) = LabelLines.Nearest(pts[k].X, pts[k].Y, lines[j]);
                    if (d <= maxGap && Alongside(hs[k], hb)) cnt++;
                }
                if (cnt * step >= runMinJ) near.Add((j, cnt));
            }
            double runMin = Num.PyMin(runMinM, 0.5 * L);
            foreach (var (j, _) in near) runMin = Math.Min(runMin, Num.PyMin(runMinM, 0.5 * Num.PyMin(L, lengths[j])));
            var ranked = near.OrderBy(x => -x.Cnt).Select(x => x.J).ToList();
            // per sample: the partner and the nearest point on it; Gap = the line's own point inside a midline
            var pair = new (int J, double Qx, double Qy, bool Gap)?[pts.Count];
            for (int k = 0; k < pts.Count; k++)
            {
                if (consumed[i][k]) continue;
                foreach (int j in ranked)
                {
                    var (d, qx, qy, hb) = LabelLines.Nearest(pts[k].X, pts[k].Y, lines[j]);
                    if (!(d <= maxGap && Alongside(hs[k], hb))) continue;
                    int mBest = 0;
                    double dBest = double.NaN;
                    for (int m = 0; m < samp[j].Count; m++)
                    {
                        double dm = Num.Pow2(samp[j][m].X - qx) + Num.Pow2(samp[j][m].Y - qy);
                        if (m == 0 || dm < dBest) (mBest, dBest) = (m, dm);
                    }
                    if (consumed[j][mBest]) continue;
                    pair[k] = (j, qx, qy, false);
                    break;
                }
            }
            // runs: short paired runs become single, short single runs next to a midline are bridged
            var state = new char[pts.Count];
            for (int k = 0; k < pts.Count; k++) state[k] = consumed[i][k] ? 'h' : pair[k] is not null ? 'm' : 's';
            foreach (var (k0, k1, kind) in Runs(state))
                if (kind == 'm' && (k1 - k0) * step < runMin)
                    for (int m = k0; m < k1; m++) { state[m] = 's'; pair[m] = null; }
            var rr = Runs(state);
            for (int n = 0; n < rr.Count; n++)
            {
                var (k0, k1, kind) = rr[n];
                if (kind != 's' || !((k1 - k0) * step < runMin)) continue;
                bool prevMid = n > 0 && rr[n - 1].Kind == 'm', nextMid = n + 1 < rr.Count && rr[n + 1].Kind == 'm';
                if (prevMid || nextMid)
                    for (int m = k0; m < k1; m++) { state[m] = 'm'; pair[m] = (-1, 0, 0, true); }
            }
            // the partners' samples alongside this line's paired samples are used up, and this line's midline samples
            var paired = new List<LinePoint>();
            for (int k = 0; k < pts.Count; k++)
                if (pair[k] is { Gap: false }) paired.Add(pts[k]);
            if (paired.Count > 0)
            {
                var pl = paired.Count >= 2 ? paired : [paired[0], paired[0]];
                foreach (int j in pair.Where(p => p is { Gap: false }).Select(p => p!.Value.J).Distinct())
                    for (int m = 0; m < samp[j].Count; m++)
                    {
                        if (consumed[j][m]) continue;
                        var (d, _, _, hb) = LabelLines.Nearest(samp[j][m].X, samp[j][m].Y, pl);
                        if (d <= maxGap && Alongside(heads[j][m], hb)) consumed[j][m] = true;
                    }
            }
            for (int k = 0; k < pts.Count; k++)
                if (state[k] == 'm') consumed[i][k] = true;
            foreach (var (k0, k1, kind) in Runs(state))
            {
                if (kind == 'h') continue;
                var run = new List<LinePoint>();
                for (int k = k0; k < k1; k++)
                    run.Add(kind == 'm' && pair[k] is { Gap: false } pr
                        ? new LinePoint((pts[k].X + pr.Qx) / 2.0, (pts[k].Y + pr.Qy) / 2.0, pts[k].Smoothed)
                        : pts[k]);
                bool merged = kind == 'm';
                if (run.Count < 2) continue;
                var line = merged ? LabelLines.Smooth(run) : LabelLines.Dedupe(run);
                if (line.Count >= 2 && LabelLines.Length(line) >= minPiece)
                    o.Add(div[i] with { Points = line, Merged = merged, Length = LabelLines.Length(line) });
            }
        }
        o.AddRange(rest);
        return o;
    }

    static List<(int K0, int K1, char Kind)> Runs(char[] st)
    {
        var r = new List<(int, int, char)>();
        int k = 0;
        while (k < st.Length)
        {
            int e = k;
            while (e < st.Length && st[e] == st[k]) e++;
            r.Add((k, e, st[k]));
            k = e;
        }
        return r;
    }

    static (double X0, double Y0, double X1, double Y1) Box(StreetChain c)
    {
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var p in c.Points)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>
    /// The parts cut into runs that do or do not run alongside a main line of another street (within 45 m, sampled
    /// every 10 m); shared runs under 200 m stay unshared; midline parts are never shared.
    /// </summary>
    public static List<StreetPart> SplitShared(IReadOnlyList<StreetPart> parts, IReadOnlyList<StreetChain> otherTrunks, IReadOnlyList<string> otherNames)
    {
        const double step = 10.0, minRun = 200.0;
        var bb = otherTrunks.Select(c => { var b = Box(c); return (X0: b.X0 - SharedGap, Y0: b.Y0 - SharedGap, X1: b.X1 + SharedGap, Y1: b.Y1 + SharedGap); }).ToList();
        var o = new List<StreetPart>();
        foreach (var part in parts)
        {
            if (part.Midline) { o.Add(part with { Shared = null }); continue; }
            var cum = LabelLines.Cum(part.Points);
            double L = cum[^1];
            var P = Num.NpArange(0.0, L, step).Append(L).Select(t => LabelLines.At(part.Points, cum, t)).ToList();
            double px0 = P.Min(p => p.X), py0 = P.Min(p => p.Y), px1 = P.Max(p => p.X), py1 = P.Max(p => p.Y);
            var near = Enumerable.Range(0, bb.Count).Where(j => !(bb[j].X1 < px0 || bb[j].X0 > px1 || bb[j].Y1 < py0 || bb[j].Y0 > py1)).ToList();
            var who = new string?[P.Count];
            for (int k = 0; k < P.Count; k++)
                foreach (int j in near)
                {
                    var (d, _, _, hb) = LabelLines.Nearest(P[k].X, P[k].Y, otherTrunks[j].Points);
                    if (d <= SharedGap && Alongside(P[k].Heading, hb)) { who[k] = otherNames[j]; break; }
                }
            var runs = new List<(int K0, int K1, bool Shared)>();
            for (int k = 0; k < P.Count;)
            {
                int e = k;
                while (e < P.Count && (who[e] is null) == (who[k] is null)) e++;
                runs.Add((k, e, who[k] is not null));
                k = e;
            }
            for (int r = 0; r < runs.Count; r++)
                if (runs[r].Shared && (runs[r].K1 - runs[r].K0) * step < minRun) runs[r] = runs[r] with { Shared = false };
            var merged = new List<(int K0, int K1, bool Shared)>();
            foreach (var r in runs)
                if (merged.Count > 0 && merged[^1].Shared == r.Shared) merged[^1] = merged[^1] with { K1 = r.K1 };
                else merged.Add(r);
            foreach (var (k0, k1, sh) in merged)
            {
                var seg = new List<LinePoint>();
                for (int k = k0; k < k1; k++) seg.Add(new LinePoint(P[k].X, P[k].Y, P[k].Smoothed));
                if (k1 < P.Count) seg.Add(new LinePoint(P[k1].X, P[k1].Y, P[k1].Smoothed));
                if (seg.Count < 2 || LabelLines.Length(seg) < 20.0) continue;
                string? name = null;
                if (sh)
                {
                    // the most frequent name of the run, the first of equals
                    var counts = new List<(string Name, int N)>();
                    for (int k = k0; k < k1; k++)
                        if (who[k] is { } w)
                        {
                            int idx = counts.FindIndex(x => x.Name == w);
                            if (idx < 0) counts.Add((w, 1));
                            else counts[idx] = (w, counts[idx].N + 1);
                        }
                    if (counts.Count > 0)
                    {
                        var best = counts[0];
                        foreach (var cn in counts) if (cn.N > best.N) best = cn;
                        name = best.Name;
                    }
                }
                o.Add(new StreetPart(seg, part.Midline, name, part.Ramp, part.Class));
            }
        }
        return o;
    }

    /// <summary>The label parts of one street name (<paramref name="otherTrunks"/>: the main lines of other names for <see cref="SplitShared"/>, or empty).</summary>
    public static List<StreetPart> Build(IReadOnlyList<StreetChain> chains, IReadOnlyList<StreetChain> otherTrunks, IReadOnlyList<string> otherNames)
    {
        var oneway = chains.Where(c => c.IsOneWay && c.Points.Count >= 2).ToList();
        var twoway = chains.Where(c => !c.IsOneWay && c.Points.Count >= 2).ToList();
        var joined = new List<StreetChain>();
        foreach (var (pts, members) in Rechain(oneway))
            joined.Add(members[0] with { Points = pts, Length = LabelLines.Length(pts), Class = TopClass(members), OneWay = true });
        if (oneway.Count > 0 && Fragmented(oneway, joined))
        {
            var top = TopChain(oneway);
            var segs = SkeletonLines(oneway).Select(pl => top with { Points = pl, Length = LabelLines.Length(pl) }).ToList();
            joined = [];
            foreach (var (pts, _) in Rechain(segs, 8.0))
                joined.Add(top with { Points = pts, Length = LabelLines.Length(pts), OneWay = true });
        }
        var trunks = Trunks(joined);
        var pieces = PairRuns(trunks);
        foreach (var c in joined)
            if (!trunks.Any(t => ReferenceEquals(t, c))) pieces.Add(c with { Ramp = true });
        pieces.AddRange(twoway);
        var parts = new List<StreetPart>();
        foreach (var (line, flag, members) in Merge(pieces, sameFlag: true))
        {
            bool ramp = members.All(m => m.Ramp);
            var cls = TopClass(members);
            foreach (var part in LabelLines.SplitCorners(line)) parts.Add(new StreetPart(part, flag, null, ramp, cls));
        }
        return otherTrunks.Count > 0 ? SplitShared(parts, otherTrunks, otherNames) : parts;
    }
}
