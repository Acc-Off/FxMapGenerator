using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.RoadEdits;

public sealed class RoadEditsTests
{
    // ------------------------------------------------------------------------------------------------ the file

    const string Sample = """
        {
          "format": 1,
          "nodes": {
            "added:1": {"x": 324.5, "y": 3601.25, "z": 48.3, "street": 0, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false},
            "784:40": {"original": {"x": 310.125, "y": 3590.5, "z": 31.25}},
            "752:12": {"hidden": true, "original": {"x": 1210.125, "y": 3980.5, "z": 30}},
            "431:205": {"street": 1234, "original": {"x": 214, "y": -790.5, "z": 30.125, "street": 0}},
            "431:206": {"x": 216.5, "y": -801, "original": {"x": 215.25, "y": -800.5, "z": 30.125}},
            "752:13": {"original": {"x": 1220, "y": 3990, "z": 30}},
            "752:14": {"original": {"x": 1230, "y": 4000, "z": 30}}
          },
          "links": [
            {"from": "784:40", "to": "added:1", "lanesForward": 1, "lanesBack": 1, "narrow": false, "width": null},
            {"from": "431:205", "to": "431:206", "lanesForward": 1, "width": 9.5, "original": {"lanesForward": 2, "lanesBack": 1, "narrow": false}},
            {"from": "752:13", "to": "752:14", "hidden": true, "original": {"lanesForward": 1, "lanesBack": 1, "narrow": false}}
          ]
        }

        """;

    static string Lf(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void TheFileIsReadAndWrittenBackTheSame()
    {
        var text = Lf(Sample);
        var set = RoadEditsFile.Parse(Encoding.UTF8.GetBytes(text), "sample");
        Assert.Equal((7, 3), (set.Nodes.Count, set.Links.Count));
        Assert.True(set.Nodes[0].IsAdded);
        Assert.Equal((324.5, 3601.25), set.Nodes[0].Position);
        Assert.True(set.Nodes[2].Hidden);
        Assert.Equal((216.5, -801.0), set.Nodes[4].Position);
        Assert.Equal(1234u, set.Nodes[3].Set.Street);
        Assert.True(set.Links[0].IsAdded);
        Assert.Null(set.Links[0].Width);
        Assert.Equal((1, (int?)null, 9.5), (set.Links[1].Set.LanesForward, set.Links[1].Set.LanesBack, set.Links[1].Width));
        Assert.Equal(Lf(text), RoadEditsFile.Format(set));
        Assert.Equal("{\n  \"format\": 1,\n  \"nodes\": {},\n  \"links\": []\n}\n", RoadEditsFile.Format(RoadEditSet.Empty));

        using var tmp = new TempFolder();
        var path = tmp.File(RoadEditsFile.FileName);
        RoadEditsFile.Write(path, set);
        Assert.Equal(Lf(text), File.ReadAllText(path));
        Assert.Equal(set.Nodes, RoadEditsFile.Read(path).Nodes);
        // positions at 0.001 m
        var fine = new RoadEditSet([new NodeEdit("added:2", new NodeValues(1.23456, -2.0004, 3, 0, false, false, false, false))], []);
        Assert.Contains("\"x\": 1.235, \"y\": -2, \"z\": 3,", RoadEditsFile.Format(fine));
    }

    [Fact]
    public void EveryProblemOfAFileIsNamed()
    {
        const string bad = """
            {
              "format": 1,
              "extra": 1,
              "nodes": {
                "added:1": {"x": 0, "y": 0, "z": 0},
                "added:2": {"x": 0, "y": 0, "z": 0, "street": 0, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false, "hidden": true},
                "1:1": {"x": 5},
                "1:2": {"street": 7, "original": {"x": 0, "y": 0, "z": 0}},
                "1:3": {"hidden": true, "x": 1, "original": {"x": 0, "y": 0, "z": 0}},
                "1:4": {"colour": "red", "original": {"x": 0, "y": 0, "z": 0}},
                "1:5": {"x": "far", "original": {"x": 0, "y": 0, "z": 0}},
                "1:6": {"original": {"x": 0, "y": 0, "z": 0}},
                "9x": {"original": {"x": 0, "y": 0, "z": 0}},
                "added:3": {"x": 99999, "y": 0, "z": 0, "street": 0, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false}
              },
              // a comment and a trailing comma are fine
              "links": [
                {"from": "1:2", "to": "1:9", "lanesForward": 1, "lanesBack": 1, "narrow": false},
                {"from": "1:2", "to": "1:3", "hidden": true, "original": {"lanesForward": 1, "lanesBack": 1, "narrow": false}},
                {"from": "1:2", "to": "added:3", "lanesForward": 8, "lanesBack": 0, "narrow": false, "width": 0},
                {"from": "added:3", "to": "1:2", "lanesForward": 1, "lanesBack": 0, "narrow": false},
                {"from": "1:2", "to": "1:5", "lanesForward": 1, "lanesBack": 1, "narrow": false},
                {"from": "1:2", "to": "1:4", "lanesForward": 0, "lanesBack": 0, "narrow": false},
                {"from": "1:6", "to": "added:3", "width": 5, "original": {"lanesForward": 1}},
              ]
            }
            """;
        var ex = Assert.Throws<RoadEditsException>(() => RoadEditsFile.Parse(Encoding.UTF8.GetBytes(bad), "bad.json"));
        foreach (var part in new[]
        {
            "bad.json: ", "unknown field 'extra'", "node 1:4: unknown field 'colour'", "node 1:5: 'x' must be a number",
            "node added:1: an added node needs street, highway, tunnel, unpaved, switchedOff",
            "node added:2: an added node is removed, not hidden",
            "node 1:1: a node of the game needs its original position",
            "node 1:2: changes street without the original value",
            "node 1:3: a hidden node changes nothing else",
            "node key '9x'",
            "node added:3 is off the map",
            "link 1:2 1:9: node 1:9 is not in the nodes",
            "link 1:2 1:3: node 1:3 is hidden",
            "link 1:2 added:3: lanesForward 8 (0 to 7)", "link 1:2 added:3: width 0 m",
            "link added:3 1:2 is listed twice",
            "link 1:2 1:4 has no lanes in either direction",
            "link 1:6 added:3: a link of the game needs its original lanesForward, lanesBack and narrow",
            "link 1:6 added:3: a link to an added node is added",
        })
            Assert.Contains(part, ex.Message);
        Assert.DoesNotContain("node 1:5 is not in the nodes", ex.Message);         // it is listed, only unreadable
        Assert.Contains("is not valid JSON", Assert.Throws<RoadEditsException>(() => RoadEditsFile.Parse("{"u8, "x")).Message);
        Assert.Contains("format 2 was written by a newer FxMapGenerator",
            Assert.Throws<RoadEditsException>(() => RoadEditsFile.Parse("{\"format\": 2, \"nodes\": {}, \"links\": []}"u8, "x")).Message);
        Assert.Throws<RoadEditsException>(() => RoadEditsFile.Write(Path.Combine(Path.GetTempPath(), "never.json"),
            new RoadEditSet([new NodeEdit("added:1", new NodeValues(0, 0, 0))], [])));
    }

    [Fact]
    public void StreetNamesTheEditsAddAreReadWrittenAndGoWithThePaths()
    {
        const string text = """
            {
              "format": 1,
              "streets": {
                "3001": {"en": "Island Rd", "ja": "島通り"},
                "3002": {"en": "Pier Way"}
              },
              "nodes": {
                "added:1": {"x": 300, "y": 0, "z": 12, "street": 3001, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false},
                "1:2": {"street": 3002, "original": {"x": 100, "y": 0, "z": 10, "street": 0}}
              },
              "links": []
            }

            """;
        var set = RoadEditsFile.Parse(Encoding.UTF8.GetBytes(Lf(text)), "names");
        Assert.Equal(new[] { new StreetName(3001, "Island Rd", "島通り"), new StreetName(3002, "Pier Way", null) }, set.Streets);
        Assert.Equal(Lf(text), RoadEditsFile.Format(set));                                       // the Japanese name as it is, not escaped
        Assert.Null(RoadEditsFile.Parse("""{"format": 1, "streets": {"5": {"en": "A", "ja": ""}}, "nodes": {}, "links": []}"""u8, "x").Streets[0].Ja);
        var ex = Assert.Throws<RoadEditsException>(() => RoadEditsFile.Parse("""
            {"format": 1, "streets": {"0": {"en": "Zero"}, "x1": {"en": "Bad"}, "7": {"ja": "名前だけ"}, "8": {"en": " "}, "6": {"en": "A", "colour": 1},
                                      "9": {"en": "A"}, "9": {"en": "B"}},
             "nodes": {}, "links": []}
            """u8, "s"));
        foreach (var part in new[]
        {
            "street name 0: 0 is no street name", "street name key 'x1'", "street name 7: 'en' must be the English name", "street name 8: the English name is empty",
            "street name 6: unknown field 'colour'", "street name 9 is listed twice",
        })
            Assert.Contains(part, ex.Message);

        // applied: the path file carries the names (a copy too), and the road shapes' graph knows them beside the game's
        var paths = Paths();
        set.ApplyTo(paths);
        Assert.Equal(("Island Rd", "島通り"), paths.Streets[3001]);
        Assert.Equal(("Pier Way", (string?)null), paths.Clone().Streets[3002]);
        using var tmp = new TempFolder();
        var names = tmp.File("names.json");
        File.WriteAllText(names, "{\"streets\": {\"5\": {\"en\": \"Game St\"}}, \"zones\": {}}");
        var g = RoadNet.Of(paths, names);
        Assert.Equal(("Island Rd", "Pier Way", "Game St"), (g.StreetNames[3001], g.Street(g.Nodes["1:2"]), g.StreetNames[5]));
        // the labels' names: the game's and these (the Japanese name, else the English one)
        var game = new GameNames
        {
            Streets = new Dictionary<uint, IReadOnlyDictionary<string, string>> { [5] = new Dictionary<string, string> { ["en"] = "Game St" } },
            Zones = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        };
        var all = game.WithStreets(set.Streets.Select(s => (s.Hash, s.En, s.Ja)));
        Assert.Equal(("島通り", "Pier Way", "Island Rd", "Game St"), (all.Street(3001, "ja"), all.Street(3002, "ja"), all.Street(3001, "en"), all.Street(5, "ja")));
        Assert.Null(game.Street(3001, "en"));
    }

    [Fact]
    public void GroupsAreReadAndWrittenAndChangeNothingOfThePaths()
    {
        const string text = """
            {
              "format": 1,
              "groups": {
                "islandRunway": {"en": "Island Runway", "ja": "島の滑走路"},
                "pier-2": "桟橋"
              },
              "nodes": {
                "added:1": {"x": 300, "y": 0, "z": 12, "street": 0, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false, "group": "islandRunway"},
                "1:2": {"original": {"x": 100, "y": 0, "z": 10}, "group": "islandRunway"},
                "1:3": {"x": 205, "original": {"x": 200, "y": 0, "z": 10}},
                "2:1": {"hidden": true, "original": {"x": 0, "y": 100, "z": 5}, "group": "pier-2"}
              },
              "links": [
                {"from": "1:2", "to": "added:1", "lanesForward": 1, "lanesBack": 1, "narrow": false, "width": 35, "group": "islandRunway"},
                {"from": "1:2", "to": "1:3", "width": 20, "original": {"lanesForward": 2, "lanesBack": 1, "narrow": false}, "group": "pier-2"}
              ]
            }

            """;
        var set = RoadEditsFile.Parse(Encoding.UTF8.GetBytes(Lf(text)), "groups");
        // a group's name: one text, or the English and Japanese names the bundled groups have
        Assert.Equal(new[] { new EditGroup("islandRunway", ItemName.Bundled("Island Runway", "島の滑走路")), new EditGroup("pier-2", ItemName.Of("桟橋")) }, set.Groups);
        Assert.Equal(new[] { ItemName.Of("島の滑走路"), ItemName.Of("桟橋") }, RoadEditsFile.Copied(set, "ja").Groups.Select(g => g.Name));
        Assert.Contains("\"islandRunway\": \"Island Runway\"", RoadEditsFile.Format(RoadEditsFile.Copied(set, "en")));
        Assert.Equal(new[] { "islandRunway", "islandRunway", null, "pier-2" }, set.Nodes.Select(n => n.Group));
        Assert.Equal(new[] { "islandRunway", "pier-2" }, set.Links.Select(l => l.Group));
        Assert.Equal(Lf(text), RoadEditsFile.Format(set));
        // the paths come out the same as without the groups
        var ungrouped = new RoadEditSet(set.Nodes.Select(n => n with { Group = null }).ToList(), set.Links.Select(l => l with { Group = null }).ToList());
        Assert.DoesNotContain("group", RoadEditsFile.Format(ungrouped));
        PathFile a = Paths(), b = Paths();
        Assert.Equal(set.ApplyTo(a), ungrouped.ApplyTo(b), new AppliedComparer());
        Assert.Equal(JsonSerializer.Serialize(a.Nodes), JsonSerializer.Serialize(b.Nodes));
        Assert.Equal(JsonSerializer.Serialize(a.Links), JsonSerializer.Serialize(b.Links));

        var ex = Assert.Throws<RoadEditsException>(() => RoadEditsFile.Parse("""
            {"format": 1, "groups": {"-x": {"en": "Dash"}, "a": {"ja": "名前だけ"}, "b": {"en": " "}, "c": {"en": "C", "colour": 1}, "d": {"en": "D"}, "d": {"en": "D2"}, "e": 5, "f": " "},
             "nodes": {"added:1": {"x": 0, "y": 0, "z": 0, "street": 0, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false, "group": "zz"},
                       "added:2": {"x": 1, "y": 0, "z": 0, "street": 0, "highway": false, "tunnel": false, "unpaved": false, "switchedOff": false, "group": 5}},
             "links": [{"from": "added:1", "to": "added:2", "lanesForward": 1, "lanesBack": 1, "narrow": false, "group": "yy"}]}
            """u8, "g"));
        foreach (var part in new[]
        {
            "group id '-x'", "group a: 'en' must be the English name", "group b: the name is empty", "group c: unknown field 'colour'",
            "group d is listed twice", "group e: the name must be a text", "group f: the name is empty", "node added:1: group 'zz' is not in the groups",
            "node added:2: 'group' must be the id of a group", "link added:1 added:2: group 'yy' is not in the groups",
        })
            Assert.Contains(part, ex.Message);
    }

    [Fact]
    public void AGroupThatAppliesWhereFoundLeavesOutWhatDoesNotFitWithoutListingIt()
    {
        const string text = """
            {
              "format": 1,
              "groups": {
                "townInTheSky": {"en": "Town in the sky (hidden)", "ja": "空の町（非表示）", "whereFound": true},
                "mine": "自分の編集"
              },
              "nodes": {
                "2:1": {"hidden": true, "original": {"x": 0, "y": 100, "z": 5}, "group": "townInTheSky"},
                "2:2": {"hidden": true, "original": {"x": 100, "y": 100, "z": 115}, "group": "townInTheSky"},
                "9:9": {"hidden": true, "original": {"x": 0, "y": 0, "z": 110}, "group": "townInTheSky"},
                "9:8": {"hidden": true, "original": {"x": 0, "y": 0, "z": 110}, "group": "mine"}
              },
              "links": []
            }

            """;
        var set = RoadEditsFile.Parse(Encoding.UTF8.GetBytes(Lf(text)), "where found");
        Assert.Equal(new[] { true, false }, set.Groups.Select(g => g.WhereFound));
        Assert.Equal(Lf(text), RoadEditsFile.Format(set));
        // a copy keeps the mark with the one name
        var copy = RoadEditsFile.Format(RoadEditsFile.Copied(set, "ja"));
        Assert.Contains("\"townInTheSky\": {\"name\": \"空の町（非表示）\", \"whereFound\": true}", copy);
        Assert.Equal(new EditGroup("townInTheSky", ItemName.Of("空の町（非表示）"), true), RoadEditsFile.Parse(Encoding.UTF8.GetBytes(copy), "copy").Groups[0]);

        // 2:1 is hidden; 2:2 is another node now (as where Cayo Perico's file takes an area's place) and 9:9 is not read
        // (an area outside the frame): left out, and only the edit of the other group is listed
        var paths = Paths();
        var a = set.ApplyTo(paths);
        Assert.Equal(1, a.NodesHidden);
        Assert.Equal(new[] { "node 9:8 nodeMissing" }, a.NotApplied.Select(n => $"{n.Kind} {n.Key} {n.Reason}"));
        Assert.Equal(new[] { "1:1", "1:2", "1:3", "2:2" }, paths.Nodes.Select(n => n.Key));
        Assert.DoesNotContain(paths.Links, l => l.From == "2:1" || l.To == "2:1");
        Assert.Equal(a.NotApplied, set.NotAppliedTo(Paths()));

        var ex = Assert.Throws<RoadEditsException>(() => RoadEditsFile.Parse("""
            {"format": 1, "groups": {"a": {"name": 5}, "b": {"name": "B", "en": "B"}, "c": {"en": "C", "whereFound": "yes"}, "d": {"name": " ", "whereFound": true}},
             "nodes": {}, "links": []}
            """u8, "g"));
        foreach (var part in new[] { "group a: 'name' must be a text", "group b: unknown field 'en'", "group c: 'whereFound' must be true or false", "group d: the name is empty" })
            Assert.Contains(part, ex.Message);
    }

    /// <summary>Two results of applying edits alike (the lists compared by their items).</summary>
    sealed class AppliedComparer : IEqualityComparer<AppliedEdits>
    {
        public bool Equals(AppliedEdits? x, AppliedEdits? y) => x is not null && y is not null && x with { NotApplied = [] } == y with { NotApplied = [] } && x.NotApplied.SequenceEqual(y.NotApplied);
        public int GetHashCode(AppliedEdits obj) => 0;
    }

    [Fact]
    public void TheBundledEditsAreUsableAndEveryItemBelongsToANamedGroup()
    {
        var set = RoadEditsFile.Bundled;
        // written the way the app writes edits (a new project's copy reads the same as the screen would save it)
        Assert.Equal(RoadEditsFile.Format(set), Encoding.UTF8.GetString(RoadEditsFile.BundledBytes));
        Assert.Equal(set.Nodes, RoadEditsFile.Parse(RoadEditsFile.BundledBytes, "again").Nodes);
        var groups = set.Groups.ToDictionary(g => g.Id);
        Assert.All(set.Groups, g => Assert.True(g.Name.IsBundled && !string.IsNullOrWhiteSpace(g.Name.En) && !string.IsNullOrWhiteSpace(g.Name.Ja)));
        Assert.All(set.Nodes, n => Assert.Contains(n.Group ?? "", groups.Keys));
        Assert.All(set.Links, l => Assert.Contains(l.Group ?? "", groups.Keys));
        // every group holds something
        Assert.All(set.Groups, g => Assert.True(set.Nodes.Any(n => n.Group == g.Id) || set.Links.Any(l => l.Group == g.Id)));
        // North Yankton's roads (the prologue's town, 77 to 117 m over the sea south-east of the map): every node of its 11 areas
        // hidden, wherever a project's path data holds it
        Assert.Equal(new[] { "northYankton", "cayoPerico" }, set.Groups.Where(g => g.WhereFound).Select(g => g.Id));
        var yankton = groups["northYankton"];
        var hidden = set.Nodes.Where(n => n.Group == yankton.Id).ToList();
        Assert.Equal(395, hidden.Count);
        Assert.All(hidden, n => Assert.True(n.Hidden && !n.IsAdded && n.Original!.Z > 60 && n.Original.X > 2800 && n.Original.Y < -4200));
        Assert.Equal(new[] { 184, 185, 186, 187, 213, 214, 215, 216, 217, 218, 247 }, hidden.Select(n => int.Parse(n.Key.Split(':')[0])).Distinct().Order());
        Assert.DoesNotContain(set.Links, l => l.Group == yankton.Id);
        // Cayo Perico's roads: added nodes and links but for two ends on the island's road data, which only a project that
        // reads the island's roads holds; elsewhere the two links at those ends are left out without being listed
        var cayo = set.Nodes.Where(n => n.Group == "cayoPerico").ToList();
        Assert.Equal(new[] { "153:22", "153:29" }, cayo.Where(n => !n.IsAdded).Select(n => n.Key));
        Assert.Equal(42, cayo.Count(n => n.IsAdded));
        Assert.All(set.Links.Where(l => l.Group == "cayoPerico"), l => Assert.True(l.IsAdded));
        // the roads the ground draws as tracks carry the unpaved mark on the nodes added for them, so they are tracks before
        // a capture gives the ground too: the minor roads but for a few ends on paved ground, the island's road and paths
        Assert.Equal(990, set.Nodes.Count(n => n.Group == "minorRoads" && n.IsAdded && n.Set.Unpaved == true));
        Assert.Equal(34, cayo.Count(n => n.IsAdded && n.Set.Unpaved == true));
    }

    [Fact]
    public void ANewProjectStartsFromACopyOfTheBundledEdits()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("sub/new.fxmapgen.json"));
        RoadEditsFile.StartFromBundled(project, "ja");
        project.Save();
        Assert.Equal(RoadEditsFile.FileName, project.File.RoadEdits);
        // the edits as they are, the groups named in the screens' language (one name from then on)
        var copy = RoadEditsFile.Read(tmp.File("sub/road-edits.json"));
        Assert.Equal(RoadEditsFile.Bundled.Nodes, copy.Nodes);
        Assert.Equal(RoadEditsFile.Bundled.Links, copy.Links);
        Assert.Equal(RoadEditsFile.Bundled.Groups.Select(g => ItemName.Of(g.Name.Ja!)), copy.Groups.Select(g => g.Name));
        Assert.Equal(ItemName.Of("サンディ海岸飛行場"), copy.Groups.Single(g => g.Id == "sandyShoresAirfield").Name);
        Assert.Equal(RoadEditsFile.FileName, Project.Load(project.FilePath).File.RoadEdits);
        // a name taken: the next free one
        var second = Project.Create(tmp.File("sub/second.fxmapgen.json"));
        RoadEditsFile.StartFromBundled(second, "en");
        Assert.Equal("road-edits-2.json", second.File.RoadEdits);
        Assert.Equal(ItemName.Of("Sandy Shores Airfield"), RoadEditsFile.Read(tmp.File("sub/road-edits-2.json")).Groups.Single(g => g.Id == "sandyShoresAirfield").Name);
    }

    // ------------------------------------------------------------------------------------------------ applying

    static PathFile.Node PN(string key, double x, double y, double z = 10) => new() { Key = key, X = x, Y = y, Z = z };

    static void TwoWay(PathFile p, string a, string b, int forward = 1, int back = 1)
    {
        p.Links.Add(new PathFile.Link { From = a, To = b, LanesForward = forward, LanesBack = back });
        p.Links.Add(new PathFile.Link { From = b, To = a, LanesForward = back, LanesBack = forward });
    }

    /// <summary>A street 1:1 - 1:2 - 1:3 (the second link 2 lanes towards 1:3, 1 back) and a road 2:1 - 2:2.</summary>
    static PathFile Paths()
    {
        var p = new PathFile();
        p.Nodes.AddRange([PN("1:1", 0, 0), PN("1:2", 100, 0), PN("1:3", 200, 0), PN("2:1", 0, 100, 5), PN("2:2", 100, 100, 5)]);
        TwoWay(p, "1:1", "1:2");
        TwoWay(p, "1:2", "1:3", 2, 1);
        TwoWay(p, "2:1", "2:2");
        return p;
    }

    static NodeValues At(double x, double y, double z = 10) => new(x, y, z);

    static NodeEdit Added(string key, double x, double y, double z = 12, uint street = 0) =>
        new(key, new NodeValues(x, y, z, street, false, false, false, false));

    static LinkEdit AddedLink(string from, string to, int forward = 1, int back = 1, bool narrow = false, double? width = null) =>
        new(from, to, new LinkValues(forward, back, narrow), width);

    [Fact]
    public void EditsAddMoveChangeAndHideInTheGamesOrder()
    {
        var paths = Paths();
        var set = new RoadEditSet(
        [
            Added("added:1", 300, 0, street: 7),
            new NodeEdit("1:1", new NodeValues(X: -5, Y: 1), Original: At(0, 0)),
            new NodeEdit("1:2", new NodeValues(Highway: true), Original: At(100, 0) with { Highway = false }),
            new NodeEdit("1:3", new NodeValues(), Original: At(200, 0)),
            new NodeEdit("2:1", new NodeValues(), Hidden: true, Original: At(0, 100, 5)),
        ],
        [
            AddedLink("1:3", "added:1"),
            new LinkEdit("1:3", "1:2", new LinkValues(LanesForward: 3), 9.5, Original: new LinkValues(1, 2, false)),   // against the file's direction
            new LinkEdit("1:2", "1:1", new LinkValues(), Hidden: true, Original: new LinkValues(1, 1, false)),
        ]);
        Assert.Empty(RoadEditsFile.Problems(set));
        var a = set.ApplyTo(paths);
        Assert.Equal((1, 1, 1, 1, 1, 1, 1), (a.NodesAdded, a.NodesMoved, a.NodesChanged, a.NodesHidden, a.LinksAdded, a.LinksChanged, a.LinksHidden));
        Assert.Empty(a.NotApplied);
        Assert.Equal(new[] { "1:1", "1:2", "1:3", "2:2", "added:1" }, paths.Nodes.Select(n => n.Key));
        Assert.Equal((-5.0, 1.0, 10.0), (paths.Nodes[0].X, paths.Nodes[0].Y, paths.Nodes[0].Z));
        Assert.True(paths.Nodes[1].Highway);
        Assert.Equal((300.0, 7u, false), (paths.Nodes[4].X, paths.Nodes[4].Street, paths.Nodes[4].Junction));
        Assert.Equal(new[] { "1:2>1:3 2/3 9.5", "1:3>1:2 3/2 9.5", "1:3>added:1 1/1 - added" },
            paths.Links.Select(l => $"{l.From}>{l.To} {l.LanesForward}/{l.LanesBack} {(l.Width is { } w ? w.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}{(l.Added ? " added" : "")}"));
    }

    [Fact]
    public void EditsThatNoLongerFitTheGamesPathsAreNotAppliedAndSaySo()
    {
        var paths = Paths();
        var set = new RoadEditSet(
        [
            new NodeEdit("9:9", new NodeValues(X: 1), Original: At(0, 0)),
            new NodeEdit("1:1", new NodeValues(Y: 5), Original: At(1, 0)),                       // the game's node moved since
            new NodeEdit("1:2", new NodeValues(Y: 3), Original: At(100, 0.0004)),                 // within 0.001 m: the same
            new NodeEdit("1:3", new NodeValues(), Original: At(200, 0)),
            new NodeEdit("2:1", new NodeValues(), Original: At(0, 100, 5)),
            new NodeEdit("2:2", new NodeValues(), Original: At(100, 100, 5)),
            Added("added:1", 50, 50),
        ],
        [
            AddedLink("added:1", "1:1"),
            new LinkEdit("2:1", "1:3", new LinkValues(Narrow: true), Original: new LinkValues(1, 1, false)),
            new LinkEdit("1:2", "1:3", new LinkValues(Narrow: true), Original: new LinkValues(1, 1, false)),
            AddedLink("2:2", "2:1"),
            AddedLink("added:1", "1:2"),
        ]);
        Assert.Empty(RoadEditsFile.Problems(set));
        var checkedOnly = Paths();
        var listed = set.NotAppliedTo(checkedOnly);
        Assert.Equal(5, checkedOnly.Nodes.Count);                                               // only checked: the file stays as it was
        Assert.Equal((0.0, 0.0), (checkedOnly.Nodes[0].X, checkedOnly.Nodes[0].Y));
        var a = set.ApplyTo(paths);
        Assert.Equal(a.NotApplied, listed);
        Assert.Equal(new[]
        {
            "node 9:9 nodeMissing", "node 1:1 nodeChanged", "link added:1 1:1 endNotApplied", "link 2:1 1:3 linkMissing",
            "link 1:2 1:3 linkChanged", "link 2:2 2:1 linkExists",
        }, a.NotApplied.Select(n => $"{n.Kind} {n.Key} {n.Reason}"));
        Assert.Equal((1, 1, 1, 0), (a.NodesAdded, a.NodesMoved, a.LinksAdded, a.LinksChanged + a.LinksHidden));
        Assert.Equal((0.0, 0.0), (paths.Nodes[0].X, paths.Nodes[0].Y));                         // 1:1 as the game has it
        Assert.Equal(3.0, paths.Nodes[1].Y);
        var added = Assert.Single(paths.Links, l => l.Added);
        Assert.Equal(("added:1", "1:2"), (added.From, added.To));

        // a value the edit changes that the game holds otherwise now
        var street = new RoadEditSet([new NodeEdit("2:2", new NodeValues(Street: 3), Original: At(100, 100, 5) with { Street = 4 })], []).ApplyTo(Paths());
        Assert.Equal(RoadEditSet.Reasons.NodeChanged, Assert.Single(street.NotApplied).Reason);
    }

    // ------------------------------------------------------------------------------------------------ widths

    [Fact]
    public void TheRoadShapesTakeTheWidthTheEditsGiveElseTheLanes()
    {
        using var tmp = new TempFolder();
        var names = tmp.File("names.json");
        File.WriteAllText(names, "{\"streets\": {}, \"zones\": {}}");
        var paths = Paths();
        new RoadEditSet(
        [
            new NodeEdit("1:2", new NodeValues(), Original: At(100, 0)), new NodeEdit("1:3", new NodeValues(), Original: At(200, 0)),
            Added("added:1", 300, 0), Added("added:2", 400, 0),
        ],
        [
            new LinkEdit("1:2", "1:3", new LinkValues(), 9.5, Original: new LinkValues(2, 1, false)),
            AddedLink("1:3", "added:1"),
            AddedLink("added:1", "added:2", 1, 0, narrow: true),
        ]).ApplyTo(paths);
        var g = RoadNet.Of(paths, names);
        static double W(RoadLinks links, string a, string b) => links.Rows[links.Keys.IndexOf(RoadNet.Key(a, b))].Width;
        var L = RoadLinks.Build(g, g.Junctions, g.Highways(), g.Drawable.ToDictionary(k => k, _ => false));
        Assert.Equal(11.0, W(L, "1:1", "1:2"));                // the game's own: 1 + 1 lanes of 5.5 m
        Assert.Equal(9.5, W(L, "1:2", "1:3"));                 // the width the edits give
        Assert.Equal(11.0, W(L, "1:3", "added:1"));            // added without a width: its lanes
        Assert.Equal(4.0, W(L, "added:1", "added:2"));         // one narrow lane
        var T = RoadLinks.Build(g, g.Junctions, [], g.Drawable.ToDictionary(k => k, _ => true));  // all tracks: 5 m, but the given width wins
        Assert.Equal((5.0, 9.5, 5.0), (W(T, "1:1", "1:2"), W(T, "1:2", "1:3"), W(T, "1:3", "added:1")));
    }

    /// <summary>Road pass of block z8_60_132 (x0 78.75, y0 -881.25) with a north-south on-road band of 25 m, columns 100-124 (x 178.75-202.75).</summary>
    static List<string> BandLines()
    {
        var lines = new List<string>
        {
            "[fxmapgen] MSCAN BEGIN v=1 kind=road z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=4.000 n=71 pstep=1.000 pn=282 ring=0",
        };
        for (int j = 0; j < 282; j++) lines.Add($"[fxmapgen] MSCAN onroad j={j} k=0 0*100 1*25 0*157");
        lines.Add("[fxmapgen] MSCAN END kind=road n=71");
        return lines;
    }

    /// <summary>A street of 5 links on the band (x 190.75, 1:0 to 1:5 south every 20 m) and an unnamed road off it (2:0 - 2:1, x 300).</summary>
    static PathFile BandPaths()
    {
        var p = new PathFile();
        for (int i = 0; i <= 5; i++) p.Nodes.Add(new PathFile.Node { Key = $"1:{i}", X = 190.75, Y = -921.25 - 20 * i, Street = 5 });
        for (int i = 0; i < 5; i++) TwoWay(p, $"1:{i}", $"1:{i + 1}");
        return p;
    }

    static NodeEdit Street(int i) => new($"1:{i}", new NodeValues(), Original: new NodeValues(190.75, -921.25 - 20 * i, 0));

    static IReadOnlyList<MapRoad> Graph(PathFile paths) =>
        RoadGraphBuilder.Build(PathNet.Of(paths, 78.75, -881.25, 360, -1162.5), new ScanArea([ScanFile.Parse(BandLines())]), new FixedParallel(1)).Roads;

    [Fact]
    public void TheRoadGraphTakesTheWidthTheEditsFixInsteadOfMeasuring()
    {
        Assert.Equal(25.0, Assert.Single(Graph(BandPaths())).Width);                             // measured across the band

        var every = BandPaths();
        new RoadEditSet(Enumerable.Range(0, 6).Select(Street).ToList(),
            Enumerable.Range(0, 5).Select(i => new LinkEdit($"1:{i}", $"1:{i + 1}", new LinkValues(), 9.5, Original: new LinkValues(1, 1, false))).ToList()).ApplyTo(every);
        Assert.Equal(9.5, Assert.Single(Graph(every)).Width);

        var one = BandPaths();                                                                    // one link of five: the median stays the band's
        new RoadEditSet([Street(0), Street(1)], [new LinkEdit("1:0", "1:1", new LinkValues(), 9.5, Original: new LinkValues(1, 1, false))]).ApplyTo(one);
        Assert.Equal(25.0, Assert.Single(Graph(one)).Width);

        var added = BandPaths();                                                                  // an added road off the band: its lanes, not a default
        new RoadEditSet([Added("added:1", 300, -921.25, 0), Added("added:2", 300, -1021.25, 0)], [AddedLink("added:1", "added:2")]).ApplyTo(added);
        var roads = Graph(added);
        Assert.Equal(2, roads.Count);
        Assert.Equal((11.0, RoadClass.Street), (roads[1].Width, roads[1].Class));

        var shortOne = BandPaths();                                                               // too short for 3 measurements: its fixed width
        new RoadEditSet([Added("added:1", 300, -921.25, 0), Added("added:2", 300, -951.25, 0)], [AddedLink("added:1", "added:2", 2, 2)]).ApplyTo(shortOne);
        Assert.Equal(22.0, Graph(shortOne)[1].Width);
    }

    [Fact]
    public void ARoadAddedFromANamedNodeTakesTheNameOfItsAddedNodes()
    {
        // the street 5 on the band; a road added east from its middle node 1:2, 100 m
        IReadOnlyList<MapRoad> With(uint street)
        {
            var p = BandPaths();
            new RoadEditSet([Street(2), Added("added:1", 240, -961.25, street: street), Added("added:2", 290, -961.25, street: street)],
                [AddedLink("1:2", "added:1"), AddedLink("added:1", "added:2")]).ApplyTo(p);
            return Graph(p);
        }
        var unnamed = With(0);
        Assert.Equal(2, unnamed.Count);
        Assert.Equal((5u, true), (unnamed[0].Attr.Street, unnamed[0].Attr.Named));      // the street itself keeps its name
        Assert.Equal((0u, false), (unnamed[1].Attr.Street, unnamed[1].Attr.Named));     // 1:2 named, the two added nodes not: no name
        Assert.Equal((7u, true), (With(7)[1].Attr.Street, With(7)[1].Attr.Named));

        // without edits, nodes without a name do not vote (a game street keeps the name of its named nodes)
        var nodes = new List<PathNet.Node> { new("1:0", 0, 0, 0, 5, false, false, false, false), new("1:1", 0, 50, 0, 0, false, false, false, false),
            new("1:2", 0, 100, 0, 0, false, false, false, false) };
        var net = PathNet.Of(nodes, [("1:0", "1:1", 1, 1), ("1:1", "1:2", 1, 1)]);
        Assert.Equal((5u, true), (Chains.Attributes(net, [0, 1, 2]).Street, Chains.Attributes(net, [0, 1, 2]).Named));
    }

    [Fact]
    public void ARoadAddedInTheMiddleOfAnotherHasItsCornersRounded()
    {
        using var tmp = new TempFolder();
        var names = tmp.File("names.json");
        File.WriteAllText(names, "{\"streets\": {}, \"zones\": {}}");
        RoadShapes Shapes(PathFile p)
        {
            var g = RoadNet.Of(p, names);
            var L = RoadLinks.Build(g, g.Junctions, g.Highways(), g.Drawable.ToDictionary(k => k, _ => false));
            var lv = RoadLevels.Compute(L.Keys, L.Rows);
            return RoadShapes.Build(L, lv.Level, lv.Raised);
        }
        // a road south from the middle node 1:2 of the street 1:1 - 1:3 (away from the road 2:1 - 2:2): added, and the same road in the game's data
        var edited = Paths();
        new RoadEditSet([new NodeEdit("1:2", new NodeValues(), Original: At(100, 0)), Added("added:1", 100, -100, 10)], [AddedLink("1:2", "added:1")]).ApplyTo(edited);
        var game = Paths();
        game.Nodes.Add(PN("9:1", 100, -100));
        TwoWay(game, "1:2", "9:1");
        var rounded = Shapes(edited);
        Assert.Equal(new[] { "1:2" }, RoadNet.Of(edited, names) is var g2 ? RoadLinks.Build(g2, g2.Junctions, [], g2.Drawable.ToDictionary(k => k, _ => false)).Joins.Keys.ToArray() : null);
        Assert.Equal((2, 1, 0, 0), (rounded.Corners.Count, rounded.JunctionsRounded, rounded.CornersSkipped, rounded.Patches.Count));  // both corners of the T
        Assert.Equal(1, rounded.JunctionsRounded);
        Assert.Empty(Shapes(game).Corners);                                                  // the game's own middle node: as before
    }

    // ------------------------------------------------------------------------------------------------ the stages

    sealed class Work : IDisposable
    {
        readonly TempFolder _tmp = new();
        public Project Project { get; private set; }
        public WorkFolder Folder { get; }
        public StateStore State { get; }
        public static readonly BlockId B = BlockId.Parse("z8_60_132");

        public Work()
        {
            Project = Project.Create(_tmp.File("t.fxmapgen.json"));
            Project.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
            Project.File.Range = new RangeSetting { Base = "none", Add = [B.Name] };
            Project.Save();
            Folder = new WorkFolder(Project.WorkFolderPath);
            State = StateStore.Open(Folder);
            // the game files: the street on the band and an east-west road on tarmac
            var nodes = new JsonObject();
            var links = new JsonArray();
            void Node(string key, double x, double y) => nodes[key] = new JsonObject
            {
                ["x"] = x, ["y"] = y, ["z"] = 0, ["street"] = 0, ["junction"] = false, ["highway"] = false, ["tunnel"] = false, ["unpaved"] = false, ["switchedOff"] = false,
            };
            void Link(string a, string b)
            {
                foreach (var (f, t) in new[] { (a, b), (b, a) })
                    links.Add(new JsonObject
                    {
                        ["from"] = f, ["to"] = t, ["lanesForward"] = 1, ["lanesBack"] = 1, ["narrow"] = false, ["dontUseForNavigation"] = false, ["shortcut"] = false,
                        ["laneOffset"] = 0, ["gpsBothWays"] = false, ["length"] = 1,
                    });
            }
            for (int i = 0; i <= 5; i++) Node($"1:{i}", 190.75, -921.25 - 20 * i);
            for (int i = 0; i < 5; i++) Link($"1:{i}", $"1:{i + 1}");
            Node("3:0", 220, -1100);
            Node("3:1", 340, -1100);
            Link("3:0", "3:1");
            Directory.CreateDirectory(Folder.Game);
            File.WriteAllText(Path.Combine(Folder.Game, GameFilesOutput.Paths), new JsonObject { ["areas"] = new JsonArray(), ["nodes"] = nodes, ["links"] = links }.ToJsonString());
            File.WriteAllText(Path.Combine(Folder.Game, GameFilesOutput.Names), "{\"streets\": {}, \"zones\": {}}");
            File.WriteAllText(Path.Combine(Folder.Game, GameFilesOutput.Record), JsonSerializer.Serialize(
                new GameFilesStage.SourcesRecord("gta", "keys", [], 0, [], 0, 0, 0, 0, [], [], [], new Dictionary<string, int>(), 0, 0, 0, 0, 0, 0, 0), Project.Json));
            Directory.CreateDirectory(Path.GetDirectoryName(Folder.ScanFile(B))!);
            File.WriteAllLines(Folder.ScanFile(B), BandLines());
            var t0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            State.SetItems(new[] { BlockItem.Height, BlockItem.ScanGround, BlockItem.ScanRoads }.Select(i => (B, i, t0)));
            State.SetStageDone(StageKeys.GameFiles, StageKeys.World, t0.AddMinutes(1));
            // the landcover: tarmac everywhere, no water
            var (bx, by) = (WorldGrid.Left + B.Tx * WorldGrid.TileSize, WorldGrid.Top - B.Ty * WorldGrid.TileSize);
            var f = new GridFile();
            f.Meta["block"] = B.Name;
            f.Meta["x0"] = bx;
            f.Meta["y0"] = by;
            f.Meta["step"] = 1.0;
            f.Add("water", new Grid<bool>(283, 283));
            f.Add("surface", Grid<byte>.Filled(283, 283, 1));
            Directory.CreateDirectory(Path.Combine(Folder.Data, LandcoverFile.Folder));
            f.Save(LandcoverFile.PathOf(Folder.Data, B));
        }

        public string Beside(string name) => Path.Combine(Project.Folder, name);

        public void UseEdits(string? file)
        {
            Project.File.RoadEdits = file;
            Project.Save();
            Project = Project.Load(Project.FilePath);
        }

        public async Task<JobRunner> Run(Stage stage)
        {
            var runner = JobRunner.Create(new JobSetup
            {
                ProjectPath = Project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = State,
                Stages = (_, _) => new BuildPlan([stage], []),
            });
            var end = await runner.RunAsync();
            Assert.Equal(JobState.Done, end.State);
            return runner;
        }

        /// <summary>The road graph, then the landcover as made after it (its record holding the graph's digest), then the road shapes.</summary>
        public async Task<(byte[] Roads, byte[] Shapes)> Both()
        {
            await Run(new RoadGraphStage());
            State.SetStageDone(StageKeys.Landcover, B.Name, State.StageDone(StageKeys.RoadGraph, StageKeys.World)!.Value.AddMilliseconds(1));
            LandcoverStage.WriteRecord(Folder, SurfaceHeights.NameOf(SurfaceHeights.LandcoverItems(Project)), LandcoverStage.RoadGraphDigest(Folder));
            Assert.Null(RoadsStage.Waiting(Project, State));
            await Run(new RoadsStage());
            Assert.False(RoadGraphStage.IsStale(Project, State));
            Assert.False(RoadsStage.IsStale(Project, State));
            return (File.ReadAllBytes(Path.Combine(Folder.Data, RoadGraphFile.Roads)), File.ReadAllBytes(Path.Combine(Folder.Data, RoadShapesFile.FileName)));
        }

        public void Dispose() => _tmp.Dispose();
    }

    [Fact]
    public async Task NoEditsAndAnEmptyFileMakeTheSameRoadsAndChangedEditsMakeThemAgain()
    {
        using var w = new Work();
        var (roads, shapes) = await w.Both();
        Assert.Null(RoadGraphStage.ReadRecord(w.Folder)!.RoadEdits);
        Assert.Null(RoadsStage.ReadRecord(w.Folder)!.RoadEdits);

        // an empty edits file: made again (another digest), the same bytes
        File.WriteAllText(w.Beside(RoadEditsFile.FileName), RoadEditsFile.Format(RoadEditSet.Empty));
        w.UseEdits(RoadEditsFile.FileName);
        Assert.True(RoadGraphStage.IsStale(w.Project, w.State));
        Assert.True(RoadsStage.IsStale(w.Project, w.State));
        Assert.True(RoadGraphStage.EditsChanged(w.Project));
        var (roads2, shapes2) = await w.Both();
        Assert.Equal(roads, roads2);
        Assert.Equal(shapes, shapes2);
        var rec = RoadGraphStage.ReadRecord(w.Folder)!.RoadEdits!;
        Assert.Equal((RoadEditsFile.FileName, 0, 0), (rec.File, rec.NodesAdded, rec.NotApplied.Count));
        Assert.Equal(RoadEditsFile.DigestOf(w.Project), rec.Digest);

        // the same bytes saved again: nothing to make
        File.WriteAllText(w.Beside(RoadEditsFile.FileName), RoadEditsFile.Format(RoadEditSet.Empty));
        File.SetLastWriteTimeUtc(w.Beside(RoadEditsFile.FileName), DateTime.UtcNow.AddMinutes(5));
        Assert.False(RoadGraphStage.IsStale(w.Project, w.State));
        Assert.False(RoadsStage.IsStale(w.Project, w.State));

        // a road added east of the band, the road 3:0 - 3:1 hidden, an edit whose node the game does not have
        var set = new RoadEditSet(
        [
            Added("added:1", 300, -921.25, 0), Added("added:2", 300, -1021.25, 0),
            new NodeEdit("3:0", new NodeValues(), Hidden: true, Original: new NodeValues(220, -1100, 0)),
            new NodeEdit("7:7", new NodeValues(X: 100), Original: new NodeValues(90, -1000, 0)),
        ],
        [AddedLink("added:1", "added:2")]);
        RoadEditsFile.Write(w.Beside(RoadEditsFile.FileName), set);
        Assert.True(RoadGraphStage.IsStale(w.Project, w.State));
        Assert.Equal(new[] { Work.B }, RoadGraphStage.EditedBlocks(w.Project));
        var (roads3, shapes3) = await w.Both();
        Assert.NotEqual(roads, roads3);
        Assert.NotEqual(shapes, shapes3);
        var graph = RoadGraphFile.Read(Path.Combine(w.Folder.Data, RoadGraphFile.Roads));
        Assert.Contains(graph, r => r.Points.All(p => Math.Abs(p.X - 300) < 0.01) && r.Width == 11);
        Assert.DoesNotContain(graph, r => r.Points.Any(p => p.Y == -1100));
        foreach (var edits in new[] { RoadGraphStage.ReadRecord(w.Folder)!.RoadEdits!, RoadsStage.ReadRecord(w.Folder)!.RoadEdits! })
        {
            Assert.Equal((2, 1, 1), (edits.NodesAdded, edits.NodesHidden, edits.LinksAdded));
            Assert.Equal(new NotAppliedEdit("node", "7:7", RoadEditSet.Reasons.NodeMissing), Assert.Single(edits.NotApplied));
            Assert.Equal(new[] { Work.B.Name }, edits.Blocks.Keys);
        }
        Assert.Empty(RoadGraphStage.EditedBlocks(w.Project));
        Assert.False(RoadGraphStage.EditsChanged(w.Project));

        // no edits again: the first roads
        w.UseEdits(null);
        Assert.True(RoadGraphStage.IsStale(w.Project, w.State));
        var (roads4, shapes4) = await w.Both();
        Assert.Equal(roads, roads4);
        Assert.Equal(shapes, shapes4);
    }

    [Fact]
    public async Task TheRunReadsTheCopyOfTheEditsTakenWhenTheirFirstStageStarts()
    {
        using var w = new Work();
        var file = w.Beside("edits/mine.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var set = new RoadEditSet([Added("added:1", 300, -921.25, 0), Added("added:2", 300, -1021.25, 0)], [AddedLink("added:1", "added:2")]);
        RoadEditsFile.Write(file, set);
        w.UseEdits("edits/mine.json");
        var runner = JobRunner.Create(new JobSetup
        {
            ProjectPath = w.Project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = w.State,
            Stages = (_, _) => new BuildPlan([new RoadGraphStage()], []),
        });
        var copy = Path.Combine(runner.RunPath, "inputs", "mine.json");
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(copy));
        // saved after the run's start and before the first stage reading the edits (while a capture would run): the run
        // takes its copy again as that stage starts, and builds with these
        RoadEditsFile.Write(file, new RoadEditSet([Added("added:1", 300, -921.25, 0), Added("added:2", 300, -1011.25, 0)], [AddedLink("added:1", "added:2")]));
        var digest = RoadEditsFile.DigestOf(w.Project);
        Assert.Equal(JobState.Done, (await runner.RunAsync()).State);
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(copy));
        var rec = RoadGraphStage.ReadRecord(w.Folder)!.RoadEdits!;
        Assert.Equal(("edits/mine.json", digest, 1), (rec.File, rec.Digest, rec.LinksAdded));
        Assert.False(RoadGraphStage.IsStale(w.Project, w.State));                                 // made with the project's file as it is

        // the next run reads a broken file: the stage fails and names the problem
        File.WriteAllText(file, "not json any more");
        Assert.True(RoadGraphStage.IsStale(w.Project, w.State));
        var failed = await JobRunner.Create(new JobSetup
        {
            ProjectPath = w.Project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = w.State,
            Stages = (_, _) => new BuildPlan([new RoadGraphStage()], []),
        }).RunAsync();
        Assert.Contains("is not valid JSON", Assert.Single(failed.Failures).Message);
        File.Delete(file);
        Assert.Equal("missing", RoadEditsFile.DigestOf(w.Project));
        Assert.True(RoadGraphStage.IsStale(w.Project, w.State));
    }

    [Fact]
    public void TheRoadStepsHoldTheEditsWhileTheyRun()
    {
        Assert.Contains(InputKeys.RoadEdits, StageInputs.For("mapData.roadGraph"));
        Assert.Contains(InputKeys.RoadEdits, StageInputs.For("mapData.roads"));
        Assert.Contains(InputKeys.RoadEdits, StageInputs.For("mapData.labels"));                   // the street names the edits add
        Assert.DoesNotContain(InputKeys.RoadEdits, StageInputs.For("mapData.landcover"));
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("t.fxmapgen.json"));
        project.File.RoadEdits = " ";
        Assert.Contains("the road edits file is empty (null for none)", project.Validate());
        Assert.Null(RoadEditsFile.DigestOf(Project.Create(tmp.File("u.fxmapgen.json"))));
    }
}
