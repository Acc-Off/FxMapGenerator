namespace FxMapGenerator.DevTools.Island;

/// <summary>
/// The sketch's shapes as fields over the cell (local metres): how far inland a point is (the coast wandering with
/// noise on several scales, so it is no drawn line), how deep into the mountains, whether in the town or on the
/// headland, how far from a river; and from them the uplift the landscape evolution works against.
/// </summary>
sealed class Layout
{
    readonly int _seed;
    readonly (double X, double Y)[] _sea = Sketch.Local(Sketch.Sea);
    readonly (double X, double Y)[] _mountains = Sketch.Local(Sketch.Mountains);
    readonly (double X, double Y)[] _town = Sketch.Local(Sketch.Town);
    readonly (double X, double Y) _summit = Sketch.Local(Sketch.Summit);

    /// <summary>The rivers' meandering middle lines (every 4 m, from the source to the mouth): the main river and the upper one.</summary>
    public IReadOnlyList<(double X, double Y)[]> Rivers { get; }

    public Layout(int seed)
    {
        _seed = seed;
        Rivers = [Meander(Sketch.Local(Sketch.River), 320, 60, seed + 50), Meander(Sketch.Local(Sketch.UpperRiver), 220, 50, seed + 60)];
    }

    /// <summary>
    /// A meandering river along a guide line: a sine-generated curve (the direction swings by up to
    /// <paramref name="swingDeg"/> around the way to a point 150 m ahead on the guide, one full swing per
    /// <paramref name="wavelength"/> m of river, both varied by noise; Langbein and Leopold 1966), the swing smaller
    /// in the mountains, where a river runs straighter.
    /// </summary>
    (double X, double Y)[] Meander((double X, double Y)[] guide, double wavelength, double swingDeg, int seed)
    {
        var lengths = new double[guide.Length];
        for (int i = 1; i < guide.Length; i++) lengths[i] = lengths[i - 1] + Math.Sqrt(Sq(guide[i].X - guide[i - 1].X) + Sq(guide[i].Y - guide[i - 1].Y));
        (double X, double Y) At(double s)
        {
            s = Math.Clamp(s, 0, lengths[^1]);
            int k = 1;
            while (k < guide.Length - 1 && lengths[k] < s) k++;
            double f = (s - lengths[k - 1]) / Math.Max(1e-9, lengths[k] - lengths[k - 1]);
            return (guide[k - 1].X + f * (guide[k].X - guide[k - 1].X), guide[k - 1].Y + f * (guide[k].Y - guide[k - 1].Y));
        }
        (double X, double Y) Nearest((double X, double Y) p, out double sOn)
        {
            double best = double.MaxValue;
            sOn = 0;
            for (int i = 1; i < guide.Length; i++)
            {
                double dx = guide[i].X - guide[i - 1].X, dy = guide[i].Y - guide[i - 1].Y, l2 = dx * dx + dy * dy;
                double t = Math.Clamp(((p.X - guide[i - 1].X) * dx + (p.Y - guide[i - 1].Y) * dy) / l2, 0, 1);
                double d = Sq(guide[i - 1].X + t * dx - p.X) + Sq(guide[i - 1].Y + t * dy - p.Y);
                if (d < best) { best = d; sOn = lengths[i - 1] + t * (lengths[i] - lengths[i - 1]); }
            }
            return At(sOn);
        }
        var o = new List<(double X, double Y)> { guide[0] };
        var p = guide[0];
        double s = 0, phase = 0;
        const double ds = 4;
        for (int k = 0; k < 5000; k++)
        {
            Nearest(p, out double on);
            if (lengths[^1] - on < 10) break;
            var ahead = At(on + 150);
            double baseDir = Math.Atan2(ahead.Y - p.Y, ahead.X - p.X);
            double wl = wavelength * (1 + 0.3 * Noise.Fbm(s, 0.5, 900, 2, seed));
            phase += 2 * Math.PI * ds / wl;
            double swing = swingDeg * Math.PI / 180 * (0.8 + 0.4 * Noise.Fbm(s, 7.5, 700, 2, seed + 1)) * (MountainDepth(p.X, p.Y) > 0 ? 0.3 : 1);
            double dir = baseDir + swing * Math.Sin(phase);
            p = (p.X + ds * Math.Cos(dir), p.Y + ds * Math.Sin(dir));
            s += ds;
            o.Add(p);
        }
        o.Add(guide[^1]);
        return o.ToArray();
    }

    /// <summary>How far inland (m; below 0 at sea): the distance to the sketch's coast, moved by noise of 350 m and 90 m.</summary>
    public double Coast(double u, double v) =>
        -Signed(_sea, u, v) + 55 * Noise.Fbm(u, v, 350, 3, _seed + 40) + 18 * Noise.Fbm(u, v, 90, 3, _seed + 41);

    /// <summary>How deep into the mountains (m; below 0 outside the sketch's line).</summary>
    public double MountainDepth(double u, double v) => Signed(_mountains, u, v) + 60 * Noise.Fbm(u, v, 400, 2, _seed + 42);

    public bool InTown(double u, double v) => Signed(_town, u, v) + 30 * Noise.Fbm(u, v, 250, 2, _seed + 43) > 0;

    /// <summary>On the headland north of the bay (the sketch's top-left).</summary>
    public bool OnHeadland(double u, double v) => HeadlandWeight(u, v) > 0.5;

    double HeadlandWeight(double u, double v) => Noise.Smooth((v - 470) / 120) * Noise.Smooth((-280 - u) / 150);

    /// <summary>The distance (m) to the nearer of the sketch's two rivers.</summary>
    public double RiverDistance(double u, double v) => Rivers.Min(r => ToLine(r, u, v));

    /// <summary>
    /// The uplift (no unit; the heights are scaled afterwards): a little everywhere inland, rising into the mountains
    /// the deeper past their line and the nearer the summit, some on the headland; almost none in the town and along
    /// the rivers (a flat town, valleys the rivers keep).
    /// </summary>
    public double Uplift(double u, double v, double coast, double river)
    {
        double summit = Math.Exp(-(Sq(u - _summit.X) + Sq(v - _summit.Y)) / (2 * 800 * 800));
        double mountain = Noise.Smooth((MountainDepth(u, v) + 60) / 300) * (0.45 + 0.55 * summit);
        double hills = 0.1 * Noise.Smooth(coast / 300);
        double headland = 0.45 * HeadlandWeight(u, v);
        double up = 0.02 + hills + mountain + headland;
        double town = Signed(_town, u, v) + 30 * Noise.Fbm(u, v, 250, 2, _seed + 43);
        up *= 1 - 0.92 * Noise.Smooth((town + 60) / 160);
        up *= 0.15 + 0.85 * Noise.Smooth((river - 25) / 160);
        return up * (0.9 + 0.2 * Noise.Fbm(u, v, 400, 3, _seed + 5));
    }

    static double Sq(double x) => x * x;

    /// <summary>The distance to a polygon's outline, positive inside.</summary>
    static double Signed((double X, double Y)[] poly, double u, double v)
    {
        double d = double.MaxValue;
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            d = Math.Min(d, ToSegment(u, v, poly[j], poly[i]));
            if ((poly[i].Y > v) != (poly[j].Y > v) && u < (poly[j].X - poly[i].X) * (v - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        }
        return inside ? d : -d;
    }

    static double ToLine((double X, double Y)[] line, double u, double v)
    {
        double d = double.MaxValue;
        // a quick look every 10 points first, then the points near the nearest of those
        int best = 0;
        for (int i = 0; i < line.Length; i += 10)
        {
            double e = Sq(line[i].X - u) + Sq(line[i].Y - v);
            if (e < d) { d = e; best = i; }
        }
        d = double.MaxValue;
        double reach = Math.Sqrt(Sq(line[best].X - u) + Sq(line[best].Y - v)) + 50;
        for (int i = 1; i < line.Length; i++)
        {
            if (Math.Abs(line[i].X - u) > reach || Math.Abs(line[i].Y - v) > reach) continue;
            d = Math.Min(d, ToSegment(u, v, line[i - 1], line[i]));
        }
        return d;
    }

    static double ToSegment(double u, double v, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Clamp(((u - a.X) * dx + (v - a.Y) * dy) / l2, 0, 1);
        return Math.Sqrt(Sq(a.X + t * dx - u) + Sq(a.Y + t * dy - v));
    }
}
