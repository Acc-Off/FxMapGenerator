using System.Text.Json;
using System.Text.RegularExpressions;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Projects;
using FxMapGenerator.App.Services;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App.Web;

/// <summary>
/// The road editor of the open project under <c>/api/project/road-editor</c>: whether it can be used and the saved
/// edits (with those not applied to the game's path data), the path data for drawing, a node's or link's values, the
/// ground scan at a point, the bundled edits, saving the edits, reading the game files now, and tiles of the road shapes
/// in a map's colours.
/// </summary>
public static partial class RoadEditorEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/project/road-editor", (ProjectSession session, RoadEditorData data, JobManager jobs) =>
            Guard(() => Status(session.Require(), session, data)));

        api.MapGet("/project/road-editor/paths", (ProjectSession session, RoadEditorData data) =>
            Guard(() => data.PathsOf(session.Require())?.Dto ?? throw new ProjectException("NO_GAME_FILES", "the game files are not read yet")));

        api.MapGet("/project/road-editor/node", (string key, ProjectSession session, RoadEditorData data) => Guard(() =>
        {
            var paths = data.PathsOf(session.Require()) ?? throw new ProjectException("NO_GAME_FILES", "the game files are not read yet");
            if (!paths.NodeText.TryGetValue(key, out var at)) throw new ProjectException("NOT_FOUND", $"no node {key} in the game's path data");
            using var doc = JsonDocument.Parse(paths.Text(at));
            var values = doc.RootElement.Clone();
            uint street = values.TryGetProperty("street", out var s) && s.TryGetUInt32(out var h) ? h : 0;
            return new RoadNodeDto(key, values, street == 0 ? null : paths.Streets.GetValueOrDefault(street));
        }));

        api.MapGet("/project/road-editor/link", (string from, string to, ProjectSession session, RoadEditorData data) => Guard(() =>
        {
            var paths = data.PathsOf(session.Require()) ?? throw new ProjectException("NO_GAME_FILES", "the game files are not read yet");
            var pair = string.CompareOrdinal(from, to) < 0 ? (from, to) : (to, from);
            if (!paths.LinkText.TryGetValue(pair, out var list)) throw new ProjectException("NOT_FOUND", $"no link between {from} and {to} in the game's path data");
            return new RoadLinkDto(from, to, list.Select(at =>
            {
                using var doc = JsonDocument.Parse(paths.Text(at));
                return doc.RootElement.Clone();
            }).ToList());
        }));

        api.MapGet("/project/road-editor/ground", (double x, double y, ProjectSession session, RoadEditorData data) =>
            Guard(() => Ground(session.Require(), data, x, y)));

        // the bundled road edits as the app carries them (the file's form); the screen adds the groups chosen to its edits
        api.MapGet("/project/road-editor/bundled", () => Results.Bytes(RoadEditsFile.BundledBytes, "application/json"));

        api.MapPut("/project/road-editor/edits", async (HttpContext context, ProjectSession session, RoadEditorData data) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            return Guard(() =>
            {
                RoadEditSet set;
                try { set = RoadEditsFile.Parse(body.ToArray(), "the road edits"); }
                catch (RoadEditsException ex) { throw new ProjectException("INVALID", ex.Message); }
                try { session.SaveRoadEdits(set); }
                catch (RoadEditsException ex) { throw new ProjectException("INVALID", ex.Message); }
                return Status(session.Require(), session, data);
            });
        });

        // reads the game files now (GameFilesStage.ReadNow): before a first run has read them (the editor opens while the
        // capture runs), and again after the game or a server's road data changed; 409 LOCKED while a stage of a run of
        // this project that reads the files (the game files step or a map data step) is running
        api.MapPost("/project/road-editor/game-files", async (HttpContext context, ProjectSession session, JobManager jobs, AppOptions app,
            SettingsStore settings, ILoggerFactory loggers) =>
        {
            var log = loggers.CreateLogger("GameFiles");
            return await Task.Run(() => Guard(() =>
            {
                var project = session.Require();
                var busy = jobs.RunningRows(project.FilePath).Where(r => r == "gameFiles" || r.StartsWith("mapData", StringComparison.Ordinal)).ToList();
                if (busy.Count > 0)
                    throw new JobException("LOCKED", $"the game files are read by the running stage(s) {string.Join(", ", busy)}; they can be read again once they are over");
                GameFilesStage.ReadNowResult r;
                try
                {
                    r = GameFilesStage.ReadNow(project, session.State(project), GameFilesDefaults.Of(app, settings.Current),
                        new FixedParallel(Math.Max(1, Environment.ProcessorCount / 2), context.RequestAborted), line => log.LogInformation("{Line}", line), context.RequestAborted);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    throw new JobException("GAME_FILES", ex.Message);
                }
                return new RoadGameFilesDto(r.Changed, r.PathsChanged, r.Areas, r.Nodes, r.Links, r.Seconds);
            }));
        });

        // the road shapes the next run would make with edits not saved yet (the right map follows the edits); a newer
        // request cancels one still being made (then 409 SUPERSEDED)
        api.MapPost("/project/road-editor/preview", async (HttpContext context, ProjectSession session, RoadEditorData data) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            var bytes = body.ToArray();
            try
            {
                return await Task.Run(() => Guard(() =>
                {
                    RoadEditSet set;
                    try { set = RoadEditsFile.Parse(bytes, "the road edits"); }
                    catch (RoadEditsException ex) { throw new ProjectException("INVALID", ex.Message); }
                    var p = data.PreviewOf(session.Require(), set, context.RequestAborted)
                        ?? throw new ProjectException("NO_GAME_FILES", "the game files are not read yet");
                    return new RoadPreviewDto(p.Id, p.Seconds, p.Provisional);
                }));
            }
            catch (OperationCanceledException) { return Error(StatusCodes.Status409Conflict, "SUPERSEDED", "a newer preview was asked for"); }
        });

        // the roads of the road shapes last made (or of a preview: v=preview-<n>), in the colours of one of the project's
        // maps; the address carries the shapes' version, so a tile is drawn again only when they changed
        api.MapGet("/project/road-editor/shapes/{map}/{z:int}/{x:int}/{file}", (string map, int z, int x, string file, ProjectSession session, RoadEditorData data,
            HttpContext context) =>
        {
            var project = session.Current;
            var y = TileFile().Match(file);
            if (project is null || !y.Success || z < 0 || z > RoadTiles.MaxZoom) return Results.NotFound();
            var set = project.Maps.FirstOrDefault(m => m.Id == map && m.IsCellMap);
            if (set is null) return Results.NotFound();
            string v = context.Request.Query["v"].ToString();
            RoadShapesIndex? index;
            string version;
            if (v.StartsWith("preview-", StringComparison.Ordinal))
            {
                index = data.PreviewById(v)?.Index;
                version = v;
            }
            else
            {
                var shapes = data.ShapesOf(project);
                index = shapes?.Index;
                version = shapes?.Version ?? "";
            }
            if (index is null) return Results.NotFound();
            int ty = int.Parse(y.Groups[1].Value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture);
            var style = project.StyleOf(set);
            var tag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{version}-{map}-{style.Digest}-{z}-{x}-{ty}\"");
            context.Response.Headers.CacheControl = "no-cache";
            if (context.Request.Headers.IfNoneMatch.ToString() == tag.ToString())
            {
                context.Response.Headers.ETag = tag.ToString();
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            var png = RoadTiles.Png(index, style, z, x, ty);
            return Results.File(png, "image/png", entityTag: tag);
        });
    }

    [GeneratedRegex(@"^(-?\d+)\.png$")]
    private static partial Regex TileFile();

    static IResult Guard<T>(Func<T> action)
    {
        try { return Results.Json(action(), AppHost.Json); }
        catch (ProjectException ex)
        {
            int status = ex.Code switch
            {
                "NOT_FOUND" => StatusCodes.Status404NotFound,
                "NO_PROJECT" or "NO_GAME_FILES" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Error(status, ex.Code, ex.Message);
        }
        catch (JobException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
    }

    static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, AppHost.Json, statusCode: status);

    /// <summary>What the road editor starts from (see <see cref="RoadEditorDto"/>).</summary>
    internal static RoadEditorDto Status(Project project, ProjectSession session, RoadEditorData data)
    {
        var maps = project.Maps.Where(m => m.IsCellMap).Select(m => m.Id).ToList();
        var paths = data.PathsOf(project);
        string? unavailable = maps.Count == 0 ? "noRoadMaps" : paths is null ? "noGameFiles" : null;
        JsonElement? edits = null;
        string? problem = null;
        IReadOnlyList<NotAppliedEdit> notApplied = [];
        if (project.RoadEditsPath is { } file)
        {
            if (!File.Exists(file)) problem = $"road edits not found: {file}";
            else
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    var set = RoadEditsFile.Parse(bytes, file);
                    using (var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                        edits = doc.RootElement.Clone();
                    if (paths is not null) notApplied = set.NotAppliedTo(paths.Parsed);
                }
                catch (Exception ex) when (ex is RoadEditsException or IOException or JsonException) { problem = ex.Message; }
        }
        var state = session.State(project);
        string? shapesVersion = RoadEditorData.VersionOf(Path.Combine(new Core.State.WorkFolder(project.WorkFolderPath).Data, RoadShapesFile.FileName));
        bool shapesLeft;
        try { shapesLeft = RoadsStage.IsStale(project, state); }
        catch (Exception ex) when (ex is IOException or JsonException) { shapesLeft = true; }
        return new RoadEditorDto(unavailable, project.File.RoadEdits, edits, problem, notApplied, paths?.Version, shapesVersion, shapesLeft, maps);
    }

    static RoadGroundDto Ground(Project project, RoadEditorData data, double x, double y)
    {
        var b = BlockId.At(x, y);
        bool onMap = project.Frame.Contains(b);
        if (!onMap || data.ScanOf(project, b) is not { HasGround: true } s) return new RoadGroundDto(onMap ? b.Name : null, false, null, null, null, null);
        int At(double v0, double v, bool south) => Math.Clamp((int)Math.Round((south ? v0 - v : v - v0) / s.Step), 0, s.N - 1);
        int k = At(s.Y0, y, true) * s.N + At(s.X0, x, false);
        var materials = Materials.Default;
        uint hash = s.Material[k];
        return new RoadGroundDto(b.Name, true, hash == 0 ? "" : materials.Names.GetValueOrDefault(hash, ""), materials.Classes[materials.ClassOf(hash)],
            float.IsNaN(s.HitZ[k]) ? null : Math.Round(s.HitZ[k], 2), float.IsNaN(s.Water[k]) ? null : Math.Round(s.Water[k], 2));
    }
}

/// <summary>
/// <c>GET /api/project/road-editor</c>: why the editor cannot be used (<c>noRoadMaps</c>: no atlas nor road map is made;
/// <c>noGameFiles</c>: the game files are not read yet; null: it can), the project's <c>roadEdits</c> and that file's
/// contents (null without one), a problem reading it, the edits not applied to the game's path data (kind, key, reason),
/// the versions of the path data and of the road shapes last made (null: not made yet), whether the road shapes step is
/// left to do (the shapes shown are then older than the edits saved), and the maps whose colours the road tiles can take.
/// </summary>
public sealed record RoadEditorDto(string? Unavailable, string? File, JsonElement? Edits, string? Problem, IReadOnlyList<NotAppliedEdit> NotApplied,
    string? PathsVersion, string? ShapesVersion, bool ShapesLeft, IReadOnlyList<string> Maps);

/// <summary>
/// <c>POST /api/project/road-editor/preview</c>: the preview's id (the road tiles' <c>v</c>), the seconds it took, and
/// whether it is provisional (blocks of the range have no landcover yet: roads on water are not left out there and paving
/// is the path file's flag, until the capture is over and the landcover made).
/// </summary>
public sealed record RoadPreviewDto(string Id, double Seconds, bool Provisional);

/// <summary>
/// <c>POST /api/project/road-editor/game-files</c>: whether the files read differ from those there were (and whether the
/// path data does), what was read and the seconds it took.
/// </summary>
public sealed record RoadGameFilesDto(bool Changed, bool PathsChanged, int Areas, int Nodes, int Links, double Seconds);

/// <summary>A node's values as the game's path data holds them, and its street name.</summary>
public sealed record RoadNodeDto(string Key, JsonElement Values, StreetNameDto? Street);

/// <summary>A link's records as the game's path data holds them (one per direction the file lists).</summary>
public sealed record RoadLinkDto(string From, string To, IReadOnlyList<JsonElement> Records);

/// <summary>The ground scan at a point: its block, whether it has a ground scan, the material hit (the game's name, "" = none), its class, the height hit and the water surface.</summary>
public sealed record RoadGroundDto(string? Block, bool Scanned, string? Material, string? MaterialClass, double? Height, double? Water);
