using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.Core.Tests.Poi;

public sealed class PoiFilesTests
{
    static Dictionary<string, string> Label(string en, string? ja = null) => ja is null ? new() { ["en"] = en } : new() { ["en"] = en, ["ja"] = ja };

    [Fact]
    public void TheBundledPointsReadAndWriteAsTheyAre()
    {
        var set = PoiFiles.Read(PoiData.BundledPoints, null);
        Assert.Equal(new[] { "" }, set.Folders.Select(f => f.Path));
        Assert.Equal(new[] { "colored-dots", "highway-markers" }, set.Groups.Select(g => g.Path));
        var markers = set.Groups[1];
        Assert.Equal(("highway-marker", true, 26, "A", ""), (markers.Style, markers.Locked, markers.Points.Count, markers.Points[0].Label["en"], markers.Points[0].Name));
        Assert.Equal(ItemName.Bundled("Highway One markers", "Highway One マーカー"), markers.Name);
        Assert.Contains("Virus_City", markers.Credit);
        // the app's own writing of a bundled file says what it says (its bytes differ: order 0 is left out)
        foreach (var (path, text) in PoiData.BundledPoints)
        {
            var group = set.Groups.Single(g => g.Path + ".json" == path);
            var again = PoiFiles.Read([(path, PoiFiles.GroupText(group))], null).Groups.Single();
            Assert.Equal(PoiFiles.GroupText(group), PoiFiles.GroupText(again));
        }
        // the bundled styles as the screen shows them
        Assert.Equal(new[] { "highway-marker", "colored-dot", "facility" }, PoiFiles.ReadStyles(PoiData.BundledStylesText).Select(s => s.Id));

        // a copy keeps the names in the screens' language, one text from then on
        var copied = PoiFiles.Copied(set, "ja");
        Assert.Equal(new[] { ItemName.Of("色付きの丸"), ItemName.Of("Highway One マーカー") }, copied.Groups.Select(g => g.Name));
        Assert.Equal(new ItemName?[] { null }, copied.Folders.Select(f => f.Name));
        Assert.Equal(ItemName.Of("Colored dots"), PoiFiles.Copied(set, "en").Groups[0].Name);
        Assert.Contains("\"name\": \"色付きの丸\",", PoiFiles.GroupText(copied.Groups[0]));
        Assert.Contains("{ \"id\": \"A\", \"label\": { \"en\": \"A\" }, \"x\": 1033.1, \"y\": 330.18 }", PoiFiles.GroupText(copied.Groups[1]));
    }

    [Fact]
    public void AFolderGetsTheFilesOfTheSetAndKeepsWhatDidNotChange()
    {
        using var tmp = new TempFolder();
        var folder = tmp.File("poi");
        var bundled = PoiFiles.Copied(PoiFiles.Read(PoiData.BundledPoints, null), "en");
        var set = bundled with
        {
            Folders = [.. bundled.Folders, new PoiEditFolder("shops", ItemName.Of("店")), new PoiEditFolder("empty", ItemName.Of("Empty"))],
            Groups =
            [
                .. bundled.Groups,
                new PoiEditGroup("shops/garages", [new PoiEditPoint("1", "Garage by the pier", Label("Garage"), 10, 20, Color: "#123456")], ItemName.Of("ガレージ"), Style: "facility"),
            ],
        };
        var files = PoiFiles.Files(set, []);
        Assert.Equal(new[] { "colored-dots.json", "empty/_.json", "highway-markers.json", "shops/_.json", "shops/garages.json" }, files.Keys);
        var first = PoiFiles.WriteFolder(folder, files);
        Assert.Equal(5, first.Written.Count);
        var read = PoiData.Load(PoiData.ReadFolder(folder), folder, null, "");
        Assert.Equal(ItemName.Of("店"), read.Folders.Single(f => f.Path == "shops").Name);
        Assert.Equal(ItemName.Of("Highway One markers"), read.Groups.Single(g => g.Path == "highway-markers").Name);
        var garage = read.Resolve().Single(p => p.Point.Group == "shops/garages");
        Assert.Equal(("facility", "#123456", "Garage by the pier", "Garage"), (garage.Style.Id, garage.Color.ToString(), garage.Point.Name, garage.LabelIn("ja")));
        // the bundled points draw what they drew
        Assert.Equal(PoiData.Default.Resolve().Select(p => p.Drawn()), read.Resolve().Where(p => p.Point.Group != "shops/garages").Select(p => p.Drawn()));

        // the same set again writes nothing; a hand-written file that says the same keeps its comments
        File.WriteAllText(Path.Combine(folder, "shops", "garages.json"), "// ours\n" + PoiFiles.GroupText(set.Groups[^1]).Replace("\"format\": 1,", "\"format\": 1, \"order\": 0,"));
        var again = PoiFiles.WriteFolder(folder, PoiFiles.Files(set, PoiData.ReadFolder(folder).Select(e => e.Path)));
        Assert.Empty(again.Written);
        Assert.StartsWith("// ours", File.ReadAllText(Path.Combine(folder, "shops", "garages.json")));

        // a group moved into another folder, the empty folder gone, a point moved: the old file and folder are deleted
        File.WriteAllText(Path.Combine(folder, "shops", "notes.txt"), "kept");
        var moved = set with
        {
            Folders = [.. set.Folders.Where(f => f.Path != "empty"), new PoiEditFolder("jobs")],
            Groups = [.. bundled.Groups, set.Groups[^1] with { Path = "jobs/garages", Points = [new PoiEditPoint("1", "", Label("Garage"), 11, 20)] }],
        };
        var third = PoiFiles.WriteFolder(folder, PoiFiles.Files(moved, PoiData.ReadFolder(folder).Select(e => e.Path)));
        Assert.Equal(new[] { "jobs/garages.json" }, third.Written);
        Assert.Equal(new[] { "empty/_.json", "shops/garages.json" }, third.Deleted.Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(folder, "empty")));
        Assert.True(File.Exists(Path.Combine(folder, "shops", "notes.txt")));                  // a folder with other files stays
        Assert.Equal(11, PoiData.Load(PoiData.ReadFolder(folder), folder, null, "").Resolve().Single(p => p.Point.Group == "jobs/garages").Point.X);
    }

    [Fact]
    public void PathsAreCheckedForTheDisk()
    {
        var set = new PoiEditSet(
            [new PoiEditFolder(""), new PoiEditFolder("Shops"), new PoiEditFolder("shops"), new PoiEditFolder("bad:name"), new PoiEditFolder("lost/child")],
            [
                new PoiEditGroup("shops", []),
                new PoiEditGroup("Shops/_", []),
                new PoiEditGroup("Shops/a", [new PoiEditPoint("1", "", Label("A"), 0, 0), new PoiEditPoint("1", "", Label("B"), 0, 0)]),
                new PoiEditGroup("nowhere/b", []),
            ],
            [new PoiEditStyle("s", ItemName.Of("S"), "dot", "#000000", 10), new PoiEditStyle("s", ItemName.Of("S"), "dot", "#000000", 10)]);
        Assert.Equal(new[]
        {
            "folder 'bad:name': not a folder name for the disk",
            "folder 'lost/child': its folder 'lost' is missing",
            "group 'Shops/_': '_' is the name of a folder's settings",
            "group 'Shops/a': two points named '1'",
            "group 'nowhere/b': its folder 'nowhere' is missing",
            "two POI styles named 's'",
        }, PoiFiles.PathProblems(set).Where(p => !p.Contains("differ only in case")));
        Assert.Contains("'Shops', 'shops': names that differ only in case are one name on the disk", PoiFiles.PathProblems(set));
        Assert.Equal(("a_b_c", "CON_", "_1", "病院 (61)"), (PoiFiles.SafeName("a:b?c. "), PoiFiles.SafeName("CON"), PoiFiles.SafeName(" "), PoiFiles.SafeName("病院 (61)")));
    }

    [Fact]
    public void StylesAreWrittenOneALine()
    {
        var styles = new List<PoiEditStyle>
        {
            new("clinic", ItemName.Of("診療所"), "icon", "#d02020", 30, "bold", "#ffffff", 3, "#ffffff", true, "hospital-box", null, 14),
            new("name", ItemName.Bundled("Name", null), "text", "#202020", 12),
        };
        var text = PoiFiles.StylesText(styles);
        Assert.Equal("""
            {
              "format": 1,
              "styles": [
                { "id": "clinic", "name": "診療所", "look": "icon", "icon": "hospital-box", "color": "#d02020", "size": 30, "weight": "bold", "outline": "#ffffff", "outlineWidth": 3, "badgeColor": "#ffffff", "showLabel": true, "labelSize": 14 },
                { "id": "name", "name": { "en": "Name" }, "look": "text", "color": "#202020", "size": 12 }
              ]
            }

            """.Replace("\r\n", "\n"), text);
        Assert.Equal(text, PoiFiles.StylesText(PoiFiles.ReadStyles(text)));
        // and they read as POI styles
        Assert.Equal(new[] { "clinic", "name" }, PoiData.Load([], "poi", text, "styles").Styles.Skip(3).Select(s => s.Id));
    }

    [Fact]
    public void ACsvOrJsonFileGivesPointsAndTheLinesLeftOut()
    {
        var csv = PoiImport.Read("﻿label,labelJa,name,x,y,color,size,note\n"
            + "Clinic,診療所,Pillbox clinic,10.5,-20,#AABBCC,,first\n"
            + "\"Bank, Main St\",,,1e2,200,,30,\"two\nlines\"\n"
            + "Far,,,99999,0,,,\n"
            + "Bad,,,x,1,red,-3,\n"
            + ",,,,,,,\n"
            + ",,Only a name,1,2,,,", csv: true);
        Assert.Equal(new[] { ("1", "Clinic", "Pillbox clinic", 10.5, -20.0, "#aabbcc"), ("2", "Bank, Main St", "", 100.0, 200.0, (string?)null) },
            csv.Points.Take(2).Select(p => (p.Id, p.Label["en"], p.Name, p.X, p.Y, p.Color)));
        Assert.Equal(("診療所", 30.0, false), (csv.Points[0].Label["ja"], csv.Points[1].Size!.Value, csv.Points[1].Label.ContainsKey("ja")));
        Assert.Equal(("3", "Only a name", 0), (csv.Points[2].Id, csv.Points[2].Name, csv.Points[2].Label.Count));
        Assert.Equal(new[] { (5, "offMap (99999, 0)"), (6, "x, color (red), size") },
            csv.Skipped.Select(s => (s.Line, string.Join(", ", s.Reasons))));
        Assert.Equal("columns", PoiImport.Read("a,b\n1,2", csv: true).Skipped.Single().Reasons.Single().Code);

        var group = PoiImport.Read("""{"style": "facility", "points": [{"id": "7", "name": "The A", "label": {"en": "A", "ja": "エー", "fr": "Ah"}, "x": 1, "y": 2}, {"id": "7", "label": "B", "x": "3", "y": 4}, {"label": "C", "x": 1}]}""", csv: false);
        Assert.Equal(new[] { ("7", "A", "The A", 1.0), ("2", "B", "", 3.0) }, group.Points.Select(p => (p.Id, p.Label["en"], p.Name, p.X)));
        Assert.Equal(new[] { "en", "ja" }, group.Points[0].Label.Keys);
        Assert.Equal(new[] { (3, "y") }, group.Skipped.Select(s => (s.Line, string.Join(", ", s.Reasons))));
        var list = PoiImport.Read("""[{"label": {"en": "A"}, "x": 5, "y": 6, "color": "#ff0000", "size": 20}]""", csv: false);
        Assert.Equal(("1", "#ff0000", 20.0), (list.Points[0].Id, list.Points[0].Color, list.Points[0].Size!.Value));
        Assert.Equal("json", PoiImport.Read("{ oops", csv: false).Skipped.Single().Reasons.Single().Code);
    }

    [Fact]
    public void ASampleIsTheStyleDrawnAtThreeZooms()
    {
        var map = MapStyle.Builtin("postalcodemap");
        var style = new PoiStyle("clinic", ItemName.Of("Clinic"), "icon", new Rgb(0xd0, 0x20, 0x20), 40, "bold", new Rgb(255, 255, 255), 2, null, true, "hospital-box");
        var sizes = new[] { 8, 7, 6 }.Select(z =>
        {
            var rgba = FxMapGenerator.Core.Imaging.Images.DecodeRgba(PoiSample.Png(style, Label("Clinic"), "en", map, z), out int w, out int h);
            // some of it is the icon's colour-ish, the corners the map's background
            Assert.Equal((map.Background.R, map.Background.G, map.Background.B), (rgba[0], rgba[1], rgba[2]));
            return (w, h);
        }).ToList();
        Assert.True(sizes[0].w > sizes[0].h * 1.5);                                             // the label beside makes it wide
        // each zoom snaps the part to its own pixels: a quarter of zoom 8, a pixel or two more
        Assert.InRange(sizes[2].w, sizes[0].w / 4, sizes[0].w / 4 + 2);
        Assert.InRange(sizes[2].h, sizes[0].h / 4, sizes[0].h / 4 + 2);
        var markerLook = MapStyle.Builtin("postalcodemap");
        var marker = PoiData.Default.Resolve()[0];
        Assert.NotEmpty(PoiSample.Png(marker.Style, marker.Point.Label, "ja", markerLook, 6));
    }
}
