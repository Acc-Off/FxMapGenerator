using System.Text.RegularExpressions;
using System.Xml.Linq;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.Tests.Capture;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Resource;

/// <summary>
/// The FiveM resource (resource/fxmapgen-capture) against this program: the same version, and the lines it prints are the
/// lines the capture readers take. The resource's own tests run in a fake game under Lua 5.4 (tests/resource).
/// </summary>
[Collection(nameof(ChildProcesses))]
public sealed class CaptureResourceTests
{
    [Fact]
    public void ManifestVersionIsTheProgramVersion()
    {
        var version = XDocument.Load(RepoFiles.Path("Directory.Build.props")).Descendants("Version").Single().Value;
        var manifest = File.ReadAllText(RepoFiles.Path("resource", "fxmapgen-capture", "fxmanifest.lua"));
        Assert.Matches(new Regex("(?m)^version '" + Regex.Escape(version) + "'\r?$"), manifest);
    }

    [SkippableFact]
    public async Task ResourceTestsPassAndTheirCaptureIsReadByTheCaptureReaders()
    {
        var lua = RepoFiles.Lua54();
        Skip.If(lua is null, "no Lua 5.4 (lua on the PATH, or FXMAPGEN_LUA)");
        using var tmp = new TempFolder();
        var (code, output) = RepoFiles.Run(lua!, ["tests/resource/run-tests.lua", "--emit", tmp.Path], RepoFiles.Root);
        Assert.True(code == 0, output);

        // the READY line as saved next to a shot
        var block = BlockId.Parse("z8_60_132");
        var cam = CameraLine.Read(tmp.File("z8_60_132.cam.txt"));
        Assert.Equal(block.Center.X, cam.X, 4);
        Assert.Equal(block.Center.Y, cam.Y, 4);
        Assert.Equal(2.0, cam.Fov, 3);
        Assert.Equal(1.06, cam.Margin, 3);
        Assert.Equal(1.06 * WorldGrid.BlockSize / 2 / Math.Tan(Math.PI / 180), cam.Height, 3); // the block and its margin fill the frame's height

        // the height grid: 1 m samples over the block from its north-west corner
        var grid = HeightGrid.Read(tmp.File("z8_60_132.hmap"));
        var (x0, y0, _, _) = block.Rect;
        Assert.Equal((282, x0, y0, WorldGrid.BlockSize, 1.0), (grid.N, grid.X0, grid.Y0, grid.Size, grid.Step));
        Assert.Equal((block.Tx, block.Ty), (grid.Tx, grid.Ty));
        Assert.InRange(grid.Missing, 1, 100); // the fake world's collision gaps come through as missing heights
        Assert.Equal(282 * 282, grid.Values.Length);

        // the scans (ground, then roads) as the scan reader takes them: grids over the same block, materials by class
        var scan = FxMapGenerator.Core.Scan.ScanFile.Read(tmp.File("z8_60_132.scan.txt"));
        Assert.True(scan.HasGround && scan.HasRoads);
        Assert.Equal((282, x0, y0, 1.0, 71, 4.0, 282, 1.0), (scan.N, scan.X0, scan.Y0, scan.Step, scan.RoadN, scan.RoadStep, scan.OnRoadN, scan.OnRoadStep));
        Assert.Equal((8, block.Tx, block.Ty), (scan.Z, scan.Tx, scan.Ty));
        Assert.Empty(scan.Missing.Where(kv => kv.Value > 0));                       // every row arrived
        Assert.Null(scan.Canopy);                                                    // no canopy probe asked
        var classes = FxMapGenerator.Core.Scan.Materials.Default;
        var byClass = scan.Material.GroupBy(classes.ClassOf).ToDictionary(g => classes.Classes[g.Key], g => g.Count());
        Assert.Equal(new[] { "concrete", "dirt", "grass", "tarmac" }, byClass.Keys.Order());  // the pond's bottom is mud
        Assert.True(byClass["tarmac"] > 1000 && byClass["concrete"] > 1000 && byClass["grass"] > 1000, string.Join(", ", byClass));
        Assert.Equal(1600, scan.Water.Count(float.IsFinite));                      // the pond: 40 x 40 points
        Assert.Equal(scan.Water.Count(float.IsFinite), scan.Probe.Count(float.IsFinite)); // its water collision
        Assert.Equal(new[] { "Fake Ave", "Fake St" }, scan.StreetNames.Values.Where(n => n.Length > 0).Order());
        Assert.Equal(new[] { ("LEGSQU", "Legion Square"), ("PBOX", "Pillbox Hill") }, scan.Zones.Order());
        Assert.InRange(scan.OnRoad.Count(v => v == 1), 15000, 40000);                // the 12 m streets every 100 m

        // the fake game of the capture tests answers with the same lines: the same keys in the same order
        using var game = new FakeGame();
        var lines = new List<string>();
        var console = game.OpenConsole("127.0.0.1", 0);
        console.LineReceived += l => { lock (lines) lines.Add(l); };
        console.Start();
        await console.SendAsync("fxmapgen tile 8 60 132 2 1.06");
        string Wait(string word)
        {
            for (int i = 0; i < 500; i++)
            {
                lock (lines) if (lines.FirstOrDefault(l => l.StartsWith(ProtocolLine.Prefix + word, StringComparison.Ordinal)) is { } hit) return hit;
                Thread.Sleep(10);
            }
            throw new TimeoutException(word);
        }
        Assert.Equal(Keys(File.ReadAllText(tmp.File("z8_60_132.cam.txt"))), Keys(Wait("READY ")));
        await console.SendAsync("fxmapgen hmap 1");
        Assert.Equal(Keys(File.ReadLines(tmp.File("z8_60_132.hmap")).First()), Keys(Wait("HMAP BEGIN ")));
    }

    static List<string> Keys(string line) => Regex.Matches(line, @"(\w+)=").Select(m => m.Groups[1].Value).ToList();
}
