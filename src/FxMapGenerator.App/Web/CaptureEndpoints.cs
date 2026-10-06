using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Projects;
using FxMapGenerator.App.Services;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FxMapGenerator.App.Web;

/// <summary>
/// The capture resource under <c>/api/capture-resource</c> (what the exe holds, what a folder holds, writing it into a
/// folder, a zip of it; the open project's mark that it is on the server) and <c>/api/game/resource/start</c> (start it
/// from the game console).
/// </summary>
public static class CaptureEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/capture-resource", (ProjectSession session) =>
            Results.Json(new CaptureResourceDto(CaptureResource.Name, CaptureResource.Version,
                session.Current is { } p ? ResourcePlacement.Read(new WorkFolder(p.WorkFolderPath)) : null), AppHost.Json));

        // what a folder already holds under the resource's name (the screen asks before replacing it)
        api.MapGet("/capture-resource/found", (string folder) =>
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Error(StatusCodes.Status400BadRequest, "NO_FOLDER", $"not a folder: {folder}");
            return Results.Json(new { found = CaptureResource.Found(folder) }, AppHost.Json);
        });

        api.MapPost("/capture-resource/save", (CaptureSaveRequest? request, ProjectSession session) =>
        {
            var folder = request?.Folder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Error(StatusCodes.Status400BadRequest, "NO_FOLDER", $"not a folder: {folder}");
            if (CaptureResource.Found(folder) is { } there && request!.Replace != true)
                return Error(StatusCodes.Status409Conflict, "EXISTS", $"{Path.Combine(folder, CaptureResource.Name)} is there already (version {there})");
            string written;
            try { written = CaptureResource.WriteTo(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Error(StatusCodes.Status500InternalServerError, "IO", ex.Message); }
            ResourcePlacement? mark = null;
            if (session.Current is { } p)
            {
                mark = new ResourcePlacement(CaptureResource.Version, ResourcePlacement.Saved, DateTime.UtcNow, written);
                mark.Write(new WorkFolder(p.WorkFolderPath));
            }
            return Results.Json(new CaptureResourceDto(CaptureResource.Name, CaptureResource.Version, mark, written), AppHost.Json);
        });

        api.MapGet("/capture-resource/zip", () =>
            Results.File(CaptureResource.Zip(), "application/zip", CaptureResource.Name + ".zip"));

        // the user's own mark: put on the server some other way (or taken back)
        api.MapPost("/project/capture-placed", (CapturePlacedRequest? request, ProjectSession session) =>
        {
            if (session.Current is not { } p) return Error(StatusCodes.Status409Conflict, "NO_PROJECT", "no project is open");
            var folder = new WorkFolder(p.WorkFolderPath);
            ResourcePlacement? mark = null;
            if (request?.Placed == true)
            {
                mark = new ResourcePlacement(CaptureResource.Version, ResourcePlacement.User, DateTime.UtcNow);
                mark.Write(folder);
            }
            else ResourcePlacement.Clear(folder);
            return Results.Json(new CaptureResourceDto(CaptureResource.Name, CaptureResource.Version, mark), AppHost.Json);
        });

        // start it from the game console when it does not answer (refresh, ensure); what it said, and the console's lines
        api.MapPost("/game/resource/start", async (ProjectSession session, JobManager jobs, IGameAccess game, HttpContext context) =>
        {
            if (session.Current is not { } p) return Error(StatusCodes.Status409Conflict, "NO_PROJECT", "no project is open");
            // one user of the game at a time: not beside a run's work in the game, nor beside the connection check
            IDisposable use;
            try { use = jobs.UseGame(JobManager.ResourceStart); }
            catch (Core.Jobs.JobException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
            using var held = use;
            try
            {
                var r = await new ResourceStart(game).RunAsync(p.File.Console.Host, p.File.Console.Port, context.RequestAborted).ConfigureAwait(false);
                // it answered with this version: it is on the server
                if (r.Hello is { } hello && hello.Version == CaptureResource.Version)
                    new ResourcePlacement(hello.Version, ResourcePlacement.Answered, DateTime.UtcNow).Write(new WorkFolder(p.WorkFolderPath));
                return Results.Json(new ResourceStartDto(r.State, r.Hello?.Resource, r.Hello?.Version, CaptureResource.Version, r.Hello?.Ace, r.Console, r.Reason), AppHost.Json);
            }
            catch (InvalidOperationException ex) { return Error(StatusCodes.Status409Conflict, "GAME_BUSY", ex.Message); }
        });
    }

    static IResult Error(int status, string code, string message) => Results.Json(ApiError.Of(code, message), AppHost.Json, statusCode: status);
}

/// <param name="Placed">The open project's mark that this resource is on its server (null: none, or no project).</param>
/// <param name="Written">The folder just written (save only).</param>
public sealed record CaptureResourceDto(string Name, string Version, ResourcePlacement? Placed, string? Written = null);

public sealed record CaptureSaveRequest(string? Folder, bool? Replace);

public sealed record CapturePlacedRequest(bool? Placed);

/// <param name="State"><c>noConsole</c>, <c>running</c>, <c>started</c>, <c>notStarted</c>.</param>
/// <param name="Resource">The name the resource answered under (another folder name still captures).</param>
/// <param name="Expected">The version of the exe's resource.</param>
/// <param name="Reason">Why it did not start: <c>notFound</c>, <c>denied</c>, <c>noAnswer</c> (see <see cref="ResourceStart.Result"/>).</param>
public sealed record ResourceStartDto(string State, string? Resource, string? Version, string Expected, bool? Ace, IReadOnlyList<string> Console, string? Reason);
