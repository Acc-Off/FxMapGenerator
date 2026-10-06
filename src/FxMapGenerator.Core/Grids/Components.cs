namespace FxMapGenerator.Core.Grids;

/// <summary>
/// Connected components of the cells that are on. Labels run 1.. in the order of each component's first cell in row
/// order (the order OpenCV's 4-connected and SciPy's labelling give); 0 = off.
/// </summary>
public static class Components
{
    public sealed record Labels(Grid<int> Label, int Count, int[] Area)
    {
        /// <summary>The cells of one label.</summary>
        public Grid<bool> Mask(int label) => Label.Map(l => l == label);
    }

    /// <param name="connectivity">4 (edge neighbours) or 8 (also the diagonals).</param>
    public static Labels Label(Grid<bool> mask, int connectivity)
    {
        if (connectivity is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(connectivity));
        int w = mask.Width, h = mask.Height;
        var label = new Grid<int>(w, h);
        var areas = new List<int> { 0 };
        var stack = new Stack<int>();
        int n = 0;
        for (int start = 0; start < mask.Count; start++)
        {
            if (!mask.Data[start] || label.Data[start] != 0) continue;
            n++;
            int area = 0;
            label.Data[start] = n;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                area++;
                int y = i / w, x = i % w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0 || connectivity == 4 && dx != 0 && dy != 0) continue;
                        int ny = y + dy, nx = x + dx;
                        if ((uint)ny >= (uint)h || (uint)nx >= (uint)w) continue;
                        int j = ny * w + nx;
                        if (!mask.Data[j] || label.Data[j] != 0) continue;
                        label.Data[j] = n;
                        stack.Push(j);
                    }
            }
            areas.Add(area);
        }
        return new Labels(label, n, areas.ToArray());
    }

    /// <summary>
    /// Per label (index 0 unused): the mean column and row of its cells, the sums divided by the area in double (OpenCV's
    /// <c>connectedComponentsWithStats</c> centroids, SciPy's <c>center_of_mass</c> of one label).
    /// </summary>
    public static (double X, double Y)[] Centroids(Labels l)
    {
        var sx = new long[l.Count + 1];
        var sy = new long[l.Count + 1];
        int w = l.Label.Width;
        for (int i = 0; i < l.Label.Count; i++)
        {
            int v = l.Label.Data[i];
            if (v == 0) continue;
            sx[v] += i % w;
            sy[v] += i / w;
        }
        var o = new (double X, double Y)[l.Count + 1];
        o[0] = (double.NaN, double.NaN);
        for (int v = 1; v <= l.Count; v++) o[v] = ((double)sx[v] / l.Area[v], (double)sy[v] / l.Area[v]);
        return o;
    }

    /// <summary>The components of at least <paramref name="minCells"/> cells.</summary>
    public static Grid<bool> KeepAtLeast(Grid<bool> mask, int minCells, int connectivity)
    {
        var l = Label(mask, connectivity);
        return l.Label.Map(v => v != 0 && l.Area[v] >= minCells);
    }
}
