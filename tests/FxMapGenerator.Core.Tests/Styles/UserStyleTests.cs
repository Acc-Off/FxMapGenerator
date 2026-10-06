using System.Text;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.Core.Tests.Styles;

/// <summary>User styles: the changes over a bundled style, the file (its form and its checks), a project's styles folder.</summary>
public sealed class UserStyleTests
{
    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void ChangesPutOverAStyleGiveTheOtherStyleBack()
    {
        var postal = MapStyle.Builtin(AtlasPresets.PostalCodeMap).Source;
        var regional = MapStyle.Builtin(AtlasPresets.Regional).Source;
        var changes = StyleChanges.Between(postal, regional);
        Assert.True(JsonNode.DeepEquals(regional, StyleChanges.Apply(postal, changes)));
        // objects are looked into (only what differs), lists and values are whole, a key the other lacks is null
        Assert.Contains("Regional colors", changes["credit"]!.GetValue<string>());   // each credits what it takes from the postal code map
        var ground = changes["groundRaster"]!.AsObject();
        Assert.True(ground.ContainsKey("blur"));
        Assert.Null(ground["blur"]);
        Assert.Equal(0.8, changes["shade"]!["strength"]!.GetValue<double>());
        Assert.Single(changes["shade"]!.AsObject());
        // the paints are the same (the sea of both follows PostalCodeMap: the same bands, colours and opacities)
        Assert.False(changes.ContainsKey("paint"));
        Assert.False(changes.ContainsKey("sea"));
        // nothing between a style and itself; a number written another way is the same value
        Assert.Empty(StyleChanges.Between(postal, postal));
        Assert.Empty(StyleChanges.Between(postal, StyleChanges.With(postal, "paint.roads.track.width", JsonValue.Create(5))));
        Assert.True(StyleChanges.Same(JsonNode.Parse("[1.50, {\"a\": 2}]"), JsonNode.Parse("[1.5, {\"a\": 2.0}]")));
        Assert.False(StyleChanges.Same(JsonNode.Parse("[1.5]"), JsonNode.Parse("[1.6]")));
        Assert.False(StyleChanges.Same(JsonNode.Parse("{\"a\": 1}"), JsonNode.Parse("{\"a\": 1, \"b\": null}")));
    }

    [Fact]
    public void TheShortFormsGoWholeSoTheirOrderStays()
    {
        var postal = MapStyle.Builtin(AtlasPresets.PostalCodeMap).Source;
        var entries = postal["labels"]!["street"]!["expand"]!.AsObject().Select(kv => (kv.Key, Value: kv.Value!.GetValue<string>())).ToList();
        JsonObject Table(IEnumerable<(string Key, string Value)> rows) => new(rows.Select(r => KeyValuePair.Create(r.Key, (JsonNode?)JsonValue.Create(r.Value))));
        JsonObject With(JsonObject table) => StyleChanges.With(postal, "labels.street.expand", table);
        List<string> KeysOf(JsonObject style) => style["labels"]!["street"]!["expand"]!.AsObject().Select(kv => kv.Key).ToList();

        // the first short form renamed in its place: the whole table goes into the changes, and comes back in its order
        var renamed = Table(entries.Select((e, i) => (i == 0 ? "Bv" : e.Key, e.Value)));
        var changes = StyleChanges.Between(postal, With(renamed));
        Assert.Equal(entries.Count, changes["labels"]!["street"]!["expand"]!.AsObject().Count);
        Assert.Equal(renamed.Select(kv => kv.Key), KeysOf(StyleChanges.Apply(postal, changes)));
        // and so does the file (its own keys go in the base style's order, not a table's rows)
        var file = UserStyleFile.Parse(Encoding.UTF8.GetBytes(UserStyleFile.Format(new UserStyle("mine", "Mine", AtlasPresets.PostalCodeMap, changes))), "mine.json");
        Assert.Equal(renamed.Select(kv => kv.Key), KeysOf(file.Values()));
        // the order alone changed is a change too; the same table in the same order is none
        var reversed = Table(entries.AsEnumerable().Reverse());
        Assert.Equal(reversed.Select(kv => kv.Key), KeysOf(StyleChanges.Apply(postal, StyleChanges.Between(postal, With(reversed)))));
        Assert.Empty(StyleChanges.Between(postal, With(Table(entries))));
        // the tables per zone stay key by key (their order means nothing)
        var zone = StyleChanges.Between(postal, StyleChanges.With(postal, "regions.zones.DOWNT", JsonValue.Create("lowland")));
        Assert.Equal("""{"regions":{"zones":{"DOWNT":"lowland"}}}""", zone.ToJsonString());
    }

    [Fact]
    public void PlacesAreReadAndSetThroughObjectsAndLists()
    {
        var s = MapStyle.Builtin(AtlasPresets.PostalCodeMap).Source;
        Assert.Equal("#fdfcfc", StyleChanges.At(s, "paint.roads.road.fill")!.GetValue<string>());
        Assert.Equal(4, StyleChanges.At(s, "paint.roads.tunnel.dash.1")!.GetValue<double>());
        Assert.Null(StyleChanges.At(s, "paint.roads.nothing"));
        var t = StyleChanges.With(s, "paint.roads.tunnel.dash.1", JsonValue.Create(5.5));
        Assert.Equal(new[] { 6, 5.5 }, t["paint"]!["roads"]!["tunnel"]!["dash"]!.AsArray().Select(x => x!.GetValue<double>()));
        Assert.Equal(4, StyleChanges.At(s, "paint.roads.tunnel.dash.1")!.GetValue<double>());   // a copy: the source stays
        var u = StyleChanges.With(s, "credit", null);
        Assert.False(u.ContainsKey("credit"));
    }

    [Fact]
    public void TheFileHoldsOnlyTheChangesAndReadsBackTheSame()
    {
        using var tmp = new TempFolder();
        var changes = new JsonObject
        {
            ["paint"] = new JsonObject { ["roads"] = new JsonObject { ["road"] = new JsonObject { ["fill"] = "#ffffff" } } },
            ["background"] = "#000000",
            ["credit"] = null,
        };
        var style = new UserStyle("night", "夜の地図", AtlasPresets.PostalCodeMap, changes);
        var path = tmp.File("night.json");
        UserStyleFile.Write(path, style);
        var text = File.ReadAllText(path);
        // own keys first, then the changes in the base style's order (credit, background, ..., paint), LF, no BOM
        Assert.StartsWith("{\n  \"format\": 1,\n  \"id\": \"night\",\n  \"name\": \"夜の地図\",\n  \"base\": \"postalcodemap\",\n  \"credit\": null,\n  \"background\": \"#000000\",\n  \"paint\"", text);
        Assert.DoesNotContain("\r", text);
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
        var back = UserStyleFile.Read(path);
        Assert.Equal(("night", "夜の地図", "postalcodemap"), (back.Id, back.Name, back.Base));
        Assert.True(JsonNode.DeepEquals(changes, back.Changes));
        // the style it makes: the base's values with the changes, its own id and name, no credit
        var made = back.Style();
        Assert.Equal("night", made.Id);
        Assert.Equal("#000000", made.Background.ToString());
        Assert.Equal("#ffffff", made.Paint.Road.Fill.ToString());
        Assert.Equal(MapStyle.Builtin(AtlasPresets.PostalCodeMap).Paint.Highway, made.Paint.Highway);
        Assert.Null(made.Credit);
        Assert.Equal(ItemName.Of("夜の地図"), made.Name);
    }

    [Fact]
    public void AFileWithProblemsNamesThemAll()
    {
        var ex = Assert.Throws<StyleException>(() => UserStyleFile.Parse(Bytes("""{"format": 1, "id": "Bad-Id", "name": {"en": "x"}, "base": "roadmap"}"""), "f.json"));
        Assert.Contains("small letters", ex.Message);
        Assert.Contains("'name' must be a text", ex.Message);
        Assert.Contains("'base' must be a bundled atlas style", ex.Message);
        // a bundled style's id, a newer format
        Assert.Contains("bundled style's id", Assert.Throws<StyleException>(() =>
            UserStyleFile.Parse(Bytes("""{"format": 1, "id": "regional", "name": "x", "base": "regional"}"""), "f.json")).Message);
        Assert.Contains("newer", Assert.Throws<StyleException>(() =>
            UserStyleFile.Parse(Bytes("""{"format": 2, "id": "a", "name": "x", "base": "regional"}"""), "f.json")).Message);
        // the style it makes is checked as the maps read it: a colour that is none, bands that do not increase
        var bad = Assert.Throws<StyleException>(() => UserStyleFile.Parse(Bytes("""
            {"format": 1, "id": "a", "name": "A", "base": "postalcodemap", "background": "red", "sea": {"bands": [1, 0.5]}}
            """), "f.json"));
        Assert.Contains("'red' is not a colour", bad.Message);
        Assert.Contains("sea.bands must increase", bad.Message);
        // the kind of ground stays the base's
        Assert.Contains("groundRaster.mode must stay 'blend'", Assert.Throws<StyleException>(() => UserStyleFile.Parse(Bytes("""
            {"format": 1, "id": "a", "name": "A", "base": "postalcodemap", "groundRaster": {"mode": "regions"}}
            """), "f.json")).Message);
    }

    [Fact]
    public void AWholeStyleFileBecomesAStyleOfTheBundledStyleOfItsKindOfGround()
    {
        var whole = MapStyle.Builtin(AtlasPresets.Regional).Source.DeepClone().AsObject();
        whole["id"] = "mine";
        whole["name"] = new JsonObject { ["en"] = "Mine", ["ja"] = "自分の" };
        whole["background"] = "#123456";
        var style = UserStyleFile.ParseAny(Bytes(whole.ToJsonString()), "mine.json", "ja");
        Assert.Equal(("mine", "自分の", AtlasPresets.Regional), (style.Id, style.Name, style.Base));
        Assert.Equal("Mine", UserStyleFile.ParseAny(Bytes(whole.ToJsonString()), "mine.json").Name);
        Assert.Equal("""{"background":"#123456"}""", style.Changes.ToJsonString());
        // a user style's file reads as it is
        var file = UserStyleFile.Format(style with { Id = "again" });
        Assert.Equal("again", UserStyleFile.ParseAny(Bytes(file), "again.json").Id);
        // the road map's style is no atlas style
        var road = MapStyle.Builtin("roadmap").Source.DeepClone().AsObject();
        road["id"] = "road2";
        Assert.Contains("not an atlas style", Assert.Throws<StyleException>(() => UserStyleFile.ParseAny(Bytes(road.ToJsonString()), "r.json")).Message);
    }

    [Fact]
    public void TheFirstStyleMakesTheProjectsFolderAndTheListReadsIt()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("p.fxmapgen.json"));
        project.Save();
        Assert.Null(ProjectStyles.Folder(project));
        Assert.Empty(ProjectStyles.List(project));
        Assert.True(ProjectStyles.IsTaken(project, "postalcodemap"));
        Assert.False(ProjectStyles.IsTaken(project, "mine"));

        Assert.True(ProjectStyles.Write(project, new UserStyle("mine", "Mine", AtlasPresets.Regional, new JsonObject())));
        Assert.Equal("styles", project.File.Styles);
        project.Save();
        Assert.True(File.Exists(tmp.File("styles/mine.json")));
        Assert.True(ProjectStyles.IsTaken(Project.Load(project.FilePath), "mine"));
        Assert.False(ProjectStyles.Write(project, new UserStyle("other", "Other", AtlasPresets.PostalCodeMap, new JsonObject())));

        // a file that cannot be read, and one whose id is not its name, say why
        File.WriteAllText(tmp.File("styles/broken.json"), "{");
        File.WriteAllText(tmp.File("styles/named.json"), UserStyleFile.Format(new UserStyle("othername", "X", AtlasPresets.Regional, new JsonObject())));
        var list = ProjectStyles.List(project);
        Assert.Equal(new[] { "broken", "mine", "named", "other" }, list.Select(e => e.Id));
        Assert.NotNull(list[0].Problem);
        Assert.Contains("is not its file name", list[2].Problem);
        Assert.Null(list[1].Problem);
        Assert.Equal("Mine", list[1].Style!.Name);
    }

    [Fact]
    public void AnAtlasMapReadsTheProjectsStyleAndARunItsCopy()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("p.fxmapgen.json"));
        ProjectStyles.Write(project, new UserStyle("dusk", "夕方", AtlasPresets.Regional, new JsonObject { ["background"] = "#102030" }));
        project.File.Maps.Atlas.Enabled = true;
        project.File.Maps.Atlas.Styles = ["dusk", AtlasPresets.Regional];
        project.File.Maps.Atlas.Languages = ["en", "ja"];
        project.Save();
        var loaded = Project.Load(project.FilePath);
        var map = loaded.Maps.First(m => m.Kind == MapKind.Atlas && m.Language == "ja");
        Assert.Equal("atlas-dusk-ja", map.Id);
        Assert.Equal("#102030", loaded.StyleOf(map).Background.ToString());
        // the exported maps: the style's name as written, the language of the maps not in English; English without
        Assert.Equal(("Atlas map (夕方, Japanese)", "atlas-dusk-ja"), (map.Title(loaded), map.ExportName));
        Assert.Equal(("Atlas map (夕方)", "atlas-dusk"), (MapSet.Atlas("dusk", "en").Title(loaded), MapSet.Atlas("dusk", "en").ExportName));
        Assert.Equal(("Atlas map (Regional colors)", "Atlas map (Regional colors, Japanese)"), (MapSet.Atlas(AtlasPresets.Regional, "en").Title(loaded), MapSet.Atlas(AtlasPresets.Regional, "ja").Title(loaded)));
        // the file changed: the maps read the new contents
        ProjectStyles.Write(loaded, new UserStyle("dusk", "Dusk", AtlasPresets.Regional, new JsonObject { ["background"] = "#405060" }));
        Assert.Equal("#405060", loaded.StyleOf(map).Background.ToString());
        // a region field per regions section of the styles whose ground is made of them (the same section, one field)
        Assert.Single(FxMapGenerator.Core.Regions.RegionsStage.Fields(loaded));
        ProjectStyles.Write(loaded, new UserStyle("dusk", "Dusk", AtlasPresets.Regional, new JsonObject { ["regions"] = new JsonObject { ["blur"] = 200 } }));
        Assert.Equal(2, FxMapGenerator.Core.Regions.RegionsStage.Fields(loaded).Count);
        // a run reads its copies
        var copies = tmp.File("run/styles");
        Directory.CreateDirectory(copies);
        UserStyleFile.Write(ProjectStyles.PathOf(copies, "dusk"), new UserStyle("dusk", "Dusk", AtlasPresets.Regional, new JsonObject { ["background"] = "#708090" }));
        loaded.UseStylesCopy(copies);
        Assert.Equal("#708090", loaded.StyleOf(map).Background.ToString());
        // a style the project lacks is named; the road map's style or an id of another form is no atlas map
        File.Delete(ProjectStyles.PathOf(copies, "dusk"));
        Assert.Equal("STYLE", Assert.Throws<ProjectException>(() => loaded.StyleOf(map)).Code);
        Assert.False(MapSet.TryParse("atlas-roadmap-en", out _));
        Assert.False(MapSet.TryParse("atlas-Dusk-en", out _));
    }

    [Fact]
    public void StylesWithOtherShadeValuesOrRegionsGetShadingsAndFieldsOfTheirOwn()
    {
        var postal = MapStyle.Builtin(AtlasPresets.PostalCodeMap);
        var regional = MapStyle.Builtin(AtlasPresets.Regional);
        MapStyle With(MapStyle s, string key, JsonNode value) => MapStyle.Parse(StyleChanges.With(s.Source, key, value).ToJsonString(), "t");
        // the bundled atlas styles differ in the shading's strength only (applied when drawing): one shading
        Assert.Single(FxMapGenerator.Core.Cells.CellPrepStage.Needs([postal, regional]).Shades);
        // another light's height: two, each its own file
        var shades = FxMapGenerator.Core.Cells.CellPrepStage.Needs([postal, With(postal, "shade.altitude", JsonValue.Create(30)), regional]).Shades;
        Assert.Equal(2, shades.Count);
        Assert.Matches("^shade-[0-9a-f]{12}\\.grid$", FxMapGenerator.Core.Cells.CellFiles.ShadeName(shades[0]));
        Assert.NotEqual(FxMapGenerator.Core.Cells.CellFiles.ShadeName(shades[0]), FxMapGenerator.Core.Cells.CellFiles.ShadeName(shades[1]));
        // the region colours: a field per regions section (other values of the style do not make another)
        Assert.Null(FxMapGenerator.Core.Cells.CellPrepStage.FieldKey(postal));
        Assert.NotEqual(FxMapGenerator.Core.Cells.CellPrepStage.FieldKey(regional), FxMapGenerator.Core.Cells.CellPrepStage.FieldKey(With(regional, "regions.blur", JsonValue.Create(200))));
        Assert.Equal(FxMapGenerator.Core.Cells.CellPrepStage.FieldKey(regional), FxMapGenerator.Core.Cells.CellPrepStage.FieldKey(With(regional, "background", JsonValue.Create("#000000"))));

        // a map's drawing compares the files of the cell's data it reads: another style's ground picture changing leaves it
        Dictionary<string, string> Files(string other, string own) => new()
        {
            ["layers.json"] = "a", [FxMapGenerator.Core.Cells.CellFiles.ShadeName(shades[0])] = "b", ["ground-postalcodemap.png"] = own, ["ground-regional.png"] = other,
        };
        var record = new FxMapGenerator.Core.Cells.CellRecord([], new Dictionary<string, string>(), [], [], new Dictionary<string, JsonNode>(), 0, 0, 0, Outputs: Files("c", "d"));
        var drawn = FxMapGenerator.Core.Cells.CellPrepStage.OutputFor(record, postal);
        Assert.NotNull(drawn);
        Assert.Equal(drawn, FxMapGenerator.Core.Cells.CellPrepStage.OutputFor(record with { Outputs = Files("e", "d") }, postal));
        Assert.NotEqual(drawn, FxMapGenerator.Core.Cells.CellPrepStage.OutputFor(record with { Outputs = Files("c", "f") }, postal));
        Assert.Null(FxMapGenerator.Core.Cells.CellPrepStage.OutputFor(record with { Outputs = new Dictionary<string, string> { ["layers.json"] = "a" } }, postal));
    }

    [Fact]
    public void AStyleIsInUseByTheAtlasMapsAndTheMinimapThatTakeIt()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("p.fxmapgen.json"));
        project.File.Maps.Atlas.Styles = ["mine", AtlasPresets.PostalCodeMap];
        project.File.Maps.Atlas.Languages = ["en", "ja"];
        project.File.Minimap.Map = "atlas-mine-ja";
        Assert.Equal(new[] { "atlas-mine-en", "atlas-mine-ja" }, ProjectStyles.UsedBy(project, "mine"));
        Assert.Equal(new[] { "atlas-postalcodemap-en", "atlas-postalcodemap-ja" }, ProjectStyles.UsedBy(project, AtlasPresets.PostalCodeMap));
        project.File.Maps.Atlas.Languages = ["en"];
        Assert.Equal(new[] { "atlas-postalcodemap-en" }, ProjectStyles.UsedBy(project, AtlasPresets.PostalCodeMap));
        Assert.Equal(new[] { "atlas-mine-en", "atlas-mine-ja" }, ProjectStyles.UsedBy(project, "mine"));   // the minimap's still
        Assert.Empty(ProjectStyles.UsedBy(project, "other"));
    }
}
