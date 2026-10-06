using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.RoadGraph;

namespace FxMapGenerator.Core.Landcover;

/// <summary>
/// Parking structures: tarmac standing well above the ground around it is a garage top, ramp or deck, and counts as a
/// building, unless it lies mostly under a street, major road or highway of the road graph (a viaduct) or mostly on water
/// (a ship). On the 3 x 3 mosaic (a 151 m opening needs the margin):
/// <list type="number">
/// <item>the reference ground is the grey opening of the surface with a 151 m ellipse</item>
/// <item>tarmac cells, not water, at least <see cref="MinHeight"/> m above it; opened with a 3 x 3 square (the mosaic's
///   edge erodes); pieces joined over corners</item>
/// <item>a piece of at least <see cref="MinArea"/> m² counts unless a quarter of it lies under the roads' lines (drawn
///   with their width, at least 6 m), or half of the 20 m ring around it is water</item>
/// </list>
/// A surface lot on a raised platform is caught too: it cannot be told from a garage top by height or material.
/// </summary>
public static class GarageRule
{
    public const double OpenSize = 151;
    public const float MinHeight = 6;
    public const double MinArea = 300;
    public const double RoadShare = 0.25;
    public const double WaterShare = 0.5;
    public const int RingCells = 20;
    public const double MinRoadWidth = 6;

    /// <summary>The road graph's lines the rule reads (streets, major roads, highways), each with its drawing width and bounds.</summary>
    public sealed class Roads
    {
        internal readonly List<(IReadOnlyList<P2> Points, double Width, double X0, double X1, double Y0, double Y1)> Lines = new();

        public int Count => Lines.Count;

        public static Roads Of(IEnumerable<RoadGraphFile.Road> roads)
        {
            var o = new Roads();
            foreach (var r in roads)
            {
                if (r.Class is not (RoadClass.Street or RoadClass.Major or RoadClass.Highway) || r.Points.Count < 2) continue;
                o.Lines.Add((r.Points, Math.Max(MinRoadWidth, r.Width), r.Points.Min(p => p.X), r.Points.Max(p => p.X), r.Points.Min(p => p.Y), r.Points.Max(p => p.Y)));
            }
            return o;
        }

        public static Roads Read(string path) => Of(RoadGraphFile.Read(path));
    }

    /// <summary>The garage cells of the centre block.</summary>
    public static Grid<bool> Mask(GroundModel.Mosaic mo, int tarmac, Roads roads, double x0, double y0, double step)
    {
        var d = mo.Filled;
        int size = d.Width;
        int k = Num.RoundToInt(OpenSize / step) | 1;
        var candidate = new Grid<bool>(size, size);
        int r0 = size, r1 = -1, c0 = size, c1 = -1;
        for (int i = 0; i < candidate.Count; i++)
        {
            if (mo.MaterialClass.Data[i] != tarmac || !mo.Present.Data[i] || mo.Water.Data[i]) continue;
            candidate.Data[i] = true;
            int r = i / size, c = i % size;
            r0 = Math.Min(r0, r); r1 = Math.Max(r1, r);
            c0 = Math.Min(c0, c); c1 = Math.Max(c1, c);
        }
        var o = new Grid<bool>(size, size);
        if (r1 < 0) return mo.Centre(o);
        // The opening at a cell reads the surface within twice the kernel's reach (erosion, then dilation), so on the
        // tarmac cells it comes out the same on the part of the mosaic within that reach of them.
        int reach = 2 * (k / 2);
        int wr0 = Math.Max(0, r0 - reach), wr1 = Math.Min(size - 1, r1 + reach), wc0 = Math.Max(0, c0 - reach), wc1 = Math.Min(size - 1, c1 + reach);
        var reference = Morphology.GreyOpen(d.Crop(wr0, wc0, wr1 - wr0 + 1, wc1 - wc0 + 1), Kernel.CvEllipse(k, k));
        var elev = new Grid<bool>(size, size);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                int i = r * size + c;
                if (!candidate.Data[i]) continue;
                float above = d.Data[i] - reference[r - wr0, c - wc0];
                elev.Data[i] = above >= MinHeight;
            }
        elev = Morphology.OpenEdgeOff(elev, Kernel.Rect(3, 3));
        var labels = Components.Label(elev, 8);
        if (labels.Count == 0) return mo.Centre(o);
        var via = RoadMask(roads, x0 - mo.N * step, y0 + mo.N * step, size, step);

        // the cells of each piece, in row order
        var cells = new List<int>[labels.Count + 1];
        for (int i = 0; i < elev.Count; i++)
        {
            int l = labels.Label.Data[i];
            if (l != 0) (cells[l] ??= new List<int>()).Add(i);
        }
        for (int l = 1; l <= labels.Count; l++)
        {
            var piece = cells[l];
            if (labels.Area[l] * step * step < MinArea) continue;
            int under = piece.Count(i => via.Data[i]);
            if ((double)under / piece.Count >= RoadShare) continue;
            var (ring, wet) = Ring(piece, mo.Water, size);
            if (ring > 0 && (double)wet / ring >= WaterShare) continue;
            foreach (var i in piece) o.Data[i] = true;
        }
        return mo.Centre(o);
    }

    /// <summary>Cells within <see cref="RingCells"/> steps (edge neighbour to edge neighbour) of the piece, not in it: how many, and how many of them are water.</summary>
    static (int Ring, int Wet) Ring(List<int> piece, Grid<bool> water, int size)
    {
        int r0 = int.MaxValue, r1 = -1, c0 = int.MaxValue, c1 = -1;
        foreach (var i in piece)
        {
            int r = i / size, c = i % size;
            r0 = Math.Min(r0, r); r1 = Math.Max(r1, r);
            c0 = Math.Min(c0, c); c1 = Math.Max(c1, c);
        }
        int wr0 = Math.Max(0, r0 - RingCells), wr1 = Math.Min(size - 1, r1 + RingCells);
        int wc0 = Math.Max(0, c0 - RingCells), wc1 = Math.Min(size - 1, c1 + RingCells);
        int ww = wc1 - wc0 + 1, wh = wr1 - wr0 + 1;
        var local = new Grid<bool>(ww, wh);
        foreach (var i in piece) local[i / size - wr0, i % size - wc0] = true;
        var grown = Morphology.Dilate(local, Kernel.Cross3(), RingCells);
        int ring = 0, wet = 0;
        for (int r = 0; r < wh; r++)
            for (int c = 0; c < ww; c++)
                if (grown[r, c] && !local[r, c])
                {
                    ring++;
                    if (water[wr0 + r, wc0 + c]) wet++;
                }
        return (ring, wet);
    }

    /// <summary>The cells of a size x size grid (north-west corner x0, y0; step m) under the roads' lines.</summary>
    public static Grid<bool> RoadMask(Roads roads, double x0, double y0, int size, double step)
    {
        var m = new Grid<bool>(size, size);
        double x1 = x0 + size * step, y1 = y0 - size * step;
        foreach (var (points, width, rx0, rx1, ry0, ry1) in roads.Lines)
        {
            if (rx1 < x0 || rx0 > x1 || ry1 < y1 || ry0 > y0) continue;
            var pts = points.Select(p => ((int)((p.X - x0) / step), (int)((y0 - p.Y) / step))).ToList();
            ThickLines.Polyline(m, pts, Math.Max(1, Num.RoundToInt(width / step)));
        }
        return m;
    }
}
