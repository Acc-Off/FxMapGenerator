using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Jobs;

namespace FxMapGenerator.Core.Grids;

/// <summary>
/// Gaussian blurs with the bits of the two libraries the map stages were first written with; both mirror the grid at
/// its edges with the edge cell repeated (<c>…cba|abc…</c>).
/// <list type="bullet">
/// <item><see cref="Scipy(Grid{float}, double)"/> / <see cref="Scipy(Grid{double}, double)"/>: SciPy's
///   <c>ndimage.gaussian_filter</c> (truncate 4): weights <c>exp(-0.5 / s² * x²)</c> for |x| up to <c>int(4 s + 0.5)</c>,
///   divided by their pairwise sum; down the columns first, then along the rows, each pass kept in the grid's type; a
///   value is the centre times its weight plus the pairs of cells from the outermost inwards.</item>
/// <item><see cref="OpenCv"/>: OpenCV's <c>GaussianBlur</c> of a float64 grid with the size from sigma (<c>round(8 s + 1)
///   | 1</c>): its bit-exact kernel (a table-based exponential), along the rows first (the weights in order), then down
///   the columns (the centre, then the pairs from the centre outwards).</item>
/// </list>
/// The loops over lines run on the parallel runner when one is given (the values do not depend on it).
/// Written after SciPy 1.17's <c>_gaussian_kernel1d</c>, <c>NI_Correlate1D</c> (<c>ndimage/src/ni_filters.c</c>) and
/// <c>NI_ExtendLine</c> (<c>ni_support.c</c>), BSD-3-Clause, and after OpenCV 5.0.0's <c>getGaussianKernelBitExact</c>
/// (<c>modules/imgproc/src/smooth.dispatch.cpp</c>), <c>RowFilter</c> and <c>SymmColumnFilter</c>
/// (<c>filter.simd.hpp</c>) and <c>f64_exp</c> with its table (<c>modules/core/src/softfloat.cpp</c>, which is based
/// on the SoftFloat package), Apache-2.0 and BSD-3-Clause; see THIRD-PARTY-NOTICES.md.
/// </summary>
public static class Gaussian
{
    /// <summary>SciPy's blur of a float32 grid: each pass rounded to float32 as SciPy stores it.</summary>
    public static Grid<float> Scipy(Grid<float> src, double sigma, IParallelRunner? parallel = null)
    {
        var buf = new double[src.Count];
        for (int i = 0; i < buf.Length; i++) buf[i] = src.Data[i];
        ScipyPasses(buf, src.Width, src.Height, sigma, f32: true, parallel);
        var o = new Grid<float>(src.Width, src.Height);
        for (int i = 0; i < buf.Length; i++) o.Data[i] = (float)buf[i];
        return o;
    }

    /// <summary>SciPy's blur of a float64 grid.</summary>
    public static Grid<double> Scipy(Grid<double> src, double sigma, IParallelRunner? parallel = null)
    {
        var buf = (double[])src.Data.Clone();
        ScipyPasses(buf, src.Width, src.Height, sigma, f32: false, parallel);
        return new Grid<double>(src.Width, src.Height, buf);
    }

    /// <summary>The weights of SciPy's <c>_gaussian_kernel1d</c> (order 0, truncate 4), centre at index radius.</summary>
    public static double[] ScipyKernel(double sigma)
    {
        int r = (int)(4.0 * sigma + 0.5);
        double c = -0.5 / (sigma * sigma);
        var w = new double[2 * r + 1];
        for (int i = 0; i < w.Length; i++)
        {
            long x = i - r;
            w[i] = Math.Exp(c * (double)(x * x));
        }
        double sum = Num.NpSum(w);
        for (int i = 0; i < w.Length; i++) w[i] /= sum;
        return w;
    }

    static void ScipyPasses(double[] buf, int w, int h, double sigma, bool f32, IParallelRunner? parallel)
    {
        if (sigma <= 1e-15 || buf.Length == 0) return;
        var k = ScipyKernel(sigma);
        int r = k.Length / 2;
        // axis 0: along each column
        For(parallel, w, x =>
        {
            var line = new double[h];
            var ext = new double[h + 2 * r];
            for (int y = 0; y < h; y++) line[y] = buf[y * w + x];
            Correlate(line, ext, k, r);
            for (int y = 0; y < h; y++) buf[y * w + x] = f32 ? (float)line[y] : line[y];
        });
        // axis 1: along each row
        For(parallel, h, y =>
        {
            var line = new double[w];
            var ext = new double[w + 2 * r];
            Array.Copy(buf, y * w, line, 0, w);
            Correlate(line, ext, k, r);
            for (int x = 0; x < w; x++) buf[y * w + x] = f32 ? (float)line[x] : line[x];
        });
    }

    /// <summary>NI_Correlate1D with a symmetric kernel: <c>c * w0</c>, then <c>+ (a[-j] + a[+j]) * w[j]</c> for j = r .. 1.</summary>
    static void Correlate(double[] line, double[] ext, double[] k, int r)
    {
        int n = line.Length;
        for (int i = 0; i < ext.Length; i++) ext[i] = line[Filters.Reflect(i - r, n)];
        for (int x = 0; x < n; x++)
        {
            int c = x + r;
            double s = ext[c] * k[r];
            for (int j = r; j >= 1; j--) s += (ext[c - j] + ext[c + j]) * k[r - j];
            line[x] = s;
        }
    }

    /// <summary>OpenCV's <c>GaussianBlur(src, (0, 0), sigma, borderType=BORDER_REFLECT)</c> of a float64 grid.</summary>
    public static Grid<double> OpenCv(Grid<double> src, double sigma, IParallelRunner? parallel = null)
    {
        int w = src.Width, h = src.Height;
        int n = CvRound(sigma * 4 * 2 + 1) | 1;
        int kw = w == 1 ? 1 : n, kh = h == 1 ? 1 : n;
        if (kw == 1 && kh == 1) return src.Clone();
        var kx = CvKernel(kw, sigma);
        var ky = kh == kw ? kx : CvKernel(kh, sigma);
        int rx = kw / 2, ry = kh / 2;
        // rows: the weights in order over the mirrored row
        var rows = new double[w * h];
        For(parallel, h, y =>
        {
            var ext = new double[w + 2 * rx];
            for (int i = 0; i < ext.Length; i++) ext[i] = src.Data[y * w + Filters.Reflect(i - rx, w)];
            for (int x = 0; x < w; x++)
            {
                double s = kx[0] * ext[x];
                for (int j = 1; j < kw; j++) s += kx[j] * ext[x + j];
                rows[y * w + x] = s;
            }
        });
        // columns: the centre (plus OpenCV's delta 0), then the pairs of rows from the centre outwards
        var o = new double[w * h];
        For(parallel, h, y =>
        {
            var up = new int[ry + 1];
            var down = new int[ry + 1];
            for (int j = 1; j <= ry; j++) { up[j] = Filters.Reflect(y - j, h) * w; down[j] = Filters.Reflect(y + j, h) * w; }
            int c = y * w;
            for (int x = 0; x < w; x++)
            {
                double s = ky[ry] * rows[c + x] + 0.0;
                for (int j = 1; j <= ry; j++) s += ky[ry + j] * (rows[down[j] + x] + rows[up[j] + x]);
                o[c + x] = s;
            }
        });
        return new Grid<double>(w, h, o);
    }

    /// <summary>OpenCV's bit-exact Gaussian kernel of odd size n (<c>getGaussianKernelBitExact</c>, sigma &gt; 0).</summary>
    public static double[] CvKernel(int n, double sigma)
    {
        if (n == 1) return [1.0];
        double scale2X = -0.125 / (sigma * sigma);
        int n2 = (n - 1) / 2;
        var values = new double[n2];
        double sum = 0;
        for (int i = 0, x = 1 - n; i < n2; i++, x += 2)
        {
            double t = CvExp((double)(x * x) * scale2X);
            values[i] = t;
            sum += t;
        }
        sum *= 2.0;
        sum += 1.0;
        double mul1 = 1.0 / sum;
        var k = new double[n];
        for (int i = 0; i < n2; i++)
        {
            double t = values[i] * mul1;
            k[i] = t;
            k[n - 1 - i] = t;
        }
        k[n2] = 1.0 * mul1;
        return k;
    }

    // OpenCV's softfloat exp (modules/core/src/softfloat.cpp, f64_exp: a 64-entry table of 2^(i/64) and a degree-5
    // polynomial); every step is one IEEE double operation, as in softfloat.
    const int ExpTabScale = 6, ExpTabMask = (1 << ExpTabScale) - 1;
    static readonly double ExpPolyA0 = BitConverter.UInt64BitsToDouble(0x3f83ce0f3e46f431);
    static readonly double ExpPrescale = BitConverter.UInt64BitsToDouble(0x3ff71547652b82fe) * (1 << ExpTabScale);
    static readonly double ExpPostscale = 1.0 / (1 << ExpTabScale);
    static readonly double ExpMaxVal = 3000.0 * (1 << ExpTabScale);
    static readonly double A5 = 1.0 / ExpPolyA0,
        A4 = BitConverter.UInt64BitsToDouble(0x3fe62e42fefa39f1) / ExpPolyA0,
        A3 = BitConverter.UInt64BitsToDouble(0x3fcebfbdff82a45a) / ExpPolyA0,
        A2 = BitConverter.UInt64BitsToDouble(0x3fac6b08d81fec75) / ExpPolyA0,
        A1 = BitConverter.UInt64BitsToDouble(0x3f83b2a72b4f3cd3) / ExpPolyA0,
        A0 = BitConverter.UInt64BitsToDouble(0x3f55e7aa1566c2a4) / ExpPolyA0;
    static readonly double[] ExpTab = new ulong[]
    {
        0x3ff0000000000000, 0x3ff02c9a3e778061, 0x3ff059b0d3158574, 0x3ff0874518759bc8, 0x3ff0b5586cf9890f, 0x3ff0e3ec32d3d1a2,
        0x3ff11301d0125b51, 0x3ff1429aaea92de0, 0x3ff172b83c7d517b, 0x3ff1a35beb6fcb75, 0x3ff1d4873168b9aa, 0x3ff2063b88628cd6,
        0x3ff2387a6e756238, 0x3ff26b4565e27cdd, 0x3ff29e9df51fdee1, 0x3ff2d285a6e4030b, 0x3ff306fe0a31b715, 0x3ff33c08b26416ff,
        0x3ff371a7373aa9cb, 0x3ff3a7db34e59ff7, 0x3ff3dea64c123422, 0x3ff4160a21f72e2a, 0x3ff44e086061892d, 0x3ff486a2b5c13cd0,
        0x3ff4bfdad5362a27, 0x3ff4f9b2769d2ca7, 0x3ff5342b569d4f82, 0x3ff56f4736b527da, 0x3ff5ab07dd485429, 0x3ff5e76f15ad2148,
        0x3ff6247eb03a5585, 0x3ff6623882552225, 0x3ff6a09e667f3bcd, 0x3ff6dfb23c651a2f, 0x3ff71f75e8ec5f74, 0x3ff75feb564267c9,
        0x3ff7a11473eb0187, 0x3ff7e2f336cf4e62, 0x3ff82589994cce13, 0x3ff868d99b4492ed, 0x3ff8ace5422aa0db, 0x3ff8f1ae99157736,
        0x3ff93737b0cdc5e5, 0x3ff97d829fde4e50, 0x3ff9c49182a3f090, 0x3ffa0c667b5de565, 0x3ffa5503b23e255d, 0x3ffa9e6b5579fdbf,
        0x3ffae89f995ad3ad, 0x3ffb33a2b84f15fb, 0x3ffb7f76f2fb5e47, 0x3ffbcc1e904bc1d2, 0x3ffc199bdd85529c, 0x3ffc67f12e57d14b,
        0x3ffcb720dcef9069, 0x3ffd072d4a07897c, 0x3ffd5818dcfba487, 0x3ffda9e603db3285, 0x3ffdfc97337b9b5f, 0x3ffe502ee78b3ff6,
        0x3ffea4afa2a490da, 0x3ffefa1bee615a27, 0x3fff50765b6e4540, 0x3fffa7c1819e90d8,
    }.Select(BitConverter.UInt64BitsToDouble).ToArray();

    /// <summary>OpenCV's softfloat <c>exp</c> of a double.</summary>
    public static double CvExp(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (double.IsInfinity(x)) return x > 0 ? x : 0.0;
        int exponent = (int)((BitConverter.DoubleToUInt64Bits(x) >> 52) & 0x7FF);
        double x0 = exponent > 1023 + 10 ? (x < 0 ? -ExpMaxVal : ExpMaxVal) : x * ExpPrescale;
        int val0 = CvRound(x0);
        int t = (val0 >> ExpTabScale) + 1023;
        t = t < 0 ? 0 : t > 2047 ? 2047 : t;
        double buf = BitConverter.UInt64BitsToDouble((ulong)t << 52);
        x0 = (x0 - Math.Round(x0, MidpointRounding.ToEven)) * ExpPostscale;
        double poly = ((((A0 * x0 + A1) * x0 + A2) * x0 + A3) * x0 + A4) * x0 + A5;
        return buf * ExpPolyA0 * ExpTab[val0 & ExpTabMask] * poly;
    }

    /// <summary><c>cvRound</c>: to the nearest integer, halves to even.</summary>
    static int CvRound(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    static void For(IParallelRunner? parallel, int count, Action<int> body) => Lines.For(parallel, count, body);
}

/// <summary>Loops over the lines of a grid, in blocks of lines on a stage's parallel runner when one is given.</summary>
public static class Lines
{
    /// <summary>Calls <paramref name="body"/> for 0 .. count - 1; with a runner, blocks of about 64 lines side by side.</summary>
    public static void For(IParallelRunner? parallel, int count, Action<int> body)
    {
        if (parallel is null || count < 128)
        {
            for (int i = 0; i < count; i++) body(i);
            return;
        }
        const int block = 64;
        var starts = Enumerable.Range(0, (count + block - 1) / block).Select(b => b * block).ToArray();
        parallel.ForEach(starts, s =>
        {
            int e = Math.Min(count, s + block);
            for (int i = s; i < e; i++) body(i);
        });
    }
}
