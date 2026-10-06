using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.Core.Tests.Poi;

public sealed class PoiTests
{
    const string Styles = """
        {"styles": [
          {"id": "red", "name": "Red", "look": "text", "color": "#ff0000", "size": 20},
          {"id": "dot", "name": "Dot", "look": "dot", "color": "#00ff00", "size": 30, "outline": "#ffffff", "outlineWidth": 2},
        ]}
        """;

    /// <summary>A POI folder of these files (path in the folder -> text) with the test styles.</summary>
    static PoiData Load(Dictionary<string, string> files, string styles = Styles) =>
        PoiData.Load(files.Select(kv => (kv.Key, kv.Value)), "poi", styles, "styles");

    [Fact]
    public void TheBundledPointsAreTheHighwayMarkersAndTheDotsLocked()
    {
        var all = PoiData.Default.Resolve();
        Assert.Equal(31, all.Count);
        Assert.Equal(new[] { "highway-markers", "colored-dots" }, PoiData.Default.Groups.Select(g => g.Path));
        var markers = all.Where(p => p.Point.Group == "highway-markers").ToList();
        Assert.Equal(Enumerable.Range('A', 26).Select(c => ((char)c).ToString()), markers.Select(m => m.LabelIn("ja")));
        Assert.All(markers, m => Assert.Equal("", m.Point.Name));
        Assert.All(markers, m => Assert.Equal(("highway-marker", "text", "#e02020", "bold"), (m.Style.Id, m.Style.Look, m.Color.ToString(), m.Style.Weight)));
        // the markers' text: 65 m (larger than on the postal code map picture, where it comes to 45.8 m)
        Assert.Equal(65, markers[0].Size);
        var dots = all.Where(p => p.Point.Group == "colored-dots").ToList();
        Assert.Equal(new[] { "#ff0000", "#f000ff", "#f6ff00", "#24ff00", "#006cff" }, dots.Select(d => d.Color.ToString()));
        Assert.Equal(new[] { 57.3, 58.7, 58.7, 58.7, 58.7 }, dots.Select(d => d.Size));
        Assert.All(all, p => Assert.True(p.Locked && p.Visible));
        Assert.All(all, p => Assert.Equal(PoiShow.Default, p.Show));
        Assert.Equal("", dots[0].LabelIn("ja"));
        // the facility names' style is there for the points a user adds
        Assert.Contains(PoiData.Default.Styles, s => s.Id == "facility" && s.Look == "text");
        // both groups name where they come from; their names and the styles' are in English and Japanese (the screens' language)
        Assert.All(PoiData.Default.Groups, g => Assert.Contains("Virus_City", g.Credit));
        Assert.Equal(ItemName.Bundled("Highway One markers", "Highway One マーカー"), PoiData.Default.Groups[0].Name);
        Assert.Equal(("色付きの丸", "Colored dots"), (PoiData.Default.Groups[1].Name.In("ja"), PoiData.Default.Groups[1].Name.In("en")));
        Assert.All(PoiData.Default.Styles, s => Assert.True(s.Name.IsBundled && s.Name.Ja is not null));
    }

    [Fact]
    public void ThePointTakesWhatItLacksFromItsGroupAndTheNearestFolderAbove()
    {
        var d = Load(new()
        {
            ["top/_.json"] = """{"name": "Top", "style": "red", "show": {"roadmap": true}}""",
            ["top/mid/_.json"] = """{"style": "dot"}""",
            ["top/mid/low.json"] = """
                {"show": {"atlas": false}, "locked": true, "points": [
                  {"id": "a", "name": "Ron at the harbour", "label": {"en": "Gas", "ja": "ガソリン"}, "x": 10, "y": 20},
                  {"id": "b", "label": {"en": "Bank"}, "x": 10, "y": 20, "style": "red", "color": "#123456", "size": 9, "locked": false}
                ]}
                """,
            ["top/off/_.json"] = """{"visible": false}""",
            ["top/off/shops.json"] = """{"points": [{"id": "c", "label": {"en": "Shop"}, "x": 10, "y": 20, "visible": true}]}""",
            ["top/parks.json"] = """{"order": -1, "points": [{"id": "d", "label": {"en": "Park"}, "x": 10, "y": 20, "visible": false, "show": {"atlas": true}}]}""",
        });
        var r = d.Resolve().ToDictionary(p => p.Point.Id);
        // a: the style from mid, the maps from its group low, locked by low
        Assert.Equal(("dot", "#00ff00", 30.0, new PoiShow(false, false), true, true), (r["a"].Style.Id, r["a"].Color.ToString(), r["a"].Size, r["a"].Show, r["a"].Visible, r["a"].Locked));
        // the label per language (English where a language has none); the name is the lists' only
        Assert.Equal(("ガソリン", "Gas", "Bank", "Bank"), (r["a"].LabelIn("ja"), r["a"].LabelIn("en"), r["b"].LabelIn("ja"), r["b"].LabelIn("en")));
        Assert.Equal(("Ron at the harbour", ""), (r["a"].Point.Name, r["b"].Point.Name));
        // b: its own style, colour and size; a locked group locks it all the same
        Assert.Equal(("red", "#123456", 9.0, true), (r["b"].Style.Id, r["b"].Color.ToString(), r["b"].Size, r["b"].Locked));
        // c: a hidden folder hides it all the same; the maps from top (the other keys at their defaults)
        Assert.Equal((false, new PoiShow(true, true), false), (r["c"].Visible, r["c"].Show, r["c"].Locked));
        // d: hidden by itself; its own maps
        Assert.Equal((false, new PoiShow(true, false)), (r["d"].Visible, r["d"].Show));

        // the folders on disk, each before those in it; groups and folders by order, then name
        Assert.Equal(new[] { "", "top", "top/mid", "top/off" }, d.Folders.Select(f => f.Path));
        Assert.Equal(new[] { "top/parks", "top/mid/low", "top/off/shops" }, d.Groups.Select(g => g.Path));
        Assert.Equal(new[] { "d", "a", "b", "c" }, d.Resolve().Select(p => p.Point.Id));
        // a name left out is the folder's or the file's own
        Assert.Equal((ItemName.Of("Top"), ItemName.Of("mid"), ItemName.Of("low")), (d.Folders[1].Name, d.Folders[2].Name, d.Groups[1].Name));
    }

    [Fact]
    public void PointsThatCannotBeDrawnAreRefusedWithEveryReason()
    {
        var e = Assert.Throws<PoiException>(() => Load(new()
        {
            ["a/_.json"] = """{"style": "blue", "name": 5}""",
            ["a/one.json"] = """
                {"points": [
                  {"id": "p", "label": {"en": "P"}, "x": 0, "y": 0},
                  {"id": "p", "label": {"en": "P"}, "x": 99999, "y": 0},
                  {"id": "q", "name": {"en": "Q"}, "label": {"en": "Q", "fr": "Q"}, "x": 0, "y": 0, "style": "green", "color": "red"},
                  {"id": "r", "label": "R", "x": 0, "y": 0}
                ]}
                """,
            ["b.json"] = """{"order": 1.5, "visible": "yes", "points": {}}""",
        }));
        Assert.Equal("poi/a/_.json: 'name' must be a text; poi/a/_.json: no POI style 'blue'" +
            "; poi/a/one.json: point 'q': 'name' must be a text; poi/a/one.json: point 'q': a label in 'fr' (en, ja); poi/a/one.json: 'color' of 'q' is not a colour (#rrggbb)" +
            "; poi/a/one.json: point 'r': 'label' must be { \"en\", \"ja\" }; poi/a/one.json: two points named 'p'" +
            "; poi/a/one.json: point 'p' (99999, 0) is off the map; poi/a/one.json: point 'q': no POI style 'green'" +
            "; poi/b.json: 'order' must be a whole number; poi/b.json: 'visible' must be true or false; poi/b.json: 'points' must be a list",
            e.Message);
        // a point that neither its group nor a folder gives a style
        var none = Assert.Throws<PoiException>(() => Load(new() { ["a/one.json"] = """{"points": [{"id": "p", "label": {"en": "P"}, "x": 0, "y": 0}]}""" }));
        Assert.Equal("poi/a/one.json: point 'p': neither it, its group nor a folder above names a POI style", none.Message);
        // files that are not a group
        var bad = Assert.Throws<PoiException>(() => Load(new() { ["x.json"] = "{ oops", ["y.json"] = "[]" }));
        Assert.StartsWith("poi/x.json: not JSON (", bad.Message);
        Assert.EndsWith("; poi/y.json: not a JSON object; poi/x.json: 'points' must be a list; poi/y.json: 'points' must be a list", bad.Message);
        // the project's styles: what cannot be drawn yet, and an id of a bundled style
        var style = Assert.Throws<PoiException>(() => Load(new(), """
            {"styles": [
              {"id": "s", "name": "S", "look": "icon", "color": "#000000", "size": 0, "weight": "heavy", "showLabel": true},
              {"id": "facility", "name": "F", "look": "text", "color": "#000000", "size": 10},
              {"id": "t", "look": "text", "color": "#000000", "size": 10}
            ]}
            """));
        Assert.Equal("styles: style 's': weight 'heavy' (normal or bold); styles: style 's': an icon style names an MDI icon ('icon') or a PNG ('image'), one of them; "
            + "styles: 'size' must be a positive number (s); styles: 't': 'name' must be a text; styles: style 'facility' is a bundled style (give yours another id)", style.Message);
    }

    [Fact]
    public void AProjectReadsItsOwnFolderAndStyles()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("t.fxmapgen.json"));
        Assert.Equal(31, PoiData.Of(project).Points.Count());
        Directory.CreateDirectory(tmp.File("poi/shops"));
        File.WriteAllText(tmp.File("poi/shops/_.json"), """{"name": "店", "style": "facility"}""");
        File.WriteAllText(tmp.File("poi/shops/stores.json"), """{"points": [{"id": "1", "label": {"en": "24/7"}, "x": 25, "y": -1350}]}""");
        File.WriteAllText(tmp.File("poi/notes.txt"), "not read");
        project.File.Poi = "poi";
        var data = PoiData.Of(project);
        var shop = data.Resolve().Single();
        Assert.Equal(("facility", new Rgb(0x20, 0x20, 0x20), false, "shops/stores"), (shop.Style.Id, shop.Color, shop.Locked, shop.Point.Group));
        Assert.Equal(ItemName.Of("店"), data.Folders.Single(f => f.Path == "shops").Name);
        // the record's hash follows what the maps draw: a point moved, not a credit, a group's or a point's name or a comment
        var sha = PoiData.Sha256(project);
        File.WriteAllText(tmp.File("poi/shops/stores.json"), """{"name": "Stores", "credit": "Our list", "points": [/* the one */ {"id": "1", "name": "The one by the pier", "label": {"en": "24/7", "ja": ""}, "x": 25, "y": -1350}]}""");
        Assert.Equal(sha, PoiData.Sha256(project));
        Assert.Equal("Our list", PoiData.Of(project).Groups.Single().Credit);
        File.WriteAllText(tmp.File("poi/shops/stores.json"), """{"points": [{"id": "1", "label": {"en": "24/7"}, "x": 26, "y": -1350}]}""");
        Assert.NotEqual(sha, PoiData.Sha256(project));
        // the project's styles come on top of the bundled ones
        File.WriteAllText(tmp.File("styles.json"), """{"styles": [{"id": "mine", "name": "Mine", "look": "dot", "color": "#0000ff", "size": 20}]}""");
        project.File.PoiStyles = "styles.json";
        Assert.Equal(new[] { "highway-marker", "colored-dot", "facility", "mine" }, PoiData.Of(project).Styles.Select(s => s.Id));
        project.File.PoiStyles = "missing.json";
        Assert.Contains("missing.json: not found", Assert.Throws<PoiException>(() => PoiData.Of(project)).Message);
        project.File.PoiStyles = null;
        project.File.Poi = "nowhere";
        Assert.Contains("nowhere: folder not found", Assert.Throws<PoiException>(() => PoiData.Of(project)).Message);
        project.File.Poi = " ";
        Assert.Contains("the points of interest folder is empty (null for the bundled ones)", project.Validate());
    }

    /// <summary>An opaque grey PNG of the given size (white unless told).</summary>
    internal static void Png(string path, int w, int h, byte grey = 255)
    {
        var rgba = new byte[w * h * 4];
        Array.Fill(rgba, grey);
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        FxMapGenerator.Core.Imaging.Images.SavePng(path, rgba, w, h);
    }

    [Fact]
    public void IconStylesNameAnMdiIconOrAPngInTheFolderOfTheirFile()
    {
        using var tmp = new TempFolder();
        Directory.CreateDirectory(tmp.File("poi-icons"));
        Png(tmp.File("poi-icons/garage.png"), 40, 20);
        File.WriteAllText(tmp.File("poi-icons/notes.png"), "not a picture");
        var ok = PoiData.Load([("shops.json", """{"points": [{"id": "1", "label": {"en": "Clinic"}, "x": 0, "y": 0, "style": "clinic"}, {"id": "2", "label": {"en": "Garage"}, "x": 0, "y": 0, "style": "garage"}]}""")],
            "poi", """
            {"styles": [
              {"id": "clinic", "name": "Clinic", "look": "icon", "icon": "hospital-box", "color": "#d02020", "size": 30, "badgeColor": "#ffffff", "showLabel": true},
              {"id": "garage", "name": "Garage", "look": "icon", "image": "poi-icons/garage.png", "color": "#000000", "size": 20, "showLabel": true, "labelSize": 15}
            ]}
            """, "styles", tmp.Path);
        var r = ok.Resolve().ToDictionary(p => p.Point.Id);
        Assert.Equal(("icon", "hospital-box", true, 18.0), (r["1"].Style.Look, r["1"].Style.Icon, r["1"].Style.ShowLabel, r["1"].LabelSize));   // 0.6 of 30
        Assert.Equal((2.0, 15.0, 64), (r["2"].Style.ImageAspect, r["2"].LabelSize, r["2"].Style.ImageSha256!.Length));
        // the mark: a circle 1.5 times the icon behind the MDI icon; the PNG in its own shape
        Assert.Equal((45.0, 45.0), PoiLayout.Mark(r["1"]));
        Assert.Equal((40.0, 20.0), PoiLayout.Mark(r["2"]));
        Assert.Equal(22.5 + 0.3 * 18, PoiLayout.LabelLeft(r["1"]) - r["1"].Point.X, 9);

        // what cannot be drawn, with every reason
        var e = Assert.Throws<PoiException>(() => PoiData.Load([], "poi", """
            {"styles": [
              {"id": "a", "name": "A", "look": "icon", "icon": "no-such-icon", "color": "#000000", "size": 10},
              {"id": "b", "name": "B", "look": "icon", "icon": "store", "image": "poi-icons/garage.png", "color": "#000000", "size": 10},
              {"id": "c", "name": "C", "look": "icon", "image": "../outside.png", "color": "#000000", "size": 10},
              {"id": "d", "name": "D", "look": "icon", "image": "poi-icons/missing.png", "color": "#000000", "size": 10},
              {"id": "e", "name": "E", "look": "icon", "image": "poi-icons/notes.png", "color": "#000000", "size": 10},
              {"id": "f", "name": "F", "look": "text", "icon": "store", "color": "#000000", "size": 10, "showLabel": true, "labelSize": -1}
            ]}
            """, "styles", tmp.Path));
        Assert.Equal("styles: style 'a': no MDI icon 'no-such-icon'; "
            + "styles: style 'b': an icon style names an MDI icon ('icon') or a PNG ('image'), one of them; "
            + "styles: style 'c': 'image' must be a file in the folder of the POI styles file (../outside.png); "
            + "styles: style 'd': image not found: poi-icons/missing.png; "
            + "styles: style 'e': poi-icons/notes.png is not a PNG; "
            + "styles: style 'f': 'icon' and 'image' are for the look icon; styles: style 'f': 'showLabel' is for the looks dot and icon; "
            + "styles: 'labelSize' must be a positive number (f)", e.Message);
        // without the styles file's folder a PNG cannot be found
        Assert.Contains("'image' must be a file in the folder of the POI styles file",
            Assert.Throws<PoiException>(() => PoiData.Load([], "poi", """{"styles": [{"id": "g", "name": "G", "look": "icon", "image": "a.png", "color": "#000000", "size": 10}]}""", "styles")).Message);
    }

    [Fact]
    public void TheRecordsFollowWhatIsDrawnOnly()
    {
        using var tmp = new TempFolder();
        Directory.CreateDirectory(tmp.File("poi-icons"));
        Png(tmp.File("poi-icons/a.png"), 10, 10);
        const string styles = """{"styles": [{"id": "pic", "name": "Picture", "look": "icon", "image": "poi-icons/a.png", "color": "#000000", "size": 10}]}""";
        string Drawn(string group, string point = """{"id": "1", "label": {"en": "A"}, "x": 5, "y": 6}""") =>
            string.Join("\n", PoiData.Load([(group, $$"""{"style": "pic", "points": [{{point}}]}""")], "poi", styles, "styles", tmp.Path)
                .Resolve().Select(p => p.Drawn()));
        var before = Drawn("a.json");
        // the same point in another group (file) or folder draws the same; so does it with a name, or an empty Japanese label
        Assert.Equal(before, Drawn("folder/b.json"));
        Assert.Equal(before, Drawn("a.json", """{"id": "2", "name": "The clinic", "label": {"en": "A", "ja": ""}, "x": 5, "y": 6}"""));
        // a Japanese label draws on the Japanese maps
        Assert.NotEqual(before, Drawn("a.json", """{"id": "1", "label": {"en": "A", "ja": "エー"}, "x": 5, "y": 6}"""));
        // another picture under the same name draws again
        Png(tmp.File("poi-icons/a.png"), 10, 20);
        Assert.NotEqual(before, Drawn("a.json"));
        // the bundled points' text is what it was before icons, but for the maps they show on (atlas, road map)
        var marker = PoiData.Default.Resolve()[0];
        Assert.Equal("en=A,ja=\t1033.1\t330.18\ttext\t#e02020\t65\tbold\t#ffffff\t5\t#d42a2a\tFalse\t-\tTrueFalse\tTrue", marker.Drawn());
    }
}
