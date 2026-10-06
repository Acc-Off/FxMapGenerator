using System.Runtime.InteropServices;
using SkiaSharp;

namespace FxMapGenerator.Core.Imaging;

/// <summary>PNG in / out as straight-alpha RGBA bytes (rows top-down), with SkiaSharp.</summary>
public static class Images
{
    /// <summary>Version of the native Skia library; reading it loads the library, so it doubles as a load check.</summary>
    public static string NativeVersion => SkiaSharpVersion.Native.ToString();

    /// <summary>A PNG (or another encoded picture) in memory as straight RGBA.</summary>
    public static byte[] DecodeRgba(byte[] encoded, out int w, out int h)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("cannot decode the picture");
        return Pixels(codec, "the picture", out w, out h);
    }

    public static byte[] LoadRgba(string path, out int w, out int h)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("cannot decode " + path);
        return Pixels(codec, path, out w, out h);
    }

    static byte[] Pixels(SKCodec codec, string path, out int w, out int h)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bytes = new byte[info.BytesSize];
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var r = codec.GetPixels(info, pin.AddrOfPinnedObject());
            if (r != SKCodecResult.Success && r != SKCodecResult.IncompleteInput) throw new InvalidDataException($"{path}: {r}");
        }
        finally { pin.Free(); }
        w = info.Width; h = info.Height;
        return bytes;
    }

    public static void SavePng(string path, byte[] rgba, int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var pm = new SKPixmap(info, pin.AddrOfPinnedObject(), info.RowBytes);
            using var data = pm.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("png encode failed");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var fs = File.Create(path);
            data.SaveTo(fs);
        }
        finally { pin.Free(); }
    }

    /// <summary>Straight RGBA made <paramref name="ow"/> x <paramref name="oh"/> (a smaller picture: averaged with mipmaps).</summary>
    public static byte[] Resize(byte[] rgba, int w, int h, int ow, int oh)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var src = new SKPixmap(info, pin.AddrOfPinnedObject(), info.RowBytes);
            using var image = SKImage.FromPixels(src);
            var outInfo = new SKImageInfo(ow, oh, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var o = new byte[ow * oh * 4];
            var outPin = GCHandle.Alloc(o, GCHandleType.Pinned);
            try
            {
                using var dst = new SKPixmap(outInfo, outPin.AddrOfPinnedObject(), outInfo.RowBytes);
                if (!image.ScalePixels(dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))) throw new InvalidOperationException("resize failed");
            }
            finally { outPin.Free(); }
            return o;
        }
        finally { pin.Free(); }
    }

    /// <summary>Straight RGBA as PNG bytes (<paramref name="quality"/>: Skia's zlib level, 0 fastest to 100 smallest).</summary>
    public static byte[] EncodePng(byte[] rgba, int w, int h, int quality = 100)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var pm = new SKPixmap(info, pin.AddrOfPinnedObject(), info.RowBytes);
            using var data = pm.Encode(SKEncodedImageFormat.Png, quality) ?? throw new InvalidOperationException("png encode failed");
            return data.ToArray();
        }
        finally { pin.Free(); }
    }

    /// <summary>Draws on a premultiplied canvas and returns straight-alpha RGBA.</summary>
    public static byte[] Draw(int w, int h, Action<SKCanvas> paint)
    {
        using var surf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        paint(surf.Canvas);
        using var img = surf.Snapshot();
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bytes = new byte[info.BytesSize];
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { img.ReadPixels(info, pin.AddrOfPinnedObject(), info.RowBytes, 0, 0); }
        finally { pin.Free(); }
        return bytes;
    }
}
