using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Projects;
using FxMapGenerator.App.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App.Web;

/// <summary>Status, settings and housekeeping endpoints under <c>/api</c>.</summary>
public static class ApiEndpoints
{
    /// <summary>
    /// The GTA V and key folders the app would use with the given settings (the command line's <c>--gta</c> / <c>--keys</c>
    /// win over them): each found or not, where it came from, and for the keys the folders tried with the files they lack.
    /// </summary>
    public static GameFilesStatusDto GameFilesStatus(string? gta, string? keys, AppOptions app)
    {
        var where = Core.GameFiles.GameFilesLocation.ResolveApp(new(app.GtaFolderOverride ?? NullIfEmpty(gta), app.KeysFolderOverride ?? NullIfEmpty(keys)));
        string? gtaProblem = where.GtaFound ? null : where.GtaFolder is null ? "notFound" : "notGta";
        return new GameFilesStatusDto(where.GtaFolder, where.GtaSource, where.GtaFound, gtaProblem, app.GtaFolderOverride is not null,
            where.KeysFolder, where.KeysSource, where.KeysFound, where.KeysTried.Select(t => new KeysTriedDto(t.Folder, t.Source == "given" ? "app" : t.Source, t.Missing)).ToList(),
            app.KeysFolderOverride is not null);
        static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", () => Results.Json(Status(), AppHost.Json));

        api.MapGet("/events", (HttpContext context, EventHub hub, JobManager jobs, ProjectSession session, IHostApplicationLifetime lifetime) =>
            hub.ServeAsync(context, () => Initial(jobs, session), lifetime.ApplicationStopping));

        JobEndpoints.Map(api);
        ProjectEndpoints.Map(api);
        RoadEditorEndpoints.Map(api);
        StyleEndpoints.Map(api);
        PoiEndpoints.Map(api);
        GameEndpoints.Map(api);
        CaptureEndpoints.Map(api);
        ExportEndpoints.Map(api);

        api.MapGet("/settings", (SettingsStore settings) => Results.Json(settings.Current, AppHost.Json));

        api.MapPut("/settings", (AppSettings incoming, SettingsStore settings, ILoggerFactory loggers) =>
        {
            settings.Save(incoming);
            loggers.CreateLogger("Settings").LogInformation("Settings saved to {Path}", settings.Path);
            return Results.Json(settings.Current, AppHost.Json);
        });

        // where the game files would come from with these folders (the settings dialog asks before saving); empty = found
        api.MapGet("/settings/gamefiles", (string? gta, string? keys, AppOptions app) =>
            Results.Json(GameFilesStatus(gta, keys, app), AppHost.Json));

        api.MapGet("/diagnostics", (SettingsStore settings, HostPaths paths, ListenerInfo listener) =>
            Results.Json(new DiagnosticsDto(AppVersion.Value, paths.DataDirectory, settings.Path, paths.LogPath ?? "", listener.Url, Libraries.List()), AppHost.Json));

        api.MapPost("/dialog/folder", async (FolderDialogRequest? request, FolderDialog dialog, HttpContext context) =>
        {
            var picked = await dialog.PickAsync(request?.Initial, request?.Title, context.RequestAborted);
            return Results.Json(new FolderDialogResult(picked), AppHost.Json);
        });

        api.MapGet("/notices", () =>
        {
            using var stream = typeof(ApiEndpoints).Assembly.GetManifestResourceStream("THIRD-PARTY-NOTICES.md");
            if (stream == null) return Results.NotFound();
            using var reader = new StreamReader(stream);
            return Results.Text(reader.ReadToEnd(), "text/markdown; charset=utf-8");
        });

        api.MapPost("/quit", (IHostApplicationLifetime lifetime, ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("App").LogInformation("Quit requested from the UI");
            _ = Task.Run(async () => { await Task.Delay(150); lifetime.StopApplication(); });
            return Results.Json(new { ok = true }, AppHost.Json);
        });
    }

    static StatusDto Status() => new(AppVersion.Value);

    /// <summary>
    /// What a new SSE subscriber gets first: the status, the open project, the current (or last) run, and what uses the
    /// game by hand.
    /// </summary>
    static IEnumerable<(string, object)> Initial(JobManager jobs, ProjectSession session)
    {
        yield return ("status", Status());
        if (session.Current is { } project) yield return ("project", ProjectDto.Of(project));
        if (jobs.Current is { } job) yield return ("job", job);
        yield return ("gameUse", new GameUseDto(jobs.GameByHand));
    }
}

public sealed record FolderDialogRequest(string? Initial, string? Title);

public sealed record FolderDialogResult(string? Path);
