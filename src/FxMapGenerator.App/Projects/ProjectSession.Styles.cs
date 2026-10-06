using System.Text.Json.Nodes;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.App.Projects;

/// <summary>The styles of the open project: the bundled atlas styles (read only) and the project's own (<see cref="ProjectStyles"/>).</summary>
public sealed partial class ProjectSession
{
    /// <summary>The shared styles: <c>styles/</c> in the app's settings folder.</summary>
    public string SharedStylesFolder => Path.Combine(_settings.DataDirectory, ProjectStyles.FolderName);

    /// <summary>
    /// The bundled atlas styles, then the project's own in id order (with the maps that use each), and the names of the
    /// game's zones the project's game files hold (for the table per zone; empty before they are read).
    /// </summary>
    public StyleListDto Styles()
    {
        var project = Require();
        var list = AtlasPresets.BuiltIn.Select(id => StyleItemDto.OfBundled(id, ProjectStyles.UsedBy(project, id))).ToList();
        list.AddRange(ProjectStyles.List(project).Select(e => StyleItemDto.Of(e, ProjectStyles.UsedBy(project, e.Id))));
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> zones = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var names = Path.Combine(new WorkFolder(project.WorkFolderPath).Game, GameFilesOutput.Names);
        if (File.Exists(names))
            try { zones = GameNames.Read(names).Zones; }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        return new StyleListDto(project.File.Styles, list, zones);
    }

    /// <summary>The shared styles (<see cref="SharedStylesFolder"/>) in id order.</summary>
    public IReadOnlyList<StyleItemDto> SharedStyles() => ProjectStyles.List(SharedStylesFolder).Select(e => StyleItemDto.Of(e, [])).ToList();

    /// <summary>A style's values: a bundled one's as the app carries it, a project style's over its base with the changes and the base's own.</summary>
    public StyleDto Style(string id)
    {
        var project = Require();
        if (AtlasPresets.BuiltIn.Contains(id))
            return new StyleDto(id, MapStyle.Builtin(id).Source["name"]!.DeepClone(), null, true, MapStyle.Builtin(id).Source.DeepClone(), new JsonObject(), null,
                ProjectStyles.UsedBy(project, id), null);
        var style = ReadOwn(project, id);
        return new StyleDto(id, JsonValue.Create(style.Name), style.Base, false, style.Values(), style.Changes.DeepClone(), MapStyle.Builtin(style.Base).Source.DeepClone(),
            ProjectStyles.UsedBy(project, id), null);
    }

    /// <summary>
    /// A new style of the project made from another: a bundled atlas style, one of the project's styles (its base and
    /// changes copied) or a shared one (<paramref name="request"/>.Shared), with its own id and name. The first style
    /// makes the project's styles folder. <see cref="ProjectException"/> INVALID (id or name), ID_TAKEN, NOT_FOUND.
    /// </summary>
    public StyleDto CreateStyle(NewStyleRequest request)
    {
        Project project;
        var id = (request.Id ?? "").Trim();
        lock (_sync)
        {
            project = Load();
            CheckNewId(project, id);
            var from = request.From ?? "";
            UserStyle source = request.Shared
                ? ProjectStyles.Find(SharedStylesFolder, from) ?? throw new ProjectException("NOT_FOUND", $"no shared style '{from}'")
                : AtlasPresets.BuiltIn.Contains(from) ? new UserStyle(from, from, from, new JsonObject())
                : ReadOwn(project, from);
            var style = source with { Id = id, Name = NameOf(request.Name), Changes = source.Changes.DeepClone().AsObject() };
            try { UserStyleFile.Check(style, $"style '{id}'"); }
            catch (StyleException ex) { throw new ProjectException("INVALID", ex.Message); }
            if (ProjectStyles.Write(project, style)) project.Save();
            _project = project;
        }
        Publish(project);
        return Style(id);
    }

    /// <summary>
    /// Saves the values of one of the project's styles: its file keeps its id, name and base, and holds the values that
    /// differ from the base (<paramref name="values"/> is the whole style as the screen edits it). <see cref="ProjectException"/>
    /// INVALID with every problem (nothing is written), NOT_FOUND; <see cref="JobException"/> LOCKED while a running stage of
    /// this project reads the style of a map it draws.
    /// </summary>
    public StyleDto SaveStyle(string id, JsonObject values)
    {
        lock (_sync)
        {
            var project = Load();
            var style = ReadOwn(project, id);
            if (ProjectStyles.UsedBy(project, id).Count > 0) Check(project, InputKeys.Style);
            var next = style with { Changes = StyleChanges.Between(MapStyle.Builtin(style.Base).Source, values, UserStyleFile.OwnKeys) };
            try { UserStyleFile.Check(next, $"style '{id}'"); }
            catch (StyleException ex) { throw new ProjectException("INVALID", ex.Message); }
            ProjectStyles.Write(project, next);
            _project = project;
        }
        // the project screen shows what the maps now need made again
        Publish(Current!);
        return Style(id);
    }

    /// <summary>Gives one of the project's styles another name (its id stays). <see cref="ProjectException"/> INVALID, NOT_FOUND.</summary>
    public StyleDto RenameStyle(string id, string? name)
    {
        lock (_sync)
        {
            var project = Load();
            var style = ReadOwn(project, id);
            ProjectStyles.Write(project, style with { Name = NameOf(name) });
            _project = project;
        }
        Publish(Current!);
        return Style(id);
    }

    /// <summary>Deletes one of the project's styles. <see cref="ProjectException"/> IN_USE while a map of the project uses it, NOT_FOUND.</summary>
    public void DeleteStyle(string id)
    {
        lock (_sync)
        {
            var project = Load();
            var path = OwnPath(project, id);
            var maps = ProjectStyles.UsedBy(project, id);
            if (maps.Count > 0) throw new ProjectException("IN_USE", $"the style '{id}' is used by {string.Join(", ", maps)}");
            File.Delete(path);
            _project = project;
        }
        Publish(Current!);
    }

    /// <summary>
    /// A style file taken into the project: a user style's (the form the app writes) or a whole style (which becomes a
    /// style of the bundled atlas style of its kind of ground, named in <paramref name="language"/>, the screens' language,
    /// when its name is in English and Japanese), under its own id or <paramref name="id"/>.
    /// <see cref="ProjectException"/> INVALID (with every problem), ID_TAKEN.
    /// </summary>
    public StyleDto ImportStyle(byte[] file, string? id, string? language = null)
    {
        Project project;
        UserStyle style;
        lock (_sync)
        {
            project = Load();
            try { style = UserStyleFile.ParseAny(file, "the file", language is "ja" ? "ja" : "en"); }
            catch (StyleException ex) { throw new ProjectException("INVALID", ex.Message); }
            if (!string.IsNullOrWhiteSpace(id)) style = style with { Id = id.Trim() };
            CheckNewId(project, style.Id);
            if (ProjectStyles.Write(project, style)) project.Save();
            _project = project;
        }
        Publish(project);
        return Style(style.Id);
    }

    /// <summary>
    /// Copies one of the project's styles into the shared styles (other projects can make styles from it). <see cref="ProjectException"/>
    /// EXISTS when a shared style has the id and <paramref name="overwrite"/> is not set, NOT_FOUND.
    /// </summary>
    public void SaveSharedStyle(string id, bool overwrite)
    {
        lock (_sync)
        {
            var project = Load();
            var style = ReadOwn(project, id);
            var path = ProjectStyles.PathOf(SharedStylesFolder, id);
            if (File.Exists(path) && !overwrite) throw new ProjectException("EXISTS", $"a shared style '{id}' exists already");
            UserStyleFile.Write(path, style);
        }
    }

    /// <summary>A style's file as it is (a bundled style's as the app carries it), with its file name.</summary>
    public (byte[] Bytes, string Name) StyleFile(string id)
    {
        var project = Require();
        if (AtlasPresets.BuiltIn.Contains(id))
        {
            using var s = EmbeddedData.Open($"styles/{id}.json");
            using var m = new MemoryStream();
            s.CopyTo(m);
            return (m.ToArray(), id + ".json");
        }
        return (File.ReadAllBytes(OwnPath(project, id)), id + ".json");
    }

    Project Load()
    {
        var open = _project ?? throw new ProjectException("NO_PROJECT", "no project is open");
        return Project.Load(open.FilePath);             // the file may have been changed from the command line
    }

    static void CheckNewId(Project project, string id)
    {
        if (UserStyleFile.IdFormProblem(id) is { } p) throw new ProjectException("INVALID", p);
        if (ProjectStyles.IsTaken(project, id)) throw new ProjectException("ID_TAKEN", $"the id '{id}' is taken");
    }

    static string NameOf(string? name) =>
        name?.Trim() is { Length: > 0 } n ? n : throw new ProjectException("INVALID", "the name is needed");

    static string OwnPath(Project project, string id)
    {
        var folder = ProjectStyles.Folder(project);
        var path = folder is null || UserStyleFile.IdFormProblem(id) is not null ? null : ProjectStyles.PathOf(folder, id);
        return path is not null && File.Exists(path) ? path : throw new ProjectException("NOT_FOUND", $"the project has no style '{id}'");
    }

    static UserStyle ReadOwn(Project project, string id)
    {
        var path = OwnPath(project, id);
        try
        {
            var style = UserStyleFile.Read(path);
            return style.Id == id ? style : throw new ProjectException("INVALID", $"{path}: its id '{style.Id}' is not its file name");
        }
        catch (StyleException ex) { throw new ProjectException("INVALID", ex.Message); }
    }
}

/// <summary><c>PUT /api/project/styles/{id}/name</c>: the style's new name.</summary>
public sealed record StyleNameEdit(string? Name);

/// <summary><c>POST /api/project/styles</c>: a new style from another (a bundled or project style's id, or a shared style's with <see cref="Shared"/>), its id and name.</summary>
public sealed record NewStyleRequest(string? From, string? Id, string? Name, bool Shared = false);

/// <summary>
/// <c>GET /api/project/styles</c>: the project's <c>styles</c> folder (null: none yet), the styles (bundled first), and the
/// names of the game's zones by code (<c>en</c>, <c>ja</c>) from the project's game files.
/// </summary>
public sealed record StyleListDto(string? Folder, IReadOnlyList<StyleItemDto> Styles, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ZoneNames);

/// <summary>
/// A style of a list: id, name (a bundled style's in English and Japanese, a project style's one text), the bundled style it
/// was made from (null for a bundled style), whether it is bundled, the project's maps that use it, and why its file cannot
/// be read (null: it can).
/// </summary>
public sealed record StyleItemDto(string Id, JsonNode Name, string? Base, bool Bundled, IReadOnlyList<string> Maps, string? Problem)
{
    public static StyleItemDto OfBundled(string id, IReadOnlyList<string> maps) => new(id, MapStyle.Builtin(id).Source["name"]!.DeepClone(), null, true, maps, null);

    public static StyleItemDto Of(ProjectStyles.Entry e, IReadOnlyList<string> maps) =>
        e.Style is { } s ? new(e.Id, JsonValue.Create(s.Name), s.Base, false, maps, null) : new(e.Id, JsonValue.Create(e.Id), null, false, maps, e.Problem);
}

/// <summary>
/// <c>GET /api/project/styles/{id}</c>: the style's values (a project style's: its base's with its changes), the values it
/// changed from its base (<see cref="Changes"/>, empty for a bundled style), its base's values (null for a bundled style)
/// and the maps that use it.
/// </summary>
public sealed record StyleDto(string Id, JsonNode Name, string? Base, bool Bundled, JsonNode Values, JsonNode Changes, JsonNode? BaseValues,
    IReadOnlyList<string> Maps, string? Problem);
