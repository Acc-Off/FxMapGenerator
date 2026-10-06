using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.Vectors;

namespace FxMapGenerator.Core.Cells;

/// <summary>A contour line: its level (m) and x, y pairs (m, rounded to 0.01).</summary>
public sealed record ContourLine(double Level, double[] Points);

/// <summary>
/// One vector layer of a cell: its kind (<c>ground</c>, <c>canopy</c>, <c>water</c>, <c>sea</c>, <c>building</c>,
/// <c>contour</c>, <c>rail</c>), its paint (the ground paint, the building colour, <c>sand</c> / <c>rock</c> for the sea
/// bed, else the kind), the depth band of a sea layer, the style values it was made for (<see cref="Set"/>; null when no
/// style value changes it), how many 1 m cells it covers, and its outlines or lines.
/// </summary>
public sealed record CellLayer(string Kind, string Paint, string? Set, int Cells, IReadOnlyList<Ring>? Rings, IReadOnlyList<ContourLine>? Lines, int Band = -1);

/// <summary>
/// The vector layers of a cell's 1 m grids for the styles of the maps drawn: every mask traced to outlines
/// (<see cref="Rings.Trace"/>, the grid's node (r, c) standing for the cell of side 1 m around (Gx0 + c, Gy0 - r)), in
/// this order: ground (one layer per ground paint, water left out), canopy, water, the sea's depth bands (sand bed, then
/// rock bed, per band list), buildings (one layer per building colour), contours, railway. Layers that depend on style
/// values are made once per distinct set of those values (see <see cref="Sets"/>).
/// </summary>
public static class CellLayers
{
    /// <summary>A layer's mask before it is traced (and the lines of a contour layer).</summary>
    public sealed record Masked(string Kind, string Paint, string? Set, Grid<bool>? Mask, IReadOnlyList<ContourLine>? Lines, int Band = -1);

    /// <summary>The keys of the style values a layer group depends on (a style draws the layers of its own keys).</summary>
    public static class Sets
    {
        public static string Ground(MapStyle s) => "ground:" + string.Join(",", s.GroundPaints);
        public static string Canopy(MapStyle s) => "canopy:" + Fmt(s.Canopy.MinArea);
        public static string Sea(MapStyle s) => "sea:" + string.Join(",", s.Sea.Bands.Select(Fmt)) + ";" + Fmt(s.Sea.RockMinArea);
        public static string Buildings(MapStyle s) => "buildings:" + Hash(new JsonObject
        {
            ["buildings"] = Canonical(s.Source["buildings"]),
            ["zones"] = Canonical(s.Source["regions"]?["zones"]),
        });
        public static string? Contours(MapStyle s) => s.Contours is null ? null : "contours:" + Fmt(s.Contours.Interval);

        static string Fmt(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        static string Hash(JsonNode n) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n.ToJsonString())))[..12].ToLowerInvariant();

        /// <summary>A copy with the object keys in ordinal order.</summary>
        static JsonNode? Canonical(JsonNode? n) => n switch
        {
            JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Canonical(kv.Value)))),
            JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
            null => null,
            _ => n.DeepClone(),
        };
    }

    /// <summary>The masks (and contour lines) of the layers the styles need.</summary>
    public static List<Masked> Masks(CellGrids g, IReadOnlyList<MapStyle> styles, Grid<float>? terrain, Materials materials, IParallelRunner? parallel = null)
    {
        int n = g.Area.W * g.Area.H;
        var o = new List<Masked>();
        var water = g.Water;
        var bld = new Grid<bool>(g.Area.W, g.Area.H);
        for (int i = 0; i < n; i++) bld.Data[i] = g.Buildings.Data[i] && !water.Data[i];
        // ground: one layer per ground paint of each distinct table
        foreach (var style in Distinct(styles, Sets.Ground))
        {
            var set = Sets.Ground(style);
            var keys = style.Paint.GroundOrder.Concat(style.GroundPaints.Where(k => k != "water").Distinct()).Distinct().ToList();
            foreach (var key in keys)
            {
                var classes = Enumerable.Range(0, style.GroundPaints.Count).Where(c => style.GroundPaints[c] == key).Select(c => (byte)c).ToArray();
                if (classes.Length == 0) continue;
                var m = new Grid<bool>(g.Area.W, g.Area.H);
                bool any = false;
                for (int i = 0; i < n; i++)
                    if (!water.Data[i] && Array.IndexOf(classes, g.Landcover.Data[i]) >= 0) { m.Data[i] = true; any = true; }
                if (any) o.Add(new Masked("ground", key, set, m, null));
            }
        }
        // canopy: water and buildings cut out, pieces smaller than the style's minimum dropped
        foreach (var style in Distinct(styles, Sets.Canopy))
        {
            var m = new Grid<bool>(g.Area.W, g.Area.H);
            for (int i = 0; i < n; i++) m.Data[i] = g.Canopy.Data[i] && !water.Data[i] && !bld.Data[i];
            if (style.Canopy.MinArea > 0 && m.Data.Any(x => x))
            {
                var l = Components.Label(m, 8);
                for (int i = 0; i < n; i++) if (m.Data[i] && l.Area[l.Label.Data[i]] < style.Canopy.MinArea) m.Data[i] = false;
            }
            if (m.Data.Any(x => x)) o.Add(new Masked("canopy", "canopy", Sets.Canopy(style), m, null));
        }
        if (water.Data.Any(x => x))
        {
            o.Add(new Masked("water", "water", null, water.Clone(), null));
            foreach (var style in Distinct(styles, Sets.Sea)) o.AddRange(Sea(g, water, style, materials));
        }
        foreach (var style in Distinct(styles, Sets.Buildings)) o.AddRange(Buildings(g, bld, style));
        if (terrain is not null)
            foreach (var style in Distinct(styles, s => Sets.Contours(s) ?? ""))
                if (style.Contours is not null)
                    o.Add(new Masked("contour", "contour", Sets.Contours(style), null, Contour(terrain, g.Area, style.Contours.Interval, parallel)));
        var rail = new Grid<bool>(g.Area.W, g.Area.H);
        bool anyRail = false;
        for (int i = 0; i < n; i++) if (g.Rail.Data[i] && !water.Data[i]) { rail.Data[i] = true; anyRail = true; }
        if (anyRail) o.Add(new Masked("rail", "rail", null, rail, null));
        return o;
    }

    /// <summary>The first style of each distinct key, in the given order.</summary>
    static IEnumerable<MapStyle> Distinct(IReadOnlyList<MapStyle> styles, Func<MapStyle, string> key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in styles) if (seen.Add(key(s))) yield return s;
    }

    /// <summary>
    /// The sea layers of one band list: the depth (0 where unknown) through a 3 x 3 median, cut at the band edges; the
    /// bed is rock where the material is rock (8-connected pieces smaller than the style's minimum go to sand), else
    /// sand. Sand layers of every band first, then rock.
    /// </summary>
    static IEnumerable<Masked> Sea(CellGrids g, Grid<bool> water, MapStyle style, Materials materials)
    {
        int w = g.Area.W, h = g.Area.H, n = w * h;
        var dep = new Grid<float>(w, h);
        for (int i = 0; i < n; i++) dep.Data[i] = float.IsFinite(g.Depth.Data[i]) ? g.Depth.Data[i] : 0f;
        dep = Filters.Median3(dep);
        var bands = style.Sea.Bands;
        var band = new int[n];
        for (int i = 0; i < n; i++)
        {
            double d = dep.Data[i];
            int k = 0;
            while (k < bands.Count && d >= bands[k]) k++;
            band[i] = k;
        }
        int rockClass = materials.IndexOf("rock");
        var rock = new Grid<bool>(w, h);
        for (int i = 0; i < n; i++) rock.Data[i] = water.Data[i] && g.MaterialClass.Data[i] == rockClass;
        if (rock.Data.Any(x => x))
        {
            var l = Components.Label(rock, 8);
            for (int i = 0; i < n; i++) if (rock.Data[i] && l.Area[l.Label.Data[i]] < style.Sea.RockMinArea) rock.Data[i] = false;
        }
        var set = Sets.Sea(style);
        foreach (var (bed, isRock) in new[] { ("sand", false), ("rock", true) })
            for (int b = 0; b <= bands.Count; b++)
            {
                var m = new Grid<bool>(w, h);
                bool any = false;
                for (int i = 0; i < n; i++)
                    if (water.Data[i] && rock.Data[i] == isRock && band[i] == b) { m.Data[i] = true; any = true; }
                if (any) yield return new Masked("sea", bed, set, m, null, b);
            }
    }

    /// <summary>
    /// One layer per building colour (in ordinal order of the colour names): each 8-connected piece of the building mask
    /// takes the colour of the zone at its centroid (rounded half to even) by the style's building table.
    /// </summary>
    static IEnumerable<Masked> Buildings(CellGrids g, Grid<bool> bld, MapStyle style)
    {
        int w = g.Area.W, h = g.Area.H;
        var l = Components.Label(bld, 8);
        if (l.Count == 0) yield break;
        var cen = Components.Centroids(l);
        var keys = new string[l.Count + 1];
        for (int k = 1; k <= l.Count; k++)
        {
            int rr = Math.Clamp((int)Math.Round(cen[k].Y, MidpointRounding.ToEven), 0, h - 1);
            int cc = Math.Clamp((int)Math.Round(cen[k].X, MidpointRounding.ToEven), 0, w - 1);
            var zone = g.ZoneCodes[g.Zone[rr, cc]];
            keys[k] = style.Buildings.KeyOf(zone, style.Regions);
        }
        var set = Sets.Buildings(style);
        foreach (var key in keys.Skip(1).Distinct().Order(StringComparer.Ordinal))
        {
            var m = new Grid<bool>(w, h);
            for (int i = 0; i < m.Count; i++) m.Data[i] = l.Label.Data[i] != 0 && keys[l.Label.Data[i]] == key;
            yield return new Masked("building", key, set, m, null);
        }
    }

    /// <summary>
    /// Contour lines every <paramref name="interval"/> m of the terrain smoothed by a 3 m Gaussian (float32): the
    /// levels from ceil(min / interval) x interval below the maximum (in float32), the lines of each level (contourpy's
    /// serial order), the grid's node (r, c) at (Gx0 + c, Gy0 - r); lines shorter than 10 m dropped.
    /// </summary>
    public static List<ContourLine> Contour(Grid<float> terrain, CellArea area, double interval, IParallelRunner? parallel = null)
    {
        const double MinLength = 10.0;
        var z = Gaussian.Scipy(terrain, 3.0, parallel);
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        foreach (var v in z.Data)
        {
            if (float.IsNaN(v)) continue;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        var zd = z.Data.Select(v => (double)v).ToArray();
        float fi = (float)interval;
        float start = MathF.Ceiling(lo / fi) * fi;
        var o = new List<ContourLine>();
        long count = (long)Math.Ceiling(((double)hi - start) / interval);
        double x0 = area.Gx0 - 0.5 + 0.5, y0 = area.Gy0 + 0.5 - 0.5;
        for (long k = 0; k < count; k++)
        {
            float lev = k == 0 ? start : start + k * fi;                                    // numpy's arange: the first value is the start (-0 stays -0)
            if (!(lev < hi)) break;
            foreach (var line in Contours.Lines(zd, z.Width, z.Height, lev))
            {
                int m = line.Length / 2;
                if (m < 2) continue;
                var pts = new double[line.Length];
                for (int i = 0; i < m; i++)
                {
                    pts[2 * i] = x0 + line[2 * i];
                    pts[2 * i + 1] = y0 - line[2 * i + 1];
                }
                double length = 0;
                for (int i = 0; i + 1 < m; i++) length += Num.NpHypot(pts[2 * i + 2] - pts[2 * i], pts[2 * i + 3] - pts[2 * i + 1]);
                if (length < MinLength) continue;
                for (int i = 0; i < pts.Length; i++) pts[i] = Num.NpRound(pts[i], 2);        // numpy floats: numpy's rounding
                o.Add(new ContourLine(lev, pts));
            }
        }
        return o;
    }

    /// <summary>The layers: every mask traced, the contour lines as they are.</summary>
    public static List<CellLayer> Trace(IReadOnlyList<Masked> masks, CellArea area, IParallelRunner? parallel = null)
    {
        var o = new CellLayer[masks.Count];
        double x0 = area.Gx0 - 0.5, y0 = area.Gy0 + 0.5;
        void One(int k)
        {
            var m = masks[k];
            o[k] = m.Mask is null
                ? new CellLayer(m.Kind, m.Paint, m.Set, 0, null, m.Lines, m.Band)
                : new CellLayer(m.Kind, m.Paint, m.Set, m.Mask.Data.Count(x => x), Rings.Trace(m.Mask, x0, y0), null, m.Band);
        }
        if (parallel is not null) parallel.ForEach(Enumerable.Range(0, masks.Count).ToList(), One);
        else for (int k = 0; k < masks.Count; k++) One(k);
        return o.ToList();
    }
}
