using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Satellite;

/// <summary>
/// The surface heights of the blocks and the project's height quality (<c>heightQuality</c>), which decides what the
/// visit takes for them and what each use reads:
/// <list type="bullet">
/// <item><c>speed</c>: the height data only (<c>capture/&lt;block&gt;.hmap</c>, the game's ground height with the water
/// surface where higher). The satellite map alone.</item>
/// <item><c>balance</c>: the ground scan only (<c>scan/&lt;block&gt;.txt</c>: per point the higher of the ray's hit and the
/// water surface).</item>
/// <item><c>quality</c>: both, per point the higher of the two.</item>
/// </list>
/// The photos (orthorectification, scale calibration, provisional tiles) read <see cref="PhotoItems"/>; the landcover's
/// ground and buildings and the cells' shade and contours <see cref="LandcoverItems"/> (the same items: speed does not go
/// with an atlas or road map).
/// </summary>
public static class SurfaceHeights
{
    public const string Speed = "speed", Balance = "balance", Quality = "quality";

    public static bool IsQuality(string? q) => q is Speed or Balance or Quality;

    /// <summary>Why a quality cannot go with these maps (for the project check), or null when it can.</summary>
    public static string? Unavailable(string quality, bool satellite, bool cellMaps) => quality switch
    {
        Speed when cellMaps => "does not go with an atlas or road map (their ground scan gives the heights)",
        _ => null,
    };

    /// <summary>
    /// The default for a set of maps: speed for the satellite map alone, quality with an atlas or road map (also without the
    /// satellite map: the height data serves their landcover, and taken with the camera the scans come quicker, so the
    /// visit takes about as long as the scans alone).
    /// </summary>
    public static string DefaultFor(bool satellite, bool cellMaps) => !cellMaps ? Speed : Quality;

    /// <summary>After the maps changed: the chosen quality when it still goes with them, else null (the default for them).</summary>
    public static string? Keep(string? quality, bool satellite, bool cellMaps) =>
        quality is not null && IsQuality(quality) && Unavailable(quality, satellite, cellMaps) is null ? quality : null;

    /// <summary>The project's maps as the two facts the height quality depends on.</summary>
    public static (bool Satellite, bool CellMaps) MapsOf(ProjectFile f) => (f.Maps.Satellite, f.Maps.Atlas.Enabled || f.Maps.Roadmap);

    /// <summary>The quality in use: the chosen one, or the default for the maps.</summary>
    public static string QualityOf(ProjectFile f)
    {
        var (sat, cell) = MapsOf(f);
        return f.HeightQuality ?? DefaultFor(sat, cell);
    }

    /// <summary>The data items the photos are placed with (orthorectification, scale calibration, provisional tiles).</summary>
    public static IReadOnlyList<BlockItem> PhotoItems(Project project) => QualityOf(project.File) switch
    {
        Balance => [BlockItem.ScanGround],
        Quality => [BlockItem.Height, BlockItem.ScanGround],
        _ => [BlockItem.Height],
    };

    /// <summary>
    /// The data items the landcover judges the ground and the buildings with, and the cells shade and draw the contours
    /// from: with quality both (per point the higher), otherwise the ground scan.
    /// </summary>
    public static IReadOnlyList<BlockItem> LandcoverItems(Project project) =>
        QualityOf(project.File) == Quality ? [BlockItem.Height, BlockItem.ScanGround] : [BlockItem.ScanGround];

    /// <summary>How the records name a set of height items: <c>both</c>, <c>scan</c> (the ground scan) or <c>grid</c> (the height data).</summary>
    public static string NameOf(IReadOnlyList<BlockItem> items) =>
        items.Contains(BlockItem.Height) ? items.Contains(BlockItem.ScanGround) ? "both" : "grid" : "scan";

    public static string PathOf(WorkFolder folder, BlockId b, BlockItem item) =>
        item == BlockItem.ScanGround ? folder.ScanFile(b) : folder.CaptureHeights(b);

    public static IReadOnlyList<string> PathsOf(WorkFolder folder, BlockId b, IReadOnlyList<BlockItem> items) =>
        items.Select(i => PathOf(folder, b, i)).ToList();

    /// <summary>The heights of a block's height grid (<c>.hmap</c>) or ground scan (any other file).</summary>
    public static HeightGrid Read(string path) =>
        path.EndsWith(".hmap", StringComparison.OrdinalIgnoreCase) ? HeightGrid.Read(path) : FromScan(ScanFile.Read(path), path);

    /// <summary>Per point the higher of the heights of several files of one block (a point without a value in one takes the other's).</summary>
    public static HeightGrid Read(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new ArgumentException("no height file", nameof(paths));
        var o = Read(paths[0]);
        foreach (var path in paths.Skip(1)) o = Higher(o, Read(path), path, paths[0]);
        return o;
    }

    /// <summary>
    /// The heights of a block's items with its ground scan already read (the height data read from its file): per point
    /// the higher, as <see cref="Read(IReadOnlyList{string})"/>.
    /// </summary>
    public static HeightGrid Of(WorkFolder folder, BlockId b, IReadOnlyList<BlockItem> items, ScanFile scan)
    {
        if (items.Count == 0) throw new ArgumentException("no height item", nameof(items));
        HeightGrid? o = null;
        foreach (var item in items)
        {
            var g = item == BlockItem.ScanGround ? FromScan(scan, b.Name) : Read(PathOf(folder, b, item));
            o = o is null ? g : Higher(o, g, PathOf(folder, b, item), PathOf(folder, b, items[0]));
        }
        return o!;
    }

    static HeightGrid Higher(HeightGrid first, HeightGrid g, string path, string firstPath)
    {
        if (g.N != first.N || g.X0 != first.X0 || g.Y0 != first.Y0 || g.Step != first.Step)
            throw new InvalidDataException($"{path} does not cover the same points as {firstPath}");
        var v = (double[])first.Values.Clone();
        for (int i = 0; i < v.Length; i++)
        {
            double a = v[i], b = g.Values[i];
            v[i] = double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b);
        }
        return new HeightGrid { N = first.N, X0 = first.X0, Y0 = first.Y0, Size = first.Size, Step = first.Step, Tx = first.Tx, Ty = first.Ty, Values = v };
    }

    /// <summary>The scan's surface: per point the higher of the ground hit and the water surface (NaN where neither).</summary>
    public static HeightGrid FromScan(ScanFile s, string? name = null)
    {
        if (!s.HasGround) throw new InvalidDataException($"{name ?? "the scan"} has no ground scan");
        var v = new double[s.N * s.N];
        for (int i = 0; i < v.Length; i++)
        {
            float z = s.HitZ[i], w = s.Water[i];
            v[i] = float.IsNaN(z) ? w : float.IsNaN(w) ? z : Math.Max(z, w);
        }
        return new HeightGrid { N = s.N, X0 = s.X0, Y0 = s.Y0, Size = s.Size, Step = s.Step, Tx = s.Tx, Ty = s.Ty, Values = v };
    }
}
