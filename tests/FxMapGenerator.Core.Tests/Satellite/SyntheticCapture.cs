using System.Globalization;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Satellite;

/// <summary>
/// Writes capture files (PNG, camera line, height grid) of a made-up textured world, as the game would deliver them
/// for a straight-down camera over the block centre with a frame margin, optionally with a scale error per axis.
/// </summary>
internal static class SyntheticCapture
{
    public const double Fov = 2.0, Margin = 1.06;
    public const int W = 1920, H = 1080;

    /// <summary>Texture in world metres: a few crossing waves, grey 0..255 per channel with different phases.</summary>
    public static (byte R, byte G, byte B) Texture(double x, double y)
    {
        double v = Math.Sin(x * 0.37) + Math.Sin(y * 0.29 + 1) + Math.Sin((x + y) * 0.113) + Math.Sin((x - 2 * y) * 0.071);
        byte c(double k) => (byte)Math.Clamp(128 + 30 * v + 20 * Math.Sin(k * x + 0.5 * y), 0, 255);
        return (c(0.05), c(0.07), c(0.09));
    }

    /// <summary>
    /// Writes &lt;block&gt;.png / .cam.txt / .hmap into <paramref name="folder"/>. <paramref name="qx"/> and <paramref name="qy"/>
    /// are the true scale corrections of this "camera" (1 = exact); <paramref name="ground"/> is the flat terrain height.
    /// </summary>
    public static ScaleCalibration.Capture Write(string folder, BlockId b, double qx = 1, double qy = 1, double ground = 0)
    {
        var (cx, cy) = b.Center;
        var png = Path.Combine(folder, b.Name + ".png");
        Images.SavePng(png, Render(b, qx, qy), W, H);
        var inv = CultureInfo.InvariantCulture;
        var cam = string.Format(inv, "[fxmapgen] READY seq=1 x={0:F4} y={1:F4} gz={2:F3} h={3:F3} fov={4:F3} margin={5:F3}", cx, cy, ground, HeightAbove, Fov, Margin);
        File.WriteAllText(Path.Combine(folder, b.Name + ".cam.txt"), cam + "\n");
        var (x0, y0, _, _) = b.Rect;
        using (var hm = new StreamWriter(Path.Combine(folder, b.Name + ".hmap")))
        {
            hm.WriteLine(string.Format(inv, "HMAP BEGIN z=8 tx={0} ty={1} x0={2:F4} y0={3:F4} size={4:F4} step=1.000 n=282", b.Tx, b.Ty, x0, y0, WorldGrid.BlockSize));
            var row = string.Join(' ', Enumerable.Repeat(ground.ToString("F1", inv), 282));
            for (int j = 0; j < 282; j++) hm.WriteLine($"HMAP j={j} k=0 {row}");
            hm.WriteLine("HMAP END nohit=0 ms=1000");
        }
        return new ScaleCalibration.Capture(png, CameraLine.Parse(cam), Path.Combine(folder, b.Name + ".hmap"));
    }

    /// <summary>Height of the camera above the ground: the block and its margin fill the frame's height.</summary>
    public static readonly double HeightAbove = Margin * (WorldGrid.BlockSize / 2) / Math.Tan(Fov * Math.PI / 180 / 2);

    /// <summary>The frame (RGBA) of the made-up world seen straight down over the block centre.</summary>
    public static byte[] Render(BlockId b, double qx = 1, double qy = 1)
    {
        var (cx, cy) = b.Center;
        double fpx = (H / 2.0) / Math.Tan(Fov * Math.PI / 180 / 2);
        var rgba = new byte[W * H * 4];
        for (int v = 0; v < H; v++)
            for (int u = 0; u < W; u++)
            {
                // inverse of the camera model: pixel centre -> world point on the flat ground
                double x = cx + (u + 0.5 - W / 2.0) * qx * HeightAbove / fpx;
                double y = cy - (v + 0.5 - H / 2.0) * qy * HeightAbove / fpx;
                var (r, g, bl) = Texture(x, y);
                int o = (v * W + u) * 4;
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = bl; rgba[o + 3] = 255;
            }
        return rgba;
    }
}
