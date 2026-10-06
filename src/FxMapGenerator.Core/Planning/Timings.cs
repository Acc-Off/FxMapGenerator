using System.Text.Json;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Planning;

/// <summary>
/// Seconds per unit behind the estimates (bundled data/timings.json). Visit times are per block and class; stage times
/// are for one worker per unit, spread over the workers of the parallel level by the planner.
/// </summary>
public sealed class Timings
{
    public required (double Low, double High) VisitSpread { get; init; }
    public required (double Low, double High) StageSpread { get; init; }
    public required (double Low, double High) Precheck { get; init; }
    public required (double Low, double High) Prepare { get; init; }
    public required (double Low, double High) Cleanup { get; init; }
    /// <summary>
    /// Visit item key (shot, height, scanGround, scanRoads, scanCanopy; scanGroundAlone and scanRoadsAlone when the visit
    /// takes no shot or height data of the block first; cameraMove once per block visited with the camera) -> seconds per
    /// land / water block.
    /// </summary>
    public required IReadOnlyDictionary<string, (double Land, double Water)> Visit { get; init; }
    /// <summary>What the camera's move to a block that is not next to the last one adds (the new place streams in): once
    /// per group of neighbouring blocks visited with the camera.</summary>
    public double FarMove { get; init; }
    public double OrthoPerBlock { get; init; }
    /// <summary>The ortho step's first measurement of the scale correction (no measured correction yet): one worker's time
    /// per captured block, the pairs of neighbours spread over the workers.</summary>
    public double ScalePerBlock { get; init; }
    public double GameFiles { get; init; }
    public double RoadGraph { get; init; }
    public double LandcoverPerBlock { get; init; }
    public double Regions { get; init; }
    public double LabelsPerLanguage { get; init; }
    public double Roads { get; init; }
    /// <summary>A cell's map data per land / water block of the cell (its margin blocks too; the sea's depth bands make a
    /// water block the heavier one).</summary>
    public (double Land, double Water) CellPrepPerBlock { get; init; }
    /// <summary>A cell's drawing of one atlas map per land / water block of the cell.</summary>
    public (double Land, double Water) CellDrawAtlasPerBlock { get; init; }
    /// <summary>A cell's drawing of the road map per land / water block of the cell.</summary>
    public (double Land, double Water) CellDrawRoadMapPerBlock { get; init; }
    /// <summary>The lower zooms of a map on one worker; the step works in parallel inside, so it takes this over the workers.</summary>
    public double LowZoomSatellite { get; init; }
    public double LowZoomAtlas { get; init; }
    public double LowZoomRoadMap { get; init; }
    /// <summary>One minimap sheet (both textures), its bands on the workers left over.</summary>
    public double YtdPerSheet { get; init; }

    public double VisitSeconds(string item, BlockClass cls) => cls == BlockClass.Land ? Visit[item].Land : Visit[item].Water;

    static double Of((double Land, double Water) v, BlockClass cls) => cls == BlockClass.Land ? v.Land : v.Water;

    /// <summary>A cell's map data for one of its blocks.</summary>
    public double CellPrep(BlockClass cls) => Of(CellPrepPerBlock, cls);

    /// <summary>The drawing of one cell block of an atlas or road map.</summary>
    public double CellDraw(MapKind kind, BlockClass cls) => Of(kind == MapKind.Atlas ? CellDrawAtlasPerBlock : CellDrawRoadMapPerBlock, cls);

    /// <summary>The lower zooms of a map on one worker (spread over the workers by the planner).</summary>
    public double LowZoom(MapKind kind) => kind switch
    {
        MapKind.Satellite => LowZoomSatellite,
        MapKind.Atlas => LowZoomAtlas,
        _ => LowZoomRoadMap,
    };


    static readonly Lazy<Timings> Bundled = new(() => Parse(EmbeddedData.Open("timings.json")));

    public static Timings Default => Bundled.Value;

    public static Timings Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        static (double, double) Pair(JsonElement e) => (e[0].GetDouble(), e[1].GetDouble());
        var visit = r.GetProperty("visit");
        var stages = r.GetProperty("stages");
        double S(string stage, string key) => stages.GetProperty(stage).GetProperty(key).GetDouble();
        static (double, double) Lw(JsonElement e) => (e.GetProperty("land").GetDouble(), e.GetProperty("water").GetDouble());
        var cells = stages.GetProperty("cells");
        var items = new Dictionary<string, (double, double)>();
        foreach (var key in new[] { "cameraMove", "shot", "height", "scanGround", "scanRoads", "scanGroundAlone", "scanRoadsAlone", "scanCanopy" })
        {
            var e = visit.GetProperty(key);
            items[key] = (e.GetProperty("land").GetDouble(), e.GetProperty("water").GetDouble());
        }
        return new Timings
        {
            VisitSpread = Pair(r.GetProperty("spread").GetProperty("visit")),
            StageSpread = Pair(r.GetProperty("spread").GetProperty("stage")),
            Precheck = Pair(r.GetProperty("precheck")),
            Prepare = Pair(visit.GetProperty("prepare")),
            Cleanup = Pair(visit.GetProperty("cleanup")),
            Visit = items,
            FarMove = visit.GetProperty("farMove").GetDouble(),
            OrthoPerBlock = S("ortho", "perBlock"),
            ScalePerBlock = S("ortho", "scalePerBlock"),
            GameFiles = S("gameFiles", "fixed"),
            RoadGraph = S("roadGraph", "fixed"),
            LandcoverPerBlock = S("landcover", "perBlock"),
            Regions = S("regions", "fixed"),
            LabelsPerLanguage = S("labels", "perLanguage"),
            Roads = S("roads", "fixed"),
            CellPrepPerBlock = Lw(cells.GetProperty("prepPerBlock")),
            CellDrawAtlasPerBlock = Lw(cells.GetProperty("drawPerBlock").GetProperty("atlas")),
            CellDrawRoadMapPerBlock = Lw(cells.GetProperty("drawPerBlock").GetProperty("roadMap")),
            LowZoomSatellite = S("lowZoom", "satellite"),
            LowZoomAtlas = S("lowZoom", "atlas"),
            LowZoomRoadMap = S("lowZoom", "roadMap"),
            YtdPerSheet = stages.GetProperty("ytd").GetProperty("perSheet").GetDouble(),
        };
    }
}

/// <summary>
/// Named shares of the logical processors (the command line's <c>--level</c>); the app and the project keep a number of
/// workers.
/// </summary>
public enum ParallelLevel { Full, Strong, Normal, Light }

public static class ParallelLevels
{
    public static double Share(this ParallelLevel level) => level switch
    {
        ParallelLevel.Full => 1.0,
        ParallelLevel.Strong => 0.7,
        ParallelLevel.Normal => 0.5,
        _ => 0.3,
    };

    public static int Workers(this ParallelLevel level, int processors) => Math.Max(1, (int)Math.Round(processors * level.Share(), MidpointRounding.AwayFromZero));

    public static string Key(this ParallelLevel level) => level.ToString().ToLowerInvariant();

    public static bool TryParse(string? key, out ParallelLevel level)
    {
        foreach (var l in Enum.GetValues<ParallelLevel>())
            if (l.Key() == key) { level = l; return true; }
        level = ParallelLevel.Normal;
        return false;
    }
}
