using System.Text.Json;
using FxMapGenerator.App.Cli;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.App.Tests;

public sealed class CliTests
{
    static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = CliCommands.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void NewThenPlan()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(dir, "server");      // the extension is added
            // a style's maps (English always) and Japanese ones: every style in every language
            var created = Run("new", file, "--maps", "satellite,atlas-postalcodemap,atlas-regional-ja");
            Assert.Equal(0, created.Code);
            Assert.True(File.Exists(file + ".fxmapgen.json"));
            Assert.Contains("satellite, atlas-postalcodemap-en, atlas-postalcodemap-ja, atlas-regional-en, atlas-regional-ja", created.Out);
            // the road edits start from a copy of the bundled ones, their groups named in English
            Assert.Contains("road edits: road-edits.json", created.Out);
            var edits = RoadEditsFile.Read(Path.Combine(dir, "road-edits.json"));
            Assert.Equal(RoadEditsFile.Bundled.Nodes, edits.Nodes);
            Assert.Equal(RoadEditsFile.Bundled.Groups.Select(g => FxMapGenerator.Core.Projects.ItemName.Of(g.Name.En!)), edits.Groups.Select(g => g.Name));
            Assert.Equal("road-edits.json", Project.Load(file + ".fxmapgen.json").File.RoadEdits);
            Assert.Equal(65, Run("new", file).Code);     // exists already

            var plan = Run("plan", file + ".fxmapgen.json", "--level", "full", "--processors", "8");
            Assert.Equal(0, plan.Code);
            Assert.Contains("8 workers of 8 processors", plan.Out);
            Assert.Contains("1045 blocks (674 land, 371 water)", plan.Out);
            Assert.Contains("Total", plan.Out);

            var json = Run("plan", file + ".fxmapgen.json", "--json");
            using var doc = JsonDocument.Parse(json.Out);
            Assert.Equal(1045, doc.RootElement.GetProperty("rangeBlocks").GetInt32());
            Assert.Equal("visit", doc.RootElement.GetProperty("rows")[2].GetProperty("id").GetString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(0, 0, "0")]
    [InlineData(0.1, 0.4, "<1 s")]        // both ends round to 0: not "<1-0 s"
    [InlineData(0.2, 12, "<1-12 s")]
    [InlineData(10, 17, "10-17 s")]
    [InlineData(100, 300, "2-5 min")]
    [InlineData(6000, 9000, "1.7-2.5 h")]
    public void DurationRanges(double low, double high, string expected) => Assert.Equal(expected, Durations.Range(low, high));

    [Fact]
    public void WrongArgumentsAndMissingFiles()
    {
        Assert.Equal(64, Run("plan").Code);
        Assert.Equal(64, Run("plan", "a.fxmapgen.json", "--level").Code);
        Assert.Equal(64, Run("new", "x.fxmapgen.json", "--maps", "atlas-z-en").Code);
        Assert.Equal(64, Run("new", "x.fxmapgen.json", "--maps", "atlas-postalcodemap-fr").Code);
        Assert.Equal(64, Run("import", "x.fxmapgen.json").Code);
        var missing = Run("plan", Path.Combine(Path.GetTempPath(), "no-such-project.fxmapgen.json"));
        Assert.Equal(65, missing.Code);
        Assert.Contains("not found", missing.Err);
    }

    [Fact]
    public void RetakeMarksListsAndClearsBlocksOfTheRange()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(dir, "r.fxmapgen.json");
            Assert.Equal(0, Run("new", file).Code);
            Assert.Contains("no block is marked", Run("retake", file).Out);

            var two = Run("retake", file, "--blocks", "z8_48_8, z8_52_8");
            Assert.Equal(0, two.Code);
            Assert.Contains("marked 2 blocks: z8_48_8, z8_52_8", two.Out);

            // a rectangle in game metres: the blocks of the range it touches (an island in a lake and its bridges)
            var touched = MapFrame.Standard.Touching(-130, 3590, 1970, 4660);
            Assert.Equal(40, touched.Count);                                              // 8 x 5 blocks
            int inRange = touched.Count(DefaultRange.Blocks.ContainsKey);
            var rect = Run("retake", file, "--rect", "-130,3590,1970,4660");
            Assert.Equal(0, rect.Code);
            Assert.Contains($"marked {inRange} blocks", rect.Out);
            var state = StateStore.Open(new WorkFolder(dir));
            Assert.Equal(inRange + 2, state.MarkedForRetake().Count);
            Assert.Contains($"{inRange + 2} blocks marked for retake:", Run("retake", file).Out);

            Assert.Contains("unmarked 1 blocks", Run("retake", file, "--blocks", "z8_48_8", "--clear").Out);
            Assert.False(StateStore.Open(new WorkFolder(dir)).IsMarkedForRetake(BlockId.Parse("z8_48_8")));

            Assert.Equal(64, Run("retake", file, "--all").Code);                           // --all only takes marks off
            Assert.Equal(64, Run("retake", file, "--clear").Code);                         // off which blocks?
            Assert.Equal(64, Run("retake", file, "--blocks", "z8_48_8", "--rect", "0,0,1,1").Code);
            Assert.Equal(64, Run("retake", file, "--rect", "0,0,1").Code);
            var outside = Run("retake", file, "--blocks", "z8_0_0");                      // open sea: not in the default range
            Assert.Equal(65, outside.Code);
            Assert.Contains("not in the range", outside.Err);

            using (WorkFolderLock.TryAcquire(new WorkFolder(dir), out _))
            {
                var busy = Run("retake", file, "--blocks", "z8_48_8");
                Assert.Equal(65, busy.Code);
                Assert.Contains("another run", busy.Err);
            }

            var all = Run("retake", file, "--clear", "--all");
            Assert.Equal(0, all.Code);
            Assert.Contains("0 blocks marked for retake in all", all.Out);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
