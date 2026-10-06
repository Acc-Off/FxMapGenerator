using System.Buffers.Binary;
using System.Text;
using FxMapGenerator.Core.Export;

namespace FxMapGenerator.Core.Tests.Export;

/// <summary>A picture held as packed rows in memory (the rows of straight RGBA), for writing test files.</summary>
public sealed class PackedPicture : IPsdRows
{
    readonly byte[][][] _rows = new byte[4][][];

    public PackedPicture(byte[] rgba, int width, int height)
    {
        var plane = new byte[width];
        var packed = new byte[PackBits.MaxPacked(width)];
        for (int p = 0; p < 4; p++)
        {
            _rows[p] = new byte[height][];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) plane[x] = rgba[(y * width + x) * 4 + p];
                _rows[p][y] = packed.AsSpan(0, PackBits.Pack(plane, packed)).ToArray();
            }
        }
    }

    public int[] Lengths(int plane) => _rows[plane].Select(r => r.Length).ToArray();

    public void Write(int plane, Stream to)
    {
        foreach (var r in _rows[plane]) to.Write(r);
    }
}

/// <summary>
/// Reads back what <see cref="PsdFile"/> writes: the header, the layers with their pictures, the merged picture. With a
/// region (a rectangle of the document), only that part of every picture is unpacked: a layer's picture and the merged
/// one are then the region's size, clear where the layer does not reach.
/// </summary>
public sealed class PsdReader
{
    /// <param name="Rgba">The layer's picture (straight RGBA), or the region's part of it.</param>
    public sealed record Layer(string Name, string AsciiName, string Blend, int Left, int Top, int Width, int Height, byte Opacity, byte Flags, byte[] Rgba, bool Clipped = false);

    public bool Big { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Channels { get; private set; }
    /// <summary>The layer count was written negative: the merged picture's fourth channel is its transparency.</summary>
    public bool MergedTransparency { get; private set; }
    public List<Layer> Layers { get; } = [];
    /// <summary>The merged picture as straight RGBA (alpha 255 when the file has three channels), or the region's part of it.</summary>
    public byte[] Merged { get; private set; } = [];

    byte[] _b = [];
    long _at;
    (int X, int Y, int W, int H)? _region;

    int U8() => _b[_at++];
    int U16() { int v = BinaryPrimitives.ReadUInt16BigEndian(_b.AsSpan((int)_at)); _at += 2; return v; }
    int I16() { int v = BinaryPrimitives.ReadInt16BigEndian(_b.AsSpan((int)_at)); _at += 2; return v; }
    long U32() { long v = BinaryPrimitives.ReadUInt32BigEndian(_b.AsSpan((int)_at)); _at += 4; return v; }
    int I32() { int v = BinaryPrimitives.ReadInt32BigEndian(_b.AsSpan((int)_at)); _at += 4; return v; }
    long U64() { long v = BinaryPrimitives.ReadInt64BigEndian(_b.AsSpan((int)_at)); _at += 8; return v; }
    long Len() => Big ? U64() : U32();
    /// <summary>Passes a block that starts with its length (4 bytes).</summary>
    void Skip() { long n = U32(); _at += n; }
    string Ascii(int n) { var s = Encoding.ASCII.GetString(_b, (int)_at, n); _at += n; return s; }

    int[] RowLengths(int height)
    {
        var lengths = new int[height];
        for (int y = 0; y < height; y++) lengths[y] = Big ? (int)U32() : U16();
        return lengths;
    }

    /// <summary>The size of the pictures read: a picture's own, or the region's.</summary>
    (int W, int H) SizeOf(int width, int height) => _region is { } r ? (r.W, r.H) : (width, height);

    /// <summary>The packed rows at the reading place, of a picture whose corner is at (left, top) of the document: the whole plane, or the region's part.</summary>
    byte[] Rows(int[] lengths, int left, int top, int width)
    {
        var (ow, oh) = SizeOf(width, lengths.Length);
        var plane = new byte[ow * oh];
        var row = new byte[width];
        for (int y = 0; y < lengths.Length; y++)
        {
            if (_region is not { } r) PackBits.Unpack(_b.AsSpan((int)_at, lengths[y]), plane.AsSpan(y * width, width));
            else if (top + y >= r.Y && top + y < r.Y + r.H)
            {
                PackBits.Unpack(_b.AsSpan((int)_at, lengths[y]), row);
                int x0 = Math.Max(left, r.X), x1 = Math.Min(left + width, r.X + r.W);
                if (x1 > x0) row.AsSpan(x0 - left, x1 - x0).CopyTo(plane.AsSpan((top + y - r.Y) * r.W + (x0 - r.X)));
            }
            _at += lengths[y];
        }
        return plane;
    }

    public static PsdReader Read(string path, (int X, int Y, int W, int H)? region = null) => Read(File.ReadAllBytes(path), region);

    public static PsdReader Read(byte[] file, (int X, int Y, int W, int H)? region = null)
    {
        var r = new PsdReader { _b = file, _region = region };
        Assert.Equal("8BPS", r.Ascii(4));
        int version = r.U16();
        Assert.Contains(version, new[] { 1, 2 });
        r.Big = version == 2;
        r._at += 6;
        r.Channels = r.U16();
        r.Height = (int)r.U32();
        r.Width = (int)r.U32();
        Assert.Equal((8, 3), (r.U16(), r.U16()));               // 8 bits, RGB
        r.Skip();                                               // colour mode data
        r.Skip();                                               // image resources
        long sectionEnd = r.Len();
        sectionEnd += r._at;
        long infoEnd = r.Len();
        infoEnd += r._at;
        int count = r.I16();
        r.MergedTransparency = count < 0;
        var heads = new List<(Layer L, int[] Ids, long[] Lengths)>();
        for (int i = 0; i < Math.Abs(count); i++)
        {
            int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32();
            int channels = r.U16();
            var ids = new int[channels];
            var lengths = new long[channels];
            for (int k = 0; k < channels; k++) (ids[k], lengths[k]) = (r.I16(), r.Len());
            Assert.Equal("8BIM", r.Ascii(4));
            string blend = r.Ascii(4);
            byte opacity = (byte)r.U8();
            bool clipped = r.U8() == 1;
            byte flags = (byte)r.U8();
            r.U8();
            long extraEnd = r.U32();
            extraEnd += r._at;
            r.Skip();                                           // mask
            r.Skip();                                           // blending ranges
            int n = r.U8();
            string ascii = r.Ascii(n);
            r._at += (4 - (1 + n) % 4) % 4;
            string name = ascii;
            while (r._at < extraEnd)
            {
                Assert.Equal("8BIM", r.Ascii(4));
                string key = r.Ascii(4);
                long blockEnd = r.U32();
                blockEnd += r._at;
                if (key == "luni")
                {
                    int chars = (int)r.U32();
                    name = Encoding.BigEndianUnicode.GetString(file, (int)r._at, 2 * chars);
                }
                r._at = blockEnd;
            }
            Assert.Equal(extraEnd, r._at);
            heads.Add((new Layer(name, ascii, blend, left, top, right - left, bottom - top, opacity, flags, [], clipped), ids, lengths));
        }
        foreach (var (l, ids, lengths) in heads)
        {
            var (ow, oh) = r.SizeOf(l.Width, l.Height);
            var rgba = new byte[ow * oh * 4];
            for (int k = 0; k < ids.Length; k++)
            {
                long end = r._at + lengths[k];
                Assert.Equal(1, r.U16());                       // packed rows
                var plane = r.Rows(r.RowLengths(l.Height), l.Left, l.Top, l.Width);
                Assert.Equal(end, r._at);
                int p = ids[k] == -1 ? 3 : ids[k];
                for (int i = 0; i < plane.Length; i++) rgba[i * 4 + p] = plane[i];
            }
            r.Layers.Add(l with { Rgba = rgba });
        }
        Assert.InRange(infoEnd - r._at, 0, 3);                  // padded to a multiple of four
        r._at = infoEnd;
        r.Skip();                                               // global layer mask
        Assert.Equal(sectionEnd, r._at);
        Assert.Equal(1, r.U16());                               // packed rows
        var all = new int[r.Channels][];
        for (int p = 0; p < r.Channels; p++) all[p] = r.RowLengths(r.Height);
        var (mw, mh) = r.SizeOf(r.Width, r.Height);
        r.Merged = new byte[mw * mh * 4];
        if (r.Channels == 3)
            for (int i = 3; i < r.Merged.Length; i += 4) r.Merged[i] = 255;
        for (int p = 0; p < r.Channels; p++)
        {
            var plane = r.Rows(all[p], 0, 0, r.Width);
            for (int i = 0; i < plane.Length; i++) r.Merged[i * 4 + p] = plane[i];
        }
        Assert.Equal(file.Length, r._at);
        return r;
    }
}
