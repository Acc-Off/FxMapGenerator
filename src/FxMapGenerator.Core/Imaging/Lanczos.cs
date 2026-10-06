namespace FxMapGenerator.Core.Imaging;

/// <summary>
/// Lanczos-3 resampling of 8-bit RGBA with the arithmetic of the common "separable fixed-point" scheme, so results
/// are reproducible to the last bit:
/// <list type="bullet">
/// <item>the filter is widened by the scale when shrinking; each output pixel's weights are normalised to 1 and turned
/// into 22-bit fixed point (rounded half away from zero);</item>
/// <item>colours are premultiplied by alpha before and unpremultiplied after (8-bit, rounded);</item>
/// <item>the horizontal pass runs first and stores 8-bit values, then the vertical pass.</item>
/// </list>
/// Single-threaded: callers run many images in parallel.
/// </summary>
public static class Lanczos
{
    const int PrecisionBits = 32 - 8 - 2;
    const double Support = 3.0;

    /// <summary>Resizes straight-alpha RGBA (rows top-down) from <paramref name="w"/> x <paramref name="h"/>.</summary>
    public static byte[] Resize(byte[] rgba, int w, int h, int outW, int outH)
    {
        if (outW == w && outH == h) return (byte[])rgba.Clone();
        var pre = Premultiply(rgba);
        byte[] cur = pre;
        int cw = w, ch = h;
        var (vBounds, vKernel, vSize) = Coefficients(h, outH);
        if (outW != w)
        {
            // Only the rows the vertical pass reads are resampled horizontally.
            int first = vBounds[0], last = vBounds[(outH - 1) * 2] + vBounds[(outH - 1) * 2 + 1];
            cur = Horizontal(cur, w, first, last - first, outW);
            for (int i = 0; i < outH; i++) vBounds[i * 2] -= first;
            cw = outW;
            ch = last - first;
        }
        if (outH != h) cur = Vertical(cur, cw, ch, outH, vBounds, vKernel, vSize);
        return Unpremultiply(cur);
    }

    static (int[] Bounds, int[] Kernel, int KSize) Coefficients(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize;
        double filterscale = Math.Max(scale, 1.0);
        double support = Support * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new int[outSize * 2];
        var kernel = new int[outSize * ksize];
        var k = new double[ksize];
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            double ww = 0.0, ss = 1.0 / filterscale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;
            for (int x = 0; x < xmax; x++)
            {
                double wgt = Filter((x + xmin - center + 0.5) * ss);
                k[x] = wgt;
                ww += wgt;
            }
            for (int x = 0; x < xmax; x++)
            {
                if (ww != 0.0) k[x] /= ww;
                double v = k[x] * (1 << PrecisionBits);
                kernel[xx * ksize + x] = v < 0 ? (int)(-0.5 + v) : (int)(0.5 + v);
            }
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        return (bounds, kernel, ksize);
    }

    static double Filter(double x)
    {
        if (-3.0 <= x && x < 3.0) return Sinc(x) * Sinc(x / 3);
        return 0.0;
    }

    static double Sinc(double x)
    {
        if (x == 0.0) return 1.0;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    static byte Clip(int v)
    {
        v >>= PrecisionBits;
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    static byte[] Horizontal(byte[] src, int w, int firstRow, int rows, int outW)
    {
        var (bounds, kernel, ksize) = Coefficients(w, outW);
        var dst = new byte[outW * rows * 4];
        for (int y = 0; y < rows; y++)
        {
            int srow = (y + firstRow) * w * 4, drow = y * outW * 4;
            for (int xx = 0; xx < outW; xx++)
            {
                int xmin = bounds[xx * 2], xmax = bounds[xx * 2 + 1], kb = xx * ksize;
                int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0, s3 = s0;
                for (int x = 0; x < xmax; x++)
                {
                    int p = srow + (x + xmin) * 4, kv = kernel[kb + x];
                    s0 += src[p] * kv; s1 += src[p + 1] * kv; s2 += src[p + 2] * kv; s3 += src[p + 3] * kv;
                }
                int o = drow + xx * 4;
                dst[o] = Clip(s0); dst[o + 1] = Clip(s1); dst[o + 2] = Clip(s2); dst[o + 3] = Clip(s3);
            }
        }
        return dst;
    }

    static byte[] Vertical(byte[] src, int w, int h, int outH, int[] bounds, int[] kernel, int ksize)
    {
        var dst = new byte[w * outH * 4];
        for (int yy = 0; yy < outH; yy++)
        {
            int ymin = bounds[yy * 2], ymax = bounds[yy * 2 + 1], kb = yy * ksize, drow = yy * w * 4;
            for (int xx = 0; xx < w; xx++)
            {
                int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0, s3 = s0;
                for (int y = 0; y < ymax; y++)
                {
                    int p = ((ymin + y) * w + xx) * 4, kv = kernel[kb + y];
                    s0 += src[p] * kv; s1 += src[p + 1] * kv; s2 += src[p + 2] * kv; s3 += src[p + 3] * kv;
                }
                int o = drow + xx * 4;
                dst[o] = Clip(s0); dst[o + 1] = Clip(s1); dst[o + 2] = Clip(s2); dst[o + 3] = Clip(s3);
            }
        }
        return dst;
    }

    static byte[] Premultiply(byte[] rgba)
    {
        var o = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            uint a = rgba[i + 3];
            o[i] = MulDiv255(rgba[i], a); o[i + 1] = MulDiv255(rgba[i + 1], a); o[i + 2] = MulDiv255(rgba[i + 2], a); o[i + 3] = (byte)a;
        }
        return o;
    }

    static byte MulDiv255(uint v, uint a)
    {
        uint t = v * a + 128;
        return (byte)(((t >> 8) + t) >> 8);
    }

    static byte[] Unpremultiply(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            uint a = rgba[i + 3];
            if (a == 255 || a == 0) continue;
            rgba[i] = (byte)Math.Min(255u, 255u * rgba[i] / a);
            rgba[i + 1] = (byte)Math.Min(255u, 255u * rgba[i + 1] / a);
            rgba[i + 2] = (byte)Math.Min(255u, 255u * rgba[i + 2] / a);
        }
        return rgba;
    }
}
