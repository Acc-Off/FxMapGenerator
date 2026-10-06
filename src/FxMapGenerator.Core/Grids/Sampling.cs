namespace FxMapGenerator.Core.Grids;

/// <summary>
/// Values of a grid between its cells, with the bits of SciPy's <c>ndimage</c> (linear, order 1). Written after
/// SciPy 1.17's <c>NI_GeometricTransform</c> (<c>ndimage/src/ni_interpolation.c</c>) and
/// <c>get_spline_interpolation_weights</c> (<c>ni_splines.c</c>); BSD-3-Clause, see THIRD-PARTY-NOTICES.md.
/// </summary>
public static class Sampling
{
    /// <summary>
    /// <c>map_coordinates(field, [row, col], order=1, mode='nearest')</c> at one point, as a float32 field gives it:
    /// weights 1 - f and 1 - (1 - f) per axis (f = the fraction past the lower cell), the four cells summed row by row
    /// from 0 with <c>value * wRow * wCol</c>, cells past the edges clamped to the edge.
    /// </summary>
    public static float Linear(Grid<float> field, double row, double col)
    {
        int h = field.Height, w = field.Width;
        double fr = Math.Floor(row), fc = Math.Floor(col);
        long r0 = (long)fr, c0 = (long)fc;
        double tr = row - fr, tc = col - fc;
        double wr0 = 1.0 - tr, wr1 = 1.0 - wr0;
        double wc0 = 1.0 - tc, wc1 = 1.0 - wc0;
        int ra = Clamp(r0, h), rb = Clamp(r0 + 1, h), ca = Clamp(c0, w), cb = Clamp(c0 + 1, w);
        double t = 0.0;
        t += (double)field.Data[ra * w + ca] * wr0 * wc0;
        t += (double)field.Data[ra * w + cb] * wr0 * wc1;
        t += (double)field.Data[rb * w + ca] * wr1 * wc0;
        t += (double)field.Data[rb * w + cb] * wr1 * wc1;
        return (float)t;
    }

    static int Clamp(long i, int n) => i < 0 ? 0 : i >= n ? n - 1 : (int)i;
}
