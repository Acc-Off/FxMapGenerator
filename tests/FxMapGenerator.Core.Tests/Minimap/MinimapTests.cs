using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.World;
using FxMapGenerator.GameData.Textures;

namespace FxMapGenerator.Core.Tests.Minimap;

public sealed class MinimapTests
{
    static readonly DateTime T0 = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SheetsCoverTheMapFrameInSquaresOf4500Metres()
    {
        Assert.Equal(new SheetId(2, 0), SheetId.At(195, -934));           // Legion Square
        Assert.Equal(new SheetId(0, 0), SheetId.At(-4140, 8400));
        Assert.Equal(new SheetId(2, 1), SheetId.At(4859, -5099));
        Assert.Null(SheetId.At(4861, 0));
        Assert.Equal((-4140.0, 8400.0, 360.0, 3900.0), new SheetId(0, 0).Rect);
        Assert.Equal(("minimap_sea_1_0", "minimap_1_0"), (new SheetId(1, 0).SeaTexture, new SheetId(1, 0).Texture));
        Assert.Equal((4096, 6), (MinimapSheets.Size, MinimapSheets.Zoom));   // 16 x 16 tiles of zoom 6 a sheet
    }

    static byte[] Flat(byte r, byte g, byte b, byte a)
    {
        var t = new byte[TileStore.TileSize * TileStore.TileSize * 4];
        for (int i = 0; i < t.Length; i += 4) { t[i] = r; t[i + 1] = g; t[i + 2] = b; t[i + 3] = a; }
        return t;
    }

    [Fact]
    public async Task OneSheetIsPutTogetherFromItsTilesAndWrittenAsBothDictionaries()
    {
        using var tmp = new TempFolder();
        var p = Project.Create(tmp.File("p.fxmapgen.json"));
        p.File.Range.Base = "none";
        p.File.Minimap.Map = "satellite";
        p.Save();
        var folder = new WorkFolder(tmp.Path);
        var tiles = new TileStore(folder.Tiles("satellite"));
        // sheet 2_0 is z6 x 0..15, y 32..47: one red tile at (3, 40) and a see-through green one in its last corner
        tiles.Write(6, 3, 40, Flat(200, 30, 20, 255));
        tiles.Write(6, 15, 47, Flat(20, 180, 40, 90));
        tiles.Write(6, 16, 40, Flat(0, 0, 255, 255));                        // sheet 2_1: not in this one
        var state = StateStore.Open(folder);
        state.SetStageDone(StageKeys.LowZoom, "satellite", T0);
        state.SetStageDone(StageKeys.Ytd, MinimapSheets.All.Where(s => s.Name != "2_0").Select(s => StageKeys.YtdUnit("satellite", s.Name)), T0.AddMinutes(1));
        MinimapStage.WriteRecord(p, "satellite");

        JobRunner Runner() => JobRunner.Create(new JobSetup
        {
            ProjectPath = p.FilePath,
            Workers = 4,
            Processors = 4,
            Memory = new FakeMemory(),
            Stages = (_, _) => new BuildPlan([new MinimapStage(MapSet.Satellite)], []),
        });
        var end = await Runner().RunAsync();
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal((2, 2), (end.Stages[0].Total, end.Stages[0].Done));          // the sheet and the small whole map

        var dir = folder.Minimap("satellite");
        var sea = Assert.Single(YtdFile.Read(Path.Combine(dir, "minimap_sea_2_0.ytd")));
        var plain = Assert.Single(YtdFile.Read(Path.Combine(dir, "minimap_2_0.ytd")));
        Assert.Equal(("minimap_sea_2_0", 4096, 4096, 1, YtdFile.FourCc(YtdFile.Kind.Dxt5)), ((string)sea.Name!, (int)sea.Width, (int)sea.Height, (int)sea.Levels, sea.Format));
        Assert.Equal(("minimap_2_0", 4096, 4096, 1, YtdFile.FourCc(YtdFile.Kind.Dxt1a)), ((string)plain.Name!, (int)plain.Width, (int)plain.Height, (int)plain.Levels, plain.Format));

        var a = YtdFile.Decompress(sea.Data!.FullData, 4096, 4096, sea.Format);
        var b = YtdFile.Decompress(plain.Data!.FullData, 4096, 4096, plain.Format);
        (byte, byte, byte, byte) At(byte[] px, int x, int y) { int i = 4 * (y * 4096 + x); return (px[i], px[i + 1], px[i + 2], px[i + 3]); }
        void Near((byte, byte, byte, byte) got, (int, int, int, int) want, int tol)
        {
            Assert.InRange(got.Item1, want.Item1 - tol, want.Item1 + tol);
            Assert.InRange(got.Item2, want.Item2 - tol, want.Item2 + tol);
            Assert.InRange(got.Item3, want.Item3 - tol, want.Item3 + tol);
            Assert.InRange(got.Item4, want.Item4 - tol, want.Item4 + tol);
        }
        int rx = 3 * 256 + 128, ry = (40 - 32) * 256 + 128;
        Near(At(a, rx, ry), (200, 30, 20, 255), 8);
        Near(At(b, rx, ry), (200, 30, 20, 255), 8);
        Near(At(a, 15 * 256 + 128, 15 * 256 + 128), (20, 180, 40, 90), 8);     // DXT5 keeps the alpha
        Near(At(b, 15 * 256 + 128, 15 * 256 + 128), (20, 180, 40, 255), 8);     // DXT1: what is not transparent is opaque
        Assert.Equal(0, At(a, 100, 100).Item4);                                  // no tile: see-through
        Assert.Equal(0, At(b, 4095, 0).Item4);                                   // the other sheet's tile is not here

        // the small whole map: 128 px, one level, opaque (no zoom 2 tile here: the satellite map's black under it)
        var lod = Assert.Single(YtdFile.Read(Path.Combine(dir, "minimap_lod_128.ytd")));
        Assert.Equal(("minimap_lod_128", 128, 128, 1, YtdFile.FourCc(YtdFile.Kind.Dxt5)), ((string)lod.Name!, (int)lod.Width, (int)lod.Height, (int)lod.Levels, lod.Format));
        var lp = YtdFile.Decompress(lod.Data!.FullData, 128, 128, lod.Format);
        Assert.All(Enumerable.Range(0, 128 * 128), i => Assert.Equal(255, lp[4 * i + 3]));

        Assert.Equal(0, (await Runner().RunAsync()).Stages[0].Total);             // made: nothing left
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        // the other outside makes every sheet again; back to the one they were made with, nothing is left
        var after = StateStore.Open(folder);                                    // with the runs' sheets
        p.File.Minimap.Outside = MinimapOutside.Transparent;
        Assert.All(MinimapSheets.All, s => Assert.True(MinimapStage.IsStale(p, after, "satellite", s)));
        p.File.Minimap.Outside = MinimapOutside.Map;
        Assert.All(MinimapSheets.All, s => Assert.False(MinimapStage.IsStale(p, after, "satellite", s)));

        // the map made again since (its lower zooms newer): a sheet is left only when the tiles it is made of changed
        var made = after.StageDone(StageKeys.Ytd, StageKeys.YtdUnit("satellite", "2_0"))!.Value;
        after.SetStageDone(StageKeys.LowZoom, "satellite", made.AddMinutes(1));
        Assert.False(MinimapStage.IsStale(p, after, "satellite", new SheetId(2, 0)));
        Assert.True(MinimapStage.IsStale(p, after, "satellite", new SheetId(2, 1)));   // no record of its tiles: not made by a run
        tiles.Write(6, 3, 40, Flat(201, 30, 20, 255));
        Assert.True(MinimapStage.IsStale(p, after, "satellite", new SheetId(2, 0)));
        Assert.Equal(new SheetId(2, 1), MinimapSheets.OfCell(new CellId(5, 3)));
        p.File.Minimap.Outside = "clear";
        Assert.Contains("the minimap outside the range 'clear' (map or transparent)", p.Validate());
    }

    [Fact]
    public void OutsideTheRangeASheetCanBeLeftTransparent()
    {
        using var tmp = new TempFolder();
        var tiles = new TileStore(tmp.Path);
        tiles.Write(6, 3, 40, Flat(200, 30, 20, 255));                         // block z8_12_160, in the range
        tiles.Write(6, 15, 47, Flat(20, 180, 40, 255));                        // block z8_60_188, outside
        var only = new HashSet<BlockId> { BlockId.Parse("z8_12_160") };
        var all = MinimapSheets.Compose(tiles, new SheetId(2, 0), new FixedParallel(2));
        var inRange = MinimapSheets.Compose(tiles, new SheetId(2, 0), new FixedParallel(2), only);
        int red = 4 * ((8 * 256 + 128) * 4096 + 3 * 256 + 128), green = 4 * ((15 * 256 + 128) * 4096 + 15 * 256 + 128);
        Assert.Equal((200, 255), (all[red], all[red + 3]));
        Assert.Equal((200, 255), (inRange[red], inRange[red + 3]));
        Assert.Equal((20, 255), (all[green], all[green + 3]));
        Assert.Equal((0, 0), (inRange[green], inRange[green + 3]));
    }
}
