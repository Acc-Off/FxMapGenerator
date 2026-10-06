using System.Globalization;

namespace FxMapGenerator.Core.Scan;

/// <summary>
/// One block's scan (<c>scan/&lt;block&gt;.txt</c>): the <c>MSCAN</c> lines of format v=1 (the prefix before <c>MSCAN</c>,
/// such as <c>[fxmapgen]</c>, does not matter). Grids are row-major with row 0 = the north edge (y0) and column 0 = the
/// west edge (x0), like the height grid.
/// <list type="bullet">
/// <item>ground pass (<c>kind=mat</c>), n x n every step m: material hash (0 = no hit), hit height, water height, the
///   water-collision probe (flags 128, <c>fol</c> rows) and the canopy probe (flags 256, <c>fol2</c> rows; only when the
///   header has <c>pflags2</c>); heights NaN where nothing was hit</item>
/// <item>road pass (<c>kind=road</c>): street hash and zone index every rstep m (rn x rn), on-road flag every pstep m (pn x pn)</item>
/// </list>
/// Rows are run-length coded (<c>v*count</c>) and may come in several parts (<c>k</c>), joined in order; a row that
/// never arrived stays at its fill value and is counted in <see cref="Missing"/>, a row without exactly the grid's points
/// in <see cref="Uneven"/>. Road-edge lines are skipped.
/// </summary>
public sealed class ScanFile
{
    public int Z { get; private set; }
    public int Tx { get; private set; }
    public int Ty { get; private set; }
    public double X0 { get; private set; }
    public double Y0 { get; private set; }
    public double Size { get; private set; }

    // ground pass
    public bool HasGround => N > 0;
    public int N { get; private set; }
    public double Step { get; private set; }
    public int Flags { get; private set; } = 1;
    /// <summary>The water-collision probe was taken (<c>fol=1</c>).</summary>
    public int Fol { get; private set; }
    public int Pflags { get; private set; } = 128;
    /// <summary>The flag of the third probe (256 = canopy), 0 when not taken.</summary>
    public int Pflags2 { get; private set; }
    public int Chunks { get; private set; } = 1;
    public uint[] Material { get; private set; } = [];
    public float[] HitZ { get; private set; } = [];
    public float[] Water { get; private set; } = [];
    public float[] Probe { get; private set; } = [];
    /// <summary>The canopy probe, or null when the scan has none.</summary>
    public float[]? Canopy { get; private set; }

    // road pass
    public bool HasRoads => RoadN > 0;
    public int RoadN { get; private set; }
    public double RoadStep { get; private set; }
    public int OnRoadN { get; private set; }
    public double OnRoadStep { get; private set; }
    public uint[] Street { get; private set; } = [];
    /// <summary>Zone index per cell: 1 = <see cref="Zones"/>[0], 0 = none.</summary>
    public short[] Zone { get; private set; } = [];
    public byte[] OnRoad { get; private set; } = [];
    public Dictionary<uint, string> StreetNames { get; } = new();
    /// <summary>Zones in index order: the zone code (<c>GET_NAME_OF_ZONE</c>) and the name the game showed.</summary>
    public List<(string Code, string Label)> Zones { get; } = new();

    /// <summary>END lines by kind (their key=value pairs), and the DONE line.</summary>
    public Dictionary<string, Dictionary<string, string>> End { get; } = new();
    public Dictionary<string, string>? Done { get; private set; }
    /// <summary>Rows that never arrived, per row kind.</summary>
    public Dictionary<string, int> Missing { get; } = new();
    /// <summary>Rows whose parts do not give exactly the grid's points, per row kind (a part cut off on its way).</summary>
    public Dictionary<string, int> Uneven { get; } = new();

    public static ScanFile Read(string path) => Parse(File.ReadLines(path));

    /// <summary>
    /// What is wrong with the lines of a scan as they came from the game, or null: a row token that is no value (the game
    /// cut a line in the middle of one), rows that never came, rows without exactly the grid's points (the game cut a line
    /// between two tokens).
    /// </summary>
    public static string? Problem(IEnumerable<string> lines)
    {
        ScanFile s;
        try { s = Parse(lines); }
        catch (Exception e) when (e is FormatException or OverflowException or KeyNotFoundException) { return "a line could not be read (" + e.Message + ")"; }
        var bad = s.Missing.Where(m => m.Value > 0).Select(m => $"{m.Value} {m.Key} rows missing")
            .Concat(s.Uneven.Where(u => u.Value > 0).Select(u => $"{u.Value} {u.Key} rows without all their points")).ToList();
        return bad.Count == 0 ? null : string.Join(", ", bad);
    }

    public static ScanFile Parse(IEnumerable<string> lines)
    {
        var s = new ScanFile();
        var rows = new Dictionary<string, Dictionary<int, SortedDictionary<int, string>>>(StringComparer.Ordinal);
        var matDict = new Dictionary<int, uint>();
        var streetIdx = new Dictionary<int, uint>();
        foreach (var raw in lines)
        {
            int at = raw.IndexOf("MSCAN ", StringComparison.Ordinal);
            if (at < 0 || (at > 0 && raw[at - 1] != ' ')) continue;
            var rest = raw.AsSpan(at + 6).TrimEnd();
            int sp = rest.IndexOf(' ');
            var tag = (sp < 0 ? rest : rest[..sp]).ToString();
            var args = sp < 0 ? ReadOnlySpan<char>.Empty : rest[(sp + 1)..];
            if (args.StartsWith("j="))
            {
                // <kind> j=<row> k=<part> <tokens>
                var a = args.ToString();
                int s1 = a.IndexOf(' ');
                var jTok = s1 < 0 ? a : a[..s1];
                var after = s1 < 0 ? "" : a[(s1 + 1)..];
                if (!after.StartsWith("k=", StringComparison.Ordinal)) continue;
                int s2 = after.IndexOf(' ');
                var kTok = s2 < 0 ? after : after[..s2];
                var tokens = s2 < 0 ? "" : after[(s2 + 1)..];
                if (!int.TryParse(jTok.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var j)
                    || !int.TryParse(kTok.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var k)) continue;
                if (!rows.TryGetValue(tag, out var byRow)) rows[tag] = byRow = new();
                if (!byRow.TryGetValue(j, out var parts)) byRow[j] = parts = new();
                parts[k] = tokens;
                continue;
            }
            var p = args.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (tag)
            {
                case "BEGIN":
                {
                    var d = KeyValues(p);
                    if (d.GetValueOrDefault("kind") == "mat")
                    {
                        s.X0 = D(d["x0"]); s.Y0 = D(d["y0"]); s.Size = D(d["size"]); s.Step = D(d["step"]);
                        s.N = I(d["n"]);
                        s.Flags = d.TryGetValue("flags", out var f) ? I(f) : 1;
                        s.Fol = d.TryGetValue("fol", out var fo) ? I(fo) : 0;
                        s.Pflags = d.TryGetValue("pflags", out var pf) ? I(pf) : 128;
                        s.Pflags2 = d.TryGetValue("pflags2", out var pf2) ? I(pf2) : 0;
                        s.Chunks = d.TryGetValue("chunks", out var ch) ? I(ch) : 1;
                        s.Z = I(d["z"]); s.Tx = I(d["tx"]); s.Ty = I(d["ty"]);
                    }
                    else if (d.GetValueOrDefault("kind") == "road")
                    {
                        if (!s.HasGround) { s.X0 = D(d["x0"]); s.Y0 = D(d["y0"]); s.Size = D(d["size"]); s.Z = I(d["z"]); s.Tx = I(d["tx"]); s.Ty = I(d["ty"]); }
                        s.RoadStep = D(d["step"]); s.RoadN = I(d["n"]);
                        s.OnRoadStep = D(d["pstep"]); s.OnRoadN = I(d["pn"]);
                    }
                    break;
                }
                case "dict" when p.Length >= 3:
                    if (p[0] == "mat") matDict[I(p[1])] = U32(p[2]);
                    else if (p[0] == "street")
                    {
                        var h = U32(p[2]);
                        streetIdx[I(p[1])] = h;
                        s.StreetNames[h] = string.Join(' ', p.Skip(3));
                    }
                    else if (p[0] == "zone") s.Zones.Add((p[2], string.Join(' ', p.Skip(3))));
                    break;
                case "END":
                {
                    var d = KeyValues(p);
                    s.End[d.GetValueOrDefault("kind") ?? "?"] = d;
                    break;
                }
                case "DONE":
                    s.Done = KeyValues(p);
                    break;
            }
        }

        if (s.HasGround)
        {
            int n = s.N;
            var mi = Grid(rows, "mat", n, 0, t => I(t), s);
            s.Material = new uint[n * n];
            for (int i = 0; i < mi.Length; i++) s.Material[i] = matDict.GetValueOrDefault(mi[i]);
            s.HitZ = Grid(rows, "hz", n, float.NaN, F, s);
            s.Water = Grid(rows, "water", n, float.NaN, F, s);
            s.Probe = s.Fol != 0 ? Grid(rows, "fol", n, float.NaN, F, s) : Filled(n * n, float.NaN);
            if (s.Pflags2 != 0) s.Canopy = Grid(rows, "fol2", n, float.NaN, F, s);
        }
        if (s.HasRoads)
        {
            int rn = s.RoadN;
            var si = Grid(rows, "street", rn, 0, t => I(t), s);
            s.Street = new uint[rn * rn];
            for (int i = 0; i < si.Length; i++) s.Street[i] = streetIdx.GetValueOrDefault(si[i]);
            s.Zone = Grid(rows, "zone", rn, (short)0, t => (short)I(t), s);
            s.OnRoad = Grid(rows, "onroad", s.OnRoadN, (byte)0, t => (byte)I(t), s);
        }
        return s;
    }

    static T[] Filled<T>(int count, T value)
    {
        var a = new T[count];
        Array.Fill(a, value);
        return a;
    }

    /// <summary>
    /// An n x n grid of one row kind: the parts of each row in order, run-length expanded, cut at n; the rows that never
    /// came and those without exactly n points are counted in the scan's <see cref="Missing"/> and <see cref="Uneven"/>.
    /// </summary>
    static T[] Grid<T>(Dictionary<string, Dictionary<int, SortedDictionary<int, string>>> rows, string kind, int n, T fill, Func<string, T> parse, ScanFile s)
    {
        var a = Filled(n * n, fill);
        int miss = 0, uneven = 0;
        rows.TryGetValue(kind, out var byRow);
        for (int j = 0; j < n; j++)
        {
            if (byRow is null || !byRow.TryGetValue(j, out var parts)) { miss++; continue; }
            int c = 0;
            long points = 0;
            foreach (var tokens in parts.Values)
                foreach (var tok in tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int star = tok.LastIndexOf('*');
                    T v;
                    int count = 1;
                    if (star >= 0)
                    {
                        v = parse(tok[..star]);
                        count = I(tok[(star + 1)..]);
                    }
                    else v = parse(tok);
                    points += count;
                    for (int r = 0; r < count && c < n; r++) a[j * n + c++] = v;
                }
            if (points != n) uneven++;
        }
        s.Missing[kind] = miss;
        s.Uneven[kind] = uneven;
        return a;
    }

    static Dictionary<string, string> KeyValues(IEnumerable<string> tokens)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tokens)
        {
            int eq = t.IndexOf('=');
            if (eq > 0) d[t[..eq]] = t[(eq + 1)..];
        }
        return d;
    }

    static int I(string s) => int.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    /// <summary>A height: 'x' or '.' = none (NaN); else the number rounded to single precision as the value is kept.</summary>
    static float F(string s) => s is "x" or "." ? float.NaN : (float)double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    /// <summary>A hash written as a signed or unsigned 32-bit number.</summary>
    static uint U32(string s) => unchecked((uint)long.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
}
