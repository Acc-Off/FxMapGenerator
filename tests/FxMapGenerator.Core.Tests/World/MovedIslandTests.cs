using System.Globalization;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.SampleIsland;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.World;

/// <summary>
/// The negative numbers from end to end: the sample island made in its cell, and again moved two cells to the north-west
/// (its blocks, cells and tiles numbered below zero) in a project whose frame has two cells added above and to the left.
/// Every step without the game makes the same tiles, only numbered two cells apart; written out, both start at their
/// frame's north-west corner and carry the same numbers. (Two cells, 4500 m, keep the region colours' 4 m grid and the
/// tiles of zoom 2 on the same boundaries around the island; one cell would not.) The same up to the rounding of the
/// positions 4500 m away: a few tiles differ on the edges of letters and lines, never on more than 1 % of their pixels,
/// where a wrong number would leave a tile out or move whole blocks.
/// </summary>
public sealed class MovedIslandTests
{
    const double Move = 2 * WorldGrid.CellSize;
    const string Map = "atlas-postalcodemap-en";

    static IslandData Moved(IslandData d, double dx, double dy)
    {
        var s = d.Surface;
        return new IslandData
        {
            Surface = new IslandSurface
            {
                X0 = s.X0 + dx, Y0 = s.Y0 + dy, Step = s.Step, Width = s.Width, Height = s.Height, Ground = s.Ground, Top = s.Top, Water = s.Water,
                Probe = s.Probe, Material = s.Material, MaterialNames = s.MaterialNames, Zone = s.Zone, ZoneCodes = s.ZoneCodes, OnRoad = s.OnRoad, Street = s.Street,
            },
            Nodes = d.Nodes.Select(n => n with { X = n.X + dx, Y = n.Y + dy, JunctionArea = n.JunctionArea is { } j ? [j[0] + dx, j[1] + dy, j[2] + dx, j[3] + dy] : null }).ToList(),
            Links = d.Links,
            Streets = d.Streets,
            Zones = d.Zones,
            Postals = d.Postals.Select(p => (p.Code, p.X + dx, p.Y + dy)).ToList(),
            Markers = d.Markers.Select(m => (m.Id, m.X + dx, m.Y + dy)).ToList(),
            Dots = d.Dots.Select(m => (m.Id, m.X + dx, m.Y + dy, m.Color)).ToList(),
        };
    }

    /// <summary>The island's project in a folder: its atlas map in the PostalCodeMap style in English, every step without the game, then its web tiles written out.</summary>
    static async Task Make(string folder, IslandData data, CellId cell, ExtraCellsSetting? extra)
    {
        var path = FxMapGenerator.Core.SampleIsland.SampleIsland.Create(folder, data, cell: cell, extraCells: extra);
        var p = Project.Load(path);
        p.File.Maps.Atlas.Styles = [AtlasPresets.PostalCodeMap];
        p.File.Maps.Atlas.Languages = ["en"];
        p.Save();
        var build = await JobRunner.Create(new JobSetup
        {
            ProjectPath = path, Workers = 4, Processors = 4, Memory = new FakeMemory(),
            Stages = (pp, s) => BuildStages.For(pp, s),
        }).RunAsync();
        Assert.True(build.State == JobState.Done && build.FailureCount == 0, build.Error ?? string.Join("; ", build.Failures.Select(f => $"{f.Stage} {f.Unit}: {f.Message}")));
        var export = await JobRunner.Create(new JobSetup
        {
            ProjectPath = path, Workers = 4, Processors = 4, Memory = new FakeMemory(),
            Stages = (_, _) => new BuildPlan([new ExportStage(new ExportOptions(Path.Combine(folder, "out"), [Map], false, null, false), "9.9.9")], []),
        }).RunAsync();
        Assert.Equal(JobState.Done, export.State);
    }

    static string Tile(string root, int z, int x, int y) =>
        Path.Combine(root, z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture) + ".png");

    /// <summary>How two tiles differ: the pixels with any channel apart and the largest step between them.</summary>
    static (int Pixels, int Largest) Difference(byte[] a, byte[] b)
    {
        var pa = FxMapGenerator.Core.Imaging.Images.DecodeRgba(a, out _, out _);
        var pb = FxMapGenerator.Core.Imaging.Images.DecodeRgba(b, out _, out _);
        int pixels = 0, largest = 0;
        for (int i = 0; i < pa.Length; i += 4)
        {
            int d = 0;
            for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(pa[i + c] - pb[i + c]));
            if (d > 0) pixels++;
            largest = Math.Max(largest, d);
        }
        return (pixels, largest);
    }

    [Fact]
    public async Task TheSampleIslandMovedTwoCellsNorthWestMakesTheSameTiles()
    {
        // the folders stay when tiles differ, to look at them
        var tmp = new TempFolder();
        var data = SampleIslandFile.Bundled();
        await Make(tmp.File("here"), data, FxMapGenerator.Core.SampleIsland.SampleIsland.Cell, null);
        await Make(tmp.File("moved"), Moved(data, -Move, Move), new CellId(-2, -2), new ExtraCellsSetting { Top = 2, Left = 2 });

        string here = new WorkFolder(tmp.File("here")).Tiles(Map), moved = new WorkFolder(tmp.File("moved")).Tiles(Map);
        string webHere = Path.Combine(tmp.File("here"), "out", "web", "tiles", "atlas-postalcodemap");
        string webMoved = Path.Combine(tmp.File("moved"), "out", "web", "tiles", "atlas-postalcodemap");
        const int Tolerated = 256 * 256 / 100;
        var wrong = new List<string>();
        int compared = 0, same = 0;
        void Compare(string what, string pathA, string pathB)
        {
            var a = File.ReadAllBytes(pathA);
            var b = File.ReadAllBytes(pathB);
            compared++;
            if (a.AsSpan().SequenceEqual(b))
            {
                same++;
                return;
            }
            var (pixels, largest) = Difference(a, b);
            if (pixels > Tolerated) wrong.Add($"{what}: {pixels} px up to {largest}");
        }
        for (int z = 2; z <= WorldGrid.Zoom; z++)
        {
            int d = 64 >> (WorldGrid.Zoom - z);                      // two cells in tiles of the zoom
            int n = Math.Max(1, 32 >> (WorldGrid.Zoom - z));         // the island's cell in tiles of the zoom
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    Compare($"work folder z{z} {x}/{y}", Tile(here, z, x, y), Tile(moved, z, x - d, y - d));
                    Compare($"written out z{z} {x}/{y}", Tile(webHere, z, x, y), Tile(webMoved, z, x, y));
                }
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {compared} tiles differ on more than 1 % ({tmp.Path} kept): {string.Join("; ", wrong.Take(40))}");
        Assert.True(same >= compared * 9 / 10, $"only {same} of {compared} tiles are the same bytes ({tmp.Path} kept)");
        // the moved project's tiles are where its frame starts: nothing written out below zero
        Assert.DoesNotContain(Directory.EnumerateFiles(webMoved, "*.png", SearchOption.AllDirectories), f => f.Contains($"{Path.DirectorySeparatorChar}-", StringComparison.Ordinal));
        Assert.True(File.Exists(Tile(moved, 8, -64, -64)));
        tmp.Dispose();
    }
}
