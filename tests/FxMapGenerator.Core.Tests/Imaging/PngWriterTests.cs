using System.IO.Compression;
using FxMapGenerator.Core.Imaging;

namespace FxMapGenerator.Core.Tests.Imaging;

/// <summary>PNG files written row by row (<see cref="PngWriter"/>), read back by <see cref="PngRows"/> and by Skia.</summary>
public sealed class PngWriterTests
{
    /// <summary>The whole picture as <see cref="PngRows"/> reads it.</summary>
    static byte[] ReadAll(string path, out int width, out int height)
    {
        using var png = PngRows.Open(path);
        (width, height) = (png.Width, png.Height);
        var all = new byte[width * height * 4];
        Assert.Equal(height, png.Read(all));
        return all;
    }

    /// <summary>
    /// A picture with what a layer of a map has: clear rows, rows of one colour, rows that repeat the row above, rows
    /// that change from pixel to pixel, and see-through pixels with colours of their own.
    /// </summary>
    static byte[] Picture(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (y < 3) continue;                                            // clear
                if (y < 6) (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (122, 157, 191, 166);     // one colour, see-through
                else if (y % 4 == 0) Buffer.BlockCopy(rgba, i - w * 4, rgba, i, 4);                     // the row above again
                else (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(x * 7 + y), (byte)(y * 5 - x), (byte)((x * y) % 251), (byte)(x % 9 == 0 ? x : 255));
            }
        return rgba;
    }

    [Fact]
    public void APictureWrittenRowByRowIsReadBackByBothReaders()
    {
        using var tmp = new TempFolder();
        const int w = 37, h = 23;
        var rgba = Picture(w, h);
        var path = tmp.File("p.png");
        using (var png = PngWriter.Create(path, w, h))
        {
            Assert.Equal((w, h, h), (png.Width, png.Height, png.RowsLeft));
            png.Write(rgba.AsSpan(0, w * 4));                                   // one row, then five, then the rest
            png.Write(rgba.AsSpan(w * 4, 5 * w * 4));
            Assert.Equal(h - 6, png.RowsLeft);
            png.Write(rgba.AsSpan(6 * w * 4));
            png.Finish();
        }
        Assert.Equal(rgba, ReadAll(path, out int rw, out int rh));
        Assert.Equal((w, h), (rw, rh));
        Assert.Equal(rgba, Images.LoadRgba(path, out int sw, out int sh));
        Assert.Equal((w, h), (sw, sh));
        Assert.Equal((w, h), PngRows.Size(path));
    }

    [Fact]
    public void PictureDataOfAnyLengthIsCutIntoChunks()
    {
        using var tmp = new TempFolder();
        const int w = 256, h = 256;
        // noise does not pack: the data is longer than a chunk several times over
        var noise = new byte[w * h * 4];
        new Random(7).NextBytes(noise);
        var path = tmp.File("noise.png");
        using (var png = PngWriter.Create(path, w, h, CompressionLevel.Fastest))
        {
            for (int y = 0; y < h; y += 16) png.Write(noise.AsSpan(y * w * 4, 16 * w * 4));
        }                                                                       // every row written: disposing ends the file
        Assert.InRange(new FileInfo(path).Length, noise.Length * 9 / 10, noise.Length * 11 / 10);
        Assert.Equal(noise, ReadAll(path, out _, out _));
        Assert.Equal(noise, Images.LoadRgba(path, out _, out _));

        // a picture of one colour, and a clear one: almost nothing is left of them
        var flat = new byte[1024 * 1024 * 4];
        for (int i = 0; i < flat.Length; i += 4) (flat[i], flat[i + 1], flat[i + 2], flat[i + 3]) = (20, 40, 60, 255);
        foreach (var (name, picture) in new[] { ("flat.png", flat), ("clear.png", new byte[flat.Length]) })
        {
            var small = tmp.File(name);
            using (var png = PngWriter.Create(small, 1024, 1024)) png.Write(picture);
            Assert.InRange(new FileInfo(small).Length, 100, 20_000);
            Assert.Equal(picture, ReadAll(small, out _, out _));
        }
    }

    [Fact]
    public void RowsMustBeWholeAndThePictureComplete()
    {
        using var tmp = new TempFolder();
        Assert.Throws<ArgumentException>(() => PngWriter.Create(tmp.File("none.png"), 0, 4));
        var path = tmp.File("p.png");
        using (var png = PngWriter.Create(path, 4, 2))
        {
            Assert.Throws<ArgumentException>(() => png.Write(new byte[15]));    // not a whole row
            Assert.Throws<ArgumentException>(() => png.Write(new byte[48]));    // three rows for a picture of two
            png.Write(new byte[16]);
            Assert.Throws<InvalidOperationException>(png.Finish);              // a row is missing
        }
        // left unfinished, it is not a picture the reader takes
        Assert.Equal(PngException.NotPng, Assert.Throws<PngException>(() =>
        {
            using var read = PngRows.Open(path);
            read.Read(new byte[4 * 2 * 4]);
        }).Code);
        // into a stream that stays open
        using var ms = new MemoryStream();
        using (var png = new PngWriter(ms, 2, 1, leaveOpen: true)) png.Write([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.True(ms.CanWrite);
        var file = tmp.File("stream.png");
        File.WriteAllBytes(file, ms.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ReadAll(file, out _, out _));
    }
}
