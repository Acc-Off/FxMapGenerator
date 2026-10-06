namespace FxMapGenerator.Core.Vectors;

/// <summary>
/// Contour lines of a grid, the same lines in the same order as contourpy's "serial" generator (1.3.3, lines of type
/// Separate; no mask, one chunk, linear interpolation): the points lie where an edge between a grid point at or below
/// the level and one above it crosses the level (<c>x = x0 * f + x1 * (1 - f)</c>, <c>f = (z1 - level) / (z1 - z0)</c>),
/// saddles are split by the mean of their four corners, lines that reach the grid's edge start there, and closed lines
/// repeat their first point at the end. Grid point (column c, row r) is at x = c, y = r. Written after contourpy's
/// base_impl.h and serial.cpp (BSD-3-Clause, see THIRD-PARTY-NOTICES.md): the start flags of each quad, the first pass
/// that counts and clears the starts a line passes through, the second pass that writes the points.
/// </summary>
public static class Contours
{
    /// <summary>The lines of one level: each an array of x, y pairs. <paramref name="z"/> is row-major, nx points a row, no NaN.</summary>
    public static List<double[]> Lines(double[] z, int nx, int ny, double level)
    {
        if (nx < 2 || ny < 2) throw new ArgumentException("the grid needs at least 2 x 2 points");
        if (z.Length != nx * ny) throw new ArgumentException("z does not match the grid size");
        var g = new Generator(z, nx, ny, level);
        return g.March();
    }

    sealed class Generator
    {
        const uint ZLevel1 = 1 << 0, MaskZLevel = ZLevel1 | (1 << 1);
        const uint MiddleShift = 2, MaskMiddle = (1 << 2) | (1 << 3);
        const uint BoundaryE = 1 << 4, BoundaryN = 1 << 5, ExistsQuad = 1 << 6;
        const uint StartE = 1 << 11, StartN = 1 << 12, StartBoundaryE = 1 << 13, StartBoundaryN = 1 << 14,
            StartBoundaryS = 1 << 15, StartBoundaryW = 1 << 16;
        const uint AnyStart = StartE | StartN | StartBoundaryE | StartBoundaryN | StartBoundaryS | StartBoundaryW;
        const uint NoStartsInRow = 1 << 21, NoMoreStarts = 1 << 22;
        const uint KeepMask = ExistsQuad | BoundaryN | BoundaryE;

        readonly double[] _z;
        readonly int _nx, _ny;
        readonly double _level;
        readonly uint[] _cache;

        // the current chunk (the whole grid) and pass
        int _pass, _lineCount;
        List<double>? _points;
        List<int>? _offsets;

        public Generator(double[] z, int nx, int ny, double level)
        {
            _z = z;
            _nx = nx;
            _ny = ny;
            _level = level;
            _cache = new uint[nx * ny];
            for (int j = 0, quad = 0; j < ny; j++)
                for (int i = 0; i < nx; i++, quad++)
                {
                    uint c = 0;
                    if (i > 0 && j > 0) c |= ExistsQuad;
                    if ((i % (nx - 1) == 0 || i == nx - 1) && j > 0) c |= BoundaryE;
                    if ((j % (ny - 1) == 0 || j == ny - 1) && i > 0) c |= BoundaryN;
                    _cache[quad] = c;
                }
        }

        uint ZLevelOf(int point) => _cache[point] & MaskZLevel;
        uint MiddleZLevel(int quad) => (_cache[quad] & MaskMiddle) >> (int)MiddleShift;
        bool Has(int quad, uint flag) => (_cache[quad] & flag) != 0;
        bool BoundaryS(int quad) => (_cache[quad - _nx] & BoundaryN) != 0;
        bool BoundaryW(int quad) => (_cache[quad - 1] & BoundaryE) != 0;
        uint ToZLevel(double v) => v > _level ? 1u : 0u;

        void SetMiddle(int quad)
        {
            double m = 0.25 * (_z[quad - _nx - 1] + _z[quad - _nx] + _z[quad - 1] + _z[quad]);
            _cache[quad] |= ToZLevel(m) << (int)MiddleShift;
        }

        void InitLevelsAndStarts()
        {
            int jFinalStart = -1;
            for (int j = 0; j < _ny; j++)
            {
                int quad = j * _nx;
                bool startInRow = false;
                uint zNw = 0, zSw = 0;
                for (int i = 0; i < _nx; i++, quad++)
                {
                    uint zSe = j == 0 ? 0 : ZLevelOf(quad - _nx);
                    _cache[quad] &= KeepMask;
                    uint zNe = ToZLevel(_z[quad]);
                    _cache[quad] |= zNe;
                    if (Has(quad, ExistsQuad))
                    {
                        switch ((zNw << 3) | (zNe << 2) | (zSw << 1) | zSe)
                        {
                            case 1:
                            case 3:
                                if (Has(quad, BoundaryE)) { _cache[quad] |= StartBoundaryE; startInRow = true; }
                                break;
                            case 2:
                            case 10:
                            case 14:
                                if (BoundaryS(quad)) { _cache[quad] |= StartBoundaryS; startInRow = true; }
                                break;
                            case 4:
                                if (Has(quad, BoundaryN)) _cache[quad] |= StartBoundaryN;
                                else if (!Has(quad, BoundaryE)) _cache[quad] |= StartN;
                                startInRow |= Has(quad, AnyStart);
                                break;
                            case 5:
                            case 7:
                                if (Has(quad, BoundaryN)) { _cache[quad] |= StartBoundaryN; startInRow = true; }
                                break;
                            case 6:
                                SetMiddle(quad);
                                if (Has(quad, BoundaryN)) _cache[quad] |= StartBoundaryN;
                                else if (!Has(quad, BoundaryE) && MiddleZLevel(quad) == 0) _cache[quad] |= StartN;
                                if (BoundaryS(quad)) _cache[quad] |= StartBoundaryS;
                                startInRow |= Has(quad, AnyStart);
                                break;
                            case 8:
                            case 12:
                            case 13:
                                if (BoundaryW(quad)) { _cache[quad] |= StartBoundaryW; startInRow = true; }
                                break;
                            case 9:
                                SetMiddle(quad);
                                if (Has(quad, BoundaryE)) _cache[quad] |= StartBoundaryE;
                                else if (!Has(quad, BoundaryN) && MiddleZLevel(quad) == 1) _cache[quad] |= StartE;
                                if (BoundaryW(quad)) _cache[quad] |= StartBoundaryW;
                                startInRow |= Has(quad, AnyStart);
                                break;
                            case 11:
                                if (Has(quad, BoundaryE)) _cache[quad] |= StartBoundaryE;
                                else if (!Has(quad, BoundaryN)) _cache[quad] |= StartE;
                                startInRow |= Has(quad, AnyStart);
                                break;
                        }
                    }
                    zNw = zNe;
                    zSw = zSe;
                }
                if (startInRow) jFinalStart = j;
                else if (j > 0) _cache[1 + j * _nx] |= NoStartsInRow;
            }
            if (jFinalStart < _ny - 1) _cache[1 + (jFinalStart + 1) * _nx] |= NoMoreStarts;
        }

        public List<double[]> March()
        {
            InitLevelsAndStarts();
            int istart = 1, iend = _nx - 1, jstart = 1, jend = _ny - 1;
            var result = new List<double[]>();
            for (_pass = 0; _pass < 2; _pass++)
            {
                _lineCount = 0;
                long totalPoints = 0;
                if (_pass == 1) { _points = new List<double>(); _offsets = new List<int>(); }
                int jFinalStart = jstart;
                for (int j = jstart; j <= jend; j++)
                {
                    int quad = istart + j * _nx;
                    if (Has(quad, NoMoreStarts)) break;
                    if (Has(quad, NoStartsInRow)) continue;
                    int prevStartCount = _lineCount;
                    for (int i = istart; i <= iend; i++, quad++)
                    {
                        if (!Has(quad, AnyStart)) continue;
                        if (Has(quad, StartBoundaryS)) totalPoints += Line(quad, _nx, -1, true);
                        if (Has(quad, StartBoundaryW)) totalPoints += Line(quad, 1, _nx, true);
                        if (Has(quad, StartBoundaryE)) totalPoints += Line(quad, -1, -_nx, true);
                        if (Has(quad, StartBoundaryN)) totalPoints += Line(quad, -_nx, 1, true);
                        if (Has(quad, StartE)) totalPoints += Line(quad, -1, -_nx, false);
                        if (Has(quad, StartN)) totalPoints += Line(quad, -_nx, 1, false);
                    }
                    if (_lineCount > prevStartCount) jFinalStart = j;
                    else _cache[istart + j * _nx] |= NoStartsInRow;
                }
                if (jFinalStart < jend) _cache[istart + (jFinalStart + 1) * _nx] |= NoMoreStarts;
                if (_pass == 0 && totalPoints == 0) break;
            }
            if (_points is not null && _offsets is not null)
            {
                _offsets.Add(_points.Count / 2);
                for (int k = 0; k + 1 < _offsets.Count; k++)
                {
                    int a = _offsets[k], b = _offsets[k + 1];
                    var line = new double[(b - a) * 2];
                    _points.CopyTo(a * 2, line, 0, line.Length);
                    result.Add(line);
                }
            }
            return result;
        }

        /// <summary>One line from a start; the points it counts (pass 0) or wrote (pass 1).</summary>
        int Line(int quad, int forward, int left, bool onBoundary)
        {
            int pointCount = 0;
            if (_pass > 0) _offsets!.Add(_points!.Count / 2);
            bool finished = FollowInterior(quad, forward, left, onBoundary, ref pointCount);
            if (_pass == 0 && !onBoundary && !finished) pointCount--;
            else _lineCount++;
            return pointCount;
        }

        void Interp(int p0, int p1)
        {
            double z0 = _z[p0], z1 = _z[p1];
            double frac = (z1 - _level) / (z1 - z0);
            double x0 = p0 % _nx, y0 = p0 / _nx, x1 = p1 % _nx, y1 = p1 / _nx;
            _points!.Add(x0 * frac + x1 * (1.0 - frac));
            _points.Add(y0 * frac + y1 * (1.0 - frac));
        }

        bool FollowInterior(int startQuad, int startForward, int startLeft, bool startOnBoundary, ref int pointCount)
        {
            int quad = startQuad, forward = startForward, left = startLeft;
            int leftPoint;
            if (forward > 0) leftPoint = forward == _nx ? quad - _nx - 1 : quad - 1;   // forward nx, left -1 | forward 1, left nx
            else leftPoint = forward == -_nx ? quad : quad - _nx;                      // forward -nx, left 1 | forward -1, left -nx
            int rightPoint = leftPoint - left;
            bool finished = false;
            while (true)
            {
                if (_pass > 0) Interp(leftPoint, rightPoint);
                pointCount++;
                if (quad == startQuad && forward == startForward && left == startLeft && !startOnBoundary && pointCount > 1)
                {
                    finished = true;
                    break;
                }
                int oppLeft = leftPoint + forward, oppRight = rightPoint + forward;
                uint zOppLeft = ZLevelOf(oppLeft), zOppRight = ZLevelOf(oppRight);
                int direction = -1;                        // 1 left, 0 straight, -1 right
                if (zOppLeft == 0)
                {
                    if (zOppRight == 0 || MiddleZLevel(quad) == 0) direction = 1;
                }
                else if (zOppRight == 0) direction = 0;
                // clear the starts this line passes through (pass 0)
                if (_pass == 0 && !(quad == startQuad && forward == startForward && left == startLeft))
                {
                    if (Has(quad, StartE) && forward == -1 && left == -_nx && direction == -1 && ZLevelOf(quad) < 2)
                    {
                        _cache[quad] &= ~StartE;
                        if (quad < startQuad) break;
                    }
                    else if (Has(quad, StartN) && forward == -_nx && left == 1 && direction == 1 && ZLevelOf(quad - 1) < 2)
                    {
                        _cache[quad] &= ~StartN;
                        if (quad < startQuad) break;
                    }
                }
                switch (direction)
                {
                    case 1: { int t = forward; forward = left; left = -t; rightPoint = oppLeft; break; }
                    case -1: { int t = forward; forward = -left; left = t; leftPoint = oppRight; break; }
                    default: leftPoint = oppLeft; rightPoint = oppRight; break;
                }
                bool reachedBoundary = forward > 0
                    ? (forward == 1 ? Has(quad, BoundaryE) : Has(quad, BoundaryN))
                    : (forward == -1 ? BoundaryW(quad) : BoundaryS(quad));
                if (reachedBoundary)
                {
                    int t = forward;
                    forward = left;
                    left = -t;
                    pointCount++;
                    if (_pass > 0) Interp(leftPoint, rightPoint);
                    break;
                }
                quad += forward;
            }
            return finished;
        }
    }
}
