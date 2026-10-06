namespace FxMapGenerator.Core.Vectors;

/// <summary>
/// The polygon stage of potrace, written from the paper only (P. Selinger, "Potrace: a polygon-based tracing algorithm",
/// 2003, sections 2.1-2.3.1): path decomposition with the minority turn policy, straight subpaths, possible segments,
/// penalties, the optimal polygon and the vertex adjustment. There is no curve stage: the map outlines keep every vertex
/// as a corner, so the output is the adjusted polygon.
/// <para>Where the paper leaves a choice open, the choices are: the next path starts at the first cell found scanning
/// from the last row up; the minority policy looks at squares of radius 3 to 6 around the corner and turns left while
/// the counts stay equal; the optimal polygon has a vertex at the path's first point; on equal segment counts and
/// penalties (within 1e-9) the later predecessor wins.</para>
/// Coordinates: cell (x = column, y = row) covers [x, x+1] x [y, y+1]; outside the mask is off.
/// </summary>
public static class PotracePolygon
{
    const int MinorityMinR = 3, MinorityMaxR = 6;
    const double TieEps = 1e-9;

    public readonly record struct P(int X, int Y);

    /// <summary>Closed paths around the cells that are on (bits[y * w + x]), in the order they are found.</summary>
    public static List<P[]> Decompose(bool[] bits, int w, int h)
    {
        var bm = (bool[])bits.Clone();
        var paths = new List<P[]>();
        bool On(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && bm[y * w + x];
        int scanRow = h - 1, scanCol = 0;
        var path = new List<P>();
        while (true)
        {
            // the next cell that is on, scanning rows from the last one up (resuming where the last one was found)
            int sx = -1, sy = -1;
            for (int y = scanRow; y >= 0; y--)
            {
                for (int x = y == scanRow ? scanCol : 0; x < w; x++)
                    if (bm[y * w + x]) { sx = x; sy = y; break; }
                if (sx >= 0) break;
            }
            if (sx < 0) break;
            scanRow = sy;
            scanCol = sx;
            // its left neighbour and the row below are off: start at their shared corner (sx, sy + 1) going up, with the
            // cell on the left of the direction (left of (dx, dy) is (-dy, dx))
            var start = new P(sx, sy + 1);
            const int d0x = 0, d0y = -1;
            int dx = d0x, dy = d0y;
            path.Clear();
            int px = start.X, py = start.Y;
            do
            {
                path.Add(new P(px, py));
                px += dx;
                py += dy;
                // the two cells ahead of corner (px, py): left and right of the edge that would go straight on
                bool left = On(FloorHalf(2 * px + dx - dy), FloorHalf(2 * py + dy + dx));
                bool right = On(FloorHalf(2 * px + dx + dy), FloorHalf(2 * py + dy - dx));
                if (left && !right) { }                                         // straight on
                else if (!left && !right) (dx, dy) = (-dy, dx);                 // turn left
                else if (left && right) (dx, dy) = (dy, -dx);                   // turn right
                else if (MinorityTurnsRight(px, py, On)) (dx, dy) = (dy, -dx);  // ambiguous corner
                else (dx, dy) = (-dy, dx);
                if (path.Count > 4 * (w + 2) * (h + 2)) throw new InvalidOperationException("path does not close");
            } while (px != start.X || py != start.Y || dx != d0x || dy != d0y);
            var arr = path.ToArray();
            paths.Add(arr);
            // remove the path: flip the cells inside it (each vertical edge flips its row to the right)
            for (int i = 0; i < arr.Length; i++)
            {
                var a = arr[i];
                var b = arr[(i + 1) % arr.Length];
                if (a.X != b.X) continue;
                int row = Math.Min(a.Y, b.Y);
                if (row < 0 || row >= h) continue;
                for (int x = Math.Max(a.X, 0); x < w; x++) bm[row * w + x] = !bm[row * w + x];
            }
        }
        return paths;
    }

    // the cell left / right of the edge from (x, y) to (x + dx, y + dy) has its centre at p + d/2 +- left/2; in doubled
    // coordinates that centre is odd, so the cell index is floor(v / 2)
    static int FloorHalf(int v) => v >> 1;

    /// <summary>
    /// At an ambiguous corner (ahead: left off, right on) turning right keeps the two cells that are on in one path.
    /// The minority policy connects the rarer value in the squares of radius 3..6 around the corner (the first radius
    /// whose counts differ); equal counts turn left.
    /// </summary>
    static bool MinorityTurnsRight(int x, int y, Func<int, int, bool> on)
    {
        for (int r = MinorityMinR; r <= MinorityMaxR; r++)
        {
            int nOn = 0, nOff = 0;
            for (int yy = y - r; yy < y + r; yy++)
                for (int xx = x - r; xx < x + r; xx++)
                    if (on(xx, yy)) nOn++; else nOff++;
            if (nOn != nOff) return nOn < nOff;
        }
        return false;
    }

    /// <summary>The adjusted polygon of one closed path (vertices in path order).</summary>
    public static (double X, double Y)[] Polygon(P[] v)
    {
        int n = v.Length;
        // straight subpaths: pivot[i] = the furthest k (unwrapped) such that the path i..k, seen from v_i, is straight:
        // not all four directions, and every v_b (i < b < k) within max-distance 1 of the line v_i v_k
        var pivot = new int[n];
        Span<(int, int)> corners = [(-1, -1), (-1, 1), (1, -1), (1, 1)];
        for (int i = 0; i < n; i++)
        {
            int dirs = 0;
            long loX = 0, loY = 0, hiX = 0, hiY = 0;
            bool haveLo = false, haveHi = false;
            int last = i + 1;
            for (int k = i + 1; k <= i + n - 1; k++)
            {
                var pa = v[(k - 1) % n];
                var pb = v[k % n];
                dirs |= DirBit(pb.X - pa.X, pb.Y - pa.Y);
                if (dirs == 15) break;
                long dxk = pb.X - v[i].X, dyk = pb.Y - v[i].Y;
                if (haveLo && loX * dyk - loY * dxk < 0) break;
                if (haveHi && hiX * dyk - hiY * dxk > 0) break;
                last = k;
                // the square of max-radius 1 around v_k, seen from v_i: directions between its extreme corners
                if (Math.Max(Math.Abs(dxk), Math.Abs(dyk)) <= 1) continue;
                long cLoX = 0, cLoY = 0, cHiX = 0, cHiY = 0;
                bool first = true;
                foreach (var (ox, oy) in corners)
                {
                    long cx = dxk + ox, cy = dyk + oy;
                    if (first) { cLoX = cHiX = cx; cLoY = cHiY = cy; first = false; continue; }
                    if (cLoX * cy - cLoY * cx < 0) { cLoX = cx; cLoY = cy; }   // more clockwise than the current low bound
                    if (cHiX * cy - cHiY * cx > 0) { cHiX = cx; cHiY = cy; }   // more counter-clockwise than the current high bound
                }
                if (!haveLo || loX * cLoY - loY * cLoX > 0) { loX = cLoX; loY = cLoY; haveLo = true; }
                if (!haveHi || hiX * cHiY - hiY * cHiX < 0) { hiX = cHiX; hiY = cHiY; haveHi = true; }
            }
            pivot[i] = last;
        }
        // lon[i] = the furthest k such that the path i..k is straight seen from every start a in [i, k): running minimum
        var lon = new int[n];
        for (int pass = 0; pass < 3; pass++)
            for (int i = n - 1; i >= 0; i--)
            {
                int next = i == n - 1 ? (pass == 0 ? pivot[0] : lon[0]) + n : lon[i + 1];
                lon[i] = Math.Min(pivot[i], next);
            }
        // possible segment i -> j: j - i <= n - 3 and the path i-1 .. j+1 straight => clip[i] = min(lon[i-1] - 1, i + n - 3)
        var clip = new int[n];
        for (int i = 0; i < n; i++)
        {
            int prev = (i - 1 + n) % n;
            int l = lon[prev] + (i == 0 ? -n : 0);
            clip[i] = Math.Max(i + 1, Math.Min(l - 1, i + n - 3));
        }
        // penalties from prefix sums over the doubled path (coordinates relative to v_0)
        var sx = new double[2 * n + 1];
        var sy = new double[2 * n + 1];
        var sxx = new double[2 * n + 1];
        var sxy = new double[2 * n + 1];
        var syy = new double[2 * n + 1];
        for (int k = 0; k < 2 * n; k++)
        {
            double x = v[k % n].X - v[0].X, y = v[k % n].Y - v[0].Y;
            sx[k + 1] = sx[k] + x; sy[k + 1] = sy[k] + y; sxx[k + 1] = sxx[k] + x * x; sxy[k + 1] = sxy[k] + x * y; syy[k + 1] = syy[k] + y * y;
        }
        double Penalty(int i, int j)                     // unwrapped i < j, j - i < n
        {
            int ii = i, jj = j;
            if (ii >= n) { ii -= n; jj -= n; }
            double cnt = jj - ii + 1;
            double ex = (sx[jj + 1] - sx[ii]) / cnt, ey = (sy[jj + 1] - sy[ii]) / cnt;
            double exx = (sxx[jj + 1] - sxx[ii]) / cnt, exy = (sxy[jj + 1] - sxy[ii]) / cnt, eyy = (syy[jj + 1] - syy[ii]) / cnt;
            var a = v[ii % n];
            var b = v[jj % n];
            double xa = a.X - v[0].X, ya = a.Y - v[0].Y, xb = b.X - v[0].X, yb = b.Y - v[0].Y;
            double x = xb - xa, y = yb - ya, mx = (xa + xb) / 2, my = (ya + yb) / 2;
            double A = exx - 2 * mx * ex + mx * mx, B = exy - mx * ey - my * ex + mx * my, C = eyy - 2 * my * ey + my * my;
            double s = C * x * x - 2 * B * x * y + A * y * y;
            return Math.Sqrt(Math.Max(0, s));
        }
        // optimal polygon: fewest segments, then least total penalty, with a vertex at path point 0
        int[] best = BestFrom(0, n, clip, Penalty);
        // vertex adjustment
        int m = best.Length;
        var lines = new (double Cx, double Cy, double Nx, double Ny)[m];
        for (int k = 0; k < m; k++)
        {
            int i = best[k], j = best[(k + 1) % m];
            if (j <= i) j += n;
            double cnt = j - i + 1, ex = 0, ey = 0, exx = 0, exy = 0, eyy = 0;
            for (int t = i; t <= j; t++)
            {
                double x = v[t % n].X, y = v[t % n].Y;
                ex += x; ey += y; exx += x * x; exy += x * y; eyy += y * y;
            }
            ex /= cnt; ey /= cnt; exx /= cnt; exy /= cnt; eyy /= cnt;
            double a = exx - ex * ex, b = exy - ex * ey, c = eyy - ey * ey;
            // eigenvector of the larger eigenvalue of [[a, b], [b, c]] = the line's direction; the normal is perpendicular
            double lam = (a + c) / 2 + Math.Sqrt((a - c) * (a - c) / 4 + b * b);
            double dxl, dyl;
            if (Math.Abs(b) > 1e-12) { dxl = lam - c; dyl = b; }
            else if (a >= c) { dxl = 1; dyl = 0; }
            else { dxl = 0; dyl = 1; }
            if (Math.Abs(dxl) < 1e-12 && Math.Abs(dyl) < 1e-12) { dxl = v[j % n].X - v[i % n].X; dyl = v[j % n].Y - v[i % n].Y; }
            double len = Math.Sqrt(dxl * dxl + dyl * dyl);
            lines[k] = (ex, ey, -dyl / len, dxl / len);
        }
        var outPts = new (double X, double Y)[m];
        for (int k = 0; k < m; k++)
        {
            var p0 = v[best[k] % n];
            outPts[k] = MinOnSquare(lines[(k - 1 + m) % m], lines[k], p0.X, p0.Y);
        }
        return outPts;
    }

    static int DirBit(int dx, int dy) => dx > 0 ? 1 : dx < 0 ? 2 : dy > 0 ? 4 : 8;

    /// <summary>Fewest segments from s to s + n (then least penalty; on a tie the later predecessor); the vertex indices, s first.</summary>
    static int[] BestFrom(int s, int n, int[] clip, Func<int, int, double> pen)
    {
        int len = n + 1;
        var cnt = new int[len];
        var pn = new double[len];
        var prev = new int[len];
        for (int t = 1; t < len; t++) { cnt[t] = int.MaxValue; pn[t] = double.MaxValue; }
        int Clip(int u) { int i = (s + u) % n; return clip[i] - i + u; }
        for (int u = 0; u < n; u++)
        {
            if (cnt[u] == int.MaxValue) continue;
            int hi = Math.Min(Clip(u), n);
            for (int t = u + 1; t <= hi; t++)
            {
                int c = cnt[u] + 1;
                if (c > cnt[t]) continue;
                double p = pn[u] + pen(s + u, s + t);
                if (c < cnt[t] || p <= pn[t] + TieEps) { cnt[t] = c; pn[t] = p; prev[t] = u; }
            }
        }
        var idx = new List<int>();
        for (int t = n; t > 0; t = prev[t]) idx.Add((s + prev[t]) % n);
        idx.Reverse();
        return idx.ToArray();
    }

    /// <summary>The point of the square |p - (x0, y0)|max &lt;= 1/2 with the least sum of squared distances to the two lines.</summary>
    static (double X, double Y) MinOnSquare((double Cx, double Cy, double Nx, double Ny) l1, (double Cx, double Cy, double Nx, double Ny) l2, double x0, double y0)
    {
        // Q(p) = sum ((p - c) . n)^2 = p'Ap - 2 b'p + const
        double a11 = l1.Nx * l1.Nx + l2.Nx * l2.Nx, a12 = l1.Nx * l1.Ny + l2.Nx * l2.Ny, a22 = l1.Ny * l1.Ny + l2.Ny * l2.Ny;
        double d1 = l1.Nx * l1.Cx + l1.Ny * l1.Cy, d2 = l2.Nx * l2.Cx + l2.Ny * l2.Cy;
        double b1 = l1.Nx * d1 + l2.Nx * d2, b2 = l1.Ny * d1 + l2.Ny * d2;
        double Q(double x, double y) => a11 * x * x + 2 * a12 * x * y + a22 * y * y - 2 * (b1 * x + b2 * y);
        double det = a11 * a22 - a12 * a12;
        if (det > 1e-9)
        {
            double x = (a22 * b1 - a12 * b2) / det, y = (a11 * b2 - a12 * b1) / det;
            if (Math.Abs(x - x0) <= 0.5 && Math.Abs(y - y0) <= 0.5) return (x, y);
        }
        // on the boundary: each edge is a 1-D quadratic; also the corners
        (double X, double Y) bestP = (x0, y0);
        double bestQ = double.MaxValue;
        void Try(double x, double y) { double q = Q(x, y); if (q < bestQ) { bestQ = q; bestP = (x, y); } }
        foreach (var yy in new[] { y0 - 0.5, y0 + 0.5 })
        {
            if (a11 > 1e-12) { double x = (b1 - a12 * yy) / a11; if (Math.Abs(x - x0) <= 0.5) Try(x, yy); }
            Try(x0 - 0.5, yy);
            Try(x0 + 0.5, yy);
        }
        foreach (var xx in new[] { x0 - 0.5, x0 + 0.5 })
            if (a22 > 1e-12) { double y = (b2 - a12 * xx) / a22; if (Math.Abs(y - y0) <= 0.5) Try(xx, y); }
        if (det <= 1e-9)
        {
            // parallel lines: the least-squares set is a line; the point of it nearest to the original vertex, if inside
            double sgn = l1.Nx * l2.Nx + l1.Ny * l2.Ny >= 0 ? 1 : -1;
            double nx = l1.Nx + sgn * l2.Nx, ny = l1.Ny + sgn * l2.Ny;
            double nl = Math.Sqrt(nx * nx + ny * ny);
            if (nl > 1e-12)
            {
                nx /= nl;
                ny /= nl;
                double c = (b1 * nx + b2 * ny) / Math.Max(1e-12, a11 * nx * nx + 2 * a12 * nx * ny + a22 * ny * ny);
                double t = c - (nx * x0 + ny * y0);
                double x = x0 + t * nx, y = y0 + t * ny;
                if (Math.Abs(x - x0) <= 0.5 && Math.Abs(y - y0) <= 0.5) Try(x, y);
            }
        }
        return bestP;
    }
}
