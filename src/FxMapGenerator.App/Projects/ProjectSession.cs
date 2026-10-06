using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Services;
using FxMapGenerator.App.Web;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App.Projects;

/// <summary>
/// The project open in the app (one for all browser tabs; the SSE event <c>project</c> tells every tab). Edits go
/// through here: each is checked against the inputs the running job still reads, then saved to the project file.
/// </summary>
public sealed partial class ProjectSession
{
    readonly object _sync = new();
    readonly SettingsStore _settings;
    readonly EventHub _hub;
    readonly JobManager _jobs;
    readonly ILogger<ProjectSession> _logger;
    Project? _project;

    public ProjectSession(SettingsStore settings, EventHub hub, JobManager jobs, ILogger<ProjectSession> logger)
    {
        _settings = settings;
        _hub = hub;
        _jobs = jobs;
        _logger = logger;
    }

    public Project? Current
    {
        get { lock (_sync) return _project; }
    }

    public Project Require() => Current ?? throw new ProjectException("NO_PROJECT", "no project is open");

    /// <summary>The work folder's state: the running job's own when it uses the folder, else read from disk.</summary>
    public StateStore State(Project project) =>
        _jobs.RunningState(project.WorkFolderPath) ?? StateStore.Open(new WorkFolder(project.WorkFolderPath));

    public ProjectDto Open(string path)
    {
        var project = Project.Load(Path.GetFullPath(path));
        lock (_sync) _project = project;
        Remember(project.FilePath);
        _logger.LogInformation("Opened project {Path}", project.FilePath);
        return Publish(project);
    }

    /// <summary>
    /// Creates, saves and opens a new project, its road edits a copy of the bundled ones
    /// (<see cref="Core.RoadEdits.RoadEditsFile.StartFromBundled"/>). <see cref="ProjectException"/> EXISTS / INVALID.
    /// </summary>
    public ProjectDto Create(NewProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path)) throw new ProjectException("INVALID", "the project file is required");
        var path = Path.GetFullPath(request.Path.Trim());
        if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            path = (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path[..^5] : path) + ProjectFile.Extension;
        if (File.Exists(path)) throw new ProjectException("EXISTS", $"{path} already exists");
        var project = Project.Create(path, request.Name);
        if (!string.IsNullOrWhiteSpace(request.WorkFolder))
        {
            var work = Path.GetFullPath(request.WorkFolder.Trim());
            var rel = Path.GetRelativePath(project.Folder, work);
            project.File.WorkFolder = rel.StartsWith("..", StringComparison.Ordinal) ? work : rel;
        }
        if (request.Preset is not null) project.File.Server.Preset = request.Preset;
        project.File.Maps.Satellite = request.Satellite ?? true;
        project.File.Maps.Atlas.Enabled = request.Atlas ?? false;
        project.File.Maps.Roadmap = request.Roadmap ?? false;
        Core.RoadEdits.RoadEditsFile.StartFromBundled(project, request.Language is "ja" ? "ja" : "en");
        project.Save();
        return Open(path);
    }

    public void Close()
    {
        lock (_sync) _project = null;
        _hub.Publish<ProjectDto?>("project", null);
    }

    /// <summary>
    /// Applies an edit and saves. <see cref="JobException"/> LOCKED when a running stage of this project still reads
    /// the input; <see cref="ProjectException"/> for a bad value.
    /// </summary>
    public ProjectDto Update(ProjectEdit edit)
    {
        Project project;
        lock (_sync)
        {
            var open = _project ?? throw new ProjectException("NO_PROJECT", "no project is open");
            project = Project.Load(open.FilePath);        // the file may have been changed from the command line
            var f = project.File;
            if (edit.Name is not null) f.Name = edit.Name.Trim();
            if (edit.Satellite is { } sat && sat != f.Maps.Satellite)
            {
                Check(project, InputKeys.Maps);
                f.Maps.Satellite = sat;
                if (!sat && f.Minimap.Map == MapSet.Satellite.Id)
                {
                    Check(project, InputKeys.Minimap);
                    f.Minimap.Map = null;
                }
            }
            if (edit.MinimapMap is not null)
            {
                Check(project, InputKeys.Minimap);
                f.Minimap.Map = edit.MinimapMap is "" or "none" ? null : edit.MinimapMap;
            }
            if (edit.MinimapOutside is { } outside && outside != f.Minimap.Outside)
            {
                Check(project, InputKeys.Minimap);
                if (!MinimapOutside.IsValid(outside)) throw new ProjectException("INVALID", $"minimap outside '{outside}' (map or transparent)");
                f.Minimap.Outside = outside;
            }
            if (edit.Parallel is { } workers) f.Parallel = workers;
            if (edit.Atlas is { } atlas)
            {
                Check(project, InputKeys.Maps);
                if (atlas.Enabled is { } on) f.Maps.Atlas.Enabled = on;
                if (atlas.Styles is { } styles)
                {
                    // a project style of the list must be one the maps can read
                    foreach (var id in styles.Distinct())
                    {
                        if (!AtlasPresets.IsKnown(id)) throw new ProjectException("INVALID", $"unknown atlas style '{id}'");
                        if (!Core.Styles.MapStyle.BuiltIn.Contains(id) && Core.Styles.ProjectStyles.Find(Core.Styles.ProjectStyles.Folder(project), id) is null)
                            throw new ProjectException("INVALID", $"the project has no readable style '{id}'");
                    }
                    f.Maps.Atlas.Styles = styles.Distinct().ToList();
                }
                if (atlas.Languages is { } languages)
                {
                    // English always, the others as listed, in the app's order
                    if (languages.FirstOrDefault(l => !AtlasPresets.Languages.Contains(l)) is { } bad) throw new ProjectException("INVALID", $"unknown language '{bad}' of the atlas maps");
                    f.Maps.Atlas.Languages = AtlasPresets.Languages.Where(l => l == "en" || languages.Contains(l)).ToList();
                }
                if (f.Minimap.Map is { } mm && mm.StartsWith("atlas-", StringComparison.Ordinal) && !project.Maps.Any(m => m.Id == mm)) f.Minimap.Map = null;
            }
            if (edit.Roadmap is { } road && road != f.Maps.Roadmap)
            {
                Check(project, InputKeys.Maps);
                f.Maps.Roadmap = road;
                if (!road && f.Minimap.Map == MapSet.Roadmap.Id)
                {
                    Check(project, InputKeys.Minimap);
                    f.Minimap.Map = null;
                }
            }
            // the height quality: as asked when it goes with the maps ("" = the default for them); after a change of the maps
            // a chosen one that no longer goes gives way to the default (the satellite map alone and speed, an atlas added)
            var (sat2, cell2) = SurfaceHeights.MapsOf(f);
            if (edit.HeightQuality == "")
            {
                Check(project, InputKeys.Maps);
                f.HeightQuality = null;
            }
            else if (edit.HeightQuality is { } q && q != f.HeightQuality)
            {
                Check(project, InputKeys.Maps);
                if (!SurfaceHeights.IsQuality(q)) throw new ProjectException("INVALID", $"height quality '{q}' (speed, balance or quality)");
                if (SurfaceHeights.Unavailable(q, sat2, cell2) is { } why) throw new ProjectException("INVALID", $"height quality '{q}' {why}");
                f.HeightQuality = q;
            }
            f.HeightQuality = SurfaceHeights.Keep(f.HeightQuality, sat2, cell2);
            if (edit.ServerPreset is not null && edit.ServerPreset != f.Server.Preset)
            {
                Check(project, InputKeys.Server);
                if (ServerPresets.Find(edit.ServerPreset) is null) throw new ProjectException("INVALID", $"server preset '{edit.ServerPreset}'");
                f.Server.Preset = edit.ServerPreset;
            }
            if (edit.StopResources is { } stop)
            {
                Check(project, InputKeys.Server);
                if (stop.Preset) f.Server.StopResources = null;
                else if (stop.List is { } list)
                {
                    var names = list.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                    if (names.FirstOrDefault(n => !ServerPresets.IsResourceName(n)) is { } bad)
                        throw new ProjectException("INVALID", $"'{bad}' is not a resource name");
                    f.Server.StopResources = names;
                }
            }
            if (edit.ServerResources is { } folders)
            {
                Check(project, InputKeys.GameFiles);
                f.GameFiles.ServerResources = folders.Select(p => Relative(project, p.Trim())).Where(p => p.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            if (edit.Console is { } console)
            {
                Check(project, InputKeys.Console);
                if (string.IsNullOrWhiteSpace(console.Host)) throw new ProjectException("INVALID", "the console host is empty");
                if (console.Port is < 1 or > 65535) throw new ProjectException("INVALID", $"console port {console.Port} (1 to 65535)");
                f.Console.Host = console.Host.Trim();
                f.Console.Port = console.Port;
            }
            if (edit.Postals is not null)
            {
                Check(project, InputKeys.Postals);
                var source = edit.Postals.Trim();
                f.Postals = source.Length == 0 ? null : Project.IsWebAddress(source) ? source : Relative(project, source);
            }
            if (edit.ExtraCells is { } cells)
            {
                // a frame that leaves blocks of the range outside is refused: they are taken out of the range first
                var frame = cells.On ? new MapFrame(cells.Top, cells.Bottom, cells.Left, cells.Right) : MapFrame.Standard;
                if (frame != project.Frame)
                {
                    Check(project, InputKeys.Frame);
                    if (frame.Problems().FirstOrDefault() is { } why) throw new ProjectException("INVALID", why);
                    var off = project.Range.Keys.Count(b => !frame.Contains(b));
                    if (off > 0) throw new ProjectException("INVALID", $"{off} block(s) of the range would lie outside the map's frame: take them out of the range first");
                }
                f.Range.ExtraCells = cells.On ? new ExtraCellsSetting { Top = cells.Top, Bottom = cells.Bottom, Left = cells.Left, Right = cells.Right } : null;
            }
            if (edit.CayoPerico is { } cayo && cayo != f.CayoPerico)
            {
                Check(project, InputKeys.CayoPerico);
                f.CayoPerico = cayo;
            }
            if (edit.Range is { } r)
            {
                Check(project, InputKeys.Range);
                if (r.Reset) project.ResetRange();
                if (r.Include is { Count: > 0 }) project.SetInRange(Blocks(r.Include), include: true);
                if (r.Exclude is { Count: > 0 }) project.SetInRange(Blocks(r.Exclude), include: false);
                if (r.Preset is { } preset) project.AddPreset(preset);
            }
            project.Save();
            _project = project;
        }
        return Publish(project);
    }

    /// <summary>
    /// Marks blocks of the range for retake (their data counts as missing until the next visit takes it again) or takes
    /// the mark off; <see cref="RetakeRequest.All"/> takes it off every block. The mark lives in the work folder's state,
    /// written through the running job's own state when a job of this app uses the folder. Refused while a running stage
    /// reads the range (the visit), and while another run (another window or the command line) holds the work folder.
    /// </summary>
    public ProjectDto Retake(RetakeRequest request)
    {
        Project project;
        lock (_sync)
        {
            project = _project ?? throw new ProjectException("NO_PROJECT", "no project is open");
            Check(project, InputKeys.Range);
            var folder = new WorkFolder(project.WorkFolderPath);
            var state = _jobs.RunningState(project.WorkFolderPath);
            if (state is null && WorkFolderLock.Holder(folder) is { } holder)
                throw new JobException("BUSY", $"another run is using the work folder {folder.Root} ({holder})");
            state ??= StateStore.Open(folder);
            if (request.All) state.MarkForRetake(state.MarkedForRetake(), retake: false);
            else
            {
                var blocks = Blocks(request.Blocks ?? []).ToList();
                var range = project.Range;
                foreach (var b in blocks)
                    if (!range.ContainsKey(b)) throw new ProjectException("INVALID", $"{b.Name} is not in the range");
                if (blocks.Count > 0) state.MarkForRetake(blocks, request.Retake);
            }
        }
        return Publish(project);
    }

    /// <summary>
    /// Saves the road edits: to the project's road edits file, or, when the project has none yet, to a new
    /// <c>road-edits.json</c> beside the project file (<c>road-edits-2.json</c>, ... when that name is taken) which the
    /// project's <c>roadEdits</c> then names. <see cref="JobException"/> LOCKED while a running stage of this project reads
    /// the edits; <see cref="Core.RoadEdits.RoadEditsException"/> when they are not usable (nothing is written).
    /// </summary>
    public ProjectDto SaveRoadEdits(Core.RoadEdits.RoadEditSet edits)
    {
        Project project;
        lock (_sync)
        {
            var open = _project ?? throw new ProjectException("NO_PROJECT", "no project is open");
            project = Project.Load(open.FilePath);
            Check(project, InputKeys.RoadEdits);
            if (project.RoadEditsPath is { } path) Core.RoadEdits.RoadEditsFile.Write(path, edits);
            else
            {
                var name = Core.RoadEdits.RoadEditsFile.FreeName(project.Folder);
                Core.RoadEdits.RoadEditsFile.Write(Path.Combine(project.Folder, name), edits);
                project.File.RoadEdits = name;
                project.Save();
            }
            _project = project;
        }
        return Publish(project);
    }

    /// <summary>Keeps the choices of an export in the project file (the folder relative to it when inside its folder).</summary>
    public ProjectDto SaveExportChoices(ExportOptions options, string? resourceName)
    {
        Project project;
        lock (_sync)
        {
            var open = _project ?? throw new ProjectException("NO_PROJECT", "no project is open");
            project = Project.Load(open.FilePath);
            var e = project.File.Export;
            var rel = Path.GetRelativePath(project.Folder, options.Folder);
            e.Folder = rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? options.Folder : rel;
            e.Tiles = options.Maps.Count > 0;
            if (options.Maps.Count > 0) e.Maps = options.Maps.ToList();
            e.Zip = options.Zip;
            e.BaseUrl = options.BaseUrl;
            e.Minimap = options.Minimap;
            e.MaxZoom = options.MaxZoom;
            // the layered files: how they are written is kept, which maps is not (they are written only when asked for)
            if (options.Editable is { } editable)
            {
                e.Editable.Zoom = editable.Zoom;
                if (editable.Written.Count > 0) e.Editable.Formats = editable.Written.ToList();
            }
            project.Save();
            _project = project;
        }
        return Publish(project);
    }

    /// <summary>
    /// Keeps the picture of a conversion and the map it was made from in the project file (the path relative to the
    /// project file when inside its folder).
    /// </summary>
    public ProjectDto SaveConvertChoices(string picture, string map)
    {
        Project project;
        lock (_sync)
        {
            var open = _project ?? throw new ProjectException("NO_PROJECT", "no project is open");
            project = Project.Load(open.FilePath);
            project.File.Export.Picture.File = Relative(project, picture);
            project.File.Export.Picture.Map = map;
            project.Save();
            _project = project;
        }
        return Publish(project);
    }

    void Check(Project project, string key)
    {
        if (_jobs.LockedBy(project.FilePath, key) is { } stages)
            throw new JobException("LOCKED", $"'{key}' is read by the running stage(s) {string.Join(", ", stages)}; it can be changed once they are over");
    }

    /// <summary>A path inside the project file's folder relative to it (the project folder can move); others as they are.</summary>
    static string Relative(Project project, string path)
    {
        if (path.Length == 0 || !Path.IsPathRooted(path)) return path;
        var rel = Path.GetRelativePath(project.Folder, path);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel;
    }

    static IEnumerable<BlockId> Blocks(IEnumerable<string> names) =>
        names.Select(n => BlockId.TryParse(n, out var b) ? b : throw new ProjectException("INVALID", $"'{n}' is not a block on the map"));

    ProjectDto Publish(Project project)
    {
        var dto = ProjectDto.Of(project);
        _hub.Publish("project", dto);
        return dto;
    }

    void Remember(string path)
    {
        var s = _settings.Current.Clone();
        s.RecentProjects.RemoveAll(p => SamePath(p, path));
        s.RecentProjects.Insert(0, path);
        _settings.Save(s);
        _hub.Publish("settings", _settings.Current);
    }

    public void Forget(string path)
    {
        var s = _settings.Current.Clone();
        s.RecentProjects.RemoveAll(p => SamePath(p, path));
        _settings.Save(s);
        _hub.Publish("settings", _settings.Current);
    }

    /// <summary>The same file however it is written (slashes, case, relative parts).</summary>
    static bool SamePath(string a, string b)
    {
        try { return JobManager.SamePath(a, b); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>The recent project files with their names; missing or unreadable ones say so.</summary>
    public IReadOnlyList<RecentProjectDto> Recent() => _settings.Current.RecentProjects.Select(path =>
    {
        if (!File.Exists(path)) return new RecentProjectDto(path, Project.NameFromPath(path), false, null);
        try { return new RecentProjectDto(path, Project.Load(path).File.Name, true, null); }
        catch (ProjectException ex) { return new RecentProjectDto(path, Project.NameFromPath(path), true, ex.Code); }
    }).ToList();
}

/// <param name="Path">The new <c>.fxmapgen.json</c> (the extension is added when missing).</param>
/// <param name="WorkFolder">Empty: the project file's folder.</param>
/// <param name="Language">The screens' language (<c>en</c>, <c>ja</c>): the names the bundled road edits' groups take in the project.</param>
public sealed record NewProjectRequest(string? Path, string? Name, string? WorkFolder, string? Preset, bool? Satellite, bool? Atlas = null, bool? Roadmap = null,
    string? Language = null);

/// <summary>A change of the open project; null fields stay as they are.</summary>
/// <param name="MinimapMap">A map id, or <c>none</c>.</param>
/// <param name="MinimapOutside">The minimap's blocks outside the range: <c>map</c> (painted) or <c>transparent</c>.</param>
/// <param name="Parallel">Default workers of new runs.</param>
/// <param name="ServerResources">The server's own resources for the road data (folders, zips or ynd files), the whole list in its order.</param>
/// <param name="Postals">Where the postal codes come from: an http(s) address or a file; "" = nearest-postal's table.</param>
/// <param name="ExtraCells">The cells added around the standard map (<see cref="ExtraCellsEdit"/>).</param>
/// <param name="CayoPerico">Whether the game files step reads the Cayo Perico island's road files (and the labels place the name of the island's zone).</param>
public sealed record ProjectEdit(string? Name, bool? Satellite, AtlasEdit? Atlas, string? MinimapMap, int? Parallel, string? ServerPreset, RangeEdit? Range,
    bool? Roadmap = null, string? HeightQuality = null, StopResourcesEdit? StopResources = null, IReadOnlyList<string>? ServerResources = null,
    ConsoleEdit? Console = null, string? Postals = null, string? MinimapOutside = null, ExtraCellsEdit? ExtraCells = null, bool? CayoPerico = null);

/// <summary>
/// The areas outside the standard map: <see cref="On"/> with the cells added on each side (all 0 keep the standard frame),
/// or off (none added: the project's <c>range.extraCells</c> goes back to null).
/// </summary>
public sealed record ExtraCellsEdit(bool On, int Top = 0, int Bottom = 0, int Left = 0, int Right = 0);

/// <summary>The resources stopped during the visit: the whole list, or back to the preset's (<see cref="Preset"/>).</summary>
public sealed record StopResourcesEdit(IReadOnlyList<string>? List, bool Preset);

/// <summary>Where the game's console listens.</summary>
public sealed record ConsoleEdit(string Host, int Port);

/// <summary>The atlas switched on or off, and / or its whole style list (style ids), and / or its languages (English always).</summary>
public sealed record AtlasEdit(bool? Enabled, IReadOnlyList<string>? Styles, IReadOnlyList<string>? Languages = null);

/// <summary>
/// Blocks put into or taken out of the range; <see cref="Reset"/> first goes back to the default range; <see cref="Preset"/>
/// puts a range preset's blocks in (after the others; the frame must hold them, see <see cref="Project.AddPreset"/>).
/// </summary>
public sealed record RangeEdit(IReadOnlyList<string>? Include, IReadOnlyList<string>? Exclude, bool Reset, string? Preset = null);

/// <summary>Blocks of the range marked for retake (<see cref="Retake"/> true) or taken off it; <see cref="All"/>: every mark off.</summary>
public sealed record RetakeRequest(IReadOnlyList<string>? Blocks, bool Retake = true, bool All = false);

/// <param name="StopResources">The resources the visit stops: the project's list, or its preset's.</param>
/// <param name="PresetStopResources">The preset's list (what "back to the preset" gives).</param>
/// <param name="Presets">The bundled server presets (id and name).</param>
/// <param name="OwnStyles">The project's own styles the maps can use (readable ones: id and names).</param>
/// <param name="Frame">The project's map frame (the standard frame with the cells it adds).</param>
/// <param name="FrameNeeded">The fewest cells on each side of the standard map that keep the range's blocks inside the frame.</param>
/// <param name="RangeOutside">Blocks of the range outside the standard map.</param>
/// <param name="RangePresets">The range presets the plan map offers, with what adding each would add.</param>
public sealed record ProjectDto(string Path, string Name, string WorkFolder, ProjectFile File, IReadOnlyList<string> Maps,
    int RangeBlocks, int RangeLand, int RangeWater, int Cells, IReadOnlyList<string> StopResources, IReadOnlyList<string> PresetStopResources,
    IReadOnlyList<PresetDto> Presets, IReadOnlyList<OwnStyleDto> OwnStyles, MapFrameDto Frame, SidesDto FrameNeeded, int RangeOutside,
    IReadOnlyList<RangePresetDto> RangePresets)
{
    public static ProjectDto Of(Project p)
    {
        var range = p.Range;
        var needed = MapFrame.Holding(range.Keys);
        return new ProjectDto(p.FilePath, p.File.Name, p.WorkFolderPath, p.File, p.Maps.Select(m => m.Id).ToList(), range.Count,
            range.Values.Count(c => c == BlockClass.Land), range.Values.Count(c => c == BlockClass.Water), CellPlan.For(range.Keys).Count,
            p.StopResources, ServerPresets.Find(p.File.Server.Preset)?.StopResources ?? [], ServerPresets.All.Select(x => new PresetDto(x.Id, x.Name)).ToList(),
            Core.Styles.ProjectStyles.List(p).Where(e => e.Style is not null).Select(e => new OwnStyleDto(e.Id, e.Style!.Name)).ToList(), MapFrameDto.Of(p.Frame),
            new SidesDto(needed.CellsTop, needed.CellsBottom, needed.CellsLeft, needed.CellsRight), range.Keys.Count(b => !MapFrame.Standard.Contains(b)),
            Core.World.RangePresets.All.Select(x => RangePresetDto.Of(x, range, p.Frame)).ToList());
    }
}

/// <summary>
/// A range preset for the plan map: its blocks by class, those of them not in the range yet (by their class once added)
/// and, of those, the ones outside the project's frame; the cells the frame needs on each side of the standard map to hold
/// them all; the west, north, east and south edges of its blocks (game metres).
/// </summary>
public sealed record RangePresetDto(string Id, int Land, int Water, int MissingLand, int MissingWater, int MissingOutside, SidesDto Needs,
    double West, double North, double East, double South)
{
    public static RangePresetDto Of(RangePreset preset, IReadOnlyDictionary<BlockId, BlockClass> range, MapFrame frame)
    {
        var missing = preset.Blocks.Where(kv => !range.ContainsKey(kv.Key)).ToList();
        var f = preset.Frame;
        var keys = preset.Blocks.Keys;
        var nw = new BlockId(keys.Min(b => b.Bx), keys.Min(b => b.By)).Rect;
        var se = new BlockId(keys.Max(b => b.Bx), keys.Max(b => b.By)).Rect;
        return new RangePresetDto(preset.Id, preset.Blocks.Values.Count(c => c == BlockClass.Land), preset.Blocks.Values.Count(c => c == BlockClass.Water),
            missing.Count(kv => kv.Value == BlockClass.Land), missing.Count(kv => kv.Value == BlockClass.Water), missing.Count(kv => !frame.Contains(kv.Key)),
            new SidesDto(f.CellsTop, f.CellsBottom, f.CellsLeft, f.CellsRight), nw.X0, nw.Y0, se.X1, se.Y1);
    }
}

/// <summary>
/// A project's map frame for the screens: its first block column and row (negative when cells are added to the left or
/// above), its blocks across and down, and its west, north, east and south edges (game metres).
/// </summary>
public sealed record MapFrameDto(int Bx0, int By0, int Cols, int Rows, double West, double North, double East, double South)
{
    public static MapFrameDto Of(MapFrame f) => new(f.Bx0, f.By0, f.BlocksX, f.BlocksY, f.X0, f.Y0, f.X1, f.Y1);
}

/// <summary>Cells on each side of the standard map.</summary>
public sealed record SidesDto(int Top, int Bottom, int Left, int Right);

/// <summary>One of the project's own styles: its id (as the maps name it) and its name.</summary>
public sealed record OwnStyleDto(string Id, string Name);

public sealed record PresetDto(string Id, string Name);

/// <param name="Problem">Why it cannot be opened (a project error code), or null.</param>
public sealed record RecentProjectDto(string Path, string Name, bool Exists, string? Problem);
