using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Satellite;

namespace FxMapGenerator.Core.Tests.Imaging;

/// <summary>PNG files read row by row (<see cref="PngRows"/>), against the same files decoded whole by Skia.</summary>
public sealed class PngRowsTests
{
    /// <summary>The whole picture read in bands of <paramref name="band"/> rows.</summary>
    static byte[] ReadAll(string path, int band, out int width, out int height)
    {
        using var png = PngRows.Open(path);
        (width, height) = (png.Width, png.Height);
        var all = new byte[width * height * 4];
        int at = 0;
        var rows = new byte[width * band * 4];
        while (png.RowsLeft > 0)
        {
            int n = png.Read(rows);
            Assert.InRange(n, 1, band);
            Buffer.BlockCopy(rows, 0, all, at, n * width * 4);
            at += n * width * 4;
        }
        Assert.Equal(all.Length, at);
        Assert.Equal(0, png.Read(rows));
        return all;
    }

    /// <summary>Samples that change along a row and from row to row, so every filter has something to predict.</summary>
    static byte[] Samples(int y, int bytes, int seed)
    {
        var row = new byte[bytes];
        for (int i = 0; i < bytes; i++) row[i] = (byte)((i * 7 + y * 13 + seed + (i * y) % 11) & 0xFF);
        return row;
    }

    [Theory]
    [InlineData(6, 8)]
    [InlineData(6, 16)]
    [InlineData(2, 8)]
    [InlineData(2, 16)]
    [InlineData(4, 8)]
    [InlineData(4, 16)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 4)]
    [InlineData(0, 8)]
    [InlineData(0, 16)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 4)]
    [InlineData(3, 8)]
    public void EveryColourTypeAndDepthReadsAsSkiaDecodesIt(int colourType, int depth)
    {
        using var tmp = new TempFolder();
        const int w = 37, h = 23;                                  // odd sizes: the last byte of a packed row is partly used
        int channels = colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int stride = (w * channels * depth + 7) / 8;
        byte[] Row(int y)
        {
            var row = Samples(y, stride, colourType * 31 + depth);
            // 16 bits a sample: both bytes the same, so that every way of making 8 bits of them gives that byte
            if (depth == 16)
                for (int i = 0; i + 1 < stride; i += 2) row[i + 1] = row[i];
            return row;
        }
        var palette = colourType == 3 ? Enumerable.Range(0, 3 << depth).Select(i => (byte)(i * 37 + 11)).ToArray() : null;
        var path = tmp.File("p.png");
        // every filter in turn, and the picture data in chunks of 100 bytes
        TestPng.Write(path, w, h, colourType, depth, Row, palette, filter: y => y % 5, idat: 100);

        var mine = ReadAll(path, 5, out int mw, out int mh);
        var skia = Images.LoadRgba(path, out int sw, out int sh);
        Assert.Equal((w, h, w, h), (mw, mh, sw, sh));
        Assert.Equal(skia, mine);
        Assert.Equal((w, h), PngRows.Size(path));
    }

    [Fact]
    public void TransparentColoursAndPaletteAlphasAreRead()
    {
        using var tmp = new TempFolder();
        const int w = 16, h = 4;
        // colour: the pixels of one colour are see-through
        byte[] Rgb(int y) => Enumerable.Range(0, w).SelectMany(x => x % 4 == 1 ? new byte[] { 10, 20, 30 } : new byte[] { (byte)(x * 9), (byte)(y * 40), 77 }).ToArray();
        var colour = tmp.File("colour.png");
        TestPng.Write(colour, w, h, 2, 8, Rgb, transparent: [0, 10, 0, 20, 0, 30], filter: _ => 4);
        var px = ReadAll(colour, h, out _, out _);
        Assert.Equal(Images.LoadRgba(colour, out _, out _), px);
        Assert.Equal((10, 20, 30, 0), (px[4], px[5], px[6], px[7]));
        Assert.Equal(255, px[3]);

        // grey of 4 bits: one grey is see-through
        byte[] Grey(int y) => Enumerable.Range(0, w / 2).Select(i => (byte)(((i + y) % 16) << 4 | 5)).ToArray();
        var grey = tmp.File("grey.png");
        TestPng.Write(grey, w, h, 0, 4, Grey, transparent: [0, 5], filter: _ => 1);
        px = ReadAll(grey, 1, out _, out _);
        Assert.Equal(Images.LoadRgba(grey, out _, out _), px);
        Assert.Equal((85, 85, 85, 0), (px[4], px[5], px[6], px[7]));     // 5 of 15 is 85 of 255

        // a palette of four colours, the first two with an opacity of their own
        byte[] Index(int y) => Enumerable.Range(0, w / 4).Select(i => (byte)(0b00_01_10_11 << (2 * ((i + y) % 4)) | 0b00_01_10_11 >> (8 - 2 * ((i + y) % 4)))).ToArray();
        var indexed = tmp.File("palette.png");
        TestPng.Write(indexed, w, h, 3, 2, Index, palette: [200, 0, 0, 0, 200, 0, 0, 0, 200, 9, 9, 9], transparent: [0, 128]);
        px = ReadAll(indexed, 3, out _, out _);
        Assert.Equal(Images.LoadRgba(indexed, out _, out _), px);
        Assert.Equal((200, 0, 0, 0), (px[0], px[1], px[2], px[3]));       // row 0 starts with the colours 0, 1, 2, 3
        Assert.Equal((0, 200, 0, 128), (px[4], px[5], px[6], px[7]));
        Assert.Equal((9, 9, 9, 255), (px[12], px[13], px[14], px[15]));
    }

    [Fact]
    public void APictureSkiaWroteReadsBackExactly()
    {
        using var tmp = new TempFolder();
        const int w = 256, h = 256;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[4 * i] = (byte)(i % w);
            rgba[4 * i + 1] = (byte)(i / w);
            rgba[4 * i + 2] = (byte)((i * 31) >> 3);
            rgba[4 * i + 3] = (byte)(i % 7 == 0 ? 90 : 255);
        }
        var path = tmp.File("t.png");
        File.WriteAllBytes(path, TileStore.EncodePng(rgba, w, h));               // filters chosen row by row by the encoder
        Assert.Equal(rgba, ReadAll(path, 64, out _, out _));
        // an opaque picture is written as colour without alpha
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        File.WriteAllBytes(path, TileStore.EncodePng(rgba, w, h));
        Assert.Equal(rgba, ReadAll(path, 256, out _, out _));
    }

    [Fact]
    public void WhatIsNotAReadablePngIsRefusedWithTheReason()
    {
        using var tmp = new TempFolder();
        var text = tmp.File("text.png");
        File.WriteAllText(text, "not a picture at all, only called one");
        Assert.Equal(PngException.NotPng, Assert.Throws<PngException>(() => PngRows.Open(text)).Code);
        Assert.Equal(PngException.NotPng, Assert.Throws<PngException>(() => PngRows.Size(text)).Code);

        byte[] Row(int y) => Samples(y, 64 * 4, 3);
        var interlaced = tmp.File("interlaced.png");
        TestPng.Write(interlaced, 64, 64, 6, 8, Row, interlaced: true);
        Assert.Equal(PngException.Interlaced, Assert.Throws<PngException>(() => PngRows.Open(interlaced)).Code);
        Assert.Equal(PngException.Interlaced, Assert.Throws<PngException>(() => PngRows.Size(interlaced)).Code);

        // a file cut short: the header is fine, the rows end early
        var whole = tmp.File("whole.png");
        TestPng.Write(whole, 64, 64, 6, 8, Row, filter: y => y % 5);
        var bytes = File.ReadAllBytes(whole);
        var cut = tmp.File("cut.png");
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);
        Assert.Equal((64, 64), PngRows.Size(cut));
        using (var png = PngRows.Open(cut))
        {
            var all = new byte[64 * 64 * 4];
            var ex = Assert.Throws<PngException>(() => png.Read(all));
            Assert.Equal(PngException.NotPng, ex.Code);
            Assert.InRange(png.RowsLeft, 1, 63);                                   // the rows before the cut were read
        }

        // a palette picture without its palette
        var noPalette = tmp.File("no-palette.png");
        TestPng.Write(noPalette, 8, 8, 3, 8, y => Samples(y, 8, 0));
        Assert.Equal(PngException.NotPng, Assert.Throws<PngException>(() => PngRows.Open(noPalette)).Code);
        Assert.Throws<FileNotFoundException>(() => PngRows.Open(tmp.File("missing.png")));
    }

    [Fact]
    public void TheFileCannotBeWrittenWhileItsRowsAreRead()
    {
        using var tmp = new TempFolder();
        var path = tmp.File("p.png");
        TestPng.Write(path, 8, 8, 6, 8, y => Samples(y, 32, 1));
        using (PngRows.Open(path))
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1, 2, 3]));
            using var again = PngRows.Open(path);                                   // read by another reader meanwhile: fine
        }
        File.WriteAllBytes(path, [1, 2, 3]);
    }
}
