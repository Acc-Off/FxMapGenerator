using System.Text.Json;

namespace FxMapGenerator.Core.GameFiles;

/// <summary>
/// <c>game/names.json</c> as the labels read it: the names of the streets (by street hash) and of the zones (by code),
/// each per language.
/// </summary>
public sealed class GameNames
{
    public required IReadOnlyDictionary<uint, IReadOnlyDictionary<string, string>> Streets { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Zones { get; init; }

    /// <summary>The name in a language, else the English one; null when neither is there (or both are empty).</summary>
    public static string? In(IReadOnlyDictionary<string, string>? names, string language)
    {
        if (names is null) return null;
        if (names.TryGetValue(language, out var n) && n.Length > 0) return n;
        return names.TryGetValue("en", out var en) && en.Length > 0 ? en : null;
    }

    public string? Street(uint hash, string language) => In(Streets.GetValueOrDefault(hash), language);

    public string? Zone(string code, string language) => In(Zones.GetValueOrDefault(code), language);

    /// <summary>These names with street names of the road edits added (English, and Japanese when there is one).</summary>
    public GameNames WithStreets(IEnumerable<(uint Hash, string En, string? Ja)> streets)
    {
        var all = new Dictionary<uint, IReadOnlyDictionary<string, string>>(Streets);
        foreach (var (h, en, ja) in streets)
            all[h] = ja is { Length: > 0 } j ? new Dictionary<string, string> { ["en"] = en, ["ja"] = j } : new Dictionary<string, string> { ["en"] = en };
        return new GameNames { Streets = all, Zones = Zones };
    }

    public static GameNames Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var streets = new Dictionary<uint, IReadOnlyDictionary<string, string>>();
        var zones = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("streets", out var s))
            foreach (var p in s.EnumerateObject())
                if (uint.TryParse(p.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var h)) streets[h] = Names(p.Value);
        if (doc.RootElement.TryGetProperty("zones", out var z))
            foreach (var p in z.EnumerateObject()) zones[p.Name] = Names(p.Value);
        return new GameNames { Streets = streets, Zones = zones };

        static IReadOnlyDictionary<string, string> Names(JsonElement e) =>
            e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }
}
