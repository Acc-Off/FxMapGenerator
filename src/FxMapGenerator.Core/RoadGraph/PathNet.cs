using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Roads;

namespace FxMapGenerator.Core.RoadGraph;

/// <summary>
/// The vehicle path graph of <c>game/paths.json</c> around a rectangle: the nodes and the links between them as
/// undirected connections (shortcut links left out; of the two directed links of a connection the first in the file
/// gives the lane counts). Order is the file's: nodes as listed, connections as
/// first met, each node's neighbours in connection order. Node keys (<c>area:node</c>) compare as strings.
/// </summary>
public sealed class PathNet
{
    /// <summary>A node; <see cref="Added"/>: one the road edits add.</summary>
    public readonly record struct Node(string Key, double X, double Y, double Z, uint Street, bool Junction, bool Highway, bool Unpaved, bool SwitchedOff,
        bool Added = false);

    /// <summary>
    /// A connection: <see cref="A"/> before <see cref="B"/> in key order; lanes in the direction A to B and back; the width
    /// the road edits fix for it (<see cref="FixedWidthOf"/>), NaN when none.
    /// </summary>
    public readonly record struct Link(int A, int B, int LanesAb, int LanesBa, bool DontUseForNavigation, bool Narrow, double FixedWidth = double.NaN);

    public IReadOnlyList<Node> Nodes => _nodes;
    public IReadOnlyList<Link> Links => _links;
    /// <summary>Neighbours of each node, in connection order.</summary>
    public IReadOnlyList<IReadOnlyList<int>> Adjacent => _adj;

    readonly List<Node> _nodes;
    readonly List<Link> _links = new();
    readonly Dictionary<(int, int), int> _linkIndex = new();
    readonly List<int>[] _adj;
    readonly int[] _rank;

    PathNet(List<Node> nodes, List<(string A, string B, int Lf, int Lb, bool Shortcut, bool DontUseForNavigation, bool Narrow, double Width)> links)
    {
        _nodes = nodes;
        var index = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++) index[nodes[i].Key] = i;
        var order = Enumerable.Range(0, nodes.Count).ToArray();
        Array.Sort(order, (a, b) => string.CompareOrdinal(nodes[a].Key, nodes[b].Key));
        _rank = new int[nodes.Count];
        for (int r = 0; r < order.Length; r++) _rank[order[r]] = r;
        foreach (var l in links)
        {
            if (!index.TryGetValue(l.A, out var a) || !index.TryGetValue(l.B, out var b) || l.Shortcut) continue;
            var k = Key(a, b);
            if (_linkIndex.ContainsKey(k)) continue;
            var (ab, ba) = k.Item1 == a ? (l.Lf, l.Lb) : (l.Lb, l.Lf);
            _linkIndex[k] = _links.Count;
            _links.Add(new Link(k.Item1, k.Item2, ab, ba, l.DontUseForNavigation, l.Narrow, l.Width));
        }
        _adj = new List<int>[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) _adj[i] = new List<int>();
        foreach (var l in _links)
        {
            _adj[l.A].Add(l.B);
            _adj[l.B].Add(l.A);
        }
    }

    /// <summary>
    /// The graph of the nodes within <paramref name="margin"/> m of the rectangle (<paramref name="x0"/>,
    /// <paramref name="y0"/>) north-west to (<paramref name="x1"/>, <paramref name="y1"/>) south-east.
    /// </summary>
    public static PathNet Read(string pathsJson, double x0, double y0, double x1, double y1, double margin = 60.0) =>
        Of(PathFile.Read(pathsJson), x0, y0, x1, y1, margin);

    /// <summary>The graph of the path file's nodes within <paramref name="margin"/> m of the rectangle (see <see cref="Read"/>).</summary>
    public static PathNet Of(PathFile paths, double x0, double y0, double x1, double y1, double margin = 60.0)
    {
        var nodes = new List<Node>();
        foreach (var n in paths.Nodes)
        {
            if (!(x0 - margin <= n.X && n.X <= x1 + margin && y1 - margin <= n.Y && n.Y <= y0 + margin)) continue;
            nodes.Add(new Node(n.Key, n.X, n.Y, n.Z, n.Street, n.Junction, n.Highway, n.Unpaved, n.SwitchedOff, n.Added));
        }
        var links = paths.Links.Select(l => (l.From, l.To, l.LanesForward, l.LanesBack, l.Shortcut, l.DontUseForNavigation, l.Narrow, FixedWidthOf(l))).ToList();
        return new PathNet(nodes, links);
    }

    /// <summary>
    /// The width the road edits fix for a connection: the width they give it, else for a connection they add the width
    /// of its lanes (as the road shapes take it: <see cref="RoadNet.WidthOfLanes"/>; it is not on the road scan, so it
    /// cannot be measured); NaN for the game's own connections.
    /// </summary>
    public static double FixedWidthOf(PathFile.Link l) =>
        l.Width ?? (l.Added ? RoadNet.WidthOfLanes(l.LanesForward + l.LanesBack, l.LaneOffset, l.Narrow) : double.NaN);

    /// <summary>A graph from nodes and directed links given in memory (tests).</summary>
    public static PathNet Of(IReadOnlyList<Node> nodes, IEnumerable<(string A, string B, int Lf, int Lb)> links) =>
        new(nodes.ToList(), links.Select(l => (l.A, l.B, l.Lf, l.Lb, false, false, false, double.NaN)).ToList());

    /// <summary>The two nodes in key order.</summary>
    public (int, int) Key(int a, int b) => _rank[a] < _rank[b] ? (a, b) : (b, a);

    /// <summary>True when node <paramref name="a"/>'s key sorts before <paramref name="b"/>'s.</summary>
    public bool KeyBefore(int a, int b) => _rank[a] < _rank[b];

    public int LinkOf(int a, int b) => _linkIndex[Key(a, b)];

    /// <summary>(lanes a to b, lanes b to a).</summary>
    public (int Forward, int Back) LanesFrom(int a, int b)
    {
        var l = _links[LinkOf(a, b)];
        return l.A == a ? (l.LanesAb, l.LanesBa) : (l.LanesBa, l.LanesAb);
    }

    public P2 P(int n) => new(_nodes[n].X, _nodes[n].Y);

    /// <summary>A junction hub: a junction-flagged node with three or more connections.</summary>
    public bool IsHub(int n) => _nodes[n].Junction && _adj[n].Count >= 3;

    /// <summary>A dead end: at most one connection.</summary>
    public bool IsFreeEnd(int n) => _adj[n].Count <= 1;
}
