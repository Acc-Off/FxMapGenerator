using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Vectors;

namespace FxMapGenerator.Core.Tests.Grids;

/// <summary>
/// The drawing parts on small fixed grids. The expected values are those of the libraries the map stages were first
/// written with (SciPy, OpenCV, numpy, Pillow, scikit-image, contourpy, potrace), to the last bit.
/// </summary>
public sealed class DrawingPartsTests
{
    static string[] Rows(Grid<bool> g) =>
        Enumerable.Range(0, g.Height).Select(r => new string(Enumerable.Range(0, g.Width).Select(c => g[r, c] ? '1' : '0').ToArray())).ToArray();

    static Grid<bool> G(params string[] rows) =>
        new(rows[0].Length, rows.Length, rows.SelectMany(r => r.Select(ch => ch == '1')).ToArray());

    [Fact]
    public void ScipyBlurKeepsFloat32Passes()
    {
        var src = new Grid<float>(4, 3, [0, 1, 2, 3, 4, 5, 6, 7.5f, 8, 9, 10, 11]);
        float[] expected = [2.1158978939056396f, 2.7600669860839844f, 3.668989419937134f, 4.356274127960205f, 4.427865505218506f, 5.075035095214844f,
            5.997225284576416f, 6.70377779006958f, 6.739307880401611f, 7.383476734161377f, 8.292399406433105f, 8.979683876037598f];
        Assert.Equal(expected, Gaussian.Scipy(src, 1.0).Data);
    }

    [Fact]
    public void OpenCvBlurWithItsKernel()
    {
        Assert.Equal([8.922106863539231e-05, 0.0010281965781389086, 0.007597402196596928, 0.03599434807370883], Gaussian.CvKernel(13, 1.5)[..4]);
        var src = new Grid<double>(5, 4, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 1, 0, 1, 0, 1, 9, 7, 5, 3, 1]);
        double[] expected = [2.720463562191733, 3.040501912288204, 3.5575578305048574, 4.082801092076956, 4.416713368164281, 3.2379969332892116,
            3.3831850226027917, 3.6254405660469833, 3.880451356002155, 4.047253946919698, 4.022939779994873, 3.881822664383592, 3.6713080450762035,
            3.477731678883959, 3.365317411062442, 4.65261004968286, 4.2811342273798285, 3.7043852547457887, 3.1456596996913464, 2.804725599012255];
        Assert.Equal(expected, Gaussian.OpenCv(src, 1.5).Data);
    }

    [Fact]
    public void MedianGradientAndLinearSamples()
    {
        Assert.Equal([3f, 3, 3, 4, 5, 5, 4, 5, 5], Filters.Median3(new Grid<float>(3, 3, [1, 9, 2, 8, 3, 7, 4, 6, 5])).Data);
        var (gr, gc) = Filters.Gradient(new Grid<double>(3, 2, [1, 2, 4, 3, 7, 8]));
        Assert.Equal([2.0, 5, 4, 2, 5, 4], gr.Data);
        Assert.Equal([1.0, 1.5, 2, 4, 2.5, 1], gc.Data);
        var f = new Grid<float>(3, 2, [0, 10, 20, 30, 40, 50]);
        Assert.Equal(20f, Sampling.Linear(f, 0.5, 0.5));
        Assert.Equal(20f, Sampling.Linear(f, -1.0, 2.7));       // past the edges: the edge cell
        Assert.Equal(31f, Sampling.Linear(f, 1.25, 0.1));
        Assert.Equal(19.999000549316406f, Sampling.Linear(f, 0.0, 1.9999));
    }

    [Fact]
    public void PolygonsAndLinesTakePillowsCells()
    {
        var img = new Grid<bool>(9, 8);
        PilDraw.Polygon(img, [(4.7, 0.2), (8.1, 3.9), (4.2, 7.5), (0.9, 3.3)]);
        Assert.Equal(["000010000", "000111000", "011111110", "111111111", "011111110", "001111100", "000111000", "000010000"], Rows(img));
        img = new Grid<bool>(9, 8);
        PilDraw.Line(img, [(0.5, 1.5), (7.9, 6.2), (8.0, 0.3)], 2);
        Assert.Equal(["000000011", "010000011", "111000011", "011110010", "000111110", "000011110", "000000111", "000000010"], Rows(img));
    }

    [Fact]
    public void ThinningClosingAndCentroids()
    {
        var m = G("000000000", "011111110", "011111110", "011111111", "011111110", "011111110", "000000000");
        Assert.Equal(["000000000", "000000000", "000001100", "000110000", "000000000", "000000000", "000000000"], Rows(Skeleton.Zhang(m)));
        Assert.Equal(["00100", "01110", "11111", "01110", "00100"], Rows(new Grid<bool>(5, 5, Kernel.Disk(2).Mask)));
        // two strokes a cell apart close into one; with the edge off, the corners of the grid erode
        var two = G("0000000", "0110110", "0000000");
        Assert.Equal(["0000000", "0111110", "0000000"], Rows(Morphology.CloseEdgeOff(two, Kernel.Rect(3, 3))));
        var l = Components.Label(G("1100", "1000", "0011"), 8);
        var c = Components.Centroids(l);
        Assert.Equal(2, l.Count);
        Assert.Equal((1.0 / 3, 1.0 / 3), c[1]);
        Assert.Equal((2.5, 2.0), c[2]);
    }

    [Fact]
    public void ContoursRunAsContourpyGivesThem()
    {
        var z = new double[] { 0, 0, 0, 0, 0, 5, 5, 0, 0, 5, 1, 0, 0, 0, 0, 3 };
        var lines = Contours.Lines(z, 4, 4, 2.0);
        Assert.Equal(2, lines.Count);
        Assert.Equal([0.4, 1.0, 1.0, 0.4, 2.0, 0.4, 2.5999999999999996, 1.0, 2.0, 1.75, 1.75, 2.0, 1.0, 2.5999999999999996, 0.4, 2.0, 0.4, 1.0], lines[0]);   // closed: first point repeated
        Assert.Equal([2.666666666666667, 3.0, 3.0, 2.666666666666667], lines[1]);                                                                           // open: from edge to edge
    }

    [Fact]
    public void OutlinesAreTheAdjustedPolygons()
    {
        var holed = new Grid<bool>(10, 8);
        for (int r = 1; r < 7; r++)
            for (int c = 1; c < 9; c++)
                holed[r, c] = !(r is 3 or 4 && c is 4 or 5);
        var rings = Rings.Trace(holed, 100, 50);
        Assert.Equal(2, rings.Count);
        Assert.Equal(48.0, rings[0].Area);
        Assert.Equal([101, 43, 101, 49, 109, 49, 109, 43], rings[0].Points);
        Assert.Equal(4.0, rings[1].Area);
        Assert.Equal([104, 45, 104, 47, 106, 47, 106, 45], rings[1].Points);     // the same square as potrace's, which starts it at (106, 45) the other way round
        var ell = G("0000000", "0110000", "0110000", "0111110", "0111110", "0000000");
        var one = Assert.Single(Rings.Trace(ell, 100, 50));
        Assert.Equal(14.0, one.Area);
        Assert.Equal([101, 45, 101, 49, 103, 49, 103, 47, 106, 47, 106, 45], one.Points);
    }
}
