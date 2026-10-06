using System.Globalization;
using System.Text.RegularExpressions;
using FxMapGenerator.Core.Grids;

namespace FxMapGenerator.Core.Satellite;

/// <summary>
/// The READY line saved next to a shot (<c>&lt;block&gt;.cam.txt</c>): camera straight down over (x, y), at gz + h,
/// vertical field of view in degrees, frame margin around the block.
/// </summary>
public sealed partial record CameraLine(double X, double Y, double GroundZ, double Height, double Fov, double Margin)
{
    public static CameraLine Parse(string text)
    {
        var kv = KeyValues(text);
        double Get(string k) => kv.TryGetValue(k, out var v) ? v : throw new InvalidDataException($"camera line without {k}: {text.Trim()}");
        return new CameraLine(Get("x"), Get("y"), Get("gz"), Get("h"), Get("fov"), kv.GetValueOrDefault("margin", 1.0));
    }

    public static CameraLine Read(string path) => Parse(File.ReadAllText(path));

    internal static Dictionary<string, double> KeyValues(string line)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Match m in KeyValue().Matches(line))
            if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) d[m.Groups[1].Value] = v;
        return d;
    }

    [GeneratedRegex(@"(\w+)=([-\d.]+)")]
    private static partial Regex KeyValue();
}

/// <summary>
/// The height grid of a block (<c>&lt;block&gt;.hmap</c>): n x n surface heights every <c>step</c> metres from the
/// block's north-west corner (x0, y0); row j runs south, column i east. Missing hits are NaN until <see cref="FillMissing"/>.
/// </summary>
public sealed class HeightGrid
{
    public required int N { get; init; }
    public required double X0 { get; init; }
    public required double Y0 { get; init; }
    public required double Size { get; init; }
    public required double Step { get; init; }
    public int Tx { get; init; }
    public int Ty { get; init; }
    /// <summary>Row-major n x n.</summary>
    public required double[] Values { get; init; }
    public int Missing { get; private set; }

    public static HeightGrid Read(string path)
    {
        Dictionary<string, double>? head = null;
        var rows = new SortedDictionary<int, SortedDictionary<int, string[]>>();
        bool end = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("HMAP BEGIN", StringComparison.Ordinal)) head = CameraLine.KeyValues(line);
            else if (line.StartsWith("HMAP j=", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int j = int.Parse(parts[1][2..], CultureInfo.InvariantCulture), k = int.Parse(parts[2][2..], CultureInfo.InvariantCulture);
                if (!rows.TryGetValue(j, out var chunks)) rows[j] = chunks = new SortedDictionary<int, string[]>();
                chunks[k] = parts[3..];
            }
            else if (line.StartsWith("HMAP END", StringComparison.Ordinal)) end = true;
        }
        if (head is null || !end) throw new InvalidDataException($"{path}: incomplete height grid");
        int n = (int)head["n"];
        var values = new double[n * n];
        Array.Fill(values, double.NaN);
        int missing = 0;
        foreach (var (j, chunks) in rows)
        {
            int i = 0;
            foreach (var v in chunks.Values.SelectMany(c => c))
            {
                if (i >= n || j >= n) break;
                values[j * n + i++] = v == "x" ? double.NaN : double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
        foreach (var v in values) if (double.IsNaN(v)) missing++;
        return new HeightGrid
        {
            N = n, X0 = head["x0"], Y0 = head["y0"], Size = head["size"], Step = head["step"],
            Tx = (int)head.GetValueOrDefault("tx"), Ty = (int)head.GetValueOrDefault("ty"),
            Values = values, Missing = missing,
        };
    }

    /// <summary>
    /// What is wrong with the lines of a height grid as they came from the game (BEGIN to END), or null: a value that is
    /// no height (the game cut a line in the middle of one), rows that never came, rows without exactly n values.
    /// </summary>
    public static string? Problem(IEnumerable<string> lines)
    {
        int n = -1;
        var values = new Dictionary<int, int>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("HMAP BEGIN", StringComparison.Ordinal))
            {
                if (CameraLine.KeyValues(line).TryGetValue("n", out var nv)) n = (int)nv;
            }
            else if (line.StartsWith("HMAP j=", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !int.TryParse(parts[1].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var j))
                    return $"a line could not be read ({line[..Math.Min(line.Length, 40)]})";
                foreach (var v in parts.AsSpan(3))
                    if (v != "x" && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        return $"row {j}: '{v}' is no height";
                values[j] = values.GetValueOrDefault(j) + parts.Length - 3;
            }
        }
        if (n < 0) return "no HMAP BEGIN with n";
        int missing = Enumerable.Range(0, n).Count(j => !values.ContainsKey(j));
        int uneven = values.Count(r => r.Key < n && r.Value != n) + values.Count(r => r.Key >= n);
        var bad = new List<string>();
        if (missing > 0) bad.Add($"{missing} rows missing");
        if (uneven > 0) bad.Add($"{uneven} rows without all their values");
        return bad.Count == 0 ? null : string.Join(", ", bad);
    }

    /// <summary>
    /// Every missing height takes the nearest measured one (of equally near ones the lower row, then the lower column:
    /// <see cref="DistanceTransform.Nearest"/>); a grid without any hit takes <paramref name="fallback"/>.
    /// </summary>
    public void FillMissing(double fallback)
    {
        if (!Values.Any(double.IsNaN)) { Missing = 0; return; }      // also for a grid made from values
        int n = N;
        var known = new Grid<bool>(n, n, Values.Select(v => !double.IsNaN(v)).ToArray());
        if (!known.Data.Any(k => k)) Array.Fill(Values, fallback);
        else
        {
            var nearest = DistanceTransform.Nearest(known);
            var src = (double[])Values.Clone();
            for (int p = 0; p < src.Length; p++)
                if (double.IsNaN(src[p])) Values[p] = src[nearest[p]];
        }
        Missing = 0;
    }
}
