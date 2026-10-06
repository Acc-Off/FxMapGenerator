using FxMapGenerator.Core.Projects;

namespace FxMapGenerator.Core.Tests.Projects;

public sealed class ServerPresetsTests
{
    [Fact]
    public void BundledPresetsAreQBCoreAndQbox()
    {
        Assert.Equal(new[] { "qbcore", "qbox" }, ServerPresets.All.Select(p => p.Id));
        var qbox = ServerPresets.Find("qbox")!;
        Assert.Equal("Qbox", qbox.Name);
        Assert.Equal(new[] { "qbx_hud", "chat", "Renewed-Weathersync", "qbx_density" }, qbox.StopResources);
        var qbcore = ServerPresets.Find("qbcore")!;
        Assert.Equal("QBCore", qbcore.Name);
        Assert.Equal(new[] { "qb-hud", "chat", "qb-weathersync", "qb-smallresources" }, qbcore.StopResources);
        Assert.Null(ServerPresets.Find("esx"));
    }

    [Fact]
    public void ProjectStopsItsOwnListElseThePresets()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("a.fxmapgen.json"));
        Assert.Equal(ServerPresets.Find("qbox")!.StopResources, p.StopResources);
        p.File.Server.Preset = "qbcore";
        Assert.Equal(ServerPresets.Find("qbcore")!.StopResources, p.StopResources);
        p.File.Server.StopResources = new() { "qbx_hud", "NearestPostal" };
        Assert.Equal(new[] { "qbx_hud", "NearestPostal" }, p.StopResources);
    }

    [Theory]
    [InlineData("qbx_hud", true)]
    [InlineData("Renewed-Weathersync", true)]
    [InlineData("my.hud", true)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("hud;quit", false)]
    [InlineData("\"hud\"", false)]
    public void ResourceNamesTheConsoleTakesAsOne(string name, bool ok) => Assert.Equal(ok, ServerPresets.IsResourceName(name));

    [Fact]
    public void ValidationRefusesAnUnknownPresetAndBadResourceNames()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("a.fxmapgen.json"));
        Assert.Empty(p.Validate());
        p.File.Server.Preset = "esx";
        p.File.Server.StopResources = new() { "ok_name", "bad;name" };
        var problems = p.Validate();
        Assert.Contains("server preset 'esx' (qbcore or qbox)", problems);
        Assert.Contains("'bad;name' is not a resource name (resources to stop)", problems);
        Assert.Equal(2, problems.Count);
    }
}
