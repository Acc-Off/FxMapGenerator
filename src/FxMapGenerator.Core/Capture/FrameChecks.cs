namespace FxMapGenerator.Core.Capture;

/// <summary>
/// A rectangle of a frame in pixels, with the number of pixels in it that stood out; <paramref name="InMap"/> when it
/// touches the part of the frame the map uses (<see cref="FrameChecks.MapArea"/>).
/// </summary>
public readonly record struct Box(int X, int Y, int Width, int Height, int Pixels, bool InMap = true);

/// <summary>A rectangle of a frame in pixels.</summary>
public readonly record struct Area(int X, int Y, int Width, int Height)
{
    public bool Touches(Box b) => b.X < X + Width && X < b.X + b.Width && b.Y < Y + Height && Y < b.Y + b.Height;
}

/// <summary>
/// Checks on a frame taken from the game window: the resource's beacon (is this the frame of the request, drawn once
/// the scene had settled?), a notification band, and what stands out from a frame of open sea (the test shot).
/// </summary>
public static class FrameChecks
{
    /// <summary>
    /// The beacon the resource draws along the top-left edge: 4 x 4 px squares, [ready: green, else red][bit 0][bit 1][bit 2]
    /// of the request number (white 1, black 0). Null when the corner shows no beacon (a black frame, or no capture
    /// environment).
    /// </summary>
    public static (bool Ready, int SeqBits)? Beacon(Frame f)
    {
        if (f.Width < 16 || f.Height < 4) return null;
        var (r, g, b) = f.Pixel(2, 2);
        bool? ready = g > 200 && r < 60 && b < 60 ? true : r > 200 && g < 60 && b < 60 ? false : null;
        if (ready is null) return null;
        int bits = 0;
        for (int i = 0; i < 3; i++)
        {
            var (br, bg, bb) = f.Pixel(4 * (i + 1) + 2, 2);
            if (br > 200 && bg > 200 && bb > 200) bits |= 1 << i;
            else if (!(br < 60 && bg < 60 && bb < 60)) return null;
        }
        return (ready.Value, bits);
    }

    /// <summary>True when the beacon says the frame is ready for request <paramref name="seq"/>.</summary>
    public static bool ReadyFor(Frame f, int seq) => Beacon(f) is { Ready: true } b && b.SeqBits == (seq & 7);

    /// <summary>
    /// A notification (txAdmin and the like) in the bottom middle: one flat green colour covering at least 45 % of the
    /// band 830..1100 x 1020..1056 (of 1920 x 1080). Water and grass are textured, so they do not fill one colour bucket.
    /// </summary>
    public static bool NotificationBand(Frame f)
    {
        double sx = f.Width / 1920.0, sy = f.Height / 1080.0;
        var buckets = new Dictionary<int, int>();
        int n = 0;
        for (int y = (int)(1020 * sy); y < (int)(1056 * sy); y += 3)
            for (int x = (int)(830 * sx); x < (int)(1100 * sx); x += 3)
            {
                var (r, g, b) = f.Pixel(x, y);
                int key = ((r >> 3) << 16) | ((g >> 3) << 8) | (b >> 3);
                buckets[key] = buckets.GetValueOrDefault(key) + 1;
                n++;
            }
        foreach (var (key, count) in buckets)
        {
            if (count < 0.45 * n) continue;
            int r = (key >> 16) << 3, g = ((key >> 8) & 0xFF) << 3, b = (key & 0xFF) << 3;
            if (g > r + 60 && g > b + 30 && g > 150) return true;
        }
        return false;
    }

    /// <summary>
    /// How far past the block's square the orthorectification can read, in pixels: points lean out of the square by their
    /// height over the block's centre (64 px is about 18 m at the frame's 3.6 px per metre, a height difference of some
    /// 700 m at a block corner).
    /// </summary>
    public const int Lean = 64;

    /// <summary>
    /// The part of a frame the map uses: the block's square in the middle (the frame height over the margin, centred) and
    /// <see cref="Lean"/> around it. The rest of the frame (its sides) never reaches the map.
    /// </summary>
    public static Area MapArea(int width, int height, double margin)
    {
        int side = (int)Math.Round(height / margin);
        int x0 = Math.Max(0, (width - side) / 2 - Lean), y0 = Math.Max(0, (height - side) / 2 - Lean);
        int x1 = Math.Min(width, (width + side) / 2 + Lean), y1 = Math.Min(height, (height + side) / 2 + Lean);
        return new Area(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// What stands out on a frame of open sea: pixels more than <paramref name="threshold"/> off the frame's median colour
    /// in any channel (the sea itself stays within about 15), gathered in 16 px cells (3 pixels make a cell count) and
    /// joined into boxes. The beacon is left out. Empty for a clean test shot. With <paramref name="mapArea"/>, a box off
    /// that area is marked as not in the map (<see cref="Box.InMap"/> false).
    /// </summary>
    public static IReadOnlyList<Box> OverSea(Frame f, Area? mapArea = null, int threshold = 40)
    {
        const int Cell = 16, MinPixels = 3;
        var median = MedianColour(f);
        int cw = (f.Width + Cell - 1) / Cell, ch = (f.Height + Cell - 1) / Cell;
        var count = new int[cw * ch];
        for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
            {
                if (y < 4 && x < 16) continue; // the beacon
                var (r, g, b) = f.Pixel(x, y);
                int d = Math.Max(Math.Abs(r - median.R), Math.Max(Math.Abs(g - median.G), Math.Abs(b - median.B)));
                if (d > threshold) count[(y / Cell) * cw + x / Cell]++;
            }
        var seen = new bool[cw * ch];
        var boxes = new List<Box>();
        var stack = new Stack<int>();
        for (int start = 0; start < count.Length; start++)
        {
            if (seen[start] || count[start] < MinPixels) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, pixels = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int c = stack.Pop(), cx = c % cw, cy = c / cw;
                x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy); x1 = Math.Max(x1, cx); y1 = Math.Max(y1, cy);
                pixels += count[c];
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                        int k = ny * cw + nx;
                        if (seen[k] || count[k] < MinPixels) continue;
                        seen[k] = true;
                        stack.Push(k);
                    }
            }
            int px = x0 * Cell, py = y0 * Cell;
            var box = new Box(px, py, Math.Min(f.Width, (x1 + 1) * Cell) - px, Math.Min(f.Height, (y1 + 1) * Cell) - py, pixels);
            boxes.Add(mapArea is { } area ? box with { InMap = area.Touches(box) } : box);
        }
        return boxes.OrderByDescending(b => b.Pixels).ToList();
    }

    /// <summary>
    /// A copy of the frame for showing the test shot: an outline (3 px) around each box, red for a box in the map and
    /// amber for one off it, and with <paramref name="mapArea"/> a thin white line around the part the map uses.
    /// </summary>
    public static Frame Marked(Frame f, IReadOnlyList<Box> boxes, Area? mapArea = null)
    {
        var rgba = (byte[])f.Rgba.Clone();
        void Put(int x, int y, (byte R, byte G, byte B) c)
        {
            if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) return;
            int o = (y * f.Width + x) * 4;
            rgba[o] = c.R; rgba[o + 1] = c.G; rgba[o + 2] = c.B; rgba[o + 3] = 255;
        }
        void Outline(int l, int top, int r, int bottom, (byte, byte, byte) c)
        {
            for (int x = l; x <= r; x++) { Put(x, top, c); Put(x, bottom, c); }
            for (int y = top; y <= bottom; y++) { Put(l, y, c); Put(r, y, c); }
        }
        if (mapArea is { } a) Outline(a.X, a.Y, a.X + a.Width - 1, a.Y + a.Height - 1, (255, 255, 255));
        foreach (var b in boxes)
            for (int t = 0; t < 3; t++)
                Outline(b.X - 2 - t, b.Y - 2 - t, b.X + b.Width + 1 + t, b.Y + b.Height + 1 + t, b.InMap ? ((byte)255, (byte)0, (byte)0) : ((byte)255, (byte)176, (byte)0));
        return new Frame(f.Width, f.Height, rgba);
    }

    /// <summary>Share of the pixels (0..1) where a channel of <paramref name="a"/> and <paramref name="b"/> (same size) differs by more than <paramref name="levels"/>.</summary>
    public static double Changed(Frame a, Frame b, int levels) => Changed(a, b, [levels])[0];

    /// <summary>The same for several levels at once.</summary>
    public static double[] Changed(Frame a, Frame b, IReadOnlyList<int> levels)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("the frames differ in size");
        byte[] p = a.Rgba, q = b.Rgba;
        var byDiff = new long[256];   // pixels by their largest channel difference
        for (int o = 0; o < p.Length; o += 4)
            byDiff[Math.Max(Math.Abs(p[o] - q[o]), Math.Max(Math.Abs(p[o + 1] - q[o + 1]), Math.Abs(p[o + 2] - q[o + 2])))]++;
        double n = p.Length / 4.0;
        return levels.Select(l => n == 0 ? 0 : byDiff.Skip(l + 1).Sum() / n).ToArray();
    }

    /// <summary>Median of each channel over every 4th pixel of every 4th row.</summary>
    static (int R, int G, int B) MedianColour(Frame f)
    {
        var hr = new int[256]; var hg = new int[256]; var hb = new int[256];
        int n = 0;
        for (int y = 0; y < f.Height; y += 4)
            for (int x = 0; x < f.Width; x += 4)
            {
                var (r, g, b) = f.Pixel(x, y);
                hr[r]++; hg[g]++; hb[b]++;
                n++;
            }
        static int Mid(int[] h, int n)
        {
            int half = (n + 1) / 2, acc = 0;
            for (int v = 0; v < 256; v++)
                if ((acc += h[v]) >= half) return v;
            return 255;
        }
        return (Mid(hr, n), Mid(hg, n), Mid(hb, n));
    }
}
