using FxMapGenerator.Core.Grids;

namespace FxMapGenerator.Core.Landcover;

/// <summary>
/// The terrain under a block, on the 3 x 3 mosaic of the block and its neighbours (the neighbours' samples count near
/// the edges; a missing neighbour is empty):
/// <list type="number">
/// <item>seeds: cells of natural ground or tarmac material that are not water</item>
/// <item>only the seeds on the terrain count: the surface is split wherever two neighbouring cells differ by more than
///   <see cref="WallRise"/> m per m (building walls; slopes and stairs stay joined), and the pieces that reach the
///   mosaic's edge or cover <see cref="TerrainMinArea"/> m² are the terrain; seeds on a roof (gravel, planters, stones)
///   drop out</item>
/// <item>the ground: every cell reachable from a seed walking between neighbouring cells whose heights differ by at most
///   <see cref="WalkRise"/> m per m (aprons, docks, plazas and podium decks far from any sample; raised roads via their
///   ramps); roofs are not reachable (walls); a walked piece counts only with a group of <see cref="MinSeedGroup"/>
///   seeds next to each other in it</item>
/// <item>the terrain height of every other cell is the height of the nearest ground cell</item>
/// </list>
/// </summary>
public static class GroundModel
{
    public const double WallRise = 2.5;
    public const double TerrainMinArea = 20000;
    public const double WalkRise = 0.15;

    /// <summary>The block's surface heights and those around it as one (3n)² grid, NaN where a neighbour is missing.</summary>
    public sealed class Mosaic
    {
        public required int N { get; init; }
        public required Grid<float> Heights { get; init; }
        /// <summary><see cref="Heights"/> with the missing parts filled from the nearest cell (the same grid when nothing is missing).</summary>
        public required Grid<float> Filled { get; init; }
        public required Grid<byte> MaterialClass { get; init; }
        public required Grid<bool> Water { get; init; }
        /// <summary>Cells of a block that is there.</summary>
        public required Grid<bool> Present { get; init; }

        /// <param name="around">The neighbours that are there, by (rows south, columns east) of -1..1.</param>
        public static Mosaic Of(BlockSurface centre, IReadOnlyDictionary<(int Di, int Dj), BlockSurface> around)
        {
            int n = centre.N, m = 3 * n;
            var heights = Grid<float>.Filled(m, m, float.NaN);
            var cls = new Grid<byte>(m, m);
            var water = new Grid<bool>(m, m);
            var present = new Grid<bool>(m, m);
            foreach (var ((di, dj), s) in around.Append(new KeyValuePair<(int, int), BlockSurface>((0, 0), centre)))
            {
                if (s.N != n) throw new InvalidDataException($"{s.Block.Name}: grid {s.N}, {centre.Block.Name} {n}");
                heights.Paste(s.Heights, (di + 1) * n, (dj + 1) * n);
                cls.Paste(s.MaterialClass, (di + 1) * n, (dj + 1) * n);
                water.Paste(s.Water, (di + 1) * n, (dj + 1) * n);
                present.Paste(Grid<bool>.Filled(n, n, true), (di + 1) * n, (dj + 1) * n);
            }
            return new Mosaic { N = n, Heights = heights, Filled = DistanceTransform.FillNaN(heights), MaterialClass = cls, Water = water, Present = present };
        }

        /// <summary>The centre block's part of a mosaic grid.</summary>
        public Grid<T> Centre<T>(Grid<T> g) => g.Crop(N, N, N, N);
    }

    /// <summary>
    /// A walked piece is ground only when it holds a group of at least this many seeds next to each other (8 around, in
    /// the piece), or all of it is seeds when it is smaller: a single seed cell (a plant or a patch of soil on an eave)
    /// does not walk the ground onto a roof.
    /// </summary>
    public const int MinSeedGroup = 2;

    /// <summary>The terrain heights (m) and the ground cells of the centre block.</summary>
    public static (Grid<float> Terrain, Grid<bool> Ground) Terrain(Mosaic mo, int tarmac, bool[] naturalGround)
    {
        var d = mo.Heights;
        int count = d.Count;
        var seeds = new Grid<bool>(d.Width, d.Height);
        for (int i = 0; i < count; i++)
        {
            byte c = mo.MaterialClass.Data[i];
            seeds.Data[i] = mo.Present.Data[i] && !mo.Water.Data[i] && (naturalGround[c] || c == tarmac) && !float.IsNaN(d.Data[i]);
        }
        var ground = Walk(d, TerrainSeeds(mo.Filled, seeds));
        var terrain = ground.Data.Any(g => g) ? DistanceTransform.Spread(d, ground) : d;
        return (mo.Centre(terrain), mo.Centre(ground));
    }

    /// <summary>The seeds on pieces of the surface split at walls that reach the grid's edge or cover <see cref="TerrainMinArea"/> m².</summary>
    public static Grid<bool> TerrainSeeds(Grid<float> d, Grid<bool> seeds)
    {
        var parts = Parts(d, (float)(WallRise * 1.0), (float)(WallRise * Math.Sqrt(2)));
        int w = d.Width, h = d.Height;
        var size = new int[d.Count];
        for (int i = 0; i < d.Count; i++) size[parts[i]]++;
        var keep = new bool[d.Count];
        for (int i = 0; i < d.Count; i++) keep[i] = size[i] * 1.0 * 1.0 >= TerrainMinArea;
        for (int x = 0; x < w; x++) { keep[parts[x]] = true; keep[parts[(h - 1) * w + x]] = true; }
        for (int y = 0; y < h; y++) { keep[parts[y * w]] = true; keep[parts[y * w + w - 1]] = true; }
        var o = new Grid<bool>(w, h);
        for (int i = 0; i < d.Count; i++) o.Data[i] = seeds.Data[i] && keep[parts[i]] && !float.IsNaN(d.Data[i]);
        return o;
    }

    /// <summary>
    /// The cells reachable from a seed over the surface in steps of at most <see cref="WalkRise"/> m per m (NaN cells are
    /// not walkable), in the pieces that hold a group of at least <see cref="MinSeedGroup"/> seeds next to each other (or
    /// all seeds when smaller).
    /// </summary>
    public static Grid<bool> Walk(Grid<float> d, Grid<bool> seeds)
    {
        const int group = MinSeedGroup;
        var parts = Parts(d, (float)(WalkRise * 1.0), (float)(WalkRise * Math.Sqrt(2)));
        int w = d.Width, h = d.Height, n = d.Count;
        var cells = new int[n];
        for (int i = 0; i < n; i++) if (!float.IsNaN(d.Data[i])) cells[parts[i]]++;
        // groups of seeds next to each other in the same piece
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        void Join(int a, int b)
        {
            if (!seeds.Data[b] || parts[a] != parts[b]) return;
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!seeds.Data[i] || float.IsNaN(d.Data[i])) continue;
                if (x + 1 < w) Join(i, i + 1);
                if (y + 1 < h)
                {
                    Join(i, i + w);
                    if (x + 1 < w) Join(i, i + w + 1);
                    if (x > 0) Join(i, i + w - 1);
                }
            }
        var size = new int[n];
        for (int i = 0; i < n; i++) if (seeds.Data[i] && !float.IsNaN(d.Data[i])) size[Find(i)]++;
        var best = new int[n];
        for (int i = 0; i < n; i++) if (seeds.Data[i] && !float.IsNaN(d.Data[i])) best[parts[i]] = Math.Max(best[parts[i]], size[Find(i)]);
        var o = new Grid<bool>(w, h);
        for (int i = 0; i < n; i++)
        {
            int p = parts[i];
            o.Data[i] = !float.IsNaN(d.Data[i]) && best[p] > 0 && best[p] >= Math.Min(group, cells[p]);
        }
        return o;
    }

    /// <summary>
    /// Pieces of the surface: neighbouring cells (8 around) are joined when both have a height and the heights differ by
    /// at most <paramref name="straight"/> (edge neighbours) or <paramref name="diagonal"/> (corner neighbours), the rise
    /// per m times the distance, compared in single precision. Returns each cell's piece as the
    /// index of one of its cells.
    /// </summary>
    static int[] Parts(Grid<float> d, float straight, float diagonal)
    {
        int w = d.Width, h = d.Height;
        var parent = new int[d.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        void Join(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }
        var v = d.Data;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float a = v[i];
                if (float.IsNaN(a)) continue;
                if (x + 1 < w) Try(i, i + 1, straight);
                if (y + 1 < h)
                {
                    Try(i, i + w, straight);
                    if (x + 1 < w) Try(i, i + w + 1, diagonal);
                    if (x > 0) Try(i, i + w - 1, diagonal);
                }
            }
        void Try(int i, int j, float limit)
        {
            float b = v[j];
            if (float.IsNaN(b)) return;
            float diff = MathF.Abs(b - v[i]);
            if (diff <= limit) Join(i, j);
        }
        var o = new int[d.Count];
        for (int i = 0; i < o.Length; i++) o[i] = Find(i);
        return o;
    }
}
