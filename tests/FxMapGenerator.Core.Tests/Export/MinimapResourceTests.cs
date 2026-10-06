using System.Globalization;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Export;

/// <summary>
/// The minimap resource as FiveM runs it: its scripts in a small fake game under Lua 5.4 (tests/minimap/run-resource.lua).
/// Extra Map Tiles draws the textures beyond the standard frame where their cells are and stretches the pause map to
/// them; the zoom script sets the zoom levels, decides by the interior the player is in what the radar and the pause map
/// show, and gives the zoom and the zoom levels back when the resource stops.
/// </summary>
[Collection(nameof(ChildProcesses))]
public sealed class MinimapResourceTests
{
    /// <summary>Scaleform units per game metre on Extra Map Tiles' pause map (1728 units for the world's 9216 m).</summary>
    const double Unit = 1728 / 9216.0;

    /// <summary>Stand-ins of the textures the minimap stage makes, in a folder like <c>ytd/&lt;map&gt;/4096/</c>.</summary>
    static string Textures(TempFolder tmp, IEnumerable<CellId> extra)
    {
        var dir = tmp.File("ytd");
        Directory.CreateDirectory(dir);
        var names = MinimapSheets.All.SelectMany(s => new[] { s.SeaTexture, s.Texture }).Append(MinimapLod.Texture).Concat(extra.Select(MinimapExtraTiles.Texture));
        foreach (var n in names) File.WriteAllBytes(Path.Combine(dir, n + ".ytd"), [1, 2, 3]);
        return dir;
    }

    /// <param name="fixedZoom">The server's setting <see cref="MinimapResource.FixedZoomSetting"/> is on.</param>
    static string[] Run(string resource, bool fixedZoom = false)
    {
        var lua = RepoFiles.Lua54();
        Skip.If(lua is null, "no Lua 5.4 (lua on the PATH, or FXMAPGEN_LUA)");
        string[] args = fixedZoom ? ["tests/minimap/run-resource.lua", resource, "fixed-zoom"] : ["tests/minimap/run-resource.lua", resource];
        var (code, output) = RepoFiles.Run(lua!, args, RepoFiles.Root);
        Assert.True(code == 0, output);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>What the zoom script did in the scenes the fake game walks through: scene name -> the rest of its line.</summary>
    static Dictionary<string, string> Scenes(string[] lines) =>
        lines.Where(l => l.StartsWith("scene ", StringComparison.Ordinal)).Select(l => l.Split(' ', 3)).ToDictionary(p => p[1], p => p[2]);

    static void ZoomIsSetAndGivenBack(string[] lines)
    {
        Assert.Equal(new[]
        {
            "zoomlevel 0 0.9600 0.9000 0.0800 0.0000 0.0000", "zoomlevel 1 1.6000 0.9000 0.0800 0.0000 0.0000",
            "zoomlevel 2 8.6000 0.9000 0.0800 0.0000 0.0000", "zoomlevel 3 12.3000 0.9000 0.0800 0.0000 0.0000",
            "zoomlevel 4 22.3000 0.9000 0.0800 0.0000 0.0000",
        }, lines.Where(l => l.StartsWith("zoomlevel ", StringComparison.Ordinal)));
        // outside: the radar's zoom every frame, on foot (the loop's first frame only waits), and nothing else
        Assert.Equal("zoom=9:1100 hide=0 outside=0 picture=0 asked=0", Scenes(lines)["outside"]);
        // the zoom is given back and the zoom levels come back once, when this resource stops (not another), and the
        // radar loop ends
        Assert.Equal("stopzoom 1 0", lines.Single(l => l.StartsWith("stopzoom ", StringComparison.Ordinal)));
        Assert.Equal(new[] { "reset 0", "reset 1", "reset 2", "reset 3", "reset 4" }, lines.Where(l => l.StartsWith("reset ", StringComparison.Ordinal)));
        Assert.Equal("alive 0", lines[^1]);
    }

    [SkippableFact]
    public void TheRadarAndThePauseMapShowWhatTheInteriorThePlayerIsInHas()
    {
        using var tmp = new TempFolder();
        MinimapResource.Write(tmp.Path, "res", Textures(tmp, []), MapSet.Satellite, "Satellite", "Test", "9.9.9", [], TestGame.Interiors, islandMap: null);
        var lines = Run(tmp.File("res"));
        var scenes = Scenes(lines);
        Assert.Equal(new[] { "outside", "no-picture", "home", "home-pause-interior", "home-pause-outside", "home-vehicle", "drain", "drain-swimming", "outside-again" },
            lines.Where(l => l.StartsWith("scene ", StringComparison.Ordinal)).Select(l => l.Split(' ')[1]));

        // an interior without a radar picture is as outside: the zoom 1100; the game is asked once what the interior is
        Assert.Equal("zoom=10:1100 hide=0 outside=0 picture=0 asked=1", scenes["no-picture"]);
        // an interior with a picture of its own: the zoom is given back to the game (0), on foot and in a vehicle; the
        // outside map is hidden only while the pause map is in its interior view, never for the radar alone
        Assert.Equal("zoom=10:0 hide=0 outside=0 picture=0 asked=1", scenes["home"]);
        Assert.Equal("zoom=10:0 hide=10 outside=0 picture=0 asked=0", scenes["home-pause-interior"]);
        Assert.Equal("zoom=10:0 hide=0 outside=0 picture=0 asked=0", scenes["home-pause-outside"]);
        Assert.Equal("zoom=10:0 hide=0 outside=0 picture=0 asked=0", scenes["home-vehicle"]);
        // an interior a picture of underground passages stands for: the zoom stays 1100, the outside map is kept and
        // the picture is added at its row's place (the picture's hash, x, y, 0, 0)
        var row = TestGame.Interiors.Underground.Single(r => r.Interiors.Contains("fxtest_drain"));
        string picture = string.Create(CultureInfo.InvariantCulture, $"picture=10:{InteriorMaps.Hash(row.Name)}:{row.X:0.0000}:{row.Y:0.0000}:0:0");
        Assert.Equal($"zoom=10:1100 hide=0 outside=10 {picture} asked=1", scenes["drain"]);
        // swimming, the zoom is not set (the one set before stays in the game); the picture is still added
        Assert.Equal($"zoom=0:nil hide=0 outside=10 {picture} asked=0", scenes["drain-swimming"]);
        // outside again: as at the start, and the game is not asked about no interior
        Assert.Equal("zoom=10:1100 hide=0 outside=0 picture=0 asked=0", scenes["outside-again"]);
        ZoomIsSetAndGivenBack(lines);
    }

    [SkippableFact]
    public void WithTheServersSettingTheZoomIsFixedEverywhereAndNothingElseIsDone()
    {
        using var tmp = new TempFolder();
        MinimapResource.Write(tmp.Path, "res", Textures(tmp, []), MapSet.Satellite, "Satellite", "Test", "9.9.9", [], TestGame.Interiors, islandMap: null);
        var lines = Run(tmp.File("res"), fixedZoom: true);
        var scenes = Scenes(lines);
        Assert.Equal(9, scenes.Count);
        foreach (var (name, did) in scenes)
        {
            string zoom = name == "outside" ? "zoom=9:1100" : name == "drain-swimming" ? "zoom=0:nil" : "zoom=10:1100";
            Assert.Equal($"{zoom} hide=0 outside=0 picture=0 asked=0", did);
        }
        ZoomIsSetAndGivenBack(lines);
    }

    [SkippableFact]
    public void TheTexturesBeyondTheStandardFrameAreDrawnOnTheirCellsAndThePauseMapReachesThem()
    {
        using var tmp = new TempFolder();
        // Cayo Perico's: east of the standard frame (row 5, column 4) and south of it (row 6); one north-west of it
        var extra = new[] { new CellId(5, 4), new CellId(6, 3), new CellId(6, 4), new CellId(-1, -1) };
        MinimapResource.Write(tmp.Path, "res", Textures(tmp, extra), MapSet.Satellite, "Satellite", "Test", "9.9.9", extra, TestGame.Interiors, islandMap: [7, 7, 7]);
        var lines = Run(tmp.File("res"));

        var tiles = lines.Where(l => l.StartsWith("tile ", StringComparison.Ordinal)).Select(l => l.Split(' ')).ToDictionary(p => p[2]);
        Assert.Equal(extra.Select(MinimapExtraTiles.Texture).Order(StringComparer.Ordinal), tiles.Keys.Order(StringComparer.Ordinal));
        foreach (var cell in extra)
        {
            var p = tiles[MinimapExtraTiles.Texture(cell)];
            // on the pause map's Scaleform, the world's (0, 0) is at (864, 1440) and north is up
            double x = WorldGrid.Left + cell.C * WorldGrid.CellSize, y = WorldGrid.Top - cell.R * WorldGrid.CellSize;
            Assert.Equal(("minimap_main_map", p[2], p[2]), (p[1], p[3], p[4]));
            Assert.Equal(864 + x * Unit, D(p[5]), 3);
            Assert.Equal(1440 - y * Unit, D(p[6]), 3);
            Assert.Equal(WorldGrid.CellSize * Unit, D(p[7]), 3);                                   // 2250 m wide and high
            Assert.Equal(WorldGrid.CellSize * Unit, D(p[8]), 3);
            Assert.Equal(("false", "100", 0.0), (p[9], p[10], D(p[11])));
        }
        // the cells side by side meet without a gap or an overlap
        Assert.Equal(D(tiles["fxmapgen_extra_6_3"][5]) + D(tiles["fxmapgen_extra_6_3"][7]), D(tiles["fxmapgen_extra_6_4"][5]), 3);
        Assert.Equal(D(tiles["fxmapgen_extra_5_4"][6]) + D(tiles["fxmapgen_extra_5_4"][8]), D(tiles["fxmapgen_extra_6_4"][6]), 3);

        // the invisible blips at the corners of all tiles: west -6390 (column -1), east 7110 (column 4 + 1), south -7350 (row 6 + 1), north 10650 (row -1)
        Assert.Equal(new[] { "blip -6390.0000 -7350.0000", "blip 7110.0000 10650.0000" }, lines.Where(l => l.StartsWith("blip ", StringComparison.Ordinal)));
        // with tiles, the radar's masks are replaced; the island map is registered
        Assert.Equal(new[] { "replace radarmasksm radar_masks radarmasksm", "replace radarmasklg radar_masks radarmasklg" },
            lines.Where(l => l.StartsWith("replace ", StringComparison.Ordinal)));
        Assert.Equal(new[] { "datafile SCALEFORM_DLC_FILE stream/int3232302352.gfx" }, lines.Where(l => l.StartsWith("datafile ", StringComparison.Ordinal)));
        ZoomIsSetAndGivenBack(lines);
    }

    [SkippableFact]
    public void TheStandardFrameAloneDrawsNoTilesAndKeepsTheRadarMasks()
    {
        using var tmp = new TempFolder();
        MinimapResource.Write(tmp.Path, "res", Textures(tmp, []), MapSet.Satellite, "Satellite", "Test", "9.9.9", [], TestGame.Interiors, islandMap: null);
        var lines = Run(tmp.File("res"));
        Assert.DoesNotContain(lines, l => l.StartsWith("tile ", StringComparison.Ordinal) || l.StartsWith("blip ", StringComparison.Ordinal)
            || l.StartsWith("replace ", StringComparison.Ordinal) || l.StartsWith("datafile ", StringComparison.Ordinal));
        ZoomIsSetAndGivenBack(lines);
    }
}
