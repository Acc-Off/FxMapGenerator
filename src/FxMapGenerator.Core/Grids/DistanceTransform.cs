namespace FxMapGenerator.Core.Grids;

/// <summary>
/// Exact Euclidean distance and nearest-feature transform: for every cell, the nearest cell of a feature mask. Two passes
/// of a one-dimensional lower envelope of parabolas (Maurer, Qi and Raghavan 2003): along each column, then along each
/// row. Where two feature cells are equally near, the one found first along the pass is kept (the lower row, then the
/// lower column), as SciPy's <c>ndimage.distance_transform_edt(..., return_indices=True)</c> does; the passes follow its
/// feature transform (BSD-3-Clause, see THIRD-PARTY-NOTICES.md), so fills made with it match cell for cell.
/// </summary>
public static class DistanceTransform
{
    /// <summary>For every cell the index (row * width + column) of the nearest feature cell; -1 everywhere when there is none.</summary>
    public static int[] Nearest(Grid<bool> feature)
    {
        int w = feature.Width, h = feature.Height;
        var fr = new int[w * h];    // row of the nearest feature so far (-1 = none)
        var fc = new int[w * h];    // its column
        int len = Math.Max(w, h);
        var lineR = new int[len];
        var lineC = new int[len];
        var copyR = new int[len];
        var copyC = new int[len];
        var g = new int[len];

        // pass 1: along each column (the nearest feature in the same column)
        for (int c = 0; c < w; c++)
        {
            for (int r = 0; r < h; r++)
            {
                bool f = feature.Data[r * w + c];
                lineR[r] = f ? r : -1;
                lineC[r] = f ? c : 0;
            }
            Voronoi(h, lineR, lineC, copyR, copyC, g, alongRows: true, coorR: 0, coorC: c);
            for (int r = 0; r < h; r++)
            {
                fr[r * w + c] = lineR[r];
                fc[r * w + c] = lineC[r];
            }
        }
        // pass 2: along each row, from the column results
        for (int r = 0; r < h; r++)
        {
            Array.Copy(fr, r * w, lineR, 0, w);
            Array.Copy(fc, r * w, lineC, 0, w);
            Voronoi(w, lineR, lineC, copyR, copyC, g, alongRows: false, coorR: r, coorC: 0);
            Array.Copy(lineR, 0, fr, r * w, w);
            Array.Copy(lineC, 0, fc, r * w, w);
        }
        var nearest = new int[w * h];
        for (int i = 0; i < nearest.Length; i++) nearest[i] = fr[i] < 0 ? -1 : fr[i] * w + fc[i];
        return nearest;
    }

    /// <summary>
    /// One line of the transform. The line's cells hold (row, column) of their nearest feature so far (row -1 = none);
    /// <paramref name="alongRows"/>: the line is a column (position = row), else a row (position = column). The other
    /// coordinate of the line is <paramref name="coorR"/> / <paramref name="coorC"/>.
    /// </summary>
    static void Voronoi(int len, int[] fR, int[] fC, int[] cR, int[] cC, int[] g, bool alongRows, int coorR, int coorC)
    {
        Array.Copy(fR, cR, len);
        Array.Copy(fC, cC, len);
        double Along(int i) => alongRows ? cR[i] : cC[i];
        double Across(int i) => alongRows ? cC[i] - coorC : cR[i] - coorR;

        int l = -1;
        for (int ii = 0; ii < len; ii++)
        {
            if (cR[ii] < 0) continue;
            double fd = Along(ii);
            double t = Across(ii), wR = t * t;
            while (l >= 1)
            {
                int idx1 = g[l], idx2 = g[l - 1];
                double f1 = Along(idx1);
                double a = f1 - Along(idx2);
                double b = fd - f1;
                double tu = Across(idx2), tv = Across(idx1);
                double uR = tu * tu, vR = tv * tv;
                double c = a + b;
                if (c * vR - b * uR - a * wR - a * b * c <= 0.0) break;
                --l;
            }
            ++l;
            g[l] = ii;
        }
        int maxl = l;
        if (maxl < 0) return;
        l = 0;
        for (int ii = 0; ii < len; ii++)
        {
            double d1 = Along(g[l]) - ii, e1 = Across(g[l]);
            double delta1 = d1 * d1 + e1 * e1;
            while (l < maxl)
            {
                double d2 = Along(g[l + 1]) - ii, e2 = Across(g[l + 1]);
                double delta2 = d2 * d2 + e2 * e2;
                if (delta1 <= delta2) break;
                delta1 = delta2;
                ++l;
            }
            fR[ii] = cR[g[l]];
            fC[ii] = cC[g[l]];
        }
    }

    /// <summary>The distance (in cells) from every cell to the nearest feature cell; +inf everywhere when there is none.</summary>
    public static double[] Distance(Grid<bool> feature)
    {
        var nearest = Nearest(feature);
        int w = feature.Width;
        var d = new double[nearest.Length];
        for (int i = 0; i < d.Length; i++)
        {
            if (nearest[i] < 0) { d[i] = double.PositiveInfinity; continue; }
            double dy = i / w - nearest[i] / w, dx = i % w - nearest[i] % w;
            d[i] = Math.Sqrt(dy * dy + dx * dx);
        }
        return d;
    }

    /// <summary>Every cell takes the value of its nearest feature cell (the grid unchanged when there is none).</summary>
    public static Grid<T> Spread<T>(Grid<T> values, Grid<bool> feature)
    {
        var nearest = Nearest(feature);
        var o = new Grid<T>(values.Width, values.Height);
        for (int i = 0; i < nearest.Length; i++) o.Data[i] = values.Data[nearest[i] < 0 ? i : nearest[i]];
        return o;
    }

    /// <summary>NaN cells take the value of the nearest cell that has one (unchanged when all or none are NaN).</summary>
    public static Grid<float> FillNaN(Grid<float> values)
    {
        var known = values.Map(v => !float.IsNaN(v));
        if (known.Data.All(k => k) || !known.Data.Any(k => k)) return values.Clone();
        return Spread(values, known);
    }
}
