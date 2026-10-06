using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Regions;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.Core.Cells;

/// <summary>
/// The 1 m ground picture of a cell for a style with a ground raster (RGBA, row 0 = the grid's north row; transparent
/// on water and outside the blocks, where the background and the water layers show). Float32 steps as the maps were
/// first made (SciPy Gaussians of float32 grids, the colours as float32), rounded half to even at the end.
/// <list type="bullet">
/// <item><c>blend</c>: every node the colour of its ground paint; natural ground (not railway) blended among itself
///   by the style's method (none: not at all): Gaussian(colour x natural) / max(Gaussian(natural), 1e-6) of the
///   method's width. Before the blur, <c>patches</c> gives the natural patches of one colour under the style's area
///   (4-connected) the colour most of their natural neighbours have (again until none changes), and <c>brush</c> takes
///   the Kuwahara filter of the natural colours (of the four squares of side radius + 1 m with the node at a corner,
///   the mean of the one whose colours vary least; natural nodes only); after it, <c>detail</c> adds back its amount
///   of the unblurred colour minus the blurred.</item>
/// <item><c>regions</c>: the region colours sampled at the node (<see cref="RegionField.Sample"/>), then per tone
///   <c>colour += w (base + (tone - base) k - base)</c> with w the Gaussian of that tone's ground classes, then trees
///   <c>colour += w (base darkening - base)</c>.</item>
/// </list>
/// </summary>
public static class GroundRaster
{
    public static byte[] Make(CellGrids g, MapStyle style, RegionField? regions, IParallelRunner? parallel = null)
    {
        var raster = style.GroundRaster ?? throw new ArgumentException($"style {style.Id} has no ground raster");
        int w = g.Area.W, h = g.Area.H, n = w * h;
        var rail = new bool[n];
        for (int i = 0; i < n; i++) rail[i] = g.Rail.Data[i] && !g.Water.Data[i];
        var r = new float[n];
        var gg = new float[n];
        var b = new float[n];
        if (raster.Mode == "blend") Blend(g, style, raster, rail, r, gg, b, parallel);
        else Regions(g, raster, rail, regions ?? throw new ArgumentException("the regional ground needs the region colours"), r, gg, b, parallel);
        var o = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            o[4 * i] = ToByte(r[i]);
            o[4 * i + 1] = ToByte(gg[i]);
            o[4 * i + 2] = ToByte(b[i]);
            o[4 * i + 3] = g.Ok.Data[i] && !g.Water.Data[i] ? (byte)255 : (byte)0;
        }
        return o;
    }

    /// <summary>
    /// What the ground picture's node (<paramref name="col"/>, <paramref name="row"/>) of <paramref name="g"/> is made of,
    /// as the picture's rules make it (<see cref="Make"/>), the largest part first:
    /// <list type="bullet">
    /// <item><c>blend</c>: the kinds of ground (<c>ground</c>, the class name) whose paints give its colour: its own kind,
    ///   or for natural ground blended by the style's method the natural kinds within the method's reach by their Gaussian
    ///   weights (patches and brush change some colours before the blur; the shares are those of the kinds).</item>
    /// <item><c>regions</c>: the regions (<c>region</c>, the region's name) of the land around it by the region colours'
    ///   Gaussian weights (the style's sea region where no land is near: <paramref name="land"/> is the frame's 4 m land
    ///   of <paramref name="field"/>), then each tone (<c>tone</c>) and the trees (<c>trees</c>) with their weight
    ///   (0 to 1, how far the node takes the tone's colour or the darkening).</item>
    /// </list>
    /// Parts under half a percent are left out.
    /// </summary>
    public static IReadOnlyList<GroundPart> Explain(CellGrids g, MapStyle style, RegionField? field, Grid<bool>? land, int col, int row)
    {
        var raster = style.GroundRaster ?? throw new ArgumentException($"style {style.Id} has no ground raster");
        int w = g.Area.W, h = g.Area.H;
        col = Math.Clamp(col, 0, w - 1);
        row = Math.Clamp(row, 0, h - 1);
        bool Rail(int j) => g.Rail.Data[j] && !g.Water.Data[j];
        // Gaussian weights of the nodes (or cells) within the blur's reach that a test takes
        static Dictionary<int, double> Weigh(int w, int h, int col, int row, double sigma, Func<int, int?> keyOf)
        {
            var o = new Dictionary<int, double>();
            int rad = Math.Max(0, (int)(4 * sigma + 0.5));
            double two = 2 * Math.Max(sigma, 1e-9) * Math.Max(sigma, 1e-9);
            for (int y = Math.Max(0, row - rad); y <= Math.Min(h - 1, row + rad); y++)
                for (int x = Math.Max(0, col - rad); x <= Math.Min(w - 1, col + rad); x++)
                {
                    if (keyOf(y * w + x) is not { } k) continue;
                    double wt = sigma <= 0 ? (x == col && y == row ? 1 : 0) : Math.Exp(-((x - col) * (x - col) + (y - row) * (y - row)) / two);
                    if (wt > 0) o[k] = o.GetValueOrDefault(k) + wt;
                }
            return o;
        }
        static double KernelSum(double sigma)
        {
            int rad = Math.Max(0, (int)(4 * sigma + 0.5));
            double two = 2 * Math.Max(sigma, 1e-9) * Math.Max(sigma, 1e-9), sum = 0;
            for (int y = -rad; y <= rad; y++)
                for (int x = -rad; x <= rad; x++) sum += sigma <= 0 ? (x == 0 && y == 0 ? 1 : 0) : Math.Exp(-(x * x + y * y) / two);
            return sum;
        }
        var parts = new List<GroundPart>();
        var names = GroundClasses.Names;
        if (raster.Mode == "blend")
        {
            var colors = ClassColors(style);
            var natural = new bool[256];
            foreach (var name in raster.Natural) natural[names.ToList().IndexOf(name)] = true;
            int i = row * w + col, lc = g.Landcover.Data[i];
            bool Nat(int j) => natural[g.Landcover.Data[j]] && !Rail(j);
            if (raster.Method == "none" || !Nat(i))
                return [new GroundPart("ground", lc < names.Count ? names[lc] : names[0], lc < colors.Length ? colors[lc] : colors[0], 1)];
            var byClass = Weigh(w, h, col, row, raster.Width, j => Nat(j) ? g.Landcover.Data[j] : null);
            double total = byClass.Values.Sum();
            foreach (var (k, wt) in byClass.OrderByDescending(kv => kv.Value))
                if (wt / total >= 0.005) parts.Add(new GroundPart("ground", names[k], colors[k], wt / total));
            return parts;
        }
        if (field is null || land is null) throw new ArgumentException("the regional ground needs the region colours and their land");
        // the regions: around the field's 4 m cell of the node, as the field mixes them
        double x = g.Area.Gx0 + col, y = g.Area.Gy0 - row;
        int fc = Math.Clamp((int)Math.Floor((x - field.OriginX) / RegionField.Step), 0, land.Width - 1);
        int fr = Math.Clamp((int)Math.Floor((field.OriginY - y) / RegionField.Step), 0, land.Height - 1);
        double sigma = style.Regions.Blur / RegionField.Step;
        var byRegion = Weigh(land.Width, land.Height, fc, fr, sigma, j => land.Data[j] && field.Region.Data[j] != 255 ? field.Region.Data[j] : null);
        double landWeight = byRegion.Values.Sum();
        if (landWeight / KernelSum(sigma) < 1e-4)
        {
            var sea = style.Regions.Colors[Math.Max(0, style.Regions.IndexOf(style.Regions.Sea))];
            parts.Add(new GroundPart("region", sea.Name, sea.Color, 1));
        }
        else
            foreach (var (k, wt) in byRegion.OrderByDescending(kv => kv.Value))
                if (wt / landWeight >= 0.005) parts.Add(new GroundPart("region", style.Regions.Colors[k].Name, style.Regions.Colors[k].Color, wt / landWeight));
        // the tones and the trees: their weights at the node
        double tk = KernelSum(raster.ToneBlur);
        foreach (var tone in raster.Tones)
        {
            var classes = GroundRasterStyle.ToneClasses.First(t => t.Name == tone.Name).Classes.Select(c => (byte)names.ToList().IndexOf(c)).ToArray();
            double v = Weigh(w, h, col, row, raster.ToneBlur, j => classes.Contains(g.Landcover.Data[j]) && !Rail(j) ? 0 : null).GetValueOrDefault(0) / tk;
            if (v >= 0.005) parts.Add(new GroundPart("tone", tone.Name, tone.Color, Math.Min(1, v)));
        }
        byte veg = (byte)GroundClass.Vegetation;
        double tv = Weigh(w, h, col, row, raster.ToneBlur, j => g.Landcover.Data[j] == veg && !Rail(j) ? 0 : null).GetValueOrDefault(0) / tk;
        if (tv >= 0.005) parts.Add(new GroundPart("trees", "trees", default, Math.Min(1, tv)));
        return parts;
    }

    static byte ToByte(float v)
    {
        double x = Math.Round((double)v, MidpointRounding.ToEven);
        return (byte)(x < 0 ? 0 : x > 255 ? 255 : x);
    }

    /// <summary>The colour of every ground class: its ground paint's colour (the water paint for <c>water</c>, the <c>ground</c> paint when missing).</summary>
    public static Rgb[] ClassColors(MapStyle style)
    {
        var o = new Rgb[GroundClasses.Count];
        var fallback = style.Paint.Ground.TryGetValue("ground", out var gr) ? gr : default;
        for (int c = 0; c < o.Length; c++)
        {
            var key = style.GroundPaints[c];
            o[c] = key == "water" ? style.Paint.Water : style.Paint.Ground.TryGetValue(key, out var col) ? col : fallback;
        }
        return o;
    }

    static void Blend(CellGrids g, MapStyle style, GroundRasterStyle raster, bool[] rail, float[] r, float[] gg, float[] b, IParallelRunner? parallel)
    {
        int w = g.Area.W, h = g.Area.H, n = w * h;
        var colors = ClassColors(style);
        var naturalClasses = new bool[256];
        foreach (var name in raster.Natural) naturalClasses[GroundClasses.Names.ToList().IndexOf(name)] = true;
        var nat = new Grid<float>(w, h);
        var cr = new Grid<float>(w, h);
        var cg = new Grid<float>(w, h);
        var cb = new Grid<float>(w, h);
        var natural = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int lc = g.Landcover.Data[i];
            natural[i] = naturalClasses[lc] && !rail[i];
            var col = lc < colors.Length ? colors[lc] : colors[(int)GroundClass.None];
            r[i] = col.R; gg[i] = col.G; b[i] = col.B;
        }
        if (raster.Method == "none") return;
        if (raster.Method == "patches") Patches(natural, w, h, (int)Math.Ceiling(raster.MinArea), r, gg, b);
        else if (raster.Method == "brush") Kuwahara(natural, w, h, (int)Math.Round(raster.Radius, MidpointRounding.ToEven), r, gg, b);
        for (int i = 0; i < n; i++)
        {
            float m = natural[i] ? 1f : 0f;
            nat.Data[i] = m;
            cr.Data[i] = r[i] * m; cg.Data[i] = gg[i] * m; cb.Data[i] = b[i] * m;
        }
        var den = Gaussian.Scipy(nat, raster.Width, parallel);
        var mr = Gaussian.Scipy(cr, raster.Width, parallel);
        var mg = Gaussian.Scipy(cg, raster.Width, parallel);
        var mb = Gaussian.Scipy(cb, raster.Width, parallel);
        float k = raster.Method == "detail" ? (float)raster.Amount : 0f;
        for (int i = 0; i < n; i++)
        {
            if (!natural[i]) continue;
            float d = Math.Max(den.Data[i], 1e-6f);
            float br = mr.Data[i] / d, bg = mg.Data[i] / d, bb = mb.Data[i] / d;
            if (k != 0) { br += k * (r[i] - br); bg += k * (gg[i] - bg); bb += k * (b[i] - bb); }
            r[i] = br; gg[i] = bg; b[i] = bb;
        }
    }

    /// <summary>
    /// The natural patches of one colour smaller than <paramref name="minArea"/> nodes (4-connected) take the colour
    /// most of their natural neighbours of other colours have (a tie: the colour whose first neighbour comes first in
    /// row order); again until no patch changes (at most 8 rounds). A patch with no natural neighbours keeps its colour.
    /// </summary>
    static void Patches(bool[] natural, int w, int h, int minArea, float[] r, float[] gg, float[] b)
    {
        int n = w * h;
        var palette = new List<(float R, float G, float B)>();
        var key = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (!natural[i]) { key[i] = -1; continue; }
            var c = (r[i], gg[i], b[i]);
            int k = palette.IndexOf(c);
            if (k < 0) { k = palette.Count; palette.Add(c); }
            key[i] = k;
        }
        int nk = palette.Count;
        if (nk < 2 || minArea <= 1) return;
        var label = new int[n];
        var stack = new Stack<int>();
        for (int round = 0; round < 8; round++)
        {
            Array.Fill(label, -1);
            var size = new List<int>();
            for (int s = 0; s < n; s++)
            {
                if (key[s] < 0 || label[s] >= 0) continue;
                int id = size.Count, count = 0, want = key[s];
                label[s] = id;
                stack.Push(s);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    count++;
                    int x = i % w, y = i / w;
                    if (x > 0 && key[i - 1] == want && label[i - 1] < 0) { label[i - 1] = id; stack.Push(i - 1); }
                    if (x < w - 1 && key[i + 1] == want && label[i + 1] < 0) { label[i + 1] = id; stack.Push(i + 1); }
                    if (y > 0 && key[i - w] == want && label[i - w] < 0) { label[i - w] = id; stack.Push(i - w); }
                    if (y < h - 1 && key[i + w] == want && label[i + w] < 0) { label[i + w] = id; stack.Push(i + w); }
                }
                size.Add(count);
            }
            var small = new int[size.Count];
            int nSmall = 0;
            for (int c = 0; c < size.Count; c++) small[c] = size[c] < minArea ? nSmall++ : -1;
            if (nSmall == 0) break;
            var votes = new int[nSmall * nk];
            var firstSeen = new int[nSmall * nk];
            Array.Fill(firstSeen, int.MaxValue);
            for (int i = 0; i < n; i++)
            {
                if (key[i] < 0) continue;
                int si = small[label[i]];
                if (si < 0) continue;
                int x = i % w, y = i / w;
                for (int d = 0; d < 4; d++)
                {
                    int j = d switch { 0 => x > 0 ? i - 1 : -1, 1 => x < w - 1 ? i + 1 : -1, 2 => y > 0 ? i - w : -1, _ => y < h - 1 ? i + w : -1 };
                    if (j < 0 || key[j] < 0 || label[j] == label[i]) continue;
                    int v = si * nk + key[j];
                    votes[v]++;
                    if (j < firstSeen[v]) firstSeen[v] = j;
                }
            }
            var to = new int[nSmall];
            for (int si = 0; si < nSmall; si++)
            {
                int best = -1;
                for (int k = 0; k < nk; k++)
                {
                    int v = si * nk + k;
                    if (votes[v] == 0) continue;
                    int bv = si * nk + best;
                    if (best < 0 || votes[v] > votes[bv] || votes[v] == votes[bv] && firstSeen[v] < firstSeen[bv]) best = k;
                }
                to[si] = best;
            }
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (key[i] < 0) continue;
                int si = small[label[i]];
                if (si < 0 || to[si] < 0) continue;
                key[i] = to[si];
                changed = true;
            }
            if (!changed) break;
        }
        for (int i = 0; i < n; i++)
            if (key[i] >= 0) (r[i], gg[i], b[i]) = palette[key[i]];
    }

    /// <summary>
    /// The Kuwahara filter of the natural nodes' colours: of the four squares of side <paramref name="radius"/> + 1
    /// nodes with the node at a corner (cut at the grid's edge), each with the mean and the variance (summed over the
    /// channels) of its natural nodes, the node takes the mean of the square that varies least (a tie: north-west,
    /// north-east, south-west, south-east). Other nodes keep their colours.
    /// </summary>
    static void Kuwahara(bool[] natural, int w, int h, int radius, float[] r, float[] gg, float[] b)
    {
        if (radius < 1) return;
        int W = w + 1, size = W * (h + 1);
        // summed-area tables of the natural count, the channels and their squares
        var sn = new double[size];
        var ch = new[] { r, gg, b };
        var s = new double[3][];
        var q = new double[3][];
        for (int c = 0; c < 3; c++) { s[c] = new double[size]; q[c] = new double[size]; }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, o = (y + 1) * W + x + 1;
                bool m = natural[i];
                sn[o] = (m ? 1 : 0) + sn[o - 1] + sn[o - W] - sn[o - W - 1];
                for (int c = 0; c < 3; c++)
                {
                    double v = m ? ch[c][i] : 0;
                    s[c][o] = v + s[c][o - 1] + s[c][o - W] - s[c][o - W - 1];
                    q[c][o] = v * v + q[c][o - 1] + q[c][o - W] - q[c][o - W - 1];
                }
            }
        double Sum(double[] t, int x0, int y0, int x1, int y1) => t[(y1 + 1) * W + x1 + 1] - t[y0 * W + x1 + 1] - t[(y1 + 1) * W + x0] + t[y0 * W + x0];
        var o3 = new[] { new float[w * h], new float[w * h], new float[w * h] };
        Span<double> mean = stackalloc double[3];
        Span<double> best = stackalloc double[3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!natural[i]) continue;
                double bestVar = double.MaxValue;
                for (int quad = 0; quad < 4; quad++)
                {
                    int x0 = quad % 2 == 0 ? Math.Max(0, x - radius) : x, x1 = quad % 2 == 0 ? x : Math.Min(w - 1, x + radius);
                    int y0 = quad < 2 ? Math.Max(0, y - radius) : y, y1 = quad < 2 ? y : Math.Min(h - 1, y + radius);
                    double cnt = Sum(sn, x0, y0, x1, y1);
                    if (cnt < 0.5) continue;
                    double v = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        mean[c] = Sum(s[c], x0, y0, x1, y1) / cnt;
                        v += Sum(q[c], x0, y0, x1, y1) / cnt - mean[c] * mean[c];
                    }
                    if (v < bestVar - 1e-9)
                    {
                        bestVar = v;
                        mean.CopyTo(best);
                    }
                }
                for (int c = 0; c < 3; c++) o3[c][i] = (float)best[c];
            }
        for (int i = 0; i < w * h; i++)
            if (natural[i]) { r[i] = o3[0][i]; gg[i] = o3[1][i]; b[i] = o3[2][i]; }
    }

    static void Regions(CellGrids g, GroundRasterStyle raster, bool[] rail, RegionField field, float[] r, float[] gg, float[] b, IParallelRunner? parallel)
    {
        int w = g.Area.W, h = g.Area.H, n = w * h;
        var br = new float[n];
        var bg = new float[n];
        var bb = new float[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                var (sr, sg, sb) = field.Sample(g.Area.Gx0 + x, g.Area.Gy0 - y);
                br[i] = sr; bg[i] = sg; bb[i] = sb;
                r[i] = sr; gg[i] = sg; b[i] = sb;
            }
        var names = GroundClasses.Names.ToList();
        foreach (var tone in raster.Tones)
        {
            var classes = GroundRasterStyle.ToneClasses.First(t => t.Name == tone.Name).Classes.Select(c => (byte)names.IndexOf(c)).ToArray();
            var m = new Grid<float>(w, h);
            for (int i = 0; i < n; i++) m.Data[i] = classes.Contains(g.Landcover.Data[i]) && !rail[i] ? 1f : 0f;
            var wt = Gaussian.Scipy(m, raster.ToneBlur, parallel);
            float k = (float)tone.Strength, tr = tone.Color.R, tg = tone.Color.G, tb = tone.Color.B;
            for (int i = 0; i < n; i++)
            {
                float v = wt.Data[i];
                r[i] += v * ((br[i] + (tr - br[i]) * k) - br[i]);
                gg[i] += v * ((bg[i] + (tg - bg[i]) * k) - bg[i]);
                b[i] += v * ((bb[i] + (tb - bb[i]) * k) - bb[i]);
            }
        }
        byte veg = (byte)GroundClass.Vegetation;
        var tree = new Grid<float>(w, h);
        for (int i = 0; i < n; i++) tree.Data[i] = g.Landcover.Data[i] == veg && !rail[i] ? 1f : 0f;
        var tw = Gaussian.Scipy(tree, raster.ToneBlur, parallel);
        float dark = (float)raster.TreeDarkening;
        for (int i = 0; i < n; i++)
        {
            float v = tw.Data[i];
            r[i] += v * (br[i] * dark - br[i]);
            gg[i] += v * (bg[i] * dark - bg[i]);
            b[i] += v * (bb[i] * dark - bb[i]);
        }
    }
}

/// <summary>
/// A part of a ground picture's node (<see cref="GroundRaster.Explain"/>): a kind of ground, a region, a tone or the
/// trees (<paramref name="Kind"/>), which one (<paramref name="Id"/>), its colour and its share (a kind or region: of
/// the node's colour; a tone or the trees: its weight).
/// </summary>
public sealed record GroundPart(string Kind, string Id, Rgb Color, double Share);
