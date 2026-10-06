namespace FxMapGenerator.Core.Grids;

/// <summary>
/// Polygons and lines set into a mask with the cells of Pillow 10.4's <c>ImageDraw</c> (Draw.c; the label placement's
/// occupancy and the street centre lines are drawn with it): coordinates cut to whole numbers toward zero, a
/// polygon filled scan line by scan line (float32 crossings, Pillow's joining of corners that meet on a scan line,
/// horizontal edges drawn as they are), a line of width 1 as Bresenham segments plus its last point, a wider line as
/// one filled quadrilateral per segment. Cells outside the mask are dropped. Written after <c>polygon_generic</c>,
/// <c>add_edge</c>, <c>ImagingDrawPolygon</c>, <c>ImagingDrawWideLine</c> and <c>line8</c> of
/// <c>src/libImaging/Draw.c</c> and <c>_draw_polygon</c> / <c>_draw_lines</c> of <c>src/_imaging.c</c>; HPND, see
/// THIRD-PARTY-NOTICES.md.
/// </summary>
public static class PilDraw
{
    struct Edge
    {
        public int X0, Y0, XMin, YMin, XMax, YMax;
        public float Dx;
    }

    /// <summary><c>ImageDraw.polygon(xy, fill=...)</c>.</summary>
    public static void Polygon(Grid<bool> img, IReadOnlyList<(double X, double Y)> xy)
    {
        int count = xy.Count;
        if (count <= 0) return;
        var ixy = new int[2 * count];
        for (int i = 0; i < count; i++) { ixy[2 * i] = (int)xy[i].X; ixy[2 * i + 1] = (int)xy[i].Y; }
        // ImagingDrawPolygon: the edge list, a horizontal edge right after another one in the same direction merged into it
        var e = new Edge[count];
        int n = 0, k;
        for (k = 0; k < count - 1; k++)
        {
            int x0 = ixy[k * 2], y0 = ixy[k * 2 + 1], x1 = ixy[k * 2 + 2], y1 = ixy[k * 2 + 3];
            if (y0 == y1 && k != 0 && y0 == ixy[k * 2 - 1])
            {
                ref var last = ref e[n - 1];
                if (x1 > x0 && x0 > ixy[k * 2 - 2]) { last.XMax = x1; continue; }
                if (x1 < x0 && x0 < ixy[k * 2 - 2]) { last.XMin = x1; continue; }
            }
            AddEdge(ref e[n++], x0, y0, x1, y1);
        }
        if (ixy[k * 2] != ixy[0] || ixy[k * 2 + 1] != ixy[1]) AddEdge(ref e[n++], ixy[k * 2], ixy[k * 2 + 1], ixy[0], ixy[1]);
        Fill(img, e, n);
    }

    /// <summary><c>ImageDraw.line(xy, fill=..., width=...)</c> (no joints).</summary>
    public static void Line(Grid<bool> img, IReadOnlyList<(double X, double Y)> xy, int width)
    {
        int n = xy.Count;
        if (width <= 1)
        {
            for (int i = 0; i < n - 1; i++) Bresenham(img, (int)xy[i].X, (int)xy[i].Y, (int)xy[i + 1].X, (int)xy[i + 1].Y);
            if (n >= 2) Point(img, (int)xy[n - 1].X, (int)xy[n - 1].Y);
            return;
        }
        for (int i = 0; i < n - 1; i++) WideLine(img, (int)xy[i].X, (int)xy[i].Y, (int)xy[i + 1].X, (int)xy[i + 1].Y, width);
    }

    static void AddEdge(ref Edge e, int x0, int y0, int x1, int y1)
    {
        if (x0 <= x1) { e.XMin = x0; e.XMax = x1; } else { e.XMin = x1; e.XMax = x0; }
        if (y0 <= y1) { e.YMin = y0; e.YMax = y1; } else { e.YMin = y1; e.YMax = y0; }
        e.Dx = y0 == y1 ? 0f : (float)(x1 - x0) / (y1 - y0);
        e.X0 = x0;
        e.Y0 = y0;
    }

    // polygon_generic of Draw.c with hline8 and no alpha
    static void Fill(Grid<bool> img, Edge[] e, int n)
    {
        if (n <= 0) return;
        int ysize = img.Height;
        int ymin = ysize - 1, ymax = 0;
        var table = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (ymin > e[i].YMin) ymin = e[i].YMin;
            if (ymax < e[i].YMax) ymax = e[i].YMax;
            if (e[i].YMin == e[i].YMax) { HLine(img, e[i].XMin, e[i].YMin, e[i].XMax); continue; }
            table.Add(i);
        }
        if (ymin < 0) ymin = 0;
        if (ymax > ysize) ymax = ysize;
        int edges = table.Count;
        var xx = new float[edges * 2];
        for (; ymin <= ymax; ymin++)
        {
            int j = 0;
            for (int i = 0; i < edges; i++)
            {
                ref var cur = ref e[table[i]];
                if (ymin < cur.YMin || ymin > cur.YMax) continue;
                xx[j++] = At(ref cur, ymin);
                if (ymin == cur.YMax && ymin < ymax)
                {
                    xx[j] = xx[j - 1];                           // needed to draw consistent polygons
                    j++;
                }
                else if (cur.Dx != 0 && MathF.Round(xx[j - 1], MidpointRounding.AwayFromZero) == xx[j - 1])
                {
                    // connect discontiguous corners (Pillow writes into xx at the other edge's index)
                    for (int k = 0; k < i; k++)
                    {
                        ref var other = ref e[table[k]];
                        if ((cur.Dx > 0 && other.Dx <= 0) || (cur.Dx < 0 && other.Dx >= 0)) continue;
                        if (((ymin == cur.YMin && ymin == other.YMin) || (ymin == cur.YMax && ymin == other.YMax)) && xx[j - 1] == At(ref other, ymin))
                        {
                            int offset = ymin == ymax ? -1 : 1;
                            float a = At(ref cur, ymin + offset), b = At(ref other, ymin + offset);
                            if (ymin == cur.YMax)
                                xx[k] = cur.Dx > 0 ? (float)(Math.Max((double)a, b) + 1) : (float)(Math.Min((double)a, b) - 1);
                            else
                                xx[k] = cur.Dx > 0 ? (float)Math.Min((double)a, b) : (float)(Math.Max((double)a, b) + 1);
                            break;
                        }
                    }
                }
            }
            Array.Sort(xx, 0, j);
            for (int i = 1; i < j; i += 2) HLine(img, RoundUp(xx[i - 1]), ymin, RoundDown(xx[i]));
        }
    }

    /// <summary>The edge's x on scan line y: <c>(y - y0) * dx + x0</c> in float32.</summary>
    static float At(ref Edge e, int y) => (float)(y - e.Y0) * e.Dx + (float)e.X0;

    // ROUND_UP / ROUND_DOWN of Draw.c for a float argument: f + 0.5F in float on the positive side, in double on the negative
    static int RoundUp(float f) => (int)(f >= 0.0 ? Math.Floor((double)(f + 0.5f)) : -Math.Floor(Math.Abs((double)f) + 0.5));
    static int RoundDown(float f) => (int)(f >= 0.0 ? Math.Ceiling((double)(f - 0.5f)) : -Math.Ceiling(Math.Abs((double)f) - 0.5));
    static int RoundUp(double f) => (int)(f >= 0.0 ? Math.Floor(f + 0.5) : -Math.Floor(Math.Abs(f) + 0.5));
    static int RoundDown(double f) => (int)(f >= 0.0 ? Math.Ceiling(f - 0.5) : -Math.Ceiling(Math.Abs(f) - 0.5));

    static void HLine(Grid<bool> img, int x0, int y0, int x1)
    {
        if (y0 < 0 || y0 >= img.Height) return;
        if (x0 < 0) x0 = 0;
        else if (x0 >= img.Width) return;
        if (x1 < 0) return;
        if (x1 >= img.Width) x1 = img.Width - 1;
        for (int x = x0; x <= x1; x++) img.Data[y0 * img.Width + x] = true;
    }

    static void Point(Grid<bool> img, int x, int y)
    {
        if (x >= 0 && x < img.Width && y >= 0 && y < img.Height) img.Data[y * img.Width + x] = true;
    }

    // line8: the end point itself is not drawn
    static void Bresenham(Grid<bool> img, int x0, int y0, int x1, int y1)
    {
        int dx = x1 - x0, dy = y1 - y0, xs = 1, ys = 1;
        if (dx < 0) { dx = -dx; xs = -1; }
        if (dy < 0) { dy = -dy; ys = -1; }
        if (dx == 0)
        {
            for (int i = 0; i < dy; i++) { Point(img, x0, y0); y0 += ys; }
        }
        else if (dy == 0)
        {
            for (int i = 0; i < dx; i++) { Point(img, x0, y0); x0 += xs; }
        }
        else if (dx > dy)
        {
            int n = dx;
            dy += dy;
            int e = dy - dx;
            dx += dx;
            for (int i = 0; i < n; i++)
            {
                Point(img, x0, y0);
                if (e >= 0) { y0 += ys; e -= dx; }
                e += dy;
                x0 += xs;
            }
        }
        else
        {
            int n = dy;
            dx += dx;
            int e = dx - dy;
            dy += dy;
            for (int i = 0; i < n; i++)
            {
                Point(img, x0, y0);
                if (e >= 0) { x0 += xs; e -= dy; }
                e += dx;
                y0 += ys;
            }
        }
    }

    // ImagingDrawWideLine
    static void WideLine(Grid<bool> img, int x0, int y0, int x1, int y1, int width)
    {
        int dx = x1 - x0, dy = y1 - y0;
        if (dx == 0 && dy == 0) { Point(img, x0, y0); return; }
        double big = Math.Sqrt((double)dx * dx + (double)dy * dy);
        double small = (width - 1) / 2.0;
        double ratioMax = RoundUp(small) / big, ratioMin = RoundDown(small) / big;
        int dxmin = RoundDown(ratioMin * dy), dxmax = RoundDown(ratioMax * dy);
        int dymin = RoundDown(ratioMin * dx), dymax = RoundDown(ratioMax * dx);
        int[] v = [x0 - dxmin, y0 + dymax, x1 - dxmin, y1 + dymax, x1 + dxmax, y1 - dymin, x0 + dxmax, y0 - dymin];
        var e = new Edge[4];
        for (int i = 0; i < 4; i++) AddEdge(ref e[i], v[2 * i], v[2 * i + 1], v[(2 * i + 2) % 8], v[(2 * i + 3) % 8]);
        Fill(img, e, 4);
    }
}
