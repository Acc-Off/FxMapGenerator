using FxMapGenerator.Core.Import;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Import;

public sealed class ImporterTests
{
    static readonly BlockId A = BlockId.Parse("z8_48_8"), B = BlockId.Parse("z8_52_8");

    [Fact]
    public void CaptureFolderBringsShotsAndCompleteHeightGrids()
    {
        using var src = new TempFolder();
        using var work = new TempFolder();
        File.WriteAllBytes(src.File("z8_48_8.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(src.File("z8_48_8.cam.txt"), "[fxmapgen] READY seq=1 x=-624.3750 y=7696.8750 gz=0.000 h=8539.785 fov=2.000 margin=1.060\n");
        File.WriteAllText(src.File("z8_48_8.hmap"), "HMAP BEGIN n=2\nHMAP j=0 k=0 1.0 2.0\nHMAP END nohit=0 ms=900\n");
        File.WriteAllBytes(src.File("z8_52_8.png"), new byte[] { 4 });
        File.WriteAllText(src.File("z8_52_8.cam.txt"), "[fxmapgen] READY seq=2\n");
        File.WriteAllText(src.File("z8_52_8.hmap"), "HMAP BEGIN n=2\nHMAP j=0 k=0 1.0\n");      // cut off
        File.WriteAllBytes(src.File("z8_56_8.png"), new byte[] { 5 });                            // no camera line
        File.WriteAllBytes(src.File("overview.png"), new byte[] { 6 });
        var when = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src.File("z8_48_8.png"), when);

        var folder = new WorkFolder(work.Path);
        var state = StateStore.Open(folder);
        var r = Importer.ImportCapture(folder, state, src.Path);
        Assert.Equal(2, r.Blocks);
        Assert.Equal(5, r.Files);
        Assert.Equal((2, 1), (r.Items[BlockItem.Shot], r.Items[BlockItem.Height]));
        Assert.Equal(new[] { "overview.png", "z8_52_8.hmap (incomplete)", "z8_56_8 (no camera line)" }, r.Skipped);
        Assert.True(state.Has(A, BlockItem.Height) && !state.Has(B, BlockItem.Height));
        Assert.Equal(when, state.ItemTime(A, BlockItem.Shot));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(folder.CapturePng(A)));

        // the state is on disk, and a second import copies nothing
        Assert.True(StateStore.Open(folder).Has(A, BlockItem.Shot));
        var again = Importer.ImportCapture(folder, state, src.Path);
        Assert.Equal((0, 5), (again.Files, again.Unchanged));
    }

    [Fact]
    public void ScanFolderBringsTheFinishedSections()
    {
        using var src = new TempFolder();
        using var work = new TempFolder();
        File.WriteAllText(src.File("z8_48_8.txt"), """
            [mapscan] MSCAN BEGIN v=1 kind=mat z=8 tx=48 ty=8 step=1.000 n=282 flags=1 pflags=128 pflags2=256
            [mapscan] MSCAN END kind=mat n=282
            [mapscan] MSCAN BEGIN v=1 kind=road z=8 tx=48 ty=8 step=4.000
            [mapscan] MSCAN END kind=road n=71
            """);
        File.WriteAllText(src.File("z8_52_8.txt"), """
            [mapscan] MSCAN BEGIN v=1 kind=mat z=8 tx=52 ty=8 step=1.000 n=282 flags=1 pflags=128
            [mapscan] MSCAN END kind=mat n=282
            [mapscan] MSCAN BEGIN v=1 kind=road z=8 tx=52 ty=8 step=4.000
            """);
        File.WriteAllText(src.File("z8_56_8.txt"), "[mapscan] MSCAN BEGIN v=1 kind=mat\n");
        File.WriteAllText(src.File("timing.csv"), "name,ms\n");

        var folder = new WorkFolder(work.Path);
        var state = StateStore.Open(folder);
        var r = Importer.ImportScan(folder, state, src.Path);
        Assert.Equal(2, r.Blocks);
        Assert.Equal((2, 1, 1), (r.Items[BlockItem.ScanGround], r.Items[BlockItem.ScanRoads], r.Items[BlockItem.ScanCanopy]));
        Assert.True(state.Has(A, BlockItem.ScanCanopy) && state.Has(A, BlockItem.ScanRoads));
        Assert.True(state.Has(B, BlockItem.ScanGround) && !state.Has(B, BlockItem.ScanRoads) && !state.Has(B, BlockItem.ScanCanopy));
        Assert.Equal(new[] { "z8_56_8 (no finished scan section)" }, r.Skipped);   // non-block files are not listed: only *.txt are read
        Assert.True(File.Exists(folder.ScanFile(A)));
    }
}
