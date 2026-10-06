using System.Text.RegularExpressions;
using FxMapGenerator.App.Game;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Projects;
using FxMapGenerator.App.Services;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FxMapGenerator.App.Web;

/// <summary>
/// The open project under <c>/api/project</c> (open, new, close, edit; its blocks, to-do table, checks and tiles)
/// and the recent list under <c>/api/projects</c>. Changes also go out as the SSE event <c>project</c>.
/// </summary>
public static partial class ProjectEndpoints
{
    /// <summary>Disk use per block of a capture (shot + camera + height grid) and of the satellite tiles, measured.</summary>
    const double CaptureBytesPerBlock = 4.6e6, SatelliteTileBytesPerBlock = 1.9e6, DiskMargin = 1e9;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/project", (ProjectSession session) =>
            session.Current is { } p ? Results.Json(ProjectDto.Of(p), AppHost.Json) : Results.NoContent());

        api.MapPost("/project/open", (OpenProjectRequest? request, ProjectSession session) =>
        {
            if (string.IsNullOrWhiteSpace(request?.Path)) return Error(StatusCodes.Status400BadRequest, "BAD_REQUEST", "path is required");
            return Guard(() => session.Open(request.Path));
        });

        api.MapPost("/project/new", (NewProjectRequest request, ProjectSession session) => Guard(() => session.Create(request)));

        api.MapPost("/project/close", (ProjectSession session) =>
        {
            session.Close();
            return Results.Json(new { ok = true }, AppHost.Json);
        });

        api.MapMethods("/project", new[] { "PATCH" }, (ProjectEdit edit, ProjectSession session) => Guard(() => session.Update(edit)));

        api.MapGet("/project/blocks", (ProjectSession session) => Guard(() => Blocks(session)));

        api.MapPost("/project/retake", (RetakeRequest request, ProjectSession session) => Guard(() => session.Retake(request)));

        api.MapGet("/project/plan", (int? workers, ProjectSession session, IBuildStages stages, JobManager jobs) => Guard(() =>
        {
            var project = session.Require();
            var state = session.State(project);
            var build = stages.For(project, state, new BuildStages.Options());
            return new PlanDto(Planner.Build(project, state, workers ?? project.File.Parallel, Environment.ProcessorCount), build.Stages.SelectMany(s => s.Rows).ToList(),
                build.Stages.ToDictionary(s => s.Row, s => s.CountReady(project, state)), build.Notes, jobs.AtStart(project.FilePath));
        }));

        api.MapGet("/project/checks", (ProjectSession session, RenderSettingsFile render, AppOptions app, SettingsStore settings) =>
            Guard(() => Checks(session, render, GameFilesDefaults.Of(app, settings.Current))));

        // The open project's tiles. The browser asks again every time (no-cache; unchanged tiles cost a 304), because the
        // same address serves whichever project is open and tiles are rewritten while a run goes on; a missing tile is
        // not kept at all (it may be written a moment later).
        api.MapGet("/project/tiles/{set}/{z:int}/{x:int}/{file}", (string set, int z, int x, string file, ProjectSession session, HttpContext context) =>
            Tile(session, context, set, z, x, file, before: false));

        // the map as it was before: the tiles kept when they were written over since the last export, the others as they are
        api.MapGet("/project/before-tiles/{set}/{z:int}/{x:int}/{file}", (string set, int z, int x, string file, ProjectSession session, HttpContext context) =>
            Tile(session, context, set, z, x, file, before: true));

        // what changed of each map since its tiles were kept (the comparison of the map view)
        api.MapGet("/project/before", (ProjectSession session) => Guard(() =>
        {
            var project = session.Require();
            var folder = new WorkFolder(project.WorkFolderPath);
            return new BeforeDto(project.Maps.Select(m => BeforeTiles.Of(folder, m.Id)).OfType<ChangedMap>().ToList());
        }));

        api.MapGet("/projects/recent", (ProjectSession session) => Results.Json(session.Recent(), AppHost.Json));

        api.MapGet("/project/point", (double x, double y, ProjectSession session) => Guard(() => Point(session, x, y)));

        api.MapPost("/projects/recent/remove", (OpenProjectRequest? request, ProjectSession session) =>
        {
            if (!string.IsNullOrWhiteSpace(request?.Path)) session.Forget(request.Path);
            return Results.Json(session.Recent(), AppHost.Json);
        });

        api.MapGet("/jobs/current/units", (JobManager jobs) => jobs.Units() is { } units ? Results.Json(units, AppHost.Json) : Results.NoContent());

        // fetch (or read) the postal codes into the work folder now, as the labels step would; the check tells how it went
        api.MapPost("/project/postals/fetch", async (ProjectSession session, HttpContext context) =>
        {
            if (session.Current is not { } project) return Error(StatusCodes.Status409Conflict, "NO_PROJECT", "no project is open");
            try { await Task.Run(() => LabelsStage.EnsurePostals(project, _ => { }, context.RequestAborted), context.RequestAborted); }
            catch (LabelsException ex) { return Error(StatusCodes.Status422UnprocessableEntity, "POSTALS", ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                return Error(StatusCodes.Status422UnprocessableEntity, "POSTALS", ex.Message);
            }
            return Results.Json(PostalsCheck(project), AppHost.Json);
        });

        api.MapPost("/dialog/file", async (FileDialogRequest? request, FolderDialog dialog, HttpContext context) =>
        {
            var picked = await dialog.PickFileAsync(request?.Initial, request?.Title, request?.Save ?? false, request?.Kind, context.RequestAborted);
            return Results.Json(new FolderDialogResult(picked), AppHost.Json);
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
                "EXISTS" or "NO_PROJECT" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Error(status, ex.Code, ex.Message);
        }
        catch (JobException ex) { return Error(StatusCodes.Status409Conflict, ex.Code, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Error(StatusCodes.Status500InternalServerError, "IO", ex.Message);
        }
    }

    static IResult Error(int status, string code, string message) => Results.Json(ApiError.Of(code, message), AppHost.Json, statusCode: status);

    static BlocksDto Blocks(ProjectSession session)
    {
        var project = session.Require();
        var state = session.State(project);
        var range = project.Range;
        var photoItems = Core.Satellite.SurfaceHeights.PhotoItems(project);
        var frame = project.Frame;
        int n = frame.BlocksX * frame.BlocksY;
        var inRange = new char[n];
        var inDefault = new char[n];
        var retake = new char[n];
        var ortho = new char[n];
        var items = new int[n];
        for (int by = frame.By0; by < frame.By1; by++)
            for (int bx = frame.Bx0; bx < frame.Bx1; bx++)
            {
                var b = new BlockId(bx, by);
                int i = (by - frame.By0) * frame.BlocksX + bx - frame.Bx0;
                inRange[i] = range.TryGetValue(b, out var c) ? Letter(c) : '.';
                inDefault[i] = DefaultRange.Blocks.TryGetValue(b, out var d) ? Letter(d) : '.';
                retake[i] = state.IsMarkedForRetake(b) ? '1' : '0';
                int mask = 0;
                foreach (var item in BlockItems.All)
                    if (state.Has(b, item)) mask |= 1 << (int)item;
                items[i] = mask;
                ortho[i] = Core.Satellite.OrthoStage.Captured(state, b, photoItems) && !Core.Satellite.OrthoStage.Stale(state, b, photoItems) ? '1' : '0';
            }
        return new BlocksDto(frame.BlocksX, frame.BlocksY, new string(inRange), new string(inDefault), items, new string(retake), new string(ortho),
            CellPlan.For(range.Keys).Select(c => c.Id.Name).ToList(), frame.Bx0, frame.By0, SatelliteSea(project, state));
        static char Letter(BlockClass c) => c == BlockClass.Land ? 'L' : 'W';
    }

    static readonly object SeaGate = new();
    static (string Folder, DateTime Done, string Colour)? _sea;

    /// <summary>
    /// The colour the satellite map's lower zooms painted the open sea with (<c>#rrggbb</c>; measured as that step and the
    /// exported viewer's background measure it), for the screens to put behind the satellite map; null until that step
    /// has run. Measured once per run of the step (16 tiles are read).
    /// </summary>
    static string? SatelliteSea(Project project, StateStore state)
    {
        if (!project.Maps.Contains(MapSet.Satellite) || state.StageDone(StageKeys.LowZoom, MapSet.Satellite.Id) is not { } done) return null;
        var folder = new WorkFolder(project.WorkFolderPath);
        lock (SeaGate)
        {
            if (_sea is { } kept && kept.Folder == folder.Root && kept.Done == done) return kept.Colour;
            var (r, g, b) = Core.Satellite.SatelliteLowZoomStage.SeaColour(project.Range, new Core.Satellite.TileStore(folder.Tiles(MapSet.Satellite.Id)));
            var colour = $"#{r:x2}{g:x2}{b:x2}";
            _sea = (folder.Root, done, colour);
            return colour;
        }
    }

    /// <summary>
    /// What the chosen maps need: only the rows that apply. The game rows come from the latest pre-check
    /// (null until there is one, and again once the program has stopped the capture resource after it), the graphics
    /// settings from FiveM's settings file; what cannot be checked yet says so (ok = null).
    /// </summary>
    static IReadOnlyList<CheckDto> Checks(ProjectSession session, RenderSettingsFile render, GameFilesLocation.Defaults gameFiles)
    {
        var project = session.Require();
        var state = session.State(project);
        var table = Planner.Build(project, state, project.File.Parallel, Environment.ProcessorCount);
        TodoRow Row(string id) => table.Rows.SelectMany(r => r.Children.Prepend(r)).Single(r => r.Id == id);
        var list = new List<CheckDto>();
        var visit = Row("visit");
        if (visit.Needed && visit.Remaining > 0)
        {
            // the exe's capture resource is on the server: the app wrote it there, it answered, or the user said so
            var placed = ResourcePlacement.Read(new WorkFolder(project.WorkFolderPath));
            list.Add(new CheckDto("capture", placed?.Version == CaptureResource.Version, new Dictionary<string, string>
            {
                ["version"] = CaptureResource.Version,
                ["placedVersion"] = placed?.Version ?? "",
                ["how"] = placed?.How ?? "",
                ["at"] = placed?.AtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["folder"] = placed?.Folder ?? "",
            }));
            // a check made before the program stopped the capture resource counts as not made
            var report = PrecheckReport.LatestFor(new WorkFolder(project.WorkFolderPath)) is { ResourceStoppedUtc: null } latest ? latest : null;
            bool? Passed(params string[] ids) => report is null ? null
                : ids.All(id => report.Items.Any(i => i.Id == id && i.Ok == true)) ? true : false;
            var at = report?.AtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "";
            // a visit without shots (scans and height grids only) needs no window, no test shot
            bool shots = Row("visit.shot").Remaining > 0;
            list.Add(new CheckDto("game", shots ? Passed("console", "resource", "window") : Passed("console", "resource"), new Dictionary<string, string>
            {
                ["console"] = $"{project.File.Console.Host}:{project.File.Console.Port}",
                ["checkedAt"] = at,
            }));
            if (Row("visit.shot").Remaining > 0)
            {
                bool? ok = null;
                var values = new Dictionary<string, string> { ["path"] = render.Path };
                try
                {
                    var status = render.Read();
                    ok = status.Exists ? status.Differences.Count == 0 : null;
                    values["differences"] = status.Differences.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    values["fiveMRunning"] = status.FiveMRunning ? "1" : "0";
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { ok = false; values["error"] = ex.Message; }
                list.Add(new CheckDto("render", ok, values));
            }
            list.Add(new CheckDto("preset", shots ? Passed("resources", "testShot", "environment") : Passed("resources"), new Dictionary<string, string>
            {
                ["preset"] = project.File.Server.Preset,
                ["checkedAt"] = at,
            }));
        }
        bool cellMaps = project.Maps.Any(m => m.IsCellMap);
        // the game's files are read for an atlas or road map (the roads, the names) and for the minimap resource (the
        // interior maps, the island map)
        if (cellMaps || project.File.Minimap.Map is not null)
        {
            // the GTA V folder and the RPF keys (values: where each came from, the key files missing in the folders tried)
            var where = GameFilesLocation.Resolve(project, gameFiles);
            var values = new Dictionary<string, string>
            {
                ["gta"] = where.GtaFolder ?? "",
                ["gtaSource"] = where.GtaSource ?? "",
                ["gtaOk"] = where.GtaFound ? "1" : "0",
                ["keys"] = where.KeysFolder ?? "",
                ["keysSource"] = where.KeysSource ?? "",
                ["keysMissing"] = string.Join("; ", where.KeysTried.Select(t => $"{t.Folder}: {string.Join(", ", t.Missing)}")),
            };
            values["gtaProblem"] = where.GtaFound ? "" : where.GtaFolder is null ? "notFound" : "notGta";
            list.Add(new CheckDto("gameFiles", where.GtaFound && where.KeysFound, values));
        }
        if (cellMaps)
        {
            var where = GameFilesLocation.Resolve(project, gameFiles);
            // the server's own path files (optional): every entry has to be there
            var missing = where.ServerResources.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
            list.Add(new CheckDto("serverResources", missing.Count == 0, new Dictionary<string, string>
            {
                ["count"] = where.ServerResources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["entries"] = string.Join("\n", where.ServerResources),
                ["missing"] = string.Join("\n", missing),
            }));
        }
        if (project.Maps.Any(m => m.Kind == MapKind.Atlas))
        {
            list.Add(PostalsCheck(project));
            list.Add(FontsCheck(project));
        }
        double need =Row("visit.shot").Remaining * CaptureBytesPerBlock + Row("ortho").Remaining * SatelliteTileBytesPerBlock + DiskMargin;
        var root = Path.GetPathRoot(project.WorkFolderPath);
        long free = root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        list.Add(new CheckDto("disk", free >= need, new Dictionary<string, string>
        {
            ["free"] = (free / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            ["need"] = (need / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            ["folder"] = project.WorkFolderPath,
        }));
        return list;
    }

    /// <summary>
    /// Where the atlas maps' postal codes come from (values: <c>source</c>, <c>kind</c> default / url / file, <c>count</c>,
    /// <c>fetched</c>, <c>state</c>): a file is read and must be a postal code list; an address is fine once the work folder
    /// has its copy, and before that it is only "not fetched yet" (the labels step fetches it; the screen can fetch it now).
    /// </summary>
    internal static CheckDto PostalsCheck(Project project)
    {
        var source = LabelsStage.PostalSource(project);
        bool web = Project.IsWebAddress(source);
        var values = new Dictionary<string, string>
        {
            ["source"] = source,
            ["kind"] = project.File.Postals is null ? "default" : web ? "url" : "file",
        };
        if (LabelsStage.CurrentCopy(project) is { } copy)
        {
            values["state"] = "copied";
            values["count"] = copy.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["fetched"] = copy.FetchedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            return new CheckDto("postals", true, values);
        }
        if (web)
        {
            values["state"] = "notFetched";
            return new CheckDto("postals", true, values);
        }
        if (!File.Exists(source))
        {
            values["state"] = "notFound";
            return new CheckDto("postals", false, values);
        }
        try
        {
            values["count"] = PostalCodes.Parse(File.ReadAllText(source), source).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["state"] = "file";
            return new CheckDto("postals", true, values);
        }
        catch (Exception ex) when (ex is LabelsException or System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            values["state"] = "bad";
            values["error"] = ex.Message;
            return new CheckDto("postals", false, values);
        }
    }

    /// <summary>
    /// The fonts the atlas maps' labels use, per style and language (values: <c>fonts</c> = "font\tstyle\tlanguage" lines,
    /// <c>missing</c> = the fonts not installed, one per line). A missing font is drawn with the default font instead.
    /// </summary>
    internal static CheckDto FontsCheck(Project project)
    {
        var used = new List<(string Font, string Map)>();
        foreach (var m in project.Maps.Where(m => m.Kind == MapKind.Atlas))
            if (project.StyleOf(m).Labels is { } labels)
                used.Add((labels.Font(m.Language!), m.Id));
        var fonts = used.Select(u => u.Font).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = fonts.Where(f => !SkiaTextMetrics.IsInstalled(f)).ToList();
        return new CheckDto("fonts", missing.Count == 0, new Dictionary<string, string>
        {
            ["fonts"] = string.Join("\n", used.Select(u => $"{u.Font}\t{u.Map}")),
            ["missing"] = string.Join("\n", missing),
        });
    }

    /// <summary>
    /// What the scans and the landcover say at a point: the material the ground scan hit (and its class, the height, the
    /// water), the zone and the street of the road scan (with the Japanese names of the game files when read), whether
    /// the point is on a road, and the landcover's ground class and building. Null parts: no scan or landcover there.
    /// </summary>
    static PointDto Point(ProjectSession session, double x, double y)
    {
        var project = session.Require();
        var folder = new WorkFolder(project.WorkFolderPath);
        var b = BlockId.At(x, y);
        if (!project.Frame.Contains(b)) return new PointDto(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        string? material = null, materialClass = null, street = null, streetJa = null, zone = null, zoneLabel = null, zoneJa = null, landcover = null;
        double? height = null, water = null;
        bool? onRoad = null, building = null;
        bool scanned = File.Exists(folder.ScanFile(b));
        if (scanned)
        {
            var s = Core.Scan.ScanFile.Read(folder.ScanFile(b));
            var materials = Core.Scan.Materials.Default;
            Dictionary<string, Dictionary<string, string>>? names = null;
            var namesPath = Path.Combine(folder.Game, Core.GameFiles.GameFilesOutput.Names);
            if (File.Exists(namesPath))
                try { names = ReadNames(namesPath); } catch (System.Text.Json.JsonException) { }
            int At(double step, int n, double v0, double v, bool south) => Math.Clamp((int)Math.Round((south ? v0 - v : v - v0) / step), 0, n - 1);
            if (s.HasGround)
            {
                int k = At(s.Step, s.N, s.Y0, y, true) * s.N + At(s.Step, s.N, s.X0, x, false);
                uint hash = s.Material[k];
                material = hash == 0 ? "" : materials.Names.GetValueOrDefault(hash, "");
                materialClass = materials.Classes[materials.ClassOf(hash)];
                height = float.IsNaN(s.HitZ[k]) ? null : Math.Round(s.HitZ[k], 1);
                water = float.IsNaN(s.Water[k]) ? null : Math.Round(s.Water[k], 1);
            }
            if (s.HasRoads)
            {
                int k = At(s.RoadStep, s.RoadN, s.Y0, y, true) * s.RoadN + At(s.RoadStep, s.RoadN, s.X0, x, false);
                uint h = s.Street[k];
                street = h == 0 ? "" : names?.GetValueOrDefault("s" + h)?.GetValueOrDefault("en") ?? s.StreetNames.GetValueOrDefault(h, "");
                streetJa = h == 0 ? null : names?.GetValueOrDefault("s" + h)?.GetValueOrDefault("ja");
                int zi = s.Zone[k];
                if (zi >= 1 && zi <= s.Zones.Count)
                {
                    (zone, zoneLabel) = s.Zones[zi - 1];
                    zoneJa = names?.GetValueOrDefault("z" + zone)?.GetValueOrDefault("ja");
                    if (names?.GetValueOrDefault("z" + zone)?.GetValueOrDefault("en") is { } en) zoneLabel = en;
                }
                int ko = At(s.OnRoadStep, s.OnRoadN, s.Y0, y, true) * s.OnRoadN + At(s.OnRoadStep, s.OnRoadN, s.X0, x, false);
                onRoad = s.OnRoad[ko] == 1;
            }
        }
        var lc = Core.Landcover.LandcoverFile.PathOf(folder.Data, b);
        if (File.Exists(lc))
        {
            var f = Core.Grids.GridFile.Load(lc);
            double x0 = f.Meta["x0"]!.GetValue<double>(), y0 = f.Meta["y0"]!.GetValue<double>(), step = f.Meta["step"]!.GetValue<double>();
            var cls = f.Get<byte>("landcover");
            int r = Math.Clamp((int)Math.Round((y0 - y) / step), 0, cls.Height - 1), c = Math.Clamp((int)Math.Round((x - x0) / step), 0, cls.Width - 1);
            var classNames = f.Meta["classes"]!.AsArray().Select(n => (string)n!).ToList();
            landcover = cls[r, c] < classNames.Count ? classNames[cls[r, c]] : null;
            building = f.Get<bool>("buildings")[r, c];
        }
        return new PointDto(b.Name, scanned, material, materialClass, height, water, zone, zoneLabel, zoneJa, street, streetJa, onRoad, landcover, building, null);
    }

    /// <summary>The names file as "s&lt;hash&gt;" / "z&lt;code&gt;" -> language -> name.</summary>
    static Dictionary<string, Dictionary<string, string>> ReadNames(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var o = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (prefix, key) in new[] { ("s", "streets"), ("z", "zones") })
            if (doc.RootElement.TryGetProperty(key, out var list))
                foreach (var e in list.EnumerateObject())
                    o[prefix + e.Name] = e.Value.EnumerateObject().Where(v => v.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        .ToDictionary(v => v.Name, v => v.Value.GetString()!);
        return o;
    }

    [GeneratedRegex(@"^(-?\d+)\.png$")]
    private static partial Regex TileFile();

    readonly record struct TileStorePath(string Root)
    {
        public string Of(int z, int x, int y) => Path.Combine(Root, z.ToString(), x.ToString(), y + ".png");
    }

    /// <summary>
    /// A tile of the open project; <paramref name="before"/>: the one kept from before it was written over when there is
    /// one. The browser asks again every time (no-cache; unchanged tiles cost a 304), because the same address serves
    /// whichever project is open and tiles are rewritten while a run goes on; a missing tile is not kept at all.
    /// </summary>
    static IResult Tile(ProjectSession session, HttpContext context, string set, int z, int x, string file, bool before)
    {
        var project = session.Current;
        var y = TileFile().Match(file);
        FileInfo? tile = null;
        if (project is not null && MapSet.TryParse(set, out _) && y.Success && z >= 0 && z <= 8)
        {
            var folder = new WorkFolder(project.WorkFolderPath);
            int ty = int.Parse(y.Groups[1].Value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture);
            if (before) tile = new FileInfo(new TileStorePath(folder.Before(set)).Of(z, x, ty));
            if (tile is null || !tile.Exists) tile = new FileInfo(new TileStorePath(folder.Tiles(set)).Of(z, x, ty));
        }
        if (tile is null || !tile.Exists)
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.NotFound();
        }
        context.Response.Headers.CacheControl = "no-cache";
        var tag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{tile.LastWriteTimeUtc.Ticks:x}-{tile.Length:x}{(before ? "-b" : "")}\"");
        return Results.File(tile.FullName, "image/png", lastModified: tile.LastWriteTimeUtc, entityTag: tag);
    }
}

public sealed record OpenProjectRequest(string? Path);

/// <summary><c>Kind</c>: the files the dialog lists (<see cref="FolderDialog.RoadData"/>, <see cref="FolderDialog.Picture"/>; a project file without it).</summary>
public sealed record FileDialogRequest(string? Initial, string? Title, bool? Save, string? Kind = null);

/// <summary>
/// All blocks of the map, row by row from the north-west (index = by * cols + bx). Range and default range: L land,
/// W water, . outside. Items: bit 0 shot, 1 height grid, 2 scan ground, 3 scan roads, 4 scan canopy. Retake: 1 marked.
/// Ortho: 1 when the block's satellite tiles are up to date. Cells: the drawing cells of the range. SatelliteSea: the
/// colour the satellite map's open sea is painted with (<c>#rrggbb</c>), null until its lower zooms are made.
/// </summary>
public sealed record BlocksDto(int Cols, int Rows, string Range, string DefaultRange, IReadOnlyList<int> Items, string Retake, string Ortho, IReadOnlyList<string> Cells,
    int Bx0, int By0, string? SatelliteSea = null);

/// <summary>The maps whose tiles changed since they were kept (the last export), with the places (see <see cref="BeforeTiles"/>).</summary>
public sealed record BeforeDto(IReadOnlyList<ChangedMap> Maps);

/// <param name="Runnable">To-do rows a run does (the rows of its stages and the parts they take on).</param>
/// <param name="Ready">Per runnable row: the units a run could do right now with the data at hand.</param>
/// <param name="AtStart">What was left per row when the running (or last) run of this project started; null without one.</param>
public sealed record PlanDto(TodoTable Table, IReadOnlyList<string> Runnable, IReadOnlyDictionary<string, int> Ready, IReadOnlyList<string> Notes, RunStart? AtStart = null);

/// <param name="Ok">True / false, or null when it cannot be checked in this version.</param>
public sealed record CheckDto(string Id, bool? Ok, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// <c>GET /api/project/point</c>: the block (null off the map), whether it has a scan, the ground scan's material (the game's
/// name, "" = no hit), its class, hit height and water surface, the road scan's zone (code, English name, Japanese name),
/// street (English, Japanese; "" = none) and on-road sample, the landcover's ground class and building.
/// </summary>
public sealed record PointDto(string? Block, bool? Scanned, string? Material, string? MaterialClass, double? Height, double? Water,
    string? Zone, string? ZoneName, string? ZoneJa, string? Street, string? StreetJa, bool? OnRoad, string? Landcover, bool? Building, string? Note);
