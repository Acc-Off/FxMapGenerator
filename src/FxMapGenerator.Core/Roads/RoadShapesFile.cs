using FxMapGenerator.Core.Grids;

namespace FxMapGenerator.Core.Roads;

/// <summary>
/// <c>data/road-shapes.grid</c>: the road shapes to draw, as a grid file (<see cref="GridFile"/>,
/// <c>Docs/spec/project-format.ja.md</c>). Game metres (x east, y north). Each collection of rings or lines keeps its
/// points as one <c>n x 2</c> f64 array and, per item, where its points start (<c>…Start</c>, i32, one more than the items):
/// <list type="bullet">
/// <item><c>tracks</c> (f64, n x 4): the segments of the unpaved tracks (x0, y0, x1, y1), drawn as lines <c>trackWidth</c> wide</item>
/// <item><c>ground.class</c>, <c>ground.casing(Start)</c>, <c>ground.fill(Start)</c>: the road and highway ribbons on the ground (casing ring, fill ring)</item>
/// <item><c>raised.level</c>, <c>raised.class</c>, <c>raised.casing(Start)</c>, <c>raised.fill(Start)</c>: the runs at level 1 and above (a road over another road)</item>
/// <item><c>cornerPatches.class</c>, <c>cornerPatches.ring(Start)</c>: where 2 or more road ends stop at a node, their corners joined</item>
/// <item><c>junctionCorners.class</c>, <c>.region(Start)</c>, <c>.arc(Start)</c>, <c>.seam(Start)</c>: the rounded corners at junctions</item>
/// <item><c>tunnels.group</c> (i32 per ring), <c>tunnels.ring(Start)</c>: the outline rings of each tunnel group</item>
/// </list>
/// Classes: 0 road, 1 highway, 2 unpaved track. Meta: <c>trackWidth</c>, <c>casing</c> (m), and the counts.
/// </summary>
public static class RoadShapesFile
{
    public const string FileName = "road-shapes.grid";

    public sealed record Contents(double[] Tracks, List<RoadRibbon> Ground, List<RaisedRun> Raised, List<CornerPatch> Patches,
        List<JunctionCorner> Corners, List<TunnelGroup> Tunnels, double TrackWidth);

    public static void Write(string path, RoadShapes s, IReadOnlyList<TunnelGroup> tunnels)
    {
        var f = new GridFile();
        f.Meta["trackWidth"] = RoadShapes.TrackWidth;
        f.Meta["casing"] = RoadShapes.Casing;
        f.Meta["pieces"] = s.Pieces.Count;
        f.Meta["tunnelPieces"] = s.TunnelPieces;
        f.Add("tracks", new Grid<double>(4, s.Tracks.Count / 4, s.Tracks.ToArray()));
        f.Add("ground.class", Classes(s.Ground.Select(r => r.Class)));
        AddRings(f, "ground.casing", s.Ground.Select(r => r.Casing));
        AddRings(f, "ground.fill", s.Ground.Select(r => r.Fill));
        f.Add("raised.level", Classes(s.Raised.Select(r => r.Level)));
        f.Add("raised.class", Classes(s.Raised.Select(r => r.Class)));
        AddRings(f, "raised.casing", s.Raised.Select(r => r.Casing));
        AddRings(f, "raised.fill", s.Raised.Select(r => r.Fill));
        f.Add("cornerPatches.class", Classes(s.Patches.Select(p => p.Class)));
        AddRings(f, "cornerPatches.ring", s.Patches.Select(p => p.Ring));
        f.Add("junctionCorners.class", Classes(s.Corners.Select(c => c.Class)));
        AddRings(f, "junctionCorners.region", s.Corners.Select(c => c.Region));
        AddRings(f, "junctionCorners.arc", s.Corners.Select(c => c.Arc));
        AddRings(f, "junctionCorners.seam", s.Corners.Select(c => c.Seam));
        var groups = new List<int>();
        var rings = new List<double[]>();
        for (int g = 0; g < tunnels.Count; g++)
            foreach (var r in tunnels[g].Rings) { groups.Add(g); rings.Add(r); }
        f.Add("tunnels.group", new Grid<int>(1, groups.Count, groups.ToArray()));
        AddRings(f, "tunnels.ring", rings);
        f.Save(path);
    }

    /// <summary>The contents <see cref="Read"/> would give for the shapes <see cref="Write"/> writes (nothing written).</summary>
    public static Contents ContentsOf(RoadShapes s, IReadOnlyList<TunnelGroup> tunnels)
    {
        var groups = new List<TunnelGroup>();
        for (int g = 0; g < tunnels.Count; g++)
            foreach (var r in tunnels[g].Rings)
            {
                while (groups.Count <= g) groups.Add(new TunnelGroup(new List<double[]>()));
                ((List<double[]>)groups[g].Rings).Add(r);
            }
        return new Contents(s.Tracks.ToArray(), s.Ground.ToList(), s.Raised.ToList(), s.Patches.ToList(), s.Corners.ToList(), groups, RoadShapes.TrackWidth);
    }

    public static Contents Read(string path)
    {
        var f = GridFile.Load(path);
        var tracks = f.Get<double>("tracks").Data;
        var gc = f.Get<byte>("ground.class").Data;
        var gCas = Rings(f, "ground.casing");
        var gFil = Rings(f, "ground.fill");
        var ground = gc.Select((c, i) => new RoadRibbon(c, gCas[i], gFil[i])).ToList();
        var rl = f.Get<byte>("raised.level").Data;
        var rc = f.Get<byte>("raised.class").Data;
        var rCas = Rings(f, "raised.casing");
        var rFil = Rings(f, "raised.fill");
        var raised = rl.Select((l, i) => new RaisedRun(l, rc[i], rCas[i], rFil[i])).ToList();
        var pc = f.Get<byte>("cornerPatches.class").Data;
        var pr = Rings(f, "cornerPatches.ring");
        var patches = pc.Select((c, i) => new CornerPatch(c, pr[i])).ToList();
        var cc = f.Get<byte>("junctionCorners.class").Data;
        var cReg = Rings(f, "junctionCorners.region");
        var cArc = Rings(f, "junctionCorners.arc");
        var cSeam = Rings(f, "junctionCorners.seam");
        var corners = cc.Select((c, i) => new JunctionCorner(c, cReg[i], cArc[i], cSeam[i])).ToList();
        var tg = f.Get<int>("tunnels.group").Data;
        var tr = Rings(f, "tunnels.ring");
        var tunnels = new List<TunnelGroup>();
        for (int i = 0; i < tg.Length; i++)
        {
            while (tunnels.Count <= tg[i]) tunnels.Add(new TunnelGroup(new List<double[]>()));
            ((List<double[]>)tunnels[tg[i]].Rings).Add(tr[i]);
        }
        return new Contents(tracks, ground, raised, patches, corners, tunnels, f.Meta["trackWidth"]!.GetValue<double>());
    }

    static Grid<byte> Classes(IEnumerable<int> values)
    {
        var a = values.Select(v => checked((byte)v)).ToArray();
        return new Grid<byte>(1, a.Length, a);
    }

    static void AddRings(GridFile f, string name, IEnumerable<double[]> rings)
    {
        var start = new List<int> { 0 };
        var pts = new List<double>();
        foreach (var r in rings)
        {
            pts.AddRange(r);
            start.Add(pts.Count / 2);
        }
        f.Add(name + "Start", new Grid<int>(1, start.Count, start.ToArray()));
        f.Add(name, new Grid<double>(2, pts.Count / 2, pts.ToArray()));
    }

    static List<double[]> Rings(GridFile f, string name)
    {
        var start = f.Get<int>(name + "Start").Data;
        var pts = f.Get<double>(name).Data;
        var o = new List<double[]>();
        for (int i = 0; i + 1 < start.Length; i++) o.Add(pts[(2 * start[i])..(2 * start[i + 1])]);
        return o;
    }
}
