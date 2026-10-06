namespace FxMapGenerator.Core.Grids;

/// <summary>Window counts and sums.</summary>
public static class Filters
{
    /// <summary>
    /// The number of cells on in the k x k window around every cell (k odd), the grid mirrored at its edges with the
    /// edge cell repeated (<c>…cba|abc…</c>, OpenCV's BORDER_REFLECT). The same as filtering the mask with a box of ones.
    /// </summary>
    public static Grid<int> BoxCountReflect(Grid<bool> mask, int k)
    {
        if (k < 1 || k % 2 == 0) throw new ArgumentOutOfRangeException(nameof(k), "odd window size");
        int w = mask.Width, h = mask.Height, r = k / 2;
        // column sums over the k rows around each row, then row sums over k columns
        var col = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = 0;
                for (int d = -r; d <= r; d++)
                    if (mask.Data[Reflect(y + d, h) * w + x]) s++;
                col[y * w + x] = s;
            }
        var o = new Grid<int>(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = 0;
                for (int d = -r; d <= r; d++) s += col[y * w + Reflect(x + d, w)];
                o.Data[y * w + x] = s;
            }
        return o;
    }

    /// <summary>
    /// The median of the 3 x 3 window around every cell, the grid mirrored at its edges with the edge cell repeated
    /// (SciPy's <c>median_filter(size=3)</c>: the fifth of the nine values in order). Values must not be NaN.
    /// </summary>
    public static Grid<float> Median3(Grid<float> src)
    {
        int w = src.Width, h = src.Height;
        var o = new Grid<float>(w, h);
        Span<float> v = stackalloc float[9];
        for (int y = 0; y < h; y++)
        {
            int ya = Reflect(y - 1, h) * w, yb = y * w, yc = Reflect(y + 1, h) * w;
            for (int x = 0; x < w; x++)
            {
                int xa = Reflect(x - 1, w), xc = Reflect(x + 1, w);
                v[0] = src.Data[ya + xa]; v[1] = src.Data[ya + x]; v[2] = src.Data[ya + xc];
                v[3] = src.Data[yb + xa]; v[4] = src.Data[yb + x]; v[5] = src.Data[yb + xc];
                v[6] = src.Data[yc + xa]; v[7] = src.Data[yc + x]; v[8] = src.Data[yc + xc];
                v.Sort();
                o.Data[yb + x] = v[4];
            }
        }
        return o;
    }

    /// <summary>
    /// numpy's <c>gradient(z, 1.0)</c> of a float64 grid: (d/drow, d/dcol), central differences <c>(b - a) / 2</c> inside
    /// and one-sided differences at the first and last row / column.
    /// </summary>
    public static (Grid<double> Rows, Grid<double> Cols) Gradient(Grid<double> z)
    {
        int w = z.Width, h = z.Height;
        var gr = new Grid<double>(w, h);
        var gc = new Grid<double>(w, h);
        var d = z.Data;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                gr.Data[i] = h < 2 ? 0 : y == 0 ? (d[i + w] - d[i]) / 1.0 : y == h - 1 ? (d[i] - d[i - w]) / 1.0 : (d[i + w] - d[i - w]) / 2.0;
                gc.Data[i] = w < 2 ? 0 : x == 0 ? (d[i + 1] - d[i]) / 1.0 : x == w - 1 ? (d[i] - d[i - 1]) / 1.0 : (d[i + 1] - d[i - 1]) / 2.0;
            }
        return (gr, gc);
    }

    /// <summary>An index mirrored into 0..n-1 with the edge repeated: -1 -> 0, -2 -> 1, n -> n-1, n+1 -> n-2.</summary>
    public static int Reflect(int i, int n)
    {
        if (n == 1) return 0;
        while ((uint)i >= (uint)n) i = i < 0 ? -i - 1 : 2 * n - i - 1;
        return i;
    }
}
