using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Scan;

namespace FxMapGenerator.Core.RoadGraph;

/// <summary>
/// The road graph: the path graph generalised into map roads, one per street stretch, with a class and a width (the
/// street labels, the parking-structure rule of the ground cover and later stages read it).
/// <list type="number">
/// <item>chains: links paired at every node by the straightest continuation (<see cref="Chains"/>); junction hubs are
///   dropped from the line, so a road goes straight through a junction</item>
/// <item>width per chain: the median on-road run across it every 4 m (<see cref="SurfaceWidth"/>; on a link whose width
///   the road edits fix, that width instead, and the road keeps the result to the end); class from the flags, the name,
///   the lanes and the width</item>
/// <item>parallel chains of one street become one road on a common midline, measured once across (<see cref="Bundles"/>)</item>
/// <item>lines simplified (Douglas-Peucker 1.5 m, 2.5 m for bundles) and gentle corners cut</item>
/// <item>short street pieces between major pieces of the same street become major; one width per divided street</item>
/// <item>minor roads whose connected group meets the other roads at one node or none are parking aisles</item>
/// <item>fragments dropped: shorter than 8 m, shorter than their width (at least 20 m), short major and highway pieces,
///   short unnamed or minor dead ends</item>
/// </list>
/// </summary>
public static class RoadGraphBuilder
{
    public sealed record Options(double MaxTurn = 35.0, double Simplify = 1.5, double SimplifyBundle = 2.5, double MaxDz = 3.0);

    public sealed record Result(IReadOnlyList<MapRoad> Roads, int Nodes, int Links, int Chains, int Bundled,
        IReadOnlyDictionary<(int, int), int> HeightRejected, int ClassSmoothed, int Dropped);

    /// <summary>Builds the graph of the path nodes around the scanned area (60 m margin).</summary>
    public static Result Build(string pathsJson, ScanArea area, IParallelRunner parallel, Options? options = null, Action<string?, double>? report = null)
    {
        report?.Invoke("paths", 0);
        var net = PathNet.Read(pathsJson, area.X0, area.Y0, area.X0 + area.W, area.Y0 - area.H);
        return Build(net, area, parallel, options, report);
    }

    public static Result Build(PathNet net, ScanArea area, IParallelRunner parallel, Options? options = null, Action<string?, double>? report = null)
    {
        var o = options ?? new Options();
        report?.Invoke("chains", 0.1);
        var chains = Chains.Build(net, o.MaxTurn);

        report?.Invoke("widths", 0.2);
        var roads = new MapRoad[chains.Count];
        parallel.ForEach(Enumerable.Range(0, chains.Count).ToList(), i =>
        {
            var seq = chains[i];
            var attr = Chains.Attributes(net, seq);
            var line = Chains.Straighten(net, seq);
            var pts = line.Select(net.P).ToList();
            var fixedWidths = FixedWidths(net, seq, line);
            var (w, nw) = SurfaceWidth.Measure(area, pts, fixedWidths);
            // too short for 3 stations: the width of its longest link with a fixed width
            if (fixedWidths is not null && !double.IsFinite(w)) w = Longest(pts, fixedWidths);
            roads[i] = new MapRoad
            {
                Seq = seq, Attr = attr, Points = pts, Zs = line.Select(m => net.Nodes[m].Z).ToList(),
                Class = Classify(attr, w), Width = w, WidthSamples = nw, EditedWidth = fixedWidths is null ? null : w,
            };
        });
        var list = roads.ToList();

        report?.Invoke("bundles", 0.5);
        var bundles = Bundles.Collapse(list, area, parallel, new Bundles.Options(MaxDz: o.MaxDz));

        report?.Invoke("lines", 0.8);
        foreach (var r in list) r.Points = Polyline.Chaikin(Polyline.Rdp(r.Points, r.Bundle ? o.SimplifyBundle : o.Simplify));
        int smoothed = SmoothClasses(list);
        // one width per street for divided roads (pieces of the same boulevard measured 20 vs 33 m otherwise)
        foreach (var group in list.Where(r => r.Bundle && r.Attr.Street != 0).GroupBy(r => r.Attr.Street))
        {
            var rs = group.ToList();
            if (rs.Count < 2) continue;
            double w = Num.Median(rs.Select(r => r.Width).ToList());
            foreach (var r in rs) r.Width = w;
        }
        MarkParking(list);
        foreach (var r in list)
            if (!double.IsFinite(r.Width)) r.Width = r.Class.DefaultWidth();
        foreach (var r in list)
            if (r.EditedWidth is { } ew) r.Width = ew;
        var kept = DropStubs(net, list);
        report?.Invoke(null, 1);
        return new Result(kept, net.Nodes.Count, net.Links.Count, chains.Count, bundles.Bundled, bundles.HeightRejected, smoothed, list.Count - kept.Count);
    }

    /// <summary>
    /// Per segment of the chain's line (<see cref="Chains.Straighten"/>: its hubs left out), the width the road edits fix
    /// for its links (<see cref="PathNet.Link.FixedWidth"/>; the mean when a hub joins two), NaN where none; null when no
    /// link of the chain has one.
    /// </summary>
    public static double[]? FixedWidths(PathNet net, IReadOnlyList<int> seq, IReadOnlyList<int> line)
    {
        var link = new double[seq.Count - 1];
        bool any = false;
        for (int j = 0; j < link.Length; j++)
        {
            link[j] = net.Links[net.LinkOf(seq[j], seq[j + 1])].FixedWidth;
            any |= double.IsFinite(link[j]);
        }
        if (!any) return null;
        // the positions of the line's nodes in the chain: its ends and the nodes that are not hubs (Straighten)
        var at = new List<int> { 0 };
        for (int j = 1; j < seq.Count - 1; j++)
            if (!net.IsHub(seq[j])) at.Add(j);
        at.Add(seq.Count - 1);
        var o = new double[line.Count - 1];
        for (int k = 0; k < o.Length; k++)
        {
            double sum = 0;
            int n = 0;
            for (int j = at[k]; j < at[k + 1]; j++)
                if (double.IsFinite(link[j])) { sum += link[j]; n++; }
            o[k] = n > 0 ? sum / n : double.NaN;
        }
        return o;
    }

    /// <summary>The fixed width of the longest segment that has one.</summary>
    static double Longest(IReadOnlyList<P2> pts, double[] fixedWidths)
    {
        double best = -1, w = double.NaN;
        for (int k = 0; k < fixedWidths.Length; k++)
        {
            double len = Num.Hypot(pts[k + 1].X - pts[k].X, pts[k + 1].Y - pts[k].Y);
            if (double.IsFinite(fixedWidths[k]) && len > best) (best, w) = (len, fixedWidths[k]);
        }
        return w;
    }

    public static RoadClass Classify(ChainAttributes a, double width)
    {
        if (a.Highway > 0.5) return RoadClass.Highway;
        if (a.Unpaved > 0.5) return RoadClass.Track;
        double lanes = a.LanesForward + a.LanesBack;
        if (a.Named && (lanes >= 3 || (double.IsFinite(width) && width >= 16))) return RoadClass.Major;
        if (a.SwitchedOff > 0.5 && !a.Named && (!double.IsFinite(width) || width < 8)) return RoadClass.Minor;
        return RoadClass.Street;
    }

    /// <summary>
    /// A street piece shorter than 120 m that ends at a major piece of the same street is the same road: it becomes major
    /// and takes the wider width (so one road has no width steps where its carriageways split differently). In road
    /// order; a piece promoted counts for the pieces after it.
    /// </summary>
    static int SmoothClasses(List<MapRoad> roads)
    {
        var ends = new Dictionary<int, List<int>>();
        void End(int node, int i)
        {
            if (!ends.TryGetValue(node, out var l)) ends[node] = l = new List<int>();
            l.Add(i);
        }
        for (int i = 0; i < roads.Count; i++)
        {
            End(roads[i].Seq[0], i);
            End(roads[i].Seq[^1], i);
        }
        int n = 0;
        for (int i = 0; i < roads.Count; i++)
        {
            var r = roads[i];
            if (r.Class != RoadClass.Street || r.Length >= 120 || r.Attr.Street == 0) continue;
            foreach (var m in new[] { r.Seq[0], r.Seq[^1] })
            {
                foreach (var j in ends[m])
                    if (j != i && roads[j].Class == RoadClass.Major && roads[j].Attr.Street == r.Attr.Street)
                    {
                        r.Class = RoadClass.Major;
                        r.Width = double.IsFinite(r.Width) ? Num.PyMax(r.Width, roads[j].Width) : roads[j].Width;
                        n++;
                        break;
                    }
                if (r.Class == RoadClass.Major) break;
            }
        }
        return n;
    }

    /// <summary>Minor roads whose connected group (by shared path nodes) meets the other roads at one node or none are parking aisles.</summary>
    static void MarkParking(List<MapRoad> roads)
    {
        var owners = new Dictionary<int, List<int>>();
        for (int i = 0; i < roads.Count; i++)
            foreach (var m in roads[i].Seq)
            {
                if (!owners.TryGetValue(m, out var l)) owners[m] = l = new List<int>();
                if (!l.Contains(i)) l.Add(i);
            }
        var minor = Enumerable.Range(0, roads.Count).Where(i => roads[i].Class == RoadClass.Minor).ToList();
        var comp = new Dictionary<int, int>();
        foreach (var i in minor)
        {
            if (comp.ContainsKey(i)) continue;
            var stack = new Stack<int>();
            stack.Push(i);
            while (stack.Count > 0)
            {
                int j = stack.Pop();
                if (!comp.TryAdd(j, i)) continue;
                foreach (var m in roads[j].Seq)
                    foreach (var k in owners[m])
                        if (roads[k].Class == RoadClass.Minor && !comp.ContainsKey(k)) stack.Push(k);
            }
        }
        var attach = new Dictionary<int, HashSet<int>>();
        foreach (var i in minor)
        {
            if (!attach.TryGetValue(comp[i], out var set)) attach[comp[i]] = set = new HashSet<int>();
            foreach (var m in roads[i].Seq)
                if (owners[m].Any(k => roads[k].Class != RoadClass.Minor)) set.Add(m);
        }
        foreach (var i in minor)
            if (attach[comp[i]].Count <= 1) roads[i].Class = RoadClass.Parking;
    }

    /// <summary>
    /// Not map roads: chains shorter than 8 m, shorter than their width or 20 m, major and highway pieces under 30 m, and
    /// dead ends under 25 m that are unnamed or minor / parking (driveways, parking entrances).
    /// </summary>
    static List<MapRoad> DropStubs(PathNet net, List<MapRoad> roads, double minLen = 8.0, double stubLen = 25.0)
    {
        var o = new List<MapRoad>();
        foreach (var r in roads)
        {
            double len = r.Length;
            if (len < minLen) continue;
            double wmax = Math.Max(double.IsFinite(r.Width) ? r.Width : 0.0, 20.0);
            if (len < wmax || ((r.Class is RoadClass.Highway or RoadClass.Major) && len < 30.0)) continue;
            bool dead = net.IsFreeEnd(r.Seq[0]) || net.IsFreeEnd(r.Seq[^1]);
            if (dead && len < stubLen && (!r.Attr.Named || (r.Class is RoadClass.Minor or RoadClass.Parking))) continue;
            o.Add(r);
        }
        return o;
    }
}
