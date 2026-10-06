using System.Buffers.Binary;
using System.IO.Compression;

namespace FxMapGenerator.Core.Imaging;

/// <summary>
/// A PNG file written row by row, top row first, from straight RGBA of 8 bits a channel, so that a picture of any size
/// is never held whole (the reading side is <see cref="PngRows"/>). Colour type 6, not interlaced; every row takes the
/// filter whose bytes sum least (as signed values), the usual choice for pictures of full colour. Follows the PNG
/// specification (W3C, third edition).
/// </summary>
public sealed class PngWriter : IDisposable
{
    static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    const uint Ihdr = 0x49484452, Idat = 0x49444154, Iend = 0x49454E44;
    const int Step = 4;

    readonly Stream _to;
    readonly bool _leaveOpen;
    readonly ChunkStream _chunks;
    readonly ZLibStream _data;
    readonly byte[][] _filtered = new byte[5][];
    byte[] _row, _above;
    int _next;
    bool _done;

    public int Width { get; }
    public int Height { get; }

    /// <summary>The rows not written yet.</summary>
    public int RowsLeft => Height - _next;

    /// <param name="to">Where the file goes (written from its position on).</param>
    /// <param name="leaveOpen">Leave <paramref name="to"/> open when the writer is disposed.</param>
    public PngWriter(Stream to, int width, int height, CompressionLevel level = CompressionLevel.Optimal, bool leaveOpen = false)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"a picture of {width} x {height} pixels");
        (_to, _leaveOpen, Width, Height) = (to, leaveOpen, width, height);
        int stride = checked(width * Step);
        _row = new byte[stride];
        _above = new byte[stride];
        for (int f = 0; f < _filtered.Length; f++) _filtered[f] = new byte[1 + stride];
        to.Write(Signature);
        Span<byte> head = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], (uint)height);
        (head[8], head[9], head[10], head[11], head[12]) = (8, 6, 0, 0, 0);
        WriteChunk(to, Ihdr, head);
        _chunks = new ChunkStream(to);
        _data = new ZLibStream(_chunks, level, leaveOpen: true);
    }

    /// <summary>Opens a file for a picture (made anew).</summary>
    public static PngWriter Create(string path, int width, int height, CompressionLevel level = CompressionLevel.Optimal) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), width, height, level);

    /// <summary>Writes the next rows: <paramref name="rgba"/> holds whole rows (4 bytes a pixel, <see cref="Width"/> pixels a row).</summary>
    /// <exception cref="ArgumentException">Not whole rows, or more rows than the picture has left.</exception>
    public void Write(ReadOnlySpan<byte> rgba)
    {
        int stride = Width * Step;
        if (rgba.Length % stride != 0 || rgba.Length / stride > RowsLeft)
            throw new ArgumentException($"{rgba.Length} bytes are not rows of {Width} pixels the picture has left ({RowsLeft})");
        for (int at = 0; at < rgba.Length; at += stride)
        {
            rgba.Slice(at, stride).CopyTo(_row);
            _data.Write(Filtered());
            (_row, _above) = (_above, _row);
            _next++;
        }
    }

    /// <summary>The row with the filter that leaves the least to pack: the filter's number, then the row's bytes.</summary>
    byte[] Filtered()
    {
        byte[] row = _row, above = _above;
        int n = row.Length;
        byte[] none = _filtered[0], sub = _filtered[1], up = _filtered[2], average = _filtered[3], paeth = _filtered[4];
        for (int f = 0; f < 5; f++) _filtered[f][0] = (byte)f;
        // a clear row, or a row that is the one above it (the large empty and flat parts of a layer): nothing but zeros
        if (row.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            Array.Clear(none, 1, n);
            return none;
        }
        if (row.AsSpan().SequenceEqual(above))
        {
            Array.Clear(up, 1, n);
            return up;
        }
        row.CopyTo(none.AsSpan(1));
        for (int i = 0; i < n; i++)
        {
            int x = row[i], a = i >= Step ? row[i - Step] : 0, b = above[i], c = i >= Step ? above[i - Step] : 0;
            sub[i + 1] = (byte)(x - a);
            up[i + 1] = (byte)(x - b);
            average[i + 1] = (byte)(x - ((a + b) >> 1));
            int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
            paeth[i + 1] = (byte)(x - (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
        }
        int best = 0;
        long least = long.MaxValue;
        for (int f = 0; f < 5; f++)
        {
            long sum = 0;
            var bytes = _filtered[f];
            for (int i = 1; i <= n; i++)
            {
                int v = (sbyte)bytes[i];
                sum += v < 0 ? -v : v;
            }
            if (sum < least) (best, least) = (f, sum);
        }
        return _filtered[best];
    }

    /// <summary>Ends the file. Every row must have been written.</summary>
    /// <exception cref="InvalidOperationException">Rows are missing.</exception>
    public void Finish()
    {
        if (_done) return;
        if (RowsLeft != 0) throw new InvalidOperationException($"{RowsLeft} of {Height} rows of the picture were not written");
        _done = true;
        _data.Dispose();
        _chunks.End();
        WriteChunk(_to, Iend, []);
        _to.Flush();
    }

    /// <summary>Ends the file when every row is written (<see cref="Finish"/>); a picture left unfinished is not a PNG file.</summary>
    public void Dispose()
    {
        try
        {
            if (RowsLeft == 0) Finish();
            else if (!_done) _data.Dispose();
        }
        finally
        {
            if (!_leaveOpen) _to.Dispose();
        }
    }

    static void WriteChunk(Stream to, uint type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], type);
        to.Write(head);
        to.Write(data);
        uint crc = Crc.Add(Crc.Add(0xFFFFFFFF, head[4..]), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(head, crc);
        to.Write(head[..4]);
    }

    /// <summary>The check sum of a chunk (CRC-32, as the specification gives it).</summary>
    static class Crc
    {
        static readonly uint[] Table = Make();

        static uint[] Make()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <summary>The running sum (started at all ones, inverted at the end) with more bytes taken in.</summary>
        public static uint Add(uint crc, ReadOnlySpan<byte> bytes)
        {
            var t = Table;
            foreach (byte b in bytes) crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }

    /// <summary>The packed picture data cut into IDAT chunks of <see cref="Size"/> bytes.</summary>
    sealed class ChunkStream(Stream to) : Stream
    {
        const int Size = 1 << 16;
        readonly byte[] _buffer = new byte[Size];
        int _held;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                int n = Math.Min(buffer.Length, Size - _held);
                buffer[..n].CopyTo(_buffer.AsSpan(_held));
                _held += n;
                buffer = buffer[n..];
                if (_held == Size) End();
            }
        }

        /// <summary>Writes what is held as a chunk (nothing when nothing is held).</summary>
        public void End()
        {
            if (_held == 0) return;
            WriteChunk(to, Idat, _buffer.AsSpan(0, _held));
            _held = 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
