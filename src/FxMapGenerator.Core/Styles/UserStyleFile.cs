using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FxMapGenerator.Core.Projects;

namespace FxMapGenerator.Core.Styles;

/// <summary>
/// A user style: a bundled atlas style (<see cref="Base"/>) with some of its values changed (<see cref="Changes"/>, at the
/// same places as in a style file; see <see cref="StyleChanges"/>), its own id and name (one text in the user's words:
/// the exported maps drawn with it are named with it as written). When the app's bundled style changes, the values the
/// user style did not change follow it.
/// </summary>
public sealed record UserStyle(string Id, string Name, string Base, JsonObject Changes)
{
    /// <summary>The style's values: its base's with the changes over them, its own id and name.</summary>
    public JsonObject Values()
    {
        var o = StyleChanges.Apply(MapStyle.Builtin(Base).Source, Changes);
        o["id"] = Id;
        o["name"] = Name;
        return o;
    }

    /// <summary>The style as the maps would read it.</summary>
    public MapStyle Style() => MapStyle.Parse(Values().ToJsonString(), $"style '{Id}'");
}

/// <summary>
/// The file of a user style (<c>styles/&lt;id&gt;.json</c>): <c>format</c>, <c>id</c>, <c>name</c> (a text),
/// <c>base</c> (the bundled style it was made from), then only the values changed from the base, at the same places as in
/// a style file (null takes a key away). Reading checks the whole style it makes and names every problem.
/// </summary>
public static partial class UserStyleFile
{
    public const int CurrentFormat = 1;
    /// <summary>The longest id (the id is part of map ids and folder names).</summary>
    public const int MaxIdLength = 32;
    /// <summary>The file's own keys; everything else in it is a change of the base.</summary>
    public static readonly IReadOnlyList<string> OwnKeys = ["format", "id", "name", "base"];

    static readonly JsonSerializerOptions Text = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    [GeneratedRegex("^[a-z][a-z0-9]*$")]
    private static partial Regex IdPattern();

    /// <summary>What is wrong with the form of an id (null: fine): small letters and digits, a letter first, at most <see cref="MaxIdLength"/>.</summary>
    public static string? IdFormProblem(string id)
    {
        if (id.Length == 0) return "the id is empty";
        if (id.Length > MaxIdLength) return $"the id is longer than {MaxIdLength} characters";
        if (!IdPattern().IsMatch(id)) return $"'{id}': only small letters (a-z) and digits, a letter first";
        return null;
    }

    /// <summary>What is wrong with an id as a user style's (null: fine): its form, or a bundled style's id.</summary>
    public static string? IdProblem(string id) =>
        IdFormProblem(id) ?? (MapStyle.BuiltIn.Contains(id) ? $"'{id}' is a bundled style's id" : null);

    /// <summary>Reads a user style file; <see cref="StyleException"/> names every problem.</summary>
    public static UserStyle Read(string path) => Parse(File.ReadAllBytes(path), path);

    /// <summary>Reads a user style file's contents; <see cref="StyleException"/> names every problem.</summary>
    public static UserStyle Parse(ReadOnlySpan<byte> json, string where)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json, documentOptions: Lenient)?.AsObject() ?? throw new StyleException($"{where}: empty"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new StyleException($"{where}: not a JSON object ({ex.Message})"); }
        return FromObject(root, where);
    }

    static UserStyle FromObject(JsonObject root, string where)
    {
        var problems = new List<string>();
        if (root["format"] is not JsonValue fv || !fv.TryGetValue<int>(out int format)) problems.Add("'format' must be a number");
        else if (format > CurrentFormat) problems.Add($"format {format} is newer than this app reads ({CurrentFormat})");
        string id = root["id"] is JsonValue iv && iv.TryGetValue<string>(out var s) ? s : "";
        if (IdProblem(id) is { } idProblem) problems.Add(idProblem);
        string name = root["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n.Trim() : "";
        if (name.Length == 0) problems.Add("'name' must be a text (the style's name)");
        string baseId = root["base"] is JsonValue bv && bv.TryGetValue<string>(out var b) ? b : "";
        if (!AtlasPresets.BuiltIn.Contains(baseId)) problems.Add($"'base' must be a bundled atlas style ({string.Join(", ", AtlasPresets.BuiltIn)}), not '{baseId}'");
        var changes = new JsonObject();
        foreach (var (key, value) in root)
            if (!OwnKeys.Contains(key)) changes[key] = value?.DeepClone();
        if (problems.Count > 0) throw new StyleException($"{where}: " + string.Join("; ", problems));
        var style = new UserStyle(id, name, baseId, changes);
        Check(style, where);
        return style;
    }

    /// <summary>
    /// A style file of either form: a user style's (with <c>base</c>), or a whole style (such as a bundled style's file),
    /// which becomes a user style of the bundled style of its kind of ground (<c>groundRaster.mode</c>) with the values
    /// it differs in, named with its name in <paramref name="language"/> (a whole style's name may be in English and
    /// Japanese, as the bundled styles have it). <see cref="StyleException"/> names every problem.
    /// </summary>
    public static UserStyle ParseAny(ReadOnlySpan<byte> json, string where, string language = "en")
    {
        JsonObject root;
        try { root = JsonNode.Parse(json, documentOptions: Lenient)?.AsObject() ?? throw new StyleException($"{where}: empty"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new StyleException($"{where}: not a JSON object ({ex.Message})"); }
        if (root.ContainsKey("base")) return FromObject(root, where);
        MapStyle whole;
        try { whole = MapStyle.Parse(root.ToJsonString(), where); }
        catch (StyleException ex) { throw new StyleException(ex.Message); }
        var mode = whole.GroundRaster?.Mode;
        var baseId = AtlasPresets.BuiltIn.FirstOrDefault(id => MapStyle.Builtin(id).GroundRaster?.Mode == mode)
            ?? throw new StyleException($"{where}: not an atlas style (no ground picture of a kind the bundled atlas styles have)");
        var file = new JsonObject
        {
            ["format"] = CurrentFormat,
            ["id"] = whole.Id,
            ["name"] = whole.Name.In(language) is { Length: > 0 } named ? named : whole.Id,
            ["base"] = baseId,
        };
        foreach (var (key, value) in StyleChanges.Between(MapStyle.Builtin(baseId).Source, root, ["format", "id", "name"]))
            file[key] = value?.DeepClone();
        return FromObject(file, where);
    }

    /// <summary>Checks the style a user style makes: the ground's kind stays its base's, and the maps can read it.</summary>
    public static void Check(UserStyle style, string where)
    {
        var problems = new List<string>();
        var values = StyleChanges.Apply(MapStyle.Builtin(style.Base).Source, style.Changes);
        var mode = StyleChanges.At(values, "groundRaster.mode")?.ToString();
        if (mode != MapStyle.Builtin(style.Base).GroundRaster?.Mode) problems.Add($"groundRaster.mode must stay '{MapStyle.Builtin(style.Base).GroundRaster?.Mode}' (the kind of ground of the base style)");
        try { style.Style(); }
        catch (StyleException ex) { problems.Add(ex.Message); }
        if (problems.Count > 0) throw new StyleException($"{where}: " + string.Join("; ", problems));
    }

    /// <summary>The file's text: its own keys, then the changes in the order of the base style's keys (UTF-8, LF, two spaces).</summary>
    public static string Format(UserStyle style)
    {
        var o = new JsonObject
        {
            ["format"] = CurrentFormat,
            ["id"] = style.Id,
            ["name"] = style.Name,
            ["base"] = style.Base,
        };
        foreach (var (key, value) in Ordered(style.Changes, MapStyle.Builtin(style.Base).Source, ""))
            o[key] = value?.DeepClone();
        return o.ToJsonString(Text) + "\n";
    }

    /// <summary>
    /// A copy of <paramref name="changes"/> with its keys in the order <paramref name="like"/> has them (others after); an
    /// object whose order counts (<see cref="StyleChanges.Whole"/>) keeps its own.
    /// </summary>
    static JsonObject Ordered(JsonObject changes, JsonObject? like, string path)
    {
        if (StyleChanges.Whole.Contains(path)) return changes.DeepClone().AsObject();
        var o = new JsonObject();
        var keys = (like?.Select(kv => kv.Key).Where(changes.ContainsKey) ?? []).Concat(changes.Select(kv => kv.Key)).Distinct().ToList();
        foreach (var key in keys)
        {
            var v = changes[key];
            var at = path.Length == 0 ? key : path + "." + key;
            o[key] = v is JsonObject vo ? Ordered(vo, like?[key] as JsonObject, at) : v?.DeepClone();
        }
        return o;
    }

    /// <summary>Writes the file through a temporary one.</summary>
    public static void Write(string path, UserStyle style)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Format(style), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
