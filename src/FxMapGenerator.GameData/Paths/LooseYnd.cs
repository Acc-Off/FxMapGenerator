using System.IO.Compression;

namespace FxMapGenerator.GameData.Paths;

/// <summary>
/// Path node files outside the game's archives: the ones a server streams from its resources (a map pack replaces whole
/// areas this way). The source is a folder (searched with its subfolders, e.g. the server's resources folder, a category
/// folder such as <c>[maps]</c>, or a copy), a zip file (a pack as downloaded) or one <c>.ynd</c> file.
/// </summary>
public static class LooseYnd
{
    public sealed record Found(int Area, string Path, byte[] Bytes);

    /// <summary>
    /// Every <c>nodesNNN.ynd</c> in the source, in the dictionary order of their paths inside it (<see cref="Order"/>), so
    /// that of two files for one area the later is the one of the resource a server ensuring a category would start later.
    /// </summary>
    public static IEnumerable<Found> Read(string source, CancellationToken token = default)
    {
        if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(source);
            foreach (var e in zip.Entries.Where(e => YndFile.AreaOf(e.Name) >= 0).OrderBy(e => e.FullName, Order))
            {
                token.ThrowIfCancellationRequested();
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                yield return new Found(YndFile.AreaOf(e.Name), System.IO.Path.GetFileName(source) + "!" + e.FullName, ms.ToArray());
            }
        }
        else if (Directory.Exists(source))
        {
            var files = Directory.EnumerateFiles(source, "nodes*.ynd", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive })
                .Where(f => YndFile.AreaOf(f) >= 0)
                .OrderBy(f => System.IO.Path.GetRelativePath(source, f), Order);
            foreach (var f in files)
            {
                token.ThrowIfCancellationRequested();
                yield return new Found(YndFile.AreaOf(f), f, File.ReadAllBytes(f));
            }
        }
        else if (File.Exists(source))
        {
            int area = YndFile.AreaOf(source);
            if (area < 0) throw new InvalidDataException($"{source}: not a path node file (nodesNNN.ynd)");
            yield return new Found(area, source, File.ReadAllBytes(source));
        }
        else throw new FileNotFoundException($"server resources not found: {source}", source);
    }

    /// <summary>
    /// The dictionary order of relative paths: folder name by folder name, character by character with the letters taken
    /// in lower case (<c>[maps]</c> before <c>a</c> before <c>a b</c> before <c>B</c>; digits as characters, <c>res10</c>
    /// before <c>res9</c>; a folder's own files and subfolders by their names).
    /// </summary>
    public static readonly IComparer<string> Order = Comparer<string>.Create(ComparePaths);

    static int ComparePaths(string a, string b)
    {
        var sa = a.Split('\\', '/');
        var sb = b.Split('\\', '/');
        for (int i = 0; i < Math.Min(sa.Length, sb.Length); i++)
        {
            int c = CompareName(sa[i], sb[i]);
            if (c != 0) return c;
        }
        int n = sa.Length.CompareTo(sb.Length);
        return n != 0 ? n : string.CompareOrdinal(a, b);
    }

    static int CompareName(string a, string b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[i]));
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }
}
