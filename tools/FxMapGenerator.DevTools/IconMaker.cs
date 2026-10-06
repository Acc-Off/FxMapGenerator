using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace FxMapGenerator.DevTools;

/// <summary>
/// The executable's icon from the web page's favicon (<c>src/FxMapGenerator.Web/public/favicon.svg</c>): the SVG drawn at
/// the sizes Windows shows (16 to 256 px) and packed as PNG images into one .ico. It draws only what the favicon uses: a
/// square viewBox, <c>rect</c> (x, y, width, height, rx, fill) and <c>path</c> (d, fill, stroke, stroke-width,
/// stroke-linejoin, stroke-linecap), with the SVG defaults (fill black, no stroke, width 1, miter join, butt cap); any
/// other element or attribute is refused, so a changed favicon cannot come out wrong unnoticed.
/// </summary>
static class IconMaker
{
    /// <summary>What Windows asks an icon for (small icons at 100-250 % scale, the large ones, the jumbo view).</summary>
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    static readonly HashSet<string> RectAttributes = ["x", "y", "width", "height", "rx", "ry", "fill"];
    static readonly HashSet<string> PathAttributes = ["d", "fill", "stroke", "stroke-width", "stroke-linejoin", "stroke-linecap"];

    public static void Write(string svgPath, string icoPath)
    {
        var svg = XDocument.Load(svgPath).Root ?? throw new InvalidDataException($"{svgPath} is empty");
        var box = ViewBox(svg);
        var images = Sizes.Select(s => (Size: s, Png: Render(svg, box, s))).ToList();
        using var o = new BinaryWriter(File.Create(icoPath));
        // ICONDIR, then one ICONDIRENTRY per image (width and height 0 mean 256), then the PNG data
        o.Write((ushort)0);
        o.Write((ushort)1);
        o.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            o.Write((byte)(size >= 256 ? 0 : size));
            o.Write((byte)(size >= 256 ? 0 : size));
            o.Write((byte)0);
            o.Write((byte)0);
            o.Write((ushort)1);
            o.Write((ushort)32);
            o.Write(png.Length);
            o.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) o.Write(png);
    }

    static SKRect ViewBox(XElement svg)
    {
        if (svg.Name.LocalName != "svg") throw new InvalidDataException("not an SVG");
        var v = ((string?)svg.Attribute("viewBox") ?? throw new InvalidDataException("the SVG has no viewBox"))
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();
        if (v.Length != 4 || v[2] <= 0 || v[2] != v[3]) throw new InvalidDataException("the viewBox is not a square");
        return SKRect.Create(v[0], v[1], v[2], v[3]);
    }

    /// <summary>The SVG as an RGBA PNG of size x size.</summary>
    static byte[] Render(XElement svg, SKRect box, int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("no surface");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / box.Width);
        canvas.Translate(-box.Left, -box.Top);
        foreach (var e in svg.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "rect":
                {
                    Check(e, RectAttributes);
                    var rect = SKRect.Create(Number(e, "x", 0), Number(e, "y", 0), Number(e, "width", 0), Number(e, "height", 0));
                    float rx = Number(e, "rx", Number(e, "ry", 0)), ry = Number(e, "ry", rx);
                    using var fill = Fill(e);
                    if (fill is not null) canvas.DrawRoundRect(rect, rx, ry, fill);
                    break;
                }
                case "path":
                {
                    Check(e, PathAttributes);
                    using var path = SKPath.ParseSvgPathData((string?)e.Attribute("d") ?? "") ?? throw new InvalidDataException("a path's d cannot be read");
                    using (var fill = Fill(e))
                        if (fill is not null) canvas.DrawPath(path, fill);
                    using (var stroke = Stroke(e))
                        if (stroke is not null) canvas.DrawPath(path, stroke);
                    break;
                }
                default:
                    throw new InvalidDataException($"<{e.Name.LocalName}> is not drawn by make-icon: draw the favicon with rect and path only, or teach IconMaker");
            }
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("png encode failed");
        return data.ToArray();
    }

    static void Check(XElement e, HashSet<string> known)
    {
        foreach (var a in e.Attributes())
            if (!a.IsNamespaceDeclaration && !known.Contains(a.Name.LocalName))
                throw new InvalidDataException($"<{e.Name.LocalName} {a.Name.LocalName}=...> is not drawn by make-icon");
    }

    static SKPaint? Fill(XElement e)
    {
        var color = (string?)e.Attribute("fill") ?? "#000";
        return color == "none" ? null : new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Color(color) };
    }

    static SKPaint? Stroke(XElement e)
    {
        var color = (string?)e.Attribute("stroke") ?? "none";
        if (color == "none") return null;
        return new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = Color(color),
            StrokeWidth = Number(e, "stroke-width", 1),
            StrokeJoin = (string?)e.Attribute("stroke-linejoin") switch
            {
                null or "miter" => SKStrokeJoin.Miter,
                "round" => SKStrokeJoin.Round,
                "bevel" => SKStrokeJoin.Bevel,
                var j => throw new InvalidDataException($"stroke-linejoin {j}"),
            },
            StrokeCap = (string?)e.Attribute("stroke-linecap") switch
            {
                null or "butt" => SKStrokeCap.Butt,
                "round" => SKStrokeCap.Round,
                "square" => SKStrokeCap.Square,
                var c => throw new InvalidDataException($"stroke-linecap {c}"),
            },
        };
    }

    static SKColor Color(string s) => SKColor.TryParse(s, out var c) ? c : throw new InvalidDataException($"colour {s}");

    static float Number(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    static float Number(XElement e, string name, float fallback) => e.Attribute(name) is { } a ? Number(a.Value) : fallback;
}
