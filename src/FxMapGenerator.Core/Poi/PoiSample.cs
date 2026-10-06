using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Poi;

/// <summary>
/// A sample of a POI style for the screen: one point drawn as the cells draw it (<see cref="CellPainter"/>) on a map
/// style's background, at zoom 8 (the drawing's own scale) or made smaller as the lower tiles are (Lanczos, a half at a
/// time).
/// </summary>
public static class PoiSample
{
    /// <summary>The sample as a PNG.</summary>
    /// <param name="zoom">8, 7 or 6.</param>
    /// <param name="label">The point's label per language.</param>
    public static byte[] Png(PoiStyle style, IReadOnlyDictionary<string, string> label, string language, MapStyle map, int zoom, Rgb? color = null, double? size = null)
    {
        if (zoom is < 6 or > 8) throw new ArgumentOutOfRangeException(nameof(zoom));
        // the point in the middle of the map's north-west block
        double x = WorldGrid.Left + WorldGrid.BlockSize / 2, y = WorldGrid.Top - WorldGrid.BlockSize / 2;
        var poi = new ResolvedPoi(new PoiPoint("sample", "", "", label, x, y, null, null, null, null, null, null), style, PoiShow.Default,
            color ?? style.Color, size ?? style.Size, true, false);
        var (text, lang) = LabelPlacer.PoiLabel(poi, language);
        string font = map.Labels?.Font(lang) ?? "Bahnschrift";

        // the part it draws: the mark (or the text, or the badge) and the label beside, with a margin
        double west, east, half;
        using (var metrics = new SkiaTextMetrics())
        {
            if (style.Look is "dot" or "icon")
            {
                var (w, h) = PoiLayout.Space(poi);
                (west, east, half) = (w / 2, w / 2, h / 2);
                if (style.ShowLabel && text.Length > 0)
                {
                    var (nw, nh) = metrics.Size(text, font, style.Weight, poi.LabelSize);
                    east = Math.Max(east, PoiLayout.LabelLeft(poi) - x + nw + style.OutlineWidth);
                    half = Math.Max(half, nh / 2 + style.OutlineWidth);
                }
            }
            else
            {
                var (w, h) = metrics.Size(text.Length > 0 ? text : " ", font, style.Weight, poi.Size);
                if (style.Look == "badge") w = h = Math.Max(w, h) + 0.4 * poi.Size;
                (west, east, half) = (w / 2 + style.OutlineWidth, w / 2 + style.OutlineWidth, h / 2 + style.OutlineWidth);
            }
        }
        double margin = Math.Max(2, 0.25 * poi.Size);
        int scale = 1 << (8 - zoom);
        // whole pixels of the zoom's own, on the drawing's lattice
        int left = (int)Math.Floor((x - west - margin - WorldGrid.Left) * CellPainter.Ppm / scale) * scale;
        int right = (int)Math.Ceiling((x + east + margin - WorldGrid.Left) * CellPainter.Ppm / scale) * scale;
        int top = (int)Math.Floor((WorldGrid.Top - (y + half + margin)) * CellPainter.Ppm / scale) * scale;
        int bottom = (int)Math.Ceiling((WorldGrid.Top - (y - half - margin)) * CellPainter.Ppm / scale) * scale;
        int w8 = Math.Max(scale, right - left), h8 = Math.Max(scale, bottom - top);

        var info = new CellLayerFileInfo("sample", (WorldGrid.Left, WorldGrid.Top, WorldGrid.Left + WorldGrid.BlockSize, WorldGrid.Top - WorldGrid.BlockSize),
            WorldGrid.Left + 0.5, WorldGrid.Top - 0.5, 281, 281, [new BlockId(0, 0)]);
        using var painter = new CellPainter(new CellDrawInput { Info = info, Layers = [], Style = map, Pois = [poi], Language = language });
        var rgba = painter.DrawArea(left, top, w8, h8);
        int cw = w8, ch = h8;
        for (int z = 8; z > zoom; z--)
        {
            rgba = Lanczos.Resize(rgba, cw, ch, cw / 2, ch / 2);
            (cw, ch) = (cw / 2, ch / 2);
        }
        return Images.EncodePng(rgba, cw, ch);
    }
}
