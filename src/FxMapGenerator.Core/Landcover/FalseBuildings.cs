using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Scan;

namespace FxMapGenerator.Core.Landcover;

/// <summary>
/// Building pieces that are not buildings, taken off after the building rule (<see cref="LandcoverBuilder"/>):
/// <list type="bullet">
/// <item>rock faces: a candidate piece whose cells are at least <see cref="RockNaturalShare"/> the default material (a
///   hit without a material name, such as a rock prop) or natural ground, with walls (an edge whose outside, in the
///   block, is at least <see cref="WallDrop"/> m lower) on less than <see cref="RockWallShare"/> of its edges, and whose
///   ring of <see cref="RockRing"/> m (not water) is at least <see cref="RockRingShare"/> natural ground or the default
///   material. Such a face is steeper than the walk, so it is not ground, but a real building has walls or stands among
///   paving.</item>
/// <item>parking pieces (<see cref="GarageRule"/>, joined over corners) with, within <see cref="GarageRing"/> m, at least
///   <see cref="GarageLevelShare"/> natural ground no more than <see cref="GarageLevelBelow"/> m below the piece's median
///   height (a hill, a bank, the land side of a quay: the rule's reference ground was pulled down by the slope or the sea
///   next to it), or at least <see cref="GarageWaterShare"/> water (a bridge, a pier, a dam).</item>
/// </list>
/// The rings are measured on the 3 x 3 mosaic (blocks that are there) around the piece's cells in the chessboard
/// distance. A cell of a piece taken off stays a building when a kept piece of the other rule covers it.
/// </summary>
public static class FalseBuildings
{
    public const double RockNaturalShare = 0.5;
    public const double RockWallShare = 0.6;
    public const double RockRingShare = 0.9;
    public const double RockRing = 5;
    public const float WallDrop = 2.5f;
    public const double GarageRing = 10;
    public const float GarageLevelBelow = 3;
    public const double GarageLevelShare = 0.15;
    public const double GarageWaterShare = 0.2;

    /// <param name="Buildings">The kept pieces of both rules.</param>
    /// <param name="RockCells">Cells taken off as rock faces (and not kept by the parking rule).</param>
    /// <param name="GarageCells">Cells of the parking rule kept.</param>
    /// <param name="GarageOffCells">Cells of the parking rule taken off (and not kept as a candidate piece).</param>
    public sealed record Result(Grid<bool> Buildings, int RockCells, int GarageCells, int GarageOffCells);

    /// <param name="pieces">The candidate pieces the building rule kept (cells of the centre block), before the parking rule.</param>
    /// <param name="garage">The parking rule's cells of the centre block.</param>
    /// <param name="heights">The centre block's surface heights (holes filled).</param>
    public static Result Apply(IReadOnlyList<List<int>> pieces, Grid<bool> garage, Grid<float> heights, Grid<byte> materialClass,
        GroundModel.Mosaic mosaic, double step, Materials materials)
    {
        int n = mosaic.N;
        var natural = GroundClasses.NaturalGroundOf(materials);
        int dflt = materials.IndexOf("default");
        var ring = new Ring(mosaic);
        var keep = new Grid<bool>(n, n);
        var rock = new Grid<bool>(n, n);
        var garageOff = new Grid<bool>(n, n);
        var inPiece = new bool[n * n];
        int rockRing = Num.RoundToInt(RockRing / step), garageRing = Num.RoundToInt(GarageRing / step);

        foreach (var piece in pieces)
        {
            int wild = 0;
            foreach (var i in piece) if (materialClass.Data[i] == dflt || natural[materialClass.Data[i]]) wild++;
            bool off = false;
            if (wild / (double)piece.Count >= RockNaturalShare)
            {
                foreach (var i in piece) inPiece[i] = true;
                var (edges, walls) = Walls(piece, inPiece, heights.Data, n);
                foreach (var i in piece) inPiece[i] = false;
                if (edges > 0 && walls / (double)edges < RockWallShare)
                {
                    int around = 0, wet = 0, wildAround = 0;
                    foreach (var k in ring.Around(piece, rockRing))
                    {
                        around++;
                        byte c = mosaic.MaterialClass.Data[k];
                        if (mosaic.Water.Data[k]) wet++;
                        else if (natural[c] || c == dflt) wildAround++;
                    }
                    off = around - wet > 0 && wildAround / (double)(around - wet) >= RockRingShare;
                }
            }
            var into = off ? rock : keep;
            foreach (var i in piece) into.Data[i] = true;
        }

        var labels = Components.Label(garage, 8);
        var cells = new List<int>[labels.Count + 1];
        for (int i = 0; i < n * n; i++)
        {
            int l = labels.Label.Data[i];
            if (l != 0) (cells[l] ??= new List<int>()).Add(i);
        }
        var kept = new Grid<bool>(n, n);
        for (int l = 1; l <= labels.Count; l++)
        {
            var piece = cells[l];
            var hs = piece.Select(i => heights.Data[i]).Order().ToList();
            float level = hs[hs.Count / 2];
            int around = 0, wet = 0, atLevel = 0;
            foreach (var k in ring.Around(piece, garageRing))
            {
                around++;
                if (mosaic.Water.Data[k]) wet++;
                else if (natural[mosaic.MaterialClass.Data[k]] && mosaic.Filled.Data[k] >= level - GarageLevelBelow) atLevel++;
            }
            bool off = around > 0 && (atLevel / (double)around >= GarageLevelShare || wet / (double)around >= GarageWaterShare);
            var into = off ? garageOff : kept;
            foreach (var i in piece) into.Data[i] = true;
        }

        int rockCells = 0, garageCells = 0, garageOffCells = 0;
        for (int i = 0; i < n * n; i++)
        {
            if (kept.Data[i]) { keep.Data[i] = true; garageCells++; }
            if (keep.Data[i]) continue;
            if (rock.Data[i]) rockCells++;
            if (garageOff.Data[i]) garageOffCells++;
        }
        return new Result(keep, rockCells, garageCells, garageOffCells);
    }

    /// <summary>A piece's edges (a cell of it next to one outside, in the block) and walls among them (the outside at least <see cref="WallDrop"/> m lower).</summary>
    static (int Edges, int Walls) Walls(List<int> piece, bool[] inPiece, float[] z, int n)
    {
        int edges = 0, walls = 0;
        foreach (var i in piece)
        {
            int r = i / n, c = i % n;
            if (r > 0) Edge(i - n);
            if (r < n - 1) Edge(i + n);
            if (c > 0) Edge(i - 1);
            if (c < n - 1) Edge(i + 1);
            void Edge(int j)
            {
                if (inPiece[j]) return;
                edges++;
                if (z[i] - z[j] >= WallDrop) walls++;
            }
        }
        return (edges, walls);
    }

    /// <summary>The cells of the mosaic around a piece of the centre block.</summary>
    sealed class Ring(GroundModel.Mosaic mosaic)
    {
        readonly int _n = mosaic.N, _m = 3 * mosaic.N;
        readonly int[] _mine = new int[9 * mosaic.N * mosaic.N], _seen = new int[9 * mosaic.N * mosaic.N];
        int _mark;

        /// <summary>
        /// The cells of blocks that are there within <paramref name="radius"/> cells (chessboard) of the piece, not in it,
        /// each once (from the piece's cells with an edge neighbour outside it).
        /// </summary>
        public List<int> Around(List<int> piece, int radius)
        {
            int n = _n, m = _m, mark = ++_mark;
            foreach (var i in piece) _mine[(i / n + n) * m + i % n + n] = mark;
            var o = new List<int>();
            foreach (var i in piece)
            {
                int r = i / n + n, c = i % n + n, j = r * m + c;
                if (_mine[j - 1] == mark && _mine[j + 1] == mark && _mine[j - m] == mark && _mine[j + m] == mark) continue;
                for (int rr = Math.Max(0, r - radius); rr <= Math.Min(m - 1, r + radius); rr++)
                    for (int cc = Math.Max(0, c - radius); cc <= Math.Min(m - 1, c + radius); cc++)
                    {
                        int k = rr * m + cc;
                        if (_mine[k] == mark || _seen[k] == mark || !mosaic.Present.Data[k]) continue;
                        _seen[k] = mark;
                        o.Add(k);
                    }
            }
            return o;
        }
    }
}
