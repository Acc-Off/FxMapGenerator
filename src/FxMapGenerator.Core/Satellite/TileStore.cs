using System.Runtime.InteropServices;
using FxMapGenerator.Core.Imaging;
using SkiaSharp;

namespace FxMapGenerator.Core.Satellite;

/// <summary>
/// A web tile set on disk: <c>&lt;root&gt;/{z}/{x}/{y}.png</c>, 256 px, the same grid as the postal-code map tiles.
/// Tiles without transparency are written as RGB PNGs, the others as RGBA. Writes go through a temporary file; a tile
/// written again with the same bytes is left as it is. With <paramref name="before"/>, a tile written over with other
/// bytes leaves its earlier bytes there first (<c>&lt;before&gt;/{z}/{x}/{y}.png</c>, only the first time: the map as it was
/// before, for comparing; the export removes them).
/// </summary>
public sealed class TileStore(string root, string? before = null)
{
    public const int TileSize = 256;
    public string Root { get; } = root;

    /// <summary>Where tiles written over keep their earlier bytes, or null.</summary>
    public string? Before { get; } = before;

    /// <summary>The tiles of a map of a work folder, keeping the earlier bytes of tiles written over (<see cref="WorkFolder.Before"/>).</summary>
    public static TileStore Keeping(State.WorkFolder folder, string set) => new(folder.Tiles(set), folder.Before(set));

    public string PathOf(int z, int x, int y) => Path.Combine(Root, z.ToString(), x.ToString(), y + ".png");

    public bool Exists(int z, int x, int y) => File.Exists(PathOf(z, x, y));

    /// <summary>Resamples a square block image to 4 x 4 tiles and writes them at (tx..tx+3, ty..ty+3).</summary>
    public void WriteBlock(int z, int tx, int ty, byte[] square, int size)
    {
        const int blockPx = 4 * TileSize;
        var block = size == blockPx ? square : Lanczos.Resize(square, size, size, blockPx, blockPx);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
                Write(z, tx + i, ty + j, Crop(block, blockPx, i * TileSize, j * TileSize, TileSize, TileSize));
    }

    public void Write(int z, int x, int y, byte[] rgba) => WriteBytes(z, x, y, EncodePng(rgba, TileSize, TileSize));

    public void WriteBytes(int z, int x, int y, byte[] png)
    {
        var path = PathOf(z, x, y);
        if (File.Exists(path))
        {
            var old = File.ReadAllBytes(path);
            if (old.AsSpan().SequenceEqual(png)) return;                // the same tile: the file and its time stay
            if (Before is not null)
            {
                var keep = Path.Combine(Before, z.ToString(), x.ToString(), y + ".png");
                if (!File.Exists(keep)) WriteFile(keep, old);
            }
        }
        WriteFile(path, png);
    }

    static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The tile as straight RGBA, or null when it does not exist.</summary>
    public byte[]? Read(int z, int x, int y)
    {
        var path = PathOf(z, x, y);
        if (!File.Exists(path)) return null;
        var rgba = Images.LoadRgba(path, out var w, out var h);
        if (w != TileSize || h != TileSize) throw new InvalidDataException($"{path} is {w}x{h}, not a tile");
        return rgba;
    }

    /// <summary>
    /// One parent tile from its four children at z + 1 (a missing child is transparent): 512 px, resampled to 256.
    /// Returns false when none of the children exists.
    /// </summary>
    public bool BuildParent(int z, int x, int y)
    {
        // four children that are one and the same flat tile (open sea): the parent is that tile (Lanczos keeps a flat
        // picture flat), written without decoding
        if (SameFlatChildren(z + 1, 2 * x, 2 * y) is { } flat)
        {
            WriteBytes(z, x, y, flat);
            return true;
        }
        var canvas = new byte[2 * TileSize * 2 * TileSize * 4];
        bool any = false;
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                var child = Read(z + 1, 2 * x + dx, 2 * y + dy);
                if (child is null) continue;
                any = true;
                Paste(canvas, 2 * TileSize, child, TileSize, dx * TileSize, dy * TileSize);
            }
        if (!any) return false;
        Write(z, x, y, Lanczos.Resize(canvas, 2 * TileSize, 2 * TileSize, TileSize, TileSize));
        return true;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _flat = new();

    /// <summary>The PNG bytes of the four tiles at (x0.., y0..) of zoom z when all four are the same bytes of one opaque colour; else null.</summary>
    byte[]? SameFlatChildren(int z, int x0, int y0)
    {
        byte[]? first = null;
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                var path = PathOf(z, x0 + dx, y0 + dy);
                if (!File.Exists(path)) return null;
                var fi = new FileInfo(path);
                if (fi.Length > 4096) return null;                      // a flat tile compresses to a few hundred bytes
                var b = File.ReadAllBytes(path);
                if (first is null) first = b;
                else if (!b.AsSpan().SequenceEqual(first)) return null;
            }
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first!));
        bool flat = _flat.GetOrAdd(key, _ =>
        {
            var rgba = Images.DecodeRgba(first!, out var w, out var h);
            if (w != TileSize || h != TileSize) return false;
            for (int i = 4; i < rgba.Length; i += 4)
                if (rgba[i] != rgba[0] || rgba[i + 1] != rgba[1] || rgba[i + 2] != rgba[2] || rgba[i + 3] != rgba[3]) return false;
            return rgba[3] == 255;
        });
        return flat ? first : null;
    }

    public static byte[] Crop(byte[] rgba, int width, int x0, int y0, int w, int h)
    {
        var o = new byte[w * h * 4];
        for (int r = 0; r < h; r++) Buffer.BlockCopy(rgba, ((y0 + r) * width + x0) * 4, o, r * w * 4, w * 4);
        return o;
    }

    static void Paste(byte[] dst, int dstWidth, byte[] src, int srcWidth, int x0, int y0)
    {
        int rows = src.Length / 4 / srcWidth;
        for (int r = 0; r < rows; r++) Buffer.BlockCopy(src, r * srcWidth * 4, dst, ((y0 + r) * dstWidth + x0) * 4, srcWidth * 4);
    }

    /// <summary>PNG of straight RGBA; RGB when every pixel is opaque.</summary>
    public static byte[] EncodePng(byte[] rgba, int w, int h)
    {
        bool opaque = true;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] != 255) { opaque = false; break; }
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var pm = new SKPixmap(info, pin.AddrOfPinnedObject(), info.RowBytes);
            using var data = pm.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)) ?? throw new InvalidOperationException("png encode failed");
            return data.ToArray();
        }
        finally { pin.Free(); }
    }
}
