using System.Text.Json;

namespace FxMapGenerator.Core.GameFiles;

/// <summary>
/// <c>game/paths.json</c> as the road graph and the road shapes read it: the vehicle nodes and the directed links in the
/// file's order, with the fields they use. The road edits are applied to it (<see cref="RoadEdits.RoadEditsFile.ApplyTo"/>)
/// before either builds its graph, so both read the same edited paths.
/// </summary>
public sealed class PathFile
{
    public sealed class Node
    {
        public required string Key { get; init; }
        public double X, Y, Z;
        public uint Street;
        public bool Junction, Highway, Tunnel, Unpaved, SwitchedOff;
        /// <summary>The square of the node's junction record (min x, min y, max x, max y), or null.</summary>
        public double[]? JunctionArea;
        /// <summary>A node the road edits add (not in the game's path data).</summary>
        public bool Added;
    }

    /// <summary>One direction of a connection (every connection appears once from each end, a one-way road too).</summary>
    public sealed class Link
    {
        public required string From { get; init; }
        public required string To { get; init; }
        public int LanesForward, LanesBack;
        public bool Narrow, DontUseForNavigation, Shortcut;
        public double LaneOffset;
        /// <summary>A width (m) the road edits give the connection; null = none.</summary>
        public double? Width;
        /// <summary>A connection the road edits add (not in the game's path data).</summary>
        public bool Added;
    }

    public List<Node> Nodes { get; } = new();
    public List<Link> Links { get; } = new();
    /// <summary>The street names the road edits add (hash -> English, Japanese or null); the game's are in <c>game/names.json</c>.</summary>
    public Dictionary<uint, (string En, string? Ja)> Streets { get; } = new();

    /// <summary>A copy to apply edits to (the nodes and links are copied; a junction square is shared, it is not changed).</summary>
    public PathFile Clone()
    {
        var o = new PathFile();
        foreach (var (h, s) in Streets) o.Streets[h] = s;
        foreach (var n in Nodes)
            o.Nodes.Add(new Node
            {
                Key = n.Key, X = n.X, Y = n.Y, Z = n.Z, Street = n.Street, Junction = n.Junction, Highway = n.Highway, Tunnel = n.Tunnel, Unpaved = n.Unpaved,
                SwitchedOff = n.SwitchedOff, JunctionArea = n.JunctionArea, Added = n.Added,
            });
        foreach (var l in Links)
            o.Links.Add(new Link
            {
                From = l.From, To = l.To, LanesForward = l.LanesForward, LanesBack = l.LanesBack, Narrow = l.Narrow, DontUseForNavigation = l.DontUseForNavigation,
                Shortcut = l.Shortcut, LaneOffset = l.LaneOffset, Width = l.Width, Added = l.Added,
            });
        return o;
    }

    public static PathFile Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions { MaxDepth = 16 });
        return Of(doc);
    }

    /// <summary>The path file from its bytes.</summary>
    public static PathFile Parse(ReadOnlyMemory<byte> json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        return Of(doc);
    }

    static PathFile Of(JsonDocument doc)
    {
        var o = new PathFile();
        foreach (var p in doc.RootElement.GetProperty("nodes").EnumerateObject())
        {
            var v = p.Value;
            o.Nodes.Add(new Node
            {
                Key = p.Name, X = v.GetProperty("x").GetDouble(), Y = v.GetProperty("y").GetDouble(), Z = v.GetProperty("z").GetDouble(),
                Street = v.GetProperty("street").GetUInt32(), Junction = v.GetProperty("junction").GetBoolean(),
                Highway = v.GetProperty("highway").GetBoolean(), Tunnel = v.GetProperty("tunnel").GetBoolean(),
                Unpaved = v.GetProperty("unpaved").GetBoolean(), SwitchedOff = v.GetProperty("switchedOff").GetBoolean(),
                JunctionArea = v.TryGetProperty("junctionArea", out var ja) ? ja.EnumerateArray().Select(e => e.GetDouble()).ToArray() : null,
            });
        }
        foreach (var l in doc.RootElement.GetProperty("links").EnumerateArray())
            o.Links.Add(new Link
            {
                From = l.GetProperty("from").GetString()!, To = l.GetProperty("to").GetString()!,
                LanesForward = l.GetProperty("lanesForward").GetInt32(), LanesBack = l.GetProperty("lanesBack").GetInt32(),
                Narrow = l.GetProperty("narrow").GetBoolean(), DontUseForNavigation = l.GetProperty("dontUseForNavigation").GetBoolean(),
                Shortcut = l.GetProperty("shortcut").GetBoolean(), LaneOffset = l.GetProperty("laneOffset").GetDouble(),
            });
        return o;
    }
}
