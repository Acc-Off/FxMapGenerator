using FxMapGenerator.Core.Projects;
using FxMapGenerator.GameData.Gta;

namespace FxMapGenerator.Core.GameFiles;

/// <summary>
/// Where the game files come from for a project: the GTA V folder and the key folder of this PC (the app's settings or
/// the command line, else what can be found: the registry for GTA V, the key search order of
/// <see cref="GtaKeys.Candidates"/>), and the server's resources of the project (optional, in its order: a later one wins).
/// </summary>
public sealed record GameFilesLocation(string? GtaFolder, string? GtaSource, string? KeysFolder, string? KeysSource,
    IReadOnlyList<KeyLookup.Tried> KeysTried, IReadOnlyList<string> ServerResources)
{
    /// <summary>The app's GTA V and key folders (null: found on this PC).</summary>
    public sealed record Defaults(string? GtaFolder = null, string? KeysFolder = null);

    public bool GtaFound => GtaLocator.IsGtaFolder(GtaFolder);
    public bool KeysFound => KeysFolder is not null;

    /// <summary>The app's folders with the project's server resources.</summary>
    public static GameFilesLocation Resolve(Project project, Defaults? defaults = null) =>
        ResolveApp(defaults ?? new Defaults()) with { ServerResources = ServerResourcesOf(project) };

    /// <summary>
    /// The app's own settings alone (no project): the GTA V folder given, else the one found in the registry; the key
    /// folder given (only that one), else the key search order.
    /// </summary>
    public static GameFilesLocation ResolveApp(Defaults defaults)
    {
        string? gta, gtaSource;
        if (!string.IsNullOrWhiteSpace(defaults.GtaFolder)) (gta, gtaSource) = (Path.GetFullPath(defaults.GtaFolder), "app");
        else
        {
            gta = GtaLocator.Detect();
            gtaSource = gta is null ? null : "detected";
        }
        var keys = GtaKeys.Find(string.IsNullOrWhiteSpace(defaults.KeysFolder) ? null : Path.GetFullPath(defaults.KeysFolder));
        return new GameFilesLocation(gta, gtaSource, keys.Folder, keys.Source == "given" ? "app" : keys.Source, keys.TriedFolders, []);
    }

    /// <summary>The project's server resources as absolute paths, in its order.</summary>
    public static IReadOnlyList<string> ServerResourcesOf(Project project) =>
        (project.File.GameFiles.ServerResources ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(project.ResolvePath).ToList();
}
