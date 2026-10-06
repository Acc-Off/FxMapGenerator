using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Projects;

namespace FxMapGenerator.Core.Poi;

/// <summary>
/// A POI folder as the screen edits it: its path in the POI folder ('/'; "" for the POI folder itself) and the settings
/// of its <c>_.json</c> (a null name: the folder's own name).
/// </summary>
public sealed record PoiEditFolder(string Path, ItemName? Name = null, int Order = 0, string? Style = null, PoiShow? Show = null,
    bool Visible = true, bool Locked = false);

/// <summary>A group of points as the screen edits it: its path in the POI folder without <c>.json</c>, its settings (a null name: the file's own) and its points.</summary>
public sealed record PoiEditGroup(string Path, List<PoiEditPoint> Points, ItemName? Name = null, int Order = 0, string? Style = null,
    PoiShow? Show = null, bool Visible = true, bool Locked = false, string? Credit = null);

/// <summary>
/// A point as the screen edits it: its name (the lists'; empty: none), its label per language (the maps' text; empty:
/// none), its place, and its own values (null: taken from its group and folders, or the style's).
/// </summary>
public sealed record PoiEditPoint(string Id, string Name, Dictionary<string, string> Label, double X, double Y, string? Style = null, PoiShow? Show = null,
    string? Color = null, double? Size = null, bool? Visible = null, bool? Locked = null);

/// <summary>A POI style as the screen edits it (the fields of <c>poi-styles.json</c>).</summary>
public sealed record PoiEditStyle(string Id, ItemName Name, string Look, string Color, double Size, string Weight = "normal",
    string? Outline = null, double OutlineWidth = 0, string? BadgeColor = null, bool ShowLabel = false, string? Icon = null, string? Image = null,
    double? LabelSize = null);

/// <summary>What the POI screen edits: the folders, the groups with their points, and the project's own POI styles.</summary>
public sealed record PoiEditSet(List<PoiEditFolder> Folders, List<PoiEditGroup> Groups, List<PoiEditStyle> Styles);

/// <summary>
/// The files of a project's points of interest as the screen edits them: read into a <see cref="PoiEditSet"/>, written
/// back file by file. A file is written only when what it says changed (a file written by hand keeps its comments until
/// the screen changes it); a group file the set no longer has is deleted, and folders left without files go with it.
/// </summary>
public static partial class PoiFiles
{
    static readonly JsonDocumentOptions Tolerant = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions Text = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The name of a new project's POI folder beside the project file (<c>poi-2</c>, ... when taken).</summary>
    public const string FolderName = "poi";
    /// <summary>The name of a new project's POI styles file beside the project file.</summary>
    public const string StylesName = "poi-styles.json";
    /// <summary>The folder beside the POI styles file the screen copies PNG icons into.</summary>
    public const string ImagesFolder = "poi-icons";

    /// <summary>
    /// The PNG icons a POI styles file names (<c>image</c>), as paths in its folder ('/'); names that would leave the folder
    /// and a file that cannot be read give none (reading the styles names those).
    /// </summary>
    public static IReadOnlyList<string> ImagesOf(string stylesJson)
    {
        try
        {
            if (JsonNode.Parse(stylesJson, documentOptions: Tolerant)?["styles"] is not JsonArray a) return [];
            return a.OfType<JsonObject>()
                .Select(s => s["image"] is JsonValue v && v.TryGetValue<string>(out var i) ? i.Replace('\\', '/') : null)
                .Where(i => i is not null && SafeRelative(i))
                .Select(i => i!).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>True for a relative path ('/') that stays in its folder.</summary>
    public static bool SafeRelative(string path) => !Path.IsPathRooted(path) && path.Split('/').All(p => p is not ("" or "." or ".."));

    // ------------------------------------------------------------------------------------------------ reading

    /// <summary>The POI folder's files (by path, '/') and the project's POI styles file as the screen edits them; the files must read (see <see cref="PoiData.Load"/>).</summary>
    public static PoiEditSet Read(IEnumerable<(string Path, string Text)> files, string? stylesJson)
    {
        var folders = new SortedDictionary<string, PoiEditFolder>(StringComparer.Ordinal) { [""] = new PoiEditFolder("") };
        var groups = new List<PoiEditGroup>();
        foreach (var (path, text) in files)
        {
            int slash = path.LastIndexOf('/');
            string folder = slash < 0 ? "" : path[..slash], name = path[(slash + 1)..];
            for (var f = folder; f.Length > 0; f = f.LastIndexOf('/') is var i and >= 0 ? f[..i] : "")
                folders.TryAdd(f, new PoiEditFolder(f));
            var o = Parse(text);
            if (name == PoiData.FolderFile) folders[folder] = ReadFolder(folder, o);
            else groups.Add(ReadGroup(path[..^".json".Length], o));
        }
        var styles = stylesJson is null ? [] : ReadStyles(stylesJson);
        return new PoiEditSet(folders.Values.ToList(), groups.OrderBy(g => g.Path, StringComparer.Ordinal).ToList(), styles);
    }

    /// <summary>
    /// The set as a copy of it keeps it: the names in English and Japanese (the bundled ones') become their name in
    /// <paramref name="language"/>, the screens' language when the bundled points are copied into the project.
    /// </summary>
    public static PoiEditSet Copied(PoiEditSet set, string language) => new(
        set.Folders.Select(f => f with { Name = f.Name?.Copied(language) }).ToList(),
        set.Groups.Select(g => g with { Name = g.Name?.Copied(language) }).ToList(),
        set.Styles.Select(s => s with { Name = s.Name.Copied(language) }).ToList());

    /// <summary>The POI styles of a styles file (the bundled one or a project's).</summary>
    public static List<PoiEditStyle> ReadStyles(string stylesJson) =>
        (Parse(stylesJson)["styles"] as JsonArray ?? []).OfType<JsonObject>().Select(s => new PoiEditStyle(
            Str(s, "id") ?? "", ItemName.FromNode(s["name"]) ?? ItemName.Of(""), Str(s, "look") ?? "", Str(s, "color") ?? "#000000", Num(s, "size") ?? 0,
            Str(s, "weight") ?? "normal", Str(s, "outline"), Num(s, "outlineWidth") ?? 0, Str(s, "badgeColor"), Bool(s, "showLabel") ?? false,
            Str(s, "icon"), Str(s, "image"), Num(s, "labelSize"))).ToList();

    static JsonObject Parse(string text)
    {
        try { return JsonNode.Parse(text, documentOptions: Tolerant) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    static PoiEditFolder ReadFolder(string path, JsonObject o) =>
        new(path, ItemName.FromNode(o["name"]), (int)(Num(o, "order") ?? 0), Str(o, "style"), Show(o), Bool(o, "visible") ?? true, Bool(o, "locked") ?? false);

    static PoiEditGroup ReadGroup(string path, JsonObject o) =>
        new(path, (o["points"] as JsonArray ?? []).OfType<JsonObject>().Select(p => new PoiEditPoint(
                Str(p, "id") ?? "", Str(p, "name") ?? "", Labels(p), Num(p, "x") ?? 0, Num(p, "y") ?? 0, Str(p, "style"), Show(p),
                Str(p, "color"), Num(p, "size"), Bool(p, "visible"), Bool(p, "locked"))).ToList(),
            ItemName.FromNode(o["name"]), (int)(Num(o, "order") ?? 0), Str(o, "style"), Show(o), Bool(o, "visible") ?? true, Bool(o, "locked") ?? false, Str(o, "credit"));

    static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double? Num(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    static bool? Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>A point's label per language (none: empty).</summary>
    static Dictionary<string, string> Labels(JsonObject o) =>
        o["label"] is JsonObject n
            ? n.Where(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out _)).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    static PoiShow? Show(JsonObject o) =>
        o["show"] is JsonObject s ? new PoiShow(Bool(s, "atlas") ?? PoiShow.Default.Atlas, Bool(s, "roadmap") ?? PoiShow.Default.Roadmap) : null;

    // ------------------------------------------------------------------------------------------------ writing

    static string J(string s) => JsonSerializer.Serialize(s, Text);
    static string J(double d) => JsonSerializer.Serialize(d);
    static string J(bool b) => b ? "true" : "false";

    /// <summary>A name: a text, or a bundled name's <c>{ "en": ..., "ja": ... }</c>.</summary>
    static string NameText(ItemName n) =>
        n.Text is { } text ? J(text) : "{ \"en\": " + J(n.En ?? "") + (string.IsNullOrEmpty(n.Ja) ? "" : ", \"ja\": " + J(n.Ja)) + " }";

    /// <summary>A label: its texts, English first (empty ones left out).</summary>
    static string LabelText(IReadOnlyDictionary<string, string> n) =>
        "{ " + string.Join(", ", n.Where(kv => kv.Value.Length > 0).OrderBy(kv => kv.Key == "en" ? 0 : kv.Key == "ja" ? 1 : 2).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{J(kv.Key)}: {J(kv.Value)}")) + " }";

    static string ShowText(PoiShow s) => $"{{ \"atlas\": {J(s.Atlas)}, \"roadmap\": {J(s.Roadmap)} }}";

    /// <summary>The settings a folder's <c>_.json</c> and a group's file share, as lines (the defaults left out).</summary>
    static List<string> SettingLines(ItemName? name, string? credit, int order, string? style, PoiShow? show, bool visible, bool locked)
    {
        var o = new List<string> { "\"format\": 1" };
        if (name is not null) o.Add($"\"name\": {NameText(name)}");
        if (credit is not null) o.Add($"\"credit\": {J(credit)}");
        if (order != 0) o.Add($"\"order\": {order}");
        if (style is not null) o.Add($"\"style\": {J(style)}");
        if (show is not null) o.Add($"\"show\": {ShowText(show)}");
        if (!visible) o.Add("\"visible\": false");
        if (locked) o.Add("\"locked\": true");
        return o;
    }

    /// <summary>A folder's <c>_.json</c>.</summary>
    public static string FolderText(PoiEditFolder f) =>
        "{\n" + string.Join(",\n", SettingLines(f.Name, null, f.Order, f.Style, f.Show, f.Visible, f.Locked).Select(l => "  " + l)) + "\n}\n";

    /// <summary>A group's file: its settings, then its points one a line.</summary>
    public static string GroupText(PoiEditGroup g)
    {
        var lines = SettingLines(g.Name, g.Credit, g.Order, g.Style, g.Show, g.Visible, g.Locked);
        var sb = new StringBuilder("{\n");
        foreach (var l in lines) sb.Append("  ").Append(l).Append(",\n");
        if (g.Points.Count == 0) return sb.Append("  \"points\": []\n}\n").ToString();
        sb.Append("  \"points\": [\n");
        for (int i = 0; i < g.Points.Count; i++)
        {
            var p = g.Points[i];
            var parts = new List<string> { $"\"id\": {J(p.Id)}" };
            if (p.Name.Length > 0) parts.Add($"\"name\": {J(p.Name)}");
            if (p.Label.Values.Any(v => v.Length > 0)) parts.Add($"\"label\": {LabelText(p.Label)}");
            parts.Add($"\"x\": {J(p.X)}");
            parts.Add($"\"y\": {J(p.Y)}");
            if (p.Style is not null) parts.Add($"\"style\": {J(p.Style)}");
            if (p.Show is not null) parts.Add($"\"show\": {ShowText(p.Show)}");
            if (p.Color is not null) parts.Add($"\"color\": {J(p.Color)}");
            if (p.Size is { } size) parts.Add($"\"size\": {J(size)}");
            if (p.Visible is { } v) parts.Add($"\"visible\": {J(v)}");
            if (p.Locked is { } k) parts.Add($"\"locked\": {J(k)}");
            sb.Append("    { ").Append(string.Join(", ", parts)).Append(i + 1 < g.Points.Count ? " },\n" : " }\n");
        }
        return sb.Append("  ]\n}\n").ToString();
    }

    /// <summary>A POI styles file: its styles one a line.</summary>
    public static string StylesText(IReadOnlyList<PoiEditStyle> styles)
    {
        var sb = new StringBuilder("{\n  \"format\": 1,\n");
        if (styles.Count == 0) return sb.Append("  \"styles\": []\n}\n").ToString();
        sb.Append("  \"styles\": [\n");
        for (int i = 0; i < styles.Count; i++)
        {
            var s = styles[i];
            var parts = new List<string> { $"\"id\": {J(s.Id)}", $"\"name\": {NameText(s.Name)}", $"\"look\": {J(s.Look)}" };
            if (s.Icon is not null) parts.Add($"\"icon\": {J(s.Icon)}");
            if (s.Image is not null) parts.Add($"\"image\": {J(s.Image)}");
            parts.Add($"\"color\": {J(s.Color)}");
            parts.Add($"\"size\": {J(s.Size)}");
            if (s.Weight != "normal") parts.Add($"\"weight\": {J(s.Weight)}");
            if (s.Outline is not null) parts.Add($"\"outline\": {J(s.Outline)}, \"outlineWidth\": {J(s.OutlineWidth)}");
            if (s.BadgeColor is not null) parts.Add($"\"badgeColor\": {J(s.BadgeColor)}");
            if (s.ShowLabel) parts.Add("\"showLabel\": true");
            if (s.LabelSize is { } ls) parts.Add($"\"labelSize\": {J(ls)}");
            sb.Append("    { ").Append(string.Join(", ", parts)).Append(i + 1 < styles.Count ? " },\n" : " }\n");
        }
        return sb.Append("  ]\n}\n").ToString();
    }

    /// <summary>The same file as the app writes it (to tell whether a file on disk says what the set says).</summary>
    static string Canonical(string relative, string text)
    {
        int slash = relative.LastIndexOf('/');
        string folder = slash < 0 ? "" : relative[..slash];
        return relative[(slash + 1)..] == PoiData.FolderFile ? FolderText(ReadFolder(folder, Parse(text))) : GroupText(ReadGroup(relative[..^".json".Length], Parse(text)));
    }

    static bool NotDefault(PoiEditFolder f) => f.Name is not null || f.Order != 0 || f.Style is not null || f.Show is not null || !f.Visible || f.Locked;

    /// <summary>
    /// The files the set makes, by their path in the POI folder ('/'): every group's file, and the <c>_.json</c> of the
    /// folders that have settings, have one now (<paramref name="existing"/>) or would be left without any file.
    /// </summary>
    public static SortedDictionary<string, string> Files(PoiEditSet set, IEnumerable<string> existing)
    {
        var had = existing.ToHashSet(StringComparer.Ordinal);
        var o = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in set.Groups) o[g.Path + ".json"] = GroupText(g);
        foreach (var f in set.Folders)
        {
            string file = f.Path.Length == 0 ? PoiData.FolderFile : f.Path + "/" + PoiData.FolderFile;
            bool empty = f.Path.Length > 0 && !set.Groups.Any(g => g.Path.StartsWith(f.Path + "/", StringComparison.Ordinal));
            if (NotDefault(f) || had.Contains(file) || empty) o[file] = FolderText(f);
        }
        return o;
    }

    /// <summary>
    /// Checks the paths of a set for the disk: every folder and group path is made of names a Windows file may have, no
    /// two differ only in case, a group is not named <c>_</c>, every group's folder is in the set, point ids are unique
    /// in their group. Returns the problems (none: the set can be written).
    /// </summary>
    public static List<string> PathProblems(PoiEditSet set)
    {
        var o = new List<string>();
        var folders = set.Folders.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        if (!folders.Contains("")) o.Add("the POI folder itself is missing");
        foreach (var f in set.Folders.Where(f => f.Path.Length > 0))
        {
            if (!ValidPath(f.Path)) o.Add($"folder '{f.Path}': not a folder name for the disk");
            var parent = f.Path.LastIndexOf('/') is var i and >= 0 ? f.Path[..i] : "";
            if (!folders.Contains(parent)) o.Add($"folder '{f.Path}': its folder '{parent}' is missing");
        }
        foreach (var g in set.Groups)
        {
            if (!ValidPath(g.Path)) o.Add($"group '{g.Path}': not a file name for the disk");
            else if (g.Path[(g.Path.LastIndexOf('/') + 1)..] == "_") o.Add($"group '{g.Path}': '_' is the name of a folder's settings");
            var folder = g.Path.LastIndexOf('/') is var i and >= 0 ? g.Path[..i] : "";
            if (!folders.Contains(folder)) o.Add($"group '{g.Path}': its folder '{folder}' is missing");
            foreach (var d in g.Points.GroupBy(p => p.Id).Where(d => d.Count() > 1)) o.Add($"group '{g.Path}': two points named '{d.Key}'");
        }
        var paths = set.Folders.Select(f => f.Path).Concat(set.Groups.Select(g => g.Path + ".json")).Where(p => p.Length > 0);
        foreach (var same in paths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(d => d.Count() > 1))
            o.Add($"'{string.Join("', '", same)}': names that differ only in case are one name on the disk");
        foreach (var s in set.Styles.GroupBy(s => s.Id).Where(d => d.Count() > 1)) o.Add($"two POI styles named '{s.Key}'");
        return o;
    }

    static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>True for a path of names ('/') a Windows file or folder may have.</summary>
    public static bool ValidPath(string path) => path.Split('/').All(ValidName);

    static bool ValidName(string n) =>
        n.Length > 0 && n.Length <= 120 && n.Trim() == n && !n.EndsWith('.') && n is not ("." or "..")
        && n.All(c => c >= 32 && "<>:\"/\\|?*".IndexOf(c) < 0)
        && !Reserved.Contains(n.Split('.')[0], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A name for the disk made from a name typed on the screen: the characters a Windows file cannot have become '_',
    /// spaces and dots at the end go, a reserved name gets '_' after it, an empty name is '_1'.
    /// </summary>
    public static string SafeName(string name)
    {
        var s = new string(name.Select(c => c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (s.Length > 120) s = s[..120].TrimEnd('.', ' ');
        if (s.Length == 0 || s == "_") return "_1";
        return Reserved.Contains(s.Split('.')[0], StringComparer.OrdinalIgnoreCase) ? s + "_" : s;
    }

    /// <summary>What <see cref="WriteFolder"/> did.</summary>
    public sealed record WriteResult(IReadOnlyList<string> Written, IReadOnlyList<string> Deleted);

    /// <summary>
    /// Makes the POI folder hold <paramref name="files"/>: a file whose text on disk says the same (as the app writes it)
    /// is left alone; others are written as given. The <c>.json</c> files the set no longer has are deleted, then the
    /// folders left without any file (other files are never touched).
    /// </summary>
    public static WriteResult WriteFolder(string folder, IReadOnlyDictionary<string, string> files)
    {
        Directory.CreateDirectory(folder);
        var existing = PoiData.ReadFolder(folder).ToDictionary(e => e.Path, e => e.Text, StringComparer.Ordinal);
        var written = new List<string>();
        var deleted = new List<string>();
        foreach (var (path, text) in files)
        {
            var full = Path.Combine([folder, .. path.Split('/')]);
            if (existing.TryGetValue(path, out var now) && Canonical(path, now) == text) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
            written.Add(path);
        }
        foreach (var path in existing.Keys.Where(p => !files.ContainsKey(p)))
        {
            File.Delete(Path.Combine([folder, .. path.Split('/')]));
            deleted.Add(path);
        }
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        return new WriteResult(written, deleted);
    }

    /// <summary>A free name beside a project file: <paramref name="name"/>, else with -2, -3, ... before its extension.</summary>
    public static string FreeName(string folder, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            var candidate = i == 1 ? name : $"{stem}-{i}{ext}";
            var full = Path.Combine(folder, candidate);
            if (!File.Exists(full) && !Directory.Exists(full)) return candidate;
        }
    }
}
