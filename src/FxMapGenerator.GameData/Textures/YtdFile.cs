using System.Diagnostics;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using RageLib.GTA5.ResourceWrappers.PC.Textures;
using RageLib.Resources.Common;
using RageLib.Resources.GTA5;
using RageLib.Resources.GTA5.PC.Textures;
using RageLib.ResourceWrappers;

namespace FxMapGenerator.GameData.Textures;

/// <summary>
/// Texture dictionaries (.ytd): block compression with BCnEncoder.NET, the dictionary written as an RSC7 resource with
/// gta-toolkit. The minimap sheets use DXT5 for minimap_sea_r_c and DXT1 with 1-bit alpha for minimap_r_c, one mip level.
/// </summary>
public static class YtdFile
{
    public enum Kind { Dxt1a, Dxt5 }

    public sealed record Tex(string Name, Kind Kind, int Width, int Height, byte[] Data);

    public static uint FourCc(Kind k) => k == Kind.Dxt5 ? (uint)TextureFormat.D3DFMT_DXT5 : (uint)TextureFormat.D3DFMT_DXT1;

    /// <summary>Bytes per pixel row as the texture header counts it: DXT1 w / 2, DXT5 w.</summary>
    public static int Stride(Kind k, int w) => k == Kind.Dxt5 ? w : w / 2;

    static CompressionFormat Bc(Kind k) => k == Kind.Dxt5 ? CompressionFormat.Bc3 : CompressionFormat.Bc1WithAlpha;

    /// <summary>Version of the block compressor, for diagnostics.</summary>
    public static string EncoderVersion => typeof(BcEncoder).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>RGBA (straight alpha, rows top-down) -> one mip level of block data. <paramref name="threads"/> 0 = all cores.</summary>
    public static byte[] Compress(byte[] rgba, int w, int h, Kind k, CompressionQuality quality, int threads, out long ms)
    {
        if (w % 4 != 0 || h % 4 != 0) throw new ArgumentException($"texture size must be a multiple of 4 (got {w}x{h})");
        var enc = new BcEncoder();
        enc.OutputOptions.GenerateMipMaps = false;
        enc.OutputOptions.Quality = quality;
        enc.OutputOptions.Format = Bc(k);
        enc.Options.IsParallel = threads != 1;
        if (threads > 1) enc.Options.TaskCount = threads;
        var sw = Stopwatch.StartNew();
        var data = enc.EncodeToRawBytes(rgba, w, h, PixelFormat.Rgba32, 0, out _, out _);
        ms = sw.ElapsedMilliseconds;
        return data;
    }

    /// <summary>Bytes of one 4 x 4 block: DXT1 8, DXT5 16.</summary>
    public static int BlockBytes(Kind k) => k == Kind.Dxt5 ? 16 : 8;

    /// <summary>
    /// A band of whole block rows (<paramref name="rows"/> pixel rows from <paramref name="y0"/>, both multiples of 4),
    /// on the calling thread. Blocks are stored row by row and each is encoded on its own, so the bands of a picture put
    /// one after the other are the picture's block data: a big texture can be compressed band by band on several workers.
    /// </summary>
    public static byte[] CompressRows(byte[] rgba, int w, int y0, int rows, Kind k, CompressionQuality quality)
    {
        if (w % 4 != 0 || y0 % 4 != 0 || rows % 4 != 0) throw new ArgumentException($"bands are whole block rows (width {w}, rows {y0}+{rows})");
        var band = new byte[w * rows * 4];
        Buffer.BlockCopy(rgba, y0 * w * 4, band, 0, band.Length);
        return Compress(band, w, rows, k, quality, 1, out _);
    }

    /// <summary>Block data of one level -> straight-alpha RGBA.</summary>
    public static byte[] Decompress(byte[] data, int w, int h, uint fourCc)
    {
        var fmt = fourCc == (uint)TextureFormat.D3DFMT_DXT5 ? CompressionFormat.Bc3 :
                  fourCc == (uint)TextureFormat.D3DFMT_DXT1 ? CompressionFormat.Bc1WithAlpha :
                  throw new NotSupportedException($"texture format {fourCc:X8}");
        var px = new BcDecoder().DecodeRaw(data, w, h, fmt);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++) { rgba[4 * i] = px[i].r; rgba[4 * i + 1] = px[i].g; rgba[4 * i + 2] = px[i].b; rgba[4 * i + 3] = px[i].a; }
        return rgba;
    }

    /// <summary>A new dictionary with these textures (level 0 only), saved as an RSC7 resource (version 13).</summary>
    public static void Write(string path, IEnumerable<Tex> textures)
    {
        var dict = new PgDictionary64<TextureDX11>
        {
            Hashes = new SimpleList64<uint>(),
            Values = new ResourcePointerList64<TextureDX11> { Entries = new ResourcePointerArray64<TextureDX11>() },
        };
        foreach (var t in textures)
        {
            var tex = new TextureDX11
            {
                Width = (ushort)t.Width, Height = (ushort)t.Height, Depth = 1, Stride = (ushort)Stride(t.Kind, t.Width),
                Format = FourCc(t.Kind), Levels = 1, Data = new TextureData_GTA5_pc { FullData = t.Data },
                Name = (string_r)t.Name,
            };
            dict.Values.Entries.Add(tex);
        }
        new TextureDictionaryWrapper_GTA5_pc(dict).UpdateClass();     // sorts the name hashes, fills the fixed header values
        var res = new Resource7<PgDictionary64<TextureDX11>> { ResourceData = dict, Version = 13 };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        res.Save(path);
    }

    /// <summary>The textures of a dictionary file (header and level-0 data as stored).</summary>
    public static List<TextureDX11> Read(string path)
    {
        var res = new Resource7<PgDictionary64<TextureDX11>>();
        using (var fs = File.OpenRead(path)) res.Load(fs);
        var list = new List<TextureDX11>();
        var e = res.ResourceData.Values?.Entries;
        if (e != null) for (int i = 0; i < e.Count; i++) list.Add(e[i]);
        return list;
    }
}
