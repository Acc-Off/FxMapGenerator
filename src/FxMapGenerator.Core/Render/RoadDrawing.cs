using FxMapGenerator.Core.Styles;
using SkiaSharp;

namespace FxMapGenerator.Core.Render;

/// <summary>
/// How the roads of a map are drawn, in a picture whose pixel (0, 0) is the game point (<c>x0</c>, <c>y0</c>) at
/// <c>ppm</c> pixels a metre (y grows southwards): the tunnels (one translucent fill of every outline and a dashed edge),
/// the unpaved tracks, then at level 0 the ribbons' casings, the corner patches' outlines and the junction arcs'
/// casings, the ribbons and patches filled (roads before highways), the rounded junction corners,
/// and last the raised runs level by level (casings, then tracks, roads and highways). A cell's drawing
/// (<see cref="CellPainter"/>) and the road editor's road tiles (<see cref="RoadTiles"/>) both draw with this.
/// </summary>
public sealed class RoadDrawing
{
    readonly double _x0, _y0, _ppm;
    readonly List<IDisposable> _owned;

    /// <param name="owned">Paths and paints made here are added to it; the caller disposes them after drawing.</param>
    public RoadDrawing(double x0, double y0, double ppm, List<IDisposable> owned)
    {
        (_x0, _y0, _ppm) = (x0, y0, ppm);
        _owned = owned;
    }

    float Px(double x) => (float)((x - _x0) * _ppm);
    float Py(double y) => (float)((_y0 - y) * _ppm);

    SKPath Build(Action<SKPathBuilder> add, SKPathFillType fill = SKPathFillType.Winding)
    {
        using var b = new SKPathBuilder { FillType = fill };
        add(b);
        var p = b.Detach();
        _owned.Add(p);
        return p;
    }

    SKPaint Paint(SKPaint p)
    {
        _owned.Add(p);
        return p;
    }

    void AddRing(SKPathBuilder path, double[] pts, bool close)
    {
        if (pts.Length < 2) return;
        path.MoveTo(Px(pts[0]), Py(pts[1]));
        for (int i = 2; i + 1 < pts.Length; i += 2) path.LineTo(Px(pts[i]), Py(pts[i + 1]));
        if (close) path.Close();
    }

    static SKColor Col(Rgb c, byte alpha = 255) => new(c.R, c.G, c.B, alpha);

    SKPaint Fill(Rgb c, byte alpha = 255) => Paint(new SKPaint { Color = Col(c, alpha), IsAntialias = true, Style = SKPaintStyle.Fill });

    /// <summary>A stroke; mitred corners are cut beyond 10 times the width, as cairo does (Skia's own limit is 4).</summary>
    SKPaint Stroke(Rgb c, double widthPx, SKStrokeCap cap, SKStrokeJoin join = SKStrokeJoin.Round) =>
        Paint(new SKPaint { Color = Col(c), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)widthPx, StrokeCap = cap, StrokeJoin = join, StrokeMiter = 10 });

    Action<SKCanvas> FillPolygon(double[] pts, Rgb colour)
    {
        var path = Build(b => AddRing(b, pts, true));
        var f = Fill(colour);
        return c => c.DrawPath(path, f);
    }

    /// <summary>
    /// Adds the drawing of <paramref name="mine"/> to <paramref name="over"/>, in order; with <paramref name="tags"/>,
    /// what each step paints to it too (one tag a step).
    /// </summary>
    public void Add(List<Action<SKCanvas>> over, CellInputs.Roads mine, MapStyle st, List<StepTag>? tags = null)
    {
        void Put(Action<SKCanvas> step, StepTag tag)
        {
            over.Add(step);
            tags?.Add(tag);
        }
        var P = st.Paint;
        double cm = P.CasingWidth * _ppm;
        Rgb[] fill = [P.Road.Fill, P.Highway.Fill];
        Rgb?[] casing = [P.Road.Casing, P.Highway.Casing];
        // tunnels: one translucent fill of every outline (even-odd) and one dashed edge
        if (P.Tunnel is { } tp)
        {
            var path = Build(b =>
            {
                foreach (var g in mine.Tunnels)
                    foreach (var ring in g.Rings) AddRing(b, ring, true);
            }, SKPathFillType.EvenOdd);
            if (!path.IsEmpty)
            {
                var tf = Fill(tp.Fill, (byte)Math.Round(tp.FillAlpha * 255));
                var edge = Stroke(tp.Color, tp.Width * _ppm, SKStrokeCap.Butt);
                if (tp.Dash.Count >= 2) edge.PathEffect = SKPathEffect.CreateDash(tp.Dash.Select(v => (float)(v * _ppm)).ToArray(), 0);
                Put(c => { c.DrawPath(path, tf); c.DrawPath(path, edge); }, new StepTag("tunnel", tp.Fill));
            }
        }
        // unpaved tracks: short strokes with round ends
        {
            var t = mine.Tracks;
            var path = Build(b =>
            {
                for (int i = 0; i + 3 < t.Length; i += 4)
                {
                    b.MoveTo(Px(t[i]), Py(t[i + 1]));
                    b.LineTo(Px(t[i + 2]), Py(t[i + 3]));
                }
            });
            if (!path.IsEmpty)
            {
                var pen = Stroke(P.TrackColor, Math.Round(P.TrackWidth * 20, MidpointRounding.ToEven) / 20 * _ppm, SKStrokeCap.Round);
                Put(c => c.DrawPath(path, pen), new StepTag("track", P.TrackColor));
            }
        }
        var ground = mine.Ground;
        var patches = mine.Patches;
        var corners = mine.Corners;
        // level 0: the ribbons' casings, the corner patches' outlines, the junction arcs' casings
        foreach (var g in ground)
            if (casing[g.Class] is { } cc) Put(FillPolygon(g.Casing, cc), new StepTag("casing", cc, g.Class));
        foreach (var p in patches)
            if (casing[p.Class] is { } cc)
            {
                var path = Build(b => AddRing(b, p.Ring, true));
                var pen = Stroke(cc, 2 * cm, SKStrokeCap.Butt, SKStrokeJoin.Miter);
                Put(c => c.DrawPath(path, pen), new StepTag("casing", cc, p.Class));
            }
        foreach (var k in corners)
            if (casing[k.Class] is { } cc)
            {
                var path = Build(b => AddRing(b, k.Arc, false));
                var pen = Stroke(cc, 2 * cm, SKStrokeCap.Butt);
                Put(c => c.DrawPath(path, pen), new StepTag("casing", cc, k.Class));
            }
        for (int want = 0; want < 2; want++)
        {
            foreach (var g in ground) if (g.Class == want) Put(FillPolygon(g.Fill, fill[want]), new StepTag("road", fill[want], want));
            foreach (var p in patches) if (p.Class == want) Put(FillPolygon(p.Ring, fill[want]), new StepTag("road", fill[want], want));
        }
        for (int want = 0; want < 2; want++)
            foreach (var k in corners)
            {
                if (k.Class != want) continue;
                var region = Build(b => AddRing(b, k.Region, true));
                var f = Fill(fill[want]);
                SKPath? seam = null;
                SKPaint? seamPen = null;
                if (k.Seam.Length >= 4)
                {
                    seam = Build(b => AddRing(b, k.Seam, false));
                    seamPen = Stroke(fill[want], 2 * cm, SKStrokeCap.Butt);
                }
                Put(c =>
                {
                    c.DrawPath(region, f);
                    if (seam is null) return;
                    c.Save();
                    c.ClipPath(region, SKClipOperation.Intersect, true);
                    c.DrawPath(seam, seamPen!);
                    c.Restore();
                }, new StepTag("road", fill[want], want));
            }
        // levels 1, 2, ...: the raised runs' casings, then their fills (tracks, roads, highways)
        foreach (var level in mine.Raised.Select(x => x.Level).Distinct().Order())
        {
            var runs = mine.Raised.Where(x => x.Level == level).ToList();
            foreach (var x in runs)
                if (x.Class < 2 && casing[x.Class] is { } cc) Put(FillPolygon(x.Casing, cc), new StepTag("casing", cc, x.Class));
            foreach (int want in (ReadOnlySpan<int>)[2, 0, 1])
                foreach (var x in runs)
                    if (x.Class == want)
                        Put(FillPolygon(x.Fill, want == 2 ? P.TrackColor : fill[want]), want == 2 ? new StepTag("track", P.TrackColor) : new StepTag("road", fill[want], want));
        }
    }
}
