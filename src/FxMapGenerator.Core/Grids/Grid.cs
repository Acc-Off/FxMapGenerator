namespace FxMapGenerator.Core.Grids;

/// <summary>A 2-D array in row-major order: row 0 first (north on the map grids), <see cref="Width"/> cells a row.</summary>
public sealed class Grid<T>
{
    public int Width { get; }
    public int Height { get; }
    public T[] Data { get; }

    public Grid(int width, int height) : this(width, height, new T[checked(width * height)]) { }

    public Grid(int width, int height, T[] data)
    {
        if (width < 0 || height < 0 || data.Length != width * height) throw new ArgumentException($"grid {width} x {height} needs {width * height} values, got {data.Length}");
        Width = width;
        Height = height;
        Data = data;
    }

    public static Grid<T> Filled(int width, int height, T value)
    {
        var g = new Grid<T>(width, height);
        Array.Fill(g.Data, value);
        return g;
    }

    public int Count => Data.Length;

    public T this[int row, int col]
    {
        get => Data[row * Width + col];
        set => Data[row * Width + col] = value;
    }

    public bool Inside(int row, int col) => (uint)row < (uint)Height && (uint)col < (uint)Width;

    public Grid<T> Clone() => new(Width, Height, (T[])Data.Clone());

    public Grid<TOut> Map<TOut>(Func<T, TOut> f)
    {
        var o = new TOut[Data.Length];
        for (int i = 0; i < o.Length; i++) o[i] = f(Data[i]);
        return new Grid<TOut>(Width, Height, o);
    }

    /// <summary>The rows <paramref name="row0"/>.. and columns <paramref name="col0"/>.. of this grid, <paramref name="height"/> x <paramref name="width"/> (all inside).</summary>
    public Grid<T> Crop(int row0, int col0, int height, int width)
    {
        var o = new Grid<T>(width, height);
        for (int r = 0; r < height; r++) Array.Copy(Data, (row0 + r) * Width + col0, o.Data, r * width, width);
        return o;
    }

    /// <summary>Copies <paramref name="src"/> into this grid with its top-left cell at (<paramref name="row0"/>, <paramref name="col0"/>); what falls outside is dropped.</summary>
    public void Paste(Grid<T> src, int row0, int col0)
    {
        for (int r = 0; r < src.Height; r++)
        {
            int dr = row0 + r;
            if ((uint)dr >= (uint)Height) continue;
            int c0 = Math.Max(0, -col0), c1 = Math.Min(src.Width, Width - col0);
            if (c1 > c0) Array.Copy(src.Data, r * src.Width + c0, Data, dr * Width + col0 + c0, c1 - c0);
        }
    }
}
