using System.Text.Json.Nodes;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Regions;

/// <summary>
/// The region colours of the whole map on a 4 m grid over the project's frame (the grid of the standard frame's corner:
/// cell (j, i) of the field centred at x = x0 + 4 (i + 0.5), y = y0 - 4 (j + 0.5), <see cref="Extent"/>): the zone of each cell gives its region (the style's zone table); land cells of unknown zones
/// take the nearest known region; each region's share of the land around a cell (a Gaussian of the style's blur) mixes
/// the region colours. Cells with no land within reach take the colour of the style's sea region. The regional atlas
/// samples it for its ground (<see cref="Sample"/>), so the blur has no seams at cell edges.
/// <c>data/regions-&lt;key&gt;.grid</c> (<see cref="FileName"/>, one per regions section of the maps' styles):
/// <c>red</c>, <c>green</c>, <c>blue</c> (f32, 0..255), <c>region</c> (u8, index into the style's region colours; 255 =
/// none), <c>zone</c> (u16, index into meta <c>zones</c>); meta <c>x0</c>, <c>y0</c>, <c>step</c>, <c>zones</c>,
/// <c>regions</c> (the style's regions section it was made with).
/// </summary>
public sealed class RegionField
{
    public const double Step = 4;

    /// <summary>The key of a style's regions section: the first 12 hex digits of the SHA-256 of its JSON (styles with the same section share a field).</summary>
    public static string KeyOf(JsonNode section) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(section.ToJsonString())))[..12];

    /// <summary>The field of a regions section: <c>regions-&lt;key&gt;.grid</c> (<see cref="KeyOf"/>).</summary>
    public static string FileName(string key) => $"regions-{key}.grid";

    /// <summary>
    /// The field's 4 m cells over a frame, counted from the standard frame's north-west corner (every frame's field lies on
    /// that one grid, so the cells of the standard part stay where they are; an edge of the frame inside a cell takes the
    /// whole cell): the first column and row (negative when cells are added to the left or above) and the size.
    /// </summary>
    public static (int C0, int R0, int Width, int Height) Extent(MapFrame frame)
    {
        int c0 = (int)Math.Floor((frame.X0 - WorldGrid.Left) / Step), r0 = (int)Math.Floor((WorldGrid.Top - frame.Y0) / Step);
        int c1 = (int)Math.Ceiling((frame.X1 - WorldGrid.Left) / Step), r1 = (int)Math.Ceiling((WorldGrid.Top - frame.Y1) / Step);
        return (c0, r0, c1 - c0, r1 - r0);
    }

    public required Grid<float> Red { get; init; }
    public required Grid<float> Green { get; init; }
    public required Grid<float> Blue { get; init; }
    public required Grid<byte> Region { get; init; }
    public required Grid<ushort> Zone { get; init; }
    public required IReadOnlyList<string> Codes { get; init; }
    public JsonNode? Params { get; init; }
    /// <summary>The north-west corner of the field's cell (0, 0) (the frame's field's, <see cref="Extent"/>, or a window's).</summary>
    public double OriginX { get; init; } = WorldGrid.Left;
    public double OriginY { get; init; } = WorldGrid.Top;

    /// <summary>
    /// The land of the frame's field (<see cref="Extent"/>) at 1 m from the landcover files (a cell is land unless water or
    /// the water material): the blocks in name order, each placed at its corner rounded half to even, a later block over
    /// an earlier one; then per 4 m cell: land when at least half of its 16 cells are.
    /// </summary>
    public static Grid<bool> Land4(string dataFolder, IEnumerable<BlockId> blocks, MapFrame frame, IParallelRunner? parallel = null)
    {
        var (c0, r0, w, h) = Extent(frame);
        return Land4(dataFolder, blocks, c0, r0, w, h, parallel);
    }

    /// <summary>
    /// <see cref="Land4(string, IEnumerable{BlockId}, MapFrame, IParallelRunner?)"/> for a window of 4 m cells counted from
    /// the standard frame's corner: columns <paramref name="c0"/> to c0 + <paramref name="w"/>, rows <paramref name="r0"/>
    /// to r0 + <paramref name="h"/> (only the blocks that reach into it are read).
    /// </summary>
    public static Grid<bool> Land4(string dataFolder, IEnumerable<BlockId> blocks, int c0, int r0, int w, int h, IParallelRunner? parallel = null)
    {
        int ww = 4 * w, hw = 4 * h, ox = 4 * c0, oy = 4 * r0;
        var land = new bool[ww * hw];
        // whether a block's landcover can reach into the window: its corner in the frame's 1 m cells, a block across and
        // some metres more (a landcover grid covers its block and a seam)
        const int Slack = 16;
        bool Reaches(BlockId b)
        {
            int br = (int)Math.Round(b.Ty * WorldGrid.TileSize, MidpointRounding.ToEven) - oy - Slack;
            int bc = (int)Math.Round(b.Tx * WorldGrid.TileSize, MidpointRounding.ToEven) - ox - Slack;
            int size = (int)Math.Ceiling(WorldGrid.BlockSize) + 2 * Slack;
            return br < hw && bc < ww && br + size > 0 && bc + size > 0;
        }
        var order = blocks.Where(Reaches).OrderBy(b => b.Name + "-", StringComparer.Ordinal).ToList();
        var layers = new (double X0, double Y0, Grid<byte> Lc, Grid<bool> Water)[order.Count];
        void Load(int i)
        {
            var f = GridFile.Load(LandcoverFile.PathOf(dataFolder, order[i]));
            layers[i] = (f.Meta["x0"]!.GetValue<double>(), f.Meta["y0"]!.GetValue<double>(), f.Get<byte>("landcover"), f.Get<bool>("water"));
        }
        if (parallel is not null) parallel.ForEach(Enumerable.Range(0, order.Count).ToList(), Load);
        else for (int i = 0; i < order.Count; i++) Load(i);
        byte waterMaterial = (byte)GroundClass.WaterMaterial;
        foreach (var (x0, y0, lc, water) in layers)
        {
            int br = (int)Math.Round(WorldGrid.Top - y0, MidpointRounding.ToEven) - oy, bc = (int)Math.Round(x0 - WorldGrid.Left, MidpointRounding.ToEven) - ox;
            for (int r = Math.Max(0, -br); r < lc.Height && br + r < hw; r++)
                for (int c = Math.Max(0, -bc); c < lc.Width && bc + c < ww; c++)
                    land[(br + r) * ww + bc + c] = !(water[r, c] || lc[r, c] == waterMaterial);
        }
        var o = new Grid<bool>(w, h);
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int n = 0;
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        if (land[(4 * j + y) * ww + 4 * i + x]) n++;
                o[j, i] = n >= 8;
            }
        return o;
    }

    /// <summary>The zone of every 4 m cell of the frame's field from the zone grid (the node its centre falls on; 0 outside it).</summary>
    public static Grid<ushort> ZonesOnFrame(ZoneGrid zg, MapFrame frame)
    {
        var (c0, r0, w, h) = Extent(frame);
        return ZonesOnFrame(zg, c0, r0, w, h);
    }

    /// <summary><see cref="ZonesOnFrame(ZoneGrid, MapFrame)"/> for a window of 4 m cells (as <see cref="Land4(string, IEnumerable{BlockId}, int, int, int, int, IParallelRunner?)"/>).</summary>
    public static Grid<ushort> ZonesOnFrame(ZoneGrid zg, int c0, int r0, int w, int h)
    {
        var o = new Grid<ushort>(w, h);
        for (int j = 0; j < h; j++)
        {
            double y = WorldGrid.Top - (r0 + j + 0.5) * Step;
            long zr = (long)Math.Floor((zg.Y0 - y) / zg.Step);
            if (zr < 0 || zr >= zg.Zone.Height) continue;
            for (int i = 0; i < w; i++)
            {
                double x = WorldGrid.Left + (c0 + i + 0.5) * Step;
                long zc = (long)Math.Floor((x - zg.X0) / zg.Step);
                if (zc < 0 || zc >= zg.Zone.Width) continue;
                o[j, i] = zg.Inside[(int)zr, (int)zc] ? zg.Zone[(int)zr, (int)zc] : (ushort)0;
            }
        }
        return o;
    }

    /// <summary>The field of <paramref name="zones"/> (on the frame, codes <paramref name="codes"/>) over the land <paramref name="land4"/>.</summary>
    public static RegionField Compute(Grid<ushort> zones, IReadOnlyList<string> codes, Grid<bool> land4, RegionsStyle style, IParallelRunner? parallel = null)
    {
        int w = zones.Width, h = zones.Height, n = w * h;
        var lut = codes.Select(c => style.Zones.TryGetValue(c, out var reg) ? style.IndexOf(reg) : -1).ToArray();
        var region = new int[n];
        var known = new Grid<bool>(w, h);
        for (int i = 0; i < n; i++)
        {
            region[i] = lut[zones.Data[i]];
            known.Data[i] = land4.Data[i] && region[i] >= 0;
        }
        if (known.Data.Any(k => k))
        {
            var nearest = DistanceTransform.Nearest(known);
            var filled = new int[n];
            for (int i = 0; i < n; i++) filled[i] = region[nearest[i]];
            region = filled;
        }
        double sigma = style.Blur / Step;
        var red = new Grid<float>(w, h);
        var green = new Grid<float>(w, h);
        var blue = new Grid<float>(w, h);
        for (int k = 0; k < style.Colors.Count; k++)
        {
            var mask = new Grid<float>(w, h);
            for (int i = 0; i < n; i++) mask.Data[i] = region[i] == k && land4.Data[i] ? 1f : 0f;
            var wk = Gaussian.Scipy(mask, sigma, parallel);
            var col = style.Colors[k].Color;
            float cr = col.R, cg = col.G, cb = col.B;
            for (int i = 0; i < n; i++)
            {
                float v = wk.Data[i];
                red.Data[i] += v * cr;
                green.Data[i] += v * cg;
                blue.Data[i] += v * cb;
            }
        }
        var landF = new Grid<float>(w, h);
        for (int i = 0; i < n; i++) landF.Data[i] = land4.Data[i] ? 1f : 0f;
        var den = Gaussian.Scipy(landF, sigma, parallel);
        var sea = style.Colors[Math.Max(0, style.IndexOf(style.Sea))].Color;
        const float minDen = 1e-4f;
        for (int i = 0; i < n; i++)
        {
            float d = den.Data[i];
            if (d < minDen)
            {
                red.Data[i] = sea.R; green.Data[i] = sea.G; blue.Data[i] = sea.B;
                continue;
            }
            float m = Math.Max(d, minDen);
            red.Data[i] /= m; green.Data[i] /= m; blue.Data[i] /= m;
        }
        var reg8 = new Grid<byte>(w, h);
        for (int i = 0; i < n; i++) reg8.Data[i] = region[i] < 0 ? (byte)255 : (byte)region[i];
        return new RegionField { Red = red, Green = green, Blue = blue, Region = reg8, Zone = zones, Codes = codes };
    }

    /// <summary>
    /// The colour at a world point: a linear sample of the 4 m field (SciPy's <c>map_coordinates</c>, order 1, edges
    /// repeated), one float32 value per channel.
    /// </summary>
    /// <summary>
    /// The digest of the colours a cell's ground reads (<see cref="Sample"/> at the nodes of its 1 m grid: the field's
    /// points around them, one more on each side for the interpolation), so a cell is made again only when those changed.
    /// </summary>
    public string WindowDigest(Cells.CellArea area)
    {
        static int Clamp(long v, int n) => (int)Math.Clamp(v, 0, n - 1);
        int width = Red.Width, height = Red.Height;
        int c0 = Clamp((long)Math.Floor((area.Gx0 - OriginX) / Step - 0.5), width), c1 = Clamp((long)Math.Floor((area.Gx0 + area.W - 1 - OriginX) / Step - 0.5) + 1, width);
        int r0 = Clamp((long)Math.Floor((OriginY - area.Gy0) / Step - 0.5), height), r1 = Clamp((long)Math.Floor((OriginY - (area.Gy0 - area.H + 1)) / Step - 0.5) + 1, height);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var g in new[] { Red, Green, Blue })
            for (int r = r0; r <= r1; r++)
                hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(g.Data.AsSpan(r * g.Width + c0, c1 - c0 + 1)));
        return Convert.ToHexStringLower(hash.GetHashAndReset()[..16]);
    }

    public (float R, float G, float B) Sample(double x, double y)
    {
        double fx = (x - OriginX) / Step - 0.5, fy = (OriginY - y) / Step - 0.5;
        return (Sampling.Linear(Red, fy, fx), Sampling.Linear(Green, fy, fx), Sampling.Linear(Blue, fy, fx));
    }

    public void Save(string path)
    {
        var f = new GridFile();
        f.Meta["x0"] = OriginX;
        f.Meta["y0"] = OriginY;
        f.Meta["step"] = Step;
        f.Meta["zones"] = new JsonArray(Codes.Select(c => (JsonNode)c).ToArray());
        f.Meta["regions"] = Params?.DeepClone();
        f.Add("red", Red);
        f.Add("green", Green);
        f.Add("blue", Blue);
        f.Add("region", Region);
        f.Add("zone", Zone);
        f.Save(path);
    }

    public static RegionField Load(string path)
    {
        var f = GridFile.Load(path);
        return new RegionField
        {
            Red = f.Get<float>("red"),
            Green = f.Get<float>("green"),
            Blue = f.Get<float>("blue"),
            Region = f.Get<byte>("region"),
            Zone = f.Get<ushort>("zone"),
            Codes = f.Meta["zones"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(),
            Params = f.Meta["regions"]?.DeepClone(),
            OriginX = f.Meta["x0"]!.GetValue<double>(),
            OriginY = f.Meta["y0"]!.GetValue<double>(),
        };
    }
}
