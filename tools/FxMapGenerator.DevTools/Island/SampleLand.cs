using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.SampleIsland;

namespace FxMapGenerator.DevTools.Island;

/// <summary>
/// The made-up land of the style editor's sample, made by landscape evolution rather than drawn by hand: the sea to
/// the west, uplift growing to the east (mountains) with hills in the north and a headland; the rivers are where the
/// water gathers, a lake where the ground sank, and the ground's material follows height, slope, wetness and the sea.
/// The first stage has the land alone (no roads, no buildings).
/// </summary>
static class SampleLand
{
    /// <summary>A way to make the land: seed, the drainage's concavity (m), hillslope diffusion, steps, the highest summit (m).</summary>
    public sealed record Variant(string Name, int Seed, double M, double Diffusion, int Steps, double Summit);

    public static readonly Variant[] Variants =
    [
        new("a", 1, 0.45, 3.0, 500, 320),
        new("b", 2, 0.45, 3.0, 500, 320),
        new("c", 3, 0.45, 3.0, 500, 320),
    ];

    static readonly string[] MaterialNames =
        ["", "GRASS", "BUSHES", "SOIL", "DIRT_TRACK", "ROCK", "SNOW_LOOSE", "SAND_LOOSE", "SAND_WET", "SAND_UNDERWATER", "MUD_UNDERWATER", "GRAVEL_SMALL",
         "TARMAC", "GRAVEL_TRAIN_TRACK", "CONCRETE_PAVEMENT", "PAVING_SLAB", "CONCRETE", "ROOF_FELT", "ROOF_TILE", "METAL_CORRUGATED_IRON", "CERAMIC", "HAY"];

    static byte M(string name) => Array.IndexOf(MaterialNames, name) is var i and >= 0 ? (byte)i : throw new ArgumentException("no material " + name);

    static readonly string[] ZoneCodes = ["", "DOWNT", "BEACH", "SANDY", "GRAPES", "MTCHIL", "MTGORDO", "RICHM"];

    static readonly Dictionary<string, (string En, string Ja)> ZoneNames = new()
    {
        ["DOWNT"] = ("Port Kestrel", "ポート・ケストレル"),
        ["BEACH"] = ("Coral Beach", "コーラル・ビーチ"),
        ["SANDY"] = ("Gull Point", "ガル岬"),
        ["GRAPES"] = ("Clover Fields", "クローバー・フィールズ"),
        ["RICHM"] = ("Willow Park", "ウィロー・パーク"),
        ["MTCHIL"] = ("Juniper Hills", "ジュニパー・ヒルズ"),
        ["MTGORDO"] = ("Mount Aster", "アスター山"),
    };

    /// <summary>The land and water (no roads, no ground materials yet): the first half of making the sample.</summary>
    public static Land Terrain(Variant v, Action<string> log)
    {
        var (x0, y0, x1, y1) = SampleIsland.Rect;
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        // ---- landscape evolution on 12 m, over the cell and 600 m more to the north and the east (the river's catchment)
        const double dx = 12, pad = 12, beyond = 600;
        int w = (int)Math.Round((x1 - x0 + pad + beyond) / dx) + 1, h = w;
        double gx0 = x0 - pad, gy0 = y0 + beyond;
        var layout = new Layout(v.Seed);
        var start = new float[w * h];
        var uplift = new float[w * h];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                double u = gx0 + c * dx - cx, vv = gy0 - r * dx - cy;
                int i = r * w + c;
                double coast = layout.Coast(u, vv);
                if (coast < 0)
                {
                    start[i] = (float)-Math.Min(0.05 * -coast + 0.00012 * coast * coast, 65);
                    continue;
                }
                double river = layout.RiverDistance(u, vv);
                start[i] = (float)(0.5 + 0.003 * coast + 0.8 * (1 + Noise.Fbm(u, vv, 150, 3, v.Seed + 3)) - 4 * Math.Exp(-river * river / (2 * 60 * 60)));
                uplift[i] = (float)layout.Uplift(u, vv, coast, river);
            }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var z4 = LandscapeEvolution.Evolve(w, h, start, uplift, new LandscapeEvolution.Settings(dx, v.Steps, 10, 0.2, v.M, v.Diffusion), log);
        double top = 0;
        // scaled so the highest point inside the cell is the summit's height
        for (int i = 0; i < z4.Length; i++)
        {
            double gx = gx0 + i % w * dx, gy = gy0 - i / w * dx;
            if (start[i] > 0 && gx >= x0 && gx <= x1 && gy <= y0 && gy >= y1) top = Math.Max(top, z4[i]);
        }
        for (int i = 0; i < z4.Length; i++) if (start[i] > 0) z4[i] = (float)(z4[i] * v.Summit / top);
        log($"evolution: {w} x {h} at {dx} m, {v.Steps} steps, {sw.Elapsed.TotalSeconds:0.0} s; summit {v.Summit} m (was {top:0.00})");

        // ---- 2 m: interpolated, with fine detail and the steepest faces crumbled
        const double step = 2;
        const int per = (int)(dx / step);
        // the 2 m grid covers the cell and its pad: nodes (r, c) at (fx0 + c step, fy0 - r step)
        double fx0 = x0 - pad, fy0 = y0 + pad;
        int w2 = (int)Math.Round((x1 - x0 + 2 * pad) / step) + 1, h2 = w2;
        double offC = (fx0 - gx0) / dx, offR = (gy0 - fy0) / dx;
        var ground = new float[w2 * h2];
        var land = new bool[w2 * h2];
        for (int r = 0; r < h2; r++)
            for (int c = 0; c < w2; c++)
            {
                int i = r * w2 + c;
                double z = Bicubic(z4, w, h, offC + c / (double)per, offR + r / (double)per);
                double u = fx0 + c * step - cx, vv = fy0 - r * step - cy;
                land[i] = layout.Coast(u, vv) >= 0;
                if (land[i]) z = Math.Max(0.3, z + (0.3 + 0.006 * z) * Noise.Fbm(u, vv, 60, 3, v.Seed + 9));
                ground[i] = (float)z;
            }
        Thermal(ground, land, w2, h2, step, 1.0, 40);
        var landGrid = new Grid<bool>(w2, h2, land);
        var toLand = DistanceTransform.Distance(landGrid);
        var toSea = DistanceTransform.Distance(landGrid.Map(x => !x));
        var water = Enumerable.Repeat(float.NaN, w2 * h2).ToArray();
        for (int i = 0; i < ground.Length; i++)
            if (!land[i])
            {
                double d = toLand[i] * step;
                double u = fx0 + i % w2 * step - cx, vv = fy0 - i / w2 * step - cy;
                ground[i] = (float)SeaBed(d, u, vv, v.Seed);
                water[i] = 0;
            }

        // ---- a lake where a valley's floor sank, then the rivers where the water gathers
        var outlet = new bool[w2 * h2];
        for (int i = 0; i < outlet.Length; i++) outlet[i] = !land[i] || i % w2 == 0;
        var filled = (float[])ground.Clone();
        Flood.Fill(filled, w2, h2, outlet, 1e-4f);
        var area = Flood.Area(filled, w2, h2, step, outlet, out _);
        // the lake where the sketch has it: the ground there sinks (an elongated bowl, its edge wandering), the water
        // fills it to where it spills
        var (lu, lv) = Sketch.Local((Sketch.Lake.X, Sketch.Lake.Y));
        int lakeAt = (int)Math.Round((fy0 - (lv + cy)) / step) * w2 + (int)Math.Round((lu + cx - fx0) / step);
        {
            double la = Sketch.Lake.A * Sketch.Scale, lb = Sketch.Lake.B * Sketch.Scale, turn = Sketch.Lake.Turn * Math.PI / 180, ld = 8;
            for (int i = 0; i < ground.Length; i++)
            {
                double du = fx0 + i % w2 * step - cx - lu, dv = fy0 - i / w2 * step - cy - lv;
                double pa = du * Math.Cos(turn) + dv * Math.Sin(turn), pb = -du * Math.Sin(turn) + dv * Math.Cos(turn);
                double q = Sq(pa / la) + Sq(pb / lb) + 0.25 * Noise.Fbm(du + lu, dv + lv, 60, 2, v.Seed + 31);
                if (q < 1) ground[i] -= (float)(ld * (1 - q));
            }
            filled = (float[])ground.Clone();
            Flood.Fill(filled, w2, h2, outlet, 0);
            var pond = new Grid<bool>(w2, h2);
            for (int i = 0; i < pond.Count; i++) pond.Data[i] = land[i] && filled[i] - ground[i] > 0.4f;
            var lab = Components.Label(pond, 8);
            int lakeLabel = lab.Label.Data[lakeAt];
            for (int i = 0; i < pond.Count; i++)
                if (pond.Data[i] && lab.Label.Data[i] == lakeLabel && lakeLabel != 0) water[i] = filled[i];
            Flood.Fill(filled, w2, h2, outlet, 1e-4f);
            area = Flood.Area(filled, w2, h2, step, outlet, out _);
            log($"lake at {fx0 + lakeAt % w2 * step - cx:0}, {fy0 - lakeAt / w2 * step - cy:0} (local m), surface {water.Where(x => x > 0).DefaultIfEmpty(0).Max():0.0} m");
        }
        // the rivers along their meandering middle lines: the water surface falls from the source to the mouth (the
        // ground along the line, never rising again downstream), the bed under it, and the banks lowered into a valley
        var riverMask = new bool[w2 * h2];
        {
            var feature = new Grid<bool>(w2, h2);
            var owner = new Dictionary<int, (int River, int Point)>();
            var surfaces = new List<float[]>();
            for (int k = 0; k < layout.Rivers.Count; k++)
            {
                var line = layout.Rivers[k];
                var profile = new float[line.Length];
                float low = float.MaxValue;
                for (int j = 0; j < line.Length; j++)
                {
                    int node = NodeOf(line[j].X + cx, line[j].Y + cy, fx0, fy0, step, w2, h2);
                    float g = node >= 0 ? (land[node] ? ground[node] : 0) : low;
                    low = Math.Min(low, g);
                    profile[j] = Math.Max(0, low - 0.4f);
                    if (node >= 0 && !feature.Data[node]) { feature.Data[node] = true; owner[node] = (k, j); }
                }
                surfaces.Add(profile);
            }
            var nearestPoint = DistanceTransform.Nearest(feature);
            int riverCells = 0;
            for (int i = 0; i < ground.Length; i++)
            {
                int j = nearestPoint[i];
                if (!land[i] || !float.IsNaN(water[i]) || j < 0) continue;
                var (k, pt) = owner[j];
                double along = (double)pt / (layout.Rivers[k].Length - 1);
                double width = k == 0 ? 10 + 8 * along : 6 + 4 * along, depth = k == 0 ? 1.4 + 0.6 * along : 1.0;
                double d = Math.Sqrt(Sq(i / w2 - j / w2) + Sq(i % w2 - j % w2)) * step;
                float surface = surfaces[k][pt];
                if (d <= width / 2)
                {
                    water[i] = surface;
                    ground[i] = surface - (float)depth;
                    riverMask[i] = true;
                    riverCells++;
                }
                else if (d < 90) ground[i] = (float)SmoothMin(ground[i], surface + 0.5 + 0.12 * (d - width / 2) + 0.003 * Sq(d - width / 2), 1.5);
            }
            log($"rivers: {riverCells * step * step / 1e4:0.0} ha of water, {string.Join(" and ", layout.Rivers.Select(r => $"{(r.Length - 1) * 4 / 1000.0:0.00} km"))} long");
        }

        return new Land
        {
            Variant = v, Layout = layout, Cx = cx, Cy = cy, X0 = fx0, Y0 = fy0, Step = step, W = w2, H = h2, Ground = ground, IsLand = land, Water = water,
            River = riverMask, ToLand = toLand, ToSea = toSea, Area = area,
        };
    }

    /// <summary>The second half: the ground's materials and the zones, then the land as the sample's data (with the roads painted on).</summary>
    public static IslandData Finish(Land l, Roads? roads, Town? town, Action<string> log)
    {
        var v = l.Variant;
        var layout = l.Layout;
        double cx = l.Cx, cy = l.Cy, fx0 = l.X0, fy0 = l.Y0, step = l.Step;
        int w2 = l.W, h2 = l.H;
        var (ground, land, water, riverMask, toLand, toSea, area) = (l.Ground, l.IsLand, l.Water, l.River, l.ToLand, l.ToSea, l.Area);
        // ---- the ground's material
        var slope = Slope(ground, w2, h2, step);
        var material = new byte[w2 * h2];
        for (int i = 0; i < material.Length; i++)
        {
            double u = fx0 + i % w2 * step - cx, vv = fy0 - i / w2 * step - cy;
            if (!land[i])
            {
                // a rocky bed where the sea floor is steep or under cliffs
                bool rock = (slope[i] > 0.25 && toLand[i] * step < 250) || (toLand[i] * step < 60 && NearCliff(ground, land, w2, h2, i));
                material[i] = M(rock ? "ROCK" : "SAND_UNDERWATER");
                continue;
            }
            if (!float.IsNaN(water[i]))
            {
                material[i] = M(riverMask[i] ? "GRAVEL_SMALL" : "MUD_UNDERWATER");
                continue;
            }
            double z = ground[i], s = slope[i], sea = toSea[i] * step, wet = Math.Log10(area[i] + 1);
            double n1 = Noise.Fbm(u, vv, 60, 3, v.Seed + 21), n2 = Noise.Fbm(u, vv, 25, 3, v.Seed + 22);
            string m = "GRASS";
            if (wet > 4.0 + 0.4 * n1 && z < 0.6 * v.Summit) m = "BUSHES";
            else if (z > 30 && z < 0.55 * v.Summit && n1 > 0.1) m = "BUSHES";
            if (s > 0.9 + 0.15 * n2 && s <= 1.25 && n2 > -0.2) m = "SOIL";
            if (s > 1.25 + 0.15 * n2 || (z > 0.55 * v.Summit && s > 0.8 + 0.1 * n2)) m = "ROCK";
            if (z > 0.65 * v.Summit + 15 * n1 && s < 1.3) m = "SNOW_LOOSE";
            if (sea < 70 + 20 * n1 && z < 4 && s < 0.12) m = sea < 3 ? "SAND_WET" : "SAND_LOOSE";
            material[i] = M(m);
        }

        // ---- zones: the town, the mountains and their foothills, the headland, the coast, the lowland
        var zone = new byte[w2 * h2];
        for (int i = 0; i < zone.Length; i++)
        {
            if (!land[i]) continue;
            double u = fx0 + i % w2 * step - cx, vv = fy0 - i / w2 * step - cy, sea = toSea[i] * step;
            double md = layout.MountainDepth(u, vv);
            // the town: its middle and the blocks round it, then its houses (by the sea, or inland)
            string code = layout.InTown(u, vv)
                ? town is null || town.District(u, vv) < 2 ? "DOWNT" : sea < 300 ? "BEACH" : "RICHM"
                : md > 150 ? "MTGORDO" : md > -100 ? "MTCHIL" : layout.OnHeadland(u, vv) ? "SANDY" : sea < 250 ? "BEACH" : "GRAPES";
            zone[i] = (byte)Array.IndexOf(ZoneCodes, code);
        }

        var counts = new int[MaterialNames.Length];
        for (int i = 0; i < material.Length; i++) if (land[i] && float.IsNaN(water[i])) counts[material[i]]++;
        int dry = counts.Sum();
        log("ground: " + string.Join(", ", Enumerable.Range(1, MaterialNames.Length - 1).Where(k => counts[k] > 0).Select(k => $"{MaterialNames[k]} {100.0 * counts[k] / dry:0.0} %")));
        var steep = slope.Where((s, i) => land[i]).ToArray();
        Array.Sort(steep);
        log($"slope on land: median {steep[steep.Length / 2]:0.00}, 90 % {steep[steep.Length * 9 / 10]:0.00}, 99 % {steep[steep.Length * 99 / 100]:0.00}");

        var top = (float[])ground.Clone();
        var onRoad = new bool[w2 * h2];
        var street = new uint[w2 * h2];
        var probe = Enumerable.Repeat(float.NaN, w2 * h2).ToArray();
        roads?.Paint(l, top, material, onRoad, street, n => M(n));
        town?.Paint(top, material, probe, n => M(n));
        var postals = (town?.PostalSpots ?? []).OrderByDescending(p => Math.Round(p.Y / 80)).ThenBy(p => p.X)
            .Select((p, k) => ((101 + k).ToString(System.Globalization.CultureInfo.InvariantCulture), p.X + cx, p.Y + cy)).ToList();
        return new IslandData
        {
            Surface = new IslandSurface
            {
                X0 = fx0, Y0 = fy0, Step = step, Width = w2, Height = h2, Ground = ground, Top = top, Water = water, Probe = probe, Material = material,
                MaterialNames = MaterialNames, Zone = zone, ZoneCodes = ZoneCodes, OnRoad = onRoad, Street = street,
            },
            Nodes = roads?.Nodes ?? [],
            Links = roads?.Links ?? [],
            Streets = roads?.Streets ?? new Dictionary<uint, (string, string)>(),
            Zones = ZoneNames,
            Markers = roads?.Markers ?? [],
            Postals = postals,
            Dots = town?.Dots ?? [],
        };
    }

    static double Sq(double x) => x * x;

    /// <summary>The smaller of two values, rounded off over about <paramref name="k"/> where they meet (no crease).</summary>
    public static double SmoothMin(double a, double b, double k)
    {
        double hh = Math.Clamp(0.5 + 0.5 * (b - a) / k, 0, 1);
        return b + (a - b) * hh - k * hh * (1 - hh);
    }

    /// <summary>
    /// The sea bed's height at a distance <paramref name="d"/> (m) from the coast: deeper the farther out, but the
    /// distance stretched and shrunk by noise of 450 m and 120 m (shoals and deeps, so the depth bands do not follow the
    /// coast) and the steepness varied over 900 m; near the coast it stays shallow.
    /// </summary>
    static double SeaBed(double d, double u, double v, int seed)
    {
        double near = Noise.Smooth(d / 80);
        double stretched = Math.Max(0, d + near * (110 * Noise.Fbm(u, v, 450, 3, seed + 12) + 35 * Noise.Fbm(u, v, 120, 3, seed + 13)));
        double steep = 1 + 0.35 * Noise.Fbm(u, v, 900, 2, seed + 14);
        return -Math.Min((0.05 * stretched + 0.00012 * stretched * stretched) * steep + 0.1 * (1 + Noise.Fbm(u, v, 40, 2, seed + 11)), 65);
    }

    /// <summary>The node nearest to a point (game metres), or -1 outside the grid.</summary>
    static int NodeOf(double x, double y, double fx0, double fy0, double step, int w, int h)
    {
        int c = (int)Math.Round((x - fx0) / step), r = (int)Math.Round((fy0 - y) / step);
        return (uint)c < (uint)w && (uint)r < (uint)h ? r * w + c : -1;
    }

    /// <summary>Catmull-Rom interpolation of a grid at a fractional (column, row).</summary>
    static double Bicubic(float[] a, int w, int h, double fc, double fr)
    {
        int c = (int)Math.Floor(fc), r = (int)Math.Floor(fr);
        double tx = fc - c, ty = fr - r;
        double Row(int rr)
        {
            rr = Math.Clamp(rr, 0, h - 1);
            double P(int cc) => a[rr * w + Math.Clamp(cc, 0, w - 1)];
            return CatmullRom(P(c - 1), P(c), P(c + 1), P(c + 2), tx);
        }
        return CatmullRom(Row(r - 1), Row(r), Row(r + 1), Row(r + 2), ty);
    }

    static double CatmullRom(double p0, double p1, double p2, double p3, double t) =>
        0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t);

    /// <summary>Thermal erosion: ground steeper than <paramref name="talus"/> slides half its excess down to its lower neighbours.</summary>
    static void Thermal(float[] z, bool[] land, int w, int h, double step, double talus, int rounds)
    {
        var delta = new float[z.Length];
        Span<double> excess = stackalloc double[8];
        for (int k = 0; k < rounds; k++)
        {
            Array.Clear(delta);
            for (int r = 1; r < h - 1; r++)
                for (int c = 1; c < w - 1; c++)
                {
                    int i = r * w + c;
                    if (!land[i]) continue;
                    double total = 0, most = 0;
                    for (int n = 0; n < 8; n++)
                    {
                        int j = (r + Grid8.Dr[n]) * w + c + Grid8.Dc[n];
                        double e = z[i] - z[j] - talus * Grid8.Len[n] * step;
                        excess[n] = e > 0 && land[j] ? e : 0;
                        total += excess[n];
                        most = Math.Max(most, excess[n]);
                    }
                    if (total <= 0) continue;
                    double move = 0.5 * most;
                    delta[i] -= (float)move;
                    for (int n = 0; n < 8; n++)
                        if (excess[n] > 0) delta[(r + Grid8.Dr[n]) * w + c + Grid8.Dc[n]] += (float)(move * excess[n] / total);
                }
            for (int i = 0; i < z.Length; i++) z[i] += delta[i];
        }
    }

    static float[] Slope(float[] z, int w, int h, double step)
    {
        var s = new float[z.Length];
        for (int r = 1; r < h - 1; r++)
            for (int c = 1; c < w - 1; c++)
            {
                int i = r * w + c;
                double gx = (z[i + 1] - z[i - 1]) / (2 * step), gy = (z[i + w] - z[i - w]) / (2 * step);
                s[i] = (float)Math.Sqrt(gx * gx + gy * gy);
            }
        return s;
    }

    /// <summary>Whether land within 30 m of a sea point rises steeply (a cliff).</summary>
    static bool NearCliff(float[] z, bool[] land, int w, int h, int i)
    {
        int r0 = i / w, c0 = i % w;
        for (int r = Math.Max(0, r0 - 15); r <= Math.Min(h - 1, r0 + 15); r += 3)
            for (int c = Math.Max(0, c0 - 15); c <= Math.Min(w - 1, c0 + 15); c += 3)
                if (land[r * w + c] && z[r * w + c] > 25) return true;
        return false;
    }

    /// <summary>
    /// Where the lake goes: a valley floor in the hills (a fair drainage, a middle height, far from the sea and the
    /// grid's edges), the one with the most water gathered.
    /// </summary>
    static int LakePlace(float[] z, double[] area, bool[] land, double[] toSea, int w, int h, double step, double summit)
    {
        int best = -1;
        double flattest = double.MaxValue;
        int margin = (int)(400 / step), span = (int)(50 / step);
        for (int r = margin; r < h - margin; r += 2)
            for (int c = margin; c < w - margin; c += 2)
            {
                int i = r * w + c;
                if (!land[i] || toSea[i] * step < 450 || z[i] < 0.03 * summit || z[i] > 0.3 * summit) continue;
                if (area[i] < 20_000 || area[i] > 400_000) continue;
                // how flat: the most the ground within 50 m lies above or below
                double range = 0;
                for (int rr = r - span; rr <= r + span; rr += 5)
                    for (int cc = c - span; cc <= c + span; cc += 5)
                        range = Math.Max(range, Math.Abs(z[rr * w + cc] - z[i]));
                if (range < flattest) { flattest = range; best = i; }
            }
        return best;
    }
}

/// <summary>The sample's land and water on the 2 m grid (local metres from the cell's middle at (<see cref="Cx"/>, <see cref="Cy"/>)).</summary>
sealed class Land
{
    public required SampleLand.Variant Variant { get; init; }
    public required Layout Layout { get; init; }
    public required double Cx { get; init; }
    public required double Cy { get; init; }
    /// <summary>The grid's north-west node (game metres) and spacing.</summary>
    public required double X0 { get; init; }
    public required double Y0 { get; init; }
    public required double Step { get; init; }
    public required int W { get; init; }
    public required int H { get; init; }
    public required float[] Ground { get; init; }
    public required bool[] IsLand { get; init; }
    public required float[] Water { get; init; }
    public required bool[] River { get; init; }
    public required double[] ToLand { get; init; }
    public required double[] ToSea { get; init; }
    public required double[] Area { get; init; }

    public (double U, double V) Local(int i) => (X0 + i % W * Step - Cx, Y0 - i / W * Step - Cy);

    /// <summary>The node nearest to a local point, or -1 outside.</summary>
    public int Node(double u, double v)
    {
        int c = (int)Math.Round((u + Cx - X0) / Step), r = (int)Math.Round((Y0 - (v + Cy)) / Step);
        return (uint)c < (uint)W && (uint)r < (uint)H ? r * W + c : -1;
    }

    /// <summary>The ground at a local point, between the nodes.</summary>
    public double GroundAt(double u, double v)
    {
        double fc = (u + Cx - X0) / Step, fr = (Y0 - (v + Cy)) / Step;
        int c = Math.Clamp((int)Math.Floor(fc), 0, W - 2), r = Math.Clamp((int)Math.Floor(fr), 0, H - 2);
        double tx = Math.Clamp(fc - c, 0, 1), ty = Math.Clamp(fr - r, 0, 1);
        int i = r * W + c;
        return (Ground[i] * (1 - tx) + Ground[i + 1] * tx) * (1 - ty) + (Ground[i + W] * (1 - tx) + Ground[i + W + 1] * tx) * ty;
    }
}
