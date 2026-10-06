using FxMapGenerator.Core.Imaging;
using SkiaSharp;

namespace FxMapGenerator.Core.Tests.Imaging;

public sealed class ImagesTests
{
    [Fact]
    public void PngRoundTripKeepsStraightAlphaExactly()
    {
        const int w = 8, h = 4;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[4 * i] = (byte)(i * 7);
            rgba[4 * i + 1] = (byte)(255 - i * 5);
            rgba[4 * i + 2] = (byte)(i * 3 + 20);
            rgba[4 * i + 3] = (byte)(i % 3 == 0 ? 255 : i % 3 == 1 ? 128 : 1);
        }
        var path = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N") + ".png");
        try
        {
            Images.SavePng(path, rgba, w, h);
            var back = Images.LoadRgba(path, out var bw, out var bh);
            Assert.Equal((w, h), (bw, bh));
            Assert.Equal(rgba, back);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DrawReturnsStraightAlpha()
    {
        var px = Images.Draw(4, 4, c =>
        {
            using var paint = new SKPaint { Color = new SKColor(200, 100, 50, 128) };
            c.DrawRect(0, 0, 2, 4, paint);
        });
        // left half: the colour at half alpha (not premultiplied), right half: transparent
        Assert.InRange(px[0], 199, 201);
        Assert.InRange(px[1], 99, 101);
        Assert.InRange(px[2], 49, 51);
        Assert.Equal(128, px[3]);
        Assert.Equal(0, px[4 * 3 + 3]);
    }

    [Fact]
    public void NativeLibraryLoads()
    {
        Assert.Matches(@"^\d+\.\d+", Images.NativeVersion);
    }
}
