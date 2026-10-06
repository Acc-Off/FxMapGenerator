using System.Diagnostics;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Projects;
using FxMapGenerator.App.Services;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FxMapGenerator.App.Web;

/// <summary>
/// The export of the open project under <c>/api/project/export</c>: what can be written (tiles per map, the minimap
/// dictionaries, the maps that can be written in layers) with the saved choices, a check of other choices, starting it
/// (a run, like a build) and showing the output folder in Explorer. Under <c>/api/project/convert</c>, the conversion of
/// an edited picture into web tiles and a minimap resource of its own: a check of its choices and starting it.
/// </summary>
public static class ExportEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/project/export", (ProjectSession session, AppOptions app, SettingsStore settings) => Guard(() =>
        {
            var project = session.Require();
            var inventory = ExportInventory.Of(project, session.State(project), GameFilesDefaults.Minimap(app, settings.Current));
            return Info(project, inventory, ExportOptions.FromProject(project, inventory.Maps.Select(m => m.Map)));
        }));

        api.MapPost("/project/export/check", (ExportRequest request, ProjectSession session, AppOptions app, SettingsStore settings) => Guard(() =>
        {
            var project = session.Require();
            var inventory = ExportInventory.Of(project, session.State(project), GameFilesDefaults.Minimap(app, settings.Current));
            return Info(project, inventory, request.ToOptions(project));
        }));

        api.MapPost("/project/export", (ExportRequest request, ProjectSession session, JobManager jobs, AppOptions app, SettingsStore settings) => Guard(() =>
        {
            var project = session.Require();
            var options = request.ToOptions(project);
            var game = GameFilesDefaults.Minimap(app, settings.Current);
            var problems = ExportInventory.Of(project, session.State(project), game).Check(options);
            if (problems.Count > 0) throw new ExportRefused(problems[0]);
            session.SaveExportChoices(options, request.ResourceName);
            return jobs.StartExport(project.FilePath, project.File.Parallel, options with { ResourceName = request.ResourceName is { Length: > 0 } n ? n : null }, game);
        }));

        api.MapPost("/project/convert/check", (ConvertRequest request, ProjectSession session, AppOptions app, SettingsStore settings) => Guard(() =>
            ConvertInfo(session.Require(), request, GameFilesDefaults.Minimap(app, settings.Current))));

        api.MapPost("/project/convert", (ConvertRequest request, ProjectSession session, JobManager jobs, AppOptions app, SettingsStore settings) => Guard(() =>
        {
            var project = session.Require();
            var game = GameFilesDefaults.Minimap(app, settings.Current);
            var info = ConvertInfo(project, request, game);
            if (info.Problems.Count > 0) throw new ExportRefused(info.Problems[0]);
            var options = request.ToOptions(project);
            session.SaveConvertChoices(options.Picture, options.Map);
            return jobs.StartConvert(project.FilePath, project.File.Parallel, options, game);
        }));

        api.MapPost("/project/export/show", (ExportShowRequest? request, ProjectSession session) =>
        {
            var project = session.Current;
            if (project is null || string.IsNullOrWhiteSpace(request?.Path)) return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", "path is required");
            var path = Path.GetFullPath(request.Path);
            if (!Directory.Exists(path)) return Error(StatusCodes.Status404NotFound, "NOT_FOUND", $"{path} does not exist");
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
            return Results.Json(new { ok = true }, AppHost.Json);
        });
    }

    static ExportInfoDto Info(Project project, ExportInventory inventory, ExportOptions options)
    {
        var name = options.Minimap ? options.ResourceName ?? ExportOptions.DefaultResourceName(options.Folder, DateTime.Now) : null;
        var kept = project.File.Export.Editable;
        var picture = project.File.Export.Picture;
        var file = picture.File is { } f ? project.ResolvePath(f) : null;
        var map = picture.Map is { } m && project.Maps.Any(x => x.Id == m) ? m : EditedPicture.DefaultMap(project, file);
        return new ExportInfoDto(inventory.Maps, inventory.Minimap,
            new ExportRequest(options.Folder, options.Maps, options.Zip, options.BaseUrl, options.Minimap, name, options.MaxZoom,
                options.Editable?.Maps ?? [], options.Editable?.Zoom ?? kept.Zoom, options.Editable?.Written ?? kept.Formats),
            inventory.Check(options), ExportRecord.Read(options.Folder), ExportOptions.DefaultFolder(project), inventory.Editable,
            new ConvertSavedDto(file, map));
    }

    static ConvertInfoDto ConvertInfo(Project project, ConvertRequest request, MinimapGameFiles game)
    {
        var options = request.ToOptions(project);
        var picture = options.Picture.Length == 0 ? null : EditedPicture.Look(options.Picture, project.Frame);
        var name = options.Minimap ? options.ResourceName ?? ExportOptions.DefaultResourceName(options.Folder, DateTime.Now) : null;
        return new ConvertInfoDto(picture, options.Map.Length == 0 ? null : options.Map, picture is null ? null : EditedPicture.MapOf(project, options.Picture)?.Id,
            ConvertStages.Check(project, options, picture, game), name);
    }

    static IResult Guard<T>(Func<T> action)
    {
        try { return Results.Json(action(), AppHost.Json); }
        catch (ExportRefused ex) { return Error(StatusCodes.Status400BadRequest, "EXPORT_" + ex.Problem.Code, ex.Problem.Message); }
        catch (ProjectException ex) { return Error(ex.Code == "NO_PROJECT" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, ex.Code, ex.Message); }
        catch (JobException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Error(StatusCodes.Status500InternalServerError, "IO", ex.Message);
        }
    }

    static IResult Error(int status, string code, string message) => Results.Json(ApiError.Of(code, message), AppHost.Json, statusCode: status);

    sealed class ExportRefused(ExportProblem problem) : Exception(problem.Message)
    {
        public ExportProblem Problem { get; } = problem;
    }
}

/// <summary>Export choices from the screen; null fields take the saved ones.</summary>
/// <param name="Maps">The maps whose web tiles are written; empty = none.</param>
/// <param name="ResourceName">The minimap resource's name; null or empty = the dated default.</param>
/// <param name="MaxZoom">The finest zoom of the web tiles (6-8).</param>
/// <param name="EditableMaps">The maps written as layered files for editing; null or empty = none (never taken from the saved choices).</param>
/// <param name="EditableZoom">The zoom level of the layered files (6 or 7).</param>
/// <param name="EditableFormats">The formats of the layered files (<c>psd</c>, <c>svg</c>); null = the saved ones, empty = none chosen.</param>
/// <param name="Language">The language of the screens (<c>ja</c> or <c>en</c>): the layers of the layered files are named in it.</param>
public sealed record ExportRequest(string? Folder, IReadOnlyList<string>? Maps, bool? Zip, string? BaseUrl, bool? Minimap, string? ResourceName, int? MaxZoom = null,
    IReadOnlyList<string>? EditableMaps = null, int? EditableZoom = null, IReadOnlyList<string>? EditableFormats = null, string? Language = null)
{
    public ExportOptions ToOptions(Project project)
    {
        var saved = ExportOptions.FromProject(project, project.Maps.Select(m => m.Id));
        var folder = string.IsNullOrWhiteSpace(Folder) ? saved.Folder : Path.GetFullPath(Path.Combine(project.Folder, Folder.Trim()));
        var kept = project.File.Export.Editable;
        var editable = new EditableChoice(EditableMaps?.ToList() ?? [], EditableZoom ?? kept.Zoom,
            EditableFormats is not null ? EditableFormats.ToList() : kept.Formats.ToList(), Language == "ja" ? "ja" : "en");
        return new ExportOptions(folder, Maps?.ToList() ?? saved.Maps.ToList(), Zip ?? saved.Zip,
            BaseUrl is null ? saved.BaseUrl : string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.Trim(), Minimap ?? saved.Minimap,
            string.IsNullOrWhiteSpace(ResourceName) ? null : ResourceName.Trim(), MaxZoom ?? saved.MaxZoom, editable);
    }
}

public sealed record ExportShowRequest(string? Path);

/// <summary>The choices of a conversion of an edited picture from the screen.</summary>
/// <param name="Folder">The output folder; null or empty = the export's (the saved one, else <c>export</c> beside the project file).</param>
/// <param name="File">The PNG picture (relative to the project file's folder unless absolute).</param>
/// <param name="Map">The id of the map the picture was made from; null or empty = <see cref="EditedPicture.DefaultMap"/>.</param>
/// <param name="Tiles">Write the web tiles; null = yes.</param>
/// <param name="Minimap">Write the minimap resource; null = yes.</param>
/// <param name="ResourceName">The minimap resource's name; null or empty = the dated default.</param>
/// <param name="BaseUrl">Where the web tiles will be served from; null = the export's saved one, empty = none.</param>
public sealed record ConvertRequest(string? Folder, string? File, string? Map, bool? Tiles = null, bool? Minimap = null, string? ResourceName = null, string? BaseUrl = null)
{
    public ConvertOptions ToOptions(Project project)
    {
        var folder = string.IsNullOrWhiteSpace(Folder) ? ExportOptions.DefaultFolder(project) : Path.GetFullPath(Path.Combine(project.Folder, Folder.Trim()));
        var file = string.IsNullOrWhiteSpace(File) ? "" : project.ResolvePath(File.Trim());
        var map = string.IsNullOrWhiteSpace(Map) ? EditedPicture.DefaultMap(project, file) ?? "" : Map.Trim();
        var saved = project.File.Export.BaseUrl;
        return new ConvertOptions(folder, file, map, Tiles ?? true, Minimap ?? true, string.IsNullOrWhiteSpace(ResourceName) ? null : ResourceName.Trim(),
            BaseUrl is null ? (string.IsNullOrWhiteSpace(saved) ? null : saved.Trim()) : string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.Trim());
    }
}

/// <summary>What a conversion would do with its choices.</summary>
/// <param name="Picture">The picture as looked at (its size, its zoom level, why it cannot be converted); null when none is chosen.</param>
/// <param name="Map">The map taken for it; null when the project makes no map.</param>
/// <param name="GuessedMap">The map the picture's file name tells (<see cref="EditedPicture.MapOf"/>), or null.</param>
/// <param name="Problems">Why the conversion cannot run; empty = it can.</param>
/// <param name="ResourceName">The name the minimap resource would get; null when it is not written.</param>
public sealed record ConvertInfoDto(PictureInfo? Picture, string? Map, string? GuessedMap, IReadOnlyList<ExportProblem> Problems, string? ResourceName);

/// <summary>The picture of the last conversion, for the screen to start from.</summary>
/// <param name="File">Its full path; null = none yet.</param>
/// <param name="Map">The map taken for it: the one kept while the project makes it, else <see cref="EditedPicture.DefaultMap"/>; null when the project makes no map.</param>
public sealed record ConvertSavedDto(string? File, string? Map);

/// <param name="Options">The choices checked (the saved ones for GET), with the resource name the export would use.</param>
/// <param name="Problems">Why these choices cannot be exported; empty = they can.</param>
/// <param name="Last">The earlier export in the chosen folder, if any.</param>
/// <param name="DefaultFolder">Where exports go unless another folder is chosen.</param>
/// <param name="Editable">The maps that can be written as layered files, and the frame's blocks (their pictures' size).</param>
/// <param name="Picture">The edited picture last converted.</param>
public sealed record ExportInfoDto(IReadOnlyList<TileSetInfo> Maps, MinimapInfo Minimap, ExportRequest Options, IReadOnlyList<ExportProblem> Problems,
    ExportRecord? Last, string DefaultFolder, EditableInfo Editable, ConvertSavedDto Picture);
