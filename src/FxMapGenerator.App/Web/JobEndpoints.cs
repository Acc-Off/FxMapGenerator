using FxMapGenerator.App.Jobs;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FxMapGenerator.App.Web;

/// <summary>
/// Runs under <c>/api/jobs</c>: start a build, read the current (or last) run (204 when there was none), stop it at a boundary or now, change the
/// number of workers while it runs. Progress also comes as the SSE events <c>job</c> and <c>jobLog</c>.
/// </summary>
public static class JobEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/jobs", (JobStartRequest? request, JobManager jobs) =>
        {
            if (string.IsNullOrWhiteSpace(request?.Project)) return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", "project is required");
            int? workers = request.Workers;
            if (workers is < 1) return Error(StatusCodes.Status400BadRequest, "BAD_WORKERS", "workers must be 1 or more");
            (double, double)? scale = null;
            if (request.Scale is { } q)
            {
                if (q.Length != 2 || q[0] <= 0 || q[1] <= 0) return Error(StatusCodes.Status400BadRequest, "BAD_SCALE", "scale needs two positive numbers");
                scale = (q[0], q[1]);
            }
            try
            {
                var snapshot = jobs.Start(request.Project, workers, new BuildStages.Options(scale, request.Recalibrate));
                return Results.Json(snapshot, AppHost.Json, statusCode: StatusCodes.Status202Accepted);
            }
            catch (JobException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
            catch (ProjectException ex) { return Error(ex.Code == "NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest, ex.Code, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Error(StatusCodes.Status500InternalServerError, "IO", ex.Message);
            }
        });

        api.MapGet("/jobs/current", (JobManager jobs) => jobs.Current is { } job ? Results.Json(job, AppHost.Json) : Results.NoContent());

        api.MapPost("/jobs/current/stop", (JobStopRequest? request, JobManager jobs) =>
        {
            var mode = request?.Mode switch
            {
                null or "boundary" => StopMode.Boundary,
                "now" => StopMode.Now,
                _ => StopMode.None,
            };
            if (mode == StopMode.None) return Error(StatusCodes.Status400BadRequest, "BAD_MODE", $"unknown stop mode '{request?.Mode}' (boundary or now)");
            try { return Results.Json(jobs.Stop(mode), AppHost.Json); }
            catch (JobException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
        });

        api.MapPut("/jobs/current/workers", (JobWorkersRequest? request, JobManager jobs) =>
        {
            if (request?.Workers is not { } workers || workers < 1) return Error(StatusCodes.Status400BadRequest, "BAD_WORKERS", "workers must be 1 or more");
            try { return Results.Json(jobs.SetWorkers(workers), AppHost.Json); }
            catch (JobException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
        });
    }

    static IResult Error(int status, string code, string message) => Results.Json(ApiError.Of(code, message), AppHost.Json, statusCode: status);
}

/// <param name="Project">Path of the <c>.fxmapgen.json</c>.</param>
/// <param name="Workers">Workers of the stages without the game; null = the project's default (limited to the processors).</param>
/// <param name="Scale">Satellite scale correction [qx, qy] instead of the stored one (comparisons).</param>
/// <param name="Recalibrate">Measure the scale correction again.</param>
public sealed record JobStartRequest(string? Project, int? Workers, double[]? Scale, bool Recalibrate);

/// <param name="Mode">boundary (default: running units finish) or now.</param>
public sealed record JobStopRequest(string? Mode);

public sealed record JobWorkersRequest(int? Workers);
