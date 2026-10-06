using System.Text.Json;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Satellite;

/// <summary>
/// The parts of a map's tiles the lower zooms steps make over the project's frame (<see cref="MapFrame"/>): the open sea on
/// the frame's z8 tiles outside the range, and the tiles of z7 to z0 from the four below them. The frame they were made for
/// is kept per map (<c>data/low-zoom-&lt;map&gt;.json</c>; none = the standard frame), so a changed frame makes them again,
/// and the tiles a larger frame left outside a smaller one go.
/// </summary>
public static class LowZooms
{
    /// <summary>The z8 tiles of the frame whose block is not in the range, row by row.</summary>
    public static List<(int X, int Y)> SeaTiles(MapFrame frame, IReadOnlySet<BlockId> inRange)
    {
        var (x0, y0, x1, y1) = frame.Tiles(WorldGrid.Zoom);
        var list = new List<(int X, int Y)>();
        for (int ty = y0; ty < y1; ty++)
            for (int tx = x0; tx < x1; tx++)
                if (!inRange.Contains(new BlockId(tx >> 2, ty >> 2))) list.Add((tx, ty));
        return list;
    }

    /// <summary>
    /// Makes every tile of z7 to z0 over the frame from the four below it (a tile without any below is not written).
    /// Returns the tiles written. <paramref name="report"/> gets the zoom and the share done. With
    /// <paramref name="finest"/>, the tiles start at that zoom instead of z8 (a picture of zoom 6 or 7 cut into tiles):
    /// the zooms below it are made.
    /// </summary>
    public static int BuildParents(TileStore tiles, MapFrame frame, IParallelRunner parallel, Action<string, double> report, CancellationToken token,
        int finest = WorldGrid.Zoom)
    {
        int parents = 0;
        for (int z = finest - 1; z >= 0; z--)
        {
            token.ThrowIfCancellationRequested();
            report("z" + z, (finest - z) / (double)(finest + 1));
            var (x0, y0, x1, y1) = frame.Tiles(z);
            int nx = x1 - x0;
            var at = Enumerable.Range(0, nx * (y1 - y0)).Select(i => (X: x0 + i % nx, Y: y0 + i / nx)).ToList();
            parallel.ForEach(at, c => { if (tiles.BuildParent(z, c.X, c.Y)) Interlocked.Increment(ref parents); });
        }
        return parents;
    }

    /// <summary>Deletes the tiles of every zoom that lie outside the frame (made for a larger frame before). Returns how many.</summary>
    public static int RemoveOutside(TileStore tiles, MapFrame frame)
    {
        int removed = 0;
        for (int z = 0; z <= WorldGrid.Zoom; z++)
        {
            var zDir = Path.Combine(tiles.Root, z.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!Directory.Exists(zDir)) continue;
            var (x0, y0, x1, y1) = frame.Tiles(z);
            foreach (var xDir in Directory.EnumerateDirectories(zDir))
            {
                if (!int.TryParse(Path.GetFileName(xDir), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int x)) continue;
                bool column = x >= x0 && x < x1;
                foreach (var f in Directory.EnumerateFiles(xDir, "*.png"))
                    if (!column || !int.TryParse(Path.GetFileNameWithoutExtension(f), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int y) || y < y0 || y >= y1)
                    {
                        File.Delete(f);
                        removed++;
                    }
                if (!column && !Directory.EnumerateFileSystemEntries(xDir).Any()) Directory.Delete(xDir);
            }
        }
        return removed;
    }

    /// <summary>The record of the frame a map's lower zooms were made for.</summary>
    public static string RecordPath(WorkFolder folder, string map) => Path.Combine(folder.Data, $"low-zoom-{map}.json");

    /// <summary>The frame a map's lower zooms were made for (the standard frame when there is no record).</summary>
    public static MapFrame Recorded(WorkFolder folder, string map)
    {
        var path = RecordPath(folder, map);
        if (!File.Exists(path)) return MapFrame.Standard;
        try
        {
            var c = JsonSerializer.Deserialize<ExtraCellsSetting>(File.ReadAllText(path), Project.Json);
            return c is null ? MapFrame.Standard : new MapFrame(c.Top, c.Bottom, c.Left, c.Right);
        }
        catch (JsonException) { return new MapFrame(-1, -1, -1, -1); }       // unreadable: never the project's
    }

    /// <summary>
    /// Keeps the frame a map's lower zooms were made for (removes the record for the standard frame). A new work folder
    /// may have no data folder yet when its first lower zooms are made (the satellite map's come right after the visit).
    /// </summary>
    public static void Record(WorkFolder folder, string map, MapFrame frame)
    {
        var path = RecordPath(folder, map);
        if (frame.IsStandard)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(folder.Data);
        var c = new ExtraCellsSetting { Top = frame.CellsTop, Bottom = frame.CellsBottom, Left = frame.CellsLeft, Right = frame.CellsRight };
        File.WriteAllText(path, JsonSerializer.Serialize(c, Project.Json) + "\n");
    }

    /// <summary>
    /// Before painting and making the lower zooms of a map: when they were made for another frame, deletes the tiles
    /// outside the project's (a smaller frame leaves them behind). Returns how many went.
    /// </summary>
    public static int Prepare(WorkFolder folder, string map, TileStore tiles, MapFrame frame) =>
        Recorded(folder, map) == frame ? 0 : RemoveOutside(tiles, frame);
}
