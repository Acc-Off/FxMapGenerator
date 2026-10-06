using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.RoadEdits;

/// <summary>
/// The road edits file of a project (<c>Docs/spec/project-format.ja.md</c>, the project's <c>roadEdits</c>): the
/// nodes and links the map's roads add to, move, change and hide in the game's path data, the street names it adds, and
/// the groups of edits that belong together (the map does not read them).
/// <code>
/// {"format": 1,
///  "groups": {"&lt;id&gt;": "name"},                      (the bundled file's: {"&lt;id&gt;": {"en", "ja"?}}; a group that applies
///                                                   where found: {"name", "whereFound": true}, bundled {"en", "ja"?, "whereFound": true})
///  "streets": {"&lt;hash&gt;": {"en", "ja"?}},
///  "nodes": {"added:1": {"x", "y", "z", "street", "highway", "tunnel", "unpaved", "switchedOff", "group"?},
///            "784:40": {"x"?, "y"?, "z"?, "street"?, ..., "hidden"?, "original": {"x", "y", "z", "street"?, ...}, "group"?}},
///  "links": [{"from", "to", "lanesForward", "lanesBack", "narrow", "width", "group"?},
///            {"from", "to", "lanesForward"?, "lanesBack"?, "narrow"?, "width"?, "hidden"?, "original": {"lanesForward", "lanesBack", "narrow"}, "group"?}]}
/// </code>
/// Reading checks everything and names every problem at once. Positions are written at 0.001 m, one group, street name,
/// node or link a line; <c>groups</c> and <c>streets</c> only when there are any.
/// </summary>
public static partial class RoadEditsFile
{
    public const string FileName = "road-edits.json";
    public const int CurrentFormat = 1;
    public const int MaxLanes = 7;
    public const double MaxWidth = 100;

    [GeneratedRegex(@"^(added:[1-9][0-9]*|[0-9]+:[0-9]+)$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex GroupPattern();

    static readonly string[] NodeFields = ["x", "y", "z", "street", "highway", "tunnel", "unpaved", "switchedOff", "hidden", "original", "group"];
    static readonly string[] NodeValueFields = ["x", "y", "z", "street", "highway", "tunnel", "unpaved", "switchedOff"];
    static readonly string[] LinkFields = ["from", "to", "lanesForward", "lanesBack", "narrow", "width", "hidden", "original", "group"];
    static readonly string[] LinkValueFields = ["lanesForward", "lanesBack", "narrow"];
    static readonly string[] StreetFields = ["en", "ja"];
    /// <summary>A group's field: its edits apply where the project's path data holds what they change (<see cref="EditGroup.WhereFound"/>).</summary>
    const string GroupWhereFound = "whereFound";
    /// <summary>Names are written as they are (not escaped), the file is read by people too.</summary>
    static readonly JsonSerializerOptions Text = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <exception cref="RoadEditsException">The file is missing, not JSON, or not usable.</exception>
    public static RoadEditSet Read(string path)
    {
        if (!File.Exists(path)) throw new RoadEditsException($"road edits not found: {path}");
        return Parse(File.ReadAllBytes(path), path);
    }

    /// <exception cref="RoadEditsException">Not JSON, or not usable: every problem named.</exception>
    public static RoadEditSet Parse(ReadOnlySpan<byte> json, string name)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new RoadEditsException($"{name} is not valid JSON: {ex.Message}"); }
        using (doc)
        {
            var p = new List<string>();
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new RoadEditsException($"{name}: expected {{\"format\": 1, \"nodes\": {{...}}, \"links\": [...]}}");
            foreach (var f in root.EnumerateObject())
                if (f.Name is not ("format" or "groups" or "streets" or "nodes" or "links")) p.Add($"unknown field '{f.Name}'");
            if (!root.TryGetProperty("format", out var fmt) || fmt.ValueKind != JsonValueKind.Number || !fmt.TryGetInt32(out var format))
                p.Add("'format' must be a number");
            else if (format > CurrentFormat) p.Add($"format {format} was written by a newer FxMapGenerator (this version reads {CurrentFormat})");
            else if (format != CurrentFormat) p.Add($"format {format} (this version reads {CurrentFormat})");
            var groups = new List<EditGroup>();
            if (root.TryGetProperty("groups", out var gs))
            {
                if (gs.ValueKind != JsonValueKind.Object) p.Add("'groups' must be an object (left out for none)");
                else
                    foreach (var g in gs.EnumerateObject())
                        if (ReadGroup(g.Name, g.Value, p) is { } e) groups.Add(e);
            }
            var streets = new List<StreetName>();
            if (root.TryGetProperty("streets", out var ss))
            {
                if (ss.ValueKind != JsonValueKind.Object) p.Add("'streets' must be an object (left out for none)");
                else
                    foreach (var s in ss.EnumerateObject())
                        if (ReadStreetName(s.Name, s.Value, p) is { } e) streets.Add(e);
            }
            var nodes = new List<NodeEdit>();
            var unreadable = new HashSet<string>(StringComparer.Ordinal);
            if (!root.TryGetProperty("nodes", out var ns) || ns.ValueKind != JsonValueKind.Object) p.Add("'nodes' must be an object ({} for none)");
            else
                foreach (var n in ns.EnumerateObject())
                    if (ReadNode(n.Name, n.Value, p) is { } e) nodes.Add(e);
                    else unreadable.Add(n.Name);
            var links = new List<LinkEdit>();
            if (!root.TryGetProperty("links", out var ls) || ls.ValueKind != JsonValueKind.Array) p.Add("'links' must be a list ([] for none)");
            else
            {
                int i = 0;
                foreach (var l in ls.EnumerateArray())
                    if (ReadLink(++i, l, p) is { } e) links.Add(e);
            }
            var set = new RoadEditSet(nodes, links, streets, groups);
            p.AddRange(Problems(set, unreadable));
            if (p.Count > 0) throw new RoadEditsException($"{name}: " + string.Join("; ", p));
            return set;
        }
    }

    static StreetName? ReadStreetName(string key, JsonElement v, List<string> p)
    {
        string where = $"street name {key}";
        if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var hash)) { p.Add($"street name key '{key}' (a hash, a whole number)"); return null; }
        return ReadNames(v, where, p) is var (en, ja) ? new StreetName(hash, en, ja) : null;
    }

    /// <summary>
    /// A group: its name as a text, or an object of <c>name</c> (a text) or <c>en</c> and <c>ja</c> (the bundled groups')
    /// with <c>whereFound</c> (left out: false).
    /// </summary>
    static EditGroup? ReadGroup(string key, JsonElement v, List<string> p)
    {
        if (v.ValueKind == JsonValueKind.String) return new EditGroup(key, ItemName.Of(v.GetString()!));
        string where = $"group {key}";
        if (v.ValueKind != JsonValueKind.Object) { p.Add($"{where}: the name must be a text"); return null; }
        int before = p.Count;
        bool whereFound = ReadBool(v, GroupWhereFound, where, p) ?? false;
        if (!v.TryGetProperty("name", out var n))
            return ReadNames(v, where, p, GroupWhereFound) is var (en, ja) && p.Count == before ? new EditGroup(key, ItemName.Bundled(en, ja), whereFound) : null;
        foreach (var f in v.EnumerateObject())
            if (f.Name is not ("name" or GroupWhereFound)) p.Add($"{where}: unknown field '{f.Name}'");
        if (n.ValueKind != JsonValueKind.String) p.Add($"{where}: 'name' must be a text");
        return p.Count == before ? new EditGroup(key, ItemName.Of(n.GetString()!), whereFound) : null;
    }

    /// <summary>A street name's or group's names: <c>en</c> (needed) and <c>ja</c> (null or left out: none); <paramref name="also"/>: a field the caller reads.</summary>
    static (string En, string? Ja)? ReadNames(JsonElement v, string where, List<string> p, string? also = null)
    {
        if (v.ValueKind != JsonValueKind.Object) { p.Add($"{where} is not an object"); return null; }
        int before = p.Count;
        foreach (var f in v.EnumerateObject())
            if (!StreetFields.Contains(f.Name) && f.Name != also) p.Add($"{where}: unknown field '{f.Name}'");
        string? en = null, ja = null;
        if (!v.TryGetProperty("en", out var e) || e.ValueKind != JsonValueKind.String) p.Add($"{where}: 'en' must be the English name");
        else en = e.GetString();
        if (v.TryGetProperty("ja", out var j) && j.ValueKind != JsonValueKind.Null)
        {
            if (j.ValueKind != JsonValueKind.String) p.Add($"{where}: 'ja' must be the Japanese name or null");
            else ja = j.GetString() is { Length: > 0 } s ? s : null;
        }
        return p.Count == before ? (en!, ja) : null;
    }

    static NodeEdit? ReadNode(string key, JsonElement v, List<string> p)
    {
        string where = $"node {key}";
        if (v.ValueKind != JsonValueKind.Object) { p.Add($"{where} is not an object"); return null; }
        int before = p.Count;
        foreach (var f in v.EnumerateObject())
            if (!NodeFields.Contains(f.Name)) p.Add($"{where}: unknown field '{f.Name}'");
        var set = ReadNodeValues(v, where, p);
        bool hidden = ReadBool(v, "hidden", where, p) ?? false;
        NodeValues? original = null;
        if (v.TryGetProperty("original", out var o))
        {
            if (o.ValueKind != JsonValueKind.Object) p.Add($"{where}: 'original' is not an object");
            else
            {
                foreach (var f in o.EnumerateObject())
                    if (!NodeValueFields.Contains(f.Name)) p.Add($"{where}: unknown field 'original.{f.Name}'");
                original = ReadNodeValues(o, where + " original", p);
            }
        }
        var group = ReadGroupOf(v, where, p);
        return p.Count == before ? new NodeEdit(key, set, hidden, original, group) : null;
    }

    static string? ReadGroupOf(JsonElement v, string where, List<string> p)
    {
        if (!v.TryGetProperty("group", out var e)) return null;
        if (e.ValueKind == JsonValueKind.String) return e.GetString();
        p.Add($"{where}: 'group' must be the id of a group (left out for none)");
        return null;
    }

    static NodeValues ReadNodeValues(JsonElement v, string where, List<string> p) => new(
        ReadNumber(v, "x", where, p), ReadNumber(v, "y", where, p), ReadNumber(v, "z", where, p), ReadStreet(v, where, p),
        ReadBool(v, "highway", where, p), ReadBool(v, "tunnel", where, p), ReadBool(v, "unpaved", where, p), ReadBool(v, "switchedOff", where, p));

    static LinkEdit? ReadLink(int i, JsonElement v, List<string> p)
    {
        string where = $"link {i}";
        if (v.ValueKind != JsonValueKind.Object) { p.Add($"{where} is not an object"); return null; }
        int before = p.Count;
        foreach (var f in v.EnumerateObject())
            if (!LinkFields.Contains(f.Name)) p.Add($"{where}: unknown field '{f.Name}'");
        string? from = ReadString(v, "from", where, p), to = ReadString(v, "to", where, p);
        if (from is not null && to is not null) where = $"link {from} {to}";
        var set = ReadLinkValues(v, where, p);
        double? width = null;
        if (v.TryGetProperty("width", out var w) && w.ValueKind != JsonValueKind.Null)
        {
            if (w.ValueKind != JsonValueKind.Number || !w.TryGetDouble(out var d) || !double.IsFinite(d)) p.Add($"{where}: 'width' must be a number or null");
            else width = d;
        }
        bool hidden = ReadBool(v, "hidden", where, p) ?? false;
        LinkValues? original = null;
        if (v.TryGetProperty("original", out var o))
        {
            if (o.ValueKind != JsonValueKind.Object) p.Add($"{where}: 'original' is not an object");
            else
            {
                foreach (var f in o.EnumerateObject())
                    if (!LinkValueFields.Contains(f.Name)) p.Add($"{where}: unknown field 'original.{f.Name}'");
                original = ReadLinkValues(o, where + " original", p);
            }
        }
        var group = ReadGroupOf(v, where, p);
        if (from is null || to is null) return null;
        return p.Count == before ? new LinkEdit(from, to, set, width, hidden, original, group) : null;
    }

    static LinkValues ReadLinkValues(JsonElement v, string where, List<string> p) =>
        new(ReadInt(v, "lanesForward", where, p), ReadInt(v, "lanesBack", where, p), ReadBool(v, "narrow", where, p));

    static double? ReadNumber(JsonElement v, string field, string where, List<string> p)
    {
        if (!v.TryGetProperty(field, out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d)) return d;
        p.Add($"{where}: '{field}' must be a number");
        return null;
    }

    static int? ReadInt(JsonElement v, string field, string where, List<string> p)
    {
        if (!v.TryGetProperty(field, out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) return n;
        p.Add($"{where}: '{field}' must be a whole number");
        return null;
    }

    static uint? ReadStreet(JsonElement v, string where, List<string> p)
    {
        if (!v.TryGetProperty("street", out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out var h)) return h;
        p.Add($"{where}: 'street' must be a street name hash (0 for none)");
        return null;
    }

    static bool? ReadBool(JsonElement v, string field, string where, List<string> p)
    {
        if (!v.TryGetProperty(field, out var e)) return null;
        if (e.ValueKind is JsonValueKind.True or JsonValueKind.False) return e.GetBoolean();
        p.Add($"{where}: '{field}' must be true or false");
        return null;
    }

    static string? ReadString(JsonElement v, string field, string where, List<string> p)
    {
        if (v.TryGetProperty(field, out var e) && e.ValueKind == JsonValueKind.String) return e.GetString();
        p.Add($"{where}: '{field}' must be a node key");
        return null;
    }

    /// <summary>
    /// What makes edits unusable, in plain sentences (empty = fine): keys, the fields an added node or link must give,
    /// the originals a game node or link must carry, values out of range, links to nodes that are not listed or hidden,
    /// two edits of one link, street names without an English name, groups without a name, either listed twice, groups not listed.
    /// </summary>
    /// <param name="unreadable">Nodes the file lists that could not be read (a link may end at them).</param>
    public static List<string> Problems(RoadEditSet set, IReadOnlySet<string>? unreadable = null)
    {
        var p = new List<string>();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in set.Groups)
        {
            string where = $"group {g.Id}";
            if (!GroupPattern().IsMatch(g.Id)) p.Add($"group id '{g.Id}' (letters, digits, '-' and '_', a letter or digit first)");
            else if (!groups.Add(g.Id)) p.Add($"{where} is listed twice");
            if (g.Name.IsEmpty) p.Add($"{where}: the name is empty");
        }
        void GroupOf(string where, string? group)
        {
            if (group is not null && !groups.Contains(group)) p.Add($"{where}: group '{group}' is not in the groups");
        }
        var hashes = new HashSet<uint>();
        foreach (var s in set.Streets)
        {
            string where = $"street name {s.Hash.ToString(CultureInfo.InvariantCulture)}";
            if (s.Hash == 0) p.Add($"{where}: 0 is no street name");
            else if (!hashes.Add(s.Hash)) p.Add($"{where} is listed twice");
            if (string.IsNullOrWhiteSpace(s.En)) p.Add($"{where}: the English name is empty");
        }
        var nodes = new Dictionary<string, NodeEdit>(StringComparer.Ordinal);
        foreach (var n in set.Nodes)
        {
            string where = $"node {n.Key}";
            if (!KeyPattern().IsMatch(n.Key)) { p.Add($"node key '{n.Key}' (area:node, or added:<number> for an added node)"); continue; }
            if (!nodes.TryAdd(n.Key, n)) { p.Add($"{where} is listed twice"); continue; }
            GroupOf(where, n.Group);
            var s = n.Set;
            if (n.IsAdded)
            {
                var missing = new List<string>();
                if (s.X is null) missing.Add("x");
                if (s.Y is null) missing.Add("y");
                if (s.Z is null) missing.Add("z");
                if (s.Street is null) missing.Add("street");
                if (s.Highway is null) missing.Add("highway");
                if (s.Tunnel is null) missing.Add("tunnel");
                if (s.Unpaved is null) missing.Add("unpaved");
                if (s.SwitchedOff is null) missing.Add("switchedOff");
                if (missing.Count > 0) p.Add($"{where}: an added node needs {string.Join(", ", missing)}");
                if (n.Hidden) p.Add($"{where}: an added node is removed, not hidden");
                if (n.Original is not null) p.Add($"{where}: an added node has no original");
            }
            else
            {
                var o = n.Original;
                if (o is null || o.X is null || o.Y is null || o.Z is null) { p.Add($"{where}: a node of the game needs its original position (original x, y, z)"); continue; }
                var noOriginal = new List<string>();
                if (s.Street is not null && o.Street is null) noOriginal.Add("street");
                if (s.Highway is not null && o.Highway is null) noOriginal.Add("highway");
                if (s.Tunnel is not null && o.Tunnel is null) noOriginal.Add("tunnel");
                if (s.Unpaved is not null && o.Unpaved is null) noOriginal.Add("unpaved");
                if (s.SwitchedOff is not null && o.SwitchedOff is null) noOriginal.Add("switchedOff");
                if (noOriginal.Count > 0) p.Add($"{where}: changes {string.Join(", ", noOriginal)} without the original value");
                if (n.Hidden && (s.HasPosition || s.HasFlags)) p.Add($"{where}: a hidden node changes nothing else");
            }
            var outer = MapFrame.Outer;
            if ((s.X is { } x && (x < outer.X0 || x > outer.X1)) || (s.Y is { } y && (y < outer.Y1 || y > outer.Y0)))
                p.Add($"{where} is off the map");
        }
        var pairs = new HashSet<(string, string)>();
        foreach (var l in set.Links)
        {
            string where = $"link {l.Key}";
            if (l.From == l.To) { p.Add($"{where} joins a node to itself"); continue; }
            GroupOf(where, l.Group);
            bool ends = true;
            foreach (var end in new[] { l.From, l.To })
                if (!nodes.TryGetValue(end, out var n))
                {
                    if (unreadable?.Contains(end) != true) p.Add($"{where}: node {end} is not in the nodes");
                    ends = false;
                }
                else if (n.Hidden) { p.Add($"{where}: node {end} is hidden"); ends = false; }
            if (ends && !pairs.Add(string.CompareOrdinal(l.From, l.To) < 0 ? (l.From, l.To) : (l.To, l.From))) p.Add($"{where} is listed twice (in either direction)");
            var s = l.Set;
            foreach (var (name, lanes) in new[] { ("lanesForward", s.LanesForward), ("lanesBack", s.LanesBack) })
                if (lanes is < 0 or > MaxLanes) p.Add($"{where}: {name} {lanes} (0 to {MaxLanes})");
            if (l.Width is { } w && (w <= 0 || w > MaxWidth)) p.Add($"{where}: width {w.ToString(CultureInfo.InvariantCulture)} m (more than 0, at most {MaxWidth} m)");
            if (l.IsAdded)
            {
                var missing = new List<string>();
                if (s.LanesForward is null) missing.Add("lanesForward");
                if (s.LanesBack is null) missing.Add("lanesBack");
                if (s.Narrow is null) missing.Add("narrow");
                if (missing.Count > 0) p.Add($"{where}: an added link needs {string.Join(", ", missing)}");
                else if (s.LanesForward + s.LanesBack == 0) p.Add($"{where} has no lanes in either direction");
                if (l.Hidden) p.Add($"{where}: an added link is removed, not hidden");
            }
            else
            {
                var o = l.Original!;
                if (o.LanesForward is null || o.LanesBack is null || o.Narrow is null)
                    p.Add($"{where}: a link of the game needs its original lanesForward, lanesBack and narrow");
                else if ((s.LanesForward ?? o.LanesForward) + (s.LanesBack ?? o.LanesBack) == 0) p.Add($"{where} would have no lanes in either direction");
                if (ends && (nodes[l.From].IsAdded || nodes[l.To].IsAdded)) p.Add($"{where}: a link to an added node is added (it has no original)");
                if (l.Hidden && (s.LanesForward is not null || s.LanesBack is not null || s.Narrow is not null || l.Width is not null))
                    p.Add($"{where}: a hidden link changes nothing else");
            }
        }
        return p;
    }

    // ------------------------------------------------------------------------------------------------ writing

    static string Metres(double v) => Geometry.Num.Round(v, 3).ToString(CultureInfo.InvariantCulture);

    static string Bool(bool b) => b ? "true" : "false";

    static void Values(List<string> o, NodeValues v)
    {
        if (v.X is { } x) o.Add($"\"x\": {Metres(x)}");
        if (v.Y is { } y) o.Add($"\"y\": {Metres(y)}");
        if (v.Z is { } z) o.Add($"\"z\": {Metres(z)}");
        if (v.Street is { } s) o.Add($"\"street\": {s.ToString(CultureInfo.InvariantCulture)}");
        if (v.Highway is { } h) o.Add($"\"highway\": {Bool(h)}");
        if (v.Tunnel is { } t) o.Add($"\"tunnel\": {Bool(t)}");
        if (v.Unpaved is { } u) o.Add($"\"unpaved\": {Bool(u)}");
        if (v.SwitchedOff is { } so) o.Add($"\"switchedOff\": {Bool(so)}");
    }

    static void Values(List<string> o, LinkValues v)
    {
        if (v.LanesForward is { } f) o.Add($"\"lanesForward\": {f.ToString(CultureInfo.InvariantCulture)}");
        if (v.LanesBack is { } b) o.Add($"\"lanesBack\": {b.ToString(CultureInfo.InvariantCulture)}");
        if (v.Narrow is { } n) o.Add($"\"narrow\": {Bool(n)}");
    }

    /// <summary>A street name as the file writes it: <c>"hash": {"en": ..., "ja": ...}</c> (no <c>ja</c> without a Japanese name).</summary>
    public static string FormatStreet(StreetName s) => Names(s.Hash.ToString(CultureInfo.InvariantCulture), s.En, s.Ja);

    /// <summary>
    /// A group as the file writes it: <c>"id": "name"</c>, a bundled group's <c>"id": {"en": ..., "ja": ...}</c> (no
    /// <c>ja</c> without a Japanese name); one that applies where found as an object with <c>"whereFound": true</c> last
    /// (<c>{"name": ..., "whereFound": true}</c>, a bundled one's <c>{"en": ..., "ja": ..., "whereFound": true}</c>).
    /// </summary>
    public static string FormatGroup(EditGroup g)
    {
        const string mark = $", \"{GroupWhereFound}\": true";
        if (g.Name.Text is not { } text) return Names(g.Id, g.Name.En ?? "", g.Name.Ja, g.WhereFound ? mark : "");
        string id = JsonSerializer.Serialize(g.Id, Text), name = JsonSerializer.Serialize(text, Text);
        return g.WhereFound ? $"{id}: {{\"name\": {name}{mark}}}" : $"{id}: {name}";
    }

    static string Names(string key, string en, string? ja, string more = "") =>
        $"{JsonSerializer.Serialize(key, Text)}: {{\"en\": {JsonSerializer.Serialize(en, Text)}"
        + (ja is { Length: > 0 } j ? $", \"ja\": {JsonSerializer.Serialize(j, Text)}" : "") + more + "}";

    /// <summary>A node as the file writes it: <c>"key": {...}</c>.</summary>
    public static string FormatNode(NodeEdit n)
    {
        var o = new List<string>();
        Values(o, n.Set);
        if (n.Hidden) o.Add("\"hidden\": true");
        if (n.Original is { } orig)
        {
            var inner = new List<string>();
            Values(inner, orig);
            o.Add($"\"original\": {{{string.Join(", ", inner)}}}");
        }
        if (n.Group is { } g) o.Add($"\"group\": {JsonSerializer.Serialize(g, Text)}");
        return $"{JsonSerializer.Serialize(n.Key)}: {{{string.Join(", ", o)}}}";
    }

    /// <summary>A link as the file writes it (an added link always with its width, null = from its lanes).</summary>
    public static string FormatLink(LinkEdit l)
    {
        var o = new List<string> { $"\"from\": {JsonSerializer.Serialize(l.From)}", $"\"to\": {JsonSerializer.Serialize(l.To)}" };
        Values(o, l.Set);
        if (l.Width is { } w) o.Add($"\"width\": {Metres(w)}");
        else if (l.IsAdded) o.Add("\"width\": null");
        if (l.Hidden) o.Add("\"hidden\": true");
        if (l.Original is { } orig)
        {
            var inner = new List<string>();
            Values(inner, orig);
            o.Add($"\"original\": {{{string.Join(", ", inner)}}}");
        }
        if (l.Group is { } g) o.Add($"\"group\": {JsonSerializer.Serialize(g, Text)}");
        return $"{{{string.Join(", ", o)}}}";
    }

    /// <summary>
    /// The file's text: one group, street name, node or link a line, positions and widths at 0.001 m (<c>groups</c> and
    /// <c>streets</c> only when there are any).
    /// </summary>
    public static string Format(RoadEditSet set)
    {
        var sb = new StringBuilder("{\n  \"format\": ").Append(CurrentFormat).Append(',');
        if (set.Groups.Count > 0)
        {
            sb.Append("\n  \"groups\": {");
            for (int i = 0; i < set.Groups.Count; i++) sb.Append(i == 0 ? "\n    " : ",\n    ").Append(FormatGroup(set.Groups[i]));
            sb.Append("\n  },");
        }
        if (set.Streets.Count > 0)
        {
            sb.Append("\n  \"streets\": {");
            for (int i = 0; i < set.Streets.Count; i++) sb.Append(i == 0 ? "\n    " : ",\n    ").Append(FormatStreet(set.Streets[i]));
            sb.Append("\n  },");
        }
        sb.Append("\n  \"nodes\": {");
        for (int i = 0; i < set.Nodes.Count; i++) sb.Append(i == 0 ? "\n    " : ",\n    ").Append(FormatNode(set.Nodes[i]));
        sb.Append(set.Nodes.Count == 0 ? "},\n  \"links\": [" : "\n  },\n  \"links\": [");
        for (int i = 0; i < set.Links.Count; i++) sb.Append(i == 0 ? "\n    " : ",\n    ").Append(FormatLink(set.Links[i]));
        sb.Append(set.Links.Count == 0 ? "]\n}\n" : "\n  ]\n}\n");
        return sb.ToString();
    }

    /// <exception cref="RoadEditsException">The edits are not usable (nothing is written).</exception>
    public static void Write(string path, RoadEditSet set)
    {
        var problems = Problems(set);
        if (problems.Count > 0) throw new RoadEditsException(string.Join("; ", problems));
        var bytes = new UTF8Encoding(false).GetBytes(Format(set));
        GameFilesOutput.WriteAtomically(path, fs => fs.Write(bytes));
    }

    // ------------------------------------------------------------------------------------------------ the bundled edits

    /// <summary>The bundled road edits (repository <c>data/road-edits-default.json</c>), as <see cref="EmbeddedData.Open"/> names them.</summary>
    public const string BundledName = "road-edits-default.json";

    static readonly Lazy<byte[]> BundledFile = new(() =>
    {
        using var s = EmbeddedData.Open(BundledName);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    });

    static readonly Lazy<RoadEditSet> BundledSet = new(() => Parse(BundledFile.Value, "the bundled road edits"));

    /// <summary>
    /// The bytes of the bundled road edits: the edits the app carries for every server (the airfields' runways, hiking
    /// trails), each group of them a group of the file. A new project starts from a copy; a project takes groups in
    /// on the road editor.
    /// </summary>
    public static byte[] BundledBytes => BundledFile.Value;

    /// <summary>The bundled road edits read.</summary>
    public static RoadEditSet Bundled => BundledSet.Value;

    /// <summary>
    /// The name of a new road edits file in a project's folder: <c>road-edits.json</c>, else <c>road-edits-2.json</c>, ...
    /// (the first free one).
    /// </summary>
    public static string FreeName(string folder)
    {
        var name = FileName;
        for (int n = 2; File.Exists(Path.Combine(folder, name)); n++) name = $"road-edits-{n}.json";
        return name;
    }

    /// <summary>
    /// A new project's road edits: a copy of the bundled edits (<see cref="Bundled"/>, their groups named in
    /// <paramref name="language"/>, the screens' language) written beside the project file under <see cref="FreeName"/>,
    /// which the project's <c>roadEdits</c> then names (the project is not saved here). Written whatever maps the project
    /// makes (an atlas or road map may come later).
    /// </summary>
    public static void StartFromBundled(Project project, string language)
    {
        Directory.CreateDirectory(project.Folder);
        var name = FreeName(project.Folder);
        Write(Path.Combine(project.Folder, name), Copied(Bundled, language));
        project.File.RoadEdits = name;
    }

    /// <summary>The edits as a copy of them keeps them: the groups' names in English and Japanese (the bundled ones') become their name in <paramref name="language"/>.</summary>
    public static RoadEditSet Copied(RoadEditSet set, string language) =>
        new(set.Nodes, set.Links, set.Streets, set.Groups.Select(g => g with { Name = g.Name.Copied(language) }).ToList());

    // ------------------------------------------------------------------------------------------------ the project's edits

    /// <summary>
    /// The digest of the project's road edits file (<see cref="ContentDigest"/>): null without edits, <c>missing</c> when
    /// the file named is not there. The road graph and the road shapes record the digest of the edits they applied.
    /// </summary>
    public static string? DigestOf(Project project) =>
        project.RoadEditsPath is { } path ? ContentDigest.OfFile(path) ?? "missing" : null;

    /// <summary>
    /// The path file of the work folder with the project's road edits applied, and what they did (null without edits).
    /// </summary>
    /// <exception cref="RoadEditsException">The project's edits file is missing or not usable.</exception>
    public static (PathFile Paths, RoadEditsSummary? Edits) PathsOf(Project project, WorkFolder folder)
    {
        var paths = PathFile.Read(Path.Combine(folder.Game, GameFilesOutput.Paths));
        if (project.RoadEditsPath is not { } file) return (paths, null);
        if (!File.Exists(file)) throw new RoadEditsException($"road edits not found: {file}");
        var bytes = File.ReadAllBytes(file);
        var set = Parse(bytes, file);
        var a = set.ApplyTo(paths);
        return (paths, new RoadEditsSummary(project.File.RoadEdits!, ContentDigest.Of(bytes), a.NodesAdded, a.NodesMoved, a.NodesChanged, a.NodesHidden,
            a.LinksAdded, a.LinksChanged, a.LinksHidden, a.NotApplied, set.BlockDigests()));
    }

    /// <summary>The street names the project's road edits add (none without edits).</summary>
    /// <exception cref="RoadEditsException">The project's edits file is missing or not usable.</exception>
    public static IReadOnlyList<StreetName> StreetsOf(Project project) => project.RoadEditsPath is { } file ? Read(file).Streets : [];

    /// <summary>The digest of street names as the file writes them (null for none), as the labels record them.</summary>
    public static string? StreetsDigest(IReadOnlyList<StreetName> streets) =>
        streets.Count == 0 ? null : ContentDigest.Of(string.Join('\n', streets.Select(FormatStreet)));

    /// <summary>
    /// The digest of the street names the project's road edits add (<see cref="StreetsDigest"/>): null when they add none,
    /// <c>unreadable</c> when the file cannot be read.
    /// </summary>
    public static string? StreetsDigestOf(Project project)
    {
        if (project.RoadEditsPath is not { } file) return null;
        try { return StreetsDigest(Read(file).Streets); }
        catch (Exception ex) when (ex is RoadEditsException or IOException) { return "unreadable"; }
    }

    /// <summary>The per-block digests of the project's road edits (<see cref="RoadEditSet.BlockDigests"/>); empty without edits or when they cannot be read.</summary>
    public static IReadOnlyDictionary<string, string> BlockDigestsOf(Project project)
    {
        if (project.RoadEditsPath is not { } file || !File.Exists(file)) return new Dictionary<string, string>();
        try { return Read(file).BlockDigests(); }
        catch (Exception ex) when (ex is RoadEditsException or IOException) { return new Dictionary<string, string>(); }
    }
}

/// <summary>
/// What the road edits did, as the road graph and road shapes records keep it: the project's <c>roadEdits</c>, the
/// digest of the file applied, the edits applied per kind, those not applied with the reason, and the per-block digests
/// (<see cref="RoadEditSet.BlockDigests"/>).
/// </summary>
public sealed record RoadEditsSummary(string File, string Digest, int NodesAdded, int NodesMoved, int NodesChanged, int NodesHidden,
    int LinksAdded, int LinksChanged, int LinksHidden, IReadOnlyList<NotAppliedEdit> NotApplied, IReadOnlyDictionary<string, string> Blocks)
{
    /// <summary>One line for the run's log.</summary>
    public string Describe() =>
        $"road edits {File}: nodes {NodesAdded} added, {NodesMoved} moved, {NodesChanged} changed, {NodesHidden} hidden;"
        + $" links {LinksAdded} added, {LinksChanged} changed, {LinksHidden} hidden; {NotApplied.Count} not applied"
        + (NotApplied.Count == 0 ? "" : " (" + string.Join(", ", NotApplied.GroupBy(n => n.Reason).Select(g => $"{g.Key} {g.Count()}")) + ")");
}

public sealed class RoadEditsException(string message) : Exception(message);
