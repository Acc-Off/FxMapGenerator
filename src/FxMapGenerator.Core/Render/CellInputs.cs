using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.State;

namespace FxMapGenerator.Core.Render;

/// <summary>
/// What a cell's drawing takes of the whole-map data, and digests of it (a cell is drawn again only when what it takes
/// changed): the road shapes within <see cref="RoadPad"/> of the cell's block rectangle, the labels and points
/// of interest whose point lies within <see cref="LabelPad"/> of it. The painter draws exactly these, in this order.
/// </summary>
public static class CellInputs
{
    public const double RoadPad = 50, LabelPad = 200;

    /// <summary>The road shapes of a cell, in the order of the file: the tunnel groups one of whose outlines comes near (all
    /// their outlines), the unpaved segments (x0, y0, x1, y1 each), the ground ribbons, corner patches, junction corners and
    /// raised runs that come near.</summary>
    public sealed record Roads(List<TunnelGroup> Tunnels, double[] Tracks, List<RoadRibbon> Ground, List<CornerPatch> Patches,
        List<JunctionCorner> Corners, List<RaisedRun> Raised);

    /// <summary>True when the points' bounding box comes within <paramref name="pad"/> of the rectangle (west, north, east, south).</summary>
    public static bool Near(double[] pts, (double X0, double Y0, double X1, double Y1) r, double pad)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        for (int i = 0; i + 1 < pts.Length; i += 2)
        {
            minX = Math.Min(minX, pts[i]); maxX = Math.Max(maxX, pts[i]);
            minY = Math.Min(minY, pts[i + 1]); maxY = Math.Max(maxY, pts[i + 1]);
        }
        return maxX >= r.X0 - pad && minX <= r.X1 + pad && maxY >= r.Y1 - pad && minY <= r.Y0 + pad;
    }

    public static Roads RoadsOf(RoadShapesFile.Contents roads, (double X0, double Y0, double X1, double Y1) r)
    {
        const double pad = RoadPad;
        var t = roads.Tracks;
        var tracks = new List<double>();
        for (int i = 0; i + 3 < t.Length; i += 4)
        {
            if (Math.Max(t[i], t[i + 2]) < r.X0 - pad || Math.Min(t[i], t[i + 2]) > r.X1 + pad || Math.Max(t[i + 1], t[i + 3]) < r.Y1 - pad || Math.Min(t[i + 1], t[i + 3]) > r.Y0 + pad) continue;
            tracks.AddRange([t[i], t[i + 1], t[i + 2], t[i + 3]]);
        }
        return new Roads(
            roads.Tunnels.Where(g => g.Rings.Any(ring => Near(ring, r, pad))).ToList(),
            tracks.ToArray(),
            roads.Ground.Where(g => Near(g.Casing, r, pad)).ToList(),
            roads.Patches.Where(p => Near(p.Ring, r, pad)).ToList(),
            roads.Corners.Where(k => Near(k.Region, r, pad) || Near(k.Arc, r, pad)).ToList(),
            roads.Raised.Where(x => Near(x.Casing, r, pad)).ToList());
    }

    public static List<PlacedLabel> LabelsOf(IEnumerable<PlacedLabel> labels, (double X0, double Y0, double X1, double Y1) r) =>
        labels.Where(l => r.X0 - LabelPad <= l.X && l.X <= r.X1 + LabelPad && r.Y1 - LabelPad <= l.Y && l.Y <= r.Y0 + LabelPad).ToList();

    /// <summary>The points of interest a cell draws: those within <see cref="LabelPad"/> of it, and those whose label beside reaches it from the west.</summary>
    public static List<ResolvedPoi> PoisOf(IEnumerable<ResolvedPoi> pois, (double X0, double Y0, double X1, double Y1) r) =>
        pois.Where(p =>
        {
            double east = p.Style.ShowLabel ? PoiLayout.Reach(p, p.Point.Label.Values.MaxBy(n => n.Length) ?? "") : 0;
            return r.X0 - LabelPad - east <= p.Point.X && p.Point.X <= r.X1 + LabelPad && r.Y1 - LabelPad <= p.Point.Y && p.Point.Y <= r.Y0 + LabelPad;
        }).ToList();

    public static string RoadsDigest(RoadShapesFile.Contents roads, (double X0, double Y0, double X1, double Y1) r) =>
        ContentDigest.OfJson(new { roads.TrackWidth, Shapes = RoadsOf(roads, r) });

    public static string LabelsDigest(IEnumerable<PlacedLabel> labels, (double X0, double Y0, double X1, double Y1) r) =>
        ContentDigest.OfJson(LabelsOf(labels, r));

    /// <summary>The digest of what the cell draws of the points of interest (<see cref="ResolvedPoi.Drawn"/>, in drawing order).</summary>
    public static string PoisDigest(IEnumerable<ResolvedPoi> pois, (double X0, double Y0, double X1, double Y1) r) =>
        ContentDigest.Of(string.Join('\n', PoisOf(pois, r).Select(p => p.Drawn())));
}
