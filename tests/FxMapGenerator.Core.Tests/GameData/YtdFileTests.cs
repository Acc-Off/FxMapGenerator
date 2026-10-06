using BCnEncoder.Encoder;
using FxMapGenerator.GameData.Textures;

namespace FxMapGenerator.Core.Tests.GameData;

public sealed class YtdFileTests
{
    static byte[] Gradient(int w, int h, Func<int, int, byte> alpha)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = 4 * (y * w + x);
                rgba[i] = (byte)(x * 255 / (w - 1));
                rgba[i + 1] = (byte)(y * 255 / (h - 1));
                rgba[i + 2] = 96;
                rgba[i + 3] = alpha(x, y);
            }
        return rgba;
    }

    static string TempYtd() => Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N") + ".ytd");

    [Fact]
    public void Dxt5DictionaryRoundTrip()
    {
        const int w = 64, h = 32;
        var rgba = Gradient(w, h, (x, _) => (byte)(x * 4));
        var data = YtdFile.Compress(rgba, w, h, YtdFile.Kind.Dxt5, CompressionQuality.Balanced, 1, out _);
        Assert.Equal(w * h, data.Length);   // DXT5: 16 bytes per 4x4 block

        var path = TempYtd();
        try
        {
            YtdFile.Write(path, new[] { new YtdFile.Tex("minimap_sea_0_0", YtdFile.Kind.Dxt5, w, h, data) });
            var textures = YtdFile.Read(path);
            var t = Assert.Single(textures);
            Assert.Equal("minimap_sea_0_0", (string)t.Name!);
            Assert.Equal((w, h, 1), ((int)t.Width, (int)t.Height, (int)t.Levels));
            Assert.Equal(YtdFile.FourCc(YtdFile.Kind.Dxt5), t.Format);
            Assert.Equal(YtdFile.Stride(YtdFile.Kind.Dxt5, w), (int)t.Stride);
            Assert.Equal(data, t.Data!.FullData);

            var back = YtdFile.Decompress(t.Data!.FullData, w, h, t.Format);
            int worst = 0;
            for (int i = 0; i < back.Length; i++) worst = Math.Max(worst, Math.Abs(back[i] - rgba[i]));
            Assert.InRange(worst, 0, 12);   // smooth gradients survive block compression closely
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dxt1KeepsBinaryAlphaAndSeveralTexturesShareOneDictionary()
    {
        const int w = 16, h = 16;
        var rgba = Gradient(w, h, (x, y) => (byte)((x / 4 + y / 4) % 2 == 0 ? 255 : 0));
        var dxt1 = YtdFile.Compress(rgba, w, h, YtdFile.Kind.Dxt1a, CompressionQuality.Balanced, 0, out _);
        var dxt5 = YtdFile.Compress(rgba, w, h, YtdFile.Kind.Dxt5, CompressionQuality.Balanced, 0, out _);
        Assert.Equal(w * h / 2, dxt1.Length);   // DXT1: 8 bytes per block

        var path = TempYtd();
        try
        {
            YtdFile.Write(path, new[]
            {
                new YtdFile.Tex("minimap_1_2", YtdFile.Kind.Dxt1a, w, h, dxt1),
                new YtdFile.Tex("minimap_sea_1_2", YtdFile.Kind.Dxt5, w, h, dxt5),
            });
            var textures = YtdFile.Read(path).ToDictionary(t => (string)t.Name!);
            Assert.Equal(2, textures.Count);
            var back = YtdFile.Decompress(textures["minimap_1_2"].Data!.FullData, w, h, textures["minimap_1_2"].Format);
            for (int i = 0; i < w * h; i++) Assert.Equal(rgba[4 * i + 3], back[4 * i + 3]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(YtdFile.Kind.Dxt5)]
    [InlineData(YtdFile.Kind.Dxt1a)]
    public void BandsPutTogetherAreTheWholePicture(YtdFile.Kind kind)
    {
        const int w = 128, h = 96;
        var rgba = Gradient(w, h, (x, y) => (byte)(((x * 7) ^ (y * 13)) & 0xff));
        var whole = YtdFile.Compress(rgba, w, h, kind, CompressionQuality.Balanced, 1, out _);
        var bands = new[] { (0, 32), (32, 8), (40, 56) }.SelectMany(b => YtdFile.CompressRows(rgba, w, b.Item1, b.Item2, kind, CompressionQuality.Balanced)).ToArray();
        Assert.Equal(whole, bands);
        Assert.Throws<ArgumentException>(() => YtdFile.CompressRows(rgba, w, 2, 8, kind, CompressionQuality.Balanced));
    }

    [Fact]
    public void SizesThatAreNotMultiplesOfFourAreRefused()
    {
        Assert.Throws<ArgumentException>(() => YtdFile.Compress(new byte[6 * 4 * 4], 6, 4, YtdFile.Kind.Dxt5, CompressionQuality.Fast, 1, out _));
    }
}
