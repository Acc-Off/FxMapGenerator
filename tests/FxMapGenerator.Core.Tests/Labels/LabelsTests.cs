using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Regions;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.Core.Tests.Labels;

public sealed class LabelsTests
{
    /// <summary>Every glyph half an em wide, the text 0.7 em high (a font of fixed widths).</summary>
    sealed class FixedMetrics : ITextMetrics
    {
        public (double Width, double Height) Size(string text, string font, string weight, double em, double spacing = 0.0)
        {
            int n = ITextMetrics.Glyphs(text).Count;
            return (n * 0.5 * em + spacing * (n - 1), 0.7 * em);
        }

        public double[] Advances(string text, string font, string weight, double em) => ITextMetrics.Glyphs(text).Select(_ => 0.5 * em).ToArray();
    }

    [Fact]
    public void NumbersComeOutAsPythonAndNumpyComputeThem()
    {
        // Python's x ** 2 is the C library's pow, one unit off x * x now and then
        Assert.Equal(141484.15489845927, Num.Pow2(376.14379550706303));
        Assert.Equal(141484.15489845924, 376.14379550706303 * 376.14379550706303);
        // float // is not the floor of the quotient
        Assert.Equal(9.0, Num.PyFloorDiv(1.0, 0.1));
        Assert.Equal(10.0, Math.Floor(1.0 / 0.1));
        // numpy's arange steps by the difference of its first two values
        Assert.Equal(new[] { 12.345, 17.345, 22.345, 27.344999999999995 }, Num.NpArange(12.345, 30.0, 5.0));
        Assert.Empty(Num.NpArange(5.0, 4.0, 5.0));
    }

    [Fact]
    public void PixelSetsAreWalkedInCPythonsOrder()
    {
        var pixels = Enumerable.Range(0, 3).SelectMany(y => Enumerable.Range(0, 4).Select(x => (x, y))).ToList();
        var on = PixelSet.Of(pixels);
        // list(set(...)) of CPython 3.11 for these pixels, and of the set less two of them
        Assert.Equal(new[] { (0, 1), (1, 2), (2, 1), (0, 0), (3, 1), (1, 1), (2, 0), (3, 0), (0, 2), (2, 2), (1, 0), (3, 2) }, on.Items());
        var rest = PixelSet.Difference(on, PixelSet.Of([(1, 1), (2, 1)]));
        Assert.Equal(new[] { (0, 1), (1, 2), (0, 0), (3, 1), (2, 0), (3, 0), (0, 2), (2, 2), (1, 0), (3, 2) }, rest.Items());
        Assert.True(rest.Contains(3, 2));
        Assert.False(rest.Contains(1, 1));
        var big = PixelSet.Of(Enumerable.Range(0, 20).SelectMany(y => Enumerable.Range(0, 20).Where(x => (x * 3 + y * 5) % 4 == 0).Select(x => (x, y))).ToList());
        Assert.Equal(100, big.Count);
        Assert.Equal(new[] { (12, 4), (6, 18), (4, 0), (5, 1), (8, 0), (10, 6), (2, 2), (9, 17), (6, 2), (13, 17), (18, 10), (7, 19) }, big.Items().Take(12));
    }

    [Fact]
    public void GlyphsFollowTheLineAndReadToTheRight()
    {
        // a line drawn from east to west: the text is laid from its west end
        List<LinePoint> line = [new(100, 0), new(0, 0)];
        var cum = LabelLines.Cum(line);
        var gl = LabelLines.GlyphsAlong(line, cum, 50, ["A", "B", "C"], [10.0, 10.0, 10.0], 12)!;
        Assert.Equal(new[] { ("A", 40.0, 0.0, 0.0), ("B", 50.0, 0.0, 0.0), ("C", 60.0, 0.0, 0.0) }, gl.Select(g => (g.Char, g.X, g.Y, g.Rotation)));
        // too near the end, or a bend sharper than allowed between two glyphs
        Assert.Null(LabelLines.GlyphsAlong(line, cum, 10, ["A", "B", "C"], [10.0, 10.0, 10.0], 12));
        List<LinePoint> bent = [new(0, 0), new(50, 0), new(80, 30)];
        Assert.Null(LabelLines.GlyphsAlong(bent, LabelLines.Cum(bent), 50, ["A", "B", "C"], [10.0, 10.0, 10.0], 12));
        Assert.NotNull(LabelLines.GlyphsAlong(bent, LabelLines.Cum(bent), 50, ["A", "B", "C"], [10.0, 10.0, 10.0], 50));
        // corners cut the line into parts
        Assert.Equal(2, LabelLines.SplitCorners([new(0, 0), new(50, 0), new(50, 50)]).Count);
    }

    static LabelInput Input(IReadOnlyList<RoadGraphFile.Road> roads, IReadOnlyList<PostalCode> postals, string lang = "en", IReadOnlyCollection<string>? unnamed = null)
    {
        // a 200 m square of one zone, 4 m cells
        var zone = Grid<ushort>.Filled(50, 50, 1);
        var zones = new ZoneGrid { Codes = ["", "AAA"], Zone = zone, Inside = Grid<bool>.Filled(50, 50, true), X0 = 0, Y0 = 200, Step = 4, FrameWidth = 200, FrameHeight = 200 };
        var names = new GameNames
        {
            Streets = new Dictionary<uint, IReadOnlyDictionary<string, string>>
            {
                [7] = new Dictionary<string, string> { ["en"] = "Main St", ["ja"] = "" },
                [8] = new Dictionary<string, string> { ["en"] = "Paleto Blvd", ["ja"] = "パレト大通り" },
            },
            Zones = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["AAA"] = new Dictionary<string, string> { ["en"] = "Alpha", ["ja"] = "アルファ" } },
        };
        return new LabelInput
        {
            Frame = (0, 200, 200, 0), Postals = postals, Pois = [], Zones = zones, Names = names, Roads = roads, Routes = Routes.Bundled,
            Style = MapStyle.Builtin("postalcodemap").Labels!, Language = lang, Metrics = new FixedMetrics(), UnnamedZones = unnamed ?? [],
        };
    }

    static RoadGraphFile.Road Road(uint street, params (double X, double Y)[] pts) =>
        new(RoadClass.Street, 10, false, street, false, Polyline.Length(pts.Select(p => new P2(p.X, p.Y)).ToList()), 1, 1, pts.Select(p => new P2(p.X, p.Y)).ToList());

    [Fact]
    public void PostalCodesZoneNamesAndStreetNamesFindTheirPlaces()
    {
        var input = Input([Road(7, (10, 150), (190, 150))], [new PostalCode("1000", 40, 40, null), new PostalCode("1001", 500, 40, null)]);
        var labels = LabelPlacer.Place(input);
        // the postal code in the frame, at the default size (no size of its own, the zone has none in the table)
        var postal = Assert.Single(labels, l => l.Kind == "postal");
        Assert.Equal(("1000", 40.0, 40.0, 24.41, "bold"), (postal.Text, postal.X, postal.Y, postal.Size, postal.Weight));
        // the zone name in capitals at the middle of the square's cells (cell corners 0..196 m: 98 m)
        var zone = Assert.Single(labels, l => l.Kind == "zone");
        Assert.Equal(("ALPHA", 98.0, 102.0, 18.0, 3.0), (zone.Text, zone.X, zone.Y, zone.Size, zone.Spacing));
        // the street written out in full along the road, one label for 180 m (street class: one per 300 m)
        var street = Assert.Single(labels, l => l.Kind == "street");
        Assert.Equal(("Main Street", 150.0, 0.0), (street.Text, street.Y, street.Rotation));
        Assert.Equal("Main Street".Length, street.Chars!.Count);
        Assert.Null(street.Route);
        // a street of a numbered route: its number in the text, and the label says which (the record counts them)
        var route = Assert.Single(LabelPlacer.Place(Input([Road(8, (5, 150), (195, 150))], [])), l => l.Kind == "street");
        Assert.Equal(("Paleto Boulevard // Route 1", "1"), (route.Text, route.Route));
        var routeJa = Assert.Single(LabelPlacer.Place(Input([Road(8, (5, 150), (195, 150))], [], "ja")), l => l.Kind == "street");
        Assert.Equal(("パレト大通り", null), (routeJa.Text, routeJa.Route));
        Assert.Equal(14 * 0.5 * 11, street.Chars.Sum(c => c.Advance), 9);

        // in Japanese: the zone's own name as it is; the street has no Japanese name, so the English one
        var ja = LabelPlacer.Place(Input([Road(7, (10, 150), (190, 150))], [], "ja"));
        Assert.Equal(new[] { ("zone", "アルファ"), ("street", "Main St") }, ja.Select(l => (l.Kind, l.Text)));
    }

    [Fact]
    public void AnIconAndItsLabelBesideKeepTheirPlace()
    {
        var label = new Dictionary<string, string> { ["en"] = "Clinic" };
        LabelInput With(params ResolvedPoi[] pois)
        {
            var i = Input([], []);
            return new LabelInput
            {
                Frame = i.Frame, Postals = i.Postals, Pois = pois, Zones = i.Zones, Names = i.Names, Roads = i.Roads, Routes = i.Routes,
                Style = i.Style, Language = i.Language, Metrics = i.Metrics,
            };
        }
        ResolvedPoi At(double x, double y, bool showName)
        {
            var style = new PoiStyle("clinic", ItemName.Of("Clinic"), "icon", new Rgb(0, 0, 0), 20, "normal", null, 0, null, showName, "hospital-box");
            return new ResolvedPoi(new PoiPoint("1", "g", "", label, x, y, null, null, null, null, null, null), style, PoiShow.Default, style.Color, 20, true, false);
        }
        // alone, the zone name sits at the middle of its zone (98, 102)
        Assert.Equal((98.0, 102.0), Assert.Single(LabelPlacer.Place(With()), l => l.Kind == "zone") is var z ? (z.X, z.Y) : default);
        // an icon there takes the place
        var moved = Assert.Single(LabelPlacer.Place(With(At(98, 102, false))), l => l.Kind == "zone");
        Assert.NotEqual((98.0, 102.0), (moved.X, moved.Y));
        // an icon west of it, whose name beside reaches the middle (its left edge 10 + 3.6 m east of the point, 6 x 6 m long)
        Assert.Equal((98.0, 102.0), Assert.Single(LabelPlacer.Place(With(At(40, 102, false))), l => l.Kind == "zone") is var w ? (w.X, w.Y) : default);
        var besideName = Assert.Single(LabelPlacer.Place(With(At(40, 102, true))), l => l.Kind == "zone");
        Assert.NotEqual((98.0, 102.0), (besideName.X, besideName.Y));
    }

    [Fact]
    public void ALabelNeverGoesOverAnotherOne()
    {
        // the zone name takes the middle; a road through the middle gets its name away from it, or not at all
        var through = LabelPlacer.Place(Input([Road(7, (40, 100), (160, 100))], []));
        var zone = Assert.Single(through, l => l.Kind == "zone");
        Assert.Equal((98.0, 102.0), (zone.X, zone.Y));
        Assert.DoesNotContain(through, l => l.Kind == "street");
        // with the postal code on the middle, the zone name moves to the nearest free place (30 m up)
        var moved = LabelPlacer.Place(Input([], [new PostalCode("1000", 100, 100, 30)]));
        var z = Assert.Single(moved, l => l.Kind == "zone");
        Assert.Equal((98.0, 132.0), (z.X, z.Y));
    }

    [Fact]
    public void AZoneLeftUnnamedGetsNoNameAndLeavesItsPlaceToTheOthers()
    {
        // the zone's name is not placed; the postal code stays, and the street through the middle now gets its name there
        var labels = LabelPlacer.Place(Input([Road(7, (40, 100), (160, 100))], [new PostalCode("1000", 40, 40, null)], unnamed: ["AAA"]));
        Assert.DoesNotContain(labels, l => l.Kind == "zone");
        Assert.Single(labels, l => l.Kind == "postal");
        Assert.Equal("Main Street", Assert.Single(labels, l => l.Kind == "street").Text);
        // another zone's code in the list changes nothing
        Assert.Equal("ALPHA", Assert.Single(LabelPlacer.Place(Input([], [], unnamed: ["BBB"])), l => l.Kind == "zone").Text);
        Assert.DoesNotContain(LabelPlacer.Place(Input([], [], "ja", ["AAA"])), l => l.Kind == "zone");
    }

    [Fact]
    public void TheLabelsSectionNamesItsPlacementApartFromItsColours()
    {
        var st = MapStyle.Builtin("postalcodemap");
        Assert.Equal(st.Labels!.PlacementKey, MapStyle.Builtin("regional").Labels!.PlacementKey);    // the two atlas styles share their labels
        string Key(Action<System.Text.Json.Nodes.JsonObject> edit)
        {
            var src = (System.Text.Json.Nodes.JsonObject)st.Source.DeepClone();
            edit(src["labels"]!.AsObject());
            return MapStyle.Parse(src.ToJsonString(), "test").Labels!.PlacementKey;
        }
        Assert.Equal(st.Labels.PlacementKey, Key(l => l["street"]!["color"] = "#ff0000"));
        Assert.Equal(st.Labels.PlacementKey, Key(l => l["zone"]!["outlineWidth"] = 2));
        Assert.NotEqual(st.Labels.PlacementKey, Key(l => l["zone"]!["size"] = 20));
        Assert.NotEqual(st.Labels.PlacementKey, Key(l => l["fonts"]!["en"] = "Arial"));
        // the bundled values: the highway size includes its 1.5 times
        Assert.Equal((25.5, "bold", 2000.0, 20.0, 0.12), (st.Labels.StreetClasses[RoadClass.Highway].Size, st.Labels.StreetClasses[RoadClass.Highway].Weight,
            st.Labels.StreetClasses[RoadClass.Highway].SameNameGap, st.Labels.StreetClasses[RoadClass.Highway].MaxGlyphTurn, st.Labels.StreetClasses[RoadClass.Highway].LetterSpacing));
        Assert.Equal((24.41, 30.52, 36.62), (st.Labels.PostalSizeIn("DOWNT"), st.Labels.PostalSizeIn("PALETO"), st.Labels.PostalSizeIn("GRAPES")));
        Assert.Contains("labels.zone.size must be above 0", Assert.Throws<StyleException>(() => Key(l => l["zone"]!["size"] = 0)).Message);
    }

    [Fact]
    public async Task TheStageWritesEveryLanguagesLabelsAndKnowsWhenTheyAreOld()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("t.fxmapgen.json"));
        project.File.Maps = new MapsSetting
        {
            Satellite = false,
            Atlas = new AtlasSetting { Enabled = true, Styles = [AtlasPresets.PostalCodeMap, AtlasPresets.Regional], Languages = ["en", "ja"] },
        };
        project.File.Range = new RangeSetting { Base = "none", Add = ["z8_60_132"] };
        File.WriteAllText(tmp.File("postals.json"), """[{"code": "1000", "x": 40, "y": 40}]""");
        project.File.Postals = "postals.json";
        project.Save();
        var folder = new WorkFolder(project.WorkFolderPath);
        Directory.CreateDirectory(folder.Data);
        Directory.CreateDirectory(folder.Game);
        Input([], []).Zones.Save(Path.Combine(folder.Data, ZoneGrid.FileName));
        File.WriteAllText(Path.Combine(folder.Data, RoadGraphFile.Roads),
            """{"roads": [{"class": "street", "width": 10, "divided": false, "street": 7, "oneWay": false, "length": 180, "lanes": [1, 1], "points": [[10, 150], [190, 150]]}]}""");
        File.WriteAllText(Path.Combine(folder.Game, GameFilesOutput.Names),
            """{"streets": {"7": {"en": "Main St", "ja": ""}}, "zones": {"AAA": {"en": "Alpha", "ja": "アルファ"}}}""");
        var state = StateStore.Open(folder);
        Task<JobSnapshot> Run() => JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = state,
            Stages = (_, _) => new BuildPlan([new LabelsStage()], []),
        }).RunAsync();
        LabelsStage.Metrics = () => new FixedMetrics();
        LabelsStage.Fetch = (url, _) => url == PostalCodes.DefaultUrl ? """[{"code": "2000", "x": 60, "y": 40}]""" : throw new HttpRequestException("offline");
        try
        {
            Assert.Equal(new[] { "en", "ja" }, LabelsStage.Languages(project));
            Assert.True(LabelsStage.IsStale(project, state, "en"));
            Assert.Equal(JobState.Done, (await Run()).State);
            // the two English atlas maps share one file (the same labels section); Japanese has its own
            var key = MapStyle.Builtin(AtlasPresets.PostalCodeMap).Labels!.PlacementKey;
            var en = LabelsFile.Read(Path.Combine(folder.Data, LabelsFile.FileName("en", key)));
            Assert.Equal(("en", key, (0.0, 200.0, 200.0, 0.0)), (en.Language, en.Key, en.Frame));
            Assert.Equal(new[] { ("postal", "1000"), ("zone", "ALPHA"), ("street", "Main Street") }, en.Labels.Select(l => (l.Kind, l.Text)));
            Assert.Equal(new[] { ("postal", "1000"), ("zone", "アルファ"), ("street", "Main St") },
                LabelsFile.Read(Path.Combine(folder.Data, LabelsFile.FileName("ja", key))).Labels.Select(l => (l.Kind, l.Text)));
            Assert.False(LabelsStage.IsStale(project, state, "en"));
            Assert.False(LabelsStage.IsStale(project, state, "ja"));

            // street names the road edits add: the labels take them (the Japanese name, else the English one), and other names make them again
            string RoadGraph(uint hash) =>
                $$"""{"roads": [{"class": "street", "width": 10, "divided": false, "street": {{hash}}, "oneWay": false, "length": 180, "lanes": [1, 1], "points": [[10, 150], [190, 150]]}]}""";
            File.WriteAllText(tmp.File("edits.json"), """{"format": 1, "streets": {"9": {"en": "Island Rd", "ja": "島通り"}}, "nodes": {}, "links": []}""");
            project.File.RoadEdits = "edits.json";
            project.Save();
            Assert.True(LabelsStage.IsStale(project, state, "en"));
            File.WriteAllText(Path.Combine(folder.Data, RoadGraphFile.Roads), RoadGraph(9));
            Assert.Equal(JobState.Done, (await Run()).State);
            var street = Assert.Single(LabelsFile.Read(Path.Combine(folder.Data, LabelsFile.FileName("en", key))).Labels, l => l.Kind == "street");
            Assert.Equal(LabelPlacer.Expand("Island Rd", MapStyle.Builtin(AtlasPresets.PostalCodeMap).Labels!.Expand), street.Text);
            Assert.Equal("島通り", Assert.Single(LabelsFile.Read(Path.Combine(folder.Data, LabelsFile.FileName("ja", key))).Labels, l => l.Kind == "street").Text);
            Assert.NotNull(LabelsStage.Record(project, "ja")!.StreetsSha256);
            Assert.False(LabelsStage.IsStale(project, state, "ja"));
            File.WriteAllText(tmp.File("edits.json"), """{"format": 1, "streets": {"9": {"en": "Isle Rd", "ja": "島通り"}}, "nodes": {}, "links": []}""");
            Assert.True(LabelsStage.IsStale(project, state, "ja"));
            project.File.RoadEdits = null;
            project.Save();
            File.WriteAllText(Path.Combine(folder.Data, RoadGraphFile.Roads), RoadGraph(7));
            Assert.Equal(JobState.Done, (await Run()).State);
            Assert.Null(LabelsStage.Record(project, "en")!.StreetsSha256);
            Assert.False(LabelsStage.IsStale(project, state, "en"));

            // an edited postal code file, or points of interest of the project's own, make both languages again
            File.WriteAllText(tmp.File("postals.json"), """[{"code": "1001", "x": 40, "y": 40}]""");
            Assert.True(LabelsStage.IsStale(project, state, "en"));
            Assert.Equal(JobState.Done, (await Run()).State);
            Assert.Equal("1001", LabelsFile.Read(Path.Combine(folder.Data, LabelsFile.FileName("ja", key))).Labels[0].Text);
            Directory.CreateDirectory(tmp.File("poi"));
            File.WriteAllText(tmp.File("poi/shops.json"), """{"style": "facility", "points": [{"id": "p", "label": {"en": "Shop"}, "x": 50, "y": 60}]}""");
            project.File.Poi = "poi";
            project.Save();
            Assert.True(LabelsStage.IsStale(project, state, "ja"));

            // no source of its own: nearest-postal's table, fetched once
            project.File.Postals = null;
            project.Save();
            Assert.Equal(JobState.Done, (await Run()).State);
            Assert.Equal("2000", LabelsFile.Read(Path.Combine(folder.Data, LabelsFile.FileName("en", key))).Labels[0].Text);
            Assert.Equal(PostalCodes.DefaultUrl, LabelsStage.CurrentCopy(project)!.Source);
            Assert.False(LabelsStage.IsStale(project, state, "en"));
            // a source that cannot be fetched fails the run with the reason
            project.File.Postals = "https://example.invalid/postals.json";
            project.Save();
            var end = await Run();
            Assert.Contains("could not fetch the postal codes from https://example.invalid/postals.json (offline)", end.Failures.First().Message);
        }
        finally
        {
            LabelsStage.Metrics = null;
            LabelsStage.Fetch = null;
        }
    }

    [Fact]
    public async Task TheIslandsZoneNameIsPlacedOnlyWithCayoPericosRoads()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("i.fxmapgen.json"));
        project.File.Maps = new MapsSetting { Satellite = false, Atlas = new AtlasSetting { Enabled = true, Styles = [AtlasPresets.PostalCodeMap], Languages = ["en", "ja"] } };
        project.File.Range = new RangeSetting { Base = "none", Add = ["z8_60_132"] };
        File.WriteAllText(tmp.File("postals.json"), """[{"code": "1000", "x": 40, "y": 40}]""");
        project.File.Postals = "postals.json";
        project.Save();
        var folder = new WorkFolder(project.WorkFolderPath);
        Directory.CreateDirectory(folder.Data);
        Directory.CreateDirectory(folder.Game);
        // the island's zone over the north half of a 200 m square (20,000 m2, enough for a name), another zone over the south half
        var zone = Grid<ushort>.Filled(50, 50, 1);
        for (int r = 25; r < 50; r++)
            for (int c = 0; c < 50; c++) zone[r, c] = 2;
        new ZoneGrid { Codes = ["", LabelsStage.IslandZone, "AAA"], Zone = zone, Inside = Grid<bool>.Filled(50, 50, true), X0 = 0, Y0 = 200, Step = 4, FrameWidth = 200, FrameHeight = 200 }
            .Save(Path.Combine(folder.Data, ZoneGrid.FileName));
        File.WriteAllText(Path.Combine(folder.Data, RoadGraphFile.Roads), """{"roads": []}""");
        File.WriteAllText(Path.Combine(folder.Game, GameFilesOutput.Names),
            """{"streets": {}, "zones": {"ISHEIST": {"en": "Cayo Perico", "ja": "カヨ・ペリコ"}, "AAA": {"en": "Alpha", "ja": "アルファ"}}}""");
        var state = StateStore.Open(folder);
        Task<JobSnapshot> Run() => JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 1, Processors = 1, Memory = new FakeMemory(), State = state,
            Stages = (_, _) => new BuildPlan([new LabelsStage()], []),
        }).RunAsync();
        var key = MapStyle.Builtin(AtlasPresets.PostalCodeMap).Labels!.PlacementKey;
        string[] Zones(string lang) => LabelsFile.Read(Path.Combine(folder.Data, LabelsFile.FileName(lang, key))).Labels.Where(l => l.Kind == "zone").Select(l => l.Text).ToArray();
        LabelsStage.Metrics = () => new FixedMetrics();
        try
        {
            // a project that does not read the island's roads: the island's zone has no name, the other zone has
            Assert.Equal(new[] { LabelsStage.IslandZone }, LabelsStage.UnnamedZones(project));
            Assert.Equal(JobState.Done, (await Run()).State);
            Assert.Equal(new[] { "ALPHA" }, Zones("en"));
            Assert.Equal(new[] { "アルファ" }, Zones("ja"));
            Assert.False(LabelsStage.Record(project, "en")!.CayoPerico);
            Assert.False(LabelsStage.IsStale(project, state, "en"));

            // switched on: both languages are placed again, with the island's name
            project.File.CayoPerico = true;
            project.Save();
            Assert.Empty(LabelsStage.UnnamedZones(project));
            Assert.True(LabelsStage.IsStale(project, state, "en"));
            Assert.True(LabelsStage.IsStale(project, state, "ja"));
            Assert.Equal(JobState.Done, (await Run()).State);
            Assert.Equal(new[] { "CAYO PERICO", "ALPHA" }, Zones("en"));
            Assert.Equal(new[] { "カヨ・ペリコ", "アルファ" }, Zones("ja"));
            Assert.True(LabelsStage.Record(project, "ja")!.CayoPerico);
            Assert.False(LabelsStage.IsStale(project, state, "ja"));

            // and off again: the name goes
            project.File.CayoPerico = false;
            project.Save();
            Assert.True(LabelsStage.IsStale(project, state, "en"));
            Assert.Equal(JobState.Done, (await Run()).State);
            Assert.Equal(new[] { "ALPHA" }, Zones("en"));

            // labels whose record does not say which it was are placed again
            var record = LabelsStage.RecordPath(folder, "en");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(record))!.AsObject();
            Assert.True(json.Remove("cayoPerico"));
            File.WriteAllText(record, json.ToJsonString());
            Assert.True(LabelsStage.IsStale(project, state, "en"));
            Assert.False(LabelsStage.IsStale(project, state, "ja"));
        }
        finally
        {
            LabelsStage.Metrics = null;
        }
    }

    [Fact]
    public void PostalCodeFilesAndRoutesAreRead()
    {
        var p = PostalCodes.Parse("""[{"code": "1000", "x": 1645.4, "y": 6453.96}, {"code": "1001", "x": 1, "y": 2, "size": 30}]""", "test");
        Assert.Equal(new[] { new PostalCode("1000", 1645.4, 6453.96, null), new PostalCode("1001", 1, 2, 30) }, p);
        Assert.Equal("test: item 1 has no code; '2' has no position; '3' is off the map",
            Assert.Throws<LabelsException>(() => PostalCodes.Parse("""[{"x": 1, "y": 2}, {"code": "2", "x": 1}, {"code": "3", "x": 99999, "y": 0}]""", "test")).Message);
        Assert.Contains("expected a list", Assert.Throws<LabelsException>(() => PostalCodes.Parse("{}", "test")).Message);
        Assert.Equal(9, Routes.Bundled.Count);
        var r13 = Routes.Bundled.Single(r => r.Number == "13");
        Assert.Equal("Route 13 // Senora Freeway", Routes.Format(r13, "Senora Freeway"));
        Assert.Equal("Great Ocean Highway // Route 1", Routes.Format(Routes.Bundled[0], "Great Ocean Highway"));
        Assert.Equal("Del Perro Boulevard", LabelPlacer.Expand("Del Perro Blvd", MapStyle.Builtin("postalcodemap").Labels!.Expand));
        Assert.Equal("Stab City", LabelPlacer.Expand("Stab City", MapStyle.Builtin("postalcodemap").Labels!.Expand));     // whole words only
    }
}
