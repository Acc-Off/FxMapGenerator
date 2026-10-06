using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.RoadGraph;

namespace FxMapGenerator.Core.Styles;

/// <summary>The street names of one road class: size (em, m), weight, labels per length, the rules between labels.</summary>
/// <param name="Repeat">One label per this many metres of a line (at least one).</param>
/// <param name="SameNameGap">The least distance between two labels of the same name (m).</param>
/// <param name="MaxGlyphTurn">The most the line may turn between two glyphs (degrees).</param>
/// <param name="LetterSpacing">Space after every glyph but the last, in em.</param>
public sealed record StreetClassStyle(double Size, string Weight, double Repeat, double SameNameGap, double MaxGlyphTurn, double LetterSpacing);

/// <summary>
/// The labels section of a style: fonts per language, the space kept around a label, postal codes (size per game zone),
/// zone names, street names per road class. Colours and outlines only change the drawing; everything else changes
/// where the labels go, and <see cref="PlacementKey"/> names that part (styles with the same key share their labels).
/// </summary>
public sealed class LabelsStyle
{
    public required IReadOnlyDictionary<string, string> Fonts { get; init; }
    public required double Clearance { get; init; }
    public required double MinPixels { get; init; }
    public required Rgb PostalColor { get; init; }
    public required double PostalSize { get; init; }
    public required IReadOnlyDictionary<string, double> PostalSizeByZone { get; init; }
    public required Rgb ZoneColor { get; init; }
    public required double ZoneSize { get; init; }
    public required double ZoneSpacing { get; init; }
    public required string ZoneWeight { get; init; }
    public Rgb? ZoneOutline { get; init; }
    public required double ZoneOutlineWidth { get; init; }
    public required double ZoneMinArea { get; init; }
    public required Rgb StreetColor { get; init; }
    public required IReadOnlyDictionary<RoadClass, StreetClassStyle> StreetClasses { get; init; }
    public required bool RouteNumbers { get; init; }
    /// <summary>Short forms written out in English names (in order), e.g. <c>Blvd</c> → <c>Boulevard</c>.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Expand { get; init; }
    /// <summary>12 hex digits of the SHA-256 of the section without its colours, outlines and minimum pixel size.</summary>
    public required string PlacementKey { get; init; }

    /// <summary>The font of a language (English's when the language has none).</summary>
    public string Font(string language) => Fonts.TryGetValue(language, out var f) ? f : Fonts["en"];

    /// <summary>The size of a postal code in a zone (the zone's size, else the default).</summary>
    public double PostalSizeIn(string zone) => PostalSizeByZone.TryGetValue(zone, out var s) ? s : PostalSize;

    static readonly string[] DrawingOnly = ["color", "outline", "outlineWidth", "minPixels"];

    internal static LabelsStyle Read(MapStyle.Reader r, JsonObject o)
    {
        var fonts = r.Texts(o, "fonts").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (!fonts.ContainsKey("en")) r.Fail("labels.fonts: the English font ('en') is needed");
        var postal = r.Obj(o, "postal");
        var zone = r.Obj(o, "zone");
        var street = r.Obj(o, "street");
        var sizes = new Dictionary<string, double>(StringComparer.Ordinal);
        if (postal["sizeByZone"] is JsonObject bz)
            foreach (var kv in bz) sizes[kv.Key] = Positive(r, bz, kv.Key, "labels.postal.sizeByZone");
        var classes = new Dictionary<RoadClass, StreetClassStyle>();
        foreach (var kv in r.Obj(street, "classes"))
        {
            RoadClass c;
            try { c = RoadClasses.Parse(kv.Key); }
            catch (FormatException) { r.Fail($"labels.street.classes: unknown road class '{kv.Key}'"); continue; }
            if (kv.Value is not JsonObject co) { r.Fail($"labels.street.classes.{kv.Key} must be an object"); continue; }
            classes[c] = new StreetClassStyle(Positive(r, co, "size", "labels.street.classes." + kv.Key), Weight(r, co, "weight"), Positive(r, co, "repeat", kv.Key),
                r.Num(co, "sameNameGap"), r.Num(co, "maxGlyphTurn"), r.Num(co, "letterSpacing"));
        }
        var outline = zone["outline"] is JsonValue ov && ov.TryGetValue<string>(out var os) ? r.ParseColor(os, "labels.zone.outline") : (Rgb?)null;
        return new LabelsStyle
        {
            Fonts = fonts,
            Clearance = r.Num(o, "clearance"),
            MinPixels = r.Num(o, "minPixels"),
            PostalColor = r.Color(postal, "color"),
            PostalSize = Positive(r, postal, "size", "labels.postal"),
            PostalSizeByZone = sizes,
            ZoneColor = r.Color(zone, "color"),
            ZoneSize = Positive(r, zone, "size", "labels.zone"),
            ZoneSpacing = r.Num(zone, "spacing"),
            ZoneWeight = Weight(r, zone, "weight"),
            ZoneOutline = outline,
            ZoneOutlineWidth = zone["outlineWidth"] is null ? 0 : r.Num(zone, "outlineWidth"),
            ZoneMinArea = r.Num(zone, "minArea"),
            StreetColor = r.Color(street, "color"),
            StreetClasses = classes,
            RouteNumbers = street["routeNumbers"] is JsonValue rv && rv.TryGetValue<bool>(out var rn) && rn,
            Expand = street["expand"] is JsonObject ? r.Texts(street, "expand") : [],
            PlacementKey = KeyOf(o),
        };
    }

    static double Positive(MapStyle.Reader r, JsonObject o, string key, string where)
    {
        double v = r.Num(o, key);
        if (!(v > 0)) r.Fail($"{where}.{key} must be above 0");
        return v;
    }

    static string Weight(MapStyle.Reader r, JsonObject o, string key)
    {
        var w = r.Str(o, key);
        if (w is not ("normal" or "bold")) r.Fail($"labels: weight '{w}' (normal or bold)");
        return w;
    }

    static string KeyOf(JsonObject o)
    {
        var copy = o.DeepClone();
        Strip(copy);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(copy.ToJsonString()));
        return Convert.ToHexStringLower(bytes)[..12];

        static void Strip(JsonNode? n)
        {
            if (n is JsonObject obj)
            {
                foreach (var k in DrawingOnly) obj.Remove(k);
                foreach (var kv in obj.ToList()) Strip(kv.Value);
            }
            else if (n is JsonArray a)
                foreach (var x in a) Strip(x);
        }
    }
}
