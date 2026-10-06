using FxMapGenerator.Core.Geometry;

namespace FxMapGenerator.Core.Labels;

/// <summary>
/// A point of a label line. <see cref="Smoothed"/>: the coordinates came out of the smoothing (or were computed from
/// such a point); they round to 0.01 m the way numpy rounds (on the scaled value), the others as Python does (on the
/// exact value).
/// </summary>
public readonly record struct LinePoint(double X, double Y, bool Smoothed = false);

/// <summary>A position along a line: the point, the direction there (degrees, east = 0, counter-clockwise).</summary>
public readonly record struct LineStation(double X, double Y, double Heading, bool Smoothed);

/// <summary>Polyline helpers of the label placement (arc lengths, stations, smoothing, glyphs along a line).</summary>
public static class LabelLines
{
    /// <summary>Arc length of every vertex (0 at the first).</summary>
    public static double[] Cum(IReadOnlyList<LinePoint> pts)
    {
        var s = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++) s[i] = s[i - 1] + Num.Hypot(pts[i].X - pts[i - 1].X, pts[i].Y - pts[i - 1].Y);
        return s;
    }

    public static double Length(IReadOnlyList<LinePoint> pts) => pts.Count == 0 ? 0 : Cum(pts)[^1];

    /// <summary>
    /// The point and direction at arc length <paramref name="s"/> (kept inside the line). <paramref name="sSmoothed"/>:
    /// the arc length itself carries the numpy kind (a cut position), which the point takes over unless it was kept inside.
    /// </summary>
    public static LineStation At(IReadOnlyList<LinePoint> pts, double[] cum, double s, bool sSmoothed = false)
    {
        double s0 = s;
        s = Num.PyMin(Num.PyMax(s, 0.0), cum[^1]);
        if (0.0 > s0 || cum[^1] < s0) sSmoothed = false;
        for (int i = 1; i < pts.Count; i++)
            if (cum[i] >= s)
            {
                var (a, b) = (pts[i - 1], pts[i]);
                double t = (s - cum[i - 1]) / Num.PyMax(cum[i] - cum[i - 1], 1e-9);
                return new LineStation(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), Num.Degrees(Math.Atan2(b.Y - a.Y, b.X - a.X)), a.Smoothed || b.Smoothed || sSmoothed);
            }
        var (p, q) = (pts[^2], pts[^1]);
        return new LineStation(q.X, q.Y, Num.Degrees(Math.Atan2(q.Y - p.Y, q.X - p.X)), q.Smoothed);
    }

    public static double Heading(LinePoint a, LinePoint b) => Num.Degrees(Math.Atan2(b.Y - a.Y, b.X - a.X));

    /// <summary>The turn between two directions, 0..180 degrees.</summary>
    public static double Turn(double a, double b) => Math.Abs(Num.PyMod(b - a + 180.0, 360.0) - 180.0);

    /// <summary>The line without points closer than 5 cm to the one kept before.</summary>
    public static List<LinePoint> Dedupe(IReadOnlyList<LinePoint> pts)
    {
        var o = new List<LinePoint> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
            if (Num.Hypot(pts[i].X - o[^1].X, pts[i].Y - o[^1].Y) > 0.05) o.Add(pts[i]);
        return o;
    }

    /// <summary>The distance from a point to a line, the nearest point on it and the direction of that segment (the first nearest).</summary>
    public static (double Dist, double Qx, double Qy, double Heading) Nearest(double px, double py, IReadOnlyList<LinePoint> line)
    {
        double best = double.NaN, bx = 0, by = 0, bh = 0;
        for (int k = 0; k + 1 < line.Count; k++)
        {
            double ax = line[k].X, ay = line[k].Y;
            double dx = line[k + 1].X - ax, dy = line[k + 1].Y - ay;
            double l2 = dx * dx + dy * dy;
            if (l2 == 0) l2 = 1e-9;
            double t = ((px - ax) * dx + (py - ay) * dy) / l2;
            t = t < 0 ? 0 : t > 1 ? 1 : t;                        // numpy clip
            double qx = ax + dx * t, qy = ay + dy * t;
            double d = Num.NpHypot(qx - px, qy - py);
            if (k == 0 || d < best) (best, bx, by, bh) = (d, qx, qy, Num.Degrees(Math.Atan2(dy, dx)));   // numpy argmin: the first smallest
        }
        return (best, bx, by, bh);
    }

    /// <summary>The arc length of the point (qx, qy) on the line (0 when it is on none of its segments).</summary>
    public static (double S, bool Smoothed) ArcOf(IReadOnlyList<LinePoint> pts, double[] cum, double qx, double qy)
    {
        for (int i = 1; i < pts.Count; i++)
        {
            var (a, b) = (pts[i - 1], pts[i]);
            double seg = cum[i] - cum[i - 1];
            if (seg <= 1e-9) continue;
            double t = ((qx - a.X) * (b.X - a.X) + (qy - a.Y) * (b.Y - a.Y)) / Num.Pow2(seg);
            if (-1e-6 <= t && t <= 1 + 1e-6 && Num.Hypot(a.X + t * (b.X - a.X) - qx, a.Y + t * (b.Y - a.Y) - qy) <= 0.5)
            {
                double c = Num.PyMax(0.0, Num.PyMin(1.0, t));
                // a clamped t is the literal 0 or 1; otherwise it carries the points' kind
                return (cum[i - 1] + c * seg, (a.Smoothed || b.Smoothed) && t > 0.0 && t < 1.0);
            }
        }
        return (0.0, false);
    }

    /// <summary>The part of the line between arc lengths <paramref name="s0"/> and <paramref name="s1"/> (their numpy kinds as in <see cref="At"/>).</summary>
    public static List<LinePoint> Slice(IReadOnlyList<LinePoint> pts, double[] cum, double s0, double s1, bool s0Smoothed = false, bool s1Smoothed = false)
    {
        var a = At(pts, cum, s0, s0Smoothed);
        var o = new List<LinePoint> { new(a.X, a.Y, a.Smoothed) };
        for (int i = 0; i < pts.Count; i++)
            if (s0 < cum[i] && cum[i] < s1) o.Add(pts[i]);
        var b = At(pts, cum, s1, s1Smoothed);
        o.Add(new LinePoint(b.X, b.Y, b.Smoothed));
        return Dedupe(o);
    }

    /// <summary>
    /// The line resampled every <paramref name="step"/> m and smoothed with a Gaussian of <paramref name="sigma"/> m
    /// along the arc length (the ends stay). Lines of fewer than 3 points come back as they are.
    /// </summary>
    public static List<LinePoint> Smooth(IReadOnlyList<LinePoint> input, double step = 5.0, double sigma = 15.0)
    {
        var pts = Dedupe(input);
        if (pts.Count < 3) return pts;
        var cum = Cum(pts);
        double L = cum[^1];
        var ar = Num.NpArange(0.0, L, step);
        var ss = new double[ar.Length + 1];
        ar.CopyTo(ss, 0);
        ss[^1] = L;
        int n = ss.Length;
        var px = new double[n];
        var py = new double[n];
        for (int i = 0; i < n; i++)
        {
            var st = At(pts, cum, ss[i]);
            (px[i], py[i]) = (st.X, st.Y);
        }
        var o = new List<LinePoint>(n);
        var w = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = ss[i];
            for (int j = 0; j < n; j++)
            {
                double z = (ss[j] - t) / sigma;
                w[j] = Math.Exp(-0.5 * (z * z));
            }
            double sx = 0.0, sy = 0.0;
            for (int j = 0; j < n; j++)
            {
                sx += px[j] * w[j];
                sy += py[j] * w[j];
            }
            double sw = Num.NpSum(w);
            o.Add(new LinePoint(sx / sw, sy / sw, true));
        }
        o[0] = pts[0];
        o[^1] = pts[^1];
        return Dedupe(o);
    }

    /// <summary>The line cut at corners sharper than <paramref name="maxTurn"/> degrees.</summary>
    public static List<List<LinePoint>> SplitCorners(IReadOnlyList<LinePoint> pts, double maxTurn = 50.0)
    {
        var parts = new List<List<LinePoint>>();
        var cur = new List<LinePoint> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            cur.Add(pts[i]);
            if (i + 1 < pts.Count && Turn(Heading(pts[i - 1], pts[i]), Heading(pts[i], pts[i + 1])) > maxTurn)
            {
                parts.Add(cur);
                cur = [pts[i]];
            }
        }
        if (cur.Count >= 2) parts.Add(cur);
        return parts;
    }

    /// <summary>A glyph laid along a line: its centre, rotation (degrees) and advance, all rounded to 0.01.</summary>
    public readonly record struct Glyph(string Char, double X, double Y, double Rotation, double Advance);

    /// <summary>
    /// The glyphs of <paramref name="text"/> along the line, centred at arc length <paramref name="centre"/>, reading to
    /// the right (the line is taken backwards when its chord points left); null when the text does not fit or the line
    /// bends more than <paramref name="maxGlyphTurn"/> degrees between two glyphs.
    /// </summary>
    public static List<Glyph>? GlyphsAlong(IReadOnlyList<LinePoint> pts, double[] cum, double centre, IReadOnlyList<string> chars,
        IReadOnlyList<double> advances, double maxGlyphTurn)
    {
        double w = 0;
        foreach (var a in advances) w += a;
        double L = cum[^1];
        if (centre - w / 2 < 0 || centre + w / 2 > L) return null;
        var sa = At(pts, cum, centre - w / 2);
        var sb = At(pts, cum, centre + w / 2);
        double chord = Num.Degrees(Math.Atan2(sb.Y - sa.Y, sb.X - sa.X));
        if (!(-90.0 < chord && chord <= 90.0))
        {
            var rp = new List<LinePoint>(pts.Count);
            for (int i = pts.Count - 1; i >= 0; i--) rp.Add(pts[i]);
            var rc = new double[cum.Length];
            for (int i = 0; i < cum.Length; i++) rc[i] = L - cum[cum.Length - 1 - i];
            (pts, cum, centre) = (rp, rc, L - centre);
        }
        var o = new List<Glyph>(chars.Count);
        double s = centre - w / 2;
        double? prev = null;
        for (int i = 0; i < chars.Count; i++)
        {
            double adv = advances[i];
            var st = At(pts, cum, s + adv / 2);
            if (prev is { } p && Turn(p, st.Heading) > maxGlyphTurn) return null;
            prev = st.Heading;
            o.Add(new Glyph(chars[i], st.Smoothed ? Num.NpRound(st.X, 2) : Num.Round(st.X, 2), st.Smoothed ? Num.NpRound(st.Y, 2) : Num.Round(st.Y, 2),
                Num.Round(st.Heading, 2), Num.Round(adv, 2)));
            s += adv;
        }
        return o;
    }
}
