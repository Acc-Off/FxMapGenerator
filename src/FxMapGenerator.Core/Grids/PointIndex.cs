namespace FxMapGenerator.Core.Grids;

/// <summary>
/// Points in the plane bucketed on a square lattice, for "every point within r of here" queries. Results come sorted
/// by the points' index; a point exactly r away counts.
/// </summary>
public sealed class PointIndex
{
    readonly double[] _x, _y;
    readonly double _cell;
    readonly Dictionary<(int, int), List<int>> _buckets = new();

    /// <param name="cell">Bucket size: about the usual query radius.</param>
    public PointIndex(IReadOnlyList<(double X, double Y)> points, double cell)
    {
        if (cell <= 0) throw new ArgumentOutOfRangeException(nameof(cell));
        _cell = cell;
        _x = new double[points.Count];
        _y = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            _x[i] = points[i].X;
            _y[i] = points[i].Y;
            var key = Key(_x[i], _y[i]);
            if (!_buckets.TryGetValue(key, out var list)) _buckets[key] = list = new List<int>();
            list.Add(i);
        }
    }

    public int Count => _x.Length;

    (int, int) Key(double x, double y) => ((int)Math.Floor(x / _cell), (int)Math.Floor(y / _cell));

    /// <summary>The indices of the points within <paramref name="r"/> of (<paramref name="x"/>, <paramref name="y"/>), ascending.</summary>
    public List<int> Within(double x, double y, double r)
    {
        var found = new List<int>();
        var (bx0, by0) = Key(x - r, y - r);
        var (bx1, by1) = Key(x + r, y + r);
        double r2 = r * r;
        for (int bx = bx0; bx <= bx1; bx++)
            for (int by = by0; by <= by1; by++)
            {
                if (!_buckets.TryGetValue((bx, by), out var list)) continue;
                foreach (var i in list)
                {
                    double dx = _x[i] - x, dy = _y[i] - y;
                    if (dx * dx + dy * dy <= r2) found.Add(i);
                }
            }
        found.Sort();
        return found;
    }
}
