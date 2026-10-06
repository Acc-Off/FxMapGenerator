using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Satellite;

public sealed class SatelliteTests
{
    [Fact]
    public void LanczosKeepsFlatImagesFlatAndSameSizeUntouched()
    {
        var flat = new byte[40 * 30 * 4];
        for (int i = 0; i < flat.Length; i += 4) { flat[i] = 10; flat[i + 1] = 200; flat[i + 2] = 77; flat[i + 3] = 255; }
        var small = Lanczos.Resize(flat, 40, 30, 17, 11);
        Assert.Equal(17 * 11 * 4, small.Length);
        for (int i = 0; i < small.Length; i += 4) Assert.Equal(new byte[] { 10, 200, 77, 255 }, small[i..(i + 4)]);
        var big = Lanczos.Resize(flat, 40, 30, 80, 61);
        for (int i = 0; i < big.Length; i += 4) Assert.Equal(new byte[] { 10, 200, 77, 255 }, big[i..(i + 4)]);

        var odd = new byte[] { 1, 2, 3, 0, 4, 5, 6, 128 };
        Assert.Equal(odd, Lanczos.Resize(odd, 2, 1, 2, 1));
    }

    [Fact]
    public void LanczosDoesNotBleedTheColourOfTransparentPixels()
    {
        // left half opaque red, right half fully transparent green: after shrinking, no pixel may turn greenish
        var img = new byte[64 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 64; x++)
            {
                int o = (y * 64 + x) * 4;
                if (x < 32) { img[o] = 255; img[o + 3] = 255; }
                else { img[o + 1] = 255; img[o + 3] = 0; }
            }
        var r = Lanczos.Resize(img, 64, 8, 20, 4);
        for (int i = 0; i < r.Length; i += 4)
            if (r[i + 3] > 0) Assert.True(r[i + 1] <= 2, $"green {r[i + 1]} at {i / 4}");
    }

    [Fact]
    public void OrthorectifiedBlockShowsTheWorldAtItsPlace()
    {
        using var tmp = new TempFolder();
        var b = BlockId.Parse("z8_60_132");
        var cap = SyntheticCapture.Write(tmp.Path, b, ground: 25);
        var grid = HeightGrid.Read(cap.HeightGrid);
        var rgba = Images.LoadRgba(cap.Png, out var w, out var h);
        var square = Orthorectifier.Render(rgba, w, h, cap.Camera, grid, 1, 1);
        Assert.Equal(Orthorectifier.OutPx * Orthorectifier.OutPx * 4, square.Length);
        var (x0, y0, _, _) = b.Rect;
        double s = Orthorectifier.OutPx / WorldGrid.BlockSize;
        int worst = 0;
        for (int r = 0; r < Orthorectifier.OutPx; r += 37)
            for (int c = 0; c < Orthorectifier.OutPx; c += 41)
            {
                var (er, eg, eb) = SyntheticCapture.Texture(x0 + (c + 0.5) / s, y0 - (r + 0.5) / s);
                int o = (r * Orthorectifier.OutPx + c) * 4;
                Assert.Equal(255, square[o + 3]);        // the margin keeps the whole block inside the frame
                worst = Math.Max(worst, Math.Max(Math.Abs(square[o] - er), Math.Max(Math.Abs(square[o + 1] - eg), Math.Abs(square[o + 2] - eb))));
            }
        Assert.InRange(worst, 0, 6);   // sampling the capture between its pixels
    }

    [Fact]
    public void HeightGridFillsMissingCellsFromTheNearestHit()
    {
        using var tmp = new TempFolder();
        var path = tmp.File("z8_0_0.hmap");
        File.WriteAllText(path, "HMAP BEGIN z=8 tx=0 ty=0 x0=-4140.0 y0=8400.0 size=3.0 step=1.000 n=3\n" +
            "HMAP j=0 k=0 1.0 x 3.0\nHMAP j=1 k=0 x x x\nHMAP j=2 k=0 7.0 x 9.0\nHMAP END nohit=5 ms=1\n");
        var g = HeightGrid.Read(path);
        Assert.Equal(5, g.Missing);
        g.FillMissing(0);
        Assert.Equal(0, g.Missing);
        // of equally near hits the lower row, then the lower column: (1,0) and (1,1) take (0,0), (0,1) too, (2,1) takes (2,0)
        Assert.Equal(new[] { 1.0, 1.0, 3.0, 1.0, 1.0, 3.0, 7.0, 7.0, 9.0 }, g.Values);
    }

    [Fact]
    public void HeightLinesTheGameCutAreAProblemOfTheGrid()
    {
        static List<string> Grid() =>
        [
            "HMAP BEGIN seq=1 z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=281.250 n=2 block=z8_60_132",
            "HMAP j=0 k=0 10.0 x", "HMAP j=1 k=0 -3.5 4.0", "HMAP END nohit=1 ms=5",
        ];
        Assert.Null(HeightGrid.Problem(Grid()));
        var inValue = Grid();
        inValue[2] = "HMAP j=1 k=0 -3.5 -";
        Assert.Equal("row 1: '-' is no height", HeightGrid.Problem(inValue));
        var shortRow = Grid();
        shortRow[2] = "HMAP j=1 k=0 -3.5";
        Assert.Equal("1 rows without all their values", HeightGrid.Problem(shortRow));
        var gone = Grid();
        gone.RemoveAt(1);
        Assert.Equal("1 rows missing", HeightGrid.Problem(gone));
    }

    [Fact]
    public void CalibrationRecoversAKnownScaleError()
    {
        using var tmp = new TempFolder();
        var blocks = new[] { "z8_60_132", "z8_64_132", "z8_60_136", "z8_64_136" }.Select(BlockId.Parse).ToList();
        // the "camera" sees 1.2 % more metres north-south and 0.3 % more east-west than the model predicts
        const double trueQx = 1 / 1.003, trueQy = 1 / 1.012;
        var caps = blocks.ToDictionary(b => b, b => SyntheticCapture.Write(tmp.Path, b, trueQx, trueQy));
        var (first, final) = ScaleCalibration.Calibrate(caps, 4);
        Assert.Equal((2, 2), (final.X.Used, final.Y.Used));
        Assert.Equal(trueQx, final.Qx, 3);
        Assert.Equal(trueQy, final.Qy, 3);
        Assert.True(Math.Abs(first.Y.MedianMismatch) > 2);            // about 3.4 m before the correction
        Assert.True(Math.Abs(final.Y.MedianMismatch) < 0.1);
    }

    [Fact]
    public void BlockTilesAndParentsAreWrittenOpaqueAsRgb()
    {
        using var tmp = new TempFolder();
        var store = new TileStore(tmp.Path);
        var square = new byte[1080 * 1080 * 4];
        for (int i = 0; i < square.Length; i += 4) { square[i] = (byte)(i / 4 % 1080 / 5); square[i + 1] = 90; square[i + 2] = 30; square[i + 3] = 255; }
        store.WriteBlock(8, 4, 8, square, 1080);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++) Assert.True(store.Exists(8, 4 + i, 8 + j));
        Assert.Equal(2, File.ReadAllBytes(store.PathOf(8, 4, 8))[25]);   // PNG colour type 2 = RGB
        Assert.True(store.BuildParent(7, 2, 4));
        Assert.False(store.BuildParent(7, 0, 0));                        // no children there
        var parent = store.Read(7, 2, 4)!;
        Assert.Equal(90, parent[1]);
        Assert.Equal(255, parent[3]);
    }

    [Fact]
    public void TheLowZoomRecordIsKeptInANewWorkFolderWithoutItsDataFolder()
    {
        // the satellite map's lower zooms come right after the visit, before any other step made the data folder
        using var tmp = new TempFolder();
        var folder = new WorkFolder(tmp.File("work"));
        Assert.False(Directory.Exists(folder.Data));
        LowZooms.Record(folder, "satellite", MapFrame.Standard);                // nothing to remove: no error
        Assert.Equal(MapFrame.Standard, LowZooms.Recorded(folder, "satellite"));
        var wider = new MapFrame(0, 1, 0, 1);
        LowZooms.Record(folder, "satellite", wider);                            // the data folder is made
        Assert.Equal(wider, LowZooms.Recorded(folder, "satellite"));
        LowZooms.Record(folder, "satellite", MapFrame.Standard);
        Assert.False(File.Exists(LowZooms.RecordPath(folder, "satellite")));
    }
}
