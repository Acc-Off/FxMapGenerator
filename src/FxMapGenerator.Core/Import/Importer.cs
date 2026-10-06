using System.Collections.Concurrent;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Import;

public sealed record ImportResult(int Blocks, int Files, long Bytes, int Unchanged, IReadOnlyDictionary<BlockItem, int> Items, IReadOnlyList<string> Skipped);

/// <summary>
/// Brings captures and scans taken earlier (same file formats) into a work folder: the files are copied, never moved or
/// linked, so the source stays untouched; each block's items are recorded with the time of the source file.
/// </summary>
public static class Importer
{
    const int CopyThreads = 4;

    /// <summary>
    /// A capture folder: <c>&lt;block&gt;.png</c> with <c>.cam.txt</c> (the camera line) = the shot; <c>.hmap</c> ending in an
    /// <c>HMAP END</c> line = the height grid.
    /// </summary>
    public static ImportResult ImportCapture(WorkFolder folder, StateStore state, string source, Action<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder.Capture);
        var found = new ConcurrentBag<(BlockId, BlockItem, DateTime)>();
        var skipped = new ConcurrentBag<string>();
        long bytes = 0; int files = 0, unchanged = 0, blocks = 0;
        var pngs = Directory.EnumerateFiles(source, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        Parallel.ForEach(pngs, new ParallelOptions { MaxDegreeOfParallelism = CopyThreads, CancellationToken = ct }, png =>
        {
            var name = Path.GetFileNameWithoutExtension(png);
            if (!BlockId.TryParse(name, out var block)) { skipped.Add(Path.GetFileName(png)); return; }
            var cam = Path.Combine(source, name + ".cam.txt");
            if (!File.Exists(cam) || new FileInfo(cam).Length == 0) { skipped.Add(name + " (no camera line)"); return; }
            Copy(png, folder.CapturePng(block));
            Copy(cam, folder.CaptureCamera(block));
            found.Add((block, BlockItem.Shot, File.GetLastWriteTimeUtc(png)));
            var hmap = Path.Combine(source, name + ".hmap");
            if (File.Exists(hmap) && LastLineStartsWith(hmap, "HMAP END"))
            {
                Copy(hmap, folder.CaptureHeights(block));
                found.Add((block, BlockItem.Height, File.GetLastWriteTimeUtc(hmap)));
            }
            else if (File.Exists(hmap)) skipped.Add(name + ".hmap (incomplete)");
            if (Interlocked.Increment(ref blocks) % 100 == 0) progress?.Invoke($"capture: {blocks} of {pngs.Count} blocks");
        });
        state.SetItems(found);
        return Result(blocks, found, skipped);

        void Copy(string from, string to)
        {
            var src = new FileInfo(from);
            var dst = new FileInfo(to);
            if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) { Interlocked.Increment(ref unchanged); return; }
            File.Copy(from, to, overwrite: true);
            File.SetLastWriteTimeUtc(to, src.LastWriteTimeUtc);
            Interlocked.Add(ref bytes, src.Length);
            Interlocked.Increment(ref files);
        }

        ImportResult Result(int n, ConcurrentBag<(BlockId, BlockItem, DateTime)> items, ConcurrentBag<string> skip) =>
            new(n, files, bytes, unchanged, Count(items), skip.OrderBy(s => s).ToList());
    }

    /// <summary>
    /// A scan folder: <c>&lt;block&gt;.txt</c> with scan lines. A finished <c>kind=mat</c> section = the ground scan (and the
    /// canopy when its header carries <c>pflags2=256</c>); a finished <c>kind=road</c> section = the road scan.
    /// </summary>
    public static ImportResult ImportScan(WorkFolder folder, StateStore state, string source, Action<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder.Scan);
        var found = new ConcurrentBag<(BlockId, BlockItem, DateTime)>();
        var skipped = new ConcurrentBag<string>();
        long bytes = 0; int files = 0, unchanged = 0, blocks = 0;
        var txts = Directory.EnumerateFiles(source, "*.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        Parallel.ForEach(txts, new ParallelOptions { MaxDegreeOfParallelism = CopyThreads, CancellationToken = ct }, txt =>
        {
            var name = Path.GetFileNameWithoutExtension(txt);
            if (!BlockId.TryParse(name, out var block)) { skipped.Add(Path.GetFileName(txt)); return; }
            bool ground = false, canopy = false, roads = false, canopyHeader = false;
            foreach (var line in File.ReadLines(txt))
            {
                if (!line.Contains("MSCAN ", StringComparison.Ordinal)) continue;
                if (line.Contains("MSCAN BEGIN", StringComparison.Ordinal) && line.Contains("kind=mat", StringComparison.Ordinal))
                    canopyHeader = line.Contains("pflags2=256", StringComparison.Ordinal);
                else if (line.Contains("MSCAN END kind=mat", StringComparison.Ordinal)) { ground = true; canopy |= canopyHeader; }
                else if (line.Contains("MSCAN END kind=road", StringComparison.Ordinal)) roads = true;
            }
            if (!ground && !roads) { skipped.Add(name + " (no finished scan section)"); return; }
            var src = new FileInfo(txt);
            var dst = new FileInfo(folder.ScanFile(block));
            if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) Interlocked.Increment(ref unchanged);
            else
            {
                File.Copy(txt, dst.FullName, overwrite: true);
                File.SetLastWriteTimeUtc(dst.FullName, src.LastWriteTimeUtc);
                Interlocked.Add(ref bytes, src.Length);
                Interlocked.Increment(ref files);
            }
            var time = src.LastWriteTimeUtc;
            if (ground) found.Add((block, BlockItem.ScanGround, time));
            if (canopy) found.Add((block, BlockItem.ScanCanopy, time));
            if (roads) found.Add((block, BlockItem.ScanRoads, time));
            if (Interlocked.Increment(ref blocks) % 100 == 0) progress?.Invoke($"scan: {blocks} of {txts.Count} blocks");
        });
        state.SetItems(found);
        return new ImportResult(blocks, files, bytes, unchanged, Count(found), skipped.OrderBy(s => s).ToList());
    }

    static IReadOnlyDictionary<BlockItem, int> Count(IEnumerable<(BlockId, BlockItem Item, DateTime)> items) =>
        BlockItems.All.ToDictionary(i => i, i => items.Count(x => x.Item == i));

    static bool LastLineStartsWith(string path, string prefix)
    {
        string? last = null;
        foreach (var line in File.ReadLines(path))
            if (!string.IsNullOrWhiteSpace(line)) last = line;
        return last != null && last.TrimStart().StartsWith(prefix, StringComparison.Ordinal);
    }
}
