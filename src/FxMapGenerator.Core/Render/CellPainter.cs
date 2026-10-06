using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;
using SkiaSharp;

namespace FxMapGenerator.Core.Render;

/// <summary>What one cell's drawing for one map reads.</summary>
public sealed class CellDrawInput
{
    public required CellLayerFileInfo Info { get; init; }
    public required IReadOnlyList<CellLayer> Layers { get; init; }
    public required MapStyle Style { get; init; }
    /// <summary>The hill shading (null: the style has none, or the cell has no shade file).</summary>
    public ShadeLayer? Shade { get; init; }
    /// <summary>The style's ground picture of the cell (straight RGBA, the cell grid's size), or null to paint the ground layers.</summary>
    public (byte[] Rgba, int Width, int Height)? Ground { get; init; }
    public RoadShapesFile.Contents? Roads { get; init; }
    /// <summary>The placed labels near the cell (postal codes, zone names, street names).</summary>
    public IReadOnlyList<PlacedLabel> Labels { get; init; } = [];
    /// <summary>The points of interest this map shows.</summary>
    public IReadOnlyList<ResolvedPoi> Pois { get; init; } = [];
    public string Language { get; init; } = "en";
}

/// <summary>
/// Draws a cell of a map at the z8 scale (1024 px a block, <see cref="Ppm"/>), one block at a time, in the order the
/// maps were first drawn: the background, the ground picture (bilinear) or the ground layers, the hill shading over
/// what is drawn so far, the other layers in their order (filled even-odd, with a 0.6 px outline of the fill colour that
/// hides the seams between neighbours; the canopy as a tint or a pattern; contours as lines), the roads (tunnels,
/// tracks, the ground-level ribbons with their casings, corner patches, rounded junction corners, then
/// the raised runs level by level), and last the labels and points of interest (postal codes, points, zone names,
/// street names glyph by glyph). The paths are made once per cell in the cell picture's pixels; a block is the cell
/// picture shifted by whole blocks, so the blocks put together are the cell drawn at once.
/// </summary>
public sealed class CellPainter : IDisposable
{
    public const int BlockPx = 1024;
    public const double Ppm = BlockPx / WorldGrid.BlockSize;

    readonly CellDrawInput _in;
    readonly double _x0, _y0;
    readonly List<Action<SKCanvas>> _ground = new(), _over = new();
    /// <summary>What each step of <see cref="_ground"/> and <see cref="_over"/> paints (one tag a step, for <see cref="Pick"/>).</summary>
    readonly List<StepTag> _groundTags = new(), _overTags = new();
    /// <summary>
    /// The steps of <see cref="_over"/> (by their place in it) that paint water in a style with see-through water: they
    /// take the place of what is under them, so the layers must know what they leave of it (<see cref="DrawLayers(int, int, int, int)"/>).
    /// </summary>
    readonly HashSet<int> _replacing = new();
    readonly SKImage? _groundImage;
    readonly (double X, double Y) _groundAt;
    readonly Dictionary<(string, bool), SKTypeface> _faces = new();
    readonly List<IDisposable> _owned = new();

    /// <summary>The cell picture's size in blocks (its rect).</summary>
    public int BlocksX { get; }
    public int BlocksY { get; }

    public CellPainter(CellDrawInput input)
    {
        _in = input;
        var r = input.Info.Rect;
        (_x0, _y0) = (r.X0, r.Y0);
        BlocksX = (int)Math.Round((r.X1 - r.X0) / WorldGrid.BlockSize);
        BlocksY = (int)Math.Round((r.Y0 - r.Y1) / WorldGrid.BlockSize);
        var st = input.Style;
        if (input.Ground is { } g)
        {
            var info = new SKImageInfo(g.Width, g.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            _groundImage = SKImage.FromPixelCopy(info, g.Rgba);
            _groundAt = ((input.Info.GridX0 - 0.5 - _x0) * Ppm, (_y0 - (input.Info.GridY0 + 0.5)) * Ppm);
        }
        BuildLayers(st);
        if (input.Roads is { } roads) BuildRoads(roads, st);
    }

    // ---------------------------------------------------------------- coordinates

    float Px(double x) => (float)((x - _x0) * Ppm);
    float Py(double y) => (float)((_y0 - y) * Ppm);

    SKPath Own(SKPath p)
    {
        _owned.Add(p);
        return p;
    }

    /// <summary>A path made with a builder (even-odd or non-zero filling), kept until the painter is disposed.</summary>
    SKPath Build(Action<SKPathBuilder> add, SKPathFillType fill = SKPathFillType.Winding)
    {
        using var b = new SKPathBuilder { FillType = fill };
        add(b);
        return Own(b.Detach());
    }

    SKPaint Paint(SKPaint p)
    {
        _owned.Add(p);
        return p;
    }

    void AddRing(SKPathBuilder path, double[] pts, bool close)
    {
        if (pts.Length < 2) return;
        path.MoveTo(Px(pts[0]), Py(pts[1]));
        for (int i = 2; i + 1 < pts.Length; i += 2) path.LineTo(Px(pts[i]), Py(pts[i + 1]));
        if (close) path.Close();
    }

    static SKColor Col(Rgb c, byte alpha = 255) => new(c.R, c.G, c.B, alpha);

    SKPaint Fill(Rgb c, byte alpha = 255) => Paint(new SKPaint { Color = Col(c, alpha), IsAntialias = true, Style = SKPaintStyle.Fill });

    /// <summary>A stroke; mitred corners are cut beyond 10 times the width, as cairo does (Skia's own limit is 4).</summary>
    SKPaint Stroke(Rgb c, double widthPx, SKStrokeCap cap, SKStrokeJoin join = SKStrokeJoin.Round) =>
        Paint(new SKPaint { Color = Col(c), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)widthPx, StrokeCap = cap, StrokeJoin = join, StrokeMiter = 10 });

    // ---------------------------------------------------------------- layers

    void BuildLayers(MapStyle st)
    {
        string ground = CellLayers.Sets.Ground(st), sea = CellLayers.Sets.Sea(st), buildings = CellLayers.Sets.Buildings(st), canopy = CellLayers.Sets.Canopy(st);
        string? contours = CellLayers.Sets.Contours(st);
        bool shadeAdded = false;
        foreach (var L in _in.Layers)
        {
            if (L.Kind == "ground")
            {
                if (_groundImage is not null || L.Set != ground || !st.Paint.Ground.TryGetValue(L.Paint, out var gc)) continue;
                _ground.Add(FilledLayer(L, gc));
                _groundTags.Add(new StepTag("groundLayer", gc, Paint: L.Paint));
                continue;
            }
            if (!shadeAdded)
            {
                shadeAdded = true;
                _over.Add(null!);                              // the place of the shading: between the ground and the rest
                _overTags.Add(new StepTag("shade"));
            }
            switch (L.Kind)
            {
                case "canopy":
                    if (L.Set != canopy || st.Canopy.Mode == "none") break;
                    _over.Add(CanopyLayer(L, st.Canopy));
                    _overTags.Add(new StepTag("canopy", st.Canopy.Mode == "tint" ? st.Canopy.TintColor : st.Canopy.Mode == "dots" ? st.Canopy.Dots.Color : st.Canopy.Hatch.Color));
                    break;
                case "water":
                    _over.Add(FilledLayer(L, st.Paint.Water, st.Paint.WaterOpacity));
                    _overTags.Add(new StepTag("water", st.Paint.Water));
                    if (st.SeeThroughWater) _replacing.Add(_over.Count - 1);
                    break;
                case "sea":
                    if (L.Set != sea) break;
                    var bandColours = L.Paint == "rock" ? st.Paint.Sea.Rock : st.Paint.Sea.Sand;
                    if (L.Band >= 0 && L.Band < bandColours.Count)
                    {
                        _over.Add(FilledLayer(L, bandColours[L.Band], st.Paint.SeaOpacity[L.Band]));
                        _overTags.Add(new StepTag("sea", bandColours[L.Band], Band: L.Band, Bed: L.Paint == "rock" ? "rock" : "sand"));
                        if (st.SeeThroughWater) _replacing.Add(_over.Count - 1);
                    }
                    break;
                case "building":
                    if (L.Set != buildings || !st.Paint.Buildings.TryGetValue(L.Paint, out var bc)) break;
                    _over.Add(FilledLayer(L, bc));
                    _overTags.Add(new StepTag("building", bc, Paint: L.Paint));
                    break;
                case "contour":
                    if (contours is null || L.Set != contours || L.Lines is not { Count: > 0 } lines) break;
                    var cp = Build(b => { foreach (var ln in lines) AddRing(b, ln.Points, false); });
                    var pen = Stroke(st.Contours!.Color, st.Contours.Width * Ppm, SKStrokeCap.Butt);
                    _over.Add(c => c.DrawPath(cp, pen));
                    _overTags.Add(new StepTag("contour", st.Contours.Color));
                    break;
                case "rail":
                    _over.Add(FilledLayer(L, st.Paint.Rail));
                    _overTags.Add(new StepTag("rail", st.Paint.Rail));
                    break;
            }
        }
        if (!shadeAdded)
        {
            _over.Insert(0, null!);
            _overTags.Insert(0, new StepTag("shade"));
        }
    }

    SKPath RingsPath(CellLayer L) => Build(b => { foreach (var ring in L.Rings ?? []) AddRing(b, ring.Points, true); }, SKPathFillType.EvenOdd);

    /// <summary>
    /// A layer filled with one colour. With an <paramref name="opacity"/> under 1 (see-through water) the layer takes the
    /// place of what is under it (background, ground), so the drawing keeps that opacity there; what is drawn later over
    /// it (buildings, roads, labels) covers it as usual.
    /// </summary>
    Action<SKCanvas> FilledLayer(CellLayer L, Rgb colour, double opacity = 1)
    {
        if (L.Rings is not { Count: > 0 }) return _ => { };
        var path = RingsPath(L);
        var fill = Fill(colour);
        var hair = Stroke(colour, 0.6, SKStrokeCap.Butt);
        if (opacity < 1)
        {
            byte alpha = (byte)Math.Round(opacity * 255);
            foreach (var p in new[] { fill, hair })
            {
                p.Color = Col(colour, alpha);
                p.BlendMode = SKBlendMode.Src;
            }
        }
        return c =>
        {
            c.DrawPath(path, fill);
            c.DrawPath(path, hair);
        };
    }

    Action<SKCanvas> CanopyLayer(CellLayer L, CanopyStyle cs)
    {
        if (L.Rings is not { Count: > 0 }) return _ => { };
        var path = RingsPath(L);
        if (cs.Mode == "tint")
        {
            var tint = Fill(cs.TintColor, (byte)Math.Round(cs.TintAlpha * 255));
            return c => c.DrawPath(path, tint);
        }
        // dots / hatch: a repeating tile of spacing metres in the drawing's pixels
        var p = cs.Mode == "dots" ? cs.Dots : cs.Hatch;
        int sp = Math.Max(2, (int)Math.Round(p.Spacing * Ppm));
        double size = p.Size * Ppm;
        using var tile = new SKBitmap(sp, sp, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var tc = new SKCanvas(tile))
        {
            tc.Clear(SKColors.Transparent);
            using var tp = new SKPaint { Color = Col(p.Color), IsAntialias = true };
            if (cs.Mode == "dots")
            {
                tc.DrawCircle(sp / 4f, sp / 4f, (float)(size / 2), tp);
                tc.DrawCircle(3 * sp / 4f, 3 * sp / 4f, (float)(size / 2), tp);
            }
            else
            {
                tp.Style = SKPaintStyle.Stroke;
                tp.StrokeWidth = (float)size;
                for (int k = -1; k <= 1; k++) tc.DrawLine(k * sp, sp, sp + k * sp, 0, tp);
            }
        }
        var shader = SKShader.CreateImage(SKImage.FromBitmap(tile), SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        var paint = Paint(new SKPaint { Shader = shader, IsAntialias = true });
        return c => c.DrawPath(path, paint);
    }

    // ---------------------------------------------------------------- roads

    /// <summary>The roads of the cell, drawn as <see cref="RoadDrawing"/> draws them.</summary>
    void BuildRoads(RoadShapesFile.Contents roads, MapStyle st) =>
        new RoadDrawing(_x0, _y0, Ppm, _owned).Add(_over, CellInputs.RoadsOf(roads, _in.Info.Rect), st, _overTags);

    // ---------------------------------------------------------------- labels

    SKTypeface Face(string family, bool bold)
    {
        lock (_faces)
        {
            if (!_faces.TryGetValue((family, bold), out var tf))
                _faces[(family, bold)] = tf = TypefaceOf(family, bold);
            return tf;
        }
    }

    /// <summary>The typeface a label is drawn with: the family's bold or regular face; the default typeface when the family is not installed.</summary>
    internal static SKTypeface TypefaceOf(string family, bool bold) =>
        SKTypeface.FromFamilyName(family, bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;

    /// <summary>The font of a text of <paramref name="em"/> metres in the drawing's pixels.</summary>
    internal static SKFont FontOf(SKTypeface face, double em) => new(face, (float)(em * Ppm)) { Subpixel = true, Edging = SKFontEdging.Antialias };

    /// <summary>
    /// How a text is set, in the drawing's pixels: its width (with <paramref name="spacing"/> between the glyphs, their
    /// advances and the spacing between them; else the advance of the whole text), the box of its ink, and where its
    /// baseline lies under the middle of the ink's height. A text is drawn with the middle of its width and of its ink's
    /// height at its point; the layered files written as text place it by the same numbers (<see cref="Export.SvgFile"/>).
    /// </summary>
    internal static (double Width, SKRect Ink, double BaseY) SetText(SKFont font, string text, double spacing, IReadOnlyList<string> glyphs)
    {
        double w = spacing != 0 ? glyphs.Sum(g => (double)font.MeasureText(g)) + spacing * (glyphs.Count - 1) : font.MeasureText(text);
        font.MeasureText(text, out var ext);
        return (w, ext, -(ext.Top + ext.Height / 2.0));
    }

    void DrawText(SKCanvas c, double x, double y, double rot, string text, string family, bool bold, double em, double spacing, Rgb colour,
        Rgb? halo, double haloM, IReadOnlyList<LabelLines.Glyph>? chars, (Rgb Fill, Rgb? Stroke, double StrokeM, double Pad, bool Circle)? box = null)
    {
        using var font = FontOf(Face(family, bold), em);
        double sp = spacing * Ppm;
        var glyphs = ITextMetrics.Glyphs(text);
        var (w, ext, baseY) = SetText(font, text, sp, glyphs);
        using var ink = new SKPaint { Color = Col(colour), IsAntialias = true };
        using var haloPen = halo is { } h ? new SKPaint
        {
            Color = Col(h), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(haloM * Ppm),
            StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round,
        } : null;
        void Glyph(string s, double gx, double gy)
        {
            if (haloPen is not null)
            {
                using var path = font.GetTextPath(s, new SKPoint((float)gx, (float)gy));
                c.DrawPath(path, haloPen);
            }
            c.DrawText(s, (float)gx, (float)gy, SKTextAlign.Left, font, ink);
        }
        if (chars is { Count: > 0 })
        {
            foreach (var g in chars)
            {
                c.Save();
                c.Translate(Px(g.X), Py(g.Y));
                c.RotateDegrees((float)-g.Rotation);
                Glyph(g.Char, -g.Advance * Ppm / 2.0, baseY);
                c.Restore();
            }
            return;
        }
        c.Save();
        c.Translate(Px(x), Py(y));
        c.RotateDegrees((float)-rot);
        if (box is { } b)
        {
            double pad = b.Pad * Ppm, bw = w + 2 * pad, bh = ext.Height + 2 * pad;
            using var pb = new SKPathBuilder();
            if (b.Circle) pb.AddCircle(0, 0, (float)(Math.Max(bw, bh) / 2), SKPathDirection.Clockwise);
            else
            {
                double rr = Math.Min(bw, bh) * 0.25;
                pb.AddRoundRect(new SKRect((float)(-bw / 2), (float)(-bh / 2), (float)(bw / 2), (float)(bh / 2)), (float)rr, (float)rr, SKPathDirection.Clockwise);
            }
            using var bp = pb.Detach();
            using var bf = new SKPaint { Color = Col(b.Fill), IsAntialias = true };
            c.DrawPath(bp, bf);
            if (b.Stroke is { } bs)
            {
                using var bpen = new SKPaint { Color = Col(bs), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(b.StrokeM * Ppm), StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
                c.DrawPath(bp, bpen);
            }
        }
        double xx = -w / 2.0;
        if (sp != 0)
            foreach (var g in glyphs)
            {
                Glyph(g, xx, baseY);
                xx += font.MeasureText(g) + sp;
            }
        else if (text.Length > 0) Glyph(text, xx, baseY);
        c.Restore();
    }

    /// <summary>The labels and points of interest as drawing steps, in their drawing order, each with what it paints.</summary>
    IEnumerable<(StepTag Tag, Action<SKCanvas> Draw)> LabelSteps()
    {
        var ls = _in.Style.Labels;
        double minPx = ls?.MinPixels ?? 4.0;
        string fontEn = ls?.Font("en") ?? "Bahnschrift";
        bool Small(double em) => em * Ppm < minPx;
        foreach (var l in _in.Labels.Where(l => l.Kind == "postal"))
            if (ls is not null && !Small(l.Size))
                yield return (new StepTag("postal", ls.PostalColor, Text: l.Text),
                    c => DrawText(c, l.X, l.Y, l.Rotation, l.Text, l.Font, l.Weight == "bold", l.Size, l.Spacing, ls.PostalColor, null, 0, null));
        foreach (var poi in _in.Pois)
        {
            var s = poi.Style;
            if (Small(poi.Size)) continue;
            Rgb? outline = s.Outline is { } o && s.OutlineWidth > 0 ? o : null;
            var (text, lang) = LabelPlacer.PoiLabel(poi, _in.Language);
            var tag = new StepTag("poi", poi.Color, Text: text);
            string family = ls?.Font(lang) ?? fontEn;
            if (s.Look == "dot")
                yield return (tag, c =>
                {
                    DrawText(c, poi.Point.X, poi.Point.Y, 0, "", fontEn, true, poi.Size, 0, poi.Color, null, 0, null, (poi.Color, outline, s.OutlineWidth, poi.Size / 2, true));
                    LabelBeside(c, poi, text, family, outline, minPx);
                });
            else if (s.Look == "icon")
                yield return (tag, c =>
                {
                    DrawIcon(c, poi, outline);
                    LabelBeside(c, poi, text, family, outline, minPx);
                });
            else if (s.Look == "badge")
                yield return (tag, c => DrawText(c, poi.Point.X, poi.Point.Y, 0, text, family, s.Weight == "bold", poi.Size, 0, poi.Color, null, 0, null,
                    (s.BadgeColor ?? poi.Color, null, 0, 0.2 * poi.Size, true)));
            else yield return (tag, c => DrawText(c, poi.Point.X, poi.Point.Y, 0, text, family, s.Weight == "bold", poi.Size, 0, poi.Color, outline, s.OutlineWidth, null));
        }
        if (ls is null) yield break;
        foreach (var l in _in.Labels.Where(l => l.Kind == "zone"))
            if (!Small(l.Size))
                yield return (new StepTag("zone", ls.ZoneColor, Text: l.Text),
                    c => DrawText(c, l.X, l.Y, l.Rotation, l.Text, l.Font, l.Weight == "bold", l.Size, l.Spacing, ls.ZoneColor, ls.ZoneOutline, ls.ZoneOutlineWidth, null));
        foreach (var l in _in.Labels.Where(l => l.Kind == "street"))
            if (!Small(l.Size))
                yield return (new StepTag("street", ls.StreetColor, Text: l.Text),
                    c => DrawText(c, l.X, l.Y, l.Rotation, l.Text, l.Font, l.Weight == "bold", l.Size, l.Spacing, ls.StreetColor, null, 0, l.Chars));
    }

    /// <summary>The MDI icons' paths (24 x 24), parsed once.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SKPath?> IconPaths = new(StringComparer.Ordinal);

    /// <summary>The POI styles' PNG icons by their SHA-256, decoded once.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SKImage?> IconImages = new(StringComparer.Ordinal);

    static SKPath? IconPath(string name) => IconPaths.GetOrAdd(name, n => MdiIcons.PathOf(n) is { } d ? SKPath.ParseSvgPathData(d) : null);

    static SKImage? IconImage(PoiStyle s) =>
        s.ImageSha256 is { } sha && s.ImagePath is { } path ? IconImages.GetOrAdd(sha, _ => SKImage.FromEncodedData(File.ReadAllBytes(path))) : null;

    /// <summary>
    /// An icon: the circle behind it (the style's badge colour, its outline around the circle), then the MDI icon filled
    /// with the point's colour over its outline, or the PNG as it is; the icon's height is the point's size.
    /// </summary>
    void DrawIcon(SKCanvas c, ResolvedPoi poi, Rgb? outline)
    {
        var s = poi.Style;
        float cx = Px(poi.Point.X), cy = Py(poi.Point.Y);
        double sizePx = poi.Size * Ppm;
        if (s.BadgeColor is { } badge)
        {
            float r = (float)(PoiLayout.Mark(poi).W * Ppm / 2);
            using var fill = new SKPaint { Color = Col(badge), IsAntialias = true };
            c.DrawCircle(cx, cy, r, fill);
            if (outline is { } ol)
            {
                using var pen = new SKPaint { Color = Col(ol), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(s.OutlineWidth * Ppm) };
                c.DrawCircle(cx, cy, r, pen);
            }
        }
        if (s.Image is not null)
        {
            if (IconImage(s) is not { } img) return;
            double w = sizePx * s.ImageAspect;
            using var paint = new SKPaint { IsAntialias = true };
            c.DrawImage(img, SKRect.Create((float)(cx - w / 2), (float)(cy - sizePx / 2), (float)w, (float)sizePx),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            return;
        }
        if (IconPath(s.Icon!) is not { } path) return;
        float k = (float)(sizePx / MdiIcons.Box);
        c.Save();
        c.Translate(cx - (float)(sizePx / 2), cy - (float)(sizePx / 2));
        c.Scale(k);
        if (outline is { } halo && s.BadgeColor is null)
        {
            using var pen = new SKPaint
            {
                Color = Col(halo), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(s.OutlineWidth * Ppm / k),
                StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round,
            };
            c.DrawPath(path, pen);
        }
        using (var ink = new SKPaint { Color = Col(poi.Color), IsAntialias = true }) c.DrawPath(path, ink);
        c.Restore();
    }

    /// <summary>
    /// The label beside a dot or icon (the style's <c>showLabel</c>): its left edge east of the mark, its middle at the
    /// point's height, in the point's colour (an icon over a circle: the circle's, the icon being the colour on it).
    /// </summary>
    void LabelBeside(SKCanvas c, ResolvedPoi poi, string text, string family, Rgb? outline, double minPx)
    {
        var s = poi.Style;
        if (!s.ShowLabel || text.Length == 0 || poi.LabelSize * Ppm < minPx) return;
        bool bold = s.Weight == "bold";
        using var font = new SKFont(Face(family, bold), (float)(poi.LabelSize * Ppm)) { Subpixel = true, Edging = SKFontEdging.Antialias };
        double w = font.MeasureText(text) / Ppm;
        var colour = s.Look == "icon" && s.BadgeColor is { } circle ? circle : poi.Color;
        DrawText(c, PoiLayout.LabelLeft(poi) + w / 2, poi.Point.Y, 0, text, family, bold, poi.LabelSize, 0, colour, outline, s.OutlineWidth, null);
    }

    void DrawLabels(SKCanvas c)
    {
        foreach (var (_, draw) in LabelSteps()) draw(c);
    }

    // ---------------------------------------------------------------- blocks

    /// <summary>
    /// The block (<paramref name="bx"/>, <paramref name="by"/>) of the cell picture (0-based in the cell's rect) as
    /// 1024 x 1024 straight RGBA.
    /// </summary>
    public byte[] DrawBlock(int bx, int by) => DrawArea(bx * BlockPx, by * BlockPx, BlockPx, BlockPx);

    /// <summary>
    /// The pixels (<paramref name="x0"/>, <paramref name="y0"/>) to (x0 + <paramref name="w"/>, y0 + <paramref name="h"/>)
    /// of the cell picture (its pixel (0, 0) at the north-west corner of the cell's rect) as straight RGBA; a block is
    /// the area of its 1024 pixels.
    /// </summary>
    public unsafe byte[] DrawArea(int x0, int y0, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(Col(_in.Style.Background));
            if (_groundImage is not null)
            {
                c.Save();
                c.Translate((float)(_groundAt.X - x0), (float)(_groundAt.Y - y0));
                c.Scale((float)Ppm);
                using var p = new SKPaint();
                c.DrawImage(_groundImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), p);
                c.Restore();
            }
            c.Save();
            c.Translate(-x0, -y0);
            foreach (var d in _ground) d(c);
            c.Restore();
            bool shaded = false;
            foreach (var d in _over)
            {
                if (d is null)
                {
                    if (!shaded && _in.Shade is { } shade && _in.Style.Shade is not null)
                    {
                        c.Flush();
                        shade.Apply(new Span<byte>((void*)bmp.GetPixels(), bmp.RowBytes * h), bmp.RowBytes, w, h, x0, y0);
                        bmp.NotifyPixelsChanged();
                    }
                    shaded = true;
                    continue;
                }
                c.Save();
                c.Translate(-x0, -y0);
                d(c);
                c.Restore();
            }
            c.Save();
            c.Translate(-x0, -y0);
            DrawLabels(c);
            c.Restore();
            c.Flush();
        }
        var o = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), o, 0, o.Length);
        if (!_in.Style.SeeThroughWater)
        {
            for (int i = 3; i < o.Length; i += 4) o[i] = 255;             // the background is opaque
            return o;
        }
        // see-through water: straight alpha from the canvas's premultiplied pixels
        for (int i = 0; i < o.Length; i += 4)
        {
            int a = o[i + 3];
            if (a is 0 or 255) continue;
            for (int k = 0; k < 3; k++) o[i + k] = (byte)Math.Min(255, (o[i + k] * 255 + a / 2) / a);
        }
        return o;
    }

    /// <summary>
    /// The area <see cref="DrawArea"/> draws, layer by layer (<see cref="MapLayer"/>, by its number), each as straight
    /// RGBA; null for a layer with nothing in the area. Put over each other bottom first, the layers are the area as
    /// <see cref="DrawArea"/> draws it (to the rounding of 8 bits):
    /// <list type="bullet">
    /// <item>the ground is the background with the ground picture or ground layers, without the hill shading;</item>
    /// <item>the shading is the factor the drawing multiplies the ground by, in two layers: where the factor is under
    ///   1, the dark layer holds the grey of that factor (to be multiplied with the ground); where it is over 1, the
    ///   light layer holds the grey g with 1 / (1 - g) = factor (to be colour-dodged). Both are clear elsewhere, and
    ///   both are meant clipped to the ground (<see cref="MapLayers.Clipped"/>): they hold the factor also where the
    ///   ground is cut;</item>
    /// <item>every other step of the drawing goes to the picture of its layer (<see cref="MapLayers.Of"/>), in the
    ///   drawing's order;</item>
    /// <item>see-through water takes the place of what is under it, so the ground is cut there: the water's steps are
    ///   also drawn on a picture that starts opaque, whose opacity then is the water's and what the water leaves of
    ///   what is under it; the ground's opacity becomes what, under the water layer, gives that. Under opaque water
    ///   the ground stays whole.</item>
    /// </list>
    /// </summary>
    public byte[]?[] DrawLayers(int x0, int y0, int w, int h) => DrawLayers(x0, y0, w, h, out _);

    /// <summary>
    /// <see cref="DrawLayers(int, int, int, int)"/>, and with it the ground layer as the drawing shades it
    /// (<paramref name="shadedGround"/>: the ground layer's opacity, its colours multiplied by the shading as
    /// <see cref="DrawArea"/> multiplies them, the fraction dropped); null when the drawing has no shading. It is what
    /// the ground and the two shading layers put together stand for, without the rounding of the two greys.
    /// </summary>
    public byte[]?[] DrawLayers(int x0, int y0, int w, int h, out byte[]? shadedGround)
    {
        shadedGround = null;
        var o = new byte[]?[MapLayers.All.Count];
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        byte[] Pixels(SKBitmap bmp)
        {
            var px = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), px, 0, px.Length);
            return px;
        }
        // the ground: opaque, so its premultiplied pixels are its straight ones
        using (var bmp = new SKBitmap(info))
        {
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(Col(_in.Style.Background));
                if (_groundImage is not null)
                {
                    c.Save();
                    c.Translate((float)(_groundAt.X - x0), (float)(_groundAt.Y - y0));
                    c.Scale((float)Ppm);
                    using var p = new SKPaint();
                    c.DrawImage(_groundImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), p);
                    c.Restore();
                }
                c.Translate(-x0, -y0);
                foreach (var d in _ground) d(c);
                c.Flush();
            }
            o[(int)MapLayer.Ground] = Pixels(bmp);
        }
        var ground = o[(int)MapLayer.Ground]!;
        // the other steps, each on its layer's picture; water that takes the place of what is under it also on a picture
        // that starts opaque
        var pictures = new (SKBitmap Bitmap, SKCanvas Canvas)?[o.Length];
        (SKBitmap Bitmap, SKCanvas Canvas)? left = null;
        (SKBitmap, SKCanvas) Picture(SKColor start)
        {
            var bmp = new SKBitmap(info);
            var c = new SKCanvas(bmp);
            c.Clear(start);
            c.Translate(-x0, -y0);
            return (bmp, c);
        }
        SKCanvas On(MapLayer layer) => (pictures[(int)layer] ??= Picture(SKColors.Transparent)).Canvas;
        try
        {
            for (int i = 0; i < _over.Count; i++)
            {
                if (_over[i] is null) continue;
                _over[i](On(MapLayers.Of(_overTags[i])!.Value));
                if (_replacing.Contains(i)) _over[i]((left ??= Picture(SKColors.Black)).Canvas);
            }
            foreach (var (tag, draw) in LabelSteps()) draw(On(MapLayers.Of(tag)!.Value));
            for (int k = 0; k < o.Length; k++)
                if (pictures[k] is { } p)
                {
                    p.Canvas.Flush();
                    o[k] = Straight(Pixels(p.Bitmap));
                }
            if (left is { } l)
            {
                l.Canvas.Flush();
                var over = Pixels(l.Bitmap);
                var water = o[(int)MapLayer.Water];
                for (int i = 0; i < ground.Length; i += 4)
                {
                    // the opaque picture's opacity is the water's (a) and what the water leaves of the picture under it
                    int a = water is null ? 0 : water[i + 3], part = Math.Max(0, over[i + 3] - a);
                    if (a == 0 && part == 255) continue;
                    // under the water layer, the ground's opacity g gives the drawing's (1 - a) g = part
                    int g = a == 255 ? 255 : Math.Min(255, (255 * part + (255 - a) / 2) / (255 - a));
                    ground[i + 3] = (byte)g;
                    if (g == 0) ground[i] = ground[i + 1] = ground[i + 2] = 0;
                }
            }
        }
        finally
        {
            foreach (var p in pictures.Append(left))
                if (p is { } q)
                {
                    q.Canvas.Dispose();
                    q.Bitmap.Dispose();
                }
        }
        if (_in.Shade is { } shade && _in.Style.Shade is not null)
        {
            byte[]? dark = null, light = null;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    float f = shade.Factor(x0 + x, y0 + y);
                    if (f < 1)
                    {
                        int v = (int)MathF.Round(255 * MathF.Max(f, 0));
                        if (v == 255) continue;
                        dark ??= new byte[ground.Length];
                        (dark[i], dark[i + 1], dark[i + 2], dark[i + 3]) = ((byte)v, (byte)v, (byte)v, 255);
                    }
                    else if (f > 1)
                    {
                        int v = (int)MathF.Round(255 * (1 - 1 / f));
                        if (v == 0) continue;
                        light ??= new byte[ground.Length];
                        (light[i], light[i + 1], light[i + 2], light[i + 3]) = ((byte)v, (byte)v, (byte)v, 255);
                    }
                }
            o[(int)MapLayer.ShadeDark] = dark;
            o[(int)MapLayer.ShadeLight] = light;
            shadedGround = (byte[])ground.Clone();
            shade.Apply(shadedGround, w * 4, w, h, x0, y0);
        }
        return o;
    }

    /// <summary>Premultiplied pixels made straight, in place; null when every pixel is clear.</summary>
    static byte[]? Straight(byte[] px)
    {
        bool any = false;
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 0) continue;
            any = true;
            if (a == 255) continue;
            for (int k = 0; k < 3; k++) px[i + k] = (byte)Math.Min(255, (px[i + k] * 255 + a / 2) / a);
        }
        return any ? px : null;
    }

    /// <summary>
    /// Where the colour of the cell picture's pixel (<paramref name="px"/>, <paramref name="py"/>) comes from: the pixel as
    /// drawn, and every step of the drawing that paints it, bottom first, with the part of the pixel it covers (0 to 1):
    /// the background, the ground picture (its tag carries the colour it gives the pixel), the ground layers, the shading
    /// (the whole pixel, with the factor it multiplies what is under it by), the other layers, the roads, the labels and
    /// the points of interest.
    /// </summary>
    public (Rgb Color, IReadOnlyList<(StepTag Tag, double Cover, double Factor)> Steps) Pick(int px, int py)
    {
        var drawn = DrawArea(px, py, 1, 1);
        var steps = new List<(StepTag, double, double)> { (new StepTag("background", _in.Style.Background), 1, 1) };
        using var bmp = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        SKColor One(Action<SKCanvas> draw)
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Save();
            draw(canvas);
            canvas.Restore();
            canvas.Flush();
            return bmp.GetPixel(0, 0);
        }
        void Step(StepTag tag, Action<SKCanvas> draw)
        {
            var p = One(c =>
            {
                c.Translate(-px, -py);
                draw(c);
            });
            if (p.Alpha > 0) steps.Add((tag, p.Alpha / 255.0, 1));
        }
        if (_groundImage is not null)
        {
            var p = One(c =>
            {
                c.Translate((float)(_groundAt.X - px), (float)(_groundAt.Y - py));
                c.Scale((float)Ppm);
                using var paint = new SKPaint();
                c.DrawImage(_groundImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
            });
            if (p.Alpha > 0) steps.Add((new StepTag("ground", new Rgb(p.Red, p.Green, p.Blue)), p.Alpha / 255.0, 1));
        }
        for (int i = 0; i < _ground.Count; i++) Step(_groundTags[i], _ground[i]);
        for (int i = 0; i < _over.Count; i++)
        {
            if (_over[i] is not null)
            {
                Step(_overTags[i], _over[i]);
                continue;
            }
            if (_in.Shade is { } shade && _in.Style.Shade is not null) steps.Add((_overTags[i], 1, shade.Factor(px, py)));
        }
        foreach (var (tag, draw) in LabelSteps()) Step(tag, draw);
        return (new Rgb(drawn[0], drawn[1], drawn[2]), steps);
    }

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _owned.Clear();
        _groundImage?.Dispose();
        foreach (var f in _faces.Values) if (!ReferenceEquals(f, SKTypeface.Default)) f.Dispose();
        _faces.Clear();
    }
}
