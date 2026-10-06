using System.Buffers.Binary;
using System.Text;

namespace FxMapGenerator.Core.Export;

/// <summary>The packed rows (<see cref="PackBits"/>) of a picture's planes: 0 red, 1 green, 2 blue, 3 alpha.</summary>
public interface IPsdRows
{
    /// <summary>The bytes each packed row of a plane takes, top to bottom.</summary>
    int[] Lengths(int plane);

    /// <summary>Writes the packed rows of a plane, top to bottom.</summary>
    void Write(int plane, Stream to);
}

/// <summary>
/// One layer of a <see cref="PsdFile"/>: its name (any characters) and a name in ASCII for readers that take the old
/// name field, how it is put over what lies under it (<see cref="PsdFile.Normal"/>, <see cref="PsdFile.Multiply"/>,
/// <see cref="PsdFile.ColorDodge"/>), where its picture lies in the document, and the picture's rows (four planes).
/// A clipped layer acts only where the first layer under it that is not clipped has something (a clipping mask).
/// </summary>
public sealed record PsdLayer(string Name, string AsciiName, string BlendMode, int Left, int Top, int Width, int Height, IPsdRows Rows, bool Clipped = false);

/// <summary>
/// A layered picture written as Adobe's PSD format, or as its large document format (PSB) when a side is over
/// <see cref="MaxSide"/> pixels or the file would pass 2 GB: 8 bits a channel, RGB, the layers bottom first, every
/// plane's rows packed with <see cref="PackBits"/>, and after the layers the merged picture (what readers that take no
/// layers show). Nothing of the picture is held in memory: the rows come from <see cref="IPsdRows"/>, first their
/// lengths (<see cref="Measure"/>: the format wants every length before the data), then their bytes.
/// </summary>
public sealed class PsdFile(int width, int height, IReadOnlyList<PsdLayer> layers, IPsdRows merged, bool mergedAlpha)
{
    public const string Normal = "norm", Multiply = "mul ", ColorDodge = "div ";
    /// <summary>The longest side of a PSD file; a larger picture is a PSB file.</summary>
    public const int MaxSide = 30000;
    /// <summary>The planes of a layer in the order the file holds them: alpha (channel -1), red, green, blue.</summary>
    static readonly int[] LayerPlanes = [3, 0, 1, 2];

    public int Width { get; } = width;
    public int Height { get; } = height;
    public IReadOnlyList<PsdLayer> Layers { get; } = layers;

    /// <param name="Big">The large document format (PSB): lengths of 8 bytes, row lengths of 4.</param>
    /// <param name="Length">The file's bytes.</param>
    public sealed record Layout(bool Big, long Length, int[][][] LayerRows, int[][] MergedRows);

    int MergedPlanes => mergedAlpha ? 4 : 3;

    /// <summary>Reads the length of every row and says which of the two formats the file takes and how long it is.</summary>
    public Layout Measure()
    {
        var layerRows = Layers.Select(l => LayerPlanes.Select(p => Checked(l.Rows.Lengths(p), l.Height, l.Name)).ToArray()).ToArray();
        var mergedRows = Enumerable.Range(0, MergedPlanes).Select(p => Checked(merged.Lengths(p), Height, "the merged picture")).ToArray();
        long small = Length(big: false, layerRows, mergedRows);
        bool big = Width > MaxSide || Height > MaxSide || small > int.MaxValue
            || layerRows.Concat([mergedRows]).Any(planes => planes.Any(rows => rows.Any(n => n > ushort.MaxValue)));
        return new Layout(big, big ? Length(big: true, layerRows, mergedRows) : small, layerRows, mergedRows);
    }

    static int[] Checked(int[] rows, int height, string what) =>
        rows.Length == height ? rows : throw new InvalidOperationException($"{what}: {rows.Length} row lengths for {height} rows");

    static long Sum(int[] rows)
    {
        long s = 0;
        foreach (int n in rows) s += n;
        return s;
    }

    /// <summary>A plane's bytes in the file: the compression word, the row lengths, the rows.</summary>
    static long PlaneLength(int[] rows, bool big) => 2 + (long)rows.Length * (big ? 4 : 2) + Sum(rows);

    static int NameLength(string ascii) => (1 + Math.Min(ascii.Length, 255) + 3) / 4 * 4;

    /// <summary>The name in UTF-16 with its length before it, padded to a multiple of four.</summary>
    static int UnicodeNameLength(string name) => (4 + 2 * name.Length + 3) / 4 * 4;

    /// <summary>What follows a layer record's fixed part: no mask, no blending ranges, the two names.</summary>
    static int ExtraLength(PsdLayer l) => 4 + 4 + NameLength(l.AsciiName) + 12 + UnicodeNameLength(l.Name);

    static long RecordLength(PsdLayer l, bool big) => 16 + 2 + 4 * (2 + (big ? 8 : 4)) + 4 + 4 + 4 + 4 + ExtraLength(l);

    /// <summary>The layer records and every layer's planes, padded to a multiple of four.</summary>
    long LayerInfoLength(bool big, int[][][] layerRows)
    {
        long n = 2;
        for (int i = 0; i < Layers.Count; i++) n += RecordLength(Layers[i], big) + layerRows[i].Sum(rows => PlaneLength(rows, big));
        return (n + 3) / 4 * 4;
    }

    /// <summary>The resolution (72 pixels an inch), the one image resource written.</summary>
    const int ResourcesLength = 4 + 2 + 2 + 4 + 16;

    long Length(bool big, int[][][] layerRows, int[][] mergedRows)
    {
        int wide = big ? 8 : 4;
        long layerSection = wide + LayerInfoLength(big, layerRows) + 4;
        return 26 + 4 + 4 + ResourcesLength + wide + layerSection + 2 + mergedRows.Sum(rows => PlaneLength(rows, big) - 2);
    }

    /// <summary>
    /// Writes the file as <see cref="Measure"/> laid it out. <paramref name="progress"/> gets the bytes written so far
    /// after every plane (it may throw to stop the writing).
    /// </summary>
    public void Write(Stream to, Layout layout, Action<long>? progress = null)
    {
        bool big = layout.Big;
        var w = new Writer(to);
        // header
        w.Ascii("8BPS");
        w.U16(big ? 2 : 1);
        w.Zeros(6);
        w.U16(MergedPlanes);
        w.U32((uint)Height);
        w.U32((uint)Width);
        w.U16(8);
        w.U16(3);                                              // RGB
        w.U32(0);                                              // no colour mode data
        // image resources: the resolution
        w.U32(ResourcesLength);
        w.Ascii("8BIM");
        w.U16(1005);
        w.U16(0);                                              // no name
        w.U32(16);
        w.U32(72 << 16);
        w.U16(1);
        w.U16(1);
        w.U32(72 << 16);
        w.U16(1);
        w.U16(1);
        // layers
        long info = LayerInfoLength(big, layout.LayerRows);
        w.Len(big, big ? 8 + info + 4 : 4 + info + 4);
        w.Len(big, info);
        long start = w.Position;
        // a negative count: the merged picture's first extra channel is its transparency
        w.I16(mergedAlpha ? -Layers.Count : Layers.Count);
        for (int i = 0; i < Layers.Count; i++)
        {
            var l = Layers[i];
            w.I32(l.Top);
            w.I32(l.Left);
            w.I32(l.Top + l.Height);
            w.I32(l.Left + l.Width);
            w.U16(4);
            for (int k = 0; k < 4; k++)
            {
                w.I16(LayerPlanes[k] == 3 ? -1 : LayerPlanes[k]);
                w.Len(big, PlaneLength(layout.LayerRows[i][k], big));
            }
            w.Ascii("8BIM");
            w.Ascii(l.BlendMode);
            w.U8(255);                                         // opaque
            w.U8((byte)(l.Clipped ? 1 : 0));
            w.U8(8);                                           // visible
            w.U8(0);
            w.U32((uint)ExtraLength(l));
            w.U32(0);                                          // no mask
            w.U32(0);                                          // no blending ranges
            int name = Math.Min(l.AsciiName.Length, 255);
            w.U8((byte)name);
            w.Ascii(l.AsciiName[..name]);
            w.Zeros(NameLength(l.AsciiName) - 1 - name);
            w.Ascii("8BIM");
            w.Ascii("luni");
            w.U32((uint)UnicodeNameLength(l.Name));
            w.U32((uint)l.Name.Length);
            w.Bytes(Encoding.BigEndianUnicode.GetBytes(l.Name));
            w.Zeros(UnicodeNameLength(l.Name) - 4 - 2 * l.Name.Length);
        }
        for (int i = 0; i < Layers.Count; i++)
            for (int k = 0; k < 4; k++)
            {
                w.U16(1);                                      // packed rows
                foreach (int n in layout.LayerRows[i][k]) w.RowLength(big, n);
                w.Rows(Layers[i].Rows, LayerPlanes[k], Sum(layout.LayerRows[i][k]));
                progress?.Invoke(w.Position);
            }
        w.Zeros((int)(info - (w.Position - start)));
        w.U32(0);                                              // no global layer mask
        // the merged picture: every plane's row lengths, then every plane's rows
        w.U16(1);
        foreach (var rows in layout.MergedRows)
            foreach (int n in rows) w.RowLength(big, n);
        for (int p = 0; p < MergedPlanes; p++)
        {
            w.Rows(merged, p, Sum(layout.MergedRows[p]));
            progress?.Invoke(w.Position);
        }
        w.Flush();
        if (w.Position != layout.Length) throw new InvalidOperationException($"the file is {w.Position} bytes, {layout.Length} were laid out");
    }

    /// <summary>Big-endian values into a stream, through a small buffer.</summary>
    sealed class Writer(Stream to)
    {
        readonly byte[] _buffer = new byte[1 << 16];
        int _used;
        long _flushed;

        public long Position => _flushed + _used;

        Span<byte> Take(int n)
        {
            if (_used + n > _buffer.Length) Flush();
            var s = _buffer.AsSpan(_used, n);
            _used += n;
            return s;
        }

        public void Flush()
        {
            to.Write(_buffer, 0, _used);
            _flushed += _used;
            _used = 0;
        }

        /// <summary>The packed rows of a plane, written by their source: <paramref name="bytes"/> in all.</summary>
        public void Rows(IPsdRows rows, int plane, long bytes)
        {
            Flush();
            rows.Write(plane, to);
            _flushed += bytes;
        }

        public void U8(byte v) => Take(1)[0] = v;
        public void U16(int v) => BinaryPrimitives.WriteUInt16BigEndian(Take(2), (ushort)v);
        public void I16(int v) => BinaryPrimitives.WriteInt16BigEndian(Take(2), (short)v);
        public void U32(uint v) => BinaryPrimitives.WriteUInt32BigEndian(Take(4), v);
        public void I32(int v) => BinaryPrimitives.WriteInt32BigEndian(Take(4), v);
        public void U64(long v) => BinaryPrimitives.WriteInt64BigEndian(Take(8), v);

        /// <summary>A length: 4 bytes, 8 in the large document format.</summary>
        public void Len(bool big, long v)
        {
            if (big) U64(v);
            else U32((uint)v);
        }

        /// <summary>A packed row's length: 2 bytes, 4 in the large document format.</summary>
        public void RowLength(bool big, int v)
        {
            if (big) U32((uint)v);
            else U16(v);
        }

        public void Ascii(string s)
        {
            var t = Take(s.Length);
            for (int i = 0; i < s.Length; i++) t[i] = s[i] < 128 ? (byte)s[i] : (byte)'?';
        }

        public void Zeros(int n) => Take(n).Clear();

        public void Bytes(ReadOnlySpan<byte> b)
        {
            for (int at = 0; at < b.Length;)
            {
                int n = Math.Min(b.Length - at, _buffer.Length);
                b.Slice(at, n).CopyTo(Take(n));
                at += n;
            }
        }
    }
}
