using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Scan;

/// <summary>
/// The scanned blocks as one regular block grid, for looking up the per-block scan grids by world position: tarmac and
/// concrete (from the material classes), on-road, street. The grid spans the scanned blocks' bounding rectangle; blocks
/// inside it without a scan read as outside.
/// </summary>
public sealed class ScanArea
{
    public int Tx0 { get; }
    public int Ty0 { get; }
    /// <summary>Blocks across / down.</summary>
    public int Nbx { get; }
    public int Nby { get; }
    /// <summary>North-west corner of the rectangle (m).</summary>
    public double X0 { get; }
    public double Y0 { get; }
    public double W => Nbx * WorldGrid.BlockSize;
    public double H => Nby * WorldGrid.BlockSize;
    /// <summary>
    /// The corner moved by at most 0.5 m onto the world-wide 1 m lattice of fractional part 0.5: 1 m grids sampled from
    /// the area use (Gx0 + c, Gy0 - r), so grids of different areas line up.
    /// </summary>
    public double Gx0 => Lattice(X0);
    public double Gy0 => Lattice(Y0);

    public int N { get; }
    public double Step { get; }
    public int OnRoadN { get; }
    public double OnRoadStep { get; }
    public int RoadN { get; }
    public double RoadStep { get; }

    readonly bool[] _present;
    readonly bool[][] _tarmac, _concrete, _onRoad;
    readonly uint[][] _street;
    public Dictionary<uint, string> StreetNames { get; } = new();

    public ScanArea(IReadOnlyCollection<ScanFile> scans, Materials? materials = null)
    {
        if (scans.Count == 0) throw new ArgumentException("no scans");
        var m = materials ?? Materials.Default;
        int tarmac = m.IndexOf("tarmac"), concrete = m.IndexOf("concrete");
        Tx0 = scans.Min(s => s.Tx);
        Ty0 = scans.Min(s => s.Ty);
        Nbx = (scans.Max(s => s.Tx) - Tx0) / 4 + 1;
        Nby = (scans.Max(s => s.Ty) - Ty0) / 4 + 1;
        X0 = WorldGrid.Left + Tx0 * WorldGrid.TileSize;
        Y0 = WorldGrid.Top - Ty0 * WorldGrid.TileSize;
        var first = scans.First();
        (N, Step, OnRoadN, OnRoadStep, RoadN, RoadStep) = (first.N, first.Step, first.OnRoadN, first.OnRoadStep, first.RoadN, first.RoadStep);
        int blocks = Nbx * Nby;
        _present = new bool[blocks];
        _tarmac = new bool[blocks][];
        _concrete = new bool[blocks][];
        _onRoad = new bool[blocks][];
        _street = new uint[blocks][];
        foreach (var s in scans)
        {
            int b = (s.Ty - Ty0) / 4 * Nbx + (s.Tx - Tx0) / 4;
            var cls = m.ClassGrid(s.Material);
            _tarmac[b] = cls.Select(c => c == tarmac).ToArray();
            _concrete[b] = cls.Select(c => c == concrete).ToArray();
            _onRoad[b] = Cut(s.OnRoad, s.OnRoadN, OnRoadN).Select(v => v > 0).ToArray();
            _street[b] = Cut(s.Street, s.RoadN, RoadN);
            foreach (var (h, name) in s.StreetNames) StreetNames[h] = name;
            _present[b] = true;
        }
    }

    /// <summary>A grid of side <paramref name="from"/> as one of side <paramref name="to"/>: cut, or padded with zeros.</summary>
    static T[] Cut<T>(T[] a, int from, int to)
    {
        if (from == to) return a;
        var o = new T[to * to];
        for (int r = 0; r < Math.Min(from, to); r++) Array.Copy(a, r * from, o, r * to, Math.Min(from, to));
        return o;
    }

    /// <summary>v moved by at most 0.5 onto the lattice of values whose fractional part is <paramref name="frac"/>.</summary>
    public static double Lattice(double v, double frac = 0.5) => v + PyMod(frac - PyMod(v, 1.0) + 0.5, 1.0) - 0.5;

    static double PyMod(double a, double b)
    {
        double r = a % b;
        return r != 0 && (r < 0) != (b < 0) ? r + b : r;
    }

    /// <summary>
    /// The block and the cell of a grid of <paramref name="n"/> x <paramref name="n"/> samples every <paramref name="step"/>
    /// m under a world position: the nearest sample, a tie rounded up. Ok = inside a scanned block.
    /// </summary>
    public (int Block, int Row, int Col, bool Ok) Index(double x, double y, double step, int n)
    {
        int ix = (int)Math.Floor((x - X0) / WorldGrid.BlockSize);
        int iy = (int)Math.Floor((Y0 - y) / WorldGrid.BlockSize);
        bool ok = ix >= 0 && ix < Nbx && iy >= 0 && iy < Nby;
        int ixc = Math.Clamp(ix, 0, Nbx - 1), iyc = Math.Clamp(iy, 0, Nby - 1);
        int b = iyc * Nbx + ixc;
        ok &= _present[b];
        int c = Math.Clamp((int)Math.Floor((x - (X0 + ixc * WorldGrid.BlockSize)) / step + 0.5), 0, n - 1);
        int r = Math.Clamp((int)Math.Floor((Y0 - iyc * WorldGrid.BlockSize - y) / step + 0.5), 0, n - 1);
        return (b, r, c, ok);
    }

    public bool Inside(double x, double y) => Index(x, y, Step, N).Ok;

    public bool Contains(double x, double y, double margin = 30.0) =>
        X0 - margin <= x && x <= X0 + W + margin && Y0 - H - margin <= y && y <= Y0 + margin;

    public bool Tarmac(double x, double y) => Get(_tarmac, x, y, Step, N);
    public bool Concrete(double x, double y) => Get(_concrete, x, y, Step, N);
    public bool OnRoad(double x, double y) => Get(_onRoad, x, y, OnRoadStep, OnRoadN);

    public uint Street(double x, double y)
    {
        var (b, r, c, ok) = Index(x, y, RoadStep, RoadN);
        return ok ? _street[b][r * RoadN + c] : 0;
    }

    bool Get(bool[][] grids, double x, double y, double step, int n)
    {
        var (b, r, c, ok) = Index(x, y, step, n);
        return ok && grids[b][r * n + c];
    }
}
