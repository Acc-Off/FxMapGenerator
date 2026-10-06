using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Labels;

/// <summary>A postal code: its text, where it is written (its centre) and its size (em, m; null = the style's size for its zone).</summary>
public sealed record PostalCode(string Code, double X, double Y, double? Size);

/// <summary>
/// Postal code files in the nearest-postal form (<c>[{"code": "1000", "x": 1645.4, "y": 6453.96}, ...]</c>, the form
/// servers use with nearest-postal); an item may add <c>"size"</c> (em, m). The list may also stand in an object with the
/// credit the exported maps carry: <c>{"credit": "...", "codes": [...]}</c>.
/// </summary>
public static class PostalCodes
{
    /// <summary>The default source: nearest-postal's table for the postal code map (MIT).</summary>
    public const string DefaultUrl = "https://raw.githubusercontent.com/DevBlocky/nearest-postal/master/new-postals.json";

    /// <summary>The credit of the default source (its table carries none of its own).</summary>
    public const string DefaultCredit = "Postal codes: nearest-postal's table for the postal code map, https://github.com/DevBlocky/nearest-postal (MIT License).";

    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The copy in the work folder's <c>data/</c> the labels read.</summary>
    public const string FileName = "postals.json";

    /// <exception cref="LabelsException">Not a postal code list; every problem is named.</exception>
    public static List<PostalCode> Parse(string json, string where)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: Lenient); }
        catch (JsonException ex) { throw new LabelsException($"{where}: not JSON ({ex.Message})"); }
        if (root is JsonObject wrapped)
        {
            if (wrapped["credit"] is { } c && !(c is JsonValue cv0 && cv0.TryGetValue<string>(out _))) throw new LabelsException($"{where}: 'credit' must be text");
            root = wrapped["codes"];
        }
        if (root is not JsonArray a) throw new LabelsException($"{where}: expected a list of postal codes ([{{\"code\", \"x\", \"y\"}}, ...] or {{\"credit\", \"codes\": [...]}})");
        var o = new List<PostalCode>();
        var problems = new List<string>();
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] is not JsonObject it) { problems.Add($"item {i + 1} is not an object"); continue; }
            string? code = it["code"] is JsonValue cv && cv.TryGetValue<string>(out var cs) && cs.Length > 0 ? cs : null;
            double? x = Num(it["x"]), y = Num(it["y"]);
            double? size = it["size"] is null ? null : Num(it["size"]);
            if (code is null) problems.Add($"item {i + 1} has no code");
            else if (x is null || y is null) problems.Add($"'{code}' has no position");
            else if (it["size"] is not null && !(size > 0)) problems.Add($"'{code}': the size must be above 0");
            else if (!MapFrame.Outer.Contains(x.Value, y.Value)) problems.Add($"'{code}' is off the map");
            else o.Add(new PostalCode(code, x.Value, y.Value, size));
            if (problems.Count >= 10) break;
        }
        if (problems.Count > 0) throw new LabelsException($"{where}: " + string.Join("; ", problems));
        return o;

        static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
    }

    /// <summary>The credit of a postal code list from <paramref name="source"/>: the default source's, else the file's own <c>credit</c>, else null.</summary>
    public static string? Credit(string json, string source)
    {
        if (source == DefaultUrl) return DefaultCredit;
        try { return JsonNode.Parse(json, documentOptions: Lenient) is JsonObject o && o["credit"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null; }
        catch (JsonException) { return null; }
    }
}

/// <summary>A numbered route: the streets it runs on (English names) and the label text (<c>{name}</c> = the street name).</summary>
public sealed record Route(string Number, IReadOnlyList<string> Streets, string Text);

/// <summary>
/// The route numbers written into street names (<c>data/routes.json</c>, bundled): the numbered highways of GTA V as
/// GTA Wiki lists them, by the game's English street names; the file's <c>credit</c> goes into the exported maps that
/// carry a route number.
/// </summary>
public static class Routes
{
    static string BundledText()
    {
        using var s = EmbeddedData.Open("routes.json");
        return new StreamReader(s).ReadToEnd();
    }

    static readonly Lazy<IReadOnlyList<Route>> BundledRoutes = new(() => Parse(BundledText(), "data/routes.json"));

    static readonly Lazy<string?> BundledCredit = new(() => JsonNode.Parse(BundledText())!["credit"]?.GetValue<string>());

    public static IReadOnlyList<Route> Bundled => BundledRoutes.Value;

    /// <summary>The credit of the bundled route numbers.</summary>
    public static string? Credit => BundledCredit.Value;

    public static IReadOnlyList<Route> Parse(string json, string where)
    {
        try
        {
            var root = JsonNode.Parse(json)!.AsObject();
            return root["routes"]!.AsArray().Select(n =>
            {
                var r = n!.AsObject();
                return new Route(r["number"]!.GetValue<string>(), r["streets"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(), r["text"]!.GetValue<string>());
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException)
        {
            throw new LabelsException($"{where}: not a routes file ({ex.Message})");
        }
    }

    /// <summary>The label text of a street on a route: the route's text with the number and the name filled in.</summary>
    public static string Format(Route r, string name) => r.Text.Replace("{n}", r.Number, StringComparison.Ordinal).Replace("{name}", name, StringComparison.Ordinal);
}

public sealed class LabelsException(string message) : Exception(message);
