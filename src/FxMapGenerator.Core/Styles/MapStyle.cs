using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Styles;

/// <summary>An RGB colour of a style (<c>#rrggbb</c>).</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Parse(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"'{hex}' is not a colour (#rrggbb)");
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public override string ToString() => $"#{R:x2}{G:x2}{B:x2}";
}

/// <summary>
/// A map style (<c>data/styles/&lt;id&gt;.json</c>, bundled: <c>postalcodemap</c>, <c>regional</c>, <c>roadmap</c>): what
/// the cell stages make (ground paint keys, ground raster, shade, sea bands, canopy, building colours, region colours,
/// contours) and how the drawing colours it. See <c>Docs/spec/style-format.ja.md</c>.
/// </summary>
public sealed class MapStyle
{
    public required string Id { get; init; }
    /// <summary>A bundled style's English and Japanese names (shown in the screens' language), a project's style's one name.</summary>
    public required ItemName Name { get; init; }
    /// <summary>The credit the exported maps drawn with the style carry (<c>credit</c>, optional), e.g. where its look comes from.</summary>
    public string? Credit { get; init; }
    public required Rgb Background { get; init; }
    /// <summary>
    /// The open sea beyond the range (the tiles outside it, the viewer behind the tiles): the colour of the sea's deepest band
    /// over sand, so that the sea of the edge blocks runs on without a step.
    /// </summary>
    public Rgb OpenSea => Paint.Sea.Sand[^1];
    /// <summary>The open sea's opacity: the deepest band's (<see cref="PaintStyle.SeaOpacity"/>), 0 to 255.</summary>
    public byte OpenSeaAlpha => (byte)Math.Round(Paint.SeaOpacity[^1] * 255);
    /// <summary>The open sea as the records keep it: its colour, followed by its opacity when that is not 1.</summary>
    public string OpenSeaText => Paint.SeaOpacity[^1] >= 1 ? OpenSea.ToString()
        : OpenSea + " " + Paint.SeaOpacity[^1].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Some water is drawn see-through (an opacity under 1): the drawings then keep their transparency.</summary>
    public bool SeeThroughWater => Paint.WaterOpacity < 1 || Paint.SeaOpacity.Any(o => o < 1);
    /// <summary>Per ground class (<see cref="GroundClasses.Names"/> order): the ground paint key its cells are drawn with.</summary>
    public required IReadOnlyList<string> GroundPaints { get; init; }
    public GroundRasterStyle? GroundRaster { get; init; }
    public ShadeStyle? Shade { get; init; }
    public required SeaStyle Sea { get; init; }
    public required CanopyStyle Canopy { get; init; }
    public required RegionsStyle Regions { get; init; }
    public required BuildingsStyle Buildings { get; init; }
    public ContoursStyle? Contours { get; init; }
    /// <summary>The labels section (placement and drawing of the text); null when the style draws no labels.</summary>
    public LabelsStyle? Labels { get; init; }
    public required PaintStyle Paint { get; init; }
    /// <summary>The file's text as parsed, for the records of what a step was made with.</summary>
    public required JsonObject Source { get; init; }

    public static IReadOnlyList<string> BuiltIn { get; } = [AtlasPresets.PostalCodeMap, AtlasPresets.Regional, "roadmap"];

    string? _digest;

    /// <summary>A short digest of the style's values (<see cref="Source"/>): another value, another digest.</summary>
    public string Digest => _digest ??= Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Source.ToJsonString())))[..16];

    static readonly Dictionary<string, MapStyle> Cache = new(StringComparer.Ordinal);

    /// <summary>A bundled style by id.</summary>
    public static MapStyle Builtin(string id)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var s)) return s;
            if (!BuiltIn.Contains(id)) throw new ArgumentException($"no bundled style '{id}'");
            using var stream = EmbeddedData.Open($"styles/{id}.json");
            using var reader = new StreamReader(stream);
            s = Parse(reader.ReadToEnd(), $"data/styles/{id}.json");
            Cache[id] = s;
            return s;
        }
    }

    /// <summary>Reads a style file's text; every problem is named in the exception.</summary>
    public static MapStyle Parse(string json, string where)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject() ?? throw new StyleException($"{where}: empty"); }
        catch (JsonException ex) { throw new StyleException($"{where}: not JSON ({ex.Message})"); }
        var r = new Reader(where);
        var gp = r.Obj(root, "groundPaints");
        var paints = GroundClasses.Names.Select(c => r.Str(gp, c)).ToList();
        var style = new MapStyle
        {
            Id = r.Str(root, "id"),
            Name = r.Name(root, "name"),
            Credit = root["credit"] is null ? null : r.Str(root, "credit"),
            Background = r.Color(root, "background"),
            GroundPaints = paints,
            GroundRaster = root["groundRaster"] is JsonObject g ? GroundRasterStyle.Read(r, g) : null,
            Shade = root["shade"] is JsonObject sh ? ShadeStyle.Read(r, sh) : null,
            Sea = SeaStyle.Read(r, r.Obj(root, "sea")),
            Canopy = CanopyStyle.Read(r, r.Obj(root, "canopy")),
            Regions = RegionsStyle.Read(r, r.Obj(root, "regions")),
            Buildings = BuildingsStyle.Read(r, r.Obj(root, "buildings")),
            Contours = root["contours"] is JsonObject ct ? ContoursStyle.Read(r, ct) : null,
            Labels = root["labels"] is JsonObject lb ? LabelsStyle.Read(r, lb) : null,
            Paint = PaintStyle.Read(r, r.Obj(root, "paint")),
            Source = root,
        };
        foreach (var key in style.GroundPaints.Distinct())
            if (key != "water" && !style.Paint.Ground.ContainsKey(key)) r.Fail($"groundPaints: paint.ground has no colour for '{key}'");
        int bands = style.Sea.Bands.Count + 1;
        if (style.Paint.Sea.Sand.Count != bands || style.Paint.Sea.Rock.Count != bands) r.Fail($"paint.sea: {bands} colours of sand and of rock needed for {bands - 1} band edges");
        if (style.Paint.SeaOpacity.Count != bands) r.Fail($"paint.sea.opacity: {bands} opacities needed for {bands - 1} band edges");
        r.Throw();
        return style;
    }

    /// <summary>Collects the problems of one file, so a user sees all of them at once.</summary>
    internal sealed class Reader(string where)
    {
        readonly List<string> _problems = new();

        public void Fail(string problem) => _problems.Add(problem);

        public void Throw()
        {
            if (_problems.Count > 0) throw new StyleException($"{where}: " + string.Join("; ", _problems));
        }

        public JsonObject Obj(JsonObject o, string key)
        {
            if (o[key] is JsonObject x) return x;
            Fail($"'{key}' must be an object");
            return new JsonObject();
        }

        public string Str(JsonObject o, string key)
        {
            if (o[key] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            Fail($"'{key}' must be text");
            return "";
        }

        /// <summary>A name: a text, or <c>{"en", "ja"}</c> (a bundled style's).</summary>
        public ItemName Name(JsonObject o, string key)
        {
            if (ItemName.FromNode(o[key]) is { } n) return n;
            Fail($"'{key}' must be a text or {{ \"en\", \"ja\" }}");
            return ItemName.Of("");
        }

        public double Num(JsonObject o, string key)
        {
            if (o[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
            Fail($"'{key}' must be a number");
            return 0;
        }

        public Rgb Color(JsonObject o, string key) => ParseColor(Str(o, key), key);

        public Rgb ParseColor(string s, string key)
        {
            try { return Rgb.Parse(s); }
            catch (FormatException) { Fail($"'{key}': '{s}' is not a colour (#rrggbb)"); return default; }
        }

        public List<double> Nums(JsonObject o, string key)
        {
            if (o[key] is JsonArray a && a.All(x => x is JsonValue v && v.TryGetValue<double>(out _))) return a.Select(x => x!.GetValue<double>()).ToList();
            Fail($"'{key}' must be a list of numbers");
            return new List<double>();
        }

        public List<string> Strs(JsonObject o, string key)
        {
            if (o[key] is JsonArray a && a.All(x => x is JsonValue v && v.TryGetValue<string>(out _))) return a.Select(x => x!.GetValue<string>()).ToList();
            Fail($"'{key}' must be a list of texts");
            return new List<string>();
        }

        /// <summary>An object of texts, in the file's order.</summary>
        public List<KeyValuePair<string, string>> Texts(JsonObject o, string key) =>
            Obj(o, key).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : Bad(key, kv.Key))).ToList();

        string Bad(string key, string sub)
        {
            Fail($"'{key}.{sub}' must be text");
            return "";
        }
    }
}

public sealed class StyleException(string message) : Exception(message);

/// <summary>
/// The 1 m ground picture under the vector layers: the material colours blended (<c>blend</c>) or the region colours
/// with the materials as tones (<c>regions</c>). A blend's <see cref="Method"/> says how the natural ground mixes, with
/// the values of its own object (the objects of the other methods stay in the file for switching, unused):
/// <c>none</c> (not at all), <c>blur</c> (a Gaussian of <c>width</c> m), <c>patches</c> (the patches under
/// <c>minArea</c> m² take the ground around them, then a Gaussian of <c>edgeWidth</c> m), <c>detail</c> (a Gaussian
/// of <c>width</c> m with <c>amount</c> of the unblurred colours added back) or <c>brush</c> (a Kuwahara filter of
/// <c>radius</c> m, then a Gaussian of <c>edgeWidth</c> m).
/// </summary>
public sealed record GroundRasterStyle(string Mode, IReadOnlyList<string> Natural, double ToneBlur, IReadOnlyList<GroundTone> Tones, double TreeDarkening)
{
    public static readonly IReadOnlyList<string> Methods = ["none", "blur", "patches", "detail", "brush"];

    /// <summary>The values of each method (none has none): key, and the largest value allowed.</summary>
    static readonly Dictionary<string, (string Key, double Max)[]> MethodValues = new()
    {
        ["blur"] = [("width", double.MaxValue)],
        ["patches"] = [("minArea", double.MaxValue), ("edgeWidth", double.MaxValue)],
        ["detail"] = [("width", double.MaxValue), ("amount", 1)],
        ["brush"] = [("radius", double.MaxValue), ("edgeWidth", double.MaxValue)],
    };

    public string Method { get; init; } = "blur";
    /// <summary>The Gaussian's width (m) of the method: blur and detail <c>width</c>, patches and brush <c>edgeWidth</c>; 0 for none.</summary>
    public double Width { get; init; }
    public double MinArea { get; init; }
    public double Amount { get; init; }
    public double Radius { get; init; }

    /// <summary>The tone names in the order they are applied, and the ground classes each covers.</summary>
    public static readonly IReadOnlyList<(string Name, string[] Classes)> ToneClasses =
        [("paved", ["urban", "defaultMaterial"]), ("dirt", ["dirt"]), ("sand", ["sand", "beach"]), ("rock", ["rock"]), ("snow", ["snow"])];

    /// <summary>A style file's groundRaster with only what the picture depends on: a blend's objects of the other methods left out.</summary>
    public static JsonObject InUse(JsonObject groundRaster)
    {
        var o = groundRaster.DeepClone().AsObject();
        if (o["mode"]?.GetValue<string>() == "blend")
        {
            var method = o["method"]?.GetValue<string>();
            foreach (var m in MethodValues.Keys) if (m != method) o.Remove(m);
        }
        return o;
    }

    internal static GroundRasterStyle Read(MapStyle.Reader r, JsonObject o)
    {
        var mode = r.Str(o, "mode");
        switch (mode)
        {
            case "blend":
            {
                var natural = r.Strs(o, "natural");
                foreach (var n in natural) if (!GroundClasses.Names.Contains(n)) r.Fail($"groundRaster.natural: unknown ground class '{n}'");
                var method = r.Str(o, "method");
                if (!Methods.Contains(method)) r.Fail($"groundRaster.method '{method}' ({string.Join(", ", Methods)})");
                var values = new Dictionary<string, double>();
                foreach (var (m, keys) in MethodValues)
                {
                    if (o[m] is null && m != method) continue;          // the chosen method's object is needed, the others are checked when there
                    var mo = r.Obj(o, m);
                    foreach (var (key, max) in keys)
                    {
                        var v = r.Num(mo, key);
                        if (v < 0 || v > max) r.Fail($"groundRaster.{m}.{key}: {v} is out of range (0 to {max})");
                        if (m == method) values[key] = v;
                    }
                }
                double V(string key) => values.GetValueOrDefault(key);
                return new GroundRasterStyle(mode, natural, 0, [], 1)
                {
                    Method = method, Width = method is "patches" or "brush" ? V("edgeWidth") : V("width"), MinArea = V("minArea"), Amount = V("amount"), Radius = V("radius"),
                };
            }
            case "regions":
            {
                var tones = new List<GroundTone>();
                var to = r.Obj(o, "tones");
                foreach (var (name, _) in ToneClasses)
                {
                    if (to[name] is not JsonObject t) continue;
                    tones.Add(new GroundTone(name, r.Color(t, "color"), r.Num(t, "strength")));
                }
                foreach (var kv in to) if (!ToneClasses.Any(tc => tc.Name == kv.Key)) r.Fail($"groundRaster.tones: unknown tone '{kv.Key}' (paved, dirt, sand, rock, snow)");
                return new GroundRasterStyle(mode, [], r.Num(o, "toneBlur"), tones, r.Num(o, "treeDarkening"));
            }
            default:
                r.Fail($"groundRaster.mode '{mode}' (blend or regions)");
                return new GroundRasterStyle(mode, [], 0, [], 1);
        }
    }
}

public sealed record GroundTone(string Name, Rgb Color, double Strength);

/// <summary>Hill shading: the heights smoothed by a Gaussian of <see cref="Blur"/> m, lit from several lights, multiplied into the ground colours by <see cref="Strength"/>.</summary>
public sealed record ShadeStyle(double Blur, double Altitude, IReadOnlyList<(double Azimuth, double Weight)> Lights, double Strength)
{
    internal static ShadeStyle Read(MapStyle.Reader r, JsonObject o)
    {
        var lights = new List<(double, double)>();
        if (o["lights"] is JsonArray a)
            foreach (var l in a)
                if (l is JsonObject lo) lights.Add((r.Num(lo, "azimuth"), r.Num(lo, "weight")));
                else r.Fail("shade.lights: every light is { azimuth, weight }");
        else r.Fail("'lights' must be a list");
        if (lights.Count == 0) r.Fail("shade.lights: at least one light");
        return new ShadeStyle(r.Num(o, "blur"), r.Num(o, "altitude"), lights, r.Num(o, "strength"));
    }
}

/// <summary>Water depth bands: the band edges (m, increasing) and the smallest rock patch kept on the sea bed (m²).</summary>
public sealed record SeaStyle(IReadOnlyList<double> Bands, double RockMinArea)
{
    internal static SeaStyle Read(MapStyle.Reader r, JsonObject o)
    {
        var bands = r.Nums(o, "bands");
        for (int i = 1; i < bands.Count; i++) if (!(bands[i] > bands[i - 1])) r.Fail("sea.bands must increase");
        return new SeaStyle(bands, r.Num(o, "rockMinArea"));
    }
}

/// <summary>Tree canopy: how it is drawn (<c>none</c>, <c>tint</c>, <c>dots</c>, <c>hatch</c>), the smallest patch kept (m²), and each way's look.</summary>
public sealed record CanopyStyle(string Mode, double MinArea, Rgb TintColor, double TintAlpha, CanopyPattern Dots, CanopyPattern Hatch)
{
    internal static CanopyStyle Read(MapStyle.Reader r, JsonObject o)
    {
        var mode = r.Str(o, "mode");
        if (mode is not ("none" or "tint" or "dots" or "hatch")) r.Fail($"canopy.mode '{mode}' (none, tint, dots or hatch)");
        var tint = r.Obj(o, "tint");
        CanopyPattern P(string key)
        {
            var p = r.Obj(o, key);
            return new CanopyPattern(r.Color(p, "color"), r.Num(p, "spacing"), r.Num(p, "size"));
        }
        return new CanopyStyle(mode, r.Num(o, "minArea"), r.Color(tint, "color"), r.Num(tint, "alpha"), P("dots"), P("hatch"));
    }
}

public sealed record CanopyPattern(Rgb Color, double Spacing, double Size);

/// <summary>Region colours: the regions in order with their colours, the zone → region table, the blur (Gaussian sigma, m) and the region used out at sea.</summary>
public sealed record RegionsStyle(double Blur, string Sea, IReadOnlyList<(string Name, Rgb Color)> Colors, IReadOnlyDictionary<string, string> Zones)
{
    public int IndexOf(string region)
    {
        for (int i = 0; i < Colors.Count; i++) if (Colors[i].Name == region) return i;
        return -1;
    }

    internal static RegionsStyle Read(MapStyle.Reader r, JsonObject o)
    {
        var colors = r.Texts(o, "colors").Select(kv => (kv.Key, r.ParseColor(kv.Value, "regions.colors." + kv.Key))).ToList();
        var zones = r.Texts(o, "zones").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var s = new RegionsStyle(r.Num(o, "blur"), r.Str(o, "sea"), colors, zones);
        if (s.IndexOf(s.Sea) < 0) r.Fail($"regions.sea '{s.Sea}' is not one of regions.colors");
        foreach (var (z, reg) in zones) if (s.IndexOf(reg) < 0) r.Fail($"regions.zones.{z}: '{reg}' is not one of regions.colors");
        return s;
    }
}

/// <summary>One colour per building: the colour of its zone, else of its zone's region, else the default.</summary>
public sealed record BuildingsStyle(string Default, IReadOnlyDictionary<string, string> ByRegion, IReadOnlyDictionary<string, string> ByZone)
{
    internal static BuildingsStyle Read(MapStyle.Reader r, JsonObject o) =>
        new(r.Str(o, "default"), r.Texts(o, "byRegion").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            r.Texts(o, "byZone").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

    /// <summary>The building colour key of a zone.</summary>
    public string KeyOf(string zone, RegionsStyle regions) =>
        ByZone.TryGetValue(zone, out var k) && k.Length > 0 ? k
        : ByRegion.TryGetValue(regions.Zones.TryGetValue(zone, out var reg) ? reg : "", out var k2) ? k2 : Default;
}

/// <summary>Contour lines every <see cref="Interval"/> m of the terrain.</summary>
public sealed record ContoursStyle(double Interval, Rgb Color, double Width)
{
    internal static ContoursStyle Read(MapStyle.Reader r, JsonObject o) => new(r.Num(o, "interval"), r.Color(o, "color"), r.Num(o, "width"));
}

public sealed record RoadPaint(Rgb Fill, Rgb? Casing);

public sealed record TunnelPaint(Rgb Color, double Width, IReadOnlyList<double> Dash, Rgb Fill, double FillAlpha);

/// <summary>
/// The drawing colours: ground paints (in drawing order), water, sea bands (sand and rock bed), buildings, rail, roads;
/// the opacity of the water where no band is painted (<see cref="WaterOpacity"/>) and of each band (<see cref="SeaOpacity"/>,
/// the same over sand and rock), 0 to 1 (1 when the style gives none).
/// </summary>
public sealed record PaintStyle(IReadOnlyDictionary<string, Rgb> Ground, IReadOnlyList<string> GroundOrder, Rgb Water, (IReadOnlyList<Rgb> Sand, IReadOnlyList<Rgb> Rock) Sea,
    IReadOnlyDictionary<string, Rgb> Buildings, Rgb Rail, double CasingWidth, RoadPaint Road, RoadPaint Highway, Rgb TrackColor, double TrackWidth, TunnelPaint? Tunnel,
    double WaterOpacity, IReadOnlyList<double> SeaOpacity)
{
    internal static PaintStyle Read(MapStyle.Reader r, JsonObject o)
    {
        var groundList = r.Texts(o, "ground");
        var ground = groundList.ToDictionary(kv => kv.Key, kv => r.ParseColor(kv.Value, "paint.ground." + kv.Key), StringComparer.Ordinal);
        var sea = r.Obj(o, "sea");
        List<Rgb> Colors(string key) => r.Strs(sea, key).Select(s => r.ParseColor(s, "paint.sea." + key)).ToList();
        var buildings = r.Texts(o, "buildings").ToDictionary(kv => kv.Key, kv => r.ParseColor(kv.Value, "paint.buildings." + kv.Key), StringComparer.Ordinal);
        var roads = r.Obj(o, "roads");
        RoadPaint Road(string key)
        {
            var p = r.Obj(roads, key);
            return new RoadPaint(r.Color(p, "fill"), p["casing"] is null ? null : r.Color(p, "casing"));
        }
        var road = Road("road");
        var highway = Road("highway");
        double casing = roads["casingWidth"] is null ? 0.6 : r.Num(roads, "casingWidth");
        if ((road.Casing is not null || highway.Casing is not null) && roads["casingWidth"] is null) r.Fail("paint.roads.casingWidth is needed with a casing colour");
        var track = r.Obj(roads, "track");
        TunnelPaint? tunnel = null;
        if (roads["tunnel"] is JsonObject t)
            tunnel = new TunnelPaint(r.Color(t, "color"), r.Num(t, "width"), r.Nums(t, "dash"), r.Color(t, "fill"), r.Num(t, "fillAlpha"));
        var sand = Colors("sand");
        double waterOpacity = o["waterOpacity"] is null ? 1 : r.Num(o, "waterOpacity");
        var seaOpacity = sea["opacity"] is null ? Enumerable.Repeat(1.0, sand.Count).ToList() : r.Nums(sea, "opacity");
        if (waterOpacity is < 0 or > 1) r.Fail("paint.waterOpacity must be 0 to 1");
        if (seaOpacity.Any(v => v is < 0 or > 1)) r.Fail("paint.sea.opacity: every opacity must be 0 to 1");
        return new PaintStyle(ground, groundList.Select(kv => kv.Key).ToList(), r.Color(o, "water"), (sand, Colors("rock")), buildings, r.Color(o, "rail"), casing, road, highway,
            r.Color(track, "color"), r.Num(track, "width"), tunnel, waterOpacity, seaOpacity);
    }
}
