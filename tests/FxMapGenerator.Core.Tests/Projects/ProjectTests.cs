using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Projects;

public sealed class ProjectTests
{
    [Fact]
    public void NewProjectDefaultsAndRoundTrip()
    {
        using var tmp = new TempFolder();
        var path = tmp.File("myserver.fxmapgen.json");
        var p = Project.Create(path);
        Assert.Equal("myserver", p.File.Name);
        Assert.Equal(new[] { MapSet.Satellite }, p.Maps);
        Assert.Equal(tmp.Path, p.WorkFolderPath);          // "." = the project file's folder
        Assert.Equal(1045, p.Range.Count);

        p.File.Maps.Atlas = new AtlasSetting { Enabled = true, Styles = [AtlasPresets.PostalCodeMap, AtlasPresets.Regional], Languages = ["ja", "en"] };
        p.File.Maps.Roadmap = true;
        p.File.Minimap.Map = "satellite";
        p.File.WorkFolder = "work";
        p.Save();

        // every style in every language, English first
        var back = Project.Load(path);
        Assert.Equal(new[] { "satellite", "atlas-postalcodemap-en", "atlas-postalcodemap-ja", "atlas-regional-en", "atlas-regional-ja", "roadmap" }, back.Maps.Select(m => m.Id));
        Assert.Equal(new[] { "en", "ja" }, back.Languages);
        Assert.True(back.MakesJapanese);
        var atlasJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["maps"]!["atlas"]!;
        Assert.Equal("[\"postalcodemap\",\"regional\"] [\"ja\",\"en\"]", atlasJson["styles"]!.ToJsonString() + " " + atlasJson["languages"]!.ToJsonString());
        Assert.Equal("satellite", back.File.Minimap.Map);
        Assert.Equal(Path.Combine(tmp.Path, "work"), back.WorkFolderPath);
        Assert.Contains("\"workFolder\": \"work\"", File.ReadAllText(path));
    }

    [Fact]
    public void NewProjectsKeepWorkersAsANumberAndTheAtlasOffWithItsDefaultStyle()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("n.fxmapgen.json"));
        p.Save();
        var text = File.ReadAllText(p.FilePath);
        Assert.Contains($"\"parallel\": {p.File.Parallel},", text);
        Assert.Contains("\"enabled\": false", text);
        var back = Project.Load(p.FilePath);
        Assert.Equal(new[] { "satellite" }, back.Maps.Select(m => m.Id));             // the atlas is off
        Assert.Equal(("postalcodemap", "en"), (back.File.Maps.Atlas.Styles.Single(), back.File.Maps.Atlas.Languages.Single()));
        Assert.False(back.MakesJapanese);
        back.File.Maps.Atlas.Enabled = true;
        Assert.Equal(new[] { "satellite", "atlas-postalcodemap-en" }, back.Maps.Select(m => m.Id));
        // Japanese is made only with the atlas on
        back.File.Maps.Atlas.Languages = ["en", "ja"];
        Assert.True(back.MakesJapanese);
        back.File.Maps.Atlas.Enabled = false;
        Assert.False(back.MakesJapanese);
    }

    [Fact]
    public void RangeEditsKeepTheBasePlusAddedMinusRemovedForm()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("e.fxmapgen.json"));
        var land = BlockId.Parse("z8_48_8");       // in the default range
        var sea = BlockId.Parse("z8_0_0");         // outside it
        Assert.Equal(2, p.SetInRange(new[] { land, sea }, include: false) + p.SetInRange(new[] { sea }, include: true));
        Assert.Equal(new[] { "z8_48_8" }, p.File.Range.Remove);
        Assert.Equal(new[] { "z8_0_0" }, p.File.Range.Add);
        Assert.Equal(0, p.SetInRange(new[] { sea }, include: true));       // already in
        Assert.Equal(2, p.SetInRange(new[] { land, sea, land }, include: true) + p.SetInRange(new[] { sea }, include: false));
        Assert.Empty(p.File.Range.Remove);                                   // the base block came back
        Assert.Empty(p.File.Range.Add);                                      // the added one left again
        Assert.Equal(1045, p.Range.Count);
        p.SetInRange(new[] { land }, include: false);
        p.ResetRange();
        Assert.Equal((1045, 0, 0), (p.Range.Count, p.File.Range.Add.Count, p.File.Range.Remove.Count));
    }

    [Fact]
    public void CellsAddedAroundTheMapTakeBlocksWithNegativeNumbers()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("x.fxmapgen.json"));
        Assert.True(p.Frame.IsStandard);
        var west = BlockId.Parse("z8_-4_-4");
        Assert.Equal(0, p.SetInRange([west], include: true));           // off the frame: left as it is
        p.File.Range.ExtraCells = new ExtraCellsSetting { Top = 1, Left = 1 };
        Assert.Equal(new MapFrame(1, 0, 1, 0), p.Frame);
        Assert.Equal(1, p.SetInRange([west], include: true));
        Assert.Equal(new[] { "z8_-4_-4" }, p.File.Range.Add);
        Assert.Equal(BlockClass.Water, p.Range[west]);
        Assert.Empty(p.Validate());
        p.Save();
        var again = Project.Load(p.FilePath);
        Assert.Equal(new MapFrame(1, 0, 1, 0), again.Frame);
        Assert.Contains(west, again.Range.Keys);
        Assert.False(again.File.CayoPerico);

        // taking the cells away again leaves the block outside the frame: the project says so
        again.File.Range.ExtraCells = null;
        Assert.Contains(again.Validate(), m => m.Contains("outside the map's frame (z8_-4_-4)", StringComparison.Ordinal));
    }

    [Fact]
    public void RangeAddsAndRemovesBlocks()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("r.fxmapgen.json"));
        p.File.Range.Add.Add("z8_0_0");          // open sea, outside the default table
        p.File.Range.Remove.Add("z8_48_8");      // a land block of the default
        var r = p.Range;
        Assert.Equal(1045, r.Count);
        Assert.Equal(BlockClass.Water, r[BlockId.Parse("z8_0_0")]);
        Assert.False(r.ContainsKey(BlockId.Parse("z8_48_8")));
        p.File.Range.Base = "none";
        Assert.Single(p.Range);
    }

    [Theory]
    [InlineData("\"minimap\": { \"map\": \"roadmap\" }", "minimap map 'roadmap'")]
    [InlineData("\"parallel\": 0", "parallel 0")]
    [InlineData("\"range\": { \"base\": \"default\", \"add\": [\"z8_1_1\"], \"remove\": [] }", "'z8_1_1' is not a block")]
    [InlineData("\"range\": { \"base\": \"default\", \"add\": [\"z8_-4_0\"], \"remove\": [] }", "1 block(s) of the range lie outside the map's frame (z8_-4_0)")]
    [InlineData("\"range\": { \"base\": \"none\", \"add\": [], \"remove\": [], \"extraCells\": { \"top\": 2, \"bottom\": 1, \"left\": 0, \"right\": 0 } }", "at most 2 cells can be added above and below")]
    [InlineData("\"range\": { \"base\": \"none\", \"add\": [], \"remove\": [], \"extraCells\": { \"top\": 0, \"bottom\": 0, \"left\": 3, \"right\": 2 } }", "at most 4 cells can be added left and right")]
    [InlineData("\"range\": { \"base\": \"none\", \"add\": [], \"remove\": [], \"extraCells\": { \"top\": 0, \"bottom\": 0, \"left\": -1, \"right\": 0 } }", "must be 0 or more")]
    [InlineData("\"maps\": { \"satellite\": true, \"atlas\": { \"enabled\": true, \"styles\": [ \"Big-Map\" ] }, \"roadmap\": false }", "unknown atlas style 'Big-Map'")]
    [InlineData("\"maps\": { \"atlas\": { \"styles\": [ \"regional\", \"regional\" ] } }", "the atlas style 'regional' is listed twice")]
    [InlineData("\"maps\": { \"atlas\": { \"languages\": [ \"ja\" ] } }", "always made in English")]
    [InlineData("\"maps\": { \"atlas\": { \"languages\": [ \"en\", \"fr\" ] } }", "unknown language 'fr'")]
    [InlineData("\"gameFiles\": { \"serverResources\": null }", "must be a list")]
    [InlineData("\"gameFiles\": { \"serverResources\": [\"a\", \" \"] }", "entry of the server's resources is empty")]
    public void InvalidProjectsAreRefusedWithTheReason(string field, string reason)
    {
        using var tmp = new TempFolder();
        var path = tmp.File("bad.fxmapgen.json");
        File.WriteAllText(path, "{ \"format\": 1, \"name\": \"bad\", " + field + " }");
        var ex = Assert.Throws<ProjectException>(() => Project.Load(path));
        Assert.Equal("INVALID", ex.Code);
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void NewerFormatsAndBrokenFilesAreRefused()
    {
        using var tmp = new TempFolder();
        var newer = tmp.File("newer.fxmapgen.json");
        File.WriteAllText(newer, "{ \"format\": 99, \"name\": \"n\" }");
        Assert.Equal("NEWER_FORMAT", Assert.Throws<ProjectException>(() => Project.Load(newer)).Code);
        var broken = tmp.File("broken.fxmapgen.json");
        File.WriteAllText(broken, "{ \"format\": ");
        Assert.Equal("INVALID_JSON", Assert.Throws<ProjectException>(() => Project.Load(broken)).Code);
        Assert.Equal("NOT_FOUND", Assert.Throws<ProjectException>(() => Project.Load(tmp.File("none.fxmapgen.json"))).Code);
    }

    [Fact]
    public async Task SavingWaitsForAReaderOfTheFileAndPassesOnARefusalThatLasts()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("s.fxmapgen.json"));
        p.Save();

        // a reader holds the file the way File.ReadAllText opens it (a run reading the project again does): Windows
        // refuses to replace the file meanwhile, so the save cannot end before the reader lets go
        var reader = new FileStream(p.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        p.File.Name = "saved beside a reader";
        var save = Task.Run(p.Save);
        for (int i = 0; i < 6000 && !File.Exists(p.FilePath + ".tmp"); i++) await Task.Delay(5);
        Assert.True(File.Exists(p.FilePath + ".tmp"));         // the save has come to the replacement
        await Task.Delay(100);
        Assert.False(save.IsCompleted);
        reader.Dispose();
        await save.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("saved beside a reader", Project.Load(p.FilePath).File.Name);

        // a reader that stays: the refusal comes out as it is once the patience is over, and the file is the one before
        using (new FileStream(p.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            p.File.Name = "not saved";
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ex = Assert.ThrowsAny<Exception>(p.Save);
            Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().Name);
            Assert.True(clock.Elapsed > Project.SavePatience / 2, clock.Elapsed.ToString());
        }
        Assert.Equal("saved beside a reader", Project.Load(p.FilePath).File.Name);
    }
}
