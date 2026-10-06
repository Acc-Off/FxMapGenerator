using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Cells;

/// <summary>
/// The heights of a cell's 1 m grid and what the shade and the contours take from them:
/// <list type="bullet">
/// <item><see cref="Surface"/>: the blocks' surface heights (roofs and water surfaces included), holes taking the
///   nearest height.</item>
/// <item><see cref="Terrain"/> (contours): buildings (grown 3 m) and hard surfaces standing more than 8 m above a
///   151 m grey opening (viaducts) replaced by that opening, then a 71 m grey opening.</item>
/// <item><see cref="ShadeHeights"/> (shade): the surface with buildings (grown 3 m), tree and bush material (grown
///   1 m) and the viaducts cut out and filled from the ground around them: normalised Gaussian averages at growing
///   scales (1.5 .. 96 m), each filling the cells it reaches with enough known ground.</item>
/// <item><see cref="Light"/>: the hill shading of those heights from the style's lights.</item>
/// </list>
/// </summary>
public static class CellHeights
{
    public const double WideOpening = 151, TerrainOpening = 71, BuildingMargin = 3, ViaductHeight = 8;
    static readonly double[] FillScales = [1.5, 3, 6, 12, 24, 48, 96];

    /// <summary>The surface heights at the nodes (float32, the nearest height where none was taken); null when no block has heights.</summary>
    public static Grid<float>? Surface(CellArea area, IReadOnlyDictionary<BlockId, float[]> heights, int n)
    {
        if (heights.Count == 0) return null;
        var per = new float[]?[area.Nbx * area.Nby];
        foreach (var (b, v) in heights) per[(b.Ty - area.Ty0) / 4 * area.Nbx + (b.Tx - area.Tx0) / 4] = v;
        var dsm = Grid<float>.Filled(area.W, area.H, float.NaN);
        for (int r = 0; r < area.H; r++)
            for (int c = 0; c < area.W; c++)
            {
                var (bx, by, row, col, ok) = area.Index(area.Gx0 + c, area.Gy0 - r, 1.0, n);
                if (ok && per[by * area.Nbx + bx] is { } v) dsm[r, c] = v[row * n + col];
            }
        return DistanceTransform.FillNaN(dsm);
    }

    /// <summary>The 151 m grey opening of the surface (under everything narrower than that).</summary>
    public static Grid<float> Wide(Grid<float> dsm) => Morphology.GreyOpen(dsm, Kernel.CvEllipse((int)WideOpening, (int)WideOpening));

    /// <summary>Hard surfaces (ground class urban or the DEFAULT material) standing more than 8 m above the wide opening: tall buildings and viaducts.</summary>
    static bool Viaduct(Grid<float> dsm, Grid<float> wide, Grid<byte> lc, int i) =>
        (lc.Data[i] == (byte)GroundClass.Urban || lc.Data[i] == (byte)GroundClass.DefaultMaterial) && dsm.Data[i] - wide.Data[i] > ViaductHeight;

    /// <summary>The terrain the contours are drawn from.</summary>
    public static Grid<float> Terrain(Grid<float> dsm, Grid<float> wide, Grid<bool> buildings, Grid<byte> lc)
    {
        var cut = new Grid<bool>(dsm.Width, dsm.Height);
        for (int i = 0; i < cut.Count; i++) cut.Data[i] = buildings.Data[i] || Viaduct(dsm, wide, lc, i);
        var ground = dsm.Clone();
        if (cut.Data.Any(x => x))
        {
            int k = (int)Math.Round(BuildingMargin, MidpointRounding.ToEven) * 2 + 1;
            var m = Morphology.Dilate(cut, Kernel.CvEllipse(k, k));
            for (int i = 0; i < m.Count; i++) if (m.Data[i]) ground.Data[i] = wide.Data[i];
        }
        return Morphology.GreyOpen(ground, Kernel.CvEllipse((int)TerrainOpening, (int)TerrainOpening));
    }

    /// <summary>The heights the shade is lit from (float64).</summary>
    public static Grid<double> ShadeHeights(Grid<float> dsm, Grid<float> wide, Grid<bool> buildings, Grid<byte> lc, IParallelRunner? parallel = null)
    {
        var cut = Morphology.Dilate(buildings, Kernel.CvEllipse(7, 7));
        var veg = Morphology.Dilate(lc.Map(v => v == (byte)GroundClass.Vegetation), Kernel.CvEllipse(3, 3));
        for (int i = 0; i < cut.Count; i++) cut.Data[i] |= veg.Data[i] || Viaduct(dsm, wide, lc, i);
        return FillFromAround(dsm, cut, parallel);
    }

    /// <summary>The hole cells filled from the known ones around them (OpenCV Gaussian blurs of the known values and of the known mask).</summary>
    public static Grid<double> FillFromAround(Grid<float> z, Grid<bool> hole, IParallelRunner? parallel = null)
    {
        int n = z.Count;
        var est = new Grid<double>(z.Width, z.Height);
        var known = new bool[n];
        for (int i = 0; i < n; i++)
        {
            known[i] = !hole.Data[i];
            est.Data[i] = hole.Data[i] ? 0.0 : z.Data[i];
        }
        foreach (var sg in FillScales)
        {
            if (known.All(k => k)) break;
            var src = new Grid<double>(z.Width, z.Height);
            var kn = new Grid<double>(z.Width, z.Height);
            for (int i = 0; i < n; i++) { src.Data[i] = known[i] ? est.Data[i] : 0.0; kn.Data[i] = known[i] ? 1.0 : 0.0; }
            var num = Gaussian.OpenCv(src, sg, parallel);
            var den = Gaussian.OpenCv(kn, sg, parallel);
            var fresh = new List<int>();
            for (int i = 0; i < n; i++)
                if (!known[i] && den.Data[i] > 0.2) fresh.Add(i);
            foreach (var i in fresh)
            {
                est.Data[i] = num.Data[i] / den.Data[i];
                known[i] = true;
            }
        }
        if (!known.All(k => k))
        {
            var kept = new List<float>();
            for (int i = 0; i < n; i++) if (!hole.Data[i]) kept.Add(z.Data[i]);
            double mean = kept.Count > 0 ? Num.NpMeanF32(kept) : 0.0;
            for (int i = 0; i < n; i++) if (!known[i]) est.Data[i] = mean;
        }
        return est;
    }

    /// <summary>
    /// The light (0..1) of heights z from the style's lights (the weighted mean of one hillshade per light; flat ground =
    /// cos(90° - altitude)), and that flat value. A hillshade (ESRI's formula) smooths z by a Gaussian of the style's
    /// blur first, takes numpy's gradient (rows run north to south) and clips to 0..1.
    /// </summary>
    public static (Grid<double> Light, double Flat) Light(Grid<double> z, ShadeStyle sh, IParallelRunner? parallel = null)
    {
        double alt = sh.Altitude;
        double tot = 0;
        foreach (var (_, wgt) in sh.Lights) tot += wgt;
        var smooth = Gaussian.Scipy(z, sh.Blur, parallel);
        var (gr, gc) = Filters.Gradient(smooth);
        int n = z.Count;
        double zen = Num.Radians(90.0 - alt);
        double cosZen = Math.Cos(zen), sinZen = Math.Sin(zen);
        var slope = new double[n];
        var aspect = new double[n];
        for (int i = 0; i < n; i++)
        {
            double dzdx = gc.Data[i], dzdy = -gr.Data[i];
            slope[i] = Math.Atan(Num.NpHypot(dzdx, dzdy));
            aspect[i] = Math.Atan2(dzdy, -dzdx);
        }
        var sum = new double[n];
        bool first = true;
        foreach (var (az, wgt) in sh.Lights)
        {
            double azm = Num.Radians(360.0 - az + 90.0);
            for (int i = 0; i < n; i++)
            {
                double hs = cosZen * Math.Cos(slope[i]) + sinZen * Math.Sin(slope[i]) * Math.Cos(azm - aspect[i]);
                hs = hs < 0.0 ? 0.0 : hs > 1.0 ? 1.0 : hs;
                double v = wgt * hs;
                sum[i] = first ? 0 + v : sum[i] + v;
            }
            first = false;
        }
        var light = new Grid<double>(z.Width, z.Height);
        for (int i = 0; i < n; i++) light.Data[i] = sum[i] / tot;
        return (light, Math.Cos(Num.Radians(90.0 - alt)));
    }

    /// <summary>The light as kept with the cell: round(255 x light), 0..255 (numpy's <c>rint</c>: halves to even).</summary>
    public static Grid<byte> Quantize(Grid<double> light)
    {
        var o = new Grid<byte>(light.Width, light.Height);
        for (int i = 0; i < o.Count; i++)
        {
            double v = Math.Round(light.Data[i] * 255, MidpointRounding.ToEven);
            o.Data[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
        return o;
    }
}
