using System.Numerics;

namespace FxMapGenerator.Core.Geometry;

/// <summary>
/// Floating-point helpers that give the same bits as CPython 3.11 (<c>math</c>, <c>round</c>) and numpy (<c>interp</c> /
/// <c>median</c>), so the stages take the same side of every threshold as those calls do. The ones written after those
/// projects' code, with what they follow (CPython 3.11: PSF-2.0; numpy: BSD-3-Clause; see THIRD-PARTY-NOTICES.md):
/// <list type="bullet">
/// <item><c>Hypot</c>: <c>vector_norm</c> in CPython's <c>Modules/mathmodule.c</c>.</item>
/// <item><c>PyFloorDiv</c>: <c>float_floor_div</c> / <c>_float_div_mod</c> in CPython's <c>Objects/floatobject.c</c>.</item>
/// <item><c>Interp</c>: numpy's <c>interp</c> in <c>numpy/_core/src/multiarray/compiled_base.c</c>; <c>NpInterp</c>:
///   <c>arr_interp</c> and <c>binary_search_with_guess</c> of that file.</item>
/// <item><c>NpSum</c>: <c>pairwise_sum</c> in <c>numpy/_core/src/umath/loops_utils.h.src</c>.</item>
/// <item><c>Linspace</c>: <c>numpy.linspace</c> in <c>numpy/_core/function_base.py</c>.</item>
/// <item><c>NpArange</c>: <c>PyArray_ArangeObj</c> in <c>numpy/_core/src/multiarray/ctors.c</c> and <c>DOUBLE_fill</c>
///   in <c>arraytypes.c.src</c>.</item>
/// </list>
/// </summary>
public static class Num
{
    public const double RadToDeg = 180.0 / Math.PI;
    const double T27 = 134217729.0;                     // 2^27 + 1 (Veltkamp split)
    const double DblMin = 2.2250738585072014E-308;

    public const double DegToRad = Math.PI / 180.0;

    /// <summary>Degrees of an angle in radians (<c>math.degrees</c>: one multiplication by the rounded 180 / pi).</summary>
    public static double Degrees(double rad) => rad * RadToDeg;

    /// <summary>Radians of an angle in degrees (<c>math.radians</c>: one multiplication by the rounded pi / 180).</summary>
    public static double Radians(double deg) => deg * DegToRad;

    /// <summary>
    /// The length of (x, y) as numpy's <c>hypot</c> and <c>linalg.norm</c> of a 2-vector give it on this platform: the
    /// square root of the sum of the squares, rounded at each step (not <see cref="Hypot"/>, CPython's <c>math.hypot</c>).
    /// </summary>
    public static double NpHypot(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>
    /// <c>numpy.linspace(start, stop, num)</c> (endpoint included): <c>i * step + start</c> with <c>step = (stop - start) /
    /// (num - 1)</c>, the last value set to <paramref name="stop"/>; when the step is 0, <c>i / (num - 1) * delta + start</c>.
    /// </summary>
    public static double[] Linspace(double start, double stop, int num)
    {
        var y = new double[num];
        if (num == 0) return y;
        if (num == 1) { y[0] = start; return y; }
        int div = num - 1;
        double delta = stop - start;
        double step = delta / div;
        for (int i = 0; i < num; i++)
            y[i] = (step == 0 ? (double)i / div * delta : i * step) + start;
        y[^1] = stop;
        return y;
    }

    /// <summary>
    /// The length of (x, y) as CPython 3.11's <c>math.hypot</c> computes it: scaled by a power of two, squares split
    /// into exact parts and summed with their rounding errors, then a square root with one correction step. Nearly
    /// always the correctly rounded length (<c>Math.Sqrt(x * x + y * y)</c> is one unit off now and then).
    /// </summary>
    public static double Hypot(double x, double y)
    {
        x = Math.Abs(x);
        y = Math.Abs(y);
        if (double.IsInfinity(x) || double.IsInfinity(y)) return double.PositiveInfinity;
        if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
        double max = x > y ? x : y;
        if (max == 0.0) return max;
        int maxE = Math.ILogB(max) + 1;                 // frexp: max = m * 2^maxE, 0.5 <= m < 1
        if (maxE < -1023) return DblMin * Hypot(x / DblMin, y / DblMin);
        double scale = Math.ScaleB(1.0, -maxE);
        double csum = 1.0, frac1 = 0.0, frac2 = 0.0, frac3 = 0.0;
        Add(x * scale);
        Add(y * scale);
        double h = Math.Sqrt(csum - 1.0 + (frac1 + frac2 + frac3));

        var (hi, lo) = Split(h);
        double v = -hi * hi, old = csum;
        csum += v;
        frac1 += (old - csum) + v;
        v = -2.0 * hi * lo;
        old = csum;
        csum += v;
        frac2 += (old - csum) + v;
        v = -lo * lo;
        old = csum;
        csum += v;
        frac3 += (old - csum) + v;
        v = csum - 1.0 + (frac1 + frac2 + frac3);
        return (h + v / (2.0 * h)) / scale;

        void Add(double t)
        {
            var (hi, lo) = Split(t);
            double sq = hi * hi, old = csum;
            csum += sq;
            frac1 += (old - csum) + sq;
            sq = 2.0 * hi * lo;
            old = csum;
            csum += sq;
            frac2 += (old - csum) + sq;
            frac3 += lo * lo;
        }

        static (double Hi, double Lo) Split(double a)
        {
            double t = a * T27;
            double hi = t - (t - a);
            return (hi, a - hi);
        }
    }

    /// <summary>
    /// <paramref name="x"/> rounded to <paramref name="digits"/> decimals as Python's <c>round</c> does: on the exact
    /// value of the double, a tie to the even digit (2.675 is below the tie, 0.125 goes to 0.12).
    /// </summary>
    public static double Round(double x, int digits)
    {
        if (!double.IsFinite(x) || x == 0.0) return x;
        long bits = BitConverter.DoubleToInt64Bits(x);
        bool negative = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exp == 0) exp = 1;
        else mant |= 1L << 52;
        exp -= 1075;                                    // |x| = mant * 2^exp
        var p = BigInteger.Pow(10, digits);
        var scaled = mant * p;
        BigInteger q;
        if (exp >= 0) q = scaled << exp;
        else
        {
            int sh = -exp;
            q = scaled >> sh;
            var rem = scaled - (q << sh);
            int c = rem.CompareTo(BigInteger.One << (sh - 1));
            if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
        }
        double r = (double)q / (double)p;               // both exact (q < 2^53 for map values): one correctly rounded division
        return negative ? -r : r;
    }

    /// <summary>
    /// numpy's <c>round(x, digits)</c> of a float64 (also <c>round()</c> of a numpy float): x times 10^digits, rounded to the
    /// nearest whole number (a tie to the even one), divided by 10^digits; unlike <see cref="Round"/>, the tie is judged
    /// on the product (-992.675 gives -992.68).
    /// </summary>
    public static double NpRound(double x, int digits)
    {
        double f = Math.Pow(10, digits);
        return Math.Round(x * f, MidpointRounding.ToEven) / f;
    }

    /// <summary>Python's <c>round(x)</c> to an integer: a tie to the even one.</summary>
    public static int RoundToInt(double x) => (int)Math.Round(x, MidpointRounding.ToEven);

    /// <summary>Python's <c>max(a, b)</c>: <paramref name="a"/> unless <paramref name="b"/> is greater (a NaN never wins as b).</summary>
    public static double PyMax(double a, double b) => b > a ? b : a;

    /// <summary>Python's <c>min(a, b)</c>: <paramref name="a"/> unless <paramref name="b"/> is smaller.</summary>
    public static double PyMin(double a, double b) => b < a ? b : a;

    /// <summary>
    /// Python's <c>x ** 2</c> of a float: the C library's <c>pow(x, 2.0)</c>, which is not always <c>x * x</c> (numpy
    /// arrays square with one multiplication instead).
    /// </summary>
    public static double Pow2(double x) => Math.Pow(x, 2.0);

    /// <summary>
    /// Python's <c>a // b</c> of floats: <c>fmod</c>, the quotient of the difference, then snapped to the nearest whole
    /// number (not <c>Math.Floor(a / b)</c>, which rounds up now and then when the quotient is just below a whole number).
    /// </summary>
    public static double PyFloorDiv(double a, double b)
    {
        double mod = a % b;                                      // C fmod
        double div = (a - mod) / b;
        if (mod != 0)
        {
            if ((b < 0) != (mod < 0)) div -= 1.0;
        }
        if (div != 0)
        {
            double f = Math.Floor(div);
            if (div - f > 0.5) f += 1.0;
            return f;
        }
        return Math.CopySign(0.0, a / b);
    }

    /// <summary>
    /// <c>numpy.arange(start, stop, step)</c> of floats: <c>ceil((stop - start) / step)</c> values, the first two
    /// <c>start</c> and <c>start + step</c>, the rest <c>start + i * delta</c> with <c>delta</c> the difference of those two.
    /// </summary>
    public static double[] NpArange(double start, double stop, double step)
    {
        double len = Math.Ceiling((stop - start) / step);
        if (!(len > 0)) return [];
        var a = new double[(int)len];
        a[0] = start;
        if (a.Length > 1)
        {
            a[1] = start + step;
            double delta = a[1] - a[0];
            for (int i = 2; i < a.Length; i++) a[i] = start + i * delta;
        }
        return a;
    }

    /// <summary>
    /// numpy's median: NaN when a value is NaN, else the middle value of the sorted values, or the mean of the two
    /// middle ones; NaN for no values.
    /// </summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var a = values.ToArray();
        foreach (var v in a)
            if (double.IsNaN(v)) return double.NaN;
        Array.Sort(a);
        int m = a.Length / 2;
        return a.Length % 2 == 1 ? a[m] : (a[m - 1] + a[m]) / 2.0;
    }

    /// <summary>
    /// The mean over a window of 2 <paramref name="ramp"/> + 1 values around each value, the series extended by its end
    /// values (numpy <c>convolve(pad(v, ramp, 'edge'), ones(n) / n, 'valid')</c>): each window summed in order from 0.
    /// </summary>
    public static double[] MovingMean(IReadOnlyList<double> v, int ramp)
    {
        int n = 2 * ramp + 1;
        double w = 1.0 / n;
        var o = new double[v.Count];
        for (int t = 0; t < v.Count; t++)
        {
            double sum = 0;
            for (int k = 0; k < n; k++) sum += v[Math.Clamp(t + k - ramp, 0, v.Count - 1)] * w;
            o[t] = sum;
        }
        return o;
    }

    /// <summary>
    /// numpy's <c>interp</c>: <paramref name="xp"/> ascending (repeats allowed); left of it the first value, right of it
    /// the last; on a sample point its value (the last of equal points); else slope * (x - xp[j]) + fp[j].
    /// </summary>
    /// <summary>
    /// <c>numpy.sum</c> of a float64 array: numpy's pairwise summation (fewer than 8 values in order; up to 128 with eight
    /// running sums combined as ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7)), then the rest in order; longer ones
    /// split in two at a multiple of 8 below the half).
    /// </summary>
    public static double NpSum(IReadOnlyList<double> a) => PairwiseSum(a, 0, a.Count);

    static double PairwiseSum(IReadOnlyList<double> a, int from, int n)
    {
        if (n < 8)
        {
            double res = 0.0;
            for (int i = 0; i < n; i++) res += a[from + i];
            return res;
        }
        if (n <= 128)
        {
            double r0 = a[from], r1 = a[from + 1], r2 = a[from + 2], r3 = a[from + 3],
                r4 = a[from + 4], r5 = a[from + 5], r6 = a[from + 6], r7 = a[from + 7];
            int i;
            for (i = 8; i < n - (n % 8); i += 8)
            {
                r0 += a[from + i]; r1 += a[from + i + 1]; r2 += a[from + i + 2]; r3 += a[from + i + 3];
                r4 += a[from + i + 4]; r5 += a[from + i + 5]; r6 += a[from + i + 6]; r7 += a[from + i + 7];
            }
            double res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++) res += a[from + i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return PairwiseSum(a, from, n2) + PairwiseSum(a, from + n2, n - n2);
    }

    /// <summary>
    /// <c>numpy.mean</c> of a float32 array: the float32 pairwise sum (as <see cref="NpSum"/>, in float32), divided by
    /// the count in float32.
    /// </summary>
    public static float NpMeanF32(IReadOnlyList<float> a) => a.Count == 0 ? float.NaN : PairwiseSumF32(a, 0, a.Count) / a.Count;

    static float PairwiseSumF32(IReadOnlyList<float> a, int from, int n)
    {
        if (n < 8)
        {
            float res = 0f;
            for (int i = 0; i < n; i++) res += a[from + i];
            return res;
        }
        if (n <= 128)
        {
            float r0 = a[from], r1 = a[from + 1], r2 = a[from + 2], r3 = a[from + 3],
                r4 = a[from + 4], r5 = a[from + 5], r6 = a[from + 6], r7 = a[from + 7];
            int i;
            for (i = 8; i < n - (n % 8); i += 8)
            {
                r0 += a[from + i]; r1 += a[from + i + 1]; r2 += a[from + i + 2]; r3 += a[from + i + 3];
                r4 += a[from + i + 4]; r5 += a[from + i + 5]; r6 += a[from + i + 6]; r7 += a[from + i + 7];
            }
            float res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++) res += a[from + i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return PairwiseSumF32(a, from, n2) + PairwiseSumF32(a, from + n2, n - n2);
    }

    /// <summary>
    /// <c>numpy.interp</c> exactly as numpy computes it, also for x positions that do not increase (numpy's search with
    /// a guess: a linear search up to 4 positions, else the guess and its neighbours, then bisection).
    /// <paramref name="guess"/> carries the last index between the values of one call, as numpy does over an array of x.
    /// </summary>
    public static double NpInterp(double x, IReadOnlyList<double> xp, IReadOnlyList<double> fp, ref int guess)
    {
        int len = xp.Count;
        double lval = fp[0], rval = fp[len - 1];
        if (len == 1) return x < xp[0] ? lval : x > xp[0] ? rval : fp[0];
        if (double.IsNaN(x)) return x;
        int j = SearchWithGuess(x, xp, len, guess);
        guess = j;
        if (j == -1) return lval;
        if (j == len) return rval;
        if (j == len - 1) return fp[j];
        if (xp[j] == x) return fp[j];
        double slope = (fp[j + 1] - fp[j]) / (xp[j + 1] - xp[j]);
        double r = slope * (x - xp[j]) + fp[j];
        if (double.IsNaN(r))
        {
            r = slope * (x - xp[j + 1]) + fp[j + 1];
            if (double.IsNaN(r) && fp[j] == fp[j + 1]) r = fp[j];
        }
        return r;
    }

    static int SearchWithGuess(double key, IReadOnlyList<double> arr, int len, int guess)
    {
        const int cache = 8;
        int imin = 0, imax = len;
        if (key > arr[len - 1]) return len;
        if (key < arr[0]) return -1;
        if (len <= 4)
        {
            int i = 1;
            while (i < len && key >= arr[i]) i++;
            return i - 1;
        }
        if (guess > len - 3) guess = len - 3;
        if (guess < 1) guess = 1;
        if (key < arr[guess])
        {
            if (key < arr[guess - 1])
            {
                imax = guess - 1;
                if (guess > cache && key >= arr[guess - cache]) imin = guess - cache;
            }
            else return guess - 1;
        }
        else
        {
            if (key < arr[guess + 1]) return guess;
            if (key < arr[guess + 2]) return guess + 1;
            imin = guess + 2;
            if (guess < len - cache - 1 && key < arr[guess + cache]) imax = guess + cache;
        }
        while (imin < imax)
        {
            int imid = imin + ((imax - imin) >> 1);
            if (key >= arr[imid]) imin = imid + 1;
            else imax = imid;
        }
        return imin - 1;
    }

    /// <summary>Python's float modulo: the result has the sign of <paramref name="m"/> (0 when it divides).</summary>
    public static double PyMod(double x, double m)
    {
        double mod = x % m;                                  // C fmod (truncated)
        if (mod != 0)
        {
            if ((m < 0) != (mod < 0)) mod += m;
        }
        else mod = Math.CopySign(0.0, m);
        return mod;
    }

    public static double Interp(double x, IReadOnlyList<double> xp, IReadOnlyList<double> fp)
    {
        int n = xp.Count;
        if (double.IsNaN(x)) return x;
        if (x > xp[n - 1]) return fp[n - 1];
        if (x < xp[0]) return fp[0];
        int lo = 0, hi = n;                             // j = the last index with xp[j] <= x
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (x >= xp[mid]) lo = mid + 1;
            else hi = mid;
        }
        int j = lo - 1;
        if (j == n - 1 || xp[j] == x) return fp[j];
        double slope = (fp[j + 1] - fp[j]) / (xp[j + 1] - xp[j]);
        double r = slope * (x - xp[j]) + fp[j];
        if (double.IsNaN(r))
        {
            r = slope * (x - xp[j + 1]) + fp[j + 1];
            if (double.IsNaN(r) && fp[j] == fp[j + 1]) r = fp[j];
        }
        return r;
    }
}
