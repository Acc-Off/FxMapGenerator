using System.Globalization;
using FxMapGenerator.Core.Jobs;

namespace FxMapGenerator.Core.Scan;

/// <summary>
/// The street hashes and zone codes a scan met, from its dictionary lines
/// (<c>MSCAN dict street &lt;i&gt; &lt;hash&gt; &lt;text&gt;</c>, <c>MSCAN dict zone &lt;i&gt; &lt;code&gt; &lt;text&gt;</c>; the hash is a signed 32-bit number).
/// </summary>
public static class ScanNames
{
    /// <param name="parallel">Files read side by side; null reads them one after the other.</param>
    public static (SortedSet<uint> Streets, SortedSet<string> Zones) Collect(IReadOnlyList<string> scanFiles, IParallelRunner? parallel = null, CancellationToken token = default)
    {
        var streets = new SortedSet<uint>();
        var zones = new SortedSet<string>(StringComparer.Ordinal);
        void Read(string file)
        {
            token.ThrowIfCancellationRequested();
            var s = new List<uint>();
            var z = new List<string>();
            foreach (var line in File.ReadLines(file))
            {
                int at = line.IndexOf("MSCAN dict ", StringComparison.Ordinal);
                if (at < 0) continue;
                var parts = line[(at + 11)..].Split(' ', 4);
                if (parts.Length < 3) continue;
                if (parts[0] == "street" && long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var h))
                    s.Add(unchecked((uint)h));
                else if (parts[0] == "zone") z.Add(parts[2]);
            }
            lock (streets)
            {
                streets.UnionWith(s);
                zones.UnionWith(z);
            }
        }
        if (parallel is null) foreach (var f in scanFiles) Read(f);
        else parallel.ForEach(scanFiles, Read);
        return (streets, zones);
    }
}
