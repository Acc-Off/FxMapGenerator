using FxMapGenerator.Core.Geometry;

namespace FxMapGenerator.Core.RoadGraph;

/// <summary>Road classes, lowest first (the order decides which wins where roads of two classes meet).</summary>
public enum RoadClass { Parking, Minor, Track, Street, Major, Highway }

public static class RoadClasses
{
    /// <summary>Name in <c>roads.json</c>.</summary>
    public static string Name(this RoadClass c) => c switch
    {
        RoadClass.Parking => "parking",
        RoadClass.Minor => "minor",
        RoadClass.Track => "track",
        RoadClass.Street => "street",
        RoadClass.Major => "major",
        _ => "highway",
    };

    public static RoadClass Parse(string name) => name switch
    {
        "parking" => RoadClass.Parking,
        "minor" => RoadClass.Minor,
        "track" => RoadClass.Track,
        "street" => RoadClass.Street,
        "major" => RoadClass.Major,
        "highway" => RoadClass.Highway,
        _ => throw new FormatException($"unknown road class '{name}'"),
    };

    /// <summary>Width (m) of a road whose surface could not be measured.</summary>
    public static double DefaultWidth(this RoadClass c) => c switch
    {
        RoadClass.Highway or RoadClass.Major => 20.0,
        RoadClass.Street => 12.0,
        RoadClass.Minor => 5.0,
        _ => 4.0,
    };
}

/// <summary>One map road while the graph is built: a chain of path nodes and its line.</summary>
public sealed class MapRoad
{
    /// <summary>The chain's path nodes (hubs included).</summary>
    public required IReadOnlyList<int> Seq { get; init; }
    public required ChainAttributes Attr { get; init; }
    /// <summary>The line: the chain without its hubs, later moved onto a bundle's midline and simplified.</summary>
    public required List<P2> Points { get; set; }
    /// <summary>Path heights at the vertices of the first line.</summary>
    public required IReadOnlyList<double> Zs { get; init; }
    public RoadClass Class { get; set; }
    /// <summary>Measured width (m); NaN until known.</summary>
    public double Width { get; set; }
    /// <summary>Widths measured on the road's own line.</summary>
    public int WidthSamples { get; init; }
    /// <summary>The width from the widths the road edits fix for some of its links; the road keeps it (null = none).</summary>
    public double? EditedWidth { get; init; }
    public double? BundleWidth { get; set; }
    /// <summary>Moved onto the midline of a bundle of parallel chains of its street (a divided road).</summary>
    public bool Bundle { get; set; }
    /// <summary>Length after the attributes (m, the chain's links).</summary>
    public double Length => Attr.Length;
}
