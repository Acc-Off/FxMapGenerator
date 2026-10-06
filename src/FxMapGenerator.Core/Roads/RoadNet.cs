using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Roads;

/// <summary>A path node the road shapes use; forks move nodes and add nodes of their own (<c>t|m</c>, <c>t|thr|s</c>).</summary>
public sealed class RoadNode
{
    public double X, Y, Z;
    public uint Street;
    public bool Junction, Highway, Tunnel, Unpaved;
    /// <summary>A fork branch's start: the trunk's previous node shifted alike (the curve runs on from the trunk).</summary>
    public (double X, double Y, double Z)? Phantom;
    /// <summary>A fork node whose branches together are narrower than the trunk: the trunk narrows to this width.</summary>
    public double? ForkWidth;
    /// <summary>A fork node: the link index of its trunk.</summary>
    public int? ForkTrunk;

    public RoadNode Copy() => (RoadNode)MemberwiseClone();
}

/// <summary>One direction of a connection as the path file gives it.</summary>
public sealed record LinkRecord(int LanesForward, int LanesBack, bool Narrow, bool DontUseForNavigation, bool Shortcut, double LaneOffset);

/// <summary>A connection between two nodes (<c>A</c> before <c>B</c> in key order) with its records in each direction.</summary>
public sealed class Connection
{
    public LinkRecord? Ab, Ba;
    /// <summary>The width (m) the road edits give the connection; null = from its lanes.</summary>
    public double? Width;
    /// <summary>A connection the road edits add.</summary>
    public bool Added;

    public IEnumerable<LinkRecord> Records { get { if (Ab is not null) yield return Ab; if (Ba is not null) yield return Ba; } }

    /// <summary>(lanes A to B, lanes B to A): of the A-to-B record when there is one, else from the B-to-A record.</summary>
    public (int Ab, int Ba) Lanes => Ab is not null ? (Ab.LanesForward, Ab.LanesBack) : (Ba!.LanesBack, Ba.LanesForward);
}

/// <summary>
/// The vehicle path graph of <c>game/paths.json</c> as the road shapes read it: every connection between two nodes, the
/// drawable ones (no shortcut links) with their neighbours and degrees. Node keys (<c>area:node</c>) compare as
/// strings (ordinal); a connection's key is its two node keys in that order.
/// </summary>
public sealed class RoadNet
{
    public static readonly IComparer<(string, string)> KeyOrder = Comparer<(string, string)>.Create((x, y) =>
    {
        int c = string.CompareOrdinal(x.Item1, y.Item1);
        return c != 0 ? c : string.CompareOrdinal(x.Item2, y.Item2);
    });

    public Dictionary<string, RoadNode> Nodes { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string, string), Connection> Links { get; } = new();
    public HashSet<(string, string)> Drawable { get; private set; } = new();
    public Dictionary<string, List<string>> Adjacent { get; private set; } = new(StringComparer.Ordinal);
    /// <summary>English street names by hash (the names file, and the names the road edits add).</summary>
    public Dictionary<uint, string> StreetNames { get; } = new();
    /// <summary>The square of each node's junction record (min x, min y, max x, max y).</summary>
    public Dictionary<string, double[]> Junctions { get; } = new(StringComparer.Ordinal);

    public static (string, string) Key(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    public int Degree(string node) => Adjacent.TryGetValue(node, out var l) ? l.Count : 0;

    public IReadOnlyList<string> Neighbours(string node) => Adjacent.TryGetValue(node, out var l) ? l : [];

    /// <summary>(lanes n to m, lanes m to n) of the connection between n and m.</summary>
    public (int Out, int In) LanesFrom(string n, string m)
    {
        var k = Key(n, m);
        var (ab, ba) = Links[k].Lanes;
        return k.Item1 == n ? (ab, ba) : (ba, ab);
    }

    /// <summary>The node's street name in English ("" without one).</summary>
    public string Street(RoadNode n) => n.Street != 0 && StreetNames.TryGetValue(n.Street, out var s) ? s : "";

    public double Length((string, string) k)
    {
        var a = Nodes[k.Item1];
        var b = Nodes[k.Item2];
        return Num.Hypot(b.X - a.X, b.Y - a.Y);
    }

    public static RoadNet Read(string pathsJson, string namesJson) => Of(PathFile.Read(pathsJson), namesJson);

    /// <summary>The graph of a path file (with the road edits applied to it) and the names file.</summary>
    public static RoadNet Of(PathFile paths, string namesJson)
    {
        var g = new RoadNet();
        foreach (var n in paths.Nodes)
        {
            g.Nodes[n.Key] = new RoadNode
            {
                X = n.X, Y = n.Y, Z = n.Z, Street = n.Street, Junction = n.Junction, Highway = n.Highway, Tunnel = n.Tunnel, Unpaved = n.Unpaved,
            };
            if (n.JunctionArea is { } ja) g.Junctions[n.Key] = ja;
        }
        foreach (var l in paths.Links)
        {
            string a = l.From, b = l.To;
            if (!g.Nodes.ContainsKey(a) || !g.Nodes.ContainsKey(b)) continue;
            var k = Key(a, b);
            if (!g.Links.TryGetValue(k, out var e)) g.Links[k] = e = new Connection();
            var r = new LinkRecord(l.LanesForward, l.LanesBack, l.Narrow, l.DontUseForNavigation, l.Shortcut, l.LaneOffset);
            if (a == k.Item1) e.Ab = r;
            else e.Ba = r;
            e.Width ??= l.Width;
            e.Added |= l.Added;
        }
        foreach (var (k, e) in g.Links)
            if (!e.Records.Any(r => r.Shortcut)) g.Drawable.Add(k);
        g.RebuildAdjacency();
        using (var fs = File.OpenRead(namesJson))
        using (var doc = JsonDocument.Parse(fs))
            foreach (var p in doc.RootElement.GetProperty("streets").EnumerateObject())
                if (p.Value.TryGetProperty("en", out var en) && en.GetString() is { Length: > 0 } s && uint.TryParse(p.Name, out var h))
                    g.StreetNames[h] = s;
        foreach (var (h, s) in paths.Streets) g.StreetNames[h] = s.En;
        return g;
    }

    void RebuildAdjacency()
    {
        Adjacent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (a, b) in Drawable.Order(KeyOrder))
        {
            if (!Adjacent.TryGetValue(a, out var la)) Adjacent[a] = la = new List<string>();
            if (!Adjacent.TryGetValue(b, out var lb)) Adjacent[b] = lb = new List<string>();
            la.Add(b);
            lb.Add(a);
        }
    }

    /// <summary>The drawable links cut into runs through nodes with exactly 2 drawable links (a loop is one run).</summary>
    public List<List<(string, string)>> Runs()
    {
        var seen = new HashSet<(string, string)>();
        var runs = new List<List<(string, string)>>();
        foreach (var k in Drawable.Order(KeyOrder))
        {
            if (!seen.Add(k)) continue;
            var run = new List<(string, string)> { k };
            foreach (var end in new[] { k.Item1, k.Item2 })
            {
                var prev = k;
                var node = end;
                while (Degree(node) == 2)
                {
                    string? next = null;
                    foreach (var m in Adjacent[node])
                        if (Key(node, m) != prev) { next = m; break; }
                    if (next is null) break;
                    var e = Key(node, next);
                    if (!seen.Add(e)) break;
                    run.Add(e);
                    prev = e;
                    node = next;
                }
            }
            runs.Add(run);
        }
        return runs;
    }

    // ------------------------------------------------------------------------------------------------ highways

    public const double PieceMin = 60.0;
    static readonly string[] FreewayExtra = ["Great Ocean Hwy", "Miriam Turner Overpass"];

    public static bool IsFreewayName(string s) => s.Contains("Fwy", StringComparison.Ordinal) || s.Contains("Freeway", StringComparison.Ordinal) || FreewayExtra.Contains(s);

    /// <summary>
    /// The links drawn as highway: a freeway name (Fwy, Freeway, Great Ocean Hwy, Miriam Turner Overpass) at either end;
    /// another name when both ends carry the highway flag in a flagged stretch of 60 m or more; no name at both ends when
    /// both are flagged (ramps). Groups of unnamed, unflagged, paved links touching highways only, at 2 or more distinct
    /// nodes, are the connectors between freeways. A link highway only by the freeway name at one end whose other end
    /// meets an ordinary road (not a dead-end side road hanging off that node alone) is the last piece of a ramp: a road.
    /// </summary>
    public HashSet<(string, string)> Highways()
    {
        var flagged = Drawable.Where(k => Nodes[k.Item1].Highway && Nodes[k.Item2].Highway).ToHashSet();
        var pieceLen = new Dictionary<(string, string), double>();
        foreach (var comp in Components(flagged))
        {
            double ln = 0;
            foreach (var e in comp) ln += Length(e);
            foreach (var e in comp) pieceLen[e] = ln;
        }
        var outSet = new HashSet<(string, string)>();
        var byOneName = new Dictionary<(string, string), string>();
        foreach (var k in Drawable)
        {
            string sa = Street(Nodes[k.Item1]), sb = Street(Nodes[k.Item2]);
            bool fa = IsFreewayName(sa), fb = IsFreewayName(sb);
            if (fa || fb)
            {
                outSet.Add(k);
                if (!flagged.Contains(k) && !(fa && fb)) byOneName[k] = fa ? k.Item2 : k.Item1;
            }
            else if (flagged.Contains(k) && ((sa.Length == 0 && sb.Length == 0) || pieceLen[k] >= PieceMin))
                outSet.Add(k);
        }
        var cand = Drawable.Where(k => !outSet.Contains(k) && Street(Nodes[k.Item1]).Length == 0 && Street(Nodes[k.Item2]).Length == 0
            && !(Nodes[k.Item1].Unpaved || Nodes[k.Item2].Unpaved)).ToHashSet();
        var spur = new HashSet<(string, string)>();
        foreach (var comp in Components(cand))
        {
            var cs = comp.ToHashSet();
            var cn = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in cs) { cn.Add(e.Item1); cn.Add(e.Item2); }
            var outside = new List<(string, string)>();
            foreach (var nd in cn)
                foreach (var m in Neighbours(nd))
                {
                    var o = Key(nd, m);
                    if (!cs.Contains(o)) outside.Add(o);
                }
            var touchNodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in outside)
            {
                if (cn.Contains(o.Item1)) touchNodes.Add(o.Item1);
                if (cn.Contains(o.Item2)) touchNodes.Add(o.Item2);
            }
            int touch = touchNodes.Count;
            bool allOut = outside.All(outSet.Contains);
            if (touch == 1 && allOut) spur.UnionWith(cs);
            if (touch >= 2 && allOut) outSet.UnionWith(cs);
        }
        var drop = byOneName.Where(kv => Neighbours(kv.Value).Any(m => !outSet.Contains(Key(kv.Value, m)) && !spur.Contains(Key(kv.Value, m))))
            .Select(kv => kv.Key).ToList();
        outSet.ExceptWith(drop);
        return outSet;
    }

    /// <summary>Connected groups of links (sharing a node), each in the order found.</summary>
    static List<List<(string, string)>> Components(HashSet<(string, string)> links)
    {
        var at = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        foreach (var k in links.Order(KeyOrder))
        {
            foreach (var n in new[] { k.Item1, k.Item2 })
            {
                if (!at.TryGetValue(n, out var l)) at[n] = l = new List<(string, string)>();
                l.Add(k);
            }
        }
        var seen = new HashSet<(string, string)>();
        var comps = new List<List<(string, string)>>();
        foreach (var k in links.Order(KeyOrder))
        {
            if (seen.Contains(k)) continue;
            var comp = new List<(string, string)>();
            var stack = new Stack<(string, string)>();
            stack.Push(k);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (!seen.Add(e)) continue;
                comp.Add(e);
                foreach (var nd in new[] { e.Item1, e.Item2 })
                    foreach (var f in at[nd])
                        if (!seen.Contains(f)) stack.Push(f);
            }
            comps.Add(comp);
        }
        return comps;
    }

    // ------------------------------------------------------------------------------------------------ lane widths

    public const double LaneWidth = 5.5, NarrowLaneWidth = 4.0;

    /// <summary>
    /// The road width from the path file alone: (lanes each way + 2 x lane offset) x 5.5 m, 4.0 m per lane on narrow
    /// links, at least one lane (the lane offset -0.5 marks single-track roads where both directions share one lane).
    /// </summary>
    public static double LaneWidthOf(Connection e)
    {
        var (lf, lb) = e.Lanes;
        var rs = e.Records.ToList();
        double off = 0;
        foreach (var r in rs) off += r.LaneOffset;
        off /= rs.Count;
        return WidthOfLanes(lf + lb, off, rs.Any(r => r.Narrow));
    }

    /// <summary>The width of <paramref name="lanes"/> lanes in both directions with the lane offset (see <see cref="LaneWidthOf"/>).</summary>
    public static double WidthOfLanes(int lanes, double offset, bool narrow) => Math.Max(1.0, lanes + 2 * offset) * (narrow ? NarrowLaneWidth : LaneWidth);

    // ------------------------------------------------------------------------------------------------ world grids from the landcover

    /// <summary>
    /// The water layer and the material class (surface) of the range's landcover over the project's frame, 1 m cells: row =
    /// floor(<paramref name="Y0"/> - y + 0.5), column = floor(x - <paramref name="X0"/> + 0.5) (the standard frame: 9002 x
    /// 13502 from (-4140, 8400)). <c>Water</c> 1 = open water or no landcover there (the open sea), <c>Surface</c> the class
    /// (0 where none).
    /// </summary>
    public sealed record WorldRaster(byte[] Water, byte[] Surface, double X0, double Y0, int Width, int Height)
    {
        /// <summary>The cell of a point, or -1 off the raster.</summary>
        public long Index(double x, double y)
        {
            long r = (long)Math.Floor(Y0 - y + 0.5), c = (long)Math.Floor(x - X0 + 0.5);
            return r >= 0 && r < Height && c >= 0 && c < Width ? r * Width + c : -1;
        }

        /// <summary>The cell of a point, the nearest edge cell off the raster.</summary>
        public long ClampedIndex(double x, double y)
        {
            long r = Math.Clamp((long)Math.Floor(Y0 - y + 0.5), 0, Height - 1), c = Math.Clamp((long)Math.Floor(x - X0 + 0.5), 0, Width - 1);
            return r * Width + c;
        }
    }

    /// <summary>
    /// The range's landcover as a <see cref="WorldRaster"/> over the frame. Blocks are laid in the order of their file
    /// names; where two overlap, water is either's and the class the later one's. <paramref name="notMade"/>: blocks of
    /// the range whose landcover is not made yet (the road editor's preview before the capture is over): land without a
    /// class there, not the open sea, so a road on them is kept and its paving is the path file's flag.
    /// </summary>
    public static WorldRaster WorldGrids(string dataFolder, IEnumerable<BlockId> blocks, MapFrame frame, Jobs.IParallelRunner? parallel = null, CancellationToken token = default,
        IEnumerable<BlockId>? notMade = null)
    {
        double worldX0 = frame.X0, worldY0 = frame.Y0;
        int worldW = (int)Math.Round(frame.X1 - frame.X0) + 2, worldH = (int)Math.Round(frame.Y0 - frame.Y1) + 2;
        var water = new byte[worldW * worldH];
        var known = new byte[worldW * worldH];
        var surface = new byte[worldW * worldH];
        var order = blocks.OrderBy(b => b.Name + "-", StringComparer.Ordinal).ToList();
        var layers = new (double X0, double Y0, Grid<bool> Water, Grid<byte> Surface)[order.Count];
        void Load(int i)
        {
            var f = GridFile.Load(LandcoverFile.PathOf(dataFolder, order[i]));
            layers[i] = (f.Meta["x0"]!.GetValue<double>(), f.Meta["y0"]!.GetValue<double>(), f.Get<bool>("water"), f.Get<byte>("surface"));
        }
        if (parallel is not null) parallel.ForEach(Enumerable.Range(0, order.Count).ToList(), Load);
        else for (int i = 0; i < order.Count; i++) { token.ThrowIfCancellationRequested(); Load(i); }
        foreach (var (x0, y0, w, s) in layers)
        {
            int r0 = (int)Math.Floor(worldY0 - y0 + 0.5), c0 = (int)Math.Floor(x0 - worldX0 + 0.5);
            int h = Math.Min(w.Height, worldH - r0), wd = Math.Min(w.Width, worldW - c0);
            for (int r = Math.Max(0, -r0); r < h; r++)
            {
                int row = (r0 + r) * worldW + c0;
                for (int c = Math.Max(0, -c0); c < wd; c++)
                {
                    known[row + c] = 1;
                    if (w[r, c]) water[row + c] = 1;
                    surface[row + c] = s[r, c];
                }
            }
        }
        foreach (var b in notMade ?? [])
        {
            var (bx0, by0, bx1, by1) = b.Rect;
            int r0 = Math.Max(0, (int)Math.Floor(worldY0 - by0 + 0.5)), r1 = Math.Min(worldH - 1, (int)Math.Floor(worldY0 - by1 + 0.5));
            int c0 = Math.Max(0, (int)Math.Floor(bx0 - worldX0 + 0.5)), c1 = Math.Min(worldW - 1, (int)Math.Floor(bx1 - worldX0 + 0.5));
            if (c1 < c0) continue;
            for (int r = r0; r <= r1; r++)
                Array.Fill(known, (byte)1, r * worldW + c0, c1 - c0 + 1);
        }
        for (int i = 0; i < water.Length; i++)
            if (known[i] == 0) water[i] = 1;
        return new WorldRaster(water, surface, worldX0, worldY0, worldW, worldH);
    }

    // ------------------------------------------------------------------------------------------------ boat routes

    /// <summary>(length m, length on water m) of a link, sampled every 2 m or less along the straight link.</summary>
    public (double Length, double OnWater) LinkWater((string, string) k, WorldRaster world, double step = 2.0)
    {
        var a = Nodes[k.Item1];
        var b = Nodes[k.Item2];
        double L = Num.Hypot(b.X - a.X, b.Y - a.Y);
        int n = Math.Max(2, (int)Math.Ceiling(L / step) + 1);
        var t = Num.Linspace(0.0, 1.0, n);
        int on = 0;
        for (int i = 0; i < n; i++)
        {
            long at = world.Index(a.X + (b.X - a.X) * t[i], a.Y + (b.Y - a.Y) * t[i]);
            if (at < 0 || world.Water[at] != 0) on++;
        }
        return (L, (double)on / n * L);
    }

    public sealed record BoatResult(int Runs, List<(List<(string, string)> Run, double Length, double OnWater)> Dropped,
        List<(List<(string, string)> Run, double Length, double OnWater)> KeptWet);

    /// <summary>
    /// Boat routes out of the drawable links: a run lying on water for half its length or more (bridges and piers are not
    /// water; a ford is a short part of a land run). Rebuilds the neighbours.
    /// </summary>
    public BoatResult DropBoatRoutes(WorldRaster world, double share = 0.5, double step = 2.0)
    {
        var runs = Runs();
        var dropped = new List<(List<(string, string)>, double, double)>();
        var keptWet = new List<(List<(string, string)>, double, double)>();
        var gone = new HashSet<(string, string)>();
        foreach (var run in runs)
        {
            double L = 0, Lw = 0;
            foreach (var k in run)
            {
                var (l, w) = LinkWater(k, world, step);
                L += l;
                Lw += w;
            }
            if (L > 0 && Lw >= share * L)
            {
                dropped.Add((run, L, Lw));
                gone.UnionWith(run);
            }
            else if (Lw >= 20.0) keptWet.Add((run, L, Lw));
        }
        Drawable.ExceptWith(gone);
        RebuildAdjacency();
        return new BoatResult(runs.Count, dropped, keptWet);
    }

    // ------------------------------------------------------------------------------------------------ unpaved by material

    /// <summary>Ground class to vote: 1 natural ground (grass, dirt, sand, beach, rock, vegetation, snow), 0 hard (urban, the DEFAULT material), -1 none (no hit, water material).</summary>
    static readonly sbyte[] Ground = [-1, 0, 1, 1, 1, 1, 1, 1, 1, -1, 0];
    public const double UnpavedShare = 0.5, MaterialStep = 2.0;

    /// <summary>
    /// Per drawable link (m on natural ground, m on a hard surface) at the middles of equal pieces (2 m or less) of the
    /// straight link; tunnel links (both ends flagged) and highway links do not vote.
    /// </summary>
    public Dictionary<(string, string), (double Ground, double Hard)> MaterialVotes(WorldRaster world, HashSet<(string, string)> highways)
    {
        var o = new Dictionary<(string, string), (double, double)>();
        foreach (var k in Drawable)
        {
            var a = Nodes[k.Item1];
            var b = Nodes[k.Item2];
            if ((a.Tunnel && b.Tunnel) || highways.Contains(k)) { o[k] = (0.0, 0.0); continue; }
            double L = Num.Hypot(b.X - a.X, b.Y - a.Y);
            int n = Math.Max(1, (int)Math.Ceiling(L / MaterialStep));
            double dl = L / n;
            int g = 0, h = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (i + 0.5) / n;
                double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t;
                int cls = world.Surface[world.ClampedIndex(x, y)];
                int v = cls < Ground.Length ? Ground[cls] : 0;
                if (v == 1) g++;
                else if (v == 0) h++;
            }
            o[k] = (dl * g, dl * h);
        }
        return o;
    }

    /// <summary>
    /// Unpaved links (a brown track line): per junction-to-junction run, when natural ground is half or more of its
    /// voting metres. Highway links, and runs without a single vote, keep the path file's unpaved flag at either end.
    /// </summary>
    public Dictionary<(string, string), bool> UnpavedLinks(Dictionary<(string, string), (double Ground, double Hard)> votes, HashSet<(string, string)> highways)
    {
        bool Flag((string, string) k) => Nodes[k.Item1].Unpaved || Nodes[k.Item2].Unpaved;
        var runOf = new Dictionary<(string, string), int>();
        var runs = Runs();
        for (int i = 0; i < runs.Count; i++)
            foreach (var k in runs[i]) runOf[k] = i;
        var acc = new Dictionary<int, (double Un, double De)>();
        foreach (var k in Drawable.Order(KeyOrder))
        {
            if (highways.Contains(k)) continue;
            var (gnd, hard) = votes[k];
            var u = acc.GetValueOrDefault(runOf[k]);
            acc[runOf[k]] = (u.Un + gnd, u.De + (gnd + hard));
        }
        var o = new Dictionary<(string, string), bool>();
        foreach (var k in Drawable)
        {
            if (highways.Contains(k)) { o[k] = Flag(k); continue; }
            var (un, de) = acc.GetValueOrDefault(runOf[k]);
            o[k] = de > 0 ? un >= UnpavedShare * de : Flag(k);
        }
        return o;
    }
}
