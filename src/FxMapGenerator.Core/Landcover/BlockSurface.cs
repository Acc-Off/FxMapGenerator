using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Landcover;

/// <summary>
/// What the landcover reads of one block, on the scan's grid (n x n every step m, row 0 = the north edge): the surface
/// heights with their holes filled from the nearest measured cell, the material class of every cell, the open water,
/// the on-road samples and the tree canopy. The landcover of a block reads these of the block and of its 8 neighbours.
/// </summary>
public sealed class BlockSurface
{
    /// <summary>Open water of a water surface covers at least this many cells.</summary>
    public const int WaterSurfaceMinCells = 4;
    /// <summary>Open water of the water-collision probe (rivers, pools, fountains) covers at least this many m².</summary>
    public const double WaterProbeMinArea = 30;
    /// <summary>The water is open where the ground hit is not above it by more than this (m).</summary>
    public const float WaterTolerance = 0.05f;
    /// <summary>Canopy: foliage hits at least this high above the ground hit (m), grown by <see cref="CanopyGrow"/> m, pieces of at least <see cref="CanopyMinArea"/> m².</summary>
    public const float CanopyMinHeight = 2.0f;
    public const double CanopyGrow = 2.0, CanopyMinArea = 10;

    public required BlockId Block { get; init; }
    public required int N { get; init; }
    public required double X0 { get; init; }
    public required double Y0 { get; init; }
    public required double Step { get; init; }
    /// <summary>Surface heights (m), holes filled.</summary>
    public required Grid<float> Heights { get; init; }
    /// <summary>Material class (<see cref="Materials"/> index) of the ground hit.</summary>
    public required Grid<byte> MaterialClass { get; init; }
    /// <summary>
    /// Open water: a water surface (a water height where the ground hit is not above it, pieces of at least 4 cells), or the
    /// water-collision probe's hit likewise (pieces of at least 30 m²). Bridges and piers over water are not water.
    /// </summary>
    public required Grid<bool> Water { get; init; }
    /// <summary>The on-road samples on this grid (false without a road scan).</summary>
    public required Grid<bool> OnRoad { get; init; }
    /// <summary>Tree canopy, null when the scan has no canopy probe.</summary>
    public Grid<bool>? Canopy { get; init; }

    /// <summary>
    /// The surface of a block from its scan (ground pass, and the road pass for the on-road samples) and its surface
    /// heights (the same n x n grid; holes are filled here).
    /// </summary>
    public static BlockSurface From(BlockId block, ScanFile scan, Grid<float> heights, Materials materials)
    {
        if (!scan.HasGround) throw new InvalidDataException($"{block.Name}: the scan has no ground pass");
        int n = scan.N;
        if (heights.Width != n || heights.Height != n) throw new InvalidDataException($"{block.Name}: heights {heights.Width} x {heights.Height}, scan {n} x {n}");
        double step = scan.Step;
        var hz = scan.HitZ;
        var quad = new Grid<bool>(n, n);
        var probe = new Grid<bool>(n, n);
        for (int i = 0; i < n * n; i++)
        {
            float w = scan.Water[i], p = scan.Probe[i], z = hz[i];
            float top = z - WaterTolerance;
            quad.Data[i] = !float.IsNaN(w) && (float.IsNaN(z) || w >= top);
            probe.Data[i] = !float.IsNaN(p) && (float.IsNaN(z) || p >= top);
        }
        quad = Components.KeepAtLeast(quad, WaterSurfaceMinCells, 4);
        probe = Components.KeepAtLeast(probe, Math.Max(1, Num.RoundToInt(WaterProbeMinArea / (step * step))), 4);
        var water = new Grid<bool>(n, n);
        for (int i = 0; i < n * n; i++) water.Data[i] = quad.Data[i] || probe.Data[i];

        return new BlockSurface
        {
            Block = block, N = n, X0 = scan.X0, Y0 = scan.Y0, Step = step,
            Heights = DistanceTransform.FillNaN(heights),
            MaterialClass = new Grid<byte>(n, n, materials.ClassGrid(scan.Material)),
            Water = water,
            OnRoad = OnRoadOf(scan, n, step),
            Canopy = CanopyOf(scan, n, step),
        };
    }

    /// <summary>The surface heights of a height grid, in single precision (as the landcover keeps them).</summary>
    public static Grid<float> HeightsOf(Satellite.HeightGrid grid) => new(grid.N, grid.N, grid.Values.Select(v => (float)v).ToArray());

    /// <summary>The on-road samples (every pstep m) repeated onto the n x n grid; nothing beyond them.</summary>
    static Grid<bool> OnRoadOf(ScanFile scan, int n, double step)
    {
        var g = new Grid<bool>(n, n);
        if (!scan.HasRoads) return g;
        int pn = scan.OnRoadN, rep = Math.Max(1, Num.RoundToInt(scan.OnRoadStep / step));
        for (int r = 0; r < n && r / rep < pn; r++)
            for (int c = 0; c < n && c / rep < pn; c++)
                g.Data[r * n + c] = scan.OnRoad[(r / rep) * pn + c / rep] != 0;
        return g;
    }

    /// <summary>Canopy from the canopy probe (flags 256): foliage hits well above the ground hit, grown to the drawn crown, small pieces dropped.</summary>
    static Grid<bool>? CanopyOf(ScanFile scan, int n, double step)
    {
        if (scan.Pflags2 != 256 || scan.Canopy is null) return null;
        var f = scan.Canopy;
        var hz = scan.HitZ;
        var m = new Grid<bool>(n, n);
        for (int i = 0; i < n * n; i++)
        {
            float above = f[i] - hz[i];
            m.Data[i] = !float.IsNaN(f[i]) && (float.IsNaN(hz[i]) || above >= CanopyMinHeight);
        }
        int k = Num.RoundToInt(CanopyGrow / step) * 2 + 1;
        m = Morphology.Dilate(m, Kernel.CvEllipse(k, k));
        return Components.KeepAtLeast(m, Num.RoundToInt(CanopyMinArea / (step * step)), 8);
    }
}
