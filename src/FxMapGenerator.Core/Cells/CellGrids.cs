using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Cells;

/// <summary>
/// A cell's 1 m grids (see <see cref="CellArea"/>), each node taking the nearest sample of the block under it: the
/// landcover's ground class, water, buildings and canopy; the scan's material class, railway track and water depth
/// (water surface, or the water-collision probe, minus the ground hit; 999 where the ray found no bed, NaN on land); the
/// zone of the road scan's 4 m grid at the node's cell centre (x + 0.5, y - 0.5); and whether the node lies in a block.
/// </summary>
public sealed class CellGrids
{
    public const float NoBed = 999f;

    public required CellArea Area { get; init; }
    public required Grid<byte> Landcover { get; init; }
    public required Grid<bool> Water { get; init; }
    public required Grid<bool> Buildings { get; init; }
    public required Grid<bool> Canopy { get; init; }
    /// <summary>Material class (<see cref="Materials"/> index).</summary>
    public required Grid<byte> MaterialClass { get; init; }
    public required Grid<bool> Rail { get; init; }
    public required Grid<float> Depth { get; init; }
    /// <summary>Zone code index into <see cref="ZoneCodes"/> (0 = none).</summary>
    public required Grid<ushort> Zone { get; init; }
    public required IReadOnlyList<string> ZoneCodes { get; init; }
    public required Grid<bool> Ok { get; init; }

    /// <summary>One block's inputs: its landcover grid and its scan.</summary>
    public sealed record BlockInput(GridFile Landcover, ScanFile Scan);

    public const string RailMaterial = "GRAVEL_TRAIN_TRACK";

    public static CellGrids Build(CellArea area, IReadOnlyDictionary<BlockId, BlockInput> blocks, Materials materials)
    {
        int W = area.W, H = area.H;
        var first = blocks.Values.First().Scan;
        int n = first.N;
        double step = first.Step;
        var withRoads = blocks.Values.Select(b => b.Scan).FirstOrDefault(s => s.HasRoads);
        int rn = withRoads?.RoadN ?? 0;
        double rstep = withRoads?.RoadStep ?? 4;
        uint rail = materials.Names.FirstOrDefault(kv => kv.Value == RailMaterial).Key;
        var codes = new List<string> { "" };
        var codeIndex = new Dictionary<string, ushort>(StringComparer.Ordinal) { [""] = 0 };
        // per block of the grid: its arrays, looked up by (bx, by)
        var per = new (byte[] Lc, bool[] Water, bool[] Bld, bool[] Canopy, byte[] Cls, uint[] Mat, float[] Depth, ushort[] Zone)?[area.Nbx * area.Nby];
        foreach (var (b, input) in blocks)
        {
            int bx = (b.Tx - area.Tx0) / 4, by = (b.Ty - area.Ty0) / 4;
            var f = input.Landcover;
            var lc = f.Get<byte>("landcover");
            var s = input.Scan;
            var depth = new float[n * n];
            for (int i = 0; i < n * n; i++)
            {
                float wh = s.Water[i], hz = s.HitZ[i];
                float wz = float.IsFinite(wh) ? wh : s.Probe[i];
                depth[i] = float.IsFinite(wz) ? (float.IsFinite(hz) ? Math.Max(wz - hz, 0f) : NoBed) : float.NaN;
            }
            ushort[] zone = [];
            if (s.HasRoads)
            {
                var local = new ushort[s.Zones.Count + 1];
                for (int j = 0; j < s.Zones.Count; j++)
                {
                    var code = s.Zones[j].Code;
                    if (!codeIndex.TryGetValue(code, out var ci)) { ci = (ushort)codes.Count; codeIndex[code] = ci; codes.Add(code); }
                    local[j + 1] = ci;
                }
                zone = new ushort[rn * rn];
                int m = Math.Min(rn, s.RoadN);
                for (int r = 0; r < m; r++)
                    for (int c = 0; c < m; c++)
                        zone[r * rn + c] = local[Math.Clamp((int)s.Zone[r * s.RoadN + c], 0, local.Length - 1)];
            }
            per[by * area.Nbx + bx] = (Cut(lc.Data, lc.Width, n), Cut(f.Get<bool>("water").Data, lc.Width, n), Cut(f.Get<bool>("buildings").Data, lc.Width, n),
                f.Has("canopy") ? Cut(f.Get<bool>("canopy").Data, lc.Width, n) : new bool[n * n], Cut(materials.ClassGrid(s.Material), s.N, n),
                Cut(s.Material, s.N, n), Cut(depth, n, n), zone);
        }
        var g = new CellGrids
        {
            Area = area,
            Landcover = new Grid<byte>(W, H), Water = new Grid<bool>(W, H), Buildings = new Grid<bool>(W, H), Canopy = new Grid<bool>(W, H),
            MaterialClass = new Grid<byte>(W, H), Rail = new Grid<bool>(W, H), Depth = Grid<float>.Filled(W, H, float.NaN),
            Zone = new Grid<ushort>(W, H), ZoneCodes = codes, Ok = new Grid<bool>(W, H),
        };
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
            {
                double x = area.Gx0 + c, y = area.Gy0 - r;
                int i = r * W + c;
                var (bx, by, row, col, ok) = area.Index(x, y, step, n);
                if (ok && per[by * area.Nbx + bx] is { } p)
                {
                    int k = row * n + col;
                    g.Landcover.Data[i] = p.Lc[k];
                    g.Water.Data[i] = p.Water[k];
                    g.Buildings.Data[i] = p.Bld[k];
                    g.Canopy.Data[i] = p.Canopy[k];
                    g.MaterialClass.Data[i] = p.Cls[k];
                    g.Rail.Data[i] = p.Mat[k] == rail && rail != 0;
                    g.Depth.Data[i] = p.Depth[k];
                    g.Ok.Data[i] = true;
                }
                if (rn > 0)
                {
                    var z = area.Index(x + 0.5, y - 0.5, rstep, rn);
                    if (z.Ok && per[z.By * area.Nbx + z.Bx] is { Zone.Length: > 0 } pz) g.Zone.Data[i] = pz.Zone[z.Row * rn + z.Col];
                }
            }
        return g;
    }

    /// <summary>A row-major grid of side <paramref name="from"/> as one of side <paramref name="to"/>: cut, or padded with zeros.</summary>
    static T[] Cut<T>(T[] a, int from, int to)
    {
        if (from == to) return a;
        var o = new T[to * to];
        for (int r = 0; r < Math.Min(from, to); r++) Array.Copy(a, r * from, o, r * to, Math.Min(from, to));
        return o;
    }
}
