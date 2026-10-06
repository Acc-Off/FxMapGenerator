using FxMapGenerator.Core.Render;

namespace FxMapGenerator.Core.Export;

/// <summary>
/// A block's layers (<see cref="CellPainter.DrawLayers(int, int, int, int, out byte[])"/>, zoom 8) made as small as the
/// block is at a lower zoom, so that put over each other they are the block's tile of that zoom. The map's tiles are
/// the whole drawing made smaller; layers made smaller one by one do not add up to that (making a picture smaller
/// strengthens the contrast along its edges a little, and what is see-through does not mix the same way). So the small
/// layers are found from pictures shrunk the tiles' way: at zoom 8 the layers are put over each other one by one (the
/// ground, the ground as the drawing shades it, then each further layer over what is there), every such picture is
/// shrunk as the tiles are (<see cref="EditableLayersStage.Shrink"/>), and each small layer is what turns the small
/// picture before it into the one after it:
/// <list type="bullet">
/// <item>the ground is the first small picture;</item>
/// <item>the two shading layers hold the factor between the small ground and the small shaded ground: under 1 in the
///   dark layer (to be multiplied), over 1 in the light layer as the value v with 1 / (1 - v) = factor (to be
///   colour-dodged). A pixel is grey where one factor gives its three colours to a step of 255, and holds a factor per
///   colour elsewhere;</item>
/// <item>any other layer starts from the opacity of the layer shrunk alone and gets the colours that give the next
///   picture, so inside its shapes it keeps its paint and along their edges it takes the contrast the shrinking adds.
///   Its opacity moves only where the picture needs it (<see cref="Slack"/>): over a see-through picture to what gives
///   the next picture's opacity (the water's edge over the ground it cuts), and up where colours between 0 and 1 could
///   not give the next picture's, for a layer pulls a pixel towards a colour only as far as it is opaque. Where the
///   layer shrunk alone has nothing, it gets a pixel only if the two small pictures differ there and what the layers
///   give is not the next picture yet: the lighter or darker rim the shrinking puts beside a shape, as a little white
///   or black.</item>
/// </list>
/// Each layer is found over what the layers before it give as they are stored (8 bits), so their rounding does not add
/// up. What stays apart from the tile: the slack, the pixels beside shapes that would take more opacity than
/// <see cref="Slack.BesideOpacity"/>, what the layers at zoom 8 are apart from the drawing, and a picture that the
/// shrinking makes more see-through beside an opaque shape (a bridge over see-through water), which no layer put over
/// it can give.
/// </summary>
public static class LayeredBlock
{
    /// <summary>
    /// How far, in steps of 255, what the layers give may stay from the small picture before a layer's opacity is moved
    /// for it, and how far it is moved. Making a layer more opaque than it is alone is the only way to pull a pixel
    /// further than the layer's colours reach, and it costs the more the nearer the pixel already is to white or black
    /// (a pixel 10 steps under white is lifted 5 steps by a layer of white half opaque): a few steps of slack and a
    /// limit keep the layers close to what they are alone.
    /// </summary>
    /// <param name="Inside">The slack where the layer shrunk alone has something.</param>
    /// <param name="Beside">The slack where it has nothing or next to nothing (<see cref="Faint"/>): the pixels beside its shapes.</param>
    /// <param name="BesideOpacity">The most opacity (0 to 1) such a pixel gets over what the layer has there alone.</param>
    public readonly record struct Slack(double Inside, double Beside, double BesideOpacity = 1)
    {
        /// <summary>
        /// Three steps, which is what the layers at zoom 8 are apart from the drawing, and a quarter: on a map of light
        /// ground with dark labels, 0.05 % of the pixels of its busiest blocks then stay more than 8 steps from the tile
        /// (7.6 % with every layer shrunk alone, 1.5 % with no pixels beside the shapes).
        /// </summary>
        public static readonly Slack Default = new(3, 3, 0.25);
    }

    /// <summary>The opacity (of 255) up to which a layer shrunk alone counts as having next to nothing in a pixel: the far end of what the shrinking spreads.</summary>
    const int Faint = 8;

    /// <summary>
    /// The small layers of a block (straight RGBA, by <see cref="MapLayer"/>'s number; null for a layer with nothing).
    /// </summary>
    /// <param name="layers">The block's layers at zoom 8, 1024 pixels a side.</param>
    /// <param name="shadedGround">The ground as the drawing shades it, or null without shading.</param>
    /// <param name="zoom">7 (512 pixels a side) or 6 (256).</param>
    /// <param name="slack">How far the layers put together may stay from the small pictures; null for <see cref="Slack.Default"/>.</param>
    /// <param name="last">The last layer wanted (the layers after it stay null); null for every layer.</param>
    public static byte[]?[] Shrink(IReadOnlyList<byte[]?> layers, byte[]? shadedGround, int zoom, Slack? slack = null, MapLayer? last = null)
    {
        var (inside, beside, most) = slack ?? Slack.Default;
        int side = EditableChoice.BlockPx(zoom), n = side * side * 4;
        var o = new byte[]?[layers.Count];
        if (layers[(int)MapLayer.Ground] is not { } ground) return o;
        byte[] Small(byte[] rgba) => EditableLayersStage.Shrink(rgba, zoom) ?? new byte[n];

        var before = Small(ground);
        bool any = false;
        for (int i = 3; i < n && !any; i += 4) any = before[i] != 0;
        if (any) o[(int)MapLayer.Ground] = (byte[])before.Clone();
        // what the small layers give so far (premultiplied, 0..1), and the layers put together at zoom 8
        var now = Premultiplied(before);
        var whole = Premultiplied(shadedGround ?? ground);
        if (shadedGround is not null)
        {
            var after = Small(shadedGround);
            (o[(int)MapLayer.ShadeDark], o[(int)MapLayer.ShadeLight]) = Shading(now, before, after);
            before = after;
        }
        for (int k = 0; k < layers.Count && (last is not { } end || k <= (int)end); k++)
        {
            if (MapLayers.Blend((MapLayer)k) != LayerBlend.Normal || k == (int)MapLayer.Ground || layers[k] is not { } layer) continue;
            PutOver(whole, layer);
            var after = Small(Straight(whole));
            o[k] = Layer(now, before, after, EditableLayersStage.Shrink(layer, zoom), inside / 255, beside / 255, most);
            before = after;
        }
        return o;
    }

    static double[] Premultiplied(byte[] rgba)
    {
        var o = new double[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            double a = rgba[i + 3] / 255.0;
            (o[i], o[i + 1], o[i + 2], o[i + 3]) = (rgba[i] / 255.0 * a, rgba[i + 1] / 255.0 * a, rgba[i + 2] / 255.0 * a, a);
        }
        return o;
    }

    static byte[] Straight(double[] premultiplied)
    {
        var o = new byte[premultiplied.Length];
        for (int i = 0; i < o.Length; i += 4)
        {
            double a = premultiplied[i + 3];
            if (a <= 0) continue;
            for (int c = 0; c < 3; c++) o[i + c] = (byte)Math.Clamp(Math.Round(255 * premultiplied[i + c] / a), 0, 255);
            o[i + 3] = (byte)Math.Clamp(Math.Round(255 * a), 0, 255);
        }
        return o;
    }

    /// <summary>Puts a layer (straight RGBA) over a picture (premultiplied), in place.</summary>
    static void PutOver(double[] under, byte[] layer)
    {
        for (int i = 0; i < layer.Length; i += 4)
        {
            int alpha = layer[i + 3];
            if (alpha == 0) continue;
            double a = alpha / 255.0;
            for (int c = 0; c < 3; c++) under[i + c] = a * layer[i + c] / 255.0 + (1 - a) * under[i + c];
            under[i + 3] = a + (1 - a) * under[i + 3];
        }
    }

    /// <summary>
    /// The two shading layers that turn the small ground (<paramref name="before"/>, what <paramref name="now"/> holds)
    /// into the small shaded ground (<paramref name="after"/>; the same opacity). <paramref name="now"/> becomes what
    /// the ground and the two layers give.
    /// </summary>
    static (byte[]? Dark, byte[]? Light) Shading(double[] now, byte[] before, byte[] after)
    {
        byte[]? dark = null, light = null;
        Span<int> d = stackalloc int[3], l = stackalloc int[3];
        for (int i = 0; i < before.Length; i += 4)
        {
            if (before[i + 3] == 0) continue;
            if (!Grey(before, after, i, d, l))
                for (int c = 0; c < 3; c++)
                {
                    int u = before[i + c], t = after[i + c];
                    d[c] = t < u ? (int)Math.Round(255.0 * t / u) : 255;
                    // a colour of 0 stays 0 under any colour dodge
                    l[c] = t > u && u > 0 ? (int)Math.Round(255.0 * (1 - (double)u / t)) : 0;
                }
            bool darker = d[0] < 255 || d[1] < 255 || d[2] < 255, lighter = l[0] > 0 || l[1] > 0 || l[2] > 0;
            if (!darker && !lighter) continue;
            for (int c = 0; c < 3; c++)
            {
                double x = before[i + c] / 255.0 * d[c] / 255.0;
                if (l[c] > 0 && x > 0) x = l[c] >= 255 ? 1 : Math.Min(1, x / (1 - l[c] / 255.0));
                now[i + c] = now[i + 3] * x;
            }
            if (darker)
            {
                dark ??= new byte[before.Length];
                (dark[i], dark[i + 1], dark[i + 2], dark[i + 3]) = ((byte)d[0], (byte)d[1], (byte)d[2], 255);
            }
            if (lighter)
            {
                light ??= new byte[before.Length];
                (light[i], light[i + 1], light[i + 2], light[i + 3]) = ((byte)l[0], (byte)l[1], (byte)l[2], 255);
            }
        }
        return (dark, light);
    }

    /// <summary>
    /// The one grey of the dark layer (<paramref name="d"/>) or of the light layer (<paramref name="l"/>) that turns a
    /// pixel's three colours into the shaded ones to a step of 255: the factor between the sums of the colours, moved
    /// into what the three colours allow. False when no grey does it.
    /// </summary>
    static bool Grey(byte[] before, byte[] after, int i, Span<int> d, Span<int> l)
    {
        const double step = 1;
        int sumBefore = before[i] + before[i + 1] + before[i + 2], sumAfter = after[i] + after[i + 1] + after[i + 2];
        d.Fill(255);
        l.Fill(0);
        double lo = 0, hi = 255;
        if (sumAfter <= sumBefore)
        {
            // u v / 255 within a step of t
            for (int c = 0; c < 3; c++)
            {
                int u = before[i + c], t = after[i + c];
                if (u == 0)
                {
                    if (t > step) return false;
                    continue;
                }
                lo = Math.Max(lo, Math.Ceiling(255 * (t - step) / u));
                hi = Math.Min(hi, Math.Floor(255 * (t + step) / u));
            }
            if (lo > hi) return false;
            d.Fill(sumBefore == 0 ? 255 : (int)Math.Clamp(Math.Round(255.0 * sumAfter / sumBefore), lo, hi));
            return true;
        }
        // min(255, u / (1 - v / 255)) within a step of t
        for (int c = 0; c < 3; c++)
        {
            int u = before[i + c], t = after[i + c];
            if (u == 0)
            {
                if (t > step) return false;
                continue;
            }
            if (t - step > u) lo = Math.Max(lo, Math.Ceiling(255 * (1 - u / (t - step))));
            if (t + step < 255) hi = Math.Min(hi, Math.Floor(255 * (1 - u / (t + step))));
        }
        if (lo > hi) return false;
        l.Fill((int)Math.Clamp(Math.Round(255 * (1 - (double)sumBefore / sumAfter)), lo, hi));
        return true;
    }

    /// <summary>
    /// The layer that, put over what the layers before it give (<paramref name="now"/>), gives the small picture
    /// <paramref name="after"/>; <paramref name="before"/> is the small picture without it, <paramref name="alone"/>
    /// the layer shrunk alone (null: nothing left of it). <paramref name="now"/> becomes what the layers give with it.
    /// Null when the layer has nothing. <paramref name="inside"/> and <paramref name="beside"/> are the slack, of 1,
    /// <paramref name="most"/> the most opacity a pixel beside the shapes gets over the layer's own.
    /// </summary>
    static byte[]? Layer(double[] now, byte[] before, byte[] after, byte[]? alone, double inside, double beside, double most)
    {
        byte[]? o = null;
        for (int i = 0; i < after.Length; i += 4)
        {
            int own = alone is null ? 0 : alone[i + 3];
            double under = now[i + 3], want = after[i + 3] / 255.0;
            double slack = own > Faint ? inside : beside;
            // nothing of the layer here: a pixel only for what the layer changes of the picture and the layers do not give yet
            if (own == 0 && (Same(before, after, i) || Near(now, after, i, slack))) continue;
            // the layer's own opacity, moved to what gives the next picture's opacity to the slack (over an opaque
            // picture, any does)
            double a = own / 255.0, room = 1 - under;
            if (room > 1e-9) a = Math.Clamp(a, (want - slack - under) / room, (want + slack - under) / room);
            a = Math.Clamp(a, 0, 1);
            // and raised where colours of 0..1 cannot give the next picture's (colour = (t - (1 - a) u) / a): until they
            // do to the slack or, over a see-through picture, until the picture's opacity is as far off as the colour
            // still is (more of the layer is more of the picture too)
            double least = a;
            for (int c = 0; c < 3; c++)
            {
                double u = now[i + c], t = after[i + c] / 255.0 * want;
                if (u > 0) least = Math.Max(least, Math.Min((u - t - slack) / u, (u - t - under + want) / (u + room)));
                if (u < 1) least = Math.Max(least, Math.Min((t - u - slack) / (1 - u), (t - u - under + want) / (1 - u + room)));
            }
            a = least;
            if (own <= Faint) a = Math.Min(a, own / 255.0 + most);
            int stored = (int)Math.Round(255 * Math.Min(a, 1));
            if (stored == 0) continue;
            a = stored / 255.0;
            o ??= new byte[after.Length];
            for (int c = 0; c < 3; c++)
            {
                double u = now[i + c], t = after[i + c] / 255.0 * want;
                int colour = (int)Math.Round(255 * Math.Clamp((t - (1 - a) * u) / a, 0, 1));
                o[i + c] = (byte)colour;
                now[i + c] = a * colour / 255.0 + (1 - a) * u;
            }
            o[i + 3] = (byte)stored;
            now[i + 3] = a + (1 - a) * under;
        }
        return o;
    }

    /// <summary>Whether two pictures hold the same pixel: the same opacity, and colours under it less than a step of 255 apart.</summary>
    static bool Same(byte[] x, byte[] y, int i)
    {
        int a = x[i + 3];
        if (a != y[i + 3]) return false;
        for (int c = 0; c < 3; c++)
            if (Math.Abs(x[i + c] - y[i + c]) * a >= 255) return false;
        return true;
    }

    /// <summary>Whether what the layers give is a picture's pixel to the slack (opacity, and colours under it).</summary>
    static bool Near(double[] now, byte[] picture, int i, double slack)
    {
        double want = picture[i + 3] / 255.0;
        if (Math.Abs(now[i + 3] - want) > slack) return false;
        for (int c = 0; c < 3; c++)
            if (Math.Abs(now[i + c] - picture[i + c] / 255.0 * want) > slack) return false;
        return true;
    }
}
