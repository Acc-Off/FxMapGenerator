using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.RoadEdits;

/// <summary>Values of a node in the road edits; null = not given (for a node of the game: not changed).</summary>
public sealed record NodeValues(double? X = null, double? Y = null, double? Z = null, uint? Street = null, bool? Highway = null, bool? Tunnel = null,
    bool? Unpaved = null, bool? SwitchedOff = null)
{
    public bool HasPosition => X is not null || Y is not null || Z is not null;
    public bool HasFlags => Street is not null || Highway is not null || Tunnel is not null || Unpaved is not null || SwitchedOff is not null;
}

/// <summary>
/// A node the road edits add (<c>added:&lt;number&gt;</c>) or a node of the game (<c>area:node</c>) they move, change or
/// hide, or that an edited link ends at. A game node always carries its position as it was (<see cref="Original"/>), and
/// the value as it was of every other field it changes. <see cref="Group"/>: the group of edits it belongs to (an id of
/// <see cref="RoadEditSet.Groups"/>), or null.
/// </summary>
public sealed record NodeEdit(string Key, NodeValues Set, bool Hidden = false, NodeValues? Original = null, string? Group = null)
{
    public const string AddedPrefix = "added:";

    public bool IsAdded => Key.StartsWith(AddedPrefix, StringComparison.Ordinal);

    /// <summary>The position after the edit (a game node's fields not given stay as they were).</summary>
    public (double X, double Y) Position => (Set.X ?? Original?.X ?? 0, Set.Y ?? Original?.Y ?? 0);
}

/// <summary>Lanes of a link from <c>from</c> to <c>to</c>, back, and the narrow-road flag; null = not given.</summary>
public sealed record LinkValues(int? LanesForward = null, int? LanesBack = null, bool? Narrow = null);

/// <summary>
/// A link the road edits add (no <see cref="Original"/>), or a link of the game (found by its two nodes, in either
/// direction) they change or hide: it always carries its lanes and narrow flag as they were. <see cref="Group"/>: the
/// group of edits it belongs to, or null.
/// </summary>
public sealed record LinkEdit(string From, string To, LinkValues Set, double? Width = null, bool Hidden = false, LinkValues? Original = null, string? Group = null)
{
    public bool IsAdded => Original is null;

    public string Key => $"{From} {To}";
}

/// <summary>
/// A street name the road edits add: its hash (the nodes' <c>street</c>, not 0 and none of the game's), the English name
/// and the Japanese one (null: the English one in Japanese too).
/// </summary>
public sealed record StreetName(uint Hash, string En, string? Ja);

/// <summary>
/// A group of edits that belong together (the bundled edits: an airfield's runways, a hiking trail): its id (the nodes'
/// and links' <c>group</c>) and its name (one text; the bundled groups' in English and Japanese, which a project taking
/// them in keeps in the screens' language). The screen takes a group in and takes it back as one; the map does not read
/// a group's name. <see cref="WhereFound"/>: the group's edits of the game's nodes and links apply where the project's
/// path data holds them as the edit found them, and are left out without a word where it does not (the bundled edits
/// that hide North Yankton's roads: a frame reads only some of their areas, and Cayo Perico's files take the place of
/// others); without it such an edit is listed as not applied.
/// </summary>
public sealed record EditGroup(string Id, ItemName Name, bool WhereFound = false);

/// <summary>An edit that was not applied: <c>node</c> or <c>link</c>, its key (a link's: its two nodes), and why (<see cref="RoadEditSet.Reasons"/>).</summary>
public sealed record NotAppliedEdit(string Kind, string Key, string Reason);

/// <summary>What applying the edits to the path file did.</summary>
public sealed record AppliedEdits(int NodesAdded, int NodesMoved, int NodesChanged, int NodesHidden, int LinksAdded, int LinksChanged, int LinksHidden,
    IReadOnlyList<NotAppliedEdit> NotApplied);

/// <summary>
/// The road edits of a project (<see cref="RoadEditsFile"/>): what they add to, move, change and hide in the game's path
/// data, and the street names they add. They are a difference: applied to <c>game/paths.json</c> as read
/// (<see cref="ApplyTo"/>), before the road graph and the road shapes build their graphs; the file the game-files stage
/// writes stays as it is. The labels take the street names beside the game's.
/// </summary>
public sealed class RoadEditSet
{
    /// <summary>Why an edit was not applied.</summary>
    public static class Reasons
    {
        /// <summary>The game's path data has no such node.</summary>
        public const string NodeMissing = "nodeMissing";
        /// <summary>The node's position or a value it changes differs from its original (the server's road data changed).</summary>
        public const string NodeChanged = "nodeChanged";
        /// <summary>The game's path data has no link between the two nodes.</summary>
        public const string LinkMissing = "linkMissing";
        /// <summary>The link's lanes or narrow flag differ from its original.</summary>
        public const string LinkChanged = "linkChanged";
        /// <summary>An added link where the game's path data already has one.</summary>
        public const string LinkExists = "linkExists";
        /// <summary>The edit of a node the link ends at is not applied.</summary>
        public const string EndNotApplied = "endNotApplied";
    }

    /// <summary>Positions (m) that count as the same: the path file and the edits file both keep 3 decimals.</summary>
    const double Same = 0.0005;

    public static readonly RoadEditSet Empty = new([], []);

    public RoadEditSet(IReadOnlyList<NodeEdit> nodes, IReadOnlyList<LinkEdit> links, IReadOnlyList<StreetName>? streets = null, IReadOnlyList<EditGroup>? groups = null)
    {
        Nodes = nodes;
        Links = links;
        Streets = streets ?? [];
        Groups = groups ?? [];
    }

    /// <summary>In the file's order.</summary>
    public IReadOnlyList<NodeEdit> Nodes { get; }
    public IReadOnlyList<LinkEdit> Links { get; }
    public IReadOnlyList<StreetName> Streets { get; }
    public IReadOnlyList<EditGroup> Groups { get; }

    public bool IsEmpty => Nodes.Count == 0 && Links.Count == 0 && Streets.Count == 0 && Groups.Count == 0;

    static (string, string) Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    /// <summary>
    /// Applies the edits to the path file: adds the nodes and links (after the game's, in the edits' order) and the street
    /// names, moves nodes, changes values, and takes out the hidden nodes (with their links) and hidden links. A game
    /// node is found by its key and must still be where its original says (and hold the original values of what the edit
    /// changes); a game link must still exist with its original lanes. An edit that finds neither is not applied, and
    /// neither is an edit of a link whose end node's edit is not applied; they are listed with the reason, but for those
    /// of a group that applies where found (<see cref="EditGroup.WhereFound"/>).
    /// </summary>
    public AppliedEdits ApplyTo(PathFile paths)
    {
        var (index, found, records, linkOk, notApplied) = Match(paths);
        foreach (var s in Streets) paths.Streets[s.Hash] = (s.En, s.Ja);

        int nodesAdded = 0, moved = 0, changed = 0, nodesHidden = 0, linksAdded = 0, linksChanged = 0, linksHidden = 0;
        var hiddenNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Nodes)
        {
            if (e.IsAdded)
            {
                var s = e.Set;
                paths.Nodes.Add(new PathFile.Node
                {
                    Key = e.Key, X = s.X!.Value, Y = s.Y!.Value, Z = s.Z!.Value, Street = s.Street!.Value, Highway = s.Highway!.Value,
                    Tunnel = s.Tunnel!.Value, Unpaved = s.Unpaved!.Value, SwitchedOff = s.SwitchedOff!.Value, Added = true,
                });
                nodesAdded++;
                continue;
            }
            if (!found[e.Key]) continue;
            if (e.Hidden)
            {
                hiddenNodes.Add(e.Key);
                nodesHidden++;
                continue;
            }
            var n = paths.Nodes[index[e.Key]];
            bool mv = false, ch = false;
            if (e.Set.X is { } x) { mv |= x != n.X; n.X = x; }
            if (e.Set.Y is { } y) { mv |= y != n.Y; n.Y = y; }
            if (e.Set.Z is { } z) { mv |= z != n.Z; n.Z = z; }
            if (e.Set.Street is { } st) { ch |= st != n.Street; n.Street = st; }
            if (e.Set.Highway is { } hw) { ch |= hw != n.Highway; n.Highway = hw; }
            if (e.Set.Tunnel is { } tu) { ch |= tu != n.Tunnel; n.Tunnel = tu; }
            if (e.Set.Unpaved is { } un) { ch |= un != n.Unpaved; n.Unpaved = un; }
            if (e.Set.SwitchedOff is { } so) { ch |= so != n.SwitchedOff; n.SwitchedOff = so; }
            if (mv) moved++;
            if (ch) changed++;
        }
        var hiddenPairs = new HashSet<(string, string)>();
        for (int j = 0; j < Links.Count; j++)
        {
            if (!linkOk[j]) continue;
            var e = Links[j];
            if (e.IsAdded)
            {
                paths.Links.Add(new PathFile.Link
                {
                    From = e.From, To = e.To, LanesForward = e.Set.LanesForward!.Value, LanesBack = e.Set.LanesBack!.Value, Narrow = e.Set.Narrow!.Value,
                    Width = e.Width, Added = true,
                });
                linksAdded++;
                continue;
            }
            if (e.Hidden)
            {
                hiddenPairs.Add(Pair(e.From, e.To));
                linksHidden++;
                continue;
            }
            foreach (var i in records[Pair(e.From, e.To)])
            {
                var l = paths.Links[i];
                bool along = l.From == e.From;
                if (e.Set.LanesForward is { } f) { if (along) l.LanesForward = f; else l.LanesBack = f; }
                if (e.Set.LanesBack is { } b) { if (along) l.LanesBack = b; else l.LanesForward = b; }
                if (e.Set.Narrow is { } nr) l.Narrow = nr;
                if (e.Width is { } w) l.Width = w;
            }
            linksChanged++;
        }
        if (hiddenNodes.Count > 0) paths.Nodes.RemoveAll(n => hiddenNodes.Contains(n.Key));
        if (hiddenNodes.Count + hiddenPairs.Count > 0)
            paths.Links.RemoveAll(l => hiddenNodes.Contains(l.From) || hiddenNodes.Contains(l.To) || hiddenPairs.Contains(Pair(l.From, l.To)));
        return new AppliedEdits(nodesAdded, moved, changed, nodesHidden, linksAdded, linksChanged, linksHidden, notApplied);
    }

    /// <summary>The edits <see cref="ApplyTo"/> would not apply to the path file, with the reason (the file is not changed).</summary>
    public IReadOnlyList<NotAppliedEdit> NotAppliedTo(PathFile paths) => Match(paths).NotApplied;

    /// <summary>
    /// Finds what the edits touch in the path file: the node index, whether each game node's edit applies, the link
    /// records of every pair a link edit names, whether each link edit applies, and the edits that do not with the reason.
    /// </summary>
    (Dictionary<string, int> Index, Dictionary<string, bool> Found, Dictionary<(string, string), List<int>> Records, bool[] LinkOk, List<NotAppliedEdit> NotApplied)
        Match(PathFile paths)
    {
        var index = new Dictionary<string, int>(paths.Nodes.Count, StringComparer.Ordinal);
        for (int i = 0; i < paths.Nodes.Count; i++) index[paths.Nodes[i].Key] = i;
        var notApplied = new List<NotAppliedEdit>();
        // the groups whose edits apply where found: what does not fit is left out without being listed
        var whereFound = Groups.Where(g => g.WhereFound).Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        bool Listed(string? group) => group is null || !whereFound.Contains(group);

        // game nodes: still as the edit found them?
        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var e in Nodes)
        {
            if (e.IsAdded) continue;
            string? why = !index.TryGetValue(e.Key, out var i) ? Reasons.NodeMissing : Matches(paths.Nodes[i], e.Original!) ? null : Reasons.NodeChanged;
            found[e.Key] = why is null;
            if (why is not null && Listed(e.Group)) notApplied.Add(new NotAppliedEdit("node", e.Key, why));
        }

        // links: the game's records of every pair an edit names
        var records = new Dictionary<(string, string), List<int>>();
        foreach (var e in Links) records[Pair(e.From, e.To)] = new List<int>();
        for (int i = 0; i < paths.Links.Count; i++)
            if (records.TryGetValue(Pair(paths.Links[i].From, paths.Links[i].To), out var list)) list.Add(i);
        var linkOk = new bool[Links.Count];
        for (int j = 0; j < Links.Count; j++)
        {
            var e = Links[j];
            var recs = records[Pair(e.From, e.To)];
            string? why = null;
            if (found.GetValueOrDefault(e.From, true) is false || found.GetValueOrDefault(e.To, true) is false) why = Reasons.EndNotApplied;
            else if (e.IsAdded) { if (recs.Count > 0) why = Reasons.LinkExists; }
            else if (recs.Count == 0) why = Reasons.LinkMissing;
            else
            {
                var first = paths.Links[recs[0]];
                var (lf, lb) = first.From == e.From ? (first.LanesForward, first.LanesBack) : (first.LanesBack, first.LanesForward);
                if (lf != e.Original!.LanesForward || lb != e.Original.LanesBack || first.Narrow != e.Original.Narrow) why = Reasons.LinkChanged;
            }
            if (why is null) linkOk[j] = true;
            else if (Listed(e.Group)) notApplied.Add(new NotAppliedEdit("link", e.Key, why));
        }
        return (index, found, records, linkOk, notApplied);
    }

    /// <summary>The node is where the original says, and holds the original value of every other field it gives.</summary>
    static bool Matches(PathFile.Node n, NodeValues o) =>
        Math.Abs(n.X - o.X!.Value) <= Same && Math.Abs(n.Y - o.Y!.Value) <= Same && Math.Abs(n.Z - o.Z!.Value) <= Same
        && (o.Street is null || o.Street == n.Street) && (o.Highway is null || o.Highway == n.Highway) && (o.Tunnel is null || o.Tunnel == n.Tunnel)
        && (o.Unpaved is null || o.Unpaved == n.Unpaved) && (o.SwitchedOff is null || o.SwitchedOff == n.SwitchedOff);

    /// <summary>Spacing (m) of the points along a link whose blocks it touches.</summary>
    const double BlockStep = 20.0;

    /// <summary>
    /// Per block an edit touches (of <see cref="MapFrame.Outer"/>, the frame every project's lies in), the digest of those
    /// edits (as the file writes them, in its order): a node touches the blocks of its position and its original, a link
    /// those along its line before and after the edit. A block whose digest differs from the last applied edits' holds a
    /// change (the estimates count its cells).
    /// </summary>
    public IReadOnlyDictionary<string, string> BlockDigests()
    {
        var byKey = Nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        var lines = new SortedDictionary<BlockId, List<string>>();
        var outer = MapFrame.Outer;
        void Touch(IEnumerable<BlockId> blocks, string line)
        {
            foreach (var b in blocks.Distinct())
            {
                if (!outer.Contains(b)) continue;
                if (!lines.TryGetValue(b, out var l)) lines[b] = l = new List<string>();
                l.Add(line);
            }
        }
        IEnumerable<BlockId> Along((double X, double Y) a, (double X, double Y) b)
        {
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            int n = Math.Max(1, (int)Math.Ceiling(len / BlockStep));
            for (int i = 0; i <= n; i++) yield return BlockId.At(a.X + (b.X - a.X) * i / n, a.Y + (b.Y - a.Y) * i / n);
        }
        foreach (var e in Nodes)
        {
            var at = new List<BlockId> { BlockId.At(e.Position.X, e.Position.Y) };
            if (e.Original is { X: { } ox, Y: { } oy }) at.Add(BlockId.At(ox, oy));
            Touch(at, RoadEditsFile.FormatNode(e));
        }
        foreach (var e in Links)
        {
            var a = byKey[e.From];
            var b = byKey[e.To];
            var at = Along(a.Position, b.Position).ToList();
            if (!a.IsAdded && !b.IsAdded) at.AddRange(Along((a.Original!.X!.Value, a.Original.Y!.Value), (b.Original!.X!.Value, b.Original.Y!.Value)));
            Touch(at, RoadEditsFile.FormatLink(e));
        }
        return lines.ToDictionary(kv => kv.Key.Name, kv => ContentDigest.Of(string.Join('\n', kv.Value)));
    }
}
