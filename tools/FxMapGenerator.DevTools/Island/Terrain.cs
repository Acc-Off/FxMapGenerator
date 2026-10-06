namespace FxMapGenerator.DevTools.Island;

/// <summary>
/// Landscape evolution on a square grid: tectonic uplift against fluvial incision by the stream power law
/// (dh/dt = U - K A^m S, n = 1), solved implicitly along the drainage in O(n) per step (Braun and Willett 2013,
/// "A very efficient O(n), implicit and parallel method to solve the stream power equation"), with linear hillslope
/// diffusion. The sea (height at or below 0 in the start grid) and the grid's west edge are the base level; the other
/// edges are closed. Pits are filled every step (lakes fill with sediment), so all water reaches the base level.
/// </summary>
static class LandscapeEvolution
{
    public sealed record Settings(double Dx, int Steps, double Dt, double K, double M, double Diffusion);

    /// <summary>At most 4 threads (a short run beside other work).</summary>
    static readonly ParallelOptions Limited = new() { MaxDegreeOfParallelism = 4 };

    public static float[] Evolve(int w, int h, float[] start, float[] uplift, Settings s, Action<string>? log = null)
    {
        int n = w * h;
        var z = (float[])start.Clone();
        var outlet = new bool[n];
        for (int i = 0; i < n; i++) outlet[i] = start[i] <= 0 || i % w == 0;
        var rec = new int[n];
        var dist = new float[n];
        var area = new double[n];
        var order = new int[n];
        var lap = new float[n];
        double cell = s.Dx * s.Dx;
        for (int step = 0; step < s.Steps; step++)
        {
            Flood.Fill(z, w, h, outlet, 1e-3f);
            // receivers: the steepest way down among the 8 neighbours
            Parallel.For(0, h, Limited, r =>
            {
                for (int c = 0; c < w; c++)
                {
                    int i = r * w + c;
                    rec[i] = i;
                    if (outlet[i]) continue;
                    double best = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        int rr = r + Grid8.Dr[k], cc = c + Grid8.Dc[k];
                        if ((uint)rr >= (uint)h || (uint)cc >= (uint)w) continue;
                        int j = rr * w + cc;
                        double d = Grid8.Len[k] * s.Dx, slope = (z[i] - z[j]) / d;
                        if (slope > best) { best = slope; rec[i] = j; dist[i] = (float)d; }
                    }
                }
            });
            // the drainage area, from the top down
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort((float[])z.Clone(), order);
            Array.Fill(area, cell);
            for (int k = n - 1; k >= 0; k--)
            {
                int i = order[k];
                if (rec[i] != i) area[rec[i]] += area[i];
            }
            // uplift and incision, from the base level up (the receiver is solved before its donors)
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                if (outlet[i]) continue;
                double zi = z[i] + uplift[i] * s.Dt;
                int r = rec[i];
                if (r == i) { z[i] = (float)zi; continue; }
                double f = s.K * s.Dt * Math.Pow(area[i], s.M) / dist[i];
                z[i] = (float)((zi + f * z[r]) / (1 + f));
            }
            // hillslope diffusion
            if (s.Diffusion > 0)
            {
                double a = s.Diffusion * s.Dt / cell;
                Parallel.For(1, h - 1, Limited, r =>
                {
                    for (int c = 1; c < w - 1; c++)
                    {
                        int i = r * w + c;
                        lap[i] = outlet[i] ? 0 : (float)(a * (z[i - 1] + z[i + 1] + z[i - w] + z[i + w] - 4 * z[i]));
                    }
                });
                for (int i = 0; i < n; i++) z[i] += lap[i];
            }
            for (int i = 0; i < n; i++) if (!outlet[i] && z[i] < 0.3f) z[i] = 0.3f;
            if (log is not null && (step + 1) % 100 == 0) log($"  step {step + 1}: highest {z.Max():0.0}");
        }
        Flood.Fill(z, w, h, outlet, 1e-3f);
        return z;
    }
}

static class Grid8
{
    public static readonly int[] Dr = [-1, -1, -1, 0, 0, 1, 1, 1];
    public static readonly int[] Dc = [-1, 0, 1, -1, 1, -1, 0, 1];
    public static readonly double[] Len = [Math.Sqrt(2), 1, Math.Sqrt(2), 1, 1, Math.Sqrt(2), 1, Math.Sqrt(2)];
}

/// <summary>
/// Priority-Flood (Barnes, Lehman and Mulla 2014): every cell raised to at least the lowest way out to an outlet plus
/// a small rise per cell, so every cell drains. Returns nothing; the grid is changed in place.
/// </summary>
static class Flood
{
    public static void Fill(float[] z, int w, int h, bool[] outlet, float rise)
    {
        int n = w * h;
        var done = new bool[n];
        var queue = new PriorityQueue<int, float>(n / 4);
        for (int i = 0; i < n; i++)
            if (outlet[i]) { done[i] = true; queue.Enqueue(i, z[i]); }
        while (queue.TryDequeue(out int i, out _))
        {
            int r = i / w, c = i % w;
            for (int k = 0; k < 8; k++)
            {
                int rr = r + Grid8.Dr[k], cc = c + Grid8.Dc[k];
                if ((uint)rr >= (uint)h || (uint)cc >= (uint)w) continue;
                int j = rr * w + cc;
                if (done[j]) continue;
                done[j] = true;
                if (z[j] < z[i] + rise) z[j] = z[i] + rise;
                queue.Enqueue(j, z[j]);
            }
        }
    }

    /// <summary>The drainage area (m²) of every cell of a filled grid, by the steepest way down.</summary>
    public static double[] Area(float[] z, int w, int h, double dx, bool[] outlet, out int[] receiver)
    {
        int n = w * h;
        var rec = new int[n];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c;
                rec[i] = i;
                if (outlet[i]) continue;
                double best = 0;
                for (int k = 0; k < 8; k++)
                {
                    int rr = r + Grid8.Dr[k], cc = c + Grid8.Dc[k];
                    if ((uint)rr >= (uint)h || (uint)cc >= (uint)w) continue;
                    int j = rr * w + cc;
                    double slope = (z[i] - z[j]) / (Grid8.Len[k] * dx);
                    if (slope > best) { best = slope; rec[i] = j; }
                }
            }
        var order = Enumerable.Range(0, n).ToArray();
        Array.Sort((float[])z.Clone(), order);
        var area = new double[n];
        Array.Fill(area, dx * dx);
        for (int k = n - 1; k >= 0; k--)
        {
            int i = order[k];
            if (rec[i] != i) area[rec[i]] += area[i];
        }
        receiver = rec;
        return area;
    }
}

/// <summary>Gradient noise and its sums, the same for the same seed on every run.</summary>
static class Noise
{
    static double Hash(int a, int b, int seed)
    {
        uint h = unchecked((uint)(a * 374761393 + b * 668265263 + seed * 2147483647));
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (double)0x1000000;
    }

    public static double Perlin(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double G(int ix, int iy, double dx, double dy)
        {
            double a = Hash(ix, iy, seed) * 2 * Math.PI;
            return Math.Cos(a) * dx + Math.Sin(a) * dy;
        }
        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        double u = Fade(fx), v = Fade(fy);
        double a0 = G(x0, y0, fx, fy) + u * (G(x0 + 1, y0, fx - 1, fy) - G(x0, y0, fx, fy));
        double a1 = G(x0, y0 + 1, fx, fy - 1) + u * (G(x0 + 1, y0 + 1, fx - 1, fy - 1) - G(x0, y0 + 1, fx, fy - 1));
        return 1.41 * (a0 + v * (a1 - a0));
    }

    /// <summary>Octaves of gradient noise in about [-1, 1]; <paramref name="scale"/> is the largest feature (m).</summary>
    public static double Fbm(double x, double y, double scale, int octaves, int seed)
    {
        double sum = 0, amp = 1, norm = 0, f = 1 / scale;
        for (int k = 0; k < octaves; k++, amp *= 0.5, f *= 2.03)
        {
            sum += amp * Perlin(x * f, y * f, seed + 17 * k);
            norm += amp;
        }
        return sum / norm;
    }

    public static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
