using System.Globalization;
using System.Xml.Linq;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Export;

/// <summary>A map written as an SVG file (<see cref="SvgFile"/>): the linked pictures, the roads as shapes, the labels as text.</summary>
public sealed class SvgFileTests
{
    static readonly XNamespace Svg = "http://www.w3.org/2000/svg", Xlink = "http://www.w3.org/1999/xlink", Inkscape = "http://www.inkscape.org/namespaces/inkscape";

    /// <summary>The units a metre at a zoom level, and the units a pixel of the drawing (zoom 8).</summary>
    static double Ppm(int zoom) => EditableChoice.BlockPx(zoom) / WorldGrid.BlockSize;

    static double[] Square(double x0, double y0, double x1, double y1) => [x0, y0, x1, y0, x1, y1, x0, y1];

    static XElement Write(MapFrame frame, int zoom, string language, IReadOnlyList<SvgPicture> pictures, SvgDrawing? drawing, out IReadOnlyList<string> names)
    {
        var text = new StringWriter(CultureInfo.InvariantCulture);
        names = SvgFile.Write(text, frame, zoom, language, pictures, drawing, "a test -- map");
        Assert.DoesNotContain("\r", text.ToString());
        return XDocument.Parse(text.ToString()).Root!;
    }

    static double Num(XElement e, string attribute) => double.Parse(e.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);

    static double[] Numbers(XElement e, string attribute) =>
        e.Attribute(attribute)!.Value.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    static IReadOnlyDictionary<string, string> Style(XElement e) =>
        e.Attribute("style")!.Value.Split(';').Select(p => p.Split(':', 2)).ToDictionary(p => p[0], p => p[1]);

    static XElement Group(XElement root, string id) => root.Descendants(Svg + "g").Single(g => (string?)g.Attribute("id") == id);

    static string[] Ids(XElement parent) => parent.Elements(Svg + "g").Select(g => g.Attribute("id")!.Value).ToArray();

    static string[] Labels(XElement parent) => parent.Elements(Svg + "g").Select(g => g.Attribute(Inkscape + "label")!.Value).ToArray();

    /// <summary>The rings and lines of a path's data as points: an absolute start, steps from point to point, <c>z</c> where closed.</summary>
    static List<(List<(double X, double Y)> Points, bool Closed)> Shapes(XElement path)
    {
        var o = new List<(List<(double, double)>, bool)>();
        foreach (var part in path.Attribute("d")!.Value.Split('M', StringSplitOptions.RemoveEmptyEntries))
        {
            bool closed = part.EndsWith('z');
            var body = closed ? part[..^1] : part;
            var head = body.Split('l', 2);
            var first = head[0].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var pts = new List<(double, double)> { (first[0], first[1]) };
            if (head.Length > 1)
                foreach (var step in head[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var d = step.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    pts.Add((pts[^1].Item1 + d[0], pts[^1].Item2 + d[1]));
                }
            o.Add((pts, closed));
        }
        return o;
    }

    static void Near(double want, double got, double within = 0.011) => Assert.InRange(got, want - within, want + within);

    static SvgDrawing Drawing(MapStyle style, RoadShapesFile.Contents? roads = null, IReadOnlyList<PlacedLabel>? labels = null, IReadOnlyList<ResolvedPoi>? pois = null,
        IEnumerable<BlockId>? range = null, string language = "en", IReadOnlyDictionary<string, string>? pictures = null) => new()
    {
        Style = style, Roads = roads, Labels = labels ?? [], Pois = pois ?? [], Language = language,
        Range = (range ?? [new BlockId(0, 0), new BlockId(1, 0)]).ToHashSet(),
        PoiPictures = pictures ?? new Dictionary<string, string>(),
    };

    [Fact]
    public void TheFileIsThePicturesThenTheDrawnLayersBottomFirst()
    {
        var style = MapStyle.Builtin("postalcodemap");
        SvgPicture[] pictures =
        [
            new("Ground", "地面", "m-z6-svg/ground.png", 0, 0, 512, 256),
            new("Shading_dark_side", "陰影（暗い側）", "m-z6-svg/shading-dark.png", 256, 0, 256, 256, LayerBlend.Multiply),
            new("Shading_light_side", "陰影（明るい側）", "m-z6-svg/shading-light.png", 0, 0, 256, 256, LayerBlend.ColorDodge),
        ];
        var roads = new RoadShapesFile.Contents([], [new RoadRibbon(0, Square(-4130, 8206, -3870, 8194), Square(-4130, 8205, -3870, 8195))], [], [], [], [], 5.0);
        PlacedLabel[] labels = [new("postal", "2000", -4000, 8300, 0, 30, "Bahnschrift", "bold", 0)];
        var root = Write(MapFrame.Standard, 6, "ja", pictures, Drawing(style, roads, labels), out var names);

        Assert.Equal(("8192", "12288", "0 0 8192 12288"), (root.Attribute("width")!.Value, root.Attribute("height")!.Value, root.Attribute("viewBox")!.Value));
        // every child of the root is a layer: its id the English name, its label the name asked for
        Assert.All(root.Elements(), g => Assert.Equal("layer", g.Attribute(Inkscape + "groupmode")!.Value));
        Assert.Equal(new[] { "Ground", "Shading_dark_side", "Shading_light_side", "Roads", "Postal_codes" }, Ids(root));
        Assert.Equal(new[] { "地面", "陰影（暗い側）", "陰影（明るい側）", "道路", "番地" }, Labels(root));
        Assert.Equal(Labels(root), names);
        // a picture layer: the linked file where the layer lies, and the shading's blend modes
        var dark = Group(root, "Shading_dark_side");
        var image = dark.Elements().Single();
        Assert.Equal(("image", "m-z6-svg/shading-dark.png", 256, 0, 256, 256),
            (image.Name.LocalName, image.Attribute(Xlink + "href")!.Value, Num(image, "x"), Num(image, "y"), Num(image, "width"), Num(image, "height")));
        Assert.Equal("mix-blend-mode:multiply", dark.Attribute("style")!.Value);
        Assert.Equal("mix-blend-mode:color-dodge", Group(root, "Shading_light_side").Attribute("style")!.Value);
        Assert.Null(Group(root, "Ground").Attribute("style"));

        // in English at zoom 7: twice the units, the same layers under their English names
        var fine = Write(MapFrame.Standard, 7, "en", [], Drawing(style, roads, labels), out names);
        Assert.Equal("0 0 16384 24576", fine.Attribute("viewBox")!.Value);
        Assert.Equal(new[] { "Roads", "Postal codes" }, names);
        Assert.Equal(new[] { "Roads", "Postal_codes" }, Ids(fine));
        // a map that is one picture: no drawn layers
        var photo = Write(MapFrame.Standard, 6, "en", [new("Satellite_map", "Satellite map", "s/satellite.png", 0, 0, 8192, 12288)], null, out names);
        Assert.Equal(new[] { "Satellite map" }, names);
        Assert.Single(photo.Elements());
        // cells added above and to the left move every place: the frame's corner is the document's
        var wide = Write(new MapFrame(1, 0, 2, 0), 6, "en", [], Drawing(style, roads, labels), out _);
        Assert.Equal("0 0 12288 14336", wide.Attribute("viewBox")!.Value);
        var ring = Shapes(Group(wide, "Roads_Roads").Elements(Svg + "path").Single()).Single().Points;
        Near((-4130 - WorldGrid.Left + 2 * WorldGrid.CellSize) * Ppm(6), ring[0].X);
        Near((WorldGrid.Top + WorldGrid.CellSize - 8205) * Ppm(6), ring[0].Y);
    }

    [Fact]
    public void TheRoadsAreShapesInTheOrderTheyAreDrawn()
    {
        var style = MapStyle.Builtin("postalcodemap");
        var paint = style.Paint;
        var roads = new RoadShapesFile.Contents(
            // two segments that follow each other and one apart
            Tracks: [-4100, 8300, -4090, 8300, -4090, 8300, -4080, 8310, -4000, 8300, -3990, 8300],
            Ground:
            [
                new RoadRibbon(0, Square(-4130, 8206, -3870, 8194), Square(-4130, 8205, -3870, 8195)),
                new RoadRibbon(1, Square(-4130, 8186, -3870, 8174), Square(-4130, 8185, -3870, 8175)),
                new RoadRibbon(0, Square(2000, -2000, 2100, -2010), Square(2000, -2001, 2100, -2009)),       // far outside the range
            ],
            Raised:
            [
                new RaisedRun(1, 0, Square(-4050, 8260, -4040, 8150), Square(-4049, 8260, -4041, 8150)),
                new RaisedRun(1, 2, Square(-4030, 8260, -4020, 8150), Square(-4029, 8260, -4021, 8150)),
                new RaisedRun(2, 1, Square(-4010, 8260, -4000, 8150), Square(-4009, 8260, -4001, 8150)),
            ],
            Patches: [new CornerPatch(0, Square(-3900, 8205, -3890, 8195))],
            Corners: [new JunctionCorner(0, Region: Square(-3960, 8215, -3950, 8205), Arc: [-3960, 8215, -3955, 8212, -3950, 8205], Seam: [-3960, 8205, -3950, 8205])],
            Tunnels: [new TunnelGroup([Square(-4120, 8150, -4100, 8130), Square(-4115, 8145, -4105, 8135)])],
            TrackWidth: 5.0);
        var root = Write(MapFrame.Standard, 6, "ja", [], Drawing(style, roads), out _);
        var layer = Group(root, "Roads");
        Assert.Equal(new[] { "Roads_Tunnels", "Roads_Unpaved_tracks", "Roads_Road_casings", "Roads_Highway_casings", "Roads_Roads", "Roads_Highways",
            "Roads_Junction_corners", "Roads_Raised_roads_1", "Roads_Raised_roads_2" }, Ids(layer));
        Assert.Equal(new[] { "トンネル", "未舗装の道", "一般道の縁", "高速道路の縁", "一般道", "高速道路", "交差点の角", "高架 1", "高架 2" }, Labels(layer));
        double ppm = Ppm(6);
        double X(double x) => (x - WorldGrid.Left) * ppm;
        double Y(double y) => (WorldGrid.Top - y) * ppm;

        // a tunnel: one path of its outlines, see-through and filled even-odd, its edge dashed
        var tunnels = Group(root, "Roads_Tunnels");
        var tp = paint.Tunnel!;
        Assert.Equal((tp.Fill.ToString(), "evenodd", tp.Color.ToString()), (tunnels.Attribute("fill")!.Value, tunnels.Attribute("fill-rule")!.Value, tunnels.Attribute("stroke")!.Value));
        Near(tp.FillAlpha, Num(tunnels, "fill-opacity"));
        Near(tp.Width * ppm, Num(tunnels, "stroke-width"));
        Assert.Equal(tp.Dash.Count, Numbers(tunnels, "stroke-dasharray").Length);
        Near(tp.Dash[0] * ppm, Numbers(tunnels, "stroke-dasharray")[0]);
        var outlines = Shapes(tunnels.Elements(Svg + "path").Single());
        Assert.Equal(new[] { (4, true), (4, true) }, outlines.Select(s => (s.Points.Count, s.Closed)));
        Near(X(-4115), outlines[1].Points[0].X);
        Near(Y(8145), outlines[1].Points[0].Y);

        // the unpaved tracks: a line a run of segments, as wide as the style draws them
        var tracks = Group(root, "Roads_Unpaved_tracks");
        Assert.Equal(("none", paint.TrackColor.ToString(), "round", "round"),
            (tracks.Attribute("fill")!.Value, tracks.Attribute("stroke")!.Value, tracks.Attribute("stroke-linecap")!.Value, tracks.Attribute("stroke-linejoin")!.Value));
        Near(paint.TrackWidth * ppm, Num(tracks, "stroke-width"));
        var lines = tracks.Elements(Svg + "path").Select(p => Shapes(p).Single()).ToList();
        Assert.Equal(new[] { (3, false), (2, false) }, lines.Select(s => (s.Points.Count, s.Closed)));
        Near(X(-4080), lines[0].Points[2].X);
        Near(Y(8310), lines[0].Points[2].Y);

        // the casings: the ribbon's ring, the outline of the patch's stroke (a band: two rings) and of the arc's stroke
        var casings = Group(root, "Roads_Road_casings");
        Assert.Equal(paint.Road.Casing!.Value.ToString(), casings.Attribute("fill")!.Value);
        Assert.Null(casings.Attribute("stroke"));
        var cased = casings.Elements(Svg + "path").Select(Shapes).ToList();
        Assert.Equal(3, cased.Count);
        Assert.Equal(new[] { (X(-4130), Y(8206)), (X(-3870), Y(8206)), (X(-3870), Y(8194)), (X(-4130), Y(8194)) }.Select(p => (Math.Round(p.Item1, 2), Math.Round(p.Item2, 2))),
            cased[0].Single().Points.Select(p => (Math.Round(p.X, 2), Math.Round(p.Y, 2))));
        Assert.Equal(2, cased[1].Count);                                        // around the patch's ring, outside and inside
        double cm = paint.CasingWidth * ppm;
        Near(X(-3900) - cm, cased[1].SelectMany(s => s.Points).Min(p => p.X), 0.02);
        Near(X(-3890) + cm, cased[1].SelectMany(s => s.Points).Max(p => p.X), 0.02);
        Assert.All(cased[2], s => Assert.True(s.Closed));                       // the arc's stroke is a shape, not a line
        Assert.Single(Group(root, "Roads_Highway_casings").Elements(Svg + "path"));

        // the roads: the ribbon in the range (not the one far outside it) and the patch; the highways: theirs
        var fills = Group(root, "Roads_Roads");
        Assert.Equal(paint.Road.Fill.ToString(), fills.Attribute("fill")!.Value);
        Assert.Equal(2, fills.Elements(Svg + "path").Count());
        var ribbon = Shapes(fills.Elements(Svg + "path").First()).Single();
        Assert.True(ribbon.Closed);
        Near(X(-4130), ribbon.Points[0].X);
        Near(Y(8205), ribbon.Points[0].Y);
        Near(X(-3870), ribbon.Points[2].X);
        Near(Y(8195), ribbon.Points[2].Y);
        Assert.Equal(paint.Highway.Fill.ToString(), Group(root, "Roads_Highways").Attribute("fill")!.Value);

        // a junction corner: its region, and its seam's stroke cut to the region
        var corners = Group(root, "Roads_Junction_corners");
        Assert.Equal(new[] { "Roads_Junction_corners_Roads" }, Ids(corners));
        Assert.Equal(new[] { "一般道" }, Labels(corners));
        var corner = Group(root, "Roads_Junction_corners_Roads").Elements(Svg + "path").Select(Shapes).ToList();
        Assert.Equal(2, corner.Count);
        Assert.Equal(4, corner[0].Single().Points.Count);
        var seam = corner[1].SelectMany(s => s.Points).ToList();
        Near(Y(8205) - cm, seam.Min(p => p.Y), 0.02);                           // the half of the stroke inside the region
        Near(Y(8205), seam.Max(p => p.Y), 0.02);
        Assert.InRange(seam.Min(p => p.X), X(-3960) - 0.02, X(-3960) + 0.02);
        Assert.InRange(seam.Max(p => p.X), X(-3950) - 0.02, X(-3950) + 0.02);

        // the raised runs level by level: the casings, the tracks, the roads, the highways
        Assert.Equal(new[] { "Roads_Raised_roads_1_Road_casings", "Roads_Raised_roads_1_Unpaved_tracks", "Roads_Raised_roads_1_Roads" }, Ids(Group(root, "Roads_Raised_roads_1")));
        Assert.Equal(new[] { "一般道の縁", "未舗装の道", "一般道" }, Labels(Group(root, "Roads_Raised_roads_1")));
        Assert.Equal(new[] { "Roads_Raised_roads_2_Highway_casings", "Roads_Raised_roads_2_Highways" }, Ids(Group(root, "Roads_Raised_roads_2")));
        Assert.Equal(paint.TrackColor.ToString(), Group(root, "Roads_Raised_roads_1_Unpaved_tracks").Attribute("fill")!.Value);

        // in English; and nothing of a map without roads in its range
        Assert.Equal(new[] { "Tunnels", "Unpaved tracks", "Road casings", "Highway casings", "Roads", "Highways", "Junction corners", "Raised roads 1", "Raised roads 2" },
            Labels(Group(Write(MapFrame.Standard, 6, "en", [], Drawing(style, roads), out _), "Roads")));
        Assert.Empty(Write(MapFrame.Standard, 6, "en", [], Drawing(style, roads, range: [new BlockId(20, 20)]), out var none).Elements());
        Assert.Empty(none);
    }

    /// <summary>The numbers the drawing sets a text with, in the units of a zoom level: its width and its baseline under the middle of its ink.</summary>
    static (double Width, double BaseY) Set(string text, string family, bool bold, double em, double spacing, int zoom)
    {
        using var font = CellPainter.FontOf(CellPainter.TypefaceOf(family, bold), em);
        var (w, _, baseY) = CellPainter.SetText(font, text, spacing * CellPainter.Ppm, ITextMetrics.Glyphs(text));
        double k = Ppm(zoom) / CellPainter.Ppm;
        return (w * k, baseY * k);
    }

    [Fact]
    public void LabelsAreTextPlacedAsTheDrawingPlacesThem()
    {
        var style = MapStyle.Builtin("postalcodemap");
        var ls = style.Labels!;
        double ppm = Ppm(6);
        double X(double x) => (x - WorldGrid.Left) * ppm;
        double Y(double y) => (WorldGrid.Top - y) * ppm;
        LabelLines.Glyph[] chars = [new("A", -4000, 8200, 0, 12), new("&", -3990, 8200, 90, 10), new("b", -3990, 8210, 45, 8)];
        PlacedLabel[] labels =
        [
            new("postal", "2000", -4000, 8300, 0, 30, "Bahnschrift", "bold", 0),
            new("postal", "9", -4000, 8320, 0, 0.5, "Bahnschrift", "bold", 0),                   // too small to be drawn
            new("postal", "7777", 3000, -3000, 0, 30, "Bahnschrift", "bold", 0),                 // outside the range
            new("zone", "A <& B", -3950, 8250, 0, 18, "Bahnschrift", "normal", 3),
            new("street", "A&b", -3995, 8205, 30, 14, "Bahnschrift", "bold", 0, chars),
        ];
        var root = Write(MapFrame.Standard, 6, "en", [], Drawing(style, labels: labels), out var names);
        Assert.Equal(new[] { "Postal codes", "Zone names", "Street names" }, names);

        // a postal code: one text, the middle of its width and of its ink's height at its point
        var postal = Group(root, "Postal_codes").Elements(Svg + "text").Single();
        Assert.Equal("2000", postal.Value);
        var (w, baseY) = Set("2000", "Bahnschrift", true, 30, 0, 6);
        Near(X(-4000) - w / 2, Num(postal, "x"));
        Near(Y(8300) + baseY, Num(postal, "y"));
        Assert.Null(postal.Attribute("transform"));
        var s = Style(postal);
        Assert.Equal(("Bahnschrift", "bold", ls.PostalColor.ToString(), "none", "'kern' 0"),
            (s["font-family"], s["font-weight"], s["fill"], s["font-variant-ligatures"], s["font-feature-settings"]));
        Near(30 * ppm, double.Parse(s["font-size"][..^2], CultureInfo.InvariantCulture));
        Assert.False(s.ContainsKey("letter-spacing"));
        Assert.False(s.ContainsKey("stroke"));

        // a zone name: its letter spacing, and its outline as the text's stroke under its fill
        var zone = Group(root, "Zone_names").Elements(Svg + "text").Single();
        Assert.Equal("A <& B", zone.Value);
        (w, baseY) = Set("A <& B", "Bahnschrift", false, 18, 3, 6);
        Near(X(-3950) - w / 2, Num(zone, "x"));
        Near(Y(8250) + baseY, Num(zone, "y"));
        s = Style(zone);
        Assert.Equal(("normal", ls.ZoneColor.ToString(), ls.ZoneOutline!.Value.ToString(), "stroke fill markers"), (s["font-weight"], s["fill"], s["stroke"], s["paint-order"]));
        Near(3 * ppm, double.Parse(s["letter-spacing"][..^2], CultureInfo.InvariantCulture));
        Near(ls.ZoneOutlineWidth * ppm, double.Parse(s["stroke-width"][..^2], CultureInfo.InvariantCulture));

        // a street name: one text, a place and a turn for every character (the start of the glyph on its baseline)
        var street = Group(root, "Street_names").Elements(Svg + "text").Single();
        Assert.Equal("A&b", street.Value);
        Assert.Equal("preserve", street.Attribute(XNamespace.Xml + "space")!.Value);
        (_, baseY) = Set("A&b", "Bahnschrift", true, 14, 0, 6);
        double[] xs = Numbers(street, "x"), ys = Numbers(street, "y"), turns = Numbers(street, "rotate");
        Assert.Equal(new[] { 0.0, -90, -45 }, turns);
        Near(X(-4000) - 12 * ppm / 2, xs[0]);                                   // not turned: half its advance before its point
        Near(Y(8200) + baseY, ys[0]);
        Near(X(-3990) + baseY, xs[1]);                                          // a quarter turn to the left: it reads upwards
        Near(Y(8200) + 10 * ppm / 2, ys[1]);
        double r = Math.Sqrt(0.5);
        Near(X(-3990) - 8 * ppm / 2 * r + baseY * r, xs[2]);
        Near(Y(8210) + 8 * ppm / 2 * r + baseY * r, ys[2]);
        Assert.False(Style(street).ContainsKey("letter-spacing"));

        // a label turned as a whole turns about its point; a style without labels writes none
        PlacedLabel[] turned = [new("postal", "12", -4000, 8300, 30, 30, "Bahnschrift", "bold", 0)];
        var text = Write(MapFrame.Standard, 7, "ja", [], Drawing(style, labels: turned), out names).Descendants(Svg + "text").Single();
        Assert.Equal(new[] { "番地" }, names);
        double ppm7 = Ppm(7);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"rotate(-30 {Math.Round((-4000 - WorldGrid.Left) * ppm7, 2)} {Math.Round((WorldGrid.Top - 8300) * ppm7, 2)})"),
            text.Attribute("transform")!.Value);
        Assert.Empty(Write(MapFrame.Standard, 6, "en", [], Drawing(MapStyle.Builtin("roadmap"), labels: labels), out _).Elements());
    }

    static ResolvedPoi Poi(PoiStyle style, string name, string label, double x, double y) =>
        new(new PoiPoint("1", "g", name, new Dictionary<string, string> { ["en"] = label }, x, y, null, null, null, null, null, null), style, PoiShow.Default, style.Color, style.Size, true, false);

    [Fact]
    public void APointOfInterestIsAGroupOfItsMarkAndItsLabel()
    {
        var style = MapStyle.Builtin("postalcodemap");
        double ppm = Ppm(6);
        double X(double x) => (x - WorldGrid.Left) * ppm;
        double Y(double y) => (WorldGrid.Top - y) * ppm;
        var (red, white, blue) = (new Rgb(0xd0, 0x20, 0x20), new Rgb(255, 255, 255), new Rgb(0x20, 0x40, 0xc0));
        var dot = new PoiStyle("dot", ItemName.Of("Dot"), "dot", red, 40, "bold", white, 2, null, true, null);
        var text = new PoiStyle("text", ItemName.Of("Text"), "text", red, 30, "normal", white, 2, null, false, null);
        var badge = new PoiStyle("badge", ItemName.Of("Badge"), "badge", white, 30, "bold", null, 0, blue, false, null);
        var icon = new PoiStyle("icon", ItemName.Of("Icon"), "icon", red, 48, "bold", white, 2, null, false, "hospital-box");
        var onCircle = icon with { Id = "circle", BadgeColor = blue };
        var photo = new PoiStyle("my icon", ItemName.Of("Photo"), "icon", red, 40, "bold", null, 0, null, false, null, Image: "poi-icons/a.png", ImageSha256: "00", ImageAspect: 2);
        ResolvedPoi[] pois =
        [
            Poi(dot, "the dot", "D", -4000, 8300), Poi(text, "", "T & t", -3900, 8300), Poi(badge, "", "B", -4000, 8200),
            Poi(icon, "", "", -3900, 8200), Poi(onCircle, "", "", -3800, 8200), Poi(photo, "", "", -3800, 8300),
            Poi(dot, "far", "F", 3000, -3000),                                  // outside the range
        ];
        var root = Write(MapFrame.Standard, 6, "ja", [], Drawing(style, pois: pois, pictures: new Dictionary<string, string> { ["my icon"] = "m-z6-svg/poi-my_icon.png" }), out var names);
        Assert.Equal(new[] { "POI" }, names);
        var groups = Group(root, "Points_of_interest").Elements(Svg + "g").ToList();
        // a group a point, under its name or else its label
        Assert.Equal(new[] { "the dot", "T & t", "B", "", "", "" }, groups.Select(g => g.Attribute(Inkscape + "label")!.Value));

        // a dot: its circle with its outline, and its label beside it to the right
        Assert.Equal(new[] { "circle", "text" }, groups[0].Elements().Select(e => e.Name.LocalName));
        var circle = groups[0].Element(Svg + "circle")!;
        Near(X(-4000), Num(circle, "cx"));
        Near(Y(8300), Num(circle, "cy"));
        Near(40 * ppm / 2, Num(circle, "r"));
        Assert.Equal((red.ToString(), white.ToString()), (circle.Attribute("fill")!.Value, circle.Attribute("stroke")!.Value));
        Near(2 * ppm, Num(circle, "stroke-width"));
        var beside = groups[0].Element(Svg + "text")!;
        Assert.Equal("D", beside.Value);
        var p = pois[0];
        Near((PoiLayout.LabelLeft(p) - WorldGrid.Left) * ppm, Num(beside, "x"));
        Near(p.LabelSize * ppm, double.Parse(Style(beside)["font-size"][..^2], CultureInfo.InvariantCulture));
        Assert.Equal(white.ToString(), Style(beside)["stroke"]);

        // a label as text, with its outline
        var plain = groups[1].Elements().Single();
        Assert.Equal(("text", "T & t", red.ToString(), white.ToString()), (plain.Name.LocalName, plain.Value, Style(plain)["fill"], Style(plain)["stroke"]));
        var (w, baseY) = Set("T & t", "Bahnschrift", false, 30, 0, 6);
        Near(X(-3900) - w / 2, Num(plain, "x"));
        Near(Y(8300) + baseY, Num(plain, "y"));

        // a badge: the circle around its label
        Assert.Equal(new[] { "circle", "text" }, groups[2].Elements().Select(e => e.Name.LocalName));
        Assert.Equal((blue.ToString(), null), (groups[2].Element(Svg + "circle")!.Attribute("fill")!.Value, groups[2].Element(Svg + "circle")!.Attribute("stroke")?.Value));
        (w, _) = Set("B", "Bahnschrift", true, 30, 0, 6);
        Assert.InRange(Num(groups[2].Element(Svg + "circle")!, "r"), w / 2, w / 2 + 30 * ppm);

        // an icon: its path in its box of 24, as high as the point's size, its outline under it
        var path = groups[3].Elements().Single();
        Assert.Equal(("path", MdiIcons.PathOf("hospital-box"), red.ToString(), white.ToString(), "stroke fill markers"),
            (path.Name.LocalName, path.Attribute("d")!.Value, path.Attribute("fill")!.Value, path.Attribute("stroke")!.Value, path.Attribute("paint-order")!.Value));
        double size = 48 * ppm, k = size / MdiIcons.Box;
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"translate({Math.Round(X(-3900) - size / 2, 2)} {Math.Round(Y(8200) - size / 2, 2)}) scale({Math.Round(k, 5)})"),
            path.Attribute("transform")!.Value);
        Near(2 * ppm / k, Num(path, "stroke-width"), 0.001);
        // over a circle: the outline goes around the circle, not around the icon
        Assert.Equal(new[] { "circle", "path" }, groups[4].Elements().Select(e => e.Name.LocalName));
        Assert.Equal(white.ToString(), groups[4].Element(Svg + "circle")!.Attribute("stroke")!.Value);
        Assert.Null(groups[4].Element(Svg + "path")!.Attribute("stroke"));
        Near(PoiLayout.Mark(pois[4]).W * ppm / 2, Num(groups[4].Element(Svg + "circle")!, "r"));
        // a PNG icon: the picture the file links, as wide as the PNG is to its height
        var image = groups[5].Elements().Single();
        Assert.Equal(("image", "m-z6-svg/poi-my_icon.png"), (image.Name.LocalName, image.Attribute(Xlink + "href")!.Value));
        Near(40 * ppm * 2, Num(image, "width"));
        Near(40 * ppm, Num(image, "height"));
        Near(X(-3800) - 40 * ppm, Num(image, "x"));
        Near(Y(8300) - 40 * ppm / 2, Num(image, "y"));
    }

    [Fact]
    public void AnIdIsTheNamesLettersAndDigits()
    {
        Assert.Equal("Shading_dark_side", SvgFile.Id("Shading (dark side)"));
        Assert.Equal("Points_of_interest", SvgFile.Id("Points of interest"));
        Assert.Equal("Roads", SvgFile.Id(" Roads "));
        Assert.Equal(MapLayers.All.Count, MapLayers.All.Select(l => SvgFile.Id(MapLayers.Name(l))).Distinct().Count());
    }
}
