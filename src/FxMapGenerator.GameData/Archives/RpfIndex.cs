using System.Text;
using System.Text.RegularExpressions;
using RageLib.Archives;
using RageLib.GTA5.ArchiveWrappers;

namespace FxMapGenerator.GameData.Archives;

/// <summary>
/// Walks the GTA V archives in the game's order (root *.rpf, update/*.rpf, update/x64/dlcpacks in dlclist.xml order,
/// nested .rpf inside each) and keeps the files whose name passes the filter, with the path relative to the game folder
/// (<c>update\update.rpf\x64\levels\gta5\paths.rpf\nodes464.ynd</c>). The keys must be installed first
/// (<see cref="Gta.GtaKeys.Install"/>). Archives that cannot be opened are reported and skipped.
/// </summary>
public sealed class RpfIndex : IDisposable
{
    public sealed record Entry(string Path, IArchiveFile File, int Order)
    {
        public string Name => File.Name;
    }

    public string GtaFolder { get; }
    public List<Entry> Entries { get; } = new();
    public int ArchiveCount => _archives.Count;
    /// <summary>Archives that could not be opened, with the reason.</summary>
    public List<string> Errors { get; } = new();

    readonly List<IDisposable> _archives = new();
    readonly Func<string, bool> _filter;
    readonly CancellationToken _token;

    RpfIndex(string gtaFolder, Func<string, bool> filter, CancellationToken token)
    {
        GtaFolder = gtaFolder;
        _filter = filter;
        _token = token;
    }

    /// <param name="filter">File names to keep (e.g. every <c>nodes*.ynd</c>); nested archives are always opened.</param>
    public static RpfIndex Open(string gtaFolder, Func<string, bool> filter, CancellationToken token = default)
    {
        var ix = new RpfIndex(gtaFolder, filter, token);
        try
        {
            foreach (var path in ix.TopLevelArchives())
            {
                token.ThrowIfCancellationRequested();
                ix.IndexArchive(path);
            }
        }
        catch
        {
            ix.Dispose();
            throw;
        }
        return ix;
    }

    IEnumerable<string> TopLevelArchives()
    {
        var list = new List<string>();
        list.AddRange(Directory.GetFiles(GtaFolder, "*.rpf").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        var update = System.IO.Path.Combine(GtaFolder, "update");
        if (Directory.Exists(update))
        {
            list.AddRange(Directory.GetFiles(update, "*.rpf").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            var dlcpacks = System.IO.Path.Combine(update, "x64", "dlcpacks");
            if (Directory.Exists(dlcpacks))
            {
                var order = ReadDlcOrder(System.IO.Path.Combine(update, "update.rpf"));
                int Rank(string p)
                {
                    var pack = System.IO.Path.GetRelativePath(dlcpacks, p).Replace('\\', '/').Split('/')[0].ToLowerInvariant();
                    return order.TryGetValue(pack, out var r) ? r : int.MaxValue;
                }
                list.AddRange(Directory.GetFiles(dlcpacks, "*.rpf", SearchOption.AllDirectories)
                    .OrderBy(Rank).ThenBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
        }
        return list;
    }

    /// <summary>The pack order of <c>common/data/dlclist.xml</c> inside update.rpf (pack folder name -> rank).</summary>
    Dictionary<string, int> ReadDlcOrder(string updateRpf)
    {
        var order = new Dictionary<string, int>();
        if (!File.Exists(updateRpf)) return order;
        try
        {
            using var fs = new FileStream(updateRpf, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var arc = RageArchiveWrapper7.Open(fs, System.IO.Path.GetFileName(updateRpf), leaveOpen: true);
            if (arc.Root.GetDirectory("common")?.GetDirectory("data")?.GetFile("dlclist.xml") is not IArchiveBinaryFile file) return order;
            using var ms = new MemoryStream();
            file.ExportUncompressed(ms);
            foreach (Match m in Regex.Matches(Encoding.UTF8.GetString(ms.ToArray()), @"dlcpacks:[/\\]([^/\\<]+)[/\\]?", RegexOptions.IgnoreCase))
                order.TryAdd(m.Groups[1].Value.ToLowerInvariant(), order.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Errors.Add($"{updateRpf} (dlclist.xml): {ex.Message}");
        }
        return order;
    }

    void IndexArchive(string path)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var arc = RageArchiveWrapper7.Open(fs, System.IO.Path.GetFileName(path), leaveOpen: false);
            _archives.Add(arc);
            IndexDirectory(arc.Root, System.IO.Path.GetRelativePath(GtaFolder, path), "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fs?.Dispose();
            Errors.Add($"{path}: {ex.Message}");
        }
    }

    void IndexDirectory(IArchiveDirectory dir, string archivePath, string dirPath)
    {
        _token.ThrowIfCancellationRequested();
        foreach (var file in dir.GetFiles())
        {
            var full = archivePath + "\\" + dirPath + file.Name;
            if (_filter(file.Name)) Entries.Add(new Entry(full, file, Entries.Count));
            if (file is IArchiveBinaryFile bin && file.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) OpenNested(bin, full);
        }
        foreach (var sub in dir.GetDirectories()) IndexDirectory(sub, archivePath, dirPath + sub.Name + "\\");
    }

    void OpenNested(IArchiveBinaryFile bin, string archivePath)
    {
        try
        {
            Stream stream;
            if (bin.IsCompressed || bin.IsEncrypted)
            {
                var ms = new MemoryStream();
                bin.ExportUncompressed(ms);
                ms.Position = 0;
                stream = ms;
            }
            else stream = bin.GetStream();
            var arc = RageArchiveWrapper7.Open(stream, bin.Name, leaveOpen: true);
            _archives.Add(arc);
            IndexDirectory(arc.Root, archivePath, "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Errors.Add($"{archivePath}: {ex.Message}");
        }
    }

    /// <summary>The whole file as stored for a resource (RSC7 header + data), or the uncompressed bytes of a binary file.</summary>
    public static byte[] Read(IArchiveFile file)
    {
        var ms = new MemoryStream();
        if (file is IArchiveResourceFile res) res.Export(ms);
        else if (file is IArchiveBinaryFile bin) bin.ExportUncompressed(ms);
        else throw new InvalidDataException("unknown archive file kind: " + file.Name);
        return ms.ToArray();
    }

    public void Dispose()
    {
        foreach (var a in _archives) a.Dispose();
        _archives.Clear();
    }
}
