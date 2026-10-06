using FxMapGenerator.App.Game;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Projects;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FxMapGenerator.App.Web;

/// <summary>
/// The game under <c>/api/game</c>: the pre-check of the open project (run it, read the latest report and its test shot)
/// and FiveM's graphics settings (the difference to what the shots need, put the values in after a backup, put a backup
/// back). A finished pre-check also goes out as the SSE event <c>precheck</c>. The latest report says when the program
/// stopped the capture resource after it (<c>resourceStoppedUtc</c>): the screens then take the check as not made.
/// </summary>
public static class GameEndpoints
{
    static readonly HashSet<string> PrecheckFiles = new(StringComparer.Ordinal) { "shot.png", "shot-marked.png", "console.log", "report.json" };

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/game/precheck", (ProjectSession session) =>
        {
            if (session.Current is not { } project) return Error(StatusCodes.Status409Conflict, "NO_PROJECT", "no project is open");
            return PrecheckReport.LatestFor(new Core.State.WorkFolder(project.WorkFolderPath)) is { } report ? Results.Json(report, AppHost.Json) : Results.NoContent();
        });

        api.MapPost("/game/precheck", async (ProjectSession session, JobManager jobs, IGameAccess game, EventHub hub) =>
        {
            if (session.Current is not { } project) return Error(StatusCodes.Status409Conflict, "NO_PROJECT", "no project is open");
            // one user of the game at a time: not beside a run's work in the game, nor beside the resource's start
            IDisposable use;
            try { use = jobs.UseGame(JobManager.Precheck); }
            catch (JobException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
            using var held = use;
            try
            {
                var report = await new Precheck(game, new Precheck.Options { ResourceVersion = AppVersion.Value }).RunAsync(project).ConfigureAwait(false);
                // the resource answered with the exe's version: it is on the server
                if (report.Items.Any(i => i.Id == "resource" && i.Ok == true))
                    new ResourcePlacement(Services.CaptureResource.Version, ResourcePlacement.Answered, DateTime.UtcNow)
                        .Write(new Core.State.WorkFolder(project.WorkFolderPath));
                hub.Publish("precheck", report);
                return Results.Json(report, AppHost.Json);
            }
            catch (InvalidOperationException ex) { return Error(StatusCodes.Status409Conflict, "GAME_BUSY", ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "IO", ex.Message); }
        });

        api.MapGet("/game/precheck/files/{name}", (string name, ProjectSession session) =>
        {
            if (session.Current is not { } project) return Error(StatusCodes.Status409Conflict, "NO_PROJECT", "no project is open");
            if (!PrecheckFiles.Contains(name)) return Results.NotFound();
            var report = PrecheckReport.Latest(Path.Combine(project.WorkFolderPath, "logs"));
            var file = report is null ? null : Path.Combine(report.Folder, name);
            if (file is null || !File.Exists(file)) return Results.NotFound();
            return Results.File(file, name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : name.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "text/plain; charset=utf-8");
        });

        api.MapGet("/game/render", (RenderSettingsFile render) => RenderGuard(() => render.Read()));

        // whether FiveM runs on this PC (the notice that the work in the game is over stays until it is closed)
        api.MapGet("/game/fivem", (RenderSettingsFile render) => Results.Json(new { running = render.FiveMRunning }, AppHost.Json));

        api.MapPost("/game/render/apply", (RenderSettingsFile render) => RenderGuard(() =>
        {
            var backup = render.Apply();
            return new RenderChange(backup, render.Read());
        }));

        api.MapPost("/game/render/restore", (RenderRestoreRequest? request, RenderSettingsFile render) => RenderGuard(() =>
        {
            var restored = render.Restore(request?.Backup);
            return new RenderChange(restored, render.Read());
        }));
    }

    static IResult RenderGuard<T>(Func<T> work)
    {
        try { return Results.Json(work(), AppHost.Json); }
        catch (RenderSettingsException ex)
        {
            return Error(ex.Code is "NO_FILE" or "NO_BACKUP" ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, ex.Code, ex.Message);
        }
        catch (InvalidDataException ex) { return Error(StatusCodes.Status422UnprocessableEntity, "BAD_FILE", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "IO", ex.Message); }
    }

    static IResult Error(int status, string code, string message) => Results.Json(ApiError.Of(code, message), AppHost.Json, statusCode: status);
}

/// <param name="Backup">The backup made (apply) or put back (restore).</param>
public sealed record RenderChange(string Backup, RenderStatus Status);

public sealed record RenderRestoreRequest(string? Backup);
