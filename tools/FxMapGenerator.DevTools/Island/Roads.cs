using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.SampleIsland;
using FxMapGenerator.GameData.Text;

namespace FxMapGenerator.DevTools.Island;

/// <summary>What a stretch of a way is: on the (graded) ground, on a deck (a bridge or a raised road), or in a tunnel.</summary>
enum Run { Ground, Deck, Tunnel }

/// <summary>A road or the railway: its kind, and its line (every 5 m, local metres) with the height and the run at each point.</summary>
sealed class Way
{
    public required string Label { get; init; }
    public string? En { get; init; }
    public string? Ja { get; init; }
    public int LanesForward { get; init; } = 1;
    public int LanesBack { get; init; } = 1;
    public bool Highway { get; init; }
    public bool Unpaved { get; init; }
    public bool SwitchedOff { get; init; }
    public bool Narrow { get; init; }
    public bool Rail { get; init; }
    public required double Width { get; init; }
    /// <summary>The steepest the way may climb (rise per metre).</summary>
    public required double MaxGrade { get; init; }
    /// <summary>Which goes over at a crossing (the higher).</summary>
    public required int Priority { get; init; }
    /// <summary>Whether the way may go under the ground where the land is too high for its grade.</summary>
    public bool Tunnels { get; init; }
    public List<(double X, double Y)> P { get; set; } = new();
    public double[] Z { get; set; } = [];
    public Run[] Kind { get; set; } = [];
    /// <summary>Points that must become path nodes (where other ways join).</summary>
    public HashSet<int> Joins { get; } = new();
    public double? StartZ { get; set; }
    public double? EndZ { get; set; }
    /// <summary>The least height at points (the clearance over another way it crosses).</summary>
    public Dictionary<int, double> Floors { get; } = new();
    /// <summary>The ways this one leaves from or ends on (no crossing is looked for between them).</summary>
    public List<Way> Joined { get; } = new();
    public uint Street => En is null ? 0 : Gxt2File.Joaat(En.ToLowerInvariant());
}

/// <summary>
/// The sample's roads and railway, made on the land rather than drawn: each way is the cheapest path over the ground
/// (A* on a 4 m grid with 16 directions; the cost of a metre grows with the square of its grade over the way's
/// limit, over water (a bridge), and with the distance from the sketch's line it follows; a way that may tunnel pays at
/// most a fixed price for steep ground), smoothed, then given a height profile within its grade limit (the ground
/// averaged along it, held over water): where the profile stands high above the ground it is a deck (a bridge, a
/// raised road), where deep under it a tunnel, elsewhere the ground is graded to it with embankments. At a crossing
/// without a junction the higher-ranked way rises over the other. The highway's branch leaves by a Y of two links, the
/// avenue meets the highway at a diamond interchange of four one-way ramps (after the manner of Galin et al. 2010,
/// "Procedural generation of roads").
/// </summary>
sealed class Roads
{
    readonly Land _l;
    readonly int _w4, _h4;
    readonly float[] _t4;
    readonly bool[] _sea4, _water4;
    public List<Way> Ways { get; } = new();
    public IReadOnlyList<IslandNode> Nodes { get; private set; } = [];
    public IReadOnlyList<IslandLink> Links { get; private set; } = [];
    public IReadOnlyDictionary<uint, (string En, string Ja)> Streets { get; private set; } = new Dictionary<uint, (string, string)>();
    public IReadOnlyList<(string Id, double X, double Y)> Markers { get; private set; } = [];

    const double Box = 1122;   // the cell's half width, less a little (m)

    Roads(Land l)
    {
        _l = l;
        _w4 = (l.W - 1) / 2 + 1;
        _h4 = (l.H - 1) / 2 + 1;
        _t4 = new float[_w4 * _h4];
        _sea4 = new bool[_w4 * _h4];
        _water4 = new bool[_w4 * _h4];
        for (int r = 0; r < _h4; r++)
            for (int c = 0; c < _w4; c++)
            {
                int i = 2 * r * l.W + 2 * c, k = r * _w4 + c;
                _t4[k] = l.Ground[i];
                _sea4[k] = !l.IsLand[i];
                _water4[k] = l.IsLand[i] && !float.IsNaN(l.Water[i]);
            }
    }

    /// <summary>The highway, its branch, the avenue, the railway and the trail, with their heights (the ground not graded yet).</summary>
    public static Roads Build(Land l, Action<string> log)
    {
        var r = new Roads(l);
        r.Make(log);
        return r;
    }

    public Land Land => _l;

    /// <summary>The runs, the ground graded to the ways and the path nodes: after the town has added its streets.</summary>
    public void Complete(Action<string> log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var w in Ways) Classify(w);
        Grade();
        MakeNodes();
        Streets = Ways.Where(w => w.En is not null).GroupBy(w => w.Street).ToDictionary(g => g.Key, g => (g.First().En!, g.First().Ja!));
        foreach (var w in Ways.Where(w => w.Label is not "street"))
            log($"  {w.Label}: {(w.P.Count - 1) * 5 / 1000.0:0.00} km, deck {w.Kind.Count(k => k == Run.Deck) * 5} m, tunnel {w.Kind.Count(k => k == Run.Tunnel) * 5} m, height {w.Z.Min():0}..{w.Z.Max():0} m");
        log($"roads: {Ways.Count} ways ({Ways.Count(w => w.Label is "street")} streets), {Nodes.Count} nodes, {sw.Elapsed.TotalSeconds:0.0} s");
    }

    // ---------------------------------------------------------------- the network

    void Make(Action<string> log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // the highway, north-west to south
        var h1 = new Way { Label = "highway", En = "Kestrel Fwy", Ja = "ケストレル・フリーウェイ", LanesForward = 2, LanesBack = 2, Highway = true, Width = 22, MaxGrade = 0.05, Priority = 4, Tunnels = true };
        Route(h1, Clip(Sketch.Local(Sketch.Highway)), slopeCost: 3, guideCost: 1.5, smooth: 80);
        Ways.Add(h1);
        // the branch east into the mountains, leaving by a Y of two links
        var (ju, jv) = Sketch.Local((245, 128));
        int ji = Nearest(h1, ju, jv);
        var branchGuide = Clip(Sketch.Local(Sketch.Branch));
        var east = Unit(branchGuide[1].X - branchGuide[0].X, branchGuide[1].Y - branchGuide[0].Y);
        var bs = (X: h1.P[ji].X + east.X * 170, Y: h1.P[ji].Y + east.Y * 170);
        var branch = new Way { Label = "branch", En = "Aster Hwy", Ja = "アスター・ハイウェイ", LanesForward = 2, LanesBack = 2, Highway = true, Width = 22, MaxGrade = 0.06, Priority = 4, Tunnels = true };
        Route(branch, [bs, .. branchGuide.Where(p => (p.X - h1.P[ji].X) * east.X + (p.Y - h1.P[ji].Y) * east.Y > 250)], slopeCost: 3, guideCost: 1.5, smooth: 60);
        branch.Joined.Add(h1);
        Ways.Add(branch);
        int ja = Math.Max(1, ji - 40), jb = Math.Min(h1.P.Count - 2, ji + 40);   // 200 m either way along the highway
        foreach (var (at, toStart) in new[] { (ja, true), (jb, false) })
        {
            var dir = Unit(h1.P[at + 1].X - h1.P[at - 1].X, h1.P[at + 1].Y - h1.P[at - 1].Y);
            var travel = toStart ? (X: -dir.X, Y: -dir.Y) : dir;
            var link = new Way { Label = "junction link", En = branch.En, Ja = branch.Ja, LanesForward = 1, LanesBack = 1, Highway = true, Width = 11, MaxGrade = 0.06, Priority = 3 };
            link.P = Bezier(bs, (bs.X - east.X * 90, bs.Y - east.Y * 90), (h1.P[at].X - travel.X * 90, h1.P[at].Y - travel.Y * 90), h1.P[at]);
            link.Joined.AddRange([h1, branch]);
            h1.Joins.Add(at);
            Ways.Add(link);
        }
        Markers = [("A", h1.P[ji].X + east.X * 90 + _l.Cx, h1.P[ji].Y + east.Y * 90 + _l.Cy)];

        // the avenue from the town's quay east, past the lake, meeting the highway at a diamond interchange
        var avenue = new Way { Label = "avenue", En = "Harbor Ave", Ja = "ハーバー・アベニュー", LanesForward = 2, LanesBack = 2, Width = 22, MaxGrade = 0.07, Priority = 2 };
        Route(avenue, Clip(Sketch.Local(Sketch.Avenue)), slopeCost: 1.5, guideCost: 1.5, smooth: 70);
        Ways.Add(avenue);
        if (Crossing(h1, avenue) is { } cross)
        {
            var (hi, ai) = cross;
            int hN = Math.Max(1, hi - 46), hS = Math.Min(h1.P.Count - 2, hi + 46);   // 230 m along the highway
            int aW = Math.Max(1, ai - 18), aE = Math.Min(avenue.P.Count - 2, ai + 18); // 90 m along the avenue
            var dh = Unit(h1.P[hi + 1].X - h1.P[hi - 1].X, h1.P[hi + 1].Y - h1.P[hi - 1].Y);     // towards the highway's end (south)
            var da = Unit(avenue.P[ai + 1].X - avenue.P[ai - 1].X, avenue.P[ai + 1].Y - avenue.P[ai - 1].Y);  // east
            (double X, double Y) Off((double X, double Y) p, (double X, double Y) d, double m) => (p.X + d.X * m, p.Y + d.Y * m);
            var north = (X: -dh.X, Y: -dh.Y);
            // a diamond: each ramp runs along the highway about 90 m out and meets the avenue square (travelling north on
            // the east side, south on the west side)
            var ramps = new[]
            {
                (h1.P[hS], avenue.P[aE], north, true),      // off the highway going north, onto the avenue east of it
                (avenue.P[aE], h1.P[hN], north, false),     // from the avenue east of it, onto the highway going north
                (h1.P[hN], avenue.P[aW], dh, true),         // off the highway going south, onto the avenue west of it
                (avenue.P[aW], h1.P[hS], dh, false),        // from the avenue west of it, onto the highway going south
            };
            foreach (var (from, to, travel, leavesHighway) in ramps)
            {
                var ramp = new Way { Label = "ramp", En = h1.En, Ja = h1.Ja, LanesForward = 1, LanesBack = 0, Highway = true, Width = 6, MaxGrade = 0.07, Priority = 3 };
                ramp.P = Bezier(from, Off(from, travel, leavesHighway ? 120 : 60), Off(to, travel, leavesHighway ? -60 : -120), to);
                ramp.Joined.AddRange([h1, avenue]);
                Ways.Add(ramp);
            }
            h1.Joins.UnionWith([hN, hS]);
            avenue.Joins.UnionWith([aW, aE]);
            log($"interchange at {h1.P[hi].X:0}, {h1.P[hi].Y:0}");
        }

        // the railway
        var rail = new Way { Label = "railway", Rail = true, Width = 5, MaxGrade = 0.025, Priority = 1, Tunnels = true };
        Route(rail, Clip(Sketch.Local(Sketch.Railway)), slopeCost: 6, guideCost: 1.5, smooth: 100);
        Ways.Add(rail);

        // profiles, the lowest-ranked ways first; where a way crosses one already made (away from their joins), it must
        // pass 7.5 m over it; the links and ramps join the ways they leave from and end on
        var main = Ways.Where(w => w.Label is not ("junction link" or "ramp")).OrderBy(w => w.Priority).ToList();
        var done = new List<Way>();
        foreach (var w in main)
        {
            foreach (var lower in done) Floors(w, lower, log);
            Profile(w);
            done.Add(w);
        }
        foreach (var w in Ways.Where(w => w.Label is "junction link" or "ramp"))
        {
            w.StartZ = HeightNear(w.P[0], w);
            w.EndZ = HeightNear(w.P[^1], w);
            foreach (var lower in done) Floors(w, lower, log);
            Profile(w);
        }
        int enter = Enumerable.Range(0, branch.P.Count).FirstOrDefault(i => branch.Kind[i] == Run.Tunnel, branch.P.Count / 2);
        int trailFrom = Math.Max(0, enter - 8);
        var trail = new Way { Label = "trail", En = "Aster Trail", Ja = "アスター・トレイル", Unpaved = true, Narrow = true, Width = 6, MaxGrade = 0.2, Priority = 0 };
        var summit = Sketch.Local(Sketch.Summit);
        Route(trail, [branch.P[trailFrom], (summit.X - 60, summit.Y - 60)], slopeCost: 5, guideCost: 0, smooth: 8);
        trail.Joined.Add(branch);
        branch.Joins.Add(trailFrom);
        trail.StartZ = branch.Z[trailFrom];
        Profile(trail);
        Ways.Add(trail);

    }

    static (double X, double Y) Unit(double x, double y)
    {
        double l = Math.Sqrt(x * x + y * y);
        return l == 0 ? (1, 0) : (x / l, y / l);
    }

    static int Nearest(Way w, double u, double v)
    {
        int best = 0;
        double d = double.MaxValue;
        for (int i = 0; i < w.P.Count; i++)
        {
            double e = Sq(w.P[i].X - u) + Sq(w.P[i].Y - v);
            if (e < d) { d = e; best = i; }
        }
        return best;
    }

    static double Sq(double x) => x * x;

    /// <summary>A guide line cut to the cell (its ends moved onto the cell's edge).</summary>
    static (double X, double Y)[] Clip((double X, double Y)[] line)
    {
        static bool In((double X, double Y) p) => Math.Abs(p.X) <= Box && Math.Abs(p.Y) <= Box;
        var o = new List<(double X, double Y)>();
        for (int i = 0; i < line.Length; i++)
        {
            bool inside = In(line[i]);
            if (inside && i > 0 && !In(line[i - 1])) o.Add(Edge(line[i - 1], line[i]));
            if (inside) o.Add(line[i]);
            if (!inside && i > 0 && In(line[i - 1])) o.Add(Edge(line[i - 1], line[i]));
        }
        return o.ToArray();

        static (double X, double Y) Edge((double X, double Y) a, (double X, double Y) b)
        {
            double lo = 0, hi = 1;
            for (int k = 0; k < 40; k++)
            {
                double m = (lo + hi) / 2;
                var p = (a.X + m * (b.X - a.X), a.Y + m * (b.Y - a.Y));
                if (In(p) == In(a)) lo = m; else hi = m;
            }
            return (a.X + lo * (b.X - a.X), a.Y + lo * (b.Y - a.Y));
        }
    }

    static List<(double X, double Y)> Bezier((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
    {
        var pts = new List<(double X, double Y)>();
        for (int k = 0; k <= 400; k++)
        {
            double t = k / 400.0, a = (1 - t) * (1 - t) * (1 - t), b = 3 * (1 - t) * (1 - t) * t, c = 3 * (1 - t) * t * t, d = t * t * t;
            pts.Add((a * p0.X + b * p1.X + c * p2.X + d * p3.X, a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y));
        }
        return Resample(pts, 5);
    }

    // ---------------------------------------------------------------- routes

    static readonly (int Dr, int Dc)[] Moves =
    [
        (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (-1, 1), (1, -1), (1, 1),
        (-1, -2), (-1, 2), (1, -2), (1, 2), (-2, -1), (-2, 1), (2, -1), (2, 1),
    ];

    int Cell4(double u, double v)
    {
        int c = (int)Math.Round((u + _l.Cx - _l.X0) / 4), r = (int)Math.Round((_l.Y0 - (v + _l.Cy)) / 4);
        return Math.Clamp(r, 0, _h4 - 1) * _w4 + Math.Clamp(c, 0, _w4 - 1);
    }

    (double X, double Y) Local4(int k) => (_l.X0 + k % _w4 * 4 - _l.Cx, _l.Y0 - k / _w4 * 4 - _l.Cy);

    /// <summary>The cheapest path through the guide's points in turn (A* between each pair), smoothed and resampled every 5 m.</summary>
    void Route(Way w, (double X, double Y)[] guide, double slopeCost, double guideCost, double smooth)
    {
        double[]? toGuide = null;
        if (guideCost > 0)
        {
            var g = new Grid<bool>(_w4, _h4);
            for (int i = 1; i < guide.Length; i++)
            {
                double len = Math.Sqrt(Sq(guide[i].X - guide[i - 1].X) + Sq(guide[i].Y - guide[i - 1].Y));
                for (double s = 0; s <= len; s += 2)
                    g.Data[Cell4(guide[i - 1].X + (guide[i].X - guide[i - 1].X) * s / len, guide[i - 1].Y + (guide[i].Y - guide[i - 1].Y) * s / len)] = true;
            }
            toGuide = DistanceTransform.Distance(g);
        }
        // A* from the first point to the last (the guide's middle points only pull through the guide cost)
        int start = Cell4(guide[0].X, guide[0].Y), goal = Cell4(guide[^1].X, guide[^1].Y);
        int n = _w4 * _h4;
        var cost = new double[n];
        Array.Fill(cost, double.PositiveInfinity);
        var from = new int[n];
        var queue = new PriorityQueue<int, double>();
        cost[start] = 0;
        queue.Enqueue(start, 0);
        int gr = goal / _w4, gc = goal % _w4;
        double tunnelCap = w.Tunnels ? 4 : double.PositiveInfinity;
        while (queue.TryDequeue(out int k, out double pri))
        {
            if (k == goal) break;
            double hk = 4 * Math.Sqrt(Sq(k / _w4 - gr) + Sq(k % _w4 - gc));
            if (pri > cost[k] + hk + 1e-6) continue;
            int r = k / _w4, c = k % _w4;
            foreach (var (dr, dc) in Moves)
            {
                int rr = r + dr, cc = c + dc;
                if ((uint)rr >= (uint)_h4 || (uint)cc >= (uint)_w4) continue;
                int j = rr * _w4 + cc;
                if (_sea4[j]) continue;
                double len = 4 * Math.Sqrt(dr * dr + dc * dc);
                double grade = Math.Abs(_t4[j] - _t4[k]) / len;
                double c1 = 1 + Math.Min(slopeCost * Sq(grade / w.MaxGrade), tunnelCap);
                if (_water4[j]) c1 += 40;
                if (toGuide is not null) c1 += guideCost * Sq(toGuide[j] * 4 / 150);
                double nc = cost[k] + len * c1;
                if (nc < cost[j])
                {
                    cost[j] = nc;
                    from[j] = k;
                    queue.Enqueue(j, nc + 4 * Math.Sqrt(Sq(rr - gr) + Sq(cc - gc)));
                }
            }
        }
        var path = new List<(double X, double Y)>();
        for (int k = goal; ; k = from[k])
        {
            path.Add(Local4(k));
            if (k == start) break;
        }
        path.Reverse();
        path[0] = guide[0];
        path[^1] = guide[^1];
        // corners cut (Chaikin), then a moving average over the smoothing length, the ends kept
        for (int it = 0; it < 3; it++)
        {
            var o = new List<(double X, double Y)> { path[0] };
            for (int i = 0; i + 1 < path.Count; i++)
            {
                o.Add((0.75 * path[i].X + 0.25 * path[i + 1].X, 0.75 * path[i].Y + 0.25 * path[i + 1].Y));
                o.Add((0.25 * path[i].X + 0.75 * path[i + 1].X, 0.25 * path[i].Y + 0.75 * path[i + 1].Y));
            }
            o.Add(path[^1]);
            path = o;
        }
        path = Resample(path, 5);
        int half = Math.Max(0, (int)(smooth / 10));
        if (half > 0)
        {
            var o = new List<(double X, double Y)>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                int a = Math.Max(0, i - half), b = Math.Min(path.Count - 1, i + half), m = Math.Min(i - a, b - i);
                a = i - m; b = i + m;
                double sx = 0, sy = 0;
                for (int k = a; k <= b; k++) { sx += path[k].X; sy += path[k].Y; }
                o.Add((sx / (b - a + 1), sy / (b - a + 1)));
            }
            path = Resample(o, 5);
        }
        w.P = path;
    }

    public static List<(double X, double Y)> Resample(List<(double X, double Y)> pts, double every)
    {
        var o = new List<(double X, double Y)> { pts[0] };
        double carry = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y, len = Math.Sqrt(dx * dx + dy * dy);
            double s = every - carry;
            while (s <= len)
            {
                o.Add((pts[i - 1].X + dx * s / len, pts[i - 1].Y + dy * s / len));
                s += every;
            }
            carry = len - (s - every);
        }
        if (Math.Sqrt(Sq(o[^1].X - pts[^1].X) + Sq(o[^1].Y - pts[^1].Y)) > 0.5) o.Add(pts[^1]);
        else o[^1] = pts[^1];
        return o;
    }

    // ---------------------------------------------------------------- heights

    double WaterAt((double X, double Y) p)
    {
        int i = _l.Node(p.X, p.Y);
        return i >= 0 && !float.IsNaN(_l.Water[i]) ? _l.Water[i] : double.NaN;
    }

    /// <summary>
    /// The height profile, chosen by dynamic programming over heights in steps of a quarter of the grade limit: no step
    /// between points steeper than the limit, the ends fixed where the way joins others, at least the floor (the
    /// clearance over water and over the ways it crosses); each point costs by how far it lies from the ground: nothing
    /// within 0.5 m, then per metre of embankment up to 4 m and of cutting up to 8 m, and above that a fixed price
    /// for a deck, and for a tunnel (a way that may not tunnel pays much more). So the way keeps to the ground where
    /// it can, cuts through small rises, fills small dips, bridges water and deep valleys and tunnels through hills.
    /// </summary>
    public void Profile(Way w)
    {
        int n = w.P.Count;
        var t = w.P.Select(p => _l.GroundAt(p.X, p.Y)).ToArray();
        double clearance = w.Rail ? 5 : 6;
        var floor = new double[n];
        for (int i = 0; i < n; i++)
        {
            floor[i] = WaterAt(w.P[i]) is var wt && !double.IsNaN(wt) ? wt + clearance : double.NegativeInfinity;
            if (w.Floors.TryGetValue(i, out var f)) floor[i] = Math.Max(floor[i], f);
        }
        double rise = w.MaxGrade * 5, bin = rise / 4;
        double lo = t.Min() - 20, hi = Math.Max(t.Max(), floor.Max()) + 20;
        foreach (var z in new[] { w.StartZ, w.EndZ })
            if (z is { } zz) { lo = Math.Min(lo, zz - 5); hi = Math.Max(hi, zz + 5); }
        int m = (int)Math.Ceiling((hi - lo) / bin) + 1;
        double Cost(int i, double z)
        {
            if (z < floor[i] - 1e-6) return double.PositiveInfinity;
            double d = z - t[i];
            if (Math.Abs(d) <= 0.5) return 0;
            if (d > 0) return d <= 4 ? d : 4 + 2.5;
            return -d <= 8 ? 0.8 * -d : w.Tunnels ? 6.4 + 3.5 : 6.4 + 60;
        }
        var cost = new double[m];
        var back = new sbyte[n * m];
        for (int k = 0; k < m; k++)
        {
            double z = lo + k * bin;
            cost[k] = w.StartZ is { } sz && Math.Abs(z - sz) > bin ? double.PositiveInfinity : Cost(0, z);
        }
        var next = new double[m];
        for (int i = 1; i < n; i++)
        {
            for (int k = 0; k < m; k++)
            {
                double best = double.PositiveInfinity;
                int arg = 0;
                for (int d = -4; d <= 4; d++)
                {
                    int j = k + d;
                    if ((uint)j >= (uint)m || cost[j] >= best) continue;
                    best = cost[j];
                    arg = d;
                }
                double z = lo + k * bin;
                bool offEnd = i == n - 1 && w.EndZ is { } ez && Math.Abs(z - ez) > bin;
                next[k] = offEnd ? double.PositiveInfinity : best + Cost(i, z);
                back[i * m + k] = (sbyte)arg;
            }
            (cost, next) = (next, cost);
        }
        int at = 0;
        for (int k = 1; k < m; k++) if (cost[k] < cost[at]) at = k;
        var zs = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            zs[i] = lo + at * bin;
            if (i > 0) at += back[i * m + at];
        }
        w.Z = zs;
        Classify(w);
    }

    /// <summary>The runs: a deck over water or 3.5 m over the ground, a tunnel 10 m under it (where allowed), else the ground; runs shorter than 20 m join their neighbours.</summary>
    void Classify(Way w)
    {
        int n = w.P.Count;
        var kind = new Run[n];
        for (int i = 0; i < n; i++)
        {
            double t = _l.GroundAt(w.P[i].X, w.P[i].Y);
            kind[i] = !double.IsNaN(WaterAt(w.P[i])) || w.Z[i] - t > 3.5 ? Run.Deck : w.Tunnels && t - w.Z[i] > 10 ? Run.Tunnel : Run.Ground;
        }
        for (int i = 0; i < n;)
        {
            int j = i;
            while (j < n && kind[j] == kind[i]) j++;
            if (j - i < 4 && kind[i] != Run.Ground && !(kind[i] == Run.Deck && Enumerable.Range(i, j - i).Any(k => !double.IsNaN(WaterAt(w.P[k])))))
                for (int k = i; k < j; k++) kind[k] = Run.Ground;
            i = j;
        }
        w.Kind = kind;
    }

    /// <summary>The height of another way's point nearest to a point (where a link or a ramp joins it).</summary>
    double HeightNear((double X, double Y) p, Way self)
    {
        double best = double.MaxValue, z = _l.GroundAt(p.X, p.Y);
        foreach (var w in Ways)
        {
            if (w == self || w.Z.Length == 0) continue;
            for (int i = 0; i < w.P.Count; i++)
            {
                double d = Sq(w.P[i].X - p.X) + Sq(w.P[i].Y - p.Y);
                if (d < best && d < 4) { best = d; z = w.Z[i]; }
            }
        }
        return z;
    }

    /// <summary>The first crossing of two ways (point indices), or null.</summary>
    static (int A, int B)? Crossing(Way a, Way b)
    {
        for (int i = 0; i + 1 < a.P.Count; i++)
            for (int j = 0; j + 1 < b.P.Count; j++)
                if (Intersects(a.P[i], a.P[i + 1], b.P[j], b.P[j + 1])) return (i, j);
        return null;
    }

    public static bool Intersects((double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3, (double X, double Y) p4)
    {
        double d1 = Cross(p3, p4, p1), d2 = Cross(p3, p4, p2), d3 = Cross(p1, p2, p3), d4 = Cross(p1, p2, p4);
        return d1 * d2 < 0 && d3 * d4 < 0;
        static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }

    /// <summary>Where a way crosses one already made (not one it joins), it must pass 7.5 m over it there (the floor of its points near the crossing).</summary>
    void Floors(Way w, Way lower, Action<string> log)
    {
        if (w.Joined.Contains(lower) || lower.Joined.Contains(w)) return;
        for (int i = 0; i + 1 < w.P.Count; i++)
            for (int j = 0; j + 1 < lower.P.Count; j++)
            {
                if (!Intersects(w.P[i], w.P[i + 1], lower.P[j], lower.P[j + 1]) || lower.Kind[j] == Run.Tunnel) continue;
                double need = Math.Max(lower.Z[j], lower.Z[j + 1]) + 7.5;
                int span = (int)Math.Ceiling((lower.Width / 2 + 4) / 5);
                for (int k = Math.Max(0, i - span); k <= Math.Min(w.P.Count - 1, i + 1 + span); k++)
                    w.Floors[k] = Math.Max(w.Floors.GetValueOrDefault(k, double.NegativeInfinity), need);
                log($"  {w.Label} over {lower.Label} at {w.P[i].X:0}, {w.P[i].Y:0}");
            }
    }

    // ---------------------------------------------------------------- the ground under the roads

    /// <summary>The ground graded to the ways on the ground: their bed at the road's height, embankments and cuttings sloping 1 in 2 to the ground around.</summary>
    void Grade()
    {
        var l = _l;
        int n = l.W * l.H;
        var bestD = new double[n];
        Array.Fill(bestD, double.PositiveInfinity);
        var target = new float[n];
        var reach = new float[n];
        var half = new float[n];
        var original = (float[])l.Ground.Clone();
        foreach (var w in Ways.OrderBy(w => w.Priority))
        {
            double hw = w.Width / 2 + 1.5;
            for (int i = 0; i + 1 < w.P.Count; i++)
            {
                if (w.Kind[i] != Run.Ground || w.Kind[i + 1] != Run.Ground) continue;
                var (a, b) = (w.P[i], w.P[i + 1]);
                double margin = Math.Min(40, 2 * Math.Abs(w.Z[i] - l.GroundAt(a.X, a.Y)) + 3);
                double r = hw + margin;
                ForBox(Math.Min(a.X, b.X) - r, Math.Min(a.Y, b.Y) - r, Math.Max(a.X, b.X) + r, Math.Max(a.Y, b.Y) + r, (k, u, v) =>
                {
                    if (!l.IsLand[k] || !float.IsNaN(l.Water[k])) return;
                    var (d, t) = ToSegment(u, v, a, b);
                    if (d > r || d >= bestD[k]) return;
                    bestD[k] = d;
                    target[k] = (float)(w.Z[i] + t * (w.Z[i + 1] - w.Z[i]));
                    half[k] = (float)hw;
                    reach[k] = (float)margin;
                });
            }
        }
        for (int k = 0; k < n; k++)
        {
            if (double.IsPositiveInfinity(bestD[k])) continue;
            double d = bestD[k];
            double f = d <= half[k] ? 1 : 1 - Noise.Smooth((d - half[k]) / reach[k]);
            l.Ground[k] = (float)(original[k] + (target[k] - original[k]) * f);
        }
    }

    public void ForBox(double u0, double v0, double u1, double v1, Action<int, double, double> each)
    {
        var l = _l;
        int c0 = Math.Max(0, (int)Math.Floor((u0 + l.Cx - l.X0) / l.Step)), c1 = Math.Min(l.W - 1, (int)Math.Ceiling((u1 + l.Cx - l.X0) / l.Step));
        int r0 = Math.Max(0, (int)Math.Floor((l.Y0 - (v1 + l.Cy)) / l.Step)), r1 = Math.Min(l.H - 1, (int)Math.Ceiling((l.Y0 - (v0 + l.Cy)) / l.Step));
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                int k = r * l.W + c;
                var (u, v) = l.Local(k);
                each(k, u, v);
            }
    }

    public static (double D, double T) ToSegment(double u, double v, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Clamp(((u - a.X) * dx + (v - a.Y) * dy) / l2, 0, 1);
        return (Math.Sqrt(Sq(a.X + t * dx - u) + Sq(a.Y + t * dy - v)), t);
    }

    // ---------------------------------------------------------------- the path nodes and the scan's surface

    /// <summary>
    /// Path nodes every 4th point (20 m), where the run changes and where other ways join; the points two ways share
    /// (within 1 m) are one node. The railway has none (it is no road).
    /// </summary>
    void MakeNodes()
    {
        var nodes = new List<IslandNode>();
        var keyAt = new List<(double X, double Y, string Key)>();
        var perArea = new Dictionary<int, int>();
        var links = new List<IslandLink>();
        var degree = new Dictionary<string, int>();
        var halfWidth = new Dictionary<string, double>();
        string NodeAt(double u, double v, double z, Way w, Run run)
        {
            foreach (var (x, y, key) in keyAt)
                if (Sq(x - u) + Sq(y - v) < 1) { halfWidth[key] = Math.Max(halfWidth[key], w.Width / 2); return key; }
            double gx = u + _l.Cx, gy = v + _l.Cy;
            int area = (int)Math.Floor((gx + 8192) / 512) + (int)Math.Floor((gy + 8192) / 512) * 32;
            int id = perArea.GetValueOrDefault(area);
            perArea[area] = id + 1;
            string k = $"{area}:{id}";
            keyAt.Add((u, v, k));
            halfWidth[k] = w.Width / 2;
            nodes.Add(new IslandNode(k, gx, gy, z, w.Street, false, w.Highway, run == Run.Tunnel, w.Unpaved, w.SwitchedOff));
            return k;
        }
        // the ways that others join first, so a join takes their node
        foreach (var w in Ways.Where(w => !w.Rail).OrderByDescending(w => w.Joins.Count))
        {
            string? last = null;
            for (int i = 0; i < w.P.Count; i++)
            {
                bool keep = i == 0 || i == w.P.Count - 1 || i % 4 == 0 || w.Joins.Contains(i) || (i > 0 && w.Kind[i] != w.Kind[i - 1]);
                if (!keep) continue;
                string k = NodeAt(w.P[i].X, w.P[i].Y, w.Z[i], w, w.Kind[i]);
                if (last is not null && last != k)
                {
                    links.Add(new IslandLink(last, k, w.LanesForward, w.LanesBack, w.Narrow));
                    degree[last] = degree.GetValueOrDefault(last) + 1;
                    degree[k] = degree.GetValueOrDefault(k) + 1;
                }
                last = k;
            }
        }
        Nodes = nodes.Select(nd => degree.GetValueOrDefault(nd.Key) >= 3
            ? nd with { Junction = true, JunctionArea = [nd.X - halfWidth[nd.Key] - 2, nd.Y - halfWidth[nd.Key] - 2, nd.X + halfWidth[nd.Key] + 2, nd.Y + halfWidth[nd.Key] + 2] }
            : nd).ToList();
        Links = links;
    }

    /// <summary>
    /// The ways on the scan's surface: the ground runs as road (or track, or ballast) material and on-road samples with
    /// the street's name, then the decks (the higher last) as what a ray from above hits first; tunnels leave nothing.
    /// Every point takes the nearest named road's street.
    /// </summary>
    public void Paint(Land l, float[] top, byte[] material, bool[] onRoad, uint[] street, Func<string, byte> mat)
    {
        var centre = new uint[l.W * l.H];
        void Stretch(Way w, int i, bool deck)
        {
            var (a, b) = (w.P[i], w.P[i + 1]);
            double hw = w.Width / 2;
            byte m = mat(w.Rail ? "GRAVEL_TRAIN_TRACK" : w.Unpaved ? "DIRT_TRACK" : "TARMAC");
            ForBox(Math.Min(a.X, b.X) - hw, Math.Min(a.Y, b.Y) - hw, Math.Max(a.X, b.X) + hw, Math.Max(a.Y, b.Y) + hw, (k, u, v) =>
            {
                var (d, t) = ToSegment(u, v, a, b);
                if (d > hw) return;
                if (deck)
                {
                    float z = (float)(w.Z[i] + t * (w.Z[i + 1] - w.Z[i]));
                    if (z < top[k]) return;
                    top[k] = z;
                    material[k] = m;
                }
                else
                {
                    if (!float.IsNaN(l.Water[k]) || top[k] > l.Ground[k] + 0.5f) { if (!w.Rail) onRoad[k] = true; return; }
                    material[k] = m;
                }
                if (!w.Rail) onRoad[k] = true;
                if (w.Street != 0 && d <= 1.5) centre[k] = w.Street;
            });
        }
        foreach (var w in Ways.OrderBy(w => w.Priority))
            for (int i = 0; i + 1 < w.P.Count; i++)
                if (w.Kind[i] == Run.Ground && w.Kind[i + 1] == Run.Ground) Stretch(w, i, false);
        var decks = new List<(Way W, int I, double Z)>();
        foreach (var w in Ways)
            for (int i = 0; i + 1 < w.P.Count; i++)
                if (w.Kind[i] == Run.Deck || w.Kind[i + 1] == Run.Deck)
                    if (w.Kind[i] != Run.Tunnel && w.Kind[i + 1] != Run.Tunnel) decks.Add((w, i, Math.Max(w.Z[i], w.Z[i + 1])));
        foreach (var (w, i, _) in decks.OrderBy(d => d.Z)) Stretch(w, i, true);
        var has = new Grid<bool>(l.W, l.H, centre.Select(s => s != 0).ToArray());
        if (has.Data.Any(x => x))
        {
            var spread = DistanceTransform.Spread(new Grid<uint>(l.W, l.H, centre), has);
            Array.Copy(spread.Data, street, street.Length);
        }
    }
}
