using System.Text.Json.Nodes;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Regions;

/// <summary>
/// The zone (<c>GET_NAME_OF_ZONE</c>) of the whole range every 4 m, from the road scans' zone grids: one node per step
/// from the corner (X0, Y0) of the scanned blocks' rectangle moved onto the 1 m lattice of fractional part 0.5; node
/// (r, c) takes the zone of the nearest road-scan sample to (X0 + c step + 0.5, Y0 - r step - 0.5) in the block under
/// that point. <c>data/zones.grid</c>: <c>zone</c> (u16, index into <see cref="Codes"/>, 0 = none), <c>inside</c> (bool,
/// under a scanned block); meta <c>x0</c>, <c>y0</c>, <c>step</c>, <c>zones</c>, and <c>width</c> / <c>height</c>: the frame the
/// grid covers in metres (the scanned blocks' rectangle rounded to whole metres; the labels stay inside it). The region
/// colours and the labels read it.
/// </summary>
public sealed class ZoneGrid
{
    public const string FileName = "zones.grid";

    /// <summary>"" (no zone), then the zone codes present, in ordinal order.</summary>
    public required IReadOnlyList<string> Codes { get; init; }
    public required Grid<ushort> Zone { get; init; }
    public required Grid<bool> Inside { get; init; }
    public required double X0 { get; init; }
    public required double Y0 { get; init; }
    public required double Step { get; init; }
    /// <summary>The frame from (X0, Y0): width and height in whole metres (the grid has floor(width / step) columns).</summary>
    public required int FrameWidth { get; init; }
    public required int FrameHeight { get; init; }

    /// <summary>The zone grid of the blocks' road scans (blocks without one are left out).</summary>
    public static ZoneGrid Build(IReadOnlyList<ScanFile> scans)
    {
        var withRoads = scans.Where(s => s.HasRoads).ToList();
        if (withRoads.Count == 0) throw new ArgumentException("no road scans");
        int tx0 = withRoads.Min(s => s.Tx), ty0 = withRoads.Min(s => s.Ty);
        int nbx = (withRoads.Max(s => s.Tx) - tx0) / 4 + 1, nby = (withRoads.Max(s => s.Ty) - ty0) / 4 + 1;
        double X0 = WorldGrid.Left + tx0 * WorldGrid.TileSize, Y0 = WorldGrid.Top - ty0 * WorldGrid.TileSize;
        double gx0 = ScanArea.Lattice(X0), gy0 = ScanArea.Lattice(Y0);
        int rn = withRoads[0].RoadN;
        double step = withRoads[0].RoadStep;
        // every code a block names, then the sorted list; per block its zone indices as global ones
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in withRoads) foreach (var (code, _) in s.Zones) codes.Add(code);
        var all = new List<string> { "" };
        all.AddRange(codes);
        var index = all.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => (ushort)x.i, StringComparer.Ordinal);
        var blocks = new ushort[nbx * nby][];
        foreach (var s in withRoads)
        {
            var local = new ushort[s.Zones.Count + 1];
            for (int j = 0; j < s.Zones.Count; j++) local[j + 1] = index[s.Zones[j].Code];
            var z = new ushort[rn * rn];
            int m = Math.Min(rn, s.RoadN);
            for (int r = 0; r < m; r++)
                for (int c = 0; c < m; c++)
                    z[r * rn + c] = local[Math.Clamp((int)s.Zone[r * s.RoadN + c], 0, local.Length - 1)];
            blocks[(s.Ty - ty0) / 4 * nbx + (s.Tx - tx0) / 4] = z;
        }
        int W = (int)Math.Round(nbx * WorldGrid.BlockSize, MidpointRounding.ToEven), H = (int)Math.Round(nby * WorldGrid.BlockSize, MidpointRounding.ToEven);
        int wz = (int)Math.Floor(W / step), hz = (int)Math.Floor(H / step);
        var zone = new Grid<ushort>(wz, hz);
        var inside = new Grid<bool>(wz, hz);
        var used = new bool[all.Count];
        for (int r = 0; r < hz; r++)
            for (int c = 0; c < wz; c++)
            {
                double x = gx0 + c * step + 0.5, y = gy0 - r * step - 0.5;
                int ix = (int)Math.Floor((x - X0) / WorldGrid.BlockSize), iy = (int)Math.Floor((Y0 - y) / WorldGrid.BlockSize);
                bool ok = ix >= 0 && ix < nbx && iy >= 0 && iy < nby;
                int ixc = Math.Clamp(ix, 0, nbx - 1), iyc = Math.Clamp(iy, 0, nby - 1);
                var b = blocks[iyc * nbx + ixc];
                ok &= b is not null;
                if (!ok) continue;
                int cc = Math.Clamp((int)Math.Floor((x - (X0 + ixc * WorldGrid.BlockSize)) / step + 0.5), 0, rn - 1);
                int rr = Math.Clamp((int)Math.Floor((Y0 - iyc * WorldGrid.BlockSize - y) / step + 0.5), 0, rn - 1);
                var v = b![rr * rn + cc];
                zone[r, c] = v;
                inside[r, c] = true;
                used[v] = true;
            }
        // keep the codes that occur, in the same order
        var keep = new List<string> { "" };
        var remap = new ushort[all.Count];
        for (int i = 1; i < all.Count; i++)
            if (used[i]) { remap[i] = (ushort)keep.Count; keep.Add(all[i]); }
        for (int i = 0; i < zone.Count; i++) zone.Data[i] = remap[zone.Data[i]];
        return new ZoneGrid { Codes = keep, Zone = zone, Inside = inside, X0 = gx0, Y0 = gy0, Step = step, FrameWidth = W, FrameHeight = H };
    }

    public void Save(string path)
    {
        var f = new GridFile();
        f.Meta["x0"] = X0;
        f.Meta["y0"] = Y0;
        f.Meta["step"] = Step;
        f.Meta["width"] = FrameWidth;
        f.Meta["height"] = FrameHeight;
        f.Meta["zones"] = new JsonArray(Codes.Select(c => (JsonNode)c).ToArray());
        f.Add("zone", Zone);
        f.Add("inside", Inside);
        f.Save(path);
    }

    public static ZoneGrid Load(string path)
    {
        var f = GridFile.Load(path);
        return new ZoneGrid
        {
            Codes = f.Meta["zones"]!.AsArray().Select(n => n!.GetValue<string>()).ToList(),
            Zone = f.Get<ushort>("zone"),
            Inside = f.Get<bool>("inside"),
            X0 = f.Meta["x0"]!.GetValue<double>(),
            Y0 = f.Meta["y0"]!.GetValue<double>(),
            Step = f.Meta["step"]!.GetValue<double>(),
            FrameWidth = f.Meta["width"]!.GetValue<int>(),
            FrameHeight = f.Meta["height"]!.GetValue<int>(),
        };
    }
}
