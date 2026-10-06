using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Styles;

/// <summary>
/// The user styles in a folder: a project's (<c>styles/</c> beside the project file, the project's <c>styles</c>; made
/// with the first style) or the shared ones of the app's settings folder, a file per style named after its id.
/// </summary>
public static class ProjectStyles
{
    /// <summary>The folder a project's first style makes beside the project file (and the shared folder's name).</summary>
    public const string FolderName = "styles";

    /// <summary>One file of a style folder: the style, or why it cannot be read.</summary>
    public sealed record Entry(string Id, string Path, UserStyle? Style, string? Problem);

    /// <summary>The project's styles folder made absolute, or null (no styles yet); in a run, the copies it took at its start.</summary>
    public static string? Folder(Project project) => project.StylesFolder;

    /// <summary>The styles read, by their file's contents (and the app's bundled base under them).</summary>
    static readonly Dictionary<string, MapStyle> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// A project style as the maps read it (its base with its changes), parsed once per contents of its file.
    /// <see cref="ProjectException"/> STYLE when the project has no such file or it cannot be read.
    /// </summary>
    public static MapStyle Read(Project project, string id)
    {
        var path = Folder(project) is { } folder ? PathOf(folder, id) : null;
        byte[] bytes;
        try
        {
            if (path is null || !File.Exists(path)) throw new ProjectException("STYLE", $"the project has no style '{id}' (a map uses it)");
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ProjectException("STYLE", $"the style '{id}' cannot be read: {ex.Message}"); }
        var key = id + "\n" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        lock (Cache)
            if (Cache.TryGetValue(key, out var hit)) return hit;
        MapStyle style;
        try
        {
            var user = UserStyleFile.Parse(bytes, path);
            if (user.Id != id) throw new StyleException($"{path}: its id '{user.Id}' is not its file name");
            style = user.Style();
        }
        catch (StyleException ex) { throw new ProjectException("STYLE", $"the style '{id}' cannot be read: {ex.Message}"); }
        lock (Cache) Cache[key] = style;
        return style;
    }

    /// <summary>The file of a style in a folder.</summary>
    public static string PathOf(string folder, string id) => System.IO.Path.Combine(folder, id + ".json");

    /// <summary>The styles of a folder in id order (a missing folder has none); a file that cannot be read says why.</summary>
    public static List<Entry> List(string? folder)
    {
        var list = new List<Entry>();
        if (folder is null || !Directory.Exists(folder)) return list;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            var id = System.IO.Path.GetFileNameWithoutExtension(path);
            try
            {
                var s = UserStyleFile.Read(path);
                list.Add(s.Id == id ? new Entry(id, path, s, null) : new Entry(id, path, null, $"{path}: its id '{s.Id}' is not its file name"));
            }
            catch (Exception ex) when (ex is StyleException or IOException or UnauthorizedAccessException) { list.Add(new Entry(id, path, null, ex.Message)); }
        }
        return list;
    }

    /// <summary>The project's styles (<see cref="List(string?)"/> of its folder).</summary>
    public static List<Entry> List(Project project) => List(Folder(project));

    /// <summary>A readable style of a folder, or null.</summary>
    public static UserStyle? Find(string? folder, string id) =>
        List(folder).FirstOrDefault(e => e.Id == id)?.Style;

    /// <summary>Whether an id is taken for a new style of the project: a bundled style's, or one of the project's styles (also an unreadable file of that name).</summary>
    public static bool IsTaken(Project project, string id) =>
        MapStyle.BuiltIn.Contains(id) || (Folder(project) is { } f && File.Exists(PathOf(f, id)));

    /// <summary>
    /// Writes a style into the project's folder, making the folder <see cref="FolderName"/> beside the project file and
    /// naming it in the project (<c>styles</c>) when the project has none yet; the caller saves the project file then.
    /// Returns whether the project file changed.
    /// </summary>
    public static bool Write(Project project, UserStyle style)
    {
        bool named = false;
        if (Folder(project) is not { } folder)
        {
            project.File.Styles = FolderName;
            folder = Folder(project)!;
            named = true;
        }
        UserStyleFile.Write(PathOf(folder, style.Id), style);
        return named;
    }

    /// <summary>The maps of the project that use a style: its atlas maps (one per language), and the minimap when one of them is its map.</summary>
    public static List<string> UsedBy(Project project, string id)
    {
        var maps = project.File.Maps.Atlas.Styles.Contains(id) ? project.Languages.Select(l => $"atlas-{id}-{l}").ToList() : [];
        if (project.File.Minimap.Map is { } mm && mm.StartsWith($"atlas-{id}-", StringComparison.Ordinal) && !maps.Contains(mm)) maps.Add(mm);
        return maps;
    }

    /// <summary>The style ids a new style can be made from: the bundled atlas styles.</summary>
    public static IReadOnlyList<string> Bases => AtlasPresets.BuiltIn;
}

/// <summary>The style editor's table (<c>data/style-schema.json</c>, embedded): the screen reads it as it is.</summary>
public static class StyleSchema
{
    static readonly Lazy<byte[]> File = new(() =>
    {
        using var s = EmbeddedData.Open("style-schema.json");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    });

    public static byte[] Bytes => File.Value;
}
