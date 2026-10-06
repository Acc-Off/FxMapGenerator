using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Scan;

namespace FxMapGenerator.Core.Landcover;

/// <summary>
/// The landcover of one block from its surface and those of its neighbours (<see cref="BlockSurface"/>) and the road
/// graph's lines: the ground class of every cell, open water, buildings and tree canopy.
/// <list type="bullet">
/// <item>ground class: the class of the material hit (<see cref="GroundClasses"/>), sand within <see cref="BeachDistance"/> m
///   of open water as beach; then a 5 x 5 majority vote (no hit only where nothing else is around) and pieces under
///   <see cref="MinPieceArea"/> m² merged into what surrounds them</item>
/// <item>buildings: cells more than <see cref="BuildingHeight"/> m above the terrain (<see cref="GroundModel"/>) on a
///   material that can be a roof (not natural ground, water or no hit), or on natural material off the walked ground;
///   not water; opened with a 3 x 3 square; pieces of at least <see cref="BuildingMinArea"/> m² unless more than half of
///   a piece is on road (on-road samples or tarmac: raised roads, bridges) or less than 5 % of it is roof material (rock
///   pillars, boulders); and the parking structures (<see cref="GarageRule"/>); then the rock faces and the parking
///   pieces on a slope or over water taken off (<see cref="FalseBuildings"/>)</item>
/// </list>
/// </summary>
public static class LandcoverBuilder
{
    public const double BeachDistance = 20;
    public const int VoteSize = 5;
    public const double MinPieceArea = 50;
    public const float BuildingHeight = 3;
    public const double BuildingMinArea = 30;
    public const double RoadPieceShare = 0.5;
    public const double RoofMaterialShare = 0.05;

    /// <param name="HeightAboveTerrain">Each cell's surface height minus the terrain height under it (m).</param>
    /// <param name="GarageCells">Cells of the parking rule that are buildings.</param>
    /// <param name="RockCells">Cells taken off as rock faces (<see cref="FalseBuildings"/>).</param>
    /// <param name="GarageOffCells">Cells of the parking rule taken off (<see cref="FalseBuildings"/>).</param>
    public sealed record Result(Grid<byte> Landcover, Grid<byte> Surface, Grid<bool> Water, Grid<bool> Buildings, Grid<float> HeightAboveTerrain,
        Grid<bool>? Canopy, int GarageCells, int RockCells, int GarageOffCells);

    /// <param name="around">The neighbours that are there, by (rows south, columns east) of -1..1.</param>
    /// <param name="roads">The road graph's lines for the parking rule.</param>
    public static Result Compute(BlockSurface centre, IReadOnlyDictionary<(int Di, int Dj), BlockSurface> around, GarageRule.Roads roads, Materials materials)
    {
        int n = centre.N;
        double step = centre.Step;
        var cls = centre.MaterialClass;
        var water = centre.Water;

        // ground classes
        var ofMaterial = GroundClasses.OfMaterialClasses(materials);
        var surface = cls.Map(c => ofMaterial[c]);
        var lc = surface.Clone();
        if (water.Data.Any(w => w))
        {
            var dist = DistanceTransform.Distance(water);
            for (int i = 0; i < lc.Count; i++)
                if (surface.Data[i] == (byte)GroundClass.Sand && dist[i] * step <= BeachDistance) lc.Data[i] = (byte)GroundClass.Beach;
        }
        lc = Majority(lc, VoteSize, GroundClasses.Count);
        lc = MergeSmall(lc, Num.RoundToInt(MinPieceArea / (step * step)), GroundClasses.Count);

        // buildings
        int tarmac = materials.IndexOf("tarmac");
        var naturalGround = GroundClasses.NaturalGroundOf(materials);
        var notRoof = GroundClasses.NotRoofOf(materials);
        var mosaic = GroundModel.Mosaic.Of(centre, around);
        var (terrain, ground) = GroundModel.Terrain(mosaic, tarmac, naturalGround);
        var dsm = centre.Heights;
        var aboveTerrain = new Grid<float>(n, n);
        var cand = new Grid<bool>(n, n);
        for (int i = 0; i < cand.Count; i++)
        {
            byte c = cls.Data[i];
            float above = dsm.Data[i] - terrain.Data[i];
            aboveTerrain.Data[i] = above;
            bool roofable = !notRoof[c], naturalRoof = naturalGround[c] && !ground.Data[i];
            cand.Data[i] = above > BuildingHeight && (roofable || naturalRoof) && !water.Data[i];
        }
        cand = Morphology.Open(cand, Kernel.Rect(3, 3));
        var pieces = Components.Label(cand, 4);
        var cells = new List<int>[pieces.Count + 1];
        for (int i = 0; i < cand.Count; i++)
        {
            int l = pieces.Label.Data[i];
            if (l != 0) (cells[l] ??= new List<int>()).Add(i);
        }
        var kept = new List<List<int>>();
        double minCells = BuildingMinArea / (step * step);
        for (int l = 1; l <= pieces.Count; l++)
        {
            var piece = cells[l];
            if (piece.Count < minCells) continue;
            double onRoad = (double)piece.Count(i => centre.OnRoad.Data[i]) / piece.Count;
            double onTarmac = (double)piece.Count(i => cls.Data[i] == tarmac) / piece.Count;
            if (Math.Max(onRoad, onTarmac) > RoadPieceShare) continue;
            if ((double)piece.Count(i => !notRoof[cls.Data[i]]) / piece.Count < RoofMaterialShare) continue;
            kept.Add(piece);
        }
        var garage = GarageRule.Mask(mosaic, tarmac, roads, centre.X0, centre.Y0, step);
        var f = FalseBuildings.Apply(kept, garage, dsm, cls, mosaic, step, materials);
        return new Result(lc, surface, water, f.Buildings, aboveTerrain, centre.Canopy, f.GarageCells, f.RockCells, f.GarageOffCells);
    }

    /// <summary>
    /// Every cell takes the class (1 and up) with the most cells in the k x k window around it (the grid mirrored at its
    /// edges), the cell's own class counting half a cell more; the lower class wins a tie; no class around: 0.
    /// </summary>
    public static Grid<byte> Majority(Grid<byte> g, int k, int classes)
    {
        if (k <= 1) return g.Clone();
        var best = new Grid<byte>(g.Width, g.Height);
        var bestVotes = new int[g.Count];                 // twice the votes: 2 per cell in the window, 1 more for the cell itself
        for (int c = 1; c < classes; c++)
        {
            var m = g.Map(v => v == c);
            if (!m.Data.Any(x => x)) continue;
            var count = Filters.BoxCountReflect(m, k);
            for (int i = 0; i < g.Count; i++)
            {
                int votes = 2 * count.Data[i] + (m.Data[i] ? 1 : 0);
                if (votes > bestVotes[i]) { best.Data[i] = (byte)c; bestVotes[i] = votes; }
            }
        }
        return best;
    }

    /// <summary>
    /// Pieces (edge neighbours) of fewer than <paramref name="minCells"/> cells take the most common other class of the
    /// cells around them (8 around, the lower class on a tie); classes in order, pieces in the order of their first cell;
    /// at most <paramref name="passes"/> rounds, until nothing changes.
    /// </summary>
    public static Grid<byte> MergeSmall(Grid<byte> g, int minCells, int classes, int passes = 2)
    {
        var o = g.Clone();
        int w = o.Width, h = o.Height;
        var stamp = new int[o.Count];
        int mark = 0;
        var tally = new int[256];
        for (int pass = 0; pass < passes; pass++)
        {
            int changed = 0;
            for (int c = 0; c < classes; c++)
            {
                var m = o.Map(v => v == c);
                if (!m.Data.Any(x => x)) continue;
                var pieces = Components.Label(m, 4);
                List<int>[]? cells = null;
                for (int l = 1; l <= pieces.Count; l++)
                {
                    if (pieces.Area[l] >= minCells) continue;
                    if (cells is null)
                    {
                        cells = new List<int>[pieces.Count + 1];
                        for (int i = 0; i < o.Count; i++)
                        {
                            int x = pieces.Label.Data[i];
                            if (x != 0 && pieces.Area[x] < minCells) (cells[x] ??= new List<int>()).Add(i);
                        }
                    }
                    var piece = cells[l];
                    mark++;
                    foreach (var i in piece) stamp[i] = mark;           // the piece itself is not its ring
                    Array.Clear(tally);
                    int ring = 0;
                    foreach (var i in piece)
                    {
                        int r = i / w, col = i % w;
                        for (int dr = -1; dr <= 1; dr++)
                            for (int dc = -1; dc <= 1; dc++)
                            {
                                int rr = r + dr, cc = col + dc;
                                if ((uint)rr >= (uint)h || (uint)cc >= (uint)w) continue;
                                int j = rr * w + cc;
                                if (stamp[j] == mark || stamp[j] == -mark) continue;
                                stamp[j] = -mark;
                                if (o.Data[j] == c) continue;
                                tally[o.Data[j]]++;
                                ring++;
                            }
                    }
                    if (ring == 0) continue;
                    int top = 0;
                    for (int v = 1; v < tally.Length; v++) if (tally[v] > tally[top]) top = v;
                    foreach (var i in piece) o.Data[i] = (byte)top;
                    changed++;
                }
            }
            if (changed == 0) break;
        }
        return o;
    }
}
