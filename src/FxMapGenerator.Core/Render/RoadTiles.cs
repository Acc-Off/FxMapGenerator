using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;
using SkiaSharp;

namespace FxMapGenerator.Core.Render;

/// <summary>
/// Where the road shapes lie, to pick those of a small rectangle quickly: each item's bounding box (for a junction
/// corner the box of its region and arc, for a tunnel group of all its outlines) in square buckets over the frame every
/// project's frame lies in (<see cref="MapFrame.Outer"/>; items beyond it go to the edge buckets).
/// </summary>
public sealed class RoadShapesIndex
{
    const double Bucket = 250;
    static readonly MapFrame Over = MapFrame.Outer;
    static readonly int Cols = (int)Math.Ceiling((Over.X1 - Over.X0) / Bucket) + 1;
    static readonly int Rows = (int)Math.Ceiling((Over.Y0 - Over.Y1) / Bucket) + 1;

    readonly RoadShapesFile.Contents _roads;
    readonly Kind _tunnels, _tracks, _ground, _patches, _corners, _raised;

    /// <summary>The boxes of one kind of item (min x, min y, max x, max y) and the items per bucket, in file order.</summary>
    sealed class Kind
    {
        public readonly double[] Boxes;
        public readonly Dictionary<int, List<int>> Buckets = new();

        public Kind(int count, Func<int, (double, double, double, double)> box)
        {
            Boxes = new double[count * 4];
            for (int i = 0; i < count; i++)
            {
                var (x0, y0, x1, y1) = box(i);
                (Boxes[4 * i], Boxes[4 * i + 1], Boxes[4 * i + 2], Boxes[4 * i + 3]) = (x0, y0, x1, y1);
                if (double.IsInfinity(x0)) continue;
                for (int r = Row(y1); r <= Row(y0); r++)
                    for (int c = Col(x0); c <= Col(x1); c++)
                    {
                        int k = r * Cols + c;
                        if (!Buckets.TryGetValue(k, out var l)) Buckets[k] = l = new List<int>();
                        l.Add(i);
                    }
            }
        }

        /// <summary>The items whose box comes within <paramref name="pad"/> of the rectangle, in file order.</summary>
        public List<int> In((double X0, double Y0, double X1, double Y1) r, double pad)
        {
            var o = new HashSet<int>();
            for (int row = Row(r.Y0 + pad); row <= Row(r.Y1 - pad); row++)
                for (int c = Col(r.X0 - pad); c <= Col(r.X1 + pad); c++)
                    if (Buckets.TryGetValue(row * Cols + c, out var l))
                        foreach (var i in l)
                            if (Boxes[4 * i + 2] >= r.X0 - pad && Boxes[4 * i] <= r.X1 + pad && Boxes[4 * i + 3] >= r.Y1 - pad && Boxes[4 * i + 1] <= r.Y0 + pad)
                                o.Add(i);
            var list = o.ToList();
            list.Sort();
            return list;
        }
    }

    static int Col(double x) => Math.Clamp((int)Math.Floor((x - Over.X0) / Bucket), 0, Cols - 1);
    static int Row(double y) => Math.Clamp((int)Math.Floor((Over.Y0 - y) / Bucket), 0, Rows - 1);

    static (double, double, double, double) Box(IEnumerable<double[]> rings)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var pts in rings)
            for (int i = 0; i + 1 < pts.Length; i += 2)
            {
                minX = Math.Min(minX, pts[i]); maxX = Math.Max(maxX, pts[i]);
                minY = Math.Min(minY, pts[i + 1]); maxY = Math.Max(maxY, pts[i + 1]);
            }
        return (minX, minY, maxX, maxY);
    }

    public RoadShapesIndex(RoadShapesFile.Contents roads)
    {
        _roads = roads;
        var t = roads.Tracks;
        _tunnels = new Kind(roads.Tunnels.Count, i => Box(roads.Tunnels[i].Rings));
        _tracks = new Kind(t.Length / 4, i => (Math.Min(t[4 * i], t[4 * i + 2]), Math.Min(t[4 * i + 1], t[4 * i + 3]), Math.Max(t[4 * i], t[4 * i + 2]), Math.Max(t[4 * i + 1], t[4 * i + 3])));
        _ground = new Kind(roads.Ground.Count, i => Box([roads.Ground[i].Casing]));
        _patches = new Kind(roads.Patches.Count, i => Box([roads.Patches[i].Ring]));
        _corners = new Kind(roads.Corners.Count, i => Box([roads.Corners[i].Region, roads.Corners[i].Arc]));
        _raised = new Kind(roads.Raised.Count, i => Box([roads.Raised[i].Casing]));
    }

    public RoadShapesFile.Contents Roads => _roads;

    /// <summary>The shapes that come within <paramref name="pad"/> m of the rectangle (west, north, east, south), in file order.</summary>
    public CellInputs.Roads In((double X0, double Y0, double X1, double Y1) r, double pad)
    {
        var tracks = new List<double>();
        foreach (var i in _tracks.In(r, pad)) tracks.AddRange(_roads.Tracks.AsSpan(4 * i, 4));
        return new CellInputs.Roads(
            _tunnels.In(r, pad).Select(i => _roads.Tunnels[i]).ToList(),
            tracks.ToArray(),
            _ground.In(r, pad).Select(i => _roads.Ground[i]).ToList(),
            _patches.In(r, pad).Select(i => _roads.Patches[i]).ToList(),
            _corners.In(r, pad).Select(i => _roads.Corners[i]).ToList(),
            _raised.In(r, pad).Select(i => _roads.Raised[i]).ToList());
    }
}

/// <summary>
/// Tiles of the roads alone (transparent elsewhere) in a map's colours, drawn as the cells draw roads
/// (<see cref="RoadDrawing"/>): the road editor lays them over its map. Tile z / x / y as the map's tiles (at z8 a tile is
/// <see cref="WorldGrid.TileSize"/> m, from the map's north-west corner); zooms above 8 are drawn at their own scale.
/// </summary>
public static class RoadTiles
{
    public const int Size = 256;
    public const int MaxZoom = 11;

    /// <summary>The game rectangle (west, north, east, south) of a tile.</summary>
    public static (double X0, double Y0, double X1, double Y1) Rect(int z, int x, int y)
    {
        double size = WorldGrid.TileSize * Math.Pow(2, WorldGrid.Zoom - z);
        double x0 = WorldGrid.Left + x * size, y0 = WorldGrid.Top - y * size;
        return (x0, y0, x0 + size, y0 - size);
    }

    /// <summary>The tile as a PNG (straight RGBA, transparent where no road is).</summary>
    public static byte[] Png(RoadShapesIndex index, MapStyle style, int z, int x, int y)
    {
        var r = Rect(z, x, y);
        double ppm = Size / (r.X1 - r.X0);
        // shapes whose box reaches the tile, with room for the casings and the round ends of the tracks
        double pad = Math.Max(4, 8 / ppm);
        var owned = new List<IDisposable>();
        var over = new List<Action<SKCanvas>>();
        try
        {
            new RoadDrawing(r.X0, r.Y0, ppm, owned).Add(over, index.In(r, pad), style);
            using var bmp = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(SKColors.Transparent);
                foreach (var d in over) d(c);
                c.Flush();
            }
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            foreach (var d in owned) d.Dispose();
        }
    }
}
