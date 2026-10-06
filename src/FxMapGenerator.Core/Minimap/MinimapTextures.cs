using BCnEncoder.Encoder;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;
using FxMapGenerator.GameData.Textures;

namespace FxMapGenerator.Core.Minimap;

/// <summary>
/// The minimap's texture dictionaries made from a map's tiles and written into a folder: a sheet's two
/// (<c>minimap_sea_r_c.ytd</c> as DXT5, <c>minimap_r_c.ytd</c> as DXT1 with 1-bit alpha), the small whole map
/// (<c>minimap_lod_128.ytd</c>) and a cell's texture beyond the standard frame. The minimap step makes them from a
/// project's map (<see cref="MinimapStage"/>), the conversion of an edited picture from the picture's tiles
/// (<see cref="Export.ConvertTexturesStage"/>): the same tiles give the same files.
/// </summary>
public static class MinimapTextures
{
    /// <summary>Pixel rows per compression band (a band is a loop item of the unit).</summary>
    public const int BandRows = 256;

    /// <summary>The colour under the small whole map: black for the satellite map, else the open sea of the map's style.</summary>
    public static Rgb Under(Project project, MapSet map) => map.Kind == MapKind.Satellite ? new Rgb(0, 0, 0) : project.StyleOf(map).OpenSea;

    /// <summary>
    /// A sheet's two dictionaries from the tiles of <see cref="MinimapSheets.Zoom"/> (a block outside
    /// <paramref name="only"/>, when given, stays transparent). <paramref name="report"/> gets the step and the share done.
    /// </summary>
    public static void WriteSheet(string dir, TileStore tiles, SheetId sheet, IReadOnlySet<BlockId>? only, IParallelRunner parallel, Action<string, double> report)
    {
        report("compose", 0);
        var rgba = MinimapSheets.Compose(tiles, sheet, parallel, only);
        parallel.Token.ThrowIfCancellationRequested();
        report("dxt5", 0.1);
        var dxt5 = Compress(rgba, YtdFile.Kind.Dxt5, parallel);
        report("dxt1", 0.6);
        var dxt1 = Compress(Opaque(rgba), YtdFile.Kind.Dxt1a, parallel);
        report("write", 0.95);
        const int size = MinimapSheets.Size;
        Write(Path.Combine(dir, sheet.SeaTexture + ".ytd"), new YtdFile.Tex(sheet.SeaTexture, YtdFile.Kind.Dxt5, size, size, dxt5));
        Write(Path.Combine(dir, sheet.Texture + ".ytd"), new YtdFile.Tex(sheet.Texture, YtdFile.Kind.Dxt1a, size, size, dxt1));
    }

    /// <summary>The small whole map from the tiles of zoom 2 (<see cref="MinimapLod"/>), over <paramref name="under"/>.</summary>
    public static void WriteLod(string dir, TileStore tiles, Rgb under, Action<string, double> report)
    {
        report("compose", 0);
        var rgba = MinimapLod.Compose(tiles, under);
        report("dxt5", 0.5);
        var data = YtdFile.Compress(rgba, MinimapLod.Size, MinimapLod.Size, YtdFile.Kind.Dxt5, CompressionQuality.Balanced, 1, out _);
        Write(Path.Combine(dir, MinimapLod.Texture + ".ytd"), new YtdFile.Tex(MinimapLod.Texture, YtdFile.Kind.Dxt5, MinimapLod.Size, MinimapLod.Size, data));
    }

    /// <summary>A cell's texture beyond the standard frame (<see cref="MinimapExtraTiles"/>), DXT5 with one level.</summary>
    public static void WriteExtra(string dir, TileStore tiles, CellId cell, IReadOnlySet<BlockId>? only, IParallelRunner parallel, Action<string, double> report)
    {
        report("compose", 0);
        var rgba = MinimapExtraTiles.Compose(tiles, cell, parallel, only);
        report("dxt5", 0.2);
        var data = Compress(rgba, YtdFile.Kind.Dxt5, parallel);
        var name = MinimapExtraTiles.Texture(cell);
        Write(Path.Combine(dir, name + ".ytd"), new YtdFile.Tex(name, YtdFile.Kind.Dxt5, MinimapExtraTiles.Size, MinimapExtraTiles.Size, data));
    }

    /// <summary>
    /// The picture for the DXT1 texture: every pixel that is not fully transparent made opaque (its colour kept), so the
    /// radar draws see-through water opaque and shows the small whole map only where the map is transparent.
    /// </summary>
    static byte[] Opaque(byte[] rgba)
    {
        var o = (byte[])rgba.Clone();
        for (int i = 3; i < o.Length; i += 4) if (o[i] != 0) o[i] = 255;
        return o;
    }

    /// <summary>The block data of the whole picture, band by band on the free workers (the bands one after the other are the picture).</summary>
    static byte[] Compress(byte[] rgba, YtdFile.Kind kind, IParallelRunner parallel)
    {
        int size = (int)Math.Sqrt(rgba.Length / 4);
        int bands = size / BandRows;
        var parts = new byte[bands][];
        parallel.ForEach(Enumerable.Range(0, bands).ToList(), b =>
        {
            parallel.Token.ThrowIfCancellationRequested();
            parts[b] = YtdFile.CompressRows(rgba, size, b * BandRows, BandRows, kind, CompressionQuality.Balanced);
        });
        var data = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, data, at, p.Length);
            at += p.Length;
        }
        return data;
    }

    /// <summary>Through a temporary file, so a stopped run never leaves half a dictionary under the real name.</summary>
    static void Write(string path, YtdFile.Tex tex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        YtdFile.Write(tmp, [tex]);
        File.Move(tmp, path, overwrite: true);
    }
}
