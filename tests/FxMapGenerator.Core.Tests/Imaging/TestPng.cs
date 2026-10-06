using System.Buffers.Binary;
using System.IO.Compression;

namespace FxMapGenerator.Core.Tests.Imaging;

/// <summary>
/// Writes PNG files of any colour type and bit depth for the tests, row by row (a whole frame's picture is never held):
/// the rows come as the file stores their samples, each gets the filter asked for, and the picture data goes out in
/// IDAT chunks of a chosen size.
/// </summary>
public static class TestPng
{
    static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <param name="colourType">0 grey, 2 colour, 3 palette, 4 grey with alpha, 6 colour with alpha.</param>
    /// <param name="row">The samples of a row as the file stores them (packed for depths under 8, two bytes a sample at 16).</param>
    /// <param name="filter">The filter type (0 to 4) of a row; null = none.</param>
    /// <param name="idat">The bytes of picture data a chunk holds at most.</param>
    public static void Write(string path, int width, int height, int colourType, int depth, Func<int, byte[]> row, byte[]? palette = null,
        byte[]? transparent = null, Func<int, int>? filter = null, int idat = 1 << 16, bool interlaced = false)
    {
        int channels = colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int stride = (width * channels * depth + 7) / 8, step = Math.Max(1, channels * depth / 8);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9], header[12]) = ((byte)depth, (byte)colourType, (byte)(interlaced ? 1 : 0));
        Chunk(file, "IHDR", header);
        Chunk(file, "tEXt", "Software\0FxMapGenerator tests"u8.ToArray());
        if (palette is not null) Chunk(file, "PLTE", palette);
        if (transparent is not null) Chunk(file, "tRNS", transparent);

        var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var above = new byte[stride];
            var line = new byte[stride + 1];
            for (int y = 0; y < height; y++)
            {
                var raw = row(y);
                if (raw.Length != stride) throw new ArgumentException($"row {y} has {raw.Length} bytes, not {stride}");
                int f = filter?.Invoke(y) ?? 0;
                line[0] = (byte)f;
                if (f == 0) raw.CopyTo(line, 1);
                else
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= step ? raw[i - step] : 0, b = above[i], c = i >= step ? above[i - step] : 0;
                    int predicted = f switch
                    {
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        4 => Paeth(a, b, c),
                        _ => 0,
                    };
                    line[i + 1] = (byte)(raw[i] - predicted);
                }
                zlib.Write(line);
                raw.CopyTo(above, 0);
                while (packed.Length >= idat) Take(file, packed, idat);
            }
        }
        while (packed.Length > 0) Take(file, packed, (int)Math.Min(idat, packed.Length));
        Chunk(file, "IEND", []);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>The first <paramref name="n"/> bytes of the packed data as a chunk; the rest stays for the next.</summary>
    static void Take(Stream file, MemoryStream packed, int n)
    {
        var all = packed.GetBuffer();
        Chunk(file, "IDAT", all.AsSpan(0, n));
        int rest = (int)packed.Length - n;
        Buffer.BlockCopy(all, n, all, 0, rest);
        packed.SetLength(rest);
        packed.Position = rest;
    }

    static void Chunk(Stream file, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        file.Write(head);
        file.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var b in head[4..]) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        Span<byte> sum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sum, crc ^ 0xFFFFFFFF);
        file.Write(sum);
    }

    /// <summary>
    /// A picture of straight RGBA written as a PNG of colour with alpha, 8 bits a channel: <paramref name="at"/> gives a
    /// row's pixels (a buffer of <paramref name="width"/> x 4 bytes it may give again).
    /// </summary>
    public static void WriteRgba(string path, int width, int height, Func<int, byte[]> at) => Write(path, width, height, 6, 8, at, idat: 1 << 20);
}
