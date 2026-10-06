using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Projects;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Preview;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Styles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FxMapGenerator.App.Web;

/// <summary>
/// The style editor: the table of a style's values (<c>/api/styles/schema</c>), the shared styles, and the open project's
/// styles under <c>/api/project/styles</c> (the bundled atlas styles, read only, and the project's own: read, make from
/// another, rename, delete, export, import, copy into the shared styles).
/// </summary>
public static class StyleEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/styles/schema", () => Results.Bytes(StyleSchema.Bytes, "application/json"));

        api.MapGet("/styles/shared", (ProjectSession session) => Guard(session.SharedStyles));

        // the font families of this PC, for the labels' fonts
        api.MapGet("/fonts", () => Results.Json(InstalledFonts.Names(), AppHost.Json));

        // the style editor's sample land: made for the preview the first time it is asked for (with the open project's workers)
        api.MapGet("/styles/sample", (SampleLand sample) => Results.Json(sample.Status(), AppHost.Json));
        api.MapPost("/styles/sample", (SampleLand sample, ProjectSession session) =>
            Results.Json(sample.Start(session.Current?.File.Parallel ?? Environment.ProcessorCount / 2), AppHost.Json));
        api.MapGet("/styles/sample/overview.png", (SampleLand sample) => sample.Overview() is { } png ? Results.File(png, "image/png") : Results.NotFound());

        // the preview on the open project's own data: its state, the blocks it can draw, its places
        api.MapGet("/project/styles/preview", (ProjectSession session) => Guard(session.PreviewStatus));
        api.MapPut("/project/styles/preview/places", (List<PreviewPlace> places, ProjectSession session) => Guard(() => session.SavePreviewPlaces(places)));

        // a window of the sample land or of the open project (source) drawn with a style (the values as the screen edits
        // them) on the open project's workers: its z8 tiles, read by id; a newer request for the same side stops one still
        // being drawn
        api.MapPost("/project/styles/preview", async (PreviewRequest request, HttpContext context, SampleLand sample, ProjectSession session, PreviewDrawings drawings) =>
        {
            MapStyle style;
            try { style = MapStyle.Parse(request.Values.ToJsonString(), "the style"); }
            catch (StyleException ex) { return Error(StatusCodes.Status400BadRequest, "INVALID", ex.Message); }
            if (NotReady(request.Source, sample, session) is { } notReady) return notReady;
            var side = request.Side ?? "";
            var token = Previews.Take(side, context.RequestAborted);
            try
            {
                var preview = PreviewOf(request.Source, sample, session);
                var window = StylePreview.Window.Around(request.X, request.Y, SizeOf(request.Size));
                var drawing = await Task.Run(() => preview.Draw(style, LanguageOf(request.Language), window, new FixedParallel(Workers(session), token), token), token);
                var seconds = preview.Parts.LastOrDefault().Seconds;
                return Results.Json(new PreviewDto(drawings.Keep(side, drawing), [window.X0, window.Y0, window.X1, window.Y1], seconds), AppHost.Json);
            }
            catch (OperationCanceledException) { return Error(StatusCodes.Status409Conflict, "SUPERSEDED", "a newer preview was asked for"); }
            catch (Exception ex) when (ex is ProjectException or PreviewException or IOException) { return Error(StatusCodes.Status409Conflict, "PREVIEW", ex.Message); }
        });

        api.MapGet("/project/styles/preview/tiles/{id}/{x:int}/{file}", (string id, int x, string file, PreviewDrawings drawings, HttpContext context) =>
        {
            if (!file.EndsWith(".png", StringComparison.Ordinal) || !int.TryParse(file.AsSpan(0, file.Length - 4), out var y) || drawings.Tile(id, x, y) is not { } png)
                return Results.NotFound();
            // a drawing's id is never used again: its tiles stay the same
            context.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(png, "image/png");
        });

        // where the colour of a point of a window drawn with a style comes from
        api.MapPost("/project/styles/preview/pick", async (PickRequest request, SampleLand sample, ProjectSession session, HttpContext context) =>
        {
            MapStyle style;
            try { style = MapStyle.Parse(request.Values.ToJsonString(), "the style"); }
            catch (StyleException ex) { return Error(StatusCodes.Status400BadRequest, "INVALID", ex.Message); }
            if (NotReady(request.Source, sample, session) is { } notReady) return notReady;
            try
            {
                var preview = PreviewOf(request.Source, sample, session);
                var window = StylePreview.Window.Around(request.Cx, request.Cy, SizeOf(request.Size));
                var token = context.RequestAborted;
                var pick = await Task.Run(() => preview.Pick(style, LanguageOf(request.Language), window, request.X, request.Y, new FixedParallel(Workers(session), token), token), token);
                return Results.Json(PickDto.Of(pick), AppHost.Json);
            }
            catch (Exception ex) when (ex is ProjectException or PreviewException or IOException) { return Error(StatusCodes.Status409Conflict, "PREVIEW", ex.Message); }
        });

        api.MapGet("/project/styles", (ProjectSession session) => Guard(session.Styles));

        api.MapPost("/project/styles", (NewStyleRequest request, ProjectSession session) => Guard(() => session.CreateStyle(request)));

        // a style file (the form the app writes, or a whole style); ?id= gives it another id, ?language= the screens' language (a whole style's name in it)
        api.MapPost("/project/styles/import", async (HttpContext context, string? id, string? language, ProjectSession session) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            return Guard(() => session.ImportStyle(body.ToArray(), id, language));
        });

        api.MapGet("/project/styles/{id}", (string id, ProjectSession session) => Guard(() => session.Style(id)));

        // the whole style as the screen edits it; the file keeps the values that differ from its base
        api.MapPut("/project/styles/{id}", async (string id, HttpContext context, ProjectSession session) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            return Guard(() =>
            {
                JsonObject values;
                try { values = JsonNode.Parse(body.ToArray())?.AsObject() ?? throw new ProjectException("INVALID", "the style is empty"); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new ProjectException("INVALID", $"the style is not a JSON object ({ex.Message})"); }
                return session.SaveStyle(id, values);
            });
        });

        api.MapPut("/project/styles/{id}/name", (string id, StyleNameEdit edit, ProjectSession session) => Guard(() => session.RenameStyle(id, edit.Name)));

        api.MapDelete("/project/styles/{id}", (string id, ProjectSession session) => Guard(() =>
        {
            session.DeleteStyle(id);
            return session.Styles();
        }));

        api.MapPost("/project/styles/{id}/shared", (string id, bool? overwrite, ProjectSession session) => Guard(() =>
        {
            session.SaveSharedStyle(id, overwrite == true);
            return session.SharedStyles();
        }));

        // the style's file as it is, to save (a bundled style's as the app carries it)
        api.MapGet("/project/styles/{id}/file", (string id, ProjectSession session) =>
        {
            try
            {
                var (bytes, name) = session.StyleFile(id);
                return Results.File(bytes, "application/json", name);
            }
            catch (ProjectException ex) { return Guard<object>(() => throw ex); }
        });
    }

    static IResult Guard<T>(Func<T> action)
    {
        try { return Results.Json(action(), AppHost.Json); }
        catch (ProjectException ex)
        {
            int status = ex.Code switch
            {
                "NOT_FOUND" => StatusCodes.Status404NotFound,
                "NO_PROJECT" or "ID_TAKEN" or "IN_USE" or "EXISTS" or "NOT_READY" => StatusCodes.Status409Conflict,
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

    /// <summary>Whether a preview draws the open project's data (source <c>project</c>) rather than the sample land's.</summary>
    static bool OfProject(string? source) => source == "project";

    /// <summary>NOT_READY (409) while the source cannot be drawn yet (the sample land is being made, the project lacks its data), else null.</summary>
    static IResult? NotReady(string? source, SampleLand sample, ProjectSession session)
    {
        if (!OfProject(source)) return sample.Ready ? null : Error(StatusCodes.Status409Conflict, "NOT_READY", "the sample land is not made yet");
        if (session.Current is not { } project) return Error(StatusCodes.Status409Conflict, "NO_PROJECT", "no project is open");
        return StylePreview.Waiting(project) is { } why ? Error(StatusCodes.Status409Conflict, "NOT_READY", $"the project has no {why} yet") : null;
    }

    static StylePreview PreviewOf(string? source, SampleLand sample, ProjectSession session) => OfProject(source) ? session.Preview() : sample.Preview();

    /// <summary>The window's side the preview offers nearest to the one asked for (500 m when none).</summary>
    static double SizeOf(double? size) => size is { } s ? StylePreview.Sizes.MinBy(v => Math.Abs(v - s)) : StylePreview.Sizes[0];

    static string LanguageOf(string? language) => language is "ja" ? "ja" : "en";

    /// <summary>The open project's workers (its parallel setting), else half the CPUs.</summary>
    static int Workers(ProjectSession session) => Math.Max(1, session.Current?.File.Parallel ?? Environment.ProcessorCount / 2);

    /// <summary>The preview being drawn per side of the screen (left, right): a newer request cancels the older one.</summary>
    static class Previews
    {
        static readonly Dictionary<string, CancellationTokenSource> Running = new();

        public static CancellationToken Take(string side, CancellationToken aborted)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            lock (Running)
            {
                if (Running.TryGetValue(side, out var old)) old.Cancel();
                Running[side] = cts;
            }
            return cts.Token;
        }
    }
}

/// <summary>
/// <c>POST /api/project/styles/preview</c>: the style's values, the window's middle (m) and side (m, one of the sizes
/// the preview offers), the labels' language, the side of the screen it is for and what it draws (<c>sample</c>, the
/// default: the sample land; <c>project</c>: the open project's data).
/// </summary>
public sealed record PreviewRequest(JsonObject Values, double X, double Y, double? Size, string? Language, string? Side, string? Source = null);

/// <summary>A window drawn: the id its tiles are read by, its edges (west, north, east, south, m) and how long it took (s).</summary>
public sealed record PreviewDto(string Id, double[] Bounds, double Seconds);

/// <summary><c>POST /api/project/styles/preview/pick</c>: the style's values, the point (m), the window it is shown in (its middle and side), the labels' language and the source (as the preview's).</summary>
public sealed record PickRequest(JsonObject Values, double X, double Y, double Cx, double Cy, double? Size, string? Language, string? Source = null);

/// <summary>
/// Where the colour of a point comes from: the pixel's colour, the steps that paint it from the top (see
/// <see cref="PreviewPick"/>), the point (its zone and the game's names for it, its kind of ground, water, building) and
/// what the ground picture is made of there.
/// </summary>
public sealed record PickDto(string Color, IReadOnlyList<PickStepDto> Steps, PickPointDto Point, IReadOnlyList<PickPartDto> Ground)
{
    public static PickDto Of(PreviewPick p) => new(p.Color.ToString(),
        p.Steps.Select(s => new PickStepDto(s.Tag.Kind, s.Tag.Color?.ToString(), s.Cover, s.Factor, s.Tag.Class, s.Tag.Paint, s.Tag.Band, s.Tag.Bed, s.Tag.Text, s.Rule, s.Region)).ToList(),
        new PickPointDto(p.Point.Zone, p.Point.ZoneEn, p.Point.ZoneJa, p.Point.Ground, p.Point.Water, p.Point.Building),
        p.Ground.Select(g => new PickPartDto(g.Kind, g.Id, g.Kind == "trees" ? null : g.Color.ToString(), g.Share)).ToList());
}

public sealed record PickStepDto(string Kind, string? Color, double Cover, double Factor, int Class, string? Paint, int Band, string? Bed, string? Text, string? Rule, string? Region);

public sealed record PickPointDto(string Zone, string? ZoneEn, string? ZoneJa, string Ground, bool Water, bool Building);

public sealed record PickPartDto(string Kind, string Id, string? Color, double Share);
