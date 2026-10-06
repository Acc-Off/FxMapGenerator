using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.SampleIsland;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.SampleIsland;

public sealed class SampleIslandTests
{
    static IslandData Small()
    {
        const int w = 6, h = 5;
        int n = w * h;
        var ground = Enumerable.Range(0, n).Select(i => (float)(i * 0.25)).ToArray();
        var water = Enumerable.Repeat(float.NaN, n).ToArray();
        water[3] = 1.5f;
        var top = (float[])ground.Clone();
        top[7] = 20;
        return new IslandData
        {
            Surface = new IslandSurface
            {
                X0 = -4140, Y0 = 8400, Step = 2, Width = w, Height = h, Ground = ground, Top = top, Water = water,
                Probe = Enumerable.Repeat(float.NaN, n).ToArray(), Material = Enumerable.Range(0, n).Select(i => (byte)(i % 3)).ToArray(),
                MaterialNames = ["", "GRASS", "TARMAC"], Zone = Enumerable.Repeat((byte)1, n).ToArray(), ZoneCodes = ["", "DOWNT"],
                OnRoad = Enumerable.Range(0, n).Select(i => i % 2 == 0).ToArray(), Street = Enumerable.Repeat(77u, n).ToArray(),
            },
            Zones = new Dictionary<string, (string, string)> { ["DOWNT"] = ("Port", "ポート") },
            Streets = new Dictionary<uint, (string, string)> { [77] = ("Pier St", "ピア・ストリート") },
            Nodes = [new IslandNode("0:0", -4139, 8399, 1.25, 77, true, false, false, false, false, [-4141, 8397, -4137, 8401]), new IslandNode("0:1", -4135, 8399, 1.5, 77, false, false, true, false, false)],
            Links = [new IslandLink("0:0", "0:1", 1, 0, true)],
            Postals = [("101", -4138.5, 8398.25)],
            Markers = [("A", -4137, 8398)],
            Dots = [("1", -4136, 8397, "#ff0000")],
        };
    }

    [Fact]
    public void TheBundledShapeReadsBackWhatWasWritten()
    {
        using var temp = new TempFolder();
        var a = Small();
        SampleIslandFile.Write(temp.Path, a);
        var b = SampleIslandFile.Read(name => File.OpenRead(temp.File(name)));
        Assert.Equal((a.Surface.X0, a.Surface.Y0, a.Surface.Step, a.Surface.Width, a.Surface.Height), (b.Surface.X0, b.Surface.Y0, b.Surface.Step, b.Surface.Width, b.Surface.Height));
        Assert.Equal(a.Surface.Ground, b.Surface.Ground);
        Assert.Equal(a.Surface.Top, b.Surface.Top);
        Assert.Equal(a.Surface.Water, b.Surface.Water);
        Assert.Equal(a.Surface.Material, b.Surface.Material);
        Assert.Equal(a.Surface.MaterialNames, b.Surface.MaterialNames);
        Assert.Equal(a.Surface.Zone, b.Surface.Zone);
        Assert.Equal(a.Surface.OnRoad, b.Surface.OnRoad);
        Assert.Equal(a.Surface.Street, b.Surface.Street);
        Assert.Equal(a.Zones, b.Zones);
        Assert.Equal(a.Streets, b.Streets);
        Assert.Equal(a.Nodes.Count, b.Nodes.Count);
        Assert.Equal(a.Nodes[0].JunctionArea, b.Nodes[0].JunctionArea);
        Assert.Equal(a.Nodes[1] with { JunctionArea = null }, b.Nodes[1] with { JunctionArea = null });
        Assert.Equal(a.Links, b.Links);
        Assert.Equal(a.Postals, b.Postals);
        Assert.Equal(a.Markers, b.Markers);
        Assert.Equal(a.Dots, b.Dots);
    }

    [Fact]
    public void TheAppBundlesTheSampleLand()
    {
        var d = SampleIslandFile.Bundled();
        var (x0, y0, x1, y1) = FxMapGenerator.Core.SampleIsland.SampleIsland.Rect;
        // the grid covers the cell
        Assert.True(d.Surface.X0 <= x0 && d.Surface.Y0 >= y0);
        Assert.True(d.Surface.X0 + (d.Surface.Width - 1) * d.Surface.Step >= x1 && d.Surface.Y0 - (d.Surface.Height - 1) * d.Surface.Step <= y1);
        Assert.Contains("TARMAC", d.Surface.MaterialNames);
        Assert.Contains("DOWNT", d.Surface.ZoneCodes);
        Assert.True(d.Nodes.Count > 500);
        Assert.True(d.Postals.Count > 20);
        Assert.All(d.Nodes, n => Assert.True(n.X >= x0 && n.X <= x1 + 200 && n.Y <= y0 && n.Y >= y1 - 200, n.Key));
        Assert.All(d.Links, l => Assert.Contains(d.Nodes, n => n.Key == l.From));
    }

    [Fact]
    public void TheBundledSampleLandBecomesAProjectWithEveryBlockScanned()
    {
        using var temp = new TempFolder();
        var data = SampleIslandFile.Bundled();
        var path = FxMapGenerator.Core.SampleIsland.SampleIsland.Create(temp.Path, data);
        var project = Project.Load(path);
        Assert.Equal(64, project.Range.Keys.Count());
        // the two bundled atlas styles in English and Japanese
        Assert.Equal(new[] { "atlas-postalcodemap-en", "atlas-postalcodemap-ja", "atlas-regional-en", "atlas-regional-ja" },
            project.Maps.Where(m => m.Kind == MapKind.Atlas).Select(m => m.Id));
        // its points: the groups named as the bundled ones are, the markers' labels
        var poi = FxMapGenerator.Core.Poi.PoiData.Of(project);
        Assert.Equal(new[] { "Markers", "Colored dots" }, poi.Groups.Select(g => g.Name.In("en")));
        Assert.Equal("マーカー", poi.Groups[0].Name.In("ja"));
        Assert.Equal(data.Markers.Select(m => m.Id), poi.Groups[0].Points.Select(p => p.Label["en"]));
        Assert.All(poi.Groups[1].Points, p => Assert.Empty(p.Label));
        var state = StateStore.Open(new WorkFolder(project.WorkFolderPath));
        Assert.All(FxMapGenerator.Core.SampleIsland.SampleIsland.Blocks, b =>
        {
            Assert.True(state.Has(b, BlockItem.ScanGround) && state.Has(b, BlockItem.ScanRoads), b.Name);
            Assert.True(File.Exists(new WorkFolder(project.WorkFolderPath).ScanFile(b)), b.Name);
        });
        Assert.False(GameFilesStage.IsStale(project, state));
        // nothing in the future: a road graph made at once is newer than the game files read
        Assert.True(state.StageDone(FxMapGenerator.Core.Planning.StageKeys.GameFiles, FxMapGenerator.Core.Planning.StageKeys.World) <= DateTime.UtcNow);
        var scan =ScanFile.Read(new WorkFolder(project.WorkFolderPath).ScanFile(new BlockId(4, 4)));
        Assert.True(scan.HasGround && scan.HasRoads);
        Assert.Equal(282, scan.N);
        Assert.All(scan.Missing.Values, m => Assert.Equal(0, m));
        var paths = PathFile.Read(Path.Combine(new WorkFolder(project.WorkFolderPath).Game, GameFilesOutput.Paths));
        Assert.Equal(data.Nodes.Count, paths.Nodes.Count);
        Assert.Equal(2 * data.Links.Count, paths.Links.Count);
    }
}
