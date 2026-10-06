using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.GameFiles;

namespace FxMapGenerator.Core.Tests.Export;

/// <summary>
/// Stand-ins of what a minimap resource takes from the game's files: the tests read no game, and what they write must
/// not depend on whether this PC has one.
/// </summary>
static class TestGame
{
    /// <summary>
    /// Interior maps with the interiors the fake game walks through (tests/minimap/run-resource.lua): one with a picture
    /// of its own (<c>fxtest_home</c>) and one a picture of underground passages stands for (<c>fxtest_drain</c>); two of
    /// the four pictures of underground passages are not in this game's table. The names are chosen for their hashes:
    /// those of <c>fxtest_home</c>, <c>fxtest_drain</c> and <c>V_FakeWaterTunnel</c> are above 2^31, which a script gets
    /// as negative numbers.
    /// </summary>
    public static readonly InteriorMaps.Data Interiors = new(
        new[] { 4242u, InteriorMaps.Hash("fxtest_home"), InteriorMaps.Hash("fxtest_garage") }.Order().ToList(), 2,
        [new("V_FakeMetro", -200, -1500, ["fxtest_metro"]), new("V_FakeWaterTunnel", -1500, -875, ["fxtest_drain", "fxtest_canal"])],
        ["V_FakeTunnel_SC1", "V_FakeTunnel_ID1"]);

    /// <summary>The parts given in place of a game.</summary>
    /// <param name="islandMap">The island map for a project that reads Cayo Perico's roads.</param>
    public static MinimapGameFiles Given(byte[]? islandMap = null) => MinimapGameFiles.Given(Interiors, islandMap);

    /// <summary>A PC without GTA V and without keys.</summary>
    /// <param name="scratch">A folder of the test's own.</param>
    public static MinimapGameFiles NoGame(string scratch) =>
        MinimapGameFiles.Of(new GameFilesLocation.Defaults(Path.Combine(scratch, "nowhere"), Path.Combine(scratch, "nokeys")));

    /// <summary>A PC with a folder that passes for GTA V's, and without keys.</summary>
    /// <param name="scratch">A folder of the test's own.</param>
    public static MinimapGameFiles NoKeys(string scratch)
    {
        var gta = Path.Combine(scratch, "gta");
        Directory.CreateDirectory(gta);
        File.WriteAllBytes(Path.Combine(gta, "GTA5.exe"), []);
        return MinimapGameFiles.Of(new GameFilesLocation.Defaults(gta, Path.Combine(scratch, "nokeys")));
    }
}
