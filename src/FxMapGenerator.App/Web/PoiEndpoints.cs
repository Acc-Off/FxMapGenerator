using FxMapGenerator.App.Projects;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FxMapGenerator.App.Web;

/// <summary>
/// The POI screen: the icons the POI styles can draw (<c>/api/poi/icons</c>), and the open project's points of interest
/// and POI styles under <c>/api/project/poi</c> (read, save, a style's sample, a PNG copied beside the styles, points read
/// from a CSV or JSON file).
/// </summary>
public static class PoiEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // the MDI icons as the app bundles them (name -> path, aliases, tags)
        api.MapGet("/poi/icons", () => Results.Bytes(MdiIcons.Bytes, "application/json"));

        api.MapGet("/project/poi", (ProjectSession session) => Guard(session.Poi));
        // ?language= the screens' language: the bundled points' and styles' names a first save copies into the project take it
        api.MapPut("/project/poi", (PoiEditSet set, string? language, ProjectSession session) => Guard(() => session.SavePoi(set, language is "ja" ? "ja" : "en")));

        // a POI style drawn as the maps draw it (the values as the screen edits them)
        api.MapPost("/project/poi/sample", (PoiSampleRequest request, ProjectSession session) =>
        {
            try { return Results.File(session.PoiSample(request), "image/png"); }
            catch (ProjectException ex) { return Guard<object>(() => throw ex); }
        });

        // a PNG a POI style names, for the screen
        api.MapGet("/project/poi/images", (string? path, ProjectSession session) =>
        {
            try { return Results.File(session.PoiImage(path), "image/png"); }
            catch (ProjectException ex) { return Guard<object>(() => throw ex); }
        });
        // a PNG chosen on the screen (the body; its file name in name), kept beside the POI styles
        api.MapPost("/project/poi/images", async (HttpContext context, string? name, ProjectSession session) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            return Guard(() => session.CopyPoiImage(body.ToArray(), name));
        });
        // points read from a CSV or JSON file chosen on the screen
        api.MapPost("/project/poi/import", (PoiImportRequest request, ProjectSession session) => Guard(() => session.ImportPoi(request)));
    }

    static IResult Guard<T>(Func<T> action)
    {
        try { return Results.Json(action(), AppHost.Json); }
        catch (ProjectException ex)
        {
            int status = ex.Code switch
            {
                "NOT_FOUND" => StatusCodes.Status404NotFound,
                "NO_PROJECT" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Error(status, ex.Code, ex.Message);
        }
        catch (JobException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error(StatusCodes.Status500InternalServerError, "IO", ex.Message);
        }
    }

    static IResult Error(int status, string code, string message) => Results.Json(ApiError.Of(code, message), AppHost.Json, statusCode: status);
}
