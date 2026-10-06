using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.Core.Minimap;

/// <summary>
/// The minimap's small whole map (<c>minimap_lod_128</c>): the game lays it under the radar, where it shows through the
/// transparent parts of the sheets (the DXT1 ones). The game's own is the standard frame squeezed into 128 x 128 px (the
/// same 9000 x 13500 m as the 2 x 3 sheets). This one is the map's zoom 2 tiles of that frame (2 x 3, 4500 m each) put
/// together, shrunk to the square and laid over the open sea's colour, opaque: a map drawn with see-through water shows
/// its open sea there, not the game's colours.
/// </summary>
public static class MinimapLod
{
    /// <summary>The unit of the minimap stage that makes it (<c>&lt;map&gt;@4096/lod</c>).</summary>
    public const string Unit = "lod";
    public const string Texture = "minimap_lod_128";
    public const int Size = 128;
    public const int Zoom = 2;
    const int Cols = MinimapSheets.Cols, Rows = MinimapSheets.Rows;

    /// <summary>The zoom 2 tiles of the standard frame, row by row (a tile is one 4500 m sheet).</summary>
    public static IEnumerable<(int X, int Y)> Tiles =>
        Enumerable.Range(0, Rows).SelectMany(y => Enumerable.Range(0, Cols).Select(x => (x, y)));

    /// <summary>The digest of the tiles it is made of (a missing tile counts as missing).</summary>
    public static string SourceDigest(TileStore tiles) =>
        State.ContentDigest.Of(string.Join(",", Tiles.Select(t => State.ContentDigest.OfFile(tiles.PathOf(Zoom, t.X, t.Y)) ?? "-")));

    /// <summary>
    /// The picture as straight RGBA (128 x 128, opaque): the tiles side by side (a missing one transparent), shrunk
    /// (Lanczos, the two directions by their own scale) and laid over <paramref name="under"/>.
    /// </summary>
    public static byte[] Compose(TileStore tiles, Rgb under)
    {
        const int T = TileStore.TileSize;
        int w = Cols * T, h = Rows * T;
        var canvas = new byte[w * h * 4];
        foreach (var (x, y) in Tiles)
        {
            var t = tiles.Read(Zoom, x, y);
            if (t is null) continue;
            for (int r = 0; r < T; r++) Buffer.BlockCopy(t, r * T * 4, canvas, ((y * T + r) * w + x * T) * 4, T * 4);
        }
        var small = Lanczos.Resize(canvas, w, h, Size, Size);
        for (int i = 0; i < small.Length; i += 4)
        {
            int a = small[i + 3];
            small[i] = (byte)((small[i] * a + under.R * (255 - a) + 127) / 255);
            small[i + 1] = (byte)((small[i + 1] * a + under.G * (255 - a) + 127) / 255);
            small[i + 2] = (byte)((small[i + 2] * a + under.B * (255 - a) + 127) / 255);
            small[i + 3] = 255;
        }
        return small;
    }
}
