using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Scan;

namespace FxMapGenerator.Core.RoadGraph;

/// <summary>
/// All parallel chains of one street within 20 m of each other (both carriageways, turn and bus lanes, the through
/// chain) are one road. At every station (4 m) the bundle's midline is the middle of the parallel chains; every member
/// is shifted onto it (the shift carried over its unpaired stretches and smoothed), and the road width is measured once
/// across the whole bundle from the on-road samples. Two stations pair only when their path heights differ by less than
/// <see cref="Options.MaxDz"/> m, so an elevated deck beside the ground-level road of the same name stays apart.
/// </summary>
public static class Bundles
{
    public sealed record Options(double Every = 4.0, int Ramp = 5, double LatMax = 20.0, double Half = 30.0, double Dt = 0.25, double MaxDz = 3.0);

    /// <summary>Chains bundled, and the chain pairs of one street kept apart by the height check (with their station counts).</summary>
    public sealed record Result(int Bundled, IReadOnlyDictionary<(int, int), int> HeightRejected);

    readonly record struct Sample(int Road, double Nx, double Ny, double S, double Z);

    public static Result Collapse(List<MapRoad> roads, ScanArea area, IParallelRunner parallel, Options? options = null)
    {
        var o = options ?? new Options();
        var cand = new List<int>();
        for (int i = 0; i < roads.Count; i++)
            if ((roads[i].Class is RoadClass.Street or RoadClass.Major or RoadClass.Highway) && roads[i].Attr.Street != 0) cand.Add(i);
        var pos = new List<(double X, double Y)>();
        var info = new List<Sample>();
        var byChain = new Dictionary<int, List<int>>();
        foreach (var i in cand)
        {
            var r = roads[i];
            var cum = Polyline.CumLen(r.Points);
            var list = byChain[i] = new List<int>();
            foreach (var st in Polyline.Stations(r.Points, o.Every, 1.0))
            {
                list.Add(pos.Count);
                double z = r.Zs.Count == r.Points.Count && r.Zs.Count > 0 ? Num.Interp(st.S, cum, r.Zs) : double.NaN;
                pos.Add((st.X, st.Y));
                info.Add(new Sample(i, st.Nx, st.Ny, st.S, z));
            }
        }
        var rejected = new Dictionary<(int, int), int>();
        if (pos.Count == 0) return new Result(0, rejected);

        var tree = new PointIndex(pos, o.LatMax + 3);
        var ts = SurfaceWidth.Offsets(o.Half, o.Dt);
        int c0 = ts.Length / 2;
        var shift = new (double X, double Y)[pos.Count];
        var width = new double[pos.Count];
        var neighbours = new int[pos.Count];
        const int Batch = 256;
        var batches = Enumerable.Range(0, (pos.Count + Batch - 1) / Batch).ToList();
        parallel.ForEach(batches, bi =>
        {
            var prof = new bool[ts.Length];
            var best = new List<(int Road, double Lat, double Along)>();
            var rej = new Dictionary<(int, int), int>();
            for (int j = bi * Batch; j < Math.Min(pos.Count, (bi + 1) * Batch); j++)
            {
                var (x, y) = pos[j];
                var me = info[j];
                uint street = roads[me.Road].Attr.Street;
                best.Clear();
                foreach (var k in tree.Within(x, y, o.LatMax + 3))
                {
                    var other = info[k];
                    int i2 = other.Road;
                    if (i2 == me.Road || roads[i2].Attr.Street != street || Math.Abs(me.Nx * other.Nx + me.Ny * other.Ny) < 0.85) continue;
                    if (o.MaxDz > 0 && double.IsFinite(me.Z) && double.IsFinite(other.Z) && Math.Abs(me.Z - other.Z) >= o.MaxDz)
                    {
                        var key = (Math.Min(me.Road, i2), Math.Max(me.Road, i2));
                        rej[key] = rej.GetValueOrDefault(key) + 1;
                        continue;
                    }
                    double dx = pos[k].X - x, dy = pos[k].Y - y;
                    double lat = dx * me.Nx + dy * me.Ny, along = Math.Abs(-dx * me.Ny + dy * me.Nx);
                    if (along > 3.0 || Math.Abs(lat) > o.LatMax) continue;
                    int at = best.FindIndex(b => b.Road == i2);
                    if (at < 0) best.Add((i2, lat, along));
                    else if (along < best[at].Along) best[at] = (i2, lat, along);
                }
                // the envelope midpoint: inner turn lanes do not move it
                double lo = 0.0, hi = 0.0;
                foreach (var b in best)
                {
                    if (b.Lat < lo) lo = b.Lat;
                    if (b.Lat > hi) hi = b.Lat;
                }
                neighbours[j] = best.Count;
                double target = (lo + hi) / 2.0;
                // across the bundle at the midpoint: the union of the on-road runs that hold a member; the road centre is
                // the centre of that union (grass or tarmac median alike), the width its extent
                double cx = x + me.Nx * target, cy = y + me.Ny * target;
                for (int t = 0; t < ts.Length; t++) prof[t] = area.OnRoad(cx + me.Nx * ts[t], cy + me.Ny * ts[t]);
                double minL = double.NaN, maxR = double.NaN;
                for (int m = -1; m < best.Count; m++)
                {
                    double rel = (m < 0 ? 0.0 : best[m].Lat) - target;
                    int c = Num.RoundToInt(c0 + rel / o.Dt);
                    if (c < 0 || c >= ts.Length || !prof[c]) continue;
                    if (SurfaceWidth.RunAround(prof, c, o.Dt, o.Half) is not { Open: false } r) continue;
                    double l = r.Left + rel, rr = r.Right + rel;
                    if (double.IsNaN(minL) || l < minL) minL = l;
                    if (double.IsNaN(maxR) || rr > maxR) maxR = rr;
                }
                width[j] = double.NaN;
                if (!double.IsNaN(minL) && maxR - minL <= 45.0)
                {
                    width[j] = maxR - minL;
                    if (neighbours[j] > 0) target += (maxR + minL) / 2.0;       // centre the ribbon on the surface
                }
                shift[j] = (me.Nx * target, me.Ny * target);
            }
            lock (rejected)
                foreach (var (k, v) in rej) rejected[k] = rejected.GetValueOrDefault(k) + v;
        });

        int n = 0;
        foreach (var i in cand)
        {
            var js = byChain[i];
            if (js.Count < 2) continue;
            var paired = new List<double>();
            var px = new List<double>();
            var py = new List<double>();
            for (int k = 0; k < js.Count; k++)
                if (neighbours[js[k]] > 0)
                {
                    paired.Add(k);
                    px.Add(shift[js[k]].X);
                    py.Add(shift[js[k]].Y);
                }
            if (paired.Count < 0.5 * js.Count) continue;
            var sx = new double[js.Count];
            var sy = new double[js.Count];
            for (int k = 0; k < js.Count; k++)
            {
                sx[k] = Num.Interp(k, paired, px);           // unpaired stations (tails, junction gaps) follow the neighbours
                sy[k] = Num.Interp(k, paired, py);
            }
            if (js.Count > 2 * o.Ramp + 1)
            {
                sx = Num.MovingMean(sx, o.Ramp);
                sy = Num.MovingMean(sy, o.Ramp);
            }
            var ss = js.Select(j => info[j].S).ToArray();
            var r = roads[i];
            var cum = Polyline.CumLen(r.Points);
            var moved = new List<P2>(r.Points.Count);
            for (int v = 0; v < r.Points.Count; v++)
                moved.Add(new P2(r.Points[v].X + Num.Interp(cum[v], ss, sx), r.Points[v].Y + Num.Interp(cum[v], ss, sy)));
            r.Points = moved;
            var ws = new List<double>();
            for (int k = 0; k < js.Count; k++)
                if (neighbours[js[k]] > 0 && double.IsFinite(width[js[k]])) ws.Add(width[js[k]]);
            if (ws.Count >= 3) r.BundleWidth = Num.Median(ws);
            r.Bundle = true;
            n++;
        }
        foreach (var r in roads)
        {
            if (!r.Bundle) continue;
            if (r.BundleWidth is { } w) r.Width = w;
            if (r.Class == RoadClass.Street) r.Class = RoadClass.Major;
        }
        return new Result(n, rejected);
    }
}
