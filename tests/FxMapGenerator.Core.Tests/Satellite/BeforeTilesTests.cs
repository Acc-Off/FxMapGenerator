using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Satellite;

public sealed class BeforeTilesTests
{
    static byte[] Flat(byte v)
    {
        var t = new byte[TileStore.TileSize * TileStore.TileSize * 4];
        for (int i = 0; i < t.Length; i += 4) (t[i], t[i + 1], t[i + 2], t[i + 3]) = (v, v, v, 255);
        return TileStore.EncodePng(t, TileStore.TileSize, TileStore.TileSize);
    }

    [Fact]
    public void ATileWrittenOverKeepsItsFirstBytesAndTheSameBytesAreNotWritten()
    {
        using var tmp = new TempFolder();
        var folder = new WorkFolder(tmp.Path);
        var tiles = TileStore.Keeping(folder, "roadmap");
        tiles.WriteBytes(8, 10, 20, Flat(10));                                   // new: nothing to keep
        Assert.Null(BeforeTiles.Of(folder, "roadmap"));
        var path = tiles.PathOf(8, 10, 20);
        var time = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, time.AddHours(-1));
        tiles.WriteBytes(8, 10, 20, Flat(10));                                   // the same bytes: the file and its time stay
        Assert.Equal(time.AddHours(-1), File.GetLastWriteTimeUtc(path));
        Assert.Null(BeforeTiles.Of(folder, "roadmap"));

        tiles.WriteBytes(8, 10, 20, Flat(20));
        tiles.WriteBytes(8, 10, 20, Flat(30));                                   // only the first bytes are kept
        var kept = Path.Combine(folder.Before("roadmap"), "8", "10", "20.png");
        Assert.Equal(Flat(10), File.ReadAllBytes(kept));
        Assert.Equal(Flat(30), File.ReadAllBytes(path));
        var changed = BeforeTiles.Of(folder, "roadmap")!;
        Assert.Equal(1, changed.Tiles);
        Assert.Equal(new[] { 10, 20 }, changed.Z8);
        Assert.Equal(new ChangedArea(WorldGrid.Left + 2 * WorldGrid.BlockSize, WorldGrid.Top - 5 * WorldGrid.BlockSize,
            WorldGrid.Left + 3 * WorldGrid.BlockSize, WorldGrid.Top - 6 * WorldGrid.BlockSize, 1, false), Assert.Single(changed.Areas));

        // a store without the folder keeps nothing
        var plain = new TileStore(folder.Tiles("atlas"));
        plain.WriteBytes(8, 0, 0, Flat(1));
        plain.WriteBytes(8, 0, 0, Flat(2));
        Assert.Null(BeforeTiles.Of(folder, "atlas"));
    }

    [Fact]
    public void PlacesWhoseChangeDoesNotShowComeLast()
    {
        using var tmp = new TempFolder();
        var folder = new WorkFolder(tmp.Path);
        var tiles = TileStore.Keeping(folder, "roadmap");
        // block (2, 5): 2 steps of 256 (does not show); block (10, 10): 3 steps (shows); block (20, 20): 2 tiles, 1 and 40
        foreach (var (x, y, a, b) in new[] { (8, 20, 100, 102), (40, 40, 100, 103), (80, 80, 50, 51), (81, 80, 50, 90) })
        {
            tiles.WriteBytes(8, x, y, Flat((byte)a));
            tiles.WriteBytes(8, x, y, Flat((byte)b));
        }
        Assert.Equal(2, BeforeTiles.Difference(Path.Combine(folder.Before("roadmap"), "8", "8", "20.png"), tiles.PathOf(8, 8, 20)));
        Assert.Equal(3, BeforeTiles.Difference(Path.Combine(folder.Before("roadmap"), "8", "40", "40.png"), tiles.PathOf(8, 40, 40)));
        var changed = BeforeTiles.Of(folder, "roadmap")!;
        Assert.Equal(new[] { 8, 20, 80, 80 }, changed.FaintZ8);
        Assert.Equal(new[] { (2, false), (1, false), (1, true) }, changed.Areas.Select(a => (a.Tiles, a.Faint)));   // a place with one tile that shows shows
        Assert.Equal(WorldGrid.Left + 2 * WorldGrid.BlockSize, changed.Areas[2].X0);
    }

    [Fact]
    public void ChangedTilesAreGatheredIntoPlacesByNeighbouringBlocks()
    {
        // blocks (2, 5) and (3, 6) touch at a corner: one place of 3 tiles; block (10, 10) another of 1
        var areas = BeforeTiles.Areas([(8, 20), (9, 21), (12, 24), (40, 40)]);
        Assert.Equal(2, areas.Count);
        Assert.Equal(3, areas[0].Tiles);
        Assert.Equal((WorldGrid.Left + 2 * WorldGrid.BlockSize, WorldGrid.Left + 4 * WorldGrid.BlockSize), (areas[0].X0, areas[0].X1));
        Assert.Equal(1, areas[1].Tiles);
    }
}
