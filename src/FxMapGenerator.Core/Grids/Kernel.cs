namespace FxMapGenerator.Core.Grids;

/// <summary>
/// A structuring element: a width x height mask with an anchor (its centre cell). Every row of the kernels here is one
/// run of set cells, kept as <see cref="Spans"/> (column offsets from the anchor, inclusive) for the morphology.
/// </summary>
public sealed class Kernel
{
    public int Width { get; }
    public int Height { get; }
    public int AnchorX { get; }
    public int AnchorY { get; }
    public bool[] Mask { get; }
    /// <summary>Per kernel row: (row offset from the anchor, first column offset, last column offset); rows without cells left out.</summary>
    public IReadOnlyList<(int Dy, int X0, int X1)> Spans { get; }

    Kernel(int width, int height, bool[] mask)
    {
        Width = width;
        Height = height;
        AnchorX = width / 2;
        AnchorY = height / 2;
        Mask = mask;
        var spans = new List<(int, int, int)>();
        for (int y = 0; y < height; y++)
        {
            int first = -1, last = -1;
            for (int x = 0; x < width; x++)
                if (mask[y * width + x])
                {
                    if (first < 0) first = x;
                    else if (last != x - 1) throw new ArgumentException("kernel rows must be one run of cells");
                    last = x;
                }
            if (first >= 0) spans.Add((y - AnchorY, first - AnchorX, last - AnchorX));
        }
        Spans = spans;
    }

    public static Kernel FromMask(int width, int height, bool[] mask) => new(width, height, (bool[])mask.Clone());

    /// <summary>All cells set (a box).</summary>
    public static Kernel Rect(int width, int height) => new(width, height, Enumerable.Repeat(true, width * height).ToArray());

    /// <summary>3 x 3 with the four edge neighbours (the default structure of a binary dilation or erosion on a 2-D grid).</summary>
    public static Kernel Cross3() => new(3, 3, [false, true, false, true, true, true, false, true, false]);

    /// <summary>A disk of radius r (2r + 1 cells across): the cells with x² + y² &lt;= r² (scikit-image's <c>disk(r)</c>).</summary>
    public static Kernel Disk(int r)
    {
        int n = 2 * r + 1;
        var mask = new bool[n * n];
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
                mask[(y + r) * n + x + r] = x * x + y * y <= r * r;
        return new Kernel(n, n, mask);
    }

    /// <summary>
    /// The elliptic kernel of OpenCV's <c>getStructuringElement(MORPH_ELLIPSE, (width, height))</c>: per row dy = i - r
    /// (r = height / 2, c = width / 2), the cells c - dx .. c + dx with dx = round(c * sqrt((r² - dy²) / r²)), rounded
    /// half to even. The row formula follows OpenCV (Apache-2.0, see THIRD-PARTY-NOTICES.md).
    /// </summary>
    public static Kernel CvEllipse(int width, int height)
    {
        if (width == 1 && height == 1) return Rect(1, 1);
        int r = height / 2, c = width / 2;
        double invR2 = r != 0 ? 1.0 / ((double)r * r) : 0;
        var mask = new bool[width * height];
        for (int i = 0; i < height; i++)
        {
            int dy = i - r, j1 = 0, j2 = 0;
            if (Math.Abs(dy) <= r)
            {
                int dx = (int)Math.Round(c * Math.Sqrt((r * r - dy * dy) * invR2), MidpointRounding.ToEven);
                j1 = Math.Max(c - dx, 0);
                j2 = Math.Min(c + dx + 1, width);
            }
            for (int j = j1; j < j2; j++) mask[i * width + j] = true;
        }
        return new Kernel(width, height, mask);
    }
}
