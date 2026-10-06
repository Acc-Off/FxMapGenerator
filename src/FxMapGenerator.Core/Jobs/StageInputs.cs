namespace FxMapGenerator.Core.Jobs;

/// <summary>
/// Names of the project inputs a stage reads while it runs. Until the stage is over, the input cannot be edited; the
/// run itself reads the copy taken at its start (<c>logs/run-&lt;time&gt;/inputs/</c>), so other edits never reach it.
/// Keys nest with a dot: <c>style</c> covers <c>style.regions</c> and <c>style.labels</c>.
/// </summary>
public static class InputKeys
{
    public const string Range = "range";
    /// <summary>The map's frame (the cells added around the standard map): the range's blocks stay as they are when it changes.</summary>
    public const string Frame = "frame";
    public const string Maps = "maps";
    public const string Server = "server";
    public const string Console = "console";
    public const string GameFiles = "gameFiles";
    /// <summary>Whether the project reads Cayo Perico's roads: the game files take the island's road files with it, the labels the name of the island's zone.</summary>
    public const string CayoPerico = "cayoPerico";
    public const string Postals = "postals";
    public const string Languages = "languages";
    public const string Style = "style";
    public const string StyleRegions = "style.regions";
    public const string StyleLabels = "style.labels";
    public const string Poi = "poi";
    public const string RoadEdits = "roadEdits";
    public const string Minimap = "minimap";

    /// <summary>True when a stage reading <paramref name="held"/> blocks an edit of <paramref name="edited"/> (same key, or one contains the other).</summary>
    public static bool Overlap(string held, string edited) =>
        held == edited || edited.StartsWith(held + ".", StringComparison.Ordinal) || held.StartsWith(edited + ".", StringComparison.Ordinal);
}

/// <summary>What each stage (by its to-do row id) reads while it runs.</summary>
public static class StageInputs
{
    static readonly Dictionary<string, string[]> Table = new()
    {
        ["visit"] = [InputKeys.Range, InputKeys.Maps, InputKeys.Server, InputKeys.Console],
        ["ortho"] = [InputKeys.Range],
        ["gameFiles"] = [InputKeys.GameFiles, InputKeys.Frame, InputKeys.CayoPerico],
        ["mapData.roadGraph"] = [InputKeys.Range, InputKeys.RoadEdits],
        ["mapData.landcover"] = [InputKeys.Range],
        ["mapData.regions"] = [InputKeys.Range, InputKeys.StyleRegions, InputKeys.Frame],
        ["mapData.labels"] = [InputKeys.Postals, InputKeys.Languages, InputKeys.StyleLabels, InputKeys.Poi, InputKeys.RoadEdits, InputKeys.CayoPerico],
        ["mapData.roads"] = [InputKeys.Range, InputKeys.RoadEdits, InputKeys.Frame],
        ["cells"] = [InputKeys.Style, InputKeys.Poi],
        ["lowZoom.satellite"] = [InputKeys.Range, InputKeys.Frame],
        ["lowZoom"] = [InputKeys.Style, InputKeys.Poi, InputKeys.Frame],
        ["ytd"] = [InputKeys.Minimap],
    };

    /// <summary>The inputs of a row: its own entry, else its parent's (<c>cells.atlas-c-en</c> -> <c>cells</c>).</summary>
    public static IReadOnlyList<string> For(string row)
    {
        for (var id = row; ; id = id[..id.LastIndexOf('.')])
        {
            if (Table.TryGetValue(id, out var keys)) return keys;
            if (!id.Contains('.')) return Array.Empty<string>();
        }
    }
}
