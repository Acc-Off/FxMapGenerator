using FxMapGenerator.Core.Grids;

namespace FxMapGenerator.DevTools.Island;

/// <summary>A building's or a yard's footprint: a turned rectangle (local metres), what it is and how high.</summary>
sealed record Plot(string Kind, double X, double Y, double Width, double Depth, double Turn, double Height, string Roof);

/// <summary>
/// The sample's town and farms, grown on the land and its roads: the streets follow a field of directions (the coast's,
/// the avenue's and the highway's, each the stronger the nearer, blended as doubled angles; after Chen et al. 2008,
/// "Interactive procedural street modeling"), traced as two families of lines crossing at right angles, kept a set
/// distance from their own kind and stopped by the highway, the railway, the lake and the sea and the town's edge;
/// their crossings and the ends that run up to another street or the avenue are junctions. Along both sides of every
/// street lie lots (their frontage and depth drawn by the district: tall blocks in the middle, lower ones round them,
/// houses with yards and some pools further out, a few car parks and two parks); outside the town, the flat low land
/// is cut into fields along the avenue's direction, with a barn here and there.
/// </summary>
sealed class Town
{
    readonly Land _l;
    readonly Roads _r;
    readonly Random _rand;
    readonly int _w4, _h4;
    readonly double[] _dHighway, _dRail, _dStill;
    readonly double[] _theta;
    readonly bool[] _inTown;
    readonly (double X, double Y) _centre;
    readonly List<(double X, double Y, double R, bool Pond)> _parks = new();
    public List<Plot> Plots { get; } = new();
    public List<(double X, double Y)> PostalSpots { get; } = new();
    public List<Way> StreetWays { get; } = new();
    /// <summary>A coloured dot on the park without the pond (a point of interest).</summary>
    public List<(string Id, double X, double Y, string Color)> Dots { get; } = new();

    Town(Land l, Roads r, int seed)
    {
        _l = l;
        _r = r;
        _rand = new Random(seed);
        _w4 = (l.W - 1) / 2 + 1;
        _h4 = (l.H - 1) / 2 + 1;
        var avenue = r.Ways.First(w => w.Label == "avenue");
        // the district's middle: 150 m along the avenue from the quay
        _centre = avenue.P[Math.Min(avenue.P.Count - 1, 30)];
        _inTown = new bool[_w4 * _h4];
        var still = new Grid<bool>(_w4, _h4);
        for (int k = 0; k < _inTown.Length; k++)
        {
            var (u, v) = Local4(k);
            int i = _l.Node(u, v);
            bool land = i >= 0 && _l.IsLand[i];
            bool water = i >= 0 && !float.IsNaN(_l.Water[i]);
            _inTown[k] = land && !water && l.Layout.InTown(u, v);
            still.Data[k] = !land || (water && !_l.River[i]);
        }
        _dStill = Scaled(DistanceTransform.Distance(still));
        _dHighway = Scaled(DistanceTransform.Distance(Rasterize(r.Ways.Where(w => w.Highway))));
        _dRail = Scaled(DistanceTransform.Distance(Rasterize(r.Ways.Where(w => w.Rail))));
        _theta = Field(avenue, r.Ways.First(w => w.Label == "highway"));
    }

    public static Town Build(Land l, Roads r, Action<string> log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = new Town(l, r, l.Variant.Seed + 100);
        t.Parks();
        t.Streets(log);
        t.Lots(log);
        t.Farms(log);
        t.Landmarks();
        t.Postals();
        log($"town: {t.StreetWays.Count} streets, {t.Plots.Count(p => p.Kind is "tower" or "block")} town buildings, {t.Plots.Count(p => p.Kind == "house")} houses, "
            + $"{t.Plots.Count(p => p.Kind == "pool")} pools, {t.Plots.Count(p => p.Kind == "parking")} car parks, {t.Plots.Count(p => p.Kind is "soil" or "hay")} fields, "
            + $"{t.Plots.Count(p => p.Kind == "barn")} barns, {t.PostalSpots.Count} postal codes, {sw.Elapsed.TotalSeconds:0.0} s");
        return t;
    }

    static double Sq(double x) => x * x;

    double[] Scaled(double[] cells) => cells.Select(d => d * 4).ToArray();

    (double X, double Y) Local4(int k) => (_l.X0 + k % _w4 * 4 - _l.Cx, _l.Y0 - k / _w4 * 4 - _l.Cy);

    int Cell4(double u, double v)
    {
        int c = (int)Math.Round((u + _l.Cx - _l.X0) / 4), r = (int)Math.Round((_l.Y0 - (v + _l.Cy)) / 4);
        return (uint)c < (uint)_w4 && (uint)r < (uint)_h4 ? r * _w4 + c : -1;
    }

    Grid<bool> Rasterize(IEnumerable<Way> ways)
    {
        var g = new Grid<bool>(_w4, _h4);
        foreach (var w in ways)
            for (int i = 0; i < w.P.Count; i++)
                if (w.Kind.Length == 0 || w.Kind[i] != Run.Tunnel)
                    if (Cell4(w.P[i].X, w.P[i].Y) is var k and >= 0) g.Data[k] = true;
        return g;
    }

    /// <summary>The district of a point: 0 the middle (tall blocks), 1 round it, 2 the houses further out.</summary>
    public int District(double u, double v)
    {
        double d = Math.Sqrt(Sq(u - _centre.X) + Sq(v - _centre.Y)) + 40 * Noise.Fbm(u, v, 200, 2, 77);
        return d < 280 ? 0 : d < 540 ? 1 : 2;
    }

    public bool InTown(double u, double v) => Cell4(u, v) is var k and >= 0 && _inTown[k];

    // ---------------------------------------------------------------- the field of directions

    /// <summary>
    /// The streets' direction at every 4 m cell: the coast's line near the coast, the avenue's near the avenue, the
    /// highway's near it, and the avenue's everywhere a little; each weighed by its nearness, blended as doubled
    /// angles (a direction and its reverse are the same), then smoothed.
    /// </summary>
    double[] Field(Way avenue, Way highway)
    {
        int n = _w4 * _h4;
        var toSea = new double[n];
        for (int k = 0; k < n; k++) toSea[k] = _l.Node(Local4(k).X, Local4(k).Y) is var i and >= 0 ? _l.ToSea[i] * _l.Step : 0;
        (int[] Near, Dictionary<int, double> Dir) Along(Way w)
        {
            var g = new Grid<bool>(_w4, _h4);
            var dir = new Dictionary<int, double>();
            for (int i = 1; i + 1 < w.P.Count; i++)
                if (Cell4(w.P[i].X, w.P[i].Y) is var k and >= 0)
                {
                    g.Data[k] = true;
                    dir[k] = Math.Atan2(w.P[i + 1].Y - w.P[i - 1].Y, w.P[i + 1].X - w.P[i - 1].X);
                }
            return (DistanceTransform.Nearest(g), dir);
        }
        var (nearA, dirA) = Along(avenue);
        var (nearH, dirH) = Along(highway);
        double baseDir = Math.Atan2(avenue.P[^1].Y - avenue.P[0].Y, avenue.P[^1].X - avenue.P[0].X);
        var cx = new Grid<float>(_w4, _h4);
        var cy = new Grid<float>(_w4, _h4);
        for (int k = 0; k < n; k++)
        {
            int r = k / _w4, c = k % _w4;
            double sx = 0.2 * Math.Cos(2 * baseDir), sy = 0.2 * Math.Sin(2 * baseDir);
            void Add(double angle, double weight) { sx += weight * Math.Cos(2 * angle); sy += weight * Math.Sin(2 * angle); }
            if (r > 0 && r < _h4 - 1 && c > 0 && c < _w4 - 1)
            {
                double gx = toSea[k + 1] - toSea[k - 1], gy = toSea[k - _w4] - toSea[k + _w4];
                if (gx != 0 || gy != 0) Add(Math.Atan2(gy, gx) + Math.PI / 2, Math.Exp(-Sq(toSea[k] / 220)));
            }
            foreach (var (near, dir, reach, strength) in new[] { (nearA, dirA, 180.0, 1.3), (nearH, dirH, 160.0, 0.8) })
            {
                int j = near[k];
                if (j < 0 || !dir.TryGetValue(j, out var a)) continue;
                double d = Math.Sqrt(Sq(j / _w4 - r) + Sq(j % _w4 - c)) * 4;
                Add(a, strength * Math.Exp(-Sq(d / reach)));
            }
            cx.Data[k] = (float)sx;
            cy.Data[k] = (float)sy;
        }
        var bx = Gaussian.Scipy(cx, 5);
        var by = Gaussian.Scipy(cy, 5);
        var theta = new double[n];
        for (int k = 0; k < n; k++) theta[k] = 0.5 * Math.Atan2(by.Data[k], bx.Data[k]);
        return theta;
    }

    (double X, double Y) Direction(double u, double v, int family, (double X, double Y) previous)
    {
        int k = Cell4(u, v);
        double a = (k >= 0 ? _theta[k] : 0) + (family == 1 ? Math.PI / 2 : 0);
        var d = (X: Math.Cos(a), Y: Math.Sin(a));
        return d.X * previous.X + d.Y * previous.Y < 0 ? (-d.X, -d.Y) : d;
    }

    // ---------------------------------------------------------------- streets

    const double Step = 5;
    static readonly double[] Separation = [100, 75];

    sealed class Line
    {
        public required int Family { get; init; }
        public List<(double X, double Y)> P { get; } = new();
    }

    /// <summary>Points of the streets traced so far, by family, in 20 m buckets.</summary>
    readonly Dictionary<(int, int, int), List<(double X, double Y)>> _buckets = new();

    double NearestOfFamily(double u, double v, int family, double within)
    {
        double best = double.MaxValue;
        int r = (int)Math.Ceiling(within / 20);
        int bx = (int)Math.Floor(u / 20), by = (int)Math.Floor(v / 20);
        for (int i = bx - r; i <= bx + r; i++)
            for (int j = by - r; j <= by + r; j++)
                if (_buckets.TryGetValue((family, i, j), out var pts))
                    foreach (var p in pts) best = Math.Min(best, Sq(p.X - u) + Sq(p.Y - v));
        return Math.Sqrt(best);
    }

    void Remember(Line line)
    {
        foreach (var p in line.P)
        {
            var key = (line.Family, (int)Math.Floor(p.X / 20), (int)Math.Floor(p.Y / 20));
            if (!_buckets.TryGetValue(key, out var list)) _buckets[key] = list = new();
            list.Add(p);
        }
    }

    bool Open(double u, double v)
    {
        int k = Cell4(u, v);
        return k >= 0 && _inTown[k] && _dHighway[k] > 27 && _dRail[k] > 12 && _dStill[k] > 12 && !InPark(u, v, 0);
    }

    bool InPark(double u, double v, double margin) => _parks.Any(p => Sq(u - p.X) + Sq(v - p.Y) < Sq(p.R + margin));

    /// <summary>A street traced from a seed both ways along its family's direction until it leaves the open town, meets its own kind or turns too sharply.</summary>
    Line? Trace((double X, double Y) seed, int family)
    {
        double test = 0.55 * Separation[family];
        if (!Open(seed.X, seed.Y) || NearestOfFamily(seed.X, seed.Y, family, test) < test) return null;
        var line = new Line { Family = family };
        var halves = new List<(double X, double Y)>[2];
        for (int side = 0; side < 2; side++)
        {
            var pts = new List<(double X, double Y)>();
            var p = seed;
            var d0 = Direction(p.X, p.Y, family, (1, 0));
            (double X, double Y) prev = side == 0 ? d0 : (-d0.X, -d0.Y);
            for (int s = 0; s < 300; s++)
            {
                var d1 = Direction(p.X, p.Y, family, prev);
                var mid = (X: p.X + d1.X * Step / 2, Y: p.Y + d1.Y * Step / 2);
                var d2 = Direction(mid.X, mid.Y, family, d1);
                var q = (X: p.X + d2.X * Step, Y: p.Y + d2.Y * Step);
                if (d2.X * prev.X + d2.Y * prev.Y < Math.Cos(Math.PI / 5)) break;
                if (!Open(q.X, q.Y) || NearestOfFamily(q.X, q.Y, family, test) < test) break;
                pts.Add(q);
                p = q;
                prev = d2;
            }
            halves[side] = pts;
        }
        halves[1].Reverse();
        line.P.AddRange(halves[1]);
        line.P.Add(seed);
        line.P.AddRange(halves[0]);
        return (line.P.Count - 1) * Step >= 80 ? line : null;
    }

    void Streets(Action<string> log)
    {
        var avenue = _r.Ways.First(w => w.Label == "avenue");
        var lines = new List<Line>();
        var seeds = new Queue<((double X, double Y) P, int Family)>();
        // seeds: across the avenue every 75 m, and alongside it 100 m either way
        for (int i = 0; i < avenue.P.Count; i += 15)
        {
            var p = avenue.P[i];
            if (!InTown(p.X, p.Y)) continue;
            seeds.Enqueue((p, 1));
            var d = Direction(p.X, p.Y, 0, (1, 0));
            foreach (int side in new[] { 1, -1 }) seeds.Enqueue(((p.X - d.Y * side * 100, p.Y + d.X * side * 100), 0));
        }
        // the avenue itself counts as a street of the first family (the others keep their distance from it)
        Remember(new Line { Family = 0 }.Also(l => l.P.AddRange(avenue.P)));
        while (seeds.Count > 0 && lines.Count < 80)
        {
            var (seed, family) = seeds.Dequeue();
            if (Trace(seed, family) is not { } line) continue;
            lines.Add(line);
            Remember(line);
            int other = 1 - family;
            for (int i = 0; i < line.P.Count; i += (int)(Separation[other] / Step))
                seeds.Enqueue((line.P[i], other));
            for (int i = 0; i < line.P.Count; i += (int)(Separation[family] / Step))
            {
                int a = Math.Max(0, i - 1), b = Math.Min(line.P.Count - 1, i + 1);
                var t = Unit(line.P[b].X - line.P[a].X, line.P[b].Y - line.P[a].Y);
                foreach (int side in new[] { 1, -1 })
                    seeds.Enqueue(((line.P[i].X - t.Y * side * Separation[family], line.P[i].Y + t.X * side * Separation[family]), family));
            }
        }
        log($"streets traced: {lines.Count}");
        MakeStreetWays(lines, avenue, log);
    }

    static (double X, double Y) Unit(double x, double y)
    {
        double l = Math.Sqrt(x * x + y * y);
        return l == 0 ? (1, 0) : (x / l, y / l);
    }

    /// <summary>
    /// The traced lines as streets: every crossing of two lines, every crossing of a line and the avenue (on the
    /// avenue's nearest point) and every end within 25 m of another line or the avenue (joined to its nearest point)
    /// is a junction both share; the ends beyond the last junction are cut when shorter than 30 m; lines with no
    /// junction are dropped.
    /// </summary>
    void MakeStreetWays(List<Line> lines, Way avenue, Action<string> log)
    {
        var cuts = lines.Select(_ => new List<(double S, (double X, double Y) P)>()).ToList();
        double[] Lengths(List<(double X, double Y)> p)
        {
            var s = new double[p.Count];
            for (int i = 1; i < p.Count; i++) s[i] = s[i - 1] + Math.Sqrt(Sq(p[i].X - p[i - 1].X) + Sq(p[i].Y - p[i - 1].Y));
            return s;
        }
        var len = lines.Select(l => Lengths(l.P)).ToList();
        var avenueJoins = new HashSet<int>();
        var startShift = new double[lines.Count];
        (double S, int Seg, double T)? Hit(List<(double X, double Y)> a, double[] sa, int i, List<(double X, double Y)> b, int j)
        {
            var (p1, p2, p3, p4) = (a[i], a[i + 1], b[j], b[j + 1]);
            double den = (p2.X - p1.X) * (p4.Y - p3.Y) - (p2.Y - p1.Y) * (p4.X - p3.X);
            if (Math.Abs(den) < 1e-9) return null;
            double ta = ((p3.X - p1.X) * (p4.Y - p3.Y) - (p3.Y - p1.Y) * (p4.X - p3.X)) / den;
            double tb = ((p3.X - p1.X) * (p2.Y - p1.Y) - (p3.Y - p1.Y) * (p2.X - p1.X)) / den;
            if (ta < 0 || ta > 1 || tb < 0 || tb > 1) return null;
            return (sa[i] + ta * (sa[i + 1] - sa[i]), j, tb);
        }
        // line with line
        for (int a = 0; a < lines.Count; a++)
            for (int b = a + 1; b < lines.Count; b++)
            {
                if (lines[a].Family == lines[b].Family) continue;
                var (pa, pb) = (lines[a].P, lines[b].P);
                for (int i = 0; i + 1 < pa.Count; i++)
                    for (int j = 0; j + 1 < pb.Count; j++)
                    {
                        if (Math.Abs(pa[i].X - pb[j].X) > 12 || Math.Abs(pa[i].Y - pb[j].Y) > 12) continue;
                        if (Hit(pa, len[a], i, pb, j) is not { } h) continue;
                        double sb = len[b][j] + h.T * (len[b][j + 1] - len[b][j]);
                        var p = (pa[i].X + (pa[i + 1].X - pa[i].X) * (h.S - len[a][i]) / Math.Max(1e-9, len[a][i + 1] - len[a][i]),
                                 pa[i].Y + (pa[i + 1].Y - pa[i].Y) * (h.S - len[a][i]) / Math.Max(1e-9, len[a][i + 1] - len[a][i]));
                        cuts[a].Add((h.S, p));
                        cuts[b].Add((sb, p));
                    }
            }
        // line with the avenue (on the avenue's nearest point)
        int NearestOn(List<(double X, double Y)> pts, (double X, double Y) q)
        {
            int best = 0;
            double d = double.MaxValue;
            for (int i = 0; i < pts.Count; i++) if (Sq(pts[i].X - q.X) + Sq(pts[i].Y - q.Y) is var e && e < d) { d = e; best = i; }
            return best;
        }
        var alen = Lengths(avenue.P);
        for (int a = 0; a < lines.Count; a++)
        {
            var pa = lines[a].P;
            for (int i = 0; i + 1 < pa.Count; i++)
                for (int j = 0; j + 1 < avenue.P.Count; j++)
                {
                    if (Math.Abs(pa[i].X - avenue.P[j].X) > 12 || Math.Abs(pa[i].Y - avenue.P[j].Y) > 12) continue;
                    if (Hit(pa, len[a], i, avenue.P, j) is not { } h) continue;
                    int k = h.T < 0.5 ? j : j + 1;
                    avenueJoins.Add(k);
                    cuts[a].Add((h.S, avenue.P[k]));
                }
        }
        // ends that run up to another line or the avenue
        for (int a = 0; a < lines.Count; a++)
            foreach (bool atStart in new[] { true, false })
            {
                var end = atStart ? lines[a].P[0] : lines[a].P[^1];
                double sEnd = atStart ? 0 : len[a][^1];
                if (cuts[a].Any(c => Math.Abs(c.S - sEnd) < 10)) continue;
                double best = 25;
                (int Line, int Index)? to = null;
                for (int b = 0; b < lines.Count; b++)
                {
                    if (b == a || lines[b].Family == lines[a].Family) continue;
                    int i = NearestOn(lines[b].P, end);
                    double d = Math.Sqrt(Sq(lines[b].P[i].X - end.X) + Sq(lines[b].P[i].Y - end.Y));
                    if (d < best) { best = d; to = (b, i); }
                }
                int ai = NearestOn(avenue.P, end);
                double da = Math.Sqrt(Sq(avenue.P[ai].X - end.X) + Sq(avenue.P[ai].Y - end.Y));
                if (da < best)
                {
                    avenueJoins.Add(ai);
                    var p = avenue.P[ai];
                    if (atStart) { lines[a].P.Insert(0, p); startShift[a] = da; } else lines[a].P.Add(p);
                    cuts[a].Add((atStart ? -da : len[a][^1] + da, p));
                }
                else if (to is { } t)
                {
                    var p = lines[t.Line].P[t.Index];
                    if (atStart) { lines[a].P.Insert(0, p); startShift[a] = best; } else lines[a].P.Add(p);
                    cuts[a].Add((atStart ? -best : len[a][^1] + best, p));
                    cuts[t.Line].Add((len[t.Line][t.Index], p));
                }
            }
        // the streets: from the first junction to the last (longer loose ends kept), resampled with the junctions exact
        var names = Names().GetEnumerator();
        foreach (var (line, i) in lines.Select((l, i) => (l, i)))
        {
            var c = cuts[i].OrderBy(x => x.S).ToList();
            if (c.Count == 0) continue;
            var s = Lengths(line.P);
            double shift = startShift[i];   // an end joined at the start moved the line's lengths
            double s0 = c[0].S + shift, s1 = c[^1].S + shift;
            if (s0 < 30) s0 = 0;
            if (s[^1] - s1 < 30) s1 = s[^1];
            if (s1 - s0 < 40) continue;
            var pts = new List<(double X, double Y)>();
            var joins = new List<int>();
            (double X, double Y) At(double sq)
            {
                int k = 1;
                while (k < s.Length - 1 && s[k] < sq) k++;
                double f = Math.Clamp((sq - s[k - 1]) / Math.Max(1e-9, s[k] - s[k - 1]), 0, 1);
                return (line.P[k - 1].X + f * (line.P[k].X - line.P[k - 1].X), line.P[k - 1].Y + f * (line.P[k].Y - line.P[k - 1].Y));
            }
            var marks = c.Select(x => (S: x.S + shift, x.P)).Where(x => x.S >= s0 - 1 && x.S <= s1 + 1).ToList();
            var stops = new List<(double S, (double X, double Y) P, bool Join)> { (s0, At(s0), false) };
            stops.AddRange(marks.Select(m => (m.S, m.P, true)));
            stops.Add((s1, At(s1), false));
            stops = stops.OrderBy(x => x.S).ToList();
            for (int k = 0; k < stops.Count; k++)
            {
                if (k > 0)
                {
                    double from = stops[k - 1].S, to = stops[k].S;
                    int parts = Math.Max(1, (int)Math.Round((to - from) / Step));
                    for (int q = 1; q < parts; q++) pts.Add(At(from + (to - from) * q / parts));
                }
                if (pts.Count > 0 && Sq(pts[^1].X - stops[k].P.X) + Sq(pts[^1].Y - stops[k].P.Y) < 1) { if (stops[k].Join) joins.Add(pts.Count - 1); continue; }
                pts.Add(stops[k].P);
                if (stops[k].Join) joins.Add(pts.Count - 1);
            }
            names.MoveNext();
            var (en, ja) = names.Current;
            var way = new Way { Label = "street", En = en, Ja = ja, Width = 11, MaxGrade = 0.12, Priority = 2 };
            way.P = pts;
            way.Joins.UnionWith(joins);
            way.Joined.Add(avenue);
            StreetWays.Add(way);
        }
        foreach (var k in avenueJoins) avenue.Joins.Add(k);
        foreach (var w in StreetWays) w.Joined.AddRange(StreetWays.Where(o => o != w));
        foreach (var w in StreetWays)
        {
            _r.Profile(w);
            _r.Ways.Add(w);
        }
        log($"streets: {StreetWays.Count} ({StreetWays.Sum(w => (w.P.Count - 1) * Step) / 1000:0.0} km), {avenueJoins.Count} junctions on the avenue");
    }

    static IEnumerable<(string En, string Ja)> Names()
    {
        (string, string)[] words =
        [
            ("Anchor", "アンカー"), ("Pier", "ピア"), ("Cedar", "シダー"), ("Maple", "メープル"), ("Birch", "バーチ"), ("Dock", "ドック"), ("Market", "マーケット"),
            ("Lantern", "ランタン"), ("River", "リバー"), ("Willow", "ウィロー"), ("Beacon", "ビーコン"), ("Gull", "ガル"), ("Tide", "タイド"), ("Pine", "パイン"),
            ("Elm", "エルム"), ("Oak", "オーク"), ("Cove", "コーブ"), ("Bay", "ベイ"), ("Sail", "セイル"), ("Reef", "リーフ"), ("Orchard", "オーチャード"),
            ("Mill", "ミル"), ("Station", "ステーション"), ("Chapel", "チャペル"), ("Garden", "ガーデン"), ("Spring", "スプリング"), ("Hill", "ヒル"),
            ("Bridge", "ブリッジ"), ("Canal", "カナル"), ("Ferry", "フェリー"), ("Shell", "シェル"), ("Pearl", "パール"), ("Heron", "ヘロン"), ("Laurel", "ローレル"),
            ("Harbor", "ハーバー"), ("Salt", "ソルト"), ("Quarry", "クオリー"), ("Mission", "ミッション"), ("Summit", "サミット"), ("Crane", "クレーン"),
        ];
        (string, string)[] kinds = [("St", "ストリート"), ("Ln", "レーン"), ("Rd", "ロード")];
        for (int round = 0; ; round++)
            foreach (var (w, wj) in words)
            {
                var (k, kj) = kinds[round % kinds.Length];
                yield return ($"{w} {k}", $"{wj}・{kj}");
            }
    }

    // ---------------------------------------------------------------- parks, lots, fields

    void Parks()
    {
        var avenue = _r.Ways.First(w => w.Label == "avenue");
        foreach (var (at, off, r, pond) in new[] { (60, 150.0, 60.0, true), (150, -150.0, 50.0, false) })
        {
            if (at >= avenue.P.Count - 1) continue;
            var p = avenue.P[at];
            var t = Unit(avenue.P[at + 1].X - avenue.P[at - 1].X, avenue.P[at + 1].Y - avenue.P[at - 1].Y);
            var c = (X: p.X - t.Y * off, Y: p.Y + t.X * off);
            if (InTown(c.X, c.Y)) _parks.Add((c.X, c.Y, r, pond));
        }
    }

    /// <summary>The ground a plot or a way already takes (2 m grid).</summary>
    bool[] _taken = [];

    void Take(Way w, double extra)
    {
        double hw = w.Width / 2 + extra;
        for (int i = 0; i + 1 < w.P.Count; i++)
        {
            if (w.Kind[i] == Run.Tunnel && w.Kind[i + 1] == Run.Tunnel) continue;
            var (a, b) = (w.P[i], w.P[i + 1]);
            _r.ForBox(Math.Min(a.X, b.X) - hw, Math.Min(a.Y, b.Y) - hw, Math.Max(a.X, b.X) + hw, Math.Max(a.Y, b.Y) + hw, (k, u, v) =>
            {
                if (Roads.ToSegment(u, v, a, b).D <= hw) _taken[k] = true;
            });
        }
    }

    void ForRect(double x, double y, double w, double d, double turn, Action<int> each)
    {
        double c = Math.Cos(turn), s = Math.Sin(turn), r = Math.Sqrt(w * w + d * d) / 2;
        _r.ForBox(x - r, y - r, x + r, y + r, (k, u, v) =>
        {
            double du = u - x, dv = v - y, a = du * c + dv * s, b = -du * s + dv * c;
            if (Math.Abs(a) <= w / 2 && Math.Abs(b) <= d / 2) each(k);
        });
    }

    bool Free(double x, double y, double w, double d, double turn, bool town)
    {
        bool free = true;
        ForRect(x, y, w, d, turn, k =>
        {
            var (u, v) = _l.Local(k);
            if (_taken[k] || !_l.IsLand[k] || !float.IsNaN(_l.Water[k]) || (town && !InTown(u, v)) || InPark(u, v, 4)) free = false;
        });
        return free;
    }

    void Place(Plot p)
    {
        Plots.Add(p);
        ForRect(p.X, p.Y, p.Width, p.Depth, p.Turn, k => _taken[k] = true);
    }

    /// <summary>Lots along both sides of every street and of the avenue in the town.</summary>
    void Lots(Action<string> log)
    {
        _taken = new bool[_l.W * _l.H];
        foreach (var w in _r.Ways) Take(w, w.Rail ? 4 : 2);
        var streets = StreetWays.Concat(_r.Ways.Where(w => w.Label == "avenue")).ToList();
        foreach (var w in streets)
        {
            var s = new double[w.P.Count];
            for (int i = 1; i < w.P.Count; i++) s[i] = s[i - 1] + Math.Sqrt(Sq(w.P[i].X - w.P[i - 1].X) + Sq(w.P[i].Y - w.P[i - 1].Y));
            foreach (int side in new[] { 1, -1 })
            {
                double at = 8;
                while (at < s[^1] - 8)
                {
                    int i = Array.BinarySearch(s, at);
                    if (i < 0) i = Math.Min(w.P.Count - 2, ~i);
                    i = Math.Clamp(i, 1, w.P.Count - 2);
                    var p = w.P[i];
                    int district = District(p.X, p.Y);
                    double front = district switch { 0 => 32 + 12 * _rand.NextDouble(), 1 => 24 + 8 * _rand.NextDouble(), _ => 22 + 4 * _rand.NextDouble() };
                    double depth = district switch { 0 => 40 + 10 * _rand.NextDouble(), 1 => 28 + 8 * _rand.NextDouble(), _ => 32 };
                    int j = Math.Clamp(Array.BinarySearch(s, at + front / 2) is var bs && bs < 0 ? ~bs : bs, 1, w.P.Count - 2);
                    var c = w.P[j];
                    var t = Unit(w.P[j + 1].X - w.P[j - 1].X, w.P[j + 1].Y - w.P[j - 1].Y);
                    var n = (X: -t.Y * side, Y: t.X * side);
                    double back = w.Width / 2 + (district == 2 ? 2 : 3.5);
                    var lot = (X: c.X + n.X * (back + depth / 2), Y: c.Y + n.Y * (back + depth / 2));
                    double turn = Math.Atan2(t.Y, t.X);
                    if (InTown(lot.X, lot.Y) && Free(lot.X, lot.Y, front - 1, depth, turn, true))
                        Lot(lot, front - 1, depth, turn, n, district);
                    at += front;
                }
            }
        }
        foreach (var p in _parks) Plots.Add(new Plot(p.Pond ? "park-pond" : "park", p.X, p.Y, 2 * p.R, 2 * p.R, 0, 0, ""));
        log($"lots: {Plots.Count} plots");
    }

    void Lot((double X, double Y) c, double front, double depth, double turn, (double X, double Y) n, int district)
    {
        double near = Math.Clamp(1 - Math.Sqrt(Sq(c.X - _centre.X) + Sq(c.Y - _centre.Y)) / 300, 0, 1);
        double roll = _rand.NextDouble();
        if (district < 2 && roll < (district == 0 ? 0.1 : 0.08))
        {
            Place(new Plot("parking", c.X, c.Y, front, depth, turn, 0, "TARMAC"));
            return;
        }
        if (district < 2)
        {
            Place(new Plot("yard", c.X, c.Y, front, depth, turn, 0, "CONCRETE_PAVEMENT"));
            double h = district == 0 ? 18 + 70 * Math.Pow(near, 1.3) * (0.45 + 0.55 * _rand.NextDouble()) : 8 + 10 * _rand.NextDouble();
            Plots.Add(new Plot(district == 0 ? "tower" : "block", c.X, c.Y, front - (district == 0 ? 3 : 5), depth - (district == 0 ? 3 : 6), turn, h,
                _rand.NextDouble() < 0.6 ? "CONCRETE" : "ROOF_FELT"));
            return;
        }
        // a house near the street, its yard behind, and now and then a pool in the yard
        Place(new Plot("yard", c.X, c.Y, front, depth, turn, 0, ""));
        double toFront = -depth / 2 + 6 + 5;
        var house = (X: c.X + n.X * toFront, Y: c.Y + n.Y * toFront);
        double houseWidth = Math.Min(13, front - 6);
        Plots.Add(new Plot("house", house.X, house.Y, houseWidth, 10, turn, 6 + 1.5 * _rand.NextDouble(), "ROOF_TILE"));
        // the drive: 4 m wide, from the lot's front to the house, beside it
        double side = (houseWidth / 2 + 2.5) * (_rand.NextDouble() < 0.5 ? 1 : -1);
        var along = (X: n.Y, Y: -n.X);
        double driveMid = -depth / 2 + 5.5;
        Plots.Add(new Plot("drive", c.X + n.X * driveMid + along.X * side, c.Y + n.Y * driveMid + along.Y * side, 4, 11, turn + Math.PI / 2, 0, "CONCRETE_PAVEMENT"));
        if (_rand.NextDouble() < 0.45)
        {
            double toPool = toFront + 5 + 8;
            Plots.Add(new Plot("patio", c.X + n.X * toPool, c.Y + n.Y * toPool, 13, 8, turn, 0, "PAVING_SLAB"));
            Plots.Add(new Plot("pool", c.X + n.X * toPool, c.Y + n.Y * toPool, 9, 4.5, turn, 0, "CERAMIC"));
        }
    }

    /// <summary>Fields on the flat low land outside the town and the mountains: a grid along the avenue's direction, 120 x 80 m less 6 m lanes, a barn on some.</summary>
    void Farms(Action<string> log)
    {
        var avenue = _r.Ways.First(w => w.Label == "avenue");
        double a = Math.Atan2(avenue.P[^1].Y - avenue.P[0].Y, avenue.P[^1].X - avenue.P[0].X);
        var (ux, uy) = (Math.Cos(a), Math.Sin(a));
        var smooth = Gaussian.Scipy(new Grid<float>(_l.W, _l.H, _l.Ground), 3).Data;
        var slope = new float[_l.W * _l.H];
        for (int r = 1; r < _l.H - 1; r++)
            for (int c = 1; c < _l.W - 1; c++)
            {
                int k = r * _l.W + c;
                double gx = (smooth[k + 1] - smooth[k - 1]) / (2 * _l.Step), gy = (smooth[k + _l.W] - smooth[k - _l.W]) / (2 * _l.Step);
                slope[k] = (float)Math.Sqrt(gx * gx + gy * gy);
            }
        int fields = 0;
        const double fw = 120, fd = 80;
        for (int i = -12; i <= 12; i++)
            for (int j = -16; j <= 16; j++)
            {
                double x = i * fw * ux - j * fd * uy, y = i * fw * uy + j * fd * ux;
                if (Math.Abs(x) > 1080 || Math.Abs(y) > 1080) continue;
                int good = 0, all = 0;
                ForRect(x, y, fw - 6, fd - 6, a, k =>
                {
                    all++;
                    var (u, v) = _l.Local(k);
                    if (!_taken[k] && _l.IsLand[k] && float.IsNaN(_l.Water[k]) && slope[k] < 0.12 && !InTown(u, v) && _l.Layout.MountainDepth(u, v) < -80) good++;
                });
                if (all == 0 || good < 0.85 * all) continue;
                double roll = _rand.NextDouble();
                if (roll < 0.15) continue;
                Place(new Plot(roll < 0.6 ? "soil" : "hay", x, y, fw - 6, fd - 6, a, 0, roll < 0.6 ? "SOIL" : "HAY"));
                fields++;
                if (_rand.NextDouble() < 0.18)
                    Plots.Add(new Plot("barn", x + ux * (fw / 2 - 22) - uy * (fd / 2 - 14), y + uy * (fw / 2 - 22) + ux * (fd / 2 - 14), 28, 16, a, 8, "METAL_CORRUGATED_IRON"));
            }
        log($"farms: {fields} fields");
    }

    /// <summary>
    /// A hut at the dirt trail's end on the mountain, and a cottage on the headland's highest ground within 150 m of
    /// the sea; a red dot on the park without the pond.
    /// </summary>
    void Landmarks()
    {
        var trail = _r.Ways.FirstOrDefault(w => w.Label == "trail");
        if (trail is not null)
        {
            var e = trail.P[^1];
            var d = Unit(trail.P[^1].X - trail.P[^2].X, trail.P[^1].Y - trail.P[^2].Y);
            Plots.Add(new Plot("hut", e.X + d.X * 9, e.Y + d.Y * 9, 7, 5, Math.Atan2(d.Y, d.X), 4, "ROOF_TILE"));
        }
        int best = -1;
        for (int k = 0; k < _l.Ground.Length; k += 3)
        {
            if (!_l.IsLand[k] || _l.ToSea[k] * _l.Step > 150 || _l.ToSea[k] * _l.Step < 25) continue;
            var (u, v) = _l.Local(k);
            if (!_l.Layout.OnHeadland(u, v) || Math.Abs(u) > 1080 || Math.Abs(v) > 1080) continue;
            if (best < 0 || _l.Ground[k] > _l.Ground[best]) best = k;
        }
        if (best >= 0)
        {
            var (u, v) = _l.Local(best);
            Plots.Add(new Plot("hut", u, v, 9, 7, 0.4, 5, "ROOF_TILE"));
        }
        // a farmhouse beside the avenue out in the lowland (east of the town, off the mountains and the coast)
        var avenue = _r.Ways.First(w => w.Label == "avenue");
        for (int i = avenue.P.Count - 20; i > 0; i -= 4)
        {
            var p = avenue.P[i];
            var tn = Unit(avenue.P[i + 1].X - avenue.P[i - 1].X, avenue.P[i + 1].Y - avenue.P[i - 1].Y);
            var h = (X: p.X - tn.Y * 30, Y: p.Y + tn.X * 30);
            int k = _l.Node(h.X, h.Y);
            if (k < 0 || InTown(h.X, h.Y) || _l.Layout.MountainDepth(h.X, h.Y) > -100 || _l.Layout.OnHeadland(h.X, h.Y) || _l.ToSea[k] * _l.Step < 250) continue;
            if (!Free(h.X, h.Y, 16, 14, Math.Atan2(tn.Y, tn.X), false)) continue;
            Plots.Add(new Plot("hut", h.X, h.Y, 13, 10, Math.Atan2(tn.Y, tn.X), 7, "ROOF_TILE"));
            break;
        }
        foreach (var p in _parks.Where(p => !p.Pond)) Dots.Add(("1", p.X + _l.Cx, p.Y + _l.Cy, "#ff0000"));
    }

    /// <summary>Postal codes on buildings, at least 90 m apart (the huts first, then the town's, the houses, the barns).</summary>
    void Postals()
    {
        foreach (var p in Plots.Where(p => p.Kind == "hut").Concat(Plots.Where(p => p.Kind is "tower" or "block")).Concat(Plots.Where(p => p.Kind == "house")).Concat(Plots.Where(p => p.Kind == "barn")))
            if (PostalSpots.All(q => Sq(q.X - p.X) + Sq(q.Y - p.Y) > 90 * 90)) PostalSpots.Add((p.X, p.Y));
    }

    /// <summary>
    /// The town on the scan's surface, after the ground's materials and the roads: fields and yards (paving in the
    /// town, grass round the houses), car parks, parks (grass, one with a pond), then the buildings (their roofs at the
    /// highest ground under them plus their height), patios and pools (found by the water-collision probe).
    /// </summary>
    public void Paint(float[] top, byte[] material, float[] probe, Func<string, byte> mat)
    {
        // the town's ground paved within 45 m of the lots of its middle and the blocks round it (so the paving follows
        // the blocks), and a 3.5 m pavement along both sides of every street and the avenue in the town
        byte paving = mat("CONCRETE_PAVEMENT");
        var middle = new Grid<bool>(_l.W, _l.H);
        foreach (var p in Plots.Where(p => p.Kind is "yard" or "parking" && District(p.X, p.Y) < 2))
            ForRect(p.X, p.Y, p.Width, p.Depth, p.Turn, k => middle.Data[k] = true);
        var near = DistanceTransform.Distance(middle);
        bool Bare(int k) => _l.IsLand[k] && float.IsNaN(_l.Water[k]) && top[k] <= _l.Ground[k] + 0.5f;
        for (int k = 0; k < top.Length; k++)
        {
            if (near[k] * _l.Step > 45 || !Bare(k) || _taken[k]) continue;
            var (u, v) = _l.Local(k);
            if (InTown(u, v) && !InPark(u, v, 0)) material[k] = paving;
        }
        byte tarmac = mat("TARMAC");
        foreach (var w in StreetWays.Concat(_r.Ways.Where(w => w.Label == "avenue")))
        {
            double inner = w.Width / 2, outer = inner + 3.5;
            for (int i = 0; i + 1 < w.P.Count; i++)
            {
                if (w.Kind[i] != Run.Ground || w.Kind[i + 1] != Run.Ground) continue;
                var (a, b) = (w.P[i], w.P[i + 1]);
                _r.ForBox(Math.Min(a.X, b.X) - outer, Math.Min(a.Y, b.Y) - outer, Math.Max(a.X, b.X) + outer, Math.Max(a.Y, b.Y) + outer, (k, u, v) =>
                {
                    double d = Roads.ToSegment(u, v, a, b).D;
                    if (d > inner && d <= outer && Bare(k) && material[k] != tarmac && InTown(u, v) && !InPark(u, v, 0)) material[k] = paving;
                });
            }
        }
        foreach (var p in _parks)
            _r.ForBox(p.X - p.R, p.Y - p.R, p.X + p.R, p.Y + p.R, (k, u, v) =>
            {
                double d = Math.Sqrt(Sq(u - p.X) + Sq(v - p.Y));
                if (d > p.R || top[k] > _l.Ground[k] + 0.5f || !float.IsNaN(_l.Water[k]) || material[k] == mat("TARMAC")) return;
                material[k] = mat("GRASS");
                if (p.Pond && d < 18 + 4 * Noise.Fbm(u, v, 20, 2, 5))
                {
                    probe[k] = _l.Ground[k] - 0.1f;
                    top[k] = _l.Ground[k] - 1.2f;
                    material[k] = mat("MUD_UNDERWATER");
                }
            });
        foreach (var p in Plots.Where(p => p.Kind is "soil" or "hay" or "yard" or "parking" && p.Roof.Length > 0))
            ForRect(p.X, p.Y, p.Width, p.Depth, p.Turn, k => { if (top[k] <= _l.Ground[k] + 0.5f) material[k] = mat(p.Roof); });
        foreach (var p in Plots.Where(p => p.Kind is "tower" or "block" or "house" or "barn" or "hut"))
        {
            float ground = float.MinValue;
            ForRect(p.X, p.Y, p.Width, p.Depth, p.Turn, k => ground = Math.Max(ground, _l.Ground[k]));
            if (ground == float.MinValue) continue;
            byte roof = mat(p.Roof);
            ForRect(p.X, p.Y, p.Width, p.Depth, p.Turn, k => { top[k] = ground + (float)p.Height; material[k] = roof; });
        }
        foreach (var p in Plots.Where(p => p.Kind is "patio" or "drive"))
            ForRect(p.X, p.Y, p.Width, p.Depth, p.Turn, k => { if (top[k] <= _l.Ground[k] + 0.5f) material[k] = mat(p.Roof); });
        foreach (var p in Plots.Where(p => p.Kind == "pool"))
            ForRect(p.X, p.Y, p.Width, p.Depth, p.Turn, k =>
            {
                if (top[k] > _l.Ground[k] + 0.5f) return;
                probe[k] = _l.Ground[k] - 0.15f;
                top[k] = _l.Ground[k] - 1.8f;
                material[k] = mat(p.Roof);
            });
    }
}

static class Extensions
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
