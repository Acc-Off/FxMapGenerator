using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using FxMapGenerator.GameData.Paths;
using FxMapGenerator.GameData.Text;

namespace FxMapGenerator.Core.GameFiles;

/// <summary>
/// The files of the game-files stage in the work folder's <c>game/</c> (read from the user's game, never distributed;
/// <c>Docs/spec/ynd-format.ja.md</c>, <c>gxt2-format.ja.md</c>):
/// <list type="bullet">
/// <item><c>paths.json</c>: the vehicle path nodes (key <c>area:node</c>, position, flags, the square of their junction
///   record) and the directed links between them</item>
/// <item><c>names.json</c>: the names of the streets (of the path nodes and of the scans) and of the zones the scans met, per language</item>
/// </list>
/// </summary>
public static class GameFilesOutput
{
    public const string Paths = "paths.json", Names = "names.json", Record = "sources.json";

    static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The vehicle nodes of every area read and the directed links between them (every connection appears once from
    /// each end, a one-way road too: the lanes are counted from the end that states it). Pedestrian nodes, and links to them or to nodes of areas not read, are left out. A node with a
    /// junction record carries the record's square (<c>junctionArea</c>: min x, min y, max x, max y; of two records
    /// for one node the later). Returns the node, link and junction-square counts.
    /// </summary>
    public static (int Nodes, int Links, int Junctions) WritePaths(string path, GameFileReader.Result r)
    {
        var keep = new HashSet<(int, int)>();
        foreach (var a in r.Areas)
            if (r.Files.TryGetValue(a, out var y))
                foreach (var n in y.Nodes)
                    if (!YndFile.IsPed(n)) keep.Add((n.Area, n.Id));
        var squares = new Dictionary<(int, int), double[]>();
        foreach (var a in r.Areas)
            if (r.Files.TryGetValue(a, out var y))
                foreach (var j in y.Junctions)
                    if (keep.Contains((j.RefArea, j.RefNode)))
                        squares[(j.RefArea, j.RefNode)] = [j.X0, j.Y0, j.X0 + (j.Nx - 1) * JunctionStep, j.Y0 + (j.Ny - 1) * JunctionStep];
        int nLinks = 0;
        WriteAtomically(path, fs =>
        {
            using var w = new Utf8JsonWriter(fs, Compact);
            w.WriteStartObject();
            w.WriteStartArray("areas");
            foreach (var a in r.Areas) w.WriteNumberValue(a);
            w.WriteEndArray();
            w.WriteStartObject("nodes");
            foreach (var a in r.Areas)
            {
                if (!r.Files.TryGetValue(a, out var y)) continue;
                foreach (var n in y.Nodes)
                {
                    if (YndFile.IsPed(n)) continue;
                    w.WriteStartObject($"{n.Area}:{n.Id}");
                    w.WriteNumber("x", Math.Round(n.X, 3));
                    w.WriteNumber("y", Math.Round(n.Y, 3));
                    w.WriteNumber("z", Math.Round(n.Z, 3));
                    w.WriteNumber("street", n.Street);
                    w.WriteBoolean("junction", ((n.F2 >> 2) & 1) != 0);
                    w.WriteBoolean("highway", ((n.F2 >> 6) & 1) != 0);
                    w.WriteBoolean("tunnel", (n.F3 & 1) != 0);
                    w.WriteBoolean("unpaved", ((n.F0 >> 3) & 1) != 0);
                    w.WriteBoolean("switchedOff", ((n.F2 >> 7) & 1) != 0);
                    w.WriteBoolean("noBigVehicles", ((n.F0 >> 5) & 1) != 0);
                    w.WriteBoolean("cannotGoLeft", ((n.F0 >> 7) & 1) != 0);
                    w.WriteBoolean("slipLane", (n.F1 & 1) != 0);
                    w.WriteBoolean("indicateKeepLeft", ((n.F1 >> 1) & 1) != 0);
                    w.WriteBoolean("indicateKeepRight", ((n.F1 >> 2) & 1) != 0);
                    w.WriteBoolean("noGps", (n.F2 & 1) != 0);
                    w.WriteString("special", YndFile.SpecialName(n));
                    w.WriteNumber("trafficDensity", n.F4 & 15);
                    w.WriteString("speed", YndFile.SpeedNames[(n.B25 >> 1) & 3]);
                    w.WriteNumber("deadEnd", (n.F4 >> 4) & 7);
                    w.WriteNumber("heuristic", n.F3 >> 1);
                    if (squares.TryGetValue((n.Area, n.Id), out var sq))
                    {
                        w.WriteStartArray("junctionArea");
                        foreach (var v in sq) w.WriteNumberValue(v);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
            }
            w.WriteEndObject();
            w.WriteStartArray("links");
            foreach (var a in r.Areas)
            {
                if (!r.Files.TryGetValue(a, out var y)) continue;
                foreach (var n in y.Nodes)
                    for (int k = 0; k < n.LinkCount; k++)
                    {
                        if (n.LinkFirst + k >= y.Links.Count) throw new InvalidDataException($"nodes{a}.ynd: node {n.Id} links past the end of the link array");
                        var l = y.Links[n.LinkFirst + k];
                        if (YndFile.IsPed(n) || !keep.Contains((l.Area, l.Node))) continue;
                        int off = (l.F1 >> 4) & 7;
                        w.WriteStartObject();
                        w.WriteString("from", $"{n.Area}:{n.Id}");
                        w.WriteString("to", $"{l.Area}:{l.Node}");
                        w.WriteNumber("lanesForward", (l.F2 >> 5) & 7);
                        w.WriteNumber("lanesBack", (l.F2 >> 2) & 7);
                        w.WriteBoolean("narrow", ((l.F1 >> 1) & 1) != 0);
                        w.WriteBoolean("dontUseForNavigation", (l.F2 & 1) != 0);
                        w.WriteBoolean("shortcut", ((l.F2 >> 1) & 1) != 0);
                        w.WriteNumber("laneOffset", Math.Round(off / 7.0 * ((l.F1 >> 7) != 0 ? -0.5 : 0.5), 3));
                        w.WriteBoolean("gpsBothWays", (l.F0 & 1) != 0);
                        w.WriteNumber("length", l.Length);
                        w.WriteEndObject();
                        nLinks++;
                    }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        return (keep.Count, nLinks, squares.Count);
    }

    /// <summary>Spacing of a junction record's height samples (m).</summary>
    public const double JunctionStep = 2.0;

    /// <summary>
    /// The name file: the texts of the street hashes and zone codes in every language read (a key missing from a
    /// language is left out of that entry). Zone codes are looked up as joaat(code), then joaat(lower-case code).
    /// Returns the street and zone entries without any text.
    /// </summary>
    public static (int StreetsMissing, int ZonesMissing) WriteNames(string path, IReadOnlyDictionary<string, IReadOnlyDictionary<uint, string>> tables,
        IEnumerable<uint> streets, IEnumerable<string> zones)
    {
        int missS = 0, missZ = 0;
        var keys = tables.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        WriteAtomically(path, fs =>
        {
            using var w = new Utf8JsonWriter(fs, Indented);
            w.WriteStartObject();
            w.WriteStartObject("streets");
            foreach (var h in streets.Distinct().Order())
            {
                w.WriteStartObject(h.ToString(CultureInfo.InvariantCulture));
                int found = 0;
                foreach (var k in keys)
                    if (tables[k].TryGetValue(h, out var t)) { w.WriteString(k, t); found++; }
                if (found == 0) missS++;
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartObject("zones");
            foreach (var z in zones.Distinct().Order(StringComparer.Ordinal))
            {
                w.WriteStartObject(z);
                int found = 0;
                foreach (var k in keys)
                    foreach (var h in new[] { Gxt2File.Joaat(z), Gxt2File.Joaat(z.ToLowerInvariant()) })
                        if (tables[k].TryGetValue(h, out var t)) { w.WriteString(k, t); found++; break; }
                if (found == 0) missZ++;
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        });
        return (missS, missZ);
    }

    /// <summary>Writes through a temporary file next to the target, so a stopped run never leaves half a file.</summary>
    public static void WriteAtomically(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) write(fs);
        File.Move(tmp, path, overwrite: true);
    }
}
