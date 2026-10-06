using FxMapGenerator.Core.Grids;

namespace FxMapGenerator.Core.Render;

/// <summary>
/// The hill shading of a cell as the drawing applies it: the 1 m illumination (0..255 → 0..1 in float32) enlarged to the
/// drawing's pixels as SciPy's <c>ndimage.zoom</c> (order 1) enlarges it — the output size is the grid's size times the
/// scale, rounded; the corners of the grid meet the corners of the output; each pixel is the bilinear mix of its four
/// grid values, weighted and added in double in a fixed order, then stored as float32 — and every colour channel of the
/// ground under it multiplied by <c>1 + strength (illumination - flat) / flat</c> in float32 (then cut to 0..255, the
/// fraction dropped).
/// </summary>
public sealed class ShadeLayer
{
    readonly float[] _values;
    readonly int _w, _h, _ow, _oh;
    readonly double _zx, _zy;
    readonly float _flat, _strength;
    readonly int _ox, _oy;

    /// <param name="light">The illumination grid (u8).</param>
    /// <param name="flat">The illumination of flat ground.</param>
    /// <param name="ppm">Pixels per metre of the drawing.</param>
    /// <param name="offsetX">Where the grid's cell (0, 0) lands in the drawing, pixels (rounded to whole pixels as the drawing does).</param>
    public ShadeLayer(Grid<byte> light, double flat, double strength, double ppm, double offsetX, double offsetY)
    {
        _w = light.Width;
        _h = light.Height;
        _values = new float[light.Count];
        for (int i = 0; i < _values.Length; i++) _values[i] = light.Data[i] / 255.0f;
        _oh = (int)Math.Round(_h * ppm, MidpointRounding.ToEven);
        _ow = (int)Math.Round(_w * ppm, MidpointRounding.ToEven);
        _zy = _oh > 1 ? (double)(_h - 1) / (_oh - 1) : 1.0;
        _zx = _ow > 1 ? (double)(_w - 1) / (_ow - 1) : 1.0;
        _flat = (float)flat;
        _strength = (float)strength;
        _ox = (int)Math.Round(offsetX, MidpointRounding.ToEven);
        _oy = (int)Math.Round(offsetY, MidpointRounding.ToEven);
    }

    /// <summary>The enlarged illumination at output pixel (r, c) of the zoomed grid.</summary>
    float Zoomed(int r, int c)
    {
        double cy = r * _zy, cx = c * _zx;
        int y0 = (int)Math.Floor(cy), x0 = (int)Math.Floor(cx);
        double ty = cy - y0, tx = cx - x0;
        int y1 = Math.Min(y0 + 1, _h - 1), x1 = Math.Min(x0 + 1, _w - 1);
        y0 = Math.Min(y0, _h - 1);
        x0 = Math.Min(x0, _w - 1);
        double t = 0.0;
        t += _values[y0 * _w + x0] * (1.0 - ty) * (1.0 - tx);
        t += _values[y0 * _w + x1] * (1.0 - ty) * tx;
        t += _values[y1 * _w + x0] * ty * (1.0 - tx);
        t += _values[y1 * _w + x1] * ty * tx;
        return (float)t;
    }

    /// <summary>The factor the shading multiplies the RGB of the drawing's pixel (<paramref name="px"/>, <paramref name="py"/>) by (as <see cref="Apply"/> does).</summary>
    public float Factor(int px, int py)
    {
        int zr = py - _oy, zc = px - _ox;
        float hs = zr >= 0 && zr < _oh && zc >= 0 && zc < _ow ? Zoomed(zr, zc) : _flat;
        return 1.0f + _strength * (hs - _flat) / _flat;
    }

    /// <summary>
    /// Multiplies the RGB of the pixels of a drawn piece (straight = premultiplied RGBA, opaque ground) by the shading;
    /// the piece's pixel (0, 0) is the drawing's pixel (<paramref name="px0"/>, <paramref name="py0"/>).
    /// </summary>
    public void Apply(Span<byte> rgba, int rowBytes, int width, int height, int px0, int py0)
    {
        float oneF = 1.0f;
        for (int r = 0; r < height; r++)
        {
            int zr = py0 + r - _oy;
            var row = rgba.Slice(r * rowBytes, width * 4);
            for (int c = 0; c < width; c++)
            {
                int zc = px0 + c - _ox;
                float hs = zr >= 0 && zr < _oh && zc >= 0 && zc < _ow ? Zoomed(zr, zc) : _flat;
                float f = oneF + _strength * (hs - _flat) / _flat;
                for (int k = 0; k < 3; k++)
                {
                    float v = row[c * 4 + k] * f;
                    row[c * 4 + k] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
                }
            }
        }
    }
}
