using FxMapGenerator.Core.Export;

namespace FxMapGenerator.Core.Tests.Export;

public sealed class PsdFileTests
{
    static byte[] Unpacked(ReadOnlySpan<byte> packed, int width)
    {
        var row = new byte[width];
        PackBits.Unpack(packed, row);
        return row;
    }

    [Fact]
    public void ARowPackedAndUnpackedIsTheRow()
    {
        var random = new Random(7);
        var rows = new List<byte[]>
        {
            Array.Empty<byte>(),
            new byte[] { 9 },
            new byte[] { 9, 9 },
            new byte[] { 9, 9, 9 },
            Enumerable.Repeat((byte)5, 128).ToArray(),
            Enumerable.Repeat((byte)5, 129).ToArray(),
            Enumerable.Repeat((byte)5, 1000).ToArray(),
            Enumerable.Range(0, 127).Select(i => (byte)i).ToArray(),
            Enumerable.Range(0, 128).Select(i => (byte)i).ToArray(),
            Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(),
            // pairs between other bytes stay as they are; a run of three ends the bytes before it
            new byte[] { 1, 2, 2, 3, 4, 4, 4, 5, 5, 6, 7, 7, 7, 7 },
        };
        for (int k = 0; k < 50; k++)
        {
            var row = new byte[random.Next(1, 700)];
            for (int i = 0; i < row.Length;)
            {
                int n = Math.Min(row.Length - i, random.Next(4) == 0 ? random.Next(1, 300) : 1);
                row.AsSpan(i, n).Fill((byte)random.Next(4));
                i += n;
            }
            rows.Add(row);
        }
        foreach (var row in rows)
        {
            var packed = new byte[PackBits.MaxPacked(row.Length)];
            int n = PackBits.Pack(row, packed);
            Assert.Equal(row, Unpacked(packed.AsSpan(0, n), row.Length));
        }
        // runs pack to two bytes per 128, bytes that differ to themselves and one byte per 128
        Assert.Equal(new byte[] { 129, 5, 129, 5 }, Pack(Enumerable.Repeat((byte)5, 256).ToArray()));
        Assert.Equal(129, Pack(Enumerable.Range(0, 128).Select(i => (byte)i).ToArray()).Length);
        Assert.Equal(new byte[] { 129, 5, 129, 5 }, PackBits.Flat(5, 256));
        Assert.Equal(new byte[] { 129, 5, 0, 5 }, PackBits.Flat(5, 129));
        Assert.Equal(Enumerable.Repeat((byte)5, 130).ToArray(), Unpacked(PackBits.Flat(5, 130), 130));
    }

    static byte[] Pack(byte[] row)
    {
        var packed = new byte[PackBits.MaxPacked(row.Length)];
        return packed.AsSpan(0, PackBits.Pack(row, packed)).ToArray();
    }

    [Fact]
    public void PiecesPackedApartAreTheRowPacked()
    {
        var row = Enumerable.Range(0, 600).Select(i => (byte)(i / 70)).ToArray();
        var joined = Pack(row[..256]).Concat(Pack(row[256..512])).Concat(Pack(row[512..])).ToArray();
        Assert.Equal(row, Unpacked(joined, row.Length));
        Assert.Throws<InvalidDataException>(() => PackBits.Unpack(joined, new byte[599]));
        Assert.Throws<InvalidDataException>(() => PackBits.Unpack(joined, new byte[601]));
    }

    static byte[] Picture(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> at)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                (rgba[(y * width + x) * 4], rgba[(y * width + x) * 4 + 1], rgba[(y * width + x) * 4 + 2], rgba[(y * width + x) * 4 + 3]) = at(x, y);
        return rgba;
    }

    static (PsdReader Read, PsdFile.Layout Layout, byte[] Bytes) WriteAndRead(PsdFile file)
    {
        var layout = file.Measure();
        using var ms = new MemoryStream();
        file.Write(ms, layout);
        var bytes = ms.ToArray();
        Assert.Equal(layout.Length, bytes.Length);
        return (PsdReader.Read(bytes), layout, bytes);
    }

    [Fact]
    public void AFileReadBackHasItsLayersAndItsMergedPicture()
    {
        const int w = 300, h = 40;
        var ground = Picture(w, h, (x, y) => ((byte)(x / 3), (byte)(y * 5), 77, 255));
        var shade = Picture(200, 10, (x, y) => (128, 128, 128, (byte)(x < 100 ? 255 : 0)));
        var names = Picture(w, h, (x, y) => (20, 30, 40, (byte)((x + y) % 7 == 0 ? 200 : 0)));
        var merged = Picture(w, h, (x, y) => ((byte)x, (byte)y, 9, (byte)(x < 150 ? 255 : 90)));
        var file = new PsdFile(w, h,
        [
            new PsdLayer("地面", "Ground", PsdFile.Normal, 0, 0, w, h, new PackedPicture(ground, w, h)),
            new PsdLayer("陰影（暗い側）", "Shading (dark side)", PsdFile.Multiply, 50, 20, 200, 10, new PackedPicture(shade, 200, 10), Clipped: true),
            new PsdLayer("Street names", "Street names", PsdFile.Normal, 0, 0, w, h, new PackedPicture(names, w, h)),
        ], new PackedPicture(merged, w, h), mergedAlpha: true);
        var (read, layout, bytes) = WriteAndRead(file);
        Assert.False(layout.Big);
        Assert.Equal((false, w, h, 4, true), (read.Big, read.Width, read.Height, read.Channels, read.MergedTransparency));
        Assert.Equal(new[] { "地面", "陰影（暗い側）", "Street names" }, read.Layers.Select(l => l.Name));
        Assert.Equal(new[] { "Ground", "Shading (dark side)", "Street names" }, read.Layers.Select(l => l.AsciiName));
        Assert.Equal(new[] { "norm", "mul ", "norm" }, read.Layers.Select(l => l.Blend));
        Assert.All(read.Layers, l => Assert.Equal((255, 8), (l.Opacity, l.Flags)));
        Assert.Equal(new[] { false, true, false }, read.Layers.Select(l => l.Clipped));
        Assert.Equal((50, 20, 200, 10), (read.Layers[1].Left, read.Layers[1].Top, read.Layers[1].Width, read.Layers[1].Height));
        Assert.Equal(ground, read.Layers[0].Rgba);
        Assert.Equal(shade, read.Layers[1].Rgba);
        Assert.Equal(names, read.Layers[2].Rgba);
        Assert.Equal(merged, read.Merged);
        Assert.Equal("8BPS"u8.ToArray(), bytes[..4]);

        // an opaque merged picture: three channels, a positive layer count
        var opaque = new PsdFile(w, h, [new PsdLayer("Satellite map", "Satellite map", PsdFile.Normal, 0, 0, w, h, new PackedPicture(ground, w, h))],
            new PackedPicture(ground, w, h), mergedAlpha: false);
        var (plain, _, _) = WriteAndRead(opaque);
        Assert.Equal((3, false), (plain.Channels, plain.MergedTransparency));
        Assert.Equal(ground, plain.Merged);
    }

    [Fact]
    public void APictureOverTheLongestSideIsALargeDocument()
    {
        const int w = PsdFile.MaxSide + 1, h = 2;
        var rgba = Picture(w, h, (x, y) => ((byte)(x / 200), (byte)y, 3, 255));
        var file = new PsdFile(w, h, [new PsdLayer("Ground", "Ground", PsdFile.Normal, 0, 0, w, h, new PackedPicture(rgba, w, h))],
            new PackedPicture(rgba, w, h), mergedAlpha: false);
        var (read, layout, _) = WriteAndRead(file);
        Assert.True(layout.Big);
        Assert.Equal((true, w, h), (read.Big, read.Width, read.Height));
        Assert.Equal(rgba, read.Layers[0].Rgba);
        Assert.Equal(rgba, read.Merged);
    }
}
