using System.Buffers.Binary;
using System.IO.Compression;

namespace FxMapGenerator.Core.Imaging;

/// <summary>Why a file cannot be read row by row as a PNG picture (<see cref="PngRows"/>).</summary>
public sealed class PngException(string code, string message) : Exception(message)
{
    /// <summary><c>NOT_PNG</c> (not a PNG file, or one that is broken or cut short) or <c>INTERLACED</c>.</summary>
    public string Code { get; } = code;

    public const string NotPng = "NOT_PNG", Interlaced = "INTERLACED";
}

/// <summary>
/// A PNG file read row by row, top row first, as straight RGBA of 8 bits a channel, so that a picture of any size is
/// never held whole (a map's whole frame at zoom 7 is 1.6 GB of pixels). Reads every colour type (grey, colour, palette,
/// with or without alpha, a <c>tRNS</c> colour or palette alphas) at every bit depth; 16 bits a channel keep their upper
/// 8. Interlaced files are refused: their rows do not come in order. Colour profiles and gamma are not looked at, and
/// the check sums of the chunks are not verified. Follows the PNG specification (W3C, third edition).
/// </summary>
public sealed class PngRows : IDisposable
{
    static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    const uint Ihdr = 0x49484452, Plte = 0x504C5445, Trns = 0x74524E53, Idat = 0x49444154;

    readonly FileStream _file;
    readonly ZLibStream _data;
    readonly int _colourType, _depth, _stride, _step;
    readonly byte[]? _palette, _transparent;
    byte[] _row, _above;
    int _next;

    public int Width { get; }
    public int Height { get; }

    /// <summary>The rows not read yet.</summary>
    public int RowsLeft => Height - _next;

    PngRows(FileStream file, int width, int height, int depth, int colourType, byte[]? palette, byte[]? transparent, int firstIdat)
    {
        _file = file;
        Width = width;
        Height = height;
        _depth = depth;
        _colourType = colourType;
        _palette = palette;
        _transparent = transparent;
        int channels = colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bits = channels * depth;
        _stride = checked((int)(((long)width * bits + 7) / 8));
        _step = Math.Max(1, bits / 8);
        _row = new byte[_stride];
        _above = new byte[_stride];
        _data = new ZLibStream(new IdatStream(file, firstIdat), CompressionMode.Decompress);
    }

    /// <summary>The size of a PNG file from its header alone.</summary>
    /// <exception cref="PngException">Not a PNG file, or an interlaced one.</exception>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    public static (int Width, int Height) Size(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var (width, height, _, _) = Header(file, path);
        return (width, height);
    }

    /// <summary>Opens a PNG file for reading its rows. The file cannot be written to while it is open.</summary>
    /// <exception cref="PngException">Not a PNG file, or an interlaced one.</exception>
    /// <exception cref="IOException">The file cannot be opened.</exception>
    public static PngRows Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        try
        {
            var (width, height, depth, colourType) = Header(file, path);
            byte[]? palette = null, transparent = null;
            Span<byte> head = stackalloc byte[8];
            while (true)
            {
                if (file.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8) throw Broken(path, "it has no picture data");
                uint length = BinaryPrimitives.ReadUInt32BigEndian(head), type = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
                if (length > int.MaxValue) throw Broken(path, "a chunk is too long");
                if (type == Idat)
                {
                    if (colourType == 3 && palette is null) throw Broken(path, "its palette is missing");
                    return new PngRows(file, width, height, depth, colourType, palette, transparent, (int)length);
                }
                if (type is Plte or Trns)
                {
                    var bytes = new byte[length];
                    if (file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) < bytes.Length) throw Broken(path, "it ends early");
                    if (type == Plte) palette = bytes;
                    else transparent = bytes;
                    file.Seek(4, SeekOrigin.Current);
                }
                else file.Seek(length + 4, SeekOrigin.Current);
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    static (int Width, int Height, int Depth, int ColourType) Header(FileStream file, string path)
    {
        Span<byte> head = stackalloc byte[8 + 8 + 13];
        if (file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || !head[..8].SequenceEqual(Signature)
            || BinaryPrimitives.ReadUInt32BigEndian(head[8..]) != 13 || BinaryPrimitives.ReadUInt32BigEndian(head[12..]) != Ihdr)
            throw new PngException(PngException.NotPng, $"{path} is not a PNG file");
        var h = head[16..];
        uint width = BinaryPrimitives.ReadUInt32BigEndian(h), height = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
        int depth = h[8], colourType = h[9];
        bool known = colourType switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => depth is 8 or 16,
            _ => false,
        };
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue || !known || h[10] != 0 || h[11] != 0)
            throw Broken(path, "its header is not a PNG header");
        if (h[12] != 0) throw new PngException(PngException.Interlaced, $"{path} is an interlaced PNG: its rows cannot be read one by one");
        file.Seek(4, SeekOrigin.Current);
        return ((int)width, (int)height, depth, colourType);
    }

    static PngException Broken(string path, string why) => new(PngException.NotPng, $"{path} cannot be read as a PNG picture: {why}");

    /// <summary>
    /// Reads the next rows into <paramref name="rgba"/> (4 bytes a pixel, <see cref="Width"/> pixels a row), as many as
    /// it holds whole and the picture has left. Returns the rows read.
    /// </summary>
    /// <exception cref="PngException">The picture data is broken or ends early.</exception>
    public int Read(Span<byte> rgba)
    {
        int rowBytes = Width * 4, rows = Math.Min(rgba.Length / rowBytes, RowsLeft);
        for (int r = 0; r < rows; r++)
        {
            int filter;
            try
            {
                filter = _data.ReadByte();
                if (filter >= 0) _data.ReadExactly(_row);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                throw Broken(_file.Name, $"its picture data is broken at row {_next}");
            }
            if (filter < 0) throw Broken(_file.Name, $"it ends at row {_next} of {Height}");
            Unfilter(filter);
            Convert(rgba.Slice(r * rowBytes, rowBytes));
            (_row, _above) = (_above, _row);
            _next++;
        }
        return rows;
    }

    /// <summary>Takes the row's filter off in place (the bytes to the left, above and above left are those already restored).</summary>
    void Unfilter(int filter)
    {
        byte[] row = _row, above = _above;
        int n = row.Length, step = _step;
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = step; i < n; i++) row[i] += row[i - step];
                break;
            case 2:
                for (int i = 0; i < n; i++) row[i] += above[i];
                break;
            case 3:
                for (int i = 0; i < step && i < n; i++) row[i] += (byte)(above[i] >> 1);
                for (int i = step; i < n; i++) row[i] += (byte)((row[i - step] + above[i]) >> 1);
                break;
            case 4:
                for (int i = 0; i < step && i < n; i++) row[i] += above[i];
                for (int i = step; i < n; i++)
                {
                    int a = row[i - step], b = above[i], c = above[i - step];
                    int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                break;
            default:
                throw Broken(_file.Name, $"row {_next} has an unknown filter");
        }
    }

    /// <summary>The restored row as straight RGBA.</summary>
    void Convert(Span<byte> o)
    {
        var row = _row;
        int w = Width, wide = _depth == 16 ? 2 : 1;
        switch (_colourType)
        {
            case 6:
                if (wide == 1) row.AsSpan(0, w * 4).CopyTo(o);
                else
                    for (int x = 0; x < w; x++)
                    {
                        o[4 * x] = row[8 * x];
                        o[4 * x + 1] = row[8 * x + 2];
                        o[4 * x + 2] = row[8 * x + 4];
                        o[4 * x + 3] = row[8 * x + 6];
                    }
                break;
            case 2:
                for (int x = 0, i = 0; x < w; x++, i += 3 * wide)
                {
                    o[4 * x] = row[i];
                    o[4 * x + 1] = row[i + wide];
                    o[4 * x + 2] = row[i + 2 * wide];
                    o[4 * x + 3] = _transparent is { Length: >= 6 } t && Same(row, i, t, 0, wide) && Same(row, i + wide, t, 2, wide) && Same(row, i + 2 * wide, t, 4, wide) ? (byte)0 : (byte)255;
                }
                break;
            case 4:
                for (int x = 0, i = 0; x < w; x++, i += 2 * wide)
                {
                    o[4 * x] = o[4 * x + 1] = o[4 * x + 2] = row[i];
                    o[4 * x + 3] = row[i + wide];
                }
                break;
            case 0:
            {
                int clear = _transparent is { Length: >= 2 } t ? (t[0] << 8) | t[1] : -1;
                if (_depth == 16)
                    for (int x = 0; x < w; x++)
                    {
                        o[4 * x] = o[4 * x + 1] = o[4 * x + 2] = row[2 * x];
                        o[4 * x + 3] = ((row[2 * x] << 8) | row[2 * x + 1]) == clear ? (byte)0 : (byte)255;
                    }
                else
                {
                    int max = (1 << _depth) - 1;
                    for (int x = 0; x < w; x++)
                    {
                        int v = Sample(row, x);
                        o[4 * x] = o[4 * x + 1] = o[4 * x + 2] = (byte)(v * 255 / max);
                        o[4 * x + 3] = v == clear ? (byte)0 : (byte)255;
                    }
                }
                break;
            }
            default:
            {
                byte[] palette = _palette!;
                for (int x = 0; x < w; x++)
                {
                    int v = Sample(row, x);
                    if (3 * v + 2 >= palette.Length) throw Broken(_file.Name, $"row {_next} names a colour its palette does not have");
                    o[4 * x] = palette[3 * v];
                    o[4 * x + 1] = palette[3 * v + 1];
                    o[4 * x + 2] = palette[3 * v + 2];
                    o[4 * x + 3] = _transparent is { } t && v < t.Length ? t[v] : (byte)255;
                }
                break;
            }
        }
    }

    /// <summary>A sample of 1, 2, 4 or 8 bits (the leftmost pixel in the high bits of a byte).</summary>
    int Sample(byte[] row, int x) => _depth == 8 ? row[x] : (row[x * _depth >> 3] >> (8 - _depth - (x * _depth & 7))) & ((1 << _depth) - 1);

    /// <summary>A sample of the row equals the 16-bit value of a <c>tRNS</c> colour (an 8-bit sample is its low byte).</summary>
    static bool Same(byte[] row, int i, byte[] colour, int at, int wide) =>
        wide == 2 ? row[i] == colour[at] && row[i + 1] == colour[at + 1] : row[i] == colour[at + 1];

    public void Dispose()
    {
        _data.Dispose();
        _file.Dispose();
    }

    /// <summary>The bytes of the picture data: those of the IDAT chunks one after the other, up to the first other chunk.</summary>
    sealed class IdatStream(FileStream file, int first) : Stream
    {
        int _left = first;
        bool _end;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Span<byte> head = stackalloc byte[12];
            while (_left == 0 && !_end)
            {
                // the check sum of the chunk read, then the next chunk's length and type
                if (file.ReadAtLeast(head, 12, throwOnEndOfStream: false) < 12 || BinaryPrimitives.ReadUInt32BigEndian(head[8..]) != Idat) _end = true;
                else
                {
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
                    if (length > int.MaxValue) _end = true;
                    else _left = (int)length;
                }
            }
            if (_end || buffer.Length == 0) return 0;
            int n = file.Read(buffer[..Math.Min(buffer.Length, _left)]);
            if (n == 0) _end = true;
            _left -= n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
