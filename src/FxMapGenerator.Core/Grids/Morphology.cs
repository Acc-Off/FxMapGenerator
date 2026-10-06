namespace FxMapGenerator.Core.Grids;

/// <summary>
/// Binary and grey morphology with a <see cref="Kernel"/>. Two conventions for the cells outside the grid, those of the
/// two libraries followed:
/// <list type="bullet">
/// <item>OpenCV's (<see cref="Dilate"/>, <see cref="Erode"/>, <see cref="Open"/>, the grey ones): outside cells never count,
///   so an erosion does not eat in from the edge</item>
/// <item>SciPy ndimage's (<see cref="ErodeEdgeOff"/>, <see cref="OpenEdgeOff"/>): outside cells are off, so an erosion eats
///   in from the edge. Dilations are the same in both</item>
/// </list>
/// Every kernel row is one run of cells, so a row is looked up with running counts (binary) or a running minimum /
/// maximum (grey), one pass per distinct run; for centred runs (ellipses, boxes) the grey ones widen one run into the
/// next. Grey values must not be NaN. Single-threaded: the stages run their units side by side.
/// </summary>
public static class Morphology
{
    /// <summary>On where any cell under the kernel is on (outside cells are off). <paramref name="iterations"/> repeats it.</summary>
    public static Grid<bool> Dilate(Grid<bool> src, Kernel k, int iterations = 1)
    {
        var g = src;
        for (int i = 0; i < iterations; i++) g = Binary(g, k, any: true, edgeOn: false);
        return iterations == 0 ? src.Clone() : g;
    }

    /// <summary>On where every cell under the kernel that lies inside the grid is on (OpenCV).</summary>
    public static Grid<bool> Erode(Grid<bool> src, Kernel k) => Binary(src, k, any: false, edgeOn: true);

    /// <summary>On where every cell under the kernel is on, outside cells counting as off (SciPy ndimage, border value 0).</summary>
    public static Grid<bool> ErodeEdgeOff(Grid<bool> src, Kernel k) => Binary(src, k, any: false, edgeOn: false);

    /// <summary>Erosion then dilation, OpenCV's edge (the edge does not erode).</summary>
    public static Grid<bool> Open(Grid<bool> src, Kernel k) => Dilate(Erode(src, k), k);

    /// <summary>Erosion then dilation, SciPy ndimage's edge (outside off: the edge erodes).</summary>
    public static Grid<bool> OpenEdgeOff(Grid<bool> src, Kernel k) => Dilate(ErodeEdgeOff(src, k), k);

    /// <summary>Dilation then erosion, SciPy ndimage's edge (<c>binary_closing</c>: cells near the edge may erode away).</summary>
    public static Grid<bool> CloseEdgeOff(Grid<bool> src, Kernel k) => ErodeEdgeOff(Dilate(src, k), k);

    static Grid<bool> Binary(Grid<bool> src, Kernel k, bool any, bool edgeOn)
    {
        int w = src.Width, h = src.Height;
        // prefix[y * (w + 1) + x] = cells on in row y before column x
        var prefix = new int[(w + 1) * h];
        for (int y = 0; y < h; y++)
        {
            int run = 0, o = y * (w + 1);
            for (int x = 0; x < w; x++)
            {
                prefix[o + x] = run;
                if (src.Data[y * w + x]) run++;
            }
            prefix[o + w] = run;
        }
        var dst = new Grid<bool>(w, h);
        var spans = k.Spans;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool result = !any;
                foreach (var (dy, x0, x1) in spans)
                {
                    int sy = y + dy;
                    if ((uint)sy >= (uint)h)
                    {
                        if (!any && !edgeOn) { result = false; break; }
                        continue;
                    }
                    int lo = x + x0, hi = x + x1;
                    if (!any && !edgeOn && (lo < 0 || hi >= w)) { result = false; break; }
                    lo = Math.Max(lo, 0);
                    hi = Math.Min(hi, w - 1);
                    if (lo > hi) continue;
                    int o = sy * (w + 1);
                    int on = prefix[o + hi + 1] - prefix[o + lo];
                    if (any && on > 0) { result = true; break; }
                    if (!any && on != hi - lo + 1) { result = false; break; }
                }
                dst.Data[y * w + x] = result;
            }
        }
        return dst;
    }

    /// <summary>Grey erosion: the minimum under the kernel, cells outside the grid not counting (OpenCV).</summary>
    public static Grid<float> GreyErode(Grid<float> src, Kernel k) => Grey(src, k, min: true);

    /// <summary>Grey dilation: the maximum under the kernel, cells outside the grid not counting (OpenCV).</summary>
    public static Grid<float> GreyDilate(Grid<float> src, Kernel k) => Grey(src, k, min: false);

    /// <summary>Grey opening: erosion then dilation (OpenCV's <c>morphologyEx(MORPH_OPEN)</c>).</summary>
    public static Grid<float> GreyOpen(Grid<float> src, Kernel k) => GreyDilate(GreyErode(src, k), k);

    static Grid<float> Grey(Grid<float> src, Kernel k, bool min)
    {
        if (src.Width >= 2 && k.Spans.All(s => s.X0 == -s.X1)) return GreyCentred(src, k, min);
        int w = src.Width, h = src.Height;
        float neutral = min ? float.PositiveInfinity : float.NegativeInfinity;
        var dst = Grid<float>.Filled(w, h, neutral);
        // the kernel rows grouped by their run: each source row is filtered once per distinct run
        var runs = k.Spans.GroupBy(s => (s.X0, s.X1)).Select(g => (g.Key.X0, g.Key.X1, Dys: g.Select(s => s.Dy).ToArray())).ToList();
        int pad = runs.Max(r => Math.Max(Math.Abs(r.X0), Math.Abs(r.X1)));
        var padded = new float[w + 2 * pad];
        var line = new float[w];
        var prefix = new float[w + 2 * pad];
        var suffix = new float[w + 2 * pad];
        for (int sy = 0; sy < h; sy++)
        {
            Array.Fill(padded, neutral);
            Array.Copy(src.Data, sy * w, padded, pad, w);
            foreach (var (x0, x1, dys) in runs)
            {
                SlidingExtreme(padded, pad, x0, x1, w, line, prefix, suffix, min);
                foreach (var dy in dys)
                {
                    int y = sy - dy;
                    if ((uint)y >= (uint)h) continue;
                    int o = y * w;
                    if (min) for (int x = 0; x < w; x++) { if (line[x] < dst.Data[o + x]) dst.Data[o + x] = line[x]; }
                    else for (int x = 0; x < w; x++) { if (line[x] > dst.Data[o + x]) dst.Data[o + x] = line[x]; }
                }
            }
        }
        return dst;
    }

    /// <summary>
    /// <see cref="Grey"/> for kernels whose rows are centred runs (-r..r: ellipses, boxes, the cross) on grids at least 2
    /// cells wide. The extremes of a source row over -r..r are made for r = 1, 2, ... one from the other: r = 1 from the
    /// row's cells x - 1, x, x + 1, then r from r - 1 at x - 1 and x + 1, which cover -r..r between them (at the grid's
    /// edge the one inside covers it all); several cells at a time. The same values as the general way.
    /// </summary>
    static Grid<float> GreyCentred(Grid<float> src, Kernel k, bool min)
    {
        int w = src.Width, h = src.Height;
        float neutral = min ? float.PositiveInfinity : float.NegativeInfinity;
        var dst = Grid<float>.Filled(w, h, neutral);
        int maxR = k.Spans.Max(s => s.X1);
        var rowsOf = new List<int>?[maxR + 1];
        foreach (var (dy, _, x1) in k.Spans) (rowsOf[x1] ??= new List<int>()).Add(dy);
        var a = new float[w + 2];                         // cells 1..w, a neutral cell at each end
        var b = new float[w + 2];
        for (int sy = 0; sy < h; sy++)
        {
            a[0] = a[w + 1] = b[0] = b[w + 1] = neutral;
            Array.Copy(src.Data, sy * w, a, 1, w);
            float[] cur = a, next = b;
            for (int r = 0; r <= maxR; r++)
            {
                if (r > 0)
                {
                    Widen(cur, next, w, min, withCentre: r == 1);
                    (cur, next) = (next, cur);
                }
                if (rowsOf[r] is not { } dys) continue;
                foreach (var dy in dys)
                {
                    int y = sy - dy;
                    if ((uint)y < (uint)h) Merge(dst.Data.AsSpan(y * w, w), cur.AsSpan(1, w), min);
                }
            }
        }
        return dst;
    }

    /// <summary>next[x] = the extreme of cur[x - 1], cur[x + 1] (and cur[x] too when <paramref name="withCentre"/>) for x in 1..w.</summary>
    static void Widen(float[] cur, float[] next, int w, bool min, bool withCentre)
    {
        int x = 1, n = System.Numerics.Vector<float>.Count;
        if (System.Numerics.Vector.IsHardwareAccelerated)
            for (; x + n <= w + 1; x += n)
            {
                var l = new System.Numerics.Vector<float>(cur, x - 1);
                var r = new System.Numerics.Vector<float>(cur, x + 1);
                var v = min ? System.Numerics.Vector.Min(l, r) : System.Numerics.Vector.Max(l, r);
                if (withCentre)
                {
                    var c = new System.Numerics.Vector<float>(cur, x);
                    v = min ? System.Numerics.Vector.Min(v, c) : System.Numerics.Vector.Max(v, c);
                }
                v.CopyTo(next, x);
            }
        for (; x <= w; x++)
        {
            float v = min ? Math.Min(cur[x - 1], cur[x + 1]) : Math.Max(cur[x - 1], cur[x + 1]);
            if (withCentre) v = min ? Math.Min(v, cur[x]) : Math.Max(v, cur[x]);
            next[x] = v;
        }
    }

    /// <summary>dst = the extreme of dst and line, cell by cell.</summary>
    static void Merge(Span<float> dst, ReadOnlySpan<float> line, bool min)
    {
        int x = 0, n = System.Numerics.Vector<float>.Count;
        if (System.Numerics.Vector.IsHardwareAccelerated)
            for (; x + n <= dst.Length; x += n)
            {
                var d = new System.Numerics.Vector<float>(dst[x..]);
                var l = new System.Numerics.Vector<float>(line[x..]);
                (min ? System.Numerics.Vector.Min(d, l) : System.Numerics.Vector.Max(d, l)).CopyTo(dst[x..]);
            }
        for (; x < dst.Length; x++) dst[x] = min ? Math.Min(dst[x], line[x]) : Math.Max(dst[x], line[x]);
    }

    /// <summary>
    /// line[x] = the minimum (or maximum) of padded[pad + x + x0 .. pad + x + x1] for x in 0..w-1: the van Herk / Gil-Werman
    /// scheme (running values from each block's start and end, blocks of the window's length).
    /// </summary>
    static void SlidingExtreme(float[] padded, int pad, int x0, int x1, int w, float[] line, float[] prefix, float[] suffix, bool min)
    {
        int len = x1 - x0 + 1;
        int start = pad + x0, end = pad + x1 + w - 1;           // the part of padded the windows cover
        for (int b = start; b <= end; b += len)
        {
            int e = Math.Min(b + len - 1, end);
            prefix[b] = padded[b];
            for (int i = b + 1; i <= e; i++) prefix[i] = min ? Math.Min(prefix[i - 1], padded[i]) : Math.Max(prefix[i - 1], padded[i]);
            suffix[e] = padded[e];
            for (int i = e - 1; i >= b; i--) suffix[i] = min ? Math.Min(suffix[i + 1], padded[i]) : Math.Max(suffix[i + 1], padded[i]);
        }
        for (int x = 0; x < w; x++)
        {
            int a = start + x, z = a + len - 1;
            line[x] = min ? Math.Min(suffix[a], prefix[z]) : Math.Max(suffix[a], prefix[z]);
        }
    }
}
