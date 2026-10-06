namespace FxMapGenerator.Core.Scan;

/// <summary>The two scans of a block: the ground (materials, heights, water, canopy) and the roads (on-road, streets, zones).</summary>
public enum ScanKind { Ground, Roads }

/// <summary>
/// The lines of a block's scan file (<c>scan/&lt;block&gt;.txt</c>): the MSCAN lines of its ground scan, then those of its
/// road scan. A visit that takes only one of them again keeps the other's lines from the file.
/// </summary>
public static class ScanLines
{
    static readonly HashSet<string> GroundRows = new(StringComparer.Ordinal) { "mat", "hz", "water", "fol", "fol2", "chunk" };
    static readonly HashSet<string> RoadRows = new(StringComparer.Ordinal) { "street", "zone", "onroad", "edge" };

    /// <summary>The scan a line belongs to; null for other lines (errors, notes). The line may carry a prefix before MSCAN.</summary>
    public static ScanKind? KindOf(string line)
    {
        int at = line.IndexOf("MSCAN ", StringComparison.Ordinal);
        if (at < 0 || (at > 0 && line[at - 1] != ' ')) return null;
        var p = line[(at + 6)..].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return null;
        string Value(string key)
        {
            foreach (var tok in line[(at + 6)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (tok.StartsWith(key + "=", StringComparison.Ordinal)) return tok[(key.Length + 1)..];
            return "";
        }
        switch (p[0])
        {
            case "BEGIN" or "END":
                return Value("kind") switch { "mat" => ScanKind.Ground, "road" => ScanKind.Roads, _ => null };
            case "DONE":
                // a scan of both kinds at once ("all", in imported scans) ends with one DONE line: it goes with the roads, at the end
                return Value("kind") == "ground" ? ScanKind.Ground : ScanKind.Roads;
            case "dict" when p.Length > 1:
                return p[1] == "mat" ? ScanKind.Ground : p[1] is "street" or "zone" ? ScanKind.Roads : null;
        }
        if (GroundRows.Contains(p[0])) return ScanKind.Ground;
        if (RoadRows.Contains(p[0])) return ScanKind.Roads;
        return null;
    }

    /// <summary>
    /// The file's new lines: the new ground lines or else the ground lines of <paramref name="existing"/>, then the new road
    /// lines or else the road lines there.
    /// </summary>
    public static List<string> Merge(IEnumerable<string>? existing, IReadOnlyList<string>? ground, IReadOnlyList<string>? roads)
    {
        var old = existing?.ToList() ?? [];
        var o = new List<string>();
        o.AddRange(ground ?? (IReadOnlyList<string>)old.Where(l => KindOf(l) == ScanKind.Ground).ToList());
        o.AddRange(roads ?? (IReadOnlyList<string>)old.Where(l => KindOf(l) == ScanKind.Roads).ToList());
        return o;
    }
}
