using System.Globalization;
using System.Text;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;
using SkiaSharp;

namespace FxMapGenerator.Core.Export;

/// <summary>
/// A picture an SVG file links as one of its layers: the layer's id (letters, digits and <c>_</c>), its name, the file
/// (a path beside the SVG file), where the picture lies in the document, and how it is put over what lies under it.
/// </summary>
public sealed record SvgPicture(string Id, string Label, string Href, int Left, int Top, int Width, int Height, LayerBlend Blend = LayerBlend.Normal);

/// <summary>What an SVG file's drawn layers are made of: the roads, the labels and the points of interest of a map.</summary>
public sealed class SvgDrawing
{
    public required MapStyle Style { get; init; }
    public RoadShapesFile.Contents? Roads { get; init; }
    /// <summary>The placed labels of the map (postal codes, zone names, street names).</summary>
    public IReadOnlyList<PlacedLabel> Labels { get; init; } = [];
    /// <summary>The points of interest the map shows.</summary>
    public IReadOnlyList<ResolvedPoi> Pois { get; init; } = [];
    /// <summary>The map's language: the labels of the points of interest and their font.</summary>
    public string Language { get; init; } = "en";
    /// <summary>The blocks the map is drawn in: a shape or a label is written when it reaches one of them, and then whole.</summary>
    public required IReadOnlySet<BlockId> Range { get; init; }
    /// <summary>The pictures of the POI styles that draw a PNG, by the style's id: the file the SVG file links.</summary>
    public IReadOnlyDictionary<string, string> PoiPictures { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// A map written as an SVG file for editing in a drawing program (Inkscape, Illustrator): the project's whole frame,
/// one unit a pixel of the zoom level (so a PNG written from it at one pixel a unit is a picture the conversion of an
/// edited picture takes). Its layers are those of the layered picture files (<see cref="MapLayer"/>), bottom first:
/// the ones from the ground to the railway as linked pictures (<see cref="SvgPicture"/>; the two shading layers with
/// their blend modes as <c>mix-blend-mode</c>), then what a drawing program can keep as shapes and text:
/// <list type="bullet">
/// <item>the roads as paths in the order <see cref="RoadDrawing"/> draws them, a path a shape, in groups by kind: the
///   tunnels (a path a tunnel, its fill see-through and its edge dashed), the unpaved tracks (a stroked line a run of
///   segments), the casings of the roads and of the highways, the roads, the highways, the junction corners, and the
///   raised runs level by level. Whatever the drawing strokes with the colour of a fill (a corner patch's outline, a
///   junction arc's casing, a corner's seam cut to its region) is written as the outline of that stroke, so every
///   shape of a group is filled with the group's one colour;</item>
/// <item>the postal codes, the zone names and the labels of the points of interest as one text each, placed by the
///   numbers the drawing places them with (<see cref="CellPainter.SetText"/>); a street name as one text with a place
///   and a turn for every character; an outline as the text's stroke under its fill;</item>
/// <item>a point of interest as a group: its circle, its icon (a path, or a linked PNG) and its label.</item>
/// </list>
/// Text is written with kerning and ligatures off, as the drawing sets glyph after glyph by its advance. A layer is a
/// group of the root with Inkscape's layer mark; its id is its English name (what Illustrator shows), its label the
/// name in the language asked for. Shapes and labels are not cut at the range's edge as the tiles are. The file is
/// only written, never read.
/// </summary>
public static class SvgFile
{
    /// <summary>The id of a layer or group from a name in English: its letters and digits, <c>_</c> between its words.</summary>
    public static string Id(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        return sb.ToString().TrimEnd('_');
    }

    /// <summary>
    /// Writes the file: the pictures as layers in their order, then the drawn layers (none with a null
    /// <paramref name="drawing"/>: a map that is one picture). Returns the names of the layers written, bottom first.
    /// </summary>
    /// <param name="zoom">The zoom level a unit is a pixel of (a block is 256 units at zoom 6).</param>
    /// <param name="language">The language of the layers' and groups' names (<c>ja</c>; any other: English).</param>
    public static IReadOnlyList<string> Write(TextWriter to, MapFrame frame, int zoom, string language, IReadOnlyList<SvgPicture> pictures, SvgDrawing? drawing,
        string? comment = null)
    {
        int side = EditableChoice.BlockPx(zoom), width = frame.BlocksX * side, height = frame.BlocksY * side;
        to.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        if (comment is not null) to.Write($"<!-- {comment.Replace("--", "- -")} -->\n");
        to.Write(string.Create(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">\n"));
        var names = new List<string>();
        foreach (var p in pictures)
        {
            string blend = p.Blend switch { LayerBlend.Multiply => " style=\"mix-blend-mode:multiply\"", LayerBlend.ColorDodge => " style=\"mix-blend-mode:color-dodge\"", _ => "" };
            to.Write(string.Create(CultureInfo.InvariantCulture, $"<g id=\"{p.Id}\" inkscape:groupmode=\"layer\" inkscape:label=\"{Attr(p.Label)}\"{blend}>\n<image xlink:href=\"{Attr(p.Href)}\" x=\"{p.Left}\" y=\"{p.Top}\" width=\"{p.Width}\" height=\"{p.Height}\" preserveAspectRatio=\"none\"/>\n</g>\n"));
            names.Add(p.Label);
        }
        if (drawing is not null)
        {
            using var w = new Writer(to, frame, zoom, language, drawing);
            w.Roads(names);
            w.Labels(names);
        }
        to.Write("</svg>\n");
        return names;
    }

    /// <summary>A text as an attribute's value or an element's content.</summary>
    static string Attr(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>A number with two decimals at most.</summary>
    static string N(double v)
    {
        var s = Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>A number of hundredths written as units: <c>-12.05</c>, <c>3</c>, <c>0.5</c>.</summary>
    static void Hundredths(StringBuilder sb, long v)
    {
        if (v < 0)
        {
            sb.Append('-');
            v = -v;
        }
        long whole = v / 100, part = v % 100;
        sb.Append(whole.ToString(CultureInfo.InvariantCulture));
        if (part == 0) return;
        sb.Append('.').Append((char)('0' + part / 10));
        if (part % 10 != 0) sb.Append((char)('0' + part % 10));
    }

    sealed class Writer : IDisposable
    {
        readonly TextWriter _to;
        readonly SvgDrawing _in;
        readonly bool _ja;
        /// <summary>The frame's north-west corner (m), the units a metre, and the units a pixel of the drawing (zoom 8).</summary>
        readonly double _left, _top, _ppm, _k;
        readonly Dictionary<(string, bool), SKTypeface> _faces = new();
        readonly StringBuilder _d = new();

        public Writer(TextWriter to, MapFrame frame, int zoom, string language, SvgDrawing drawing)
        {
            (_to, _in, _ja) = (to, drawing, language == "ja");
            _left = WorldGrid.Left + frame.Bx0 * WorldGrid.BlockSize;
            _top = WorldGrid.Top - frame.By0 * WorldGrid.BlockSize;
            _ppm = EditableChoice.BlockPx(zoom) / WorldGrid.BlockSize;
            _k = _ppm / CellPainter.Ppm;
        }

        public void Dispose()
        {
            foreach (var f in _faces.Values)
                if (!ReferenceEquals(f, SKTypeface.Default)) f.Dispose();
            _faces.Clear();
        }

        // ---------------------------------------------------------------- places

        double X(double x) => (x - _left) * _ppm;
        double Y(double y) => (_top - y) * _ppm;
        long Hx(double x) => (long)Math.Round(X(x) * 100, MidpointRounding.AwayFromZero);
        long Hy(double y) => (long)Math.Round(Y(y) * 100, MidpointRounding.AwayFromZero);

        /// <summary>Whether a rectangle (west, north, east, south, m) reaches a block of the range.</summary>
        bool Reaches(double x0, double y0, double x1, double y1)
        {
            int bxA = (int)Math.Floor((x0 - WorldGrid.Left) / WorldGrid.BlockSize), bxB = (int)Math.Floor((x1 - WorldGrid.Left) / WorldGrid.BlockSize);
            int byA = (int)Math.Floor((WorldGrid.Top - y0) / WorldGrid.BlockSize), byB = (int)Math.Floor((WorldGrid.Top - y1) / WorldGrid.BlockSize);
            for (int by = byA; by <= byB; by++)
                for (int bx = bxA; bx <= bxB; bx++)
                    if (_in.Range.Contains(new BlockId(bx, by))) return true;
            return false;
        }

        /// <summary>Whether the points' box reaches a block of the range.</summary>
        bool Reaches(double[] pts)
        {
            if (pts.Length < 2) return false;
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i + 1 < pts.Length; i += 2)
            {
                minX = Math.Min(minX, pts[i]); maxX = Math.Max(maxX, pts[i]);
                minY = Math.Min(minY, pts[i + 1]); maxY = Math.Max(maxY, pts[i + 1]);
            }
            return Reaches(minX, maxY, maxX, minY);
        }

        // ---------------------------------------------------------------- path data

        /// <summary>
        /// Adds a ring or a line to the path data: its first point, then the steps from point to point (a point that
        /// falls on the one before it is left out).
        /// </summary>
        void Ring(double[] pts, bool close)
        {
            if (pts.Length < 4) return;
            long px = Hx(pts[0]), py = Hy(pts[1]);
            _d.Append('M');
            Hundredths(_d, px);
            _d.Append(',');
            Hundredths(_d, py);
            bool first = true;
            for (int i = 2; i + 1 < pts.Length; i += 2)
            {
                long x = Hx(pts[i]), y = Hy(pts[i + 1]);
                if (x == px && y == py) continue;
                _d.Append(first ? 'l' : ' ');
                first = false;
                Hundredths(_d, x - px);
                _d.Append(',');
                Hundredths(_d, y - py);
                (px, py) = (x, y);
            }
            if (close) _d.Append('z');
        }

        /// <summary>A ring or a line as a Skia path in units around (<paramref name="ox"/>, <paramref name="oy"/>, hundredths), where a float keeps the hundredths.</summary>
        SKPath Local(double[] pts, bool close, long ox, long oy)
        {
            using var b = new SKPathBuilder();
            for (int i = 0; i + 1 < pts.Length; i += 2)
            {
                float x = (Hx(pts[i]) - ox) / 100f, y = (Hy(pts[i + 1]) - oy) / 100f;
                if (i == 0) b.MoveTo(x, y);
                else b.LineTo(x, y);
            }
            if (close) b.Close();
            return b.Detach();
        }

        /// <summary>
        /// Adds a Skia path (units around the origin, see <see cref="Local"/>) to the path data, its curves as short
        /// lines (the rounded joins of a stroke's outline).
        /// </summary>
        void Outline(SKPath path, long ox, long oy)
        {
            using var it = path.CreateRawIterator();
            Span<SKPoint> p = stackalloc SKPoint[4];
            long px = 0, py = 0;
            bool first = true;
            void Line(double x, double y)
            {
                long hx = ox + (long)Math.Round(x * 100, MidpointRounding.AwayFromZero), hy = oy + (long)Math.Round(y * 100, MidpointRounding.AwayFromZero);
                if (hx == px && hy == py) return;
                _d.Append(first ? 'l' : ' ');
                first = false;
                Hundredths(_d, hx - px);
                _d.Append(',');
                Hundredths(_d, hy - py);
                (px, py) = (hx, hy);
            }
            while (true)
            {
                var verb = it.Next(p);
                switch (verb)
                {
                    case SKPathVerb.Move:
                        px = ox + (long)Math.Round(p[0].X * 100.0, MidpointRounding.AwayFromZero);
                        py = oy + (long)Math.Round(p[0].Y * 100.0, MidpointRounding.AwayFromZero);
                        _d.Append('M');
                        Hundredths(_d, px);
                        _d.Append(',');
                        Hundredths(_d, py);
                        first = true;
                        break;
                    case SKPathVerb.Line:
                        Line(p[1].X, p[1].Y);
                        break;
                    case SKPathVerb.Quad:
                    case SKPathVerb.Conic:
                    {
                        // a conic is a quadratic curve whose middle point weighs w
                        double w = verb == SKPathVerb.Conic ? it.ConicWeight() : 1;
                        const int n = 6;
                        for (int i = 1; i <= n; i++)
                        {
                            double t = (double)i / n, a = (1 - t) * (1 - t), b = 2 * t * (1 - t) * w, c = t * t, sum = a + b + c;
                            Line((a * p[0].X + b * p[1].X + c * p[2].X) / sum, (a * p[0].Y + b * p[1].Y + c * p[2].Y) / sum);
                        }
                        break;
                    }
                    case SKPathVerb.Cubic:
                    {
                        const int n = 8;
                        for (int i = 1; i <= n; i++)
                        {
                            double t = (double)i / n, u = 1 - t, a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, e = t * t * t;
                            Line(a * p[0].X + b * p[1].X + c * p[2].X + e * p[3].X, a * p[0].Y + b * p[1].Y + c * p[2].Y + e * p[3].Y);
                        }
                        break;
                    }
                    case SKPathVerb.Close:
                        _d.Append('z');
                        break;
                    default:
                        return;
                }
            }
        }

        /// <summary>
        /// Adds the outline of a line stroked <paramref name="width"/> units wide (butt ends) to the path data, cut to
        /// <paramref name="within"/> when a ring is given. Returns false when nothing is left of it.
        /// </summary>
        bool Stroke(double[] pts, bool close, double width, SKStrokeJoin join, double[]? within = null)
        {
            if (pts.Length < 4) return false;
            long ox = Hx(pts[0]), oy = Hy(pts[1]);
            using var line = Local(pts, close, ox, oy);
            using var pen = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)width, StrokeCap = SKStrokeCap.Butt, StrokeJoin = join, StrokeMiter = 10 };
            using var outline = pen.GetFillPath(line);
            if (outline is null || outline.IsEmpty) return false;
            if (within is null)
            {
                Outline(outline, ox, oy);
                return true;
            }
            using var region = Local(within, true, ox, oy);
            using var cut = outline.Op(region, SKPathOp.Intersect);
            if (cut is null || cut.IsEmpty) return false;
            Outline(cut, ox, oy);
            return true;
        }

        // ---------------------------------------------------------------- roads

        string Name(string ja, string en) => _ja ? ja : en;

        /// <summary>The names of the groups of roads by class (0 road, 1 highway, 2 unpaved track) and of their casings: the words of the style screens.</summary>
        static readonly (string Ja, string En)[] Kinds = [("一般道", "Roads"), ("高速道路", "Highways"), ("未舗装の道", "Unpaved tracks")];
        static readonly (string Ja, string En)[] Casings = [("一般道の縁", "Road casings"), ("高速道路の縁", "Highway casings")];

        /// <summary>The road layer: the drawing of <see cref="RoadDrawing"/>, shape by shape.</summary>
        public void Roads(List<string> names)
        {
            if (_in.Roads is not { } roads) return;
            var P = _in.Style.Paint;
            double cm = P.CasingWidth * _ppm;
            Rgb[] fill = [P.Road.Fill, P.Highway.Fill];
            Rgb?[] casing = [P.Road.Casing, P.Highway.Casing];
            var body = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            var to = new Section(body);
            string id = SvgFile.Id(MapLayers.Name(MapLayer.Roads));

            // tunnels: a path a tunnel, every outline of it, filled even-odd and see-through, its edge dashed
            if (P.Tunnel is { } tp)
            {
                to.Open($"{id}_Tunnels", Name("トンネル", "Tunnels"),
                    string.Create(CultureInfo.InvariantCulture, $" fill=\"{tp.Fill}\" fill-opacity=\"{N(tp.FillAlpha)}\" fill-rule=\"evenodd\" stroke=\"{tp.Color}\" stroke-width=\"{N(tp.Width * _ppm)}\" stroke-linejoin=\"round\"")
                    + (tp.Dash.Count >= 2 ? $" stroke-dasharray=\"{string.Join(' ', tp.Dash.Select(v => N(v * _ppm)))}\"" : ""));
                foreach (var g in roads.Tunnels)
                {
                    if (!g.Rings.Any(Reaches)) continue;
                    foreach (var ring in g.Rings) Ring(ring, true);
                    to.Path(_d);
                }
                to.Close();
            }

            // unpaved tracks: the segments that follow each other end to start are one line
            {
                to.Open($"{id}_Unpaved_tracks", Name("未舗装の道", "Unpaved tracks"),
                    $" fill=\"none\" stroke=\"{P.TrackColor}\" stroke-width=\"{N(Math.Round(P.TrackWidth * 20, MidpointRounding.ToEven) / 20 * _ppm)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"");
                var t = roads.Tracks;
                var line = new List<double>();
                void End()
                {
                    if (line.Count >= 4)
                    {
                        var pts = line.ToArray();
                        if (Reaches(pts))
                        {
                            Ring(pts, false);
                            to.Path(_d);
                        }
                    }
                    line.Clear();
                }
                for (int i = 0; i + 3 < t.Length; i += 4)
                {
                    bool follows = line.Count > 0 && Math.Abs(t[i] - line[^2]) < 0.005 && Math.Abs(t[i + 1] - line[^1]) < 0.005;
                    if (!follows)
                    {
                        End();
                        line.Add(t[i]);
                        line.Add(t[i + 1]);
                    }
                    line.Add(t[i + 2]);
                    line.Add(t[i + 3]);
                }
                End();
                to.Close();
            }

            var ground = roads.Ground.Where(g => Reaches(g.Casing)).ToList();
            var patches = roads.Patches.Where(p => Reaches(p.Ring)).ToList();
            var corners = roads.Corners.Where(k => Reaches(k.Region) || Reaches(k.Arc)).ToList();
            // the casings: the ribbons' casing rings, the outlines of the corner patches' and of the junction arcs' strokes
            for (int c = 0; c < 2; c++)
            {
                if (casing[c] is not { } cc) continue;
                to.Open($"{id}_{SvgFile.Id(Casings[c].En)}", Name(Casings[c].Ja, Casings[c].En), $" fill=\"{cc}\"");
                foreach (var g in ground)
                    if (g.Class == c)
                    {
                        Ring(g.Casing, true);
                        to.Path(_d);
                    }
                foreach (var p in patches)
                    if (p.Class == c && Stroke(p.Ring, true, 2 * cm, SKStrokeJoin.Miter)) to.Path(_d);
                foreach (var k in corners)
                    if (k.Class == c && Stroke(k.Arc, false, 2 * cm, SKStrokeJoin.Round)) to.Path(_d);
                to.Close();
            }
            // the roads, then the highways: the ribbons' fill rings and the corner patches
            for (int c = 0; c < 2; c++)
            {
                to.Open($"{id}_{SvgFile.Id(Kinds[c].En)}", Name(Kinds[c].Ja, Kinds[c].En), $" fill=\"{fill[c]}\"");
                foreach (var g in ground)
                    if (g.Class == c)
                    {
                        Ring(g.Fill, true);
                        to.Path(_d);
                    }
                foreach (var p in patches)
                    if (p.Class == c)
                    {
                        Ring(p.Ring, true);
                        to.Path(_d);
                    }
                to.Close();
            }
            // the rounded junction corners: the region, and over it the seam along the road edges cut to the region
            to.Open($"{id}_Junction_corners", Name("交差点の角", "Junction corners"), "");
            for (int c = 0; c < 2; c++)
            {
                to.Open($"{id}_Junction_corners_{SvgFile.Id(Kinds[c].En)}", Name(Kinds[c].Ja, Kinds[c].En), $" fill=\"{fill[c]}\"");
                foreach (var k in corners)
                {
                    if (k.Class != c) continue;
                    Ring(k.Region, true);
                    to.Path(_d);
                    if (k.Seam.Length >= 4 && Stroke(k.Seam, false, 2 * cm, SKStrokeJoin.Round, k.Region)) to.Path(_d);
                }
                to.Close();
            }
            to.Close();
            // the raised runs level by level: the casings, then the tracks, the roads and the highways
            var raised = roads.Raised.Where(x => Reaches(x.Casing)).ToList();
            foreach (int level in raised.Select(x => x.Level).Distinct().Order())
            {
                var runs = raised.Where(x => x.Level == level).ToList();
                string rid = string.Create(CultureInfo.InvariantCulture, $"{id}_Raised_roads_{level}");
                to.Open(rid, string.Create(CultureInfo.InvariantCulture, $"{Name("高架", "Raised roads")} {level}"), "");
                for (int c = 0; c < 2; c++)
                {
                    if (casing[c] is not { } cc) continue;
                    to.Open($"{rid}_{SvgFile.Id(Casings[c].En)}", Name(Casings[c].Ja, Casings[c].En), $" fill=\"{cc}\"");
                    foreach (var x in runs)
                        if (x.Class == c)
                        {
                            Ring(x.Casing, true);
                            to.Path(_d);
                        }
                    to.Close();
                }
                foreach (int c in (ReadOnlySpan<int>)[2, 0, 1])
                {
                    to.Open($"{rid}_{SvgFile.Id(Kinds[c].En)}", Name(Kinds[c].Ja, Kinds[c].En), $" fill=\"{(c == 2 ? P.TrackColor : fill[c])}\"");
                    foreach (var x in runs)
                        if (x.Class == c)
                        {
                            Ring(x.Fill, true);
                            to.Path(_d);
                        }
                    to.Close();
                }
                to.Close();
            }
            Layer(MapLayer.Roads, body, to.Written, names);
        }

        /// <summary>Writes a layer whose content is gathered in <paramref name="body"/>, when it has any.</summary>
        void Layer(MapLayer layer, StringWriter body, bool any, List<string> names)
        {
            if (!any) return;
            string name = MapLayers.Name(layer, _ja ? "ja" : "en");
            _to.Write($"<g id=\"{SvgFile.Id(MapLayers.Name(layer))}\" inkscape:groupmode=\"layer\" inkscape:label=\"{Attr(name)}\">\n");
            _to.Write(body.GetStringBuilder());
            _to.Write("</g>\n");
            names.Add(name);
        }

        /// <summary>
        /// The groups of a layer written into a text: a group's start is held back until something is written in it, so
        /// a group with nothing in it leaves nothing.
        /// </summary>
        sealed class Section(StringWriter to)
        {
            readonly List<(string Start, bool Written)> _open = new();

            /// <summary>Something was written.</summary>
            public bool Written { get; private set; }

            public void Open(string id, string label, string attributes) =>
                _open.Add(($"<g id=\"{id}\" inkscape:label=\"{Attr(label)}\"{attributes}>\n", false));

            /// <summary>Writes the starts of the open groups not written yet.</summary>
            void Start()
            {
                for (int i = 0; i < _open.Count; i++)
                {
                    if (_open[i].Written) continue;
                    to.Write(_open[i].Start);
                    _open[i] = (_open[i].Start, true);
                }
                Written = true;
            }

            public void Close()
            {
                if (_open[^1].Written) to.Write("</g>\n");
                _open.RemoveAt(_open.Count - 1);
            }

            /// <summary>Writes the path data gathered as a path of the innermost group and empties it; nothing when there is none.</summary>
            public void Path(StringBuilder d, string attributes = "")
            {
                if (d.Length == 0) return;
                Start();
                to.Write("<path");
                to.Write(attributes);
                to.Write(" d=\"");
                to.Write(d);
                to.Write("\"/>\n");
                d.Clear();
            }

        }

        // ---------------------------------------------------------------- labels

        SKTypeface Face(string family, bool bold)
        {
            if (!_faces.TryGetValue((family, bold), out var tf)) _faces[(family, bold)] = tf = CellPainter.TypefaceOf(family, bold);
            return tf;
        }

        /// <summary>What switches the kerning and the ligatures off: the drawing sets glyph after glyph by its advance.</summary>
        const string Plain = "font-variant-ligatures:none;font-feature-settings:'kern' 0";

        static string Family(string family) => family.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-') ? $"'{family.Replace("'", "")}'" : family;

        /// <summary>
        /// A text as <see cref="CellPainter"/> draws it: the middle of its width and of its ink's height at its point,
        /// turned about it; with <paramref name="chars"/>, every glyph at its own point and turn. Returns the element,
        /// or null for an empty text.
        /// </summary>
        string? Text(double x, double y, double rot, string text, string family, bool bold, double em, double spacing, Rgb colour, Rgb? halo, double haloM,
            IReadOnlyList<LabelLines.Glyph>? chars)
        {
            if (text.Length == 0) return null;
            using var font = CellPainter.FontOf(Face(family, bold), em);
            var (w, _, baseY) = CellPainter.SetText(font, text, spacing * CellPainter.Ppm, ITextMetrics.Glyphs(text));
            var style = new StringBuilder();
            style.Append("font-family:").Append(Family(family)).Append(";font-weight:").Append(bold ? "bold" : "normal")
                .Append(";font-size:").Append(N(em * _ppm)).Append("px;").Append(Plain).Append(";fill:").Append(colour);
            if (spacing != 0 && chars is not { Count: > 0 }) style.Append(";letter-spacing:").Append(N(spacing * _ppm)).Append("px");
            if (halo is { } h)
                style.Append(";stroke:").Append(h).Append(";stroke-width:").Append(N(haloM * _ppm)).Append("px;stroke-linejoin:round;stroke-linecap:round;paint-order:stroke fill markers");
            var sb = new StringBuilder("<text xml:space=\"preserve\" style=\"").Append(Attr(style.ToString())).Append('"');
            if (chars is { Count: > 0 })
            {
                var xs = new StringBuilder();
                var ys = new StringBuilder();
                var turns = new StringBuilder();
                var content = new StringBuilder();
                foreach (var g in chars)
                {
                    // the glyph's start on its baseline: half its advance before its point, the baseline under the ink's middle
                    double th = -g.Rotation * Math.PI / 180, ox = -g.Advance * _ppm / 2, oy = baseY * _k;
                    if (xs.Length > 0)
                    {
                        xs.Append(' ');
                        ys.Append(' ');
                        turns.Append(' ');
                    }
                    xs.Append(N(X(g.X) + ox * Math.Cos(th) - oy * Math.Sin(th)));
                    ys.Append(N(Y(g.Y) + ox * Math.Sin(th) + oy * Math.Cos(th)));
                    turns.Append(N(-g.Rotation));
                    content.Append(g.Char);
                }
                return sb.Append(" x=\"").Append(xs).Append("\" y=\"").Append(ys).Append("\" rotate=\"").Append(turns).Append("\">").Append(Attr(content.ToString())).Append("</text>\n").ToString();
            }
            double cx = X(x), cy = Y(y);
            sb.Append(" x=\"").Append(N(cx - w * _k / 2)).Append("\" y=\"").Append(N(cy + baseY * _k)).Append('"');
            if (rot != 0) sb.Append(" transform=\"rotate(").Append(N(-rot)).Append(' ').Append(N(cx)).Append(' ').Append(N(cy)).Append(")\"");
            return sb.Append('>').Append(Attr(text)).Append("</text>\n").ToString();
        }

        /// <summary>The width (m) a text takes as the drawing sets it.</summary>
        double WidthOf(string text, string family, bool bold, double em, double spacing)
        {
            using var font = CellPainter.FontOf(Face(family, bold), em);
            return CellPainter.SetText(font, text, spacing * CellPainter.Ppm, ITextMetrics.Glyphs(text)).Width / CellPainter.Ppm;
        }

        /// <summary>Whether a label about its point reaches the range: its width and its size around the point, whatever its turn.</summary>
        bool Reaches(PlacedLabel l)
        {
            if (l.Chars is { Count: > 0 } chars)
                return Reaches(chars.Min(g => g.X) - l.Size, chars.Max(g => g.Y) + l.Size, chars.Max(g => g.X) + l.Size, chars.Min(g => g.Y) - l.Size);
            double half = Math.Max(WidthOf(l.Text, l.Font, l.Weight == "bold", l.Size, l.Spacing), l.Size) / 2 + l.Size;
            return Reaches(l.X - half, l.Y + half, l.X + half, l.Y - half);
        }

        /// <summary>
        /// The label layers in the drawing's order (<c>CellPainter.LabelSteps</c>): the postal codes, the points of
        /// interest, the zone names, the street names; a label too small for the drawing is left out as there.
        /// </summary>
        public void Labels(List<string> names)
        {
            var ls = _in.Style.Labels;
            double minPx = ls?.MinPixels ?? 4.0;
            string fontEn = ls?.Font("en") ?? "Bahnschrift";
            bool Small(double em) => em * CellPainter.Ppm < minPx;

            void Kind(MapLayer layer, string kind, Rgb colour, Rgb? halo, double haloM)
            {
                var body = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
                bool any = false;
                foreach (var l in _in.Labels)
                {
                    if (l.Kind != kind || Small(l.Size) || !Reaches(l)) continue;
                    if (Text(l.X, l.Y, l.Rotation, l.Text, l.Font, l.Weight == "bold", l.Size, l.Spacing, colour, halo, haloM, kind == "street" ? l.Chars : null) is not { } text) continue;
                    body.Write(text);
                    any = true;
                }
                Layer(layer, body, any, names);
            }

            if (ls is not null) Kind(MapLayer.Postal, "postal", ls.PostalColor, null, 0);
            Pois(names, ls, fontEn, minPx);
            if (ls is null) return;
            Kind(MapLayer.Zones, "zone", ls.ZoneColor, ls.ZoneOutline, ls.ZoneOutlineWidth);
            Kind(MapLayer.Streets, "street", ls.StreetColor, null, 0);
        }

        /// <summary>The points of interest, a group a point: the circle, the icon, the label (<c>CellPainter.LabelSteps</c>, <c>DrawIcon</c>, <c>LabelBeside</c>).</summary>
        void Pois(List<string> names, LabelsStyle? ls, string fontEn, double minPx)
        {
            var body = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            bool any = false;
            foreach (var poi in _in.Pois)
            {
                var s = poi.Style;
                if (poi.Size * CellPainter.Ppm < minPx) continue;
                var (text, lang) = LabelPlacer.PoiLabel(poi, _in.Language);
                double east = PoiLayout.Reach(poi, text), halfH = PoiLayout.Space(poi).H / 2 + poi.Size;
                if (!Reaches(poi.Point.X - east - poi.Size, poi.Point.Y + halfH, poi.Point.X + east + poi.Size, poi.Point.Y - halfH)) continue;
                Rgb? outline = s.Outline is { } o && s.OutlineWidth > 0 ? o : null;
                string family = ls?.Font(lang) ?? fontEn;
                bool bold = s.Weight == "bold";
                double cx = X(poi.Point.X), cy = Y(poi.Point.Y);
                var sb = new StringBuilder();
                string Circle(double r, Rgb fill, Rgb? stroke) =>
                    $"<circle cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N(r)}\" fill=\"{fill}\"" + (stroke is { } st ? $" stroke=\"{st}\" stroke-width=\"{N(s.OutlineWidth * _ppm)}\"" : "") + "/>\n";
                void Beside()
                {
                    if (!s.ShowLabel || text.Length == 0 || poi.LabelSize * CellPainter.Ppm < minPx) return;
                    double w = WidthOf(text, family, bold, poi.LabelSize, 0);
                    var colour = s.Look == "icon" && s.BadgeColor is { } circle ? circle : poi.Color;
                    sb.Append(Text(PoiLayout.LabelLeft(poi) + w / 2, poi.Point.Y, 0, text, family, bold, poi.LabelSize, 0, colour, outline, s.OutlineWidth, null));
                }
                if (s.Look == "dot")
                {
                    sb.Append(Circle(poi.Size * _ppm / 2, poi.Color, outline));
                    Beside();
                }
                else if (s.Look == "icon")
                {
                    double size = poi.Size * _ppm;
                    if (s.BadgeColor is { } badge) sb.Append(Circle(PoiLayout.Mark(poi).W * _ppm / 2, badge, outline));
                    if (s.Image is not null)
                    {
                        if (_in.PoiPictures.TryGetValue(s.Id, out var href))
                        {
                            double w = size * s.ImageAspect;
                            sb.Append($"<image xlink:href=\"{Attr(href)}\" x=\"{N(cx - w / 2)}\" y=\"{N(cy - size / 2)}\" width=\"{N(w)}\" height=\"{N(size)}\" preserveAspectRatio=\"none\"/>\n");
                        }
                    }
                    else if (s.Icon is { } icon && MdiIcons.PathOf(icon) is { } d)
                    {
                        double k = size / MdiIcons.Box;
                        sb.Append($"<path transform=\"translate({N(cx - size / 2)} {N(cy - size / 2)}) scale({k.ToString("0.#####", CultureInfo.InvariantCulture)})\" fill=\"{poi.Color}\"");
                        if (outline is { } halo && s.BadgeColor is null)
                            sb.Append($" stroke=\"{halo}\" stroke-width=\"{(s.OutlineWidth * _ppm / k).ToString("0.####", CultureInfo.InvariantCulture)}\" stroke-linejoin=\"round\" stroke-linecap=\"round\" paint-order=\"stroke fill markers\"");
                        sb.Append($" d=\"{Attr(d)}\"/>\n");
                    }
                    Beside();
                }
                else if (s.Look == "badge")
                {
                    // the circle around the label: the larger of its width and its ink's height, with a fifth of its size around it
                    using var font = CellPainter.FontOf(Face(family, bold), poi.Size);
                    var (w, ink, _) = CellPainter.SetText(font, text, 0, ITextMetrics.Glyphs(text));
                    double pad = 0.2 * poi.Size * CellPainter.Ppm;
                    sb.Append(Circle(Math.Max(w + 2 * pad, ink.Height + 2 * pad) / 2 * _k, s.BadgeColor ?? poi.Color, null));
                    sb.Append(Text(poi.Point.X, poi.Point.Y, 0, text, family, bold, poi.Size, 0, poi.Color, null, 0, null));
                }
                else sb.Append(Text(poi.Point.X, poi.Point.Y, 0, text, family, bold, poi.Size, 0, poi.Color, outline, s.OutlineWidth, null));
                if (sb.Length == 0) continue;
                string label = poi.Point.Name.Length > 0 ? poi.Point.Name : text;
                body.Write($"<g inkscape:label=\"{Attr(label)}\">\n");
                body.Write(sb);
                body.Write("</g>\n");
                any = true;
            }
            Layer(MapLayer.Poi, body, any, names);
        }
    }
}
