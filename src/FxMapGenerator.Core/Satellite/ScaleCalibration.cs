using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Satellite;

/// <summary>
/// Measures the scale correction q per axis from the captures themselves. Captures reach a few metres beyond their
/// block (frame margin), so neighbours overlap: both are rendered over the shared strip at the same world positions
/// (with the current q and the heights), and the shift that lines the two strips up best (normalised correlation,
/// whole pixels +-20 refined by a parabola) is their mismatch. The median mismatch of all textured pairs gives
/// q_new = q (1 + mismatch / block size). One pass from q = 1 and one refining pass are enough.
/// </summary>
public static class ScaleCalibration
{
    /// <summary>Metres rendered beyond each block edge; the 1.06 frame margin reaches about 8.4 m.</summary>
    public const double Extend = 8.0;
    const int MaxShift = 20;
    const double MinCorrelation = 0.5;

    /// <summary>A captured block: its shot, camera line and the height files its photo is placed with (the higher per point when two).</summary>
    public sealed record Capture(string Png, CameraLine Camera, IReadOnlyList<string> Heights)
    {
        public Capture(string png, CameraLine camera, string heightGrid) : this(png, camera, [heightGrid]) { }

        /// <summary>The first height file.</summary>
        public string HeightGrid => Heights[0];
    }

    public sealed record Axis(int Pairs, int Used, double MedianMismatch, double P90Mismatch, double Q);

    public sealed record Result(Axis X, Axis Y)
    {
        public double Qx => X.Q;
        public double Qy => Y.Q;
        /// <summary>Blocks left out because their shot or height grid could not be read.</summary>
        public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();
    }

    /// <summary>Measures from q = (1, 1), then again from the result.</summary>
    public static (Result First, Result Final) Calibrate(IReadOnlyDictionary<BlockId, Capture> captures, int workers, Action<string>? log = null, CancellationToken ct = default) =>
        Calibrate(captures, new FixedParallel(workers, ct), log);

    /// <inheritdoc cref="Calibrate(IReadOnlyDictionary{BlockId, Capture}, int, Action{string}?, CancellationToken)"/>
    public static (Result First, Result Final) Calibrate(IReadOnlyDictionary<BlockId, Capture> captures, IParallelRunner parallel, Action<string>? log = null)
    {
        var first = Measure(captures, 1.0, 1.0, parallel);
        if (first.Skipped.Count > 0) log?.Invoke($"calibration: {first.Skipped.Count} blocks cannot be read and are left out: {string.Join(", ", first.Skipped.Take(8))}");
        log?.Invoke($"calibration pass 1: qx {first.Qx:F5} ({first.X.Used} pairs), qy {first.Qy:F5} ({first.Y.Used} pairs)");
        var final = Measure(captures, first.Qx, first.Qy, parallel);
        log?.Invoke($"calibration pass 2: qx {final.Qx:F5} (left over {final.X.MedianMismatch:+0.00;-0.00;0.00} m), qy {final.Qy:F5} (left over {final.Y.MedianMismatch:+0.00;-0.00;0.00} m)");
        return (first, final);
    }

    /// <summary>One pass at (qx, qy): mismatches of every east-west and north-south pair of captured blocks.</summary>
    public static Result Measure(IReadOnlyDictionary<BlockId, Capture> captures, double qx, double qy, int workers, CancellationToken ct = default) =>
        Measure(captures, qx, qy, new FixedParallel(workers, ct));

    /// <inheritdoc cref="Measure(IReadOnlyDictionary{BlockId, Capture}, double, double, int, CancellationToken)"/>
    public static Result Measure(IReadOnlyDictionary<BlockId, Capture> captures, double qx, double qy, IParallelRunner parallel)
    {
        var dx = new List<double>();
        var dy = new List<double>();
        int pairsX = 0, pairsY = 0;
        double size = 0;
        Dictionary<int, Strips> above = new();
        var skipped = new System.Collections.Concurrent.ConcurrentBag<string>();
        foreach (var row in captures.Keys.GroupBy(b => b.By).OrderBy(g => g.Key))
        {
            var strips = new System.Collections.Concurrent.ConcurrentDictionary<int, Strips>();
            parallel.ForEach(row.ToList(), b =>
            {
                try { strips[b.Bx] = Strips.Render(captures[b], qx, qy); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { skipped.Add(b.Name); }
            });
            var list = new List<(float[] A, float[] B, int Rows, int Cols, int Axis, double Ppm, List<double> Into)>();
            foreach (var (bx, s) in strips)
            {
                size = s.Size;
                if (strips.TryGetValue(bx + 1, out var east)) { pairsX++; list.Add((s.Right, east.Left, s.B, s.W, 1, s.Ppm, dx)); }
                if (above.TryGetValue(bx, out var north) && row.Key == north.By + 1) { pairsY++; list.Add((north.Bottom, s.Top, s.W, s.B, 0, s.Ppm, dy)); }
            }
            var found = new System.Collections.Concurrent.ConcurrentBag<(List<double> Into, double M)>();
            parallel.ForEach(list, p =>
            {
                var m = Mismatch(p.A, p.B, p.Rows, p.Cols, p.Axis);
                if (m is { } mm) found.Add((p.Into, mm / p.Ppm));
            });
            foreach (var (into, m) in found) into.Add(m);
            above = strips.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        return new Result(Summarise(dx, pairsX, qx, size), Summarise(dy, pairsY, qy, size)) { Skipped = skipped.OrderBy(n => n).ToList() };
    }

    static Axis Summarise(List<double> d, int pairs, double q, double size)
    {
        if (d.Count == 0) return new Axis(pairs, 0, 0, 0, q);
        var sorted = d.OrderBy(v => v).ToArray();
        double median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        var abs = d.Select(Math.Abs).OrderBy(v => v).ToArray();
        double p90 = abs[Math.Min(abs.Length - 1, (int)Math.Ceiling(0.9 * abs.Length) - 1)];
        return new Axis(pairs, d.Count, median, p90, q * (1 + median / size));
    }

    /// <summary>Shift in pixels (sign as the correction expects), or null when the strips are flat or do not match.</summary>
    static double? Mismatch(float[] a, float[] b, int rows, int cols, int axis)
    {
        var sa = new double[a.Length];
        var sb = new double[b.Length];
        for (int i = 0; i < a.Length; i++)
        {
            bool gap = float.IsNaN(a[i]) || float.IsNaN(b[i]);
            sa[i] = gap ? 0 : a[i];
            sb[i] = gap ? 0 : b[i];
        }
        if (Std(sa) < 1 || Std(sb) < 1) return null;
        int n = axis == 0 ? rows : cols;
        var rs = new Dictionary<int, double>();
        for (int s = -MaxShift; s <= MaxShift; s++)
        {
            int len = n - Math.Abs(s);
            if (Math.Min(len, axis == 0 ? cols : rows) < 6) continue;
            int offA = Math.Max(s, 0), offB = Math.Max(-s, 0);
            rs[s] = Correlation(sa, sb, rows, cols, axis, offA, offB, len);
        }
        int best = rs.Aggregate((x, y) => y.Value > x.Value ? y : x).Key;
        double r0 = rs.GetValueOrDefault(best - 1, rs[best]), r1 = rs[best], r2 = rs.GetValueOrDefault(best + 1, rs[best]);
        double d = r0 - 2 * r1 + r2;
        if (r1 <= MinCorrelation) return null;
        return -(best + (Math.Abs(d) < 1e-12 ? 0.0 : 0.5 * (r0 - r2) / d));
    }

    static double Correlation(double[] a, double[] b, int rows, int cols, int axis, int offA, int offB, int len)
    {
        int nr = axis == 0 ? len : rows, nc = axis == 0 ? cols : len;
        double ma = 0, mb = 0;
        for (int r = 0; r < nr; r++)
            for (int c = 0; c < nc; c++)
            {
                ma += axis == 0 ? a[(r + offA) * cols + c] : a[r * cols + c + offA];
                mb += axis == 0 ? b[(r + offB) * cols + c] : b[r * cols + c + offB];
            }
        int count = nr * nc;
        ma /= count; mb /= count;
        double sab = 0, saa = 0, sbb = 0;
        for (int r = 0; r < nr; r++)
            for (int c = 0; c < nc; c++)
            {
                double ha = (axis == 0 ? a[(r + offA) * cols + c] : a[r * cols + c + offA]) - ma;
                double hb = (axis == 0 ? b[(r + offB) * cols + c] : b[r * cols + c + offB]) - mb;
                sab += ha * hb; saa += ha * ha; sbb += hb * hb;
            }
        return sab / Math.Sqrt(saa * sbb + 1e-9);
    }

    static double Std(double[] v)
    {
        double m = v.Average(), s = 0;
        foreach (var x in v) s += (x - m) * (x - m);
        return Math.Sqrt(s / v.Length);
    }

    /// <summary>The four overlap strips of one capture, rendered in grey at the tile scale (1024 px per block).</summary>
    sealed class Strips
    {
        public int By, B, W;
        public double Ppm, Size;
        /// <summary>East and west strips: B rows x W columns; north and south: W rows x B columns.</summary>
        public float[] Right = [], Left = [], Bottom = [], Top = [];

        public static Strips Render(Capture cap, double qx, double qy)
        {
            var grid = SurfaceHeights.Read(cap.Heights);
            grid.FillMissing(cap.Camera.GroundZ);
            var rgba = Images.LoadRgba(cap.Png, out var w, out var h);
            var gray = new double[w * h];
            for (int i = 0; i < gray.Length; i++)
                gray[i] = (rgba[4 * i] * 19595 + rgba[4 * i + 1] * 38470 + rgba[4 * i + 2] * 7471 + 0x8000) >> 16;
            double size = grid.Size, ppm = 1024 / size;
            int e = (int)(Extend * ppm), b = (int)(size * ppm), sw = (int)((2 * Extend - 0.6) * ppm);
            int s0 = (int)((size - Extend + 0.3) * ppm + e);
            var cam = cap.Camera;
            double hCam = cam.GroundZ + cam.Height, fpx = (h / 2.0) / Math.Tan(cam.Fov * (Math.PI / 180.0) / 2);
            float Sample(int row, int col)
            {
                double mx = (col + 0.5) / ppm - Extend, my = (row + 0.5) / ppm - Extend;
                double x = grid.X0 + mx, y = grid.Y0 - my;
                double gi = Math.Clamp((x - grid.X0) / grid.Step, 0, grid.N - 1), gj = Math.Clamp((grid.Y0 - y) / grid.Step, 0, grid.N - 1);
                double z = Orthorectifier.Bilinear(grid.Values, grid.N, grid.N, 1, 0, gj, gi);
                double u = w / 2.0 + (x - cam.X) * (fpx / qx / (hCam - z)), v = h / 2.0 - (y - cam.Y) * (fpx / qy / (hCam - z));
                double cr = v - 0.5, cc = u - 0.5;
                if (cr < 0 || cr > h - 1 || cc < 0 || cc > w - 1) return float.NaN;
                return (float)Orthorectifier.Bilinear(gray, h, w, 1, 0, cr, cc);
            }
            float[] Block(int r0, int c0, int rows, int cols)
            {
                var o = new float[rows * cols];
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++) o[r * cols + c] = Sample(r0 + r, c0 + c);
                return o;
            }
            return new Strips
            {
                By = BlockId.At(cam.X, cam.Y).By, B = b, W = sw, Ppm = ppm, Size = size,
                Right = Block(e, s0, b, sw), Left = Block(e, s0 - b, b, sw),
                Bottom = Block(s0, e, sw, b), Top = Block(s0 - b, e, sw, b),
            };
        }
    }
}
