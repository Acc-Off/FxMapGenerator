using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Satellite;

/// <summary>
/// A place where a map's tiles changed: the blocks around its changed z8 tiles (west, north, east, south, m), how many, and
/// whether none of them shows the change (<see cref="BeforeTiles.Faint"/>: every pixel within a few steps of before).
/// </summary>
public sealed record ChangedArea(double X0, double Y0, double X1, double Y1, int Tiles, bool Faint = false);

/// <summary>What changed of a map since its tiles were kept (<see cref="WorkFolder.Before"/>): the tiles kept per zoom, the
/// z8 tiles (x, y, ... in one list, for outlining them) and those of them whose change does not show, and the places: those
/// that show the change first, the largest first, then those that do not.</summary>
public sealed record ChangedMap(string Map, int Tiles, IReadOnlyList<int> Z8, IReadOnlyList<ChangedArea> Areas, IReadOnlyList<int>? FaintZ8 = null);

/// <summary>
/// The tiles a work folder kept as they were before they were written over (<see cref="TileStore.Before"/>): what changed
/// since the last export (or since the first run that wrote over them), for comparing the map before and after.
/// </summary>
public static class BeforeTiles
{
    /// <summary>A changed tile whose every pixel is within this many steps (of 256, any channel) of before does not show the change.</summary>
    public const int Faint = 2;

    /// <summary>The largest difference of a tile from before (any channel of any pixel), kept per pair of file versions.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Differences = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The largest difference (0-255) between the tile kept from before and the tile now, any channel of any pixel (255 when
    /// either cannot be read or they differ in size).
    /// </summary>
    public static int Difference(string before, string now)
    {
        var b = new FileInfo(before);
        var n = new FileInfo(now);
        if (!b.Exists || !n.Exists) return 255;
        string key = $"{b.FullName}|{b.Length}|{b.LastWriteTimeUtc.Ticks}|{n.Length}|{n.LastWriteTimeUtc.Ticks}";
        return Differences.GetOrAdd(key, _ =>
        {
            using var x = SkiaSharp.SKBitmap.Decode(before);
            using var y = SkiaSharp.SKBitmap.Decode(now);
            if (x is null || y is null || x.Width != y.Width || x.Height != y.Height) return 255;
            using var xs = x.Copy(SkiaSharp.SKColorType.Rgba8888);
            using var ys = y.Copy(SkiaSharp.SKColorType.Rgba8888);
            var pa = xs.GetPixelSpan();
            var pb = ys.GetPixelSpan();
            int most = 0;
            for (int i = 0; i < pa.Length && most < 255; i++) most = Math.Max(most, Math.Abs(pa[i] - pb[i]));
            return most;
        });
    }

    /// <summary>The changes of a map, or null when none of its tiles was written over.</summary>
    public static ChangedMap? Of(WorkFolder folder, string map)
    {
        var root = folder.Before(map);
        if (!Directory.Exists(root)) return null;
        int all = Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories).Count();
        if (all == 0) return null;
        var z8 = new List<(int X, int Y)>();
        var dir8 = Path.Combine(root, WorldGrid.Zoom.ToString());
        if (Directory.Exists(dir8))
            foreach (var xDir in Directory.EnumerateDirectories(dir8))
                if (int.TryParse(Path.GetFileName(xDir), out int x))
                    foreach (var f in Directory.EnumerateFiles(xDir, "*.png"))
                        if (int.TryParse(Path.GetFileNameWithoutExtension(f), out int y)) z8.Add((x, y));
        z8.Sort();
        // the tiles whose change does not show: compared with the tile now
        var now = folder.Tiles(map);
        var faint = z8.Where(t => Difference(Path.Combine(dir8, t.X.ToString(), t.Y + ".png"), Path.Combine(now, WorldGrid.Zoom.ToString(), t.X.ToString(), t.Y + ".png")) <= Faint).ToHashSet();
        return new ChangedMap(map, all, z8.SelectMany(t => new[] { t.X, t.Y }).ToList(), Areas(z8, faint), faint.Order().SelectMany(t => new[] { t.X, t.Y }).ToList());
    }

    /// <summary>
    /// The changed z8 tiles gathered by block (neighbouring blocks, corners too, are one place): the places whose change shows
    /// first, the largest first, then those all of whose tiles are in <paramref name="faint"/>.
    /// </summary>
    public static List<ChangedArea> Areas(IReadOnlyList<(int X, int Y)> tiles, IReadOnlySet<(int X, int Y)>? faint = null)
    {
        var shows = tiles.Where(t => faint?.Contains(t) != true).GroupBy(t => (t.X >> 2, t.Y >> 2)).Select(g => g.Key).ToHashSet();
        var perBlock = tiles.GroupBy(t => (t.X >> 2, t.Y >> 2)).ToDictionary(g => g.Key, g => g.Count());
        var seen = new HashSet<(int, int)>();
        var areas = new List<ChangedArea>();
        foreach (var start in perBlock.Keys.OrderBy(k => k.Item2).ThenBy(k => k.Item1))
        {
            if (!seen.Add(start)) continue;
            var queue = new Queue<(int, int)>([start]);
            int bx0 = start.Item1, bx1 = start.Item1, by0 = start.Item2, by1 = start.Item2, n = 0;
            bool anyShows = false;
            while (queue.Count > 0)
            {
                var (bx, by) = queue.Dequeue();
                n += perBlock[(bx, by)];
                anyShows |= shows.Contains((bx, by));
                bx0 = Math.Min(bx0, bx); bx1 = Math.Max(bx1, bx); by0 = Math.Min(by0, by); by1 = Math.Max(by1, by);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var nb = (bx + dx, by + dy);
                        if (perBlock.ContainsKey(nb) && seen.Add(nb)) queue.Enqueue(nb);
                    }
            }
            areas.Add(new ChangedArea(WorldGrid.Left + bx0 * WorldGrid.BlockSize, WorldGrid.Top - by0 * WorldGrid.BlockSize,
                WorldGrid.Left + (bx1 + 1) * WorldGrid.BlockSize, WorldGrid.Top - (by1 + 1) * WorldGrid.BlockSize, n, !anyShows));
        }
        return areas.OrderBy(a => a.Faint).ThenByDescending(a => a.Tiles).ThenBy(a => -a.Y0).ThenBy(a => a.X0).ToList();
    }
}
