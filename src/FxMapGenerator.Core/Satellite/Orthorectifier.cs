namespace FxMapGenerator.Core.Satellite;

/// <summary>
/// Re-projects a straight-down capture onto the block with the measured heights, so roofs and slopes land where they
/// belong. The camera at (cx, cy, gz + h) with vertical field of view f has fpx = (image height / 2) / tan(f / 2);
/// a world point (X, Y, Z) is seen at u = w/2 + (X - cx) fpx / qx / (H - Z), v = h/2 - (Y - cy) fpx / qy / (H - Z).
/// q corrects the scale per axis (measured from the overlap of neighbouring captures, see <see cref="ScaleCalibration"/>).
/// Each output pixel takes Z from the height grid (bilinear) and its colour from the capture (bilinear); pixels whose
/// source lies outside the frame become transparent.
/// </summary>
public static class Orthorectifier
{
    /// <summary>Output size: the block at 1080 px, later resampled to the 4 x 4 tiles of 256 px.</summary>
    public const int OutPx = 1080;

    /// <summary>Straight RGBA capture (rows top-down) -> RGBA block image of <see cref="OutPx"/> x <see cref="OutPx"/>.</summary>
    public static byte[] Render(byte[] capture, int w, int h, CameraLine cam, HeightGrid grid, double qx, double qy)
    {
        double x0 = grid.X0, y0 = grid.Y0, step = grid.Step;
        int n = grid.N;
        double s = OutPx / grid.Size;
        double hCam = cam.GroundZ + cam.Height;
        double fpx = (h / 2.0) / Math.Tan(cam.Fov * (Math.PI / 180.0) / 2);
        double fx = fpx / qx, fy = fpx / qy;
        double halfW = w / 2.0, halfH = h / 2.0;
        var px = new double[OutPx];
        for (int i = 0; i < OutPx; i++) px[i] = (i + 0.5) / s;
        var output = new byte[OutPx * OutPx * 4];
        for (int r = 0; r < OutPx; r++)
        {
            double y = y0 - px[r];
            double gj = Math.Clamp((y0 - y) / step, 0, n - 1);
            for (int c = 0; c < OutPx; c++)
            {
                double x = x0 + px[c];
                double gi = Math.Clamp((x - x0) / step, 0, n - 1);
                double z = Bilinear(grid.Values, n, n, 1, 0, gj, gi);
                double kx = fx / (hCam - z), ky = fy / (hCam - z);
                double u = halfW + (x - cam.X) * kx;
                double v = halfH - (y - cam.Y) * ky;
                int o = (r * OutPx + c) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    // sampled in double, stored as float, clipped and cut to an integer (not rounded)
                    float val = (float)BilinearBytes(capture, h, w, ch, v - 0.5, u - 0.5);
                    output[o + ch] = (byte)Math.Clamp(val, 0f, 255f);
                }
                output[o + 3] = u >= 0 && u <= w && v >= 0 && v <= h ? (byte)255 : (byte)0;
            }
        }
        return output;
    }

    /// <summary>
    /// Order-1 interpolation with the "nearest" edge rule: the coordinate is first clamped into the array, then the two
    /// neighbours per axis are weighted (1 - t, t); products are summed corner by corner (value x row weight x column weight).
    /// </summary>
    internal static double Bilinear(double[] a, int rows, int cols, int channels, int ch, double cr, double cc)
    {
        cr = Math.Clamp(cr, 0, rows - 1);
        cc = Math.Clamp(cc, 0, cols - 1);
        int r0 = (int)Math.Floor(cr), c0 = (int)Math.Floor(cc);
        double tr = cr - r0, tc = cc - c0;
        int r1 = Math.Min(r0 + 1, rows - 1), c1 = Math.Min(c0 + 1, cols - 1);
        double wr0 = 1.0 - tr, wc0 = 1.0 - tc;
        double t = 0.0;
        t += a[(r0 * cols + c0) * channels + ch] * wr0 * wc0;
        t += a[(r0 * cols + c1) * channels + ch] * wr0 * tc;
        t += a[(r1 * cols + c0) * channels + ch] * tr * wc0;
        t += a[(r1 * cols + c1) * channels + ch] * tr * tc;
        return t;
    }

    /// <summary><see cref="Bilinear"/> on one channel of 8-bit RGBA.</summary>
    internal static double BilinearBytes(byte[] rgba, int rows, int cols, int ch, double cr, double cc)
    {
        cr = Math.Clamp(cr, 0, rows - 1);
        cc = Math.Clamp(cc, 0, cols - 1);
        int r0 = (int)Math.Floor(cr), c0 = (int)Math.Floor(cc);
        double tr = cr - r0, tc = cc - c0;
        int r1 = Math.Min(r0 + 1, rows - 1), c1 = Math.Min(c0 + 1, cols - 1);
        double wr0 = 1.0 - tr, wc0 = 1.0 - tc;
        double t = 0.0;
        t += (double)rgba[(r0 * cols + c0) * 4 + ch] * wr0 * wc0;
        t += (double)rgba[(r0 * cols + c1) * 4 + ch] * wr0 * tc;
        t += (double)rgba[(r1 * cols + c0) * 4 + ch] * tr * wc0;
        t += (double)rgba[(r1 * cols + c1) * 4 + ch] * tr * tc;
        return t;
    }
}
