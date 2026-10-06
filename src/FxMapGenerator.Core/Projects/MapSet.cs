namespace FxMapGenerator.Core.Projects;

public enum MapKind { Satellite, Atlas, Roadmap }

/// <summary>
/// One map to build, also the name of its tile set: <c>satellite</c>, <c>roadmap</c> or <c>atlas-&lt;preset&gt;-&lt;language&gt;</c>
/// (e.g. <c>atlas-postalcodemap-en</c>). An atlas map is one style (preset) in one language.
/// </summary>
public sealed record MapSet(MapKind Kind, string? Preset = null, string? Language = null)
{
    public static readonly MapSet Satellite = new(MapKind.Satellite);
    public static readonly MapSet Roadmap = new(MapKind.Roadmap);

    public static MapSet Atlas(string preset, string language) => new(MapKind.Atlas, preset, language);

    public string Id => Kind switch
    {
        MapKind.Satellite => "satellite",
        MapKind.Roadmap => "roadmap",
        _ => $"atlas-{Preset}-{Language}",
    };

    /// <summary>
    /// The map's folder in an export (<c>tiles/&lt;name&gt;/</c>, the viewer's and lb-phone's name of it): its id, but an
    /// English atlas map without its language (<c>atlas-postalcodemap</c>; the Japanese one <c>atlas-postalcodemap-ja</c>).
    /// </summary>
    public string ExportName => Kind == MapKind.Atlas && Language == "en" ? $"atlas-{Preset}" : Id;

    /// <summary>
    /// A name for people in exported files (English): an atlas map by its style's name (a bundled style's English name, a
    /// project's style's name as written), with its language when it is not English.
    /// </summary>
    public string Title(Project project) => Kind switch
    {
        MapKind.Satellite => "Satellite map",
        MapKind.Roadmap => "Road map",
        _ => Language == "en"
            ? $"Atlas map ({project.StyleOf(this).Name.In("en")})"
            : $"Atlas map ({project.StyleOf(this).Name.In("en")}, {AtlasPresets.LanguageName(Language!)})",
    };

    /// <summary>Drawn per cell (atlas and road maps); the satellite map is cut per block from the captures.</summary>
    public bool IsCellMap => Kind != MapKind.Satellite;

    /// <summary>Whether the map needs the canopy scan (only atlas presets that draw tree canopy).</summary>
    public bool NeedsCanopy => Kind == MapKind.Atlas && AtlasPresets.DrawsCanopy(Preset!);

    public override string ToString() => Id;

    public static bool TryParse(string? id, out MapSet set)
    {
        set = Satellite;
        switch (id)
        {
            case "satellite": return true;
            case "roadmap": set = Roadmap; return true;
        }
        var parts = id?.Split('-');
        if (parts is not { Length: 3 } || parts[0] != "atlas" || !AtlasPresets.IsKnown(parts[1]) || !AtlasPresets.Languages.Contains(parts[2])) return false;
        set = Atlas(parts[1], parts[2]);
        return true;
    }

    public static MapSet Parse(string id) => TryParse(id, out var s) ? s : throw new FormatException($"unknown map '{id}'");
}

/// <summary>
/// The atlas styles bundled with this version, by id (no dashes: the id is part of the map id). A project's own styles
/// (<see cref="Styles.ProjectStyles"/>) are made from them.
/// </summary>
public static class AtlasPresets
{
    /// <summary>The default: a colour per ground material (natural grounds blended), in the look of the postal code map.</summary>
    public const string PostalCodeMap = "postalcodemap";
    /// <summary>Region colours with the ground materials as light shading.</summary>
    public const string Regional = "regional";
    public static readonly IReadOnlyList<string> BuiltIn = new[] { PostalCodeMap, Regional };
    public static readonly IReadOnlyList<string> Languages = new[] { "en", "ja" };

    /// <summary>Whether a preset can be an atlas map's: a bundled atlas style, or the id of a project's own style (whether the project has it is known when its maps are made).</summary>
    public static bool IsKnown(string preset) => BuiltIn.Contains(preset) || Styles.UserStyleFile.IdProblem(preset) is null;

    /// <summary>The language's name in English, for exported files.</summary>
    public static string LanguageName(string language) => language switch
    {
        "en" => "English",
        "ja" => "Japanese",
        _ => language,
    };

    /// <summary>Neither built-in preset draws tree canopy.</summary>
    public static bool DrawsCanopy(string preset) => false;
}
