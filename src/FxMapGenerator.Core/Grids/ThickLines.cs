namespace FxMapGenerator.Core.Grids;

/// <summary>
/// Thick polylines on a mask, cell for cell as OpenCV's <c>polylines(img, [points], isClosed=False, 1, thickness)</c> draws
/// them (8-connected, no sub-pixel shift): each segment is a filled convex quadrilateral half the thickness to either side
/// with its edges traced, and a filled disc of radius (thickness + 1) / 2 at the first point and at the end of every
/// segment. A segment with an end outside the grid is first cut to the grid widened by the thickness. The integer and
/// rounding steps follow OpenCV's drawing code (<c>modules/imgproc/src/drawing.cpp</c> of OpenCV 5.0.0: PolyLine,
/// ThickLine, FillConvexPoly, Line2, Circle, clipLine; Apache-2.0 and the Intel license of that file, see
/// THIRD-PARTY-NOTICES.md). Only thicknesses of 2 and more (OpenCV draws a thickness of 1 another way).
/// </summary>
public static class ThickLines
{
    const int XyShift = 16;
    const long XyOne = 1L << XyShift;
    const double InvXyOne = 1.0 / XyOne;
    const double DblEpsilon = 2.220446049250313e-16;

    /// <summary>Sets the cells under the open polyline through <paramref name="points"/> (cell columns x, rows y).</summary>
    public static void Polyline(Grid<bool> img, IReadOnlyList<(int X, int Y)> points, int thickness)
    {
        if (thickness < 2) throw new ArgumentOutOfRangeException(nameof(thickness), "thickness 2 or more");
        if (points.Count == 0) return;
        int flags = 3;                                      // the first segment gets a disc at both ends, the rest at their end
        var p0 = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            var p = points[i];
            Segment(img, p0.X, p0.Y, p.X, p.Y, thickness, flags);
            p0 = p;
            flags = 2;
        }
    }

    static void Segment(Grid<bool> img, long x0, long y0, long x1, long y1, int thickness, int flags)
    {
        int w = img.Width, h = img.Height;
        if (!Inside(x0, y0, w, h) || !Inside(x1, y1, w, h))
        {
            long m = thickness;
            x0 += m; y0 += m; x1 += m; y1 += m;
            ClipLine(w + 2 * m, h + 2 * m, ref x0, ref y0, ref x1, ref y1);      // the result is not checked, as in OpenCV
            x0 -= m; y0 -= m; x1 -= m; y1 -= m;
        }
        x0 <<= XyShift; y0 <<= XyShift; x1 <<= XyShift; y1 <<= XyShift;

        double dx = (x0 - x1) * InvXyOne, dy = (y1 - y0) * InvXyOne;
        double r = dx * dx + dy * dy;
        int odd = thickness & 1;
        long half = (long)thickness << (XyShift - 1);
        if (Math.Abs(r) > DblEpsilon)
        {
            r = (half + odd * XyOne * 0.5) / Math.Sqrt(r);
            long dpx = CvRound(dy * r), dpy = CvRound(dx * r);
            FillConvexQuad(img, [(x0 + dpx, y0 + dpy), (x0 - dpx, y0 - dpy), (x1 - dpx, y1 - dpy), (x1 + dpx, y1 + dpy)]);
        }
        for (int i = 0; i < 2; i++)
        {
            if ((flags & (i + 1)) != 0)
                Disc(img, (int)((x0 + (XyOne >> 1)) >> XyShift), (int)((y0 + (XyOne >> 1)) >> XyShift), (int)((half + (XyOne >> 1)) >> XyShift));
            x0 = x1;
            y0 = y1;
        }
    }

    static bool Inside(long x, long y, int w, int h) => x >= 0 && x < w && y >= 0 && y < h;

    /// <summary>OpenCV's <c>cvRound</c> on x64: to the nearest integer, a tie to the even one.</summary>
    static long CvRound(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    /// <summary>Cuts the line to 0..width-1 x 0..height-1 (Cohen-Sutherland with OpenCV's integer steps); false when it misses.</summary>
    static bool ClipLine(long width, long height, ref long x1, ref long y1, ref long x2, ref long y2)
    {
        if (width <= 0 || height <= 0) return false;
        long right = width - 1, bottom = height - 1;
        int Code(long x, long y) => (x < 0 ? 1 : 0) + (x > right ? 2 : 0) + (y < 0 ? 4 : 0) + (y > bottom ? 8 : 0);
        int c1 = Code(x1, y1), c2 = Code(x2, y2);
        if ((c1 & c2) == 0 && (c1 | c2) != 0)
        {
            long a;
            if ((c1 & 12) != 0)
            {
                a = c1 < 8 ? 0 : bottom;
                x1 += (long)((double)(a - y1) * (x2 - x1) / (y2 - y1));
                y1 = a;
                c1 = (x1 < 0 ? 1 : 0) + (x1 > right ? 2 : 0);
            }
            if ((c2 & 12) != 0)
            {
                a = c2 < 8 ? 0 : bottom;
                x2 += (long)((double)(a - y2) * (x2 - x1) / (y2 - y1));
                y2 = a;
                c2 = (x2 < 0 ? 1 : 0) + (x2 > right ? 2 : 0);
            }
            if ((c1 & c2) == 0 && (c1 | c2) != 0)
            {
                if (c1 != 0)
                {
                    a = c1 == 1 ? 0 : right;
                    y1 += (long)((double)(a - x1) * (y2 - y1) / (x2 - x1));
                    x1 = a;
                    c1 = 0;
                }
                if (c2 != 0)
                {
                    a = c2 == 1 ? 0 : right;
                    y2 += (long)((double)(a - x2) * (y2 - y1) / (x2 - x1));
                    x2 = a;
                    c2 = 0;
                }
            }
        }
        return (c1 | c2) == 0;
    }

    static void Put(Grid<bool> img, long x, long y)
    {
        if (x >= 0 && x < img.Width && y >= 0 && y < img.Height) img.Data[y * img.Width + x] = true;
    }

    /// <summary>Cells xl..xr of row y (all inside the grid).</summary>
    static void HLine(Grid<bool> img, long y, long xl, long xr)
    {
        if (xr < xl) return;
        Array.Fill(img.Data, true, (int)(y * img.Width + xl), (int)(xr - xl + 1));
    }

    /// <summary>A line between two points in 1/65536 cells (OpenCV's <c>Line2</c>): one cell per step along the longer axis.</summary>
    static void Line2(Grid<bool> img, long x1, long y1, long x2, long y2)
    {
        if (!ClipLine((long)img.Width << XyShift, (long)img.Height << XyShift, ref x1, ref y1, ref x2, ref y2)) return;
        long dx = x2 - x1, dy = y2 - y1;
        long j = dx < 0 ? -1 : 0, ax = (dx ^ j) - j;
        long i = dy < 0 ? -1 : 0, ay = (dy ^ i) - i;
        long xStep, yStep;
        int ecount;
        if (ax > ay)
        {
            dy = (dy ^ j) - j;
            x1 ^= x2 & j; x2 ^= x1 & j; x1 ^= x2 & j;          // the ends swapped when dx < 0
            y1 ^= y2 & j; y2 ^= y1 & j; y1 ^= y2 & j;
            xStep = XyOne;
            yStep = dy * (1 << XyShift) / (ax | 1);
            ecount = (int)((x2 - x1) >> XyShift);
        }
        else
        {
            dx = (dx ^ i) - i;
            x1 ^= x2 & i; x2 ^= x1 & i; x1 ^= x2 & i;          // swapped when dy < 0
            y1 ^= y2 & i; y2 ^= y1 & i; y1 ^= y2 & i;
            xStep = dx * (1 << XyShift) / (ay | 1);
            yStep = XyOne;
            ecount = (int)((y2 - y1) >> XyShift);
        }
        x1 += XyOne >> 1;
        y1 += XyOne >> 1;
        Put(img, (x2 + (XyOne >> 1)) >> XyShift, (y2 + (XyOne >> 1)) >> XyShift);
        if (ax > ay)
        {
            x1 >>= XyShift;
            for (; ecount >= 0; ecount--, x1++, y1 += yStep) Put(img, x1, y1 >> XyShift);
        }
        else
        {
            y1 >>= XyShift;
            for (; ecount >= 0; ecount--, x1 += xStep, y1++) Put(img, x1 >> XyShift, y1);
        }
    }

    /// <summary>A filled convex polygon of points in 1/65536 cells (OpenCV's <c>FillConvexPoly</c>, 8-connected): its edges traced, then row spans between the left and the right edge.</summary>
    static void FillConvexQuad(Grid<bool> img, (long X, long Y)[] v)
    {
        const int shift = XyShift;
        const long delta = 1L << shift >> 1;
        const long delta1 = XyOne >> 1, delta2 = XyOne >> 1;
        int npts = v.Length, w = img.Width, h = img.Height;
        int imin = 0, edges = npts;
        long xmin = v[0].X, xmax = v[0].X, ymin = v[0].Y, ymax = v[0].Y;
        var p0 = v[npts - 1];
        for (int k = 0; k < npts; k++)
        {
            var p = v[k];
            if (p.Y < ymin) { ymin = p.Y; imin = k; }
            ymax = Math.Max(ymax, p.Y);
            xmax = Math.Max(xmax, p.X);
            xmin = Math.Min(xmin, p.X);
            Line2(img, p0.X, p0.Y, p.X, p.Y);
            p0 = p;
        }
        xmin = (xmin + delta) >> shift;
        xmax = (xmax + delta) >> shift;
        ymin = (ymin + delta) >> shift;
        ymax = (ymax + delta) >> shift;
        if (npts < 3 || (int)xmax < 0 || (int)ymax < 0 || (int)xmin >= w || (int)ymin >= h) return;
        ymax = Math.Min(ymax, h - 1);

        Span<int> eIdx = stackalloc int[2], eDi = stackalloc int[2], eYe = stackalloc int[2];
        Span<long> eX = stackalloc long[2], eDx = stackalloc long[2];
        eIdx[0] = eIdx[1] = imin;
        int y = (int)ymin;
        eYe[0] = eYe[1] = y;
        eDi[0] = 1;
        eDi[1] = npts - 1;
        eX[0] = eX[1] = -XyOne;
        eDx[0] = eDx[1] = 0;
        do
        {
            for (int s = 0; s < 2; s++)
            {
                if (y < eYe[s]) continue;
                int idx0 = eIdx[s], di = eDi[s];
                int idx = idx0 + di;
                if (idx >= npts) idx -= npts;
                for (; edges-- > 0;)
                {
                    int ty = (int)((v[idx].Y + delta) >> shift);
                    if (ty > y)
                    {
                        long xs = v[idx0].X, xe = v[idx].X;
                        eYe[s] = ty;
                        eDx[s] = ((xe - xs) * 2 + ((long)ty - y)) / (2 * ((long)ty - y));
                        eX[s] = xs;
                        eIdx[s] = idx;
                        break;
                    }
                    idx0 = idx;
                    idx += di;
                    if (idx >= npts) idx -= npts;
                }
            }
            if (edges < 0) break;
            if (y >= 0)
            {
                int left = eX[0] > eX[1] ? 1 : 0, right = 1 - left;
                long xx1 = (eX[left] + delta1) >> XyShift, xx2 = (eX[right] + delta2) >> XyShift;
                if (xx2 >= 0 && xx1 < w) HLine(img, y, Math.Max(xx1, 0), Math.Min(xx2, w - 1));
            }
            eX[0] += eDx[0];
            eX[1] += eDx[1];
        }
        while (++y <= (int)ymax);
    }

    /// <summary>A filled disc (OpenCV's <c>Circle</c> with fill): row spans from the integer circle, cut to the grid.</summary>
    static void Disc(Grid<bool> img, int cx, int cy, int radius)
    {
        int w = img.Width, h = img.Height;
        long err = 0, dx = radius, dy = 0, plus = 1, minus = ((long)radius << 1) - 1;
        while (dx >= dy)
        {
            long y11 = cy - dy, y12 = cy + dy, y21 = cy - dx, y22 = cy + dx;
            long x11 = cx - dx, x12 = cx + dx, x21 = cx - dy, x22 = cx + dy;
            if (x11 < w && x12 >= 0 && y21 < h && y22 >= 0)
            {
                long a = Math.Max(x11, 0), b = Math.Min(x12, w - 1);
                if (y11 >= 0 && y11 < h) HLine(img, y11, a, b);
                if (y12 >= 0 && y12 < h) HLine(img, y12, a, b);
                if (x21 < w && x22 >= 0)
                {
                    long c = Math.Max(x21, 0), d = Math.Min(x22, w - 1);
                    if (y21 >= 0 && y21 < h) HLine(img, y21, c, d);
                    if (y22 >= 0 && y22 < h) HLine(img, y22, c, d);
                }
            }
            dy++;
            err += plus;
            plus += 2;
            long mask = (err <= 0 ? 1 : 0) - 1;               // 0 while inside, -1 when x steps in
            err -= minus & mask;
            dx += mask;
            minus -= mask & 2;
        }
    }
}
