namespace FxMapGenerator.Core.Grids;

/// <summary>
/// Thinning of a mask to lines one cell wide, with the cells of scikit-image's <c>skeletonize</c> (method Zhang, the 2-D
/// default: T. Y. Zhang and C. Y. Suen, "A fast parallel algorithm for thinning digital patterns", CACM 1984, as the
/// table of scikit-image 0.26's <c>_fast_skeletonize</c>): passes over the grid that remove cells by the pattern of their
/// eight neighbours, the first sub-pass the patterns marked 1 or 3, the second 2 or 3, until nothing changes. Cells
/// outside the grid are off. <c>_fast_skeletonize</c> is in <c>skimage/morphology/_skeletonize_various_cy.pyx</c>;
/// BSD-3-Clause, see THIRD-PARTY-NOTICES.md.
/// </summary>
public static class Skeleton
{
    // one entry per pattern of the 8 neighbours: NW 1, N 2, NE 4, E 8, SE 16, S 32, SW 64, W 128
    static readonly byte[] Lut =
    [
        0, 0, 0, 1, 0, 0, 1, 3, 0, 0, 3, 1, 1, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2, 0, 3, 0, 3, 3,
        0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 3, 0, 2, 2,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 3, 0, 2, 0,
        0, 0, 3, 1, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
        3, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 3, 1, 3, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 3, 3, 0, 1, 0, 0, 0, 0, 2, 2, 0, 0, 2, 0, 0, 0,
    ];

    public static Grid<bool> Zhang(Grid<bool> mask)
    {
        int rows = mask.Height + 2, cols = mask.Width + 2;
        var sk = new byte[rows * cols];
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
                if (mask.Data[y * mask.Width + x]) sk[(y + 1) * cols + x + 1] = 1;
        var cleaned = (byte[])sk.Clone();
        bool removed = true;
        while (removed)
        {
            removed = false;
            for (int pass = 0; pass < 2; pass++)
            {
                bool first = pass == 0;
                for (int r = 1; r < rows - 1; r++)
                    for (int c = 1; c < cols - 1; c++)
                    {
                        int i = r * cols + c;
                        if (sk[i] == 0) continue;
                        int nb = Lut[sk[i - cols - 1] + 2 * sk[i - cols] + 4 * sk[i - cols + 1] + 8 * sk[i + 1]
                                     + 16 * sk[i + cols + 1] + 32 * sk[i + cols] + 64 * sk[i + cols - 1] + 128 * sk[i - 1]];
                        if (nb == 0) continue;
                        if (nb == 3 || (nb == 1 && first) || (nb == 2 && !first))
                        {
                            cleaned[i] = 0;
                            removed = true;
                        }
                    }
                Array.Copy(cleaned, sk, sk.Length);
            }
        }
        var o = new Grid<bool>(mask.Width, mask.Height);
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
                o.Data[y * mask.Width + x] = sk[(y + 1) * cols + x + 1] != 0;
        return o;
    }
}
