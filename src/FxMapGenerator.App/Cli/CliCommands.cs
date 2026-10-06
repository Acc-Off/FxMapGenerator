using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FxMapGenerator.App.Game;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Services;
using FxMapGenerator.App.Web;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Import;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.App.Cli;

/// <summary>
/// Subcommands that run without the UI:
/// <c>new</c> (create a project file), <c>import</c> (bring earlier captures / scans into the work folder),
/// <c>plan</c> (print the to-do table), <c>build</c> (run what is left), <c>precheck</c> (check the game before a visit),
/// <c>export</c> (write the web tiles and the minimap resource), <c>convert</c> (make web tiles and a minimap resource
/// from an edited picture), <c>retake</c> (mark blocks to be taken again).
/// </summary>
public static class CliCommands
{
    public static readonly string[] Names = { "new", "import", "plan", "build", "precheck", "export", "convert", "retake" };

    public static string Usage => """
          FxMapGenerator new <file.fxmapgen.json> [--name <name>] [--maps satellite,atlas-postalcodemap,atlas-postalcodemap-ja,roadmap,...] [--work <folder>]
          FxMapGenerator import <file.fxmapgen.json> [--capture <folder>] [--scan <folder>]
          FxMapGenerator plan <file.fxmapgen.json> [--workers <n> | --level full|strong|normal|light] [--processors <n>] [--json]
          FxMapGenerator build <file.fxmapgen.json> [--workers <n> | --level full|strong|normal|light] [--scale <qx>,<qy>] [--recalibrate] [--no-game] [--gta <folder>] [--keys <folder>]
          FxMapGenerator precheck <file.fxmapgen.json>
          FxMapGenerator export <file.fxmapgen.json> [--out <folder>] [--maps satellite,...|none] [--zip] [--base-url <url>] [--max-zoom 6|7|8] [--no-minimap] [--resource-name <name>] [--editable <map>,...] [--editable-zoom 6|7] [--editable-format psd,svg] [--workers <n>] [--gta <folder>] [--keys <folder>]
          FxMapGenerator convert <file.fxmapgen.json> --picture <file.png> [--map <map>] [--out <folder>] [--no-tiles] [--no-minimap] [--resource-name <name>] [--base-url <url>] [--workers <n>] [--gta <folder>] [--keys <folder>]
          FxMapGenerator retake <file.fxmapgen.json> [--blocks z8_<x>_<y>,... | --rect <west>,<south>,<east>,<north> | --all] [--clear]
        """;

    public static bool IsCommand(string arg) => Names.Contains(arg);

    /// <summary>Exit codes: 0 done, 64 wrong arguments, 65 bad project or input (or the work folder is in use); build: 1 failures, 130 stopped.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var (positional, options) = Split(args.Skip(1));
            if (positional.Count != 1) throw new UsageException($"{args[0]} needs one project file");
            return args[0] switch
            {
                "new" => New(positional[0], options, output),
                "import" => Import(positional[0], options, output),
                "build" => Build(positional[0], options, output),
                "precheck" => RunPrecheck(positional[0], options, output),
                "export" => Export(positional[0], options, output),
                "convert" => Convert(positional[0], options, output),
                "retake" => Retake(positional[0], options, output),
                _ => Plan(positional[0], options, output),
            };
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine("usage:");
            error.WriteLine(Usage);
            return 64;
        }
        catch (Exception ex) when (ex is ProjectException or JobException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return 65;
        }
    }

    static int New(string path, Dictionary<string, string?> o, TextWriter output)
    {
        Allow(o, "name", "maps", "work");
        if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) path += ProjectFile.Extension;
        if (File.Exists(path)) throw new ProjectException("EXISTS", $"{path} already exists");
        var project = Project.Create(path, o.GetValueOrDefault("name"));
        if (o.TryGetValue("work", out var work) && !string.IsNullOrWhiteSpace(work)) project.File.WorkFolder = work;
        if (o.TryGetValue("maps", out var maps) && maps is not null)
        {
            project.File.Maps = new MapsSetting { Satellite = false };
            var atlas = project.File.Maps.Atlas;
            foreach (var entry in maps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // atlas-<style> (its maps in English) or a map id (atlas-<style>-ja: Japanese maps too); every atlas style
                // is made in every language; a new project has the bundled styles only (its own are made in the style editor)
                var id = entry.StartsWith("atlas-", StringComparison.Ordinal) && entry.Split('-').Length == 2 ? entry + "-en" : entry;
                if (!MapSet.TryParse(id, out var set) || (set.Kind == MapKind.Atlas && !AtlasPresets.BuiltIn.Contains(set.Preset!)))
                    throw new UsageException($"unknown map '{entry}' (satellite, roadmap, atlas-<{string.Join('|', AtlasPresets.BuiltIn)}>, with -ja: Japanese maps too)");
                switch (set.Kind)
                {
                    case MapKind.Satellite: project.File.Maps.Satellite = true; break;
                    case MapKind.Roadmap: project.File.Maps.Roadmap = true; break;
                    default:
                        if (!atlas.Enabled) (atlas.Enabled, atlas.Styles) = (true, []);
                        if (!atlas.Styles.Contains(set.Preset!)) atlas.Styles.Add(set.Preset!);
                        if (!atlas.Languages.Contains(set.Language!)) atlas.Languages = AtlasPresets.Languages.Where(l => l == set.Language || atlas.Languages.Contains(l)).ToList();
                        break;
                }
            }
        }
        // the bundled road edits' groups are named in English (the command line has no screen language)
        RoadEditsFile.StartFromBundled(project, "en");
        project.Save();
        output.WriteLine($"created {project.FilePath}");
        output.WriteLine($"  maps: {string.Join(", ", project.Maps)}   work folder: {project.WorkFolderPath}   road edits: {project.File.RoadEdits}");
        return 0;
    }

    static int Import(string path, Dictionary<string, string?> o, TextWriter output)
    {
        Allow(o, "capture", "scan");
        if (!o.ContainsKey("capture") && !o.ContainsKey("scan")) throw new UsageException("import needs --capture and / or --scan");
        var project = Project.Load(path);
        var folder = new WorkFolder(project.WorkFolderPath);
        var state = StateStore.Open(folder);
        foreach (var (kind, source) in new[] { ("capture", o.GetValueOrDefault("capture")), ("scan", o.GetValueOrDefault("scan")) })
        {
            if (source is null) continue;
            if (!Directory.Exists(source)) throw new ProjectException("NOT_FOUND", $"{kind} folder not found: {source}");
            var sw = Stopwatch.StartNew();
            var r = kind == "capture"
                ? Importer.ImportCapture(folder, state, source, output.WriteLine)
                : Importer.ImportScan(folder, state, source, output.WriteLine);
            var items = string.Join(", ", r.Items.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key.Key()} {kv.Value}"));
            output.WriteLine($"{kind}: {r.Blocks} blocks ({items}); {r.Files} files copied ({r.Bytes / 1048576.0:n0} MB), {r.Unchanged} already there; {sw.Elapsed.TotalSeconds:n1} s");
            if (r.Skipped.Count > 0) output.WriteLine($"  skipped {r.Skipped.Count}: {string.Join(", ", r.Skipped.Take(8))}{(r.Skipped.Count > 8 ? ", ..." : "")}");
        }
        output.WriteLine($"work folder: {folder.Root}");
        return 0;
    }

    static int Plan(string path, Dictionary<string, string?> o, TextWriter output)
    {
        Allow(o, "workers", "level", "processors", "json");
        var project = Project.Load(path);
        int processors = o.TryGetValue("processors", out var p) ? int.TryParse(p, out var n) && n > 0 ? n : throw new UsageException("--processors needs a positive number") : Environment.ProcessorCount;
        int workers = Workers(o, processors) ?? project.File.Parallel;
        var state = StateStore.Open(new WorkFolder(project.WorkFolderPath));
        var table = Planner.Build(project, state, workers, processors);
        if (o.ContainsKey("json"))
        {
            output.WriteLine(JsonSerializer.Serialize(table, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return 0;
        }
        output.WriteLine($"{project.File.Name}  ({project.FilePath})");
        output.WriteLine($"maps: {string.Join(", ", project.Maps)}");
        output.WriteLine($"range: {table.RangeBlocks} blocks ({table.RangeLand} land, {table.RangeWater} water), {table.Cells} cells");
        output.WriteLine($"parallel: {table.Workers} workers of {table.Processors} processors (stages without the game)");
        output.WriteLine();
        output.WriteLine($"{"",-2}{"",-30}{"left",-36}time");
        foreach (var row in table.Rows) Print(row, 0, output);
        output.WriteLine();
        output.WriteLine($"  {"Total",-30}{"",-36}{Durations.Range(table.Low, table.High)}  (in the game {Durations.Range(table.GameLow, table.GameHigh)})");
        return 0;
    }

    /// <summary>
    /// Runs the stages left as a job (the same as the app's run button): a run folder under the work folder's logs/,
    /// progress every few seconds. Ctrl+C once stops after the running units, twice stops now.
    /// Exit codes: 0 done, 1 units failed or a stage failed, 130 stopped.
    /// </summary>
    static int Build(string path, Dictionary<string, string?> o, TextWriter output)
    {
        Allow(o, "workers", "level", "scale", "recalibrate", "no-game", "gta", "keys");
        int? workers = Workers(o, Environment.ProcessorCount);
        (double, double)? scale = null;
        if (o.TryGetValue("scale", out var q))
        {
            var parts = q!.Split(',');
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var qx)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var qy) || qx <= 0 || qy <= 0)
                throw new UsageException("--scale needs two positive numbers, e.g. 0.99755,0.98853");
            scale = (qx, qy);
        }
        var gameFiles = GameFilesOf(o);
        var sync = TextWriter.Synchronized(output);
        var runner = JobRunner.Create(new JobSetup
        {
            ProjectPath = path,
            Workers = workers,
            Stages = (p, s) => BuildStages.For(p, s, new BuildStages.Options(scale, o.ContainsKey("recalibrate"),
                o.ContainsKey("no-game") ? null : new LocalGame(), new VisitStage.Options { ResourceVersion = AppVersion.Value }, gameFiles)),
            Version = AppVersion.Value,
            Log = line => sync.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
        });
        return RunToEnd(runner, sync);
    }

    /// <summary>The GTA V and key folders for a project that leaves them empty: the options, else the app's settings.</summary>
    static GameFilesLocation.Defaults GameFilesOf(Dictionary<string, string?> o)
    {
        var app = new AppOptions
        {
            GtaFolderOverride = o.TryGetValue("gta", out var gta) ? Path.GetFullPath(gta!) : null,
            KeysFolderOverride = o.TryGetValue("keys", out var keys) ? Path.GetFullPath(keys!) : null,
        };
        return GameFilesDefaults.Of(app, SettingsOf(app));
    }

    static AppSettings SettingsOf(AppOptions app)
    {
        var settings = new SettingsStore(app.DataDirectory, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
        settings.Load();
        return settings.Current;
    }

    /// <summary>
    /// The layered files of an export from <c>--editable</c> (the maps; without it none are written, whatever the
    /// project keeps), <c>--editable-zoom</c> and <c>--editable-format</c> (else as the project last wrote them). The
    /// layers are named in the language of the app's settings (English unless it is <c>ja</c>).
    /// </summary>
    static EditableChoice? EditableOf(Project project, Dictionary<string, string?> o)
    {
        if (!o.TryGetValue("editable", out var maps))
        {
            if (o.ContainsKey("editable-zoom") || o.ContainsKey("editable-format")) throw new UsageException("--editable-zoom and --editable-format go with --editable <map>,...");
            return null;
        }
        if (string.IsNullOrWhiteSpace(maps)) throw new UsageException("--editable takes the maps to write in layers (satellite, roadmap, atlas-postalcodemap-en, ...)");
        var kept = project.File.Export.Editable;
        int zoom = kept.Zoom;
        if (o.TryGetValue("editable-zoom", out var z) && !int.TryParse(z, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out zoom))
            throw new UsageException($"--editable-zoom takes a zoom level ({EditableChoice.DefaultZoom} or {EditableChoice.FinestZoom}), not '{z}'");
        var formats = o.TryGetValue("editable-format", out var f)
            ? (f ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : kept.Formats.ToList();
        return new EditableChoice(maps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), zoom, formats,
            SettingsOf(new AppOptions()).Language == "ja" ? "ja" : "en");
    }

    /// <summary>
    /// Writes an export (see <see cref="ExportStage"/>) as a run: the choices not given come from the project's last export
    /// (or the defaults: every map with tiles, a folder, the minimap when the project has one). What the minimap resource
    /// takes from the game's files (the interior maps, and the island map of a project that reads Cayo Perico's roads)
    /// comes from the game of the GTA V and key folders (<c>--gta</c>, <c>--keys</c>, else the app's settings). The layered files for editing are written for the maps of <c>--editable</c> only
    /// (<see cref="EditableOf"/>).
    /// Exit codes: 0 written, 65 the choices cannot be exported, 1 failed, 130 stopped.
    /// </summary>
    static int Export(string path, Dictionary<string, string?> o, TextWriter output)
    {
        Allow(o, "out", "maps", "zip", "base-url", "no-minimap", "resource-name", "max-zoom", "workers", "gta", "keys", "editable", "editable-zoom", "editable-format");
        var project = Project.Load(path);
        var state = StateStore.Open(new WorkFolder(project.WorkFolderPath));
        var game = MinimapGameFiles.Of(GameFilesOf(o));
        var inventory = ExportInventory.Of(project, state, game);
        var saved = ExportOptions.FromProject(project, inventory.Maps.Select(m => m.Map));
        var maps = o.TryGetValue("maps", out var list)
            ? list == "none" ? new List<string>() : list!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : saved.Maps.ToList();
        int maxZoom = saved.MaxZoom;
        if (o.TryGetValue("max-zoom", out var mz) && !int.TryParse(mz, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out maxZoom))
            throw new UsageException($"--max-zoom takes a zoom level ({ExportOptions.LowestMaxZoom}-8), not '{mz}'");
        var options = new ExportOptions(o.TryGetValue("out", out var outDir) ? Path.GetFullPath(outDir!) : saved.Folder, maps,
            o.ContainsKey("zip") || saved.Zip, o.GetValueOrDefault("base-url") ?? saved.BaseUrl, !o.ContainsKey("no-minimap") && project.File.Minimap.Map is not null,
            o.GetValueOrDefault("resource-name"), maxZoom, EditableOf(project, o));
        var problems = inventory.Check(options);
        foreach (var p in problems) output.WriteLine($"cannot export: {p.Message}");
        if (problems.Count > 0) return 65;
        var sync = TextWriter.Synchronized(output);
        var runner = JobRunner.Create(new JobSetup
        {
            ProjectPath = path,
            Workers = Workers(o, Environment.ProcessorCount),
            Stages = (_, _) => new BuildPlan(ExportStages.For(options, AppVersion.Value, game), []),
            Version = AppVersion.Value,
            Log = line => sync.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
        });
        return RunToEnd(runner, sync);
    }

    /// <summary>
    /// Converts an edited picture (see <see cref="ConvertStages"/>) as a run: <c>--picture</c> is the PNG file (the
    /// project's whole frame at zoom 6 or 7), <c>--map</c> the map it was made from (without it: the map its file name
    /// tells, else the minimap's, else the project's first; <see cref="EditedPicture.DefaultMap"/>). The web tiles go into
    /// <c>web-edited/&lt;name&gt;/</c> of the output folder and the minimap resource beside it, unless <c>--no-tiles</c> or
    /// <c>--no-minimap</c> leaves one out. The folder and the address of the lb-phone example come from the project's last
    /// export unless given; the project file is not changed.
    /// Exit codes: 0 written, 65 the picture cannot be converted as asked, 1 failed, 130 stopped.
    /// </summary>
    static int Convert(string path, Dictionary<string, string?> o, TextWriter output)
    {
        Allow(o, "picture", "map", "out", "no-tiles", "no-minimap", "resource-name", "base-url", "workers", "gta", "keys");
        if (!o.TryGetValue("picture", out var picture) || string.IsNullOrWhiteSpace(picture)) throw new UsageException("convert needs --picture <file.png>");
        var project = Project.Load(path);
        var game = MinimapGameFiles.Of(GameFilesOf(o));
        var file = Path.GetFullPath(picture);
        var map = o.GetValueOrDefault("map") ?? EditedPicture.DefaultMap(project, file) ?? "";
        var saved = project.File.Export.BaseUrl;
        var options = new ConvertOptions(o.TryGetValue("out", out var outDir) ? Path.GetFullPath(outDir!) : ExportOptions.DefaultFolder(project), file, map,
            !o.ContainsKey("no-tiles"), !o.ContainsKey("no-minimap"), o.GetValueOrDefault("resource-name"),
            o.GetValueOrDefault("base-url") ?? (string.IsNullOrWhiteSpace(saved) ? null : saved.Trim()));
        var problems = ConvertStages.Check(project, options, game: game);
        foreach (var p in problems) output.WriteLine($"cannot convert: {p.Message}");
        if (problems.Count > 0) return 65;
        output.WriteLine($"picture: {EditedPicture.Sentence(EditedPicture.Look(file, project.Frame), project.Frame)}; made from the {map} map");
        var sync = TextWriter.Synchronized(output);
        var runner = JobRunner.Create(new JobSetup
        {
            ProjectPath = path,
            Workers = Workers(o, Environment.ProcessorCount),
            Stages = (_, _) => new BuildPlan(ConvertStages.For(options, AppVersion.Value, game), []),
            Version = AppVersion.Value,
            Log = line => sync.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
        });
        return RunToEnd(runner, sync);
    }

    /// <summary>
    /// Marks blocks of the range for retake (the next build's visit takes all their items again) or, with <c>--clear</c>,
    /// takes the mark off; without blocks it lists the marked ones. <c>--blocks</c> names blocks (each must be in the
    /// range); <c>--rect</c> takes the blocks of the range a rectangle in game metres touches; <c>--all</c> (with
    /// <c>--clear</c>) every marked block. Refused while a run uses the work folder. Exit codes: 0, 64, 65.
    /// </summary>
    static int Retake(string path, Dictionary<string, string?> o, TextWriter output)
    {
        Allow(o, "blocks", "rect", "all", "clear");
        int given = new[] { "blocks", "rect", "all" }.Count(o.ContainsKey);
        if (given > 1) throw new UsageException("retake takes one of --blocks, --rect and --all");
        bool clear = o.ContainsKey("clear");
        if (o.ContainsKey("all") && !clear) throw new UsageException("--all goes with --clear (every mark off)");
        if (clear && given == 0) throw new UsageException("--clear needs --blocks, --rect or --all");
        var project = Project.Load(path);
        var folder = new WorkFolder(project.WorkFolderPath);
        using var held = WorkFolderLock.TryAcquire(folder, out var holder)
            ?? throw new JobException("BUSY", $"another run is using the work folder {folder.Root} ({holder})");
        var state = StateStore.Open(folder);
        var range = project.Range;
        List<BlockId> blocks;
        if (o.TryGetValue("blocks", out var names))
        {
            blocks = names!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => BlockId.TryParse(n, out var b) ? b : throw new UsageException($"'{n}' is not a block on the map")).ToList();
            foreach (var b in blocks)
                if (!range.ContainsKey(b)) throw new ProjectException("INVALID", $"{b.Name} is not in the range");
        }
        else if (o.TryGetValue("rect", out var rect))
        {
            var v = rect!.Split(',').Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
            if (v.Length != 4 || v.Any(double.IsNaN)) throw new UsageException($"--rect takes <west>,<south>,<east>,<north> in metres, not '{rect}'");
            blocks = project.Frame.Touching(v[0], v[1], v[2], v[3]).Where(range.ContainsKey).ToList();
        }
        else if (o.ContainsKey("all")) blocks = state.MarkedForRetake().ToList();
        else
        {
            var marked = state.MarkedForRetake();
            output.WriteLine(marked.Count == 0 ? "no block is marked for retake" : $"{marked.Count} blocks marked for retake: {string.Join(", ", marked.Select(b => b.Name))}");
            return 0;
        }
        state.MarkForRetake(blocks, retake: !clear);
        output.WriteLine($"{(clear ? "unmarked" : "marked")} {blocks.Count} blocks{(blocks.Count > 0 ? ": " + string.Join(", ", blocks.Select(b => b.Name)) : "")}");
        output.WriteLine($"{state.MarkedForRetake().Count} blocks marked for retake in all");
        return 0;
    }

    /// <summary>
    /// Runs a job to its end with progress every few seconds and a summary; Ctrl+C once stops after the running units,
    /// twice stops now. Exit codes: 0 done, 1 units failed or a stage failed, 130 stopped.
    /// </summary>
    static int RunToEnd(JobRunner runner, TextWriter sync)
    {
        sync.WriteLine($"run folder: {runner.RunPath}");

        int presses = 0;
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            if (Interlocked.Increment(ref presses) == 1)
            {
                sync.WriteLine("stopping after the running units (Ctrl+C again to stop now)");
                runner.RequestStop(StopMode.Boundary);
            }
            else runner.RequestStop(StopMode.Now);
        }
        Console.CancelKeyPress += OnCancel;
        using var progress = new System.Threading.Timer(_ => sync.WriteLine($"{DateTime.Now:HH:mm:ss} {Progress(runner.Snapshot())}"), null, ProgressEvery, ProgressEvery);
        JobSnapshot end;
        try { end = runner.RunAsync().GetAwaiter().GetResult(); }
        finally { Console.CancelKeyPress -= OnCancel; }
        progress.Change(Timeout.Infinite, Timeout.Infinite);

        foreach (var st in end.Stages)
            sync.WriteLine($"  {st.Id,-20} {st.State.ToString().ToLowerInvariant(),-8} {st.Done} of {st.Total} {st.Unit}s" +
                (st.Failed > 0 ? $", {st.Failed} failed" : "") + (st.Interrupted > 0 ? $", {st.Interrupted} interrupted" : "") +
                (st.StartedUtc is { } a && st.EndedUtc is { } b ? $"  {(b - a).TotalSeconds:n1} s" : ""));
        foreach (var f in end.Failures) sync.WriteLine($"  failed {f.Stage} {f.Unit}: {f.Message}");
        sync.WriteLine($"{end.State.ToString().ToLowerInvariant()} in {end.Elapsed:n1} s; log and records: {runner.RunPath}");
        if (end.State == JobState.Stopped) sync.WriteLine("run build again to continue");
        return end.State switch
        {
            JobState.Stopped => 130,
            JobState.Failed => 1,
            _ => end.FailureCount > 0 ? 1 : 0,
        };
    }

    static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The pre-check with the game of this PC (see <see cref="Core.Capture.Precheck"/>): prints each check and where the
    /// report and the test shot are. Exit codes: 0 all fine, 1 something to fix.
    /// </summary>
    static int RunPrecheck(string path, Dictionary<string, string?> o, TextWriter output)
    {
        Allow(o);
        var project = Project.Load(path);
        output.WriteLine($"pre-check of {project.File.Name}: console {project.File.Console.Host}:{project.File.Console.Port}, resources to stop {string.Join(", ", project.StopResources)}");
        var report = new Precheck(new LocalGame(), new Precheck.Options { ResourceVersion = AppVersion.Value }).RunAsync(project).GetAwaiter().GetResult();
        foreach (var item in report.Items)
        {
            var values = string.Join(" ", item.Values.Select(kv => $"{kv.Key}={kv.Value}"));
            output.WriteLine($"  {(item.Ok == true ? "ok" : item.Ok == false ? "NG" : "--"),-3}{item.Id,-12}{values}");
            if (item.Message.Length > 0) output.WriteLine($"     {item.Message}");
        }
        foreach (var b in report.Boxes)
            output.WriteLine($"     boxed: {b.Width} x {b.Height} px at {b.X}, {b.Y}{(b.InMap ? "" : " (off the part the map uses: shown only)")}");
        foreach (var b in report.Settle?.Blocks ?? [])
            output.WriteLine(FormattableString.Invariant($"     settle {b.Block}: ") + (b.StableMs is { } ms
                ? FormattableString.Invariant($"frames unchanged {ms / 1000.0:0.00} s after the streaming ended, {b.Fps:0.#} fps, the streaming ended after {b.SettleMs / 1000.0:0.00} s of settling (at most {b.MaxReq} requests)")
                : b.Problem + (b.Reason is null ? "" : ": " + b.Reason)));
        if (report.Settle is { } m)
            output.WriteLine(FormattableString.Invariant($"     the visit waits {m.WaitMs / 1000.0:0.00} s with no streaming before each shot") +
                (m.Ok ? FormattableString.Invariant($" (1.5 x {m.StableMs / 1000.0:0.00} s)") : " (the default)"));
        output.WriteLine($"{(report.Ok ? "all fine" : "something to fix")}; report and test shot: {report.Folder}");
        return report.Ok ? 0 : 1;
    }

    /// <summary>One line: the stage, units done, workers, time left.</summary>
    static string Progress(JobSnapshot s)
    {
        var st = s.Stages.FirstOrDefault(x => x.Id == s.Stage);
        if (st is null) return $"{s.State.ToString().ToLowerInvariant()}";
        var workers = s.Retiring > 0 ? $"{s.Running} workers ({s.Retiring} stop after their unit)"
            : s.MemoryLimitedAt is { } m ? $"{s.Running} workers (memory is short: held at {m})"
            : $"{s.Running} of {s.Workers} workers";
        var active = string.Join(", ", s.Active.Take(3).Select(a => $"{a.Unit} {a.Phase}"));
        return $"{st.Id}: {st.Done} of {st.Total} {st.Unit}s" + (st.Failed > 0 ? $" ({st.Failed} failed)" : "") +
            $", {workers}, cpu {s.Cpu * 100:0} %, left about {Durations.Range(s.RemainingLow, s.RemainingHigh)}" +
            (active.Length > 0 ? $"  [{active}{(s.Active.Count > 3 ? ", ..." : "")}]" : "") +
            (s.State == JobState.Stopping ? "  stopping" : "");
    }

    static void Print(TodoRow row, int depth, TextWriter output)
    {
        var label = new string(' ', depth * 2) + Label(row.Id);
        string left, time;
        if (!row.Needed) { left = "- (not needed)"; time = ""; }
        else if (row.Remaining == 0) { left = "0 (done)"; time = ""; }
        else
        {
            left = row.Unit switch
            {
                "block" when row.Land is not null => $"{row.Remaining} blocks ({row.Land} land, {row.Water} water)",
                "once" or "world" => row.Remaining == 1 ? "1" : $"{row.Remaining}",
                _ => $"{row.Remaining} {row.Unit}{(row.Remaining == 1 ? "" : "s")}",
            };
            time = Durations.Range(row.Low, row.High) + (row.ByHand ? " (by hand)" : row.UsesGame && depth == 0 ? " (game)" : "");
        }
        output.WriteLine($"  {label,-30}{left,-36}{time}");
        foreach (var c in row.Children) Print(c, depth + 1, output);
    }

    static string Label(string id) => id switch
    {
        "gameSetup" => "Before starting FiveM",
        "precheck" => "Pre-check (connected)",
        "visit" => "Visit",
        "visit.prepare" => "prepare",
        "visit.shot" => "shot image",
        "visit.height" => "height grid",
        "visit.scanGround" => "scan: ground",
        "visit.scanRoads" => "scan: roads",
        "visit.scanCanopy" => "scan: canopy",
        "visit.cleanup" => "clean up",
        "ortho" => "Ortho + satellite tiles",
        "gameFiles" => "Game files",
        "gameFiles.paths" => "path graph",
        "gameFiles.names" => "name tables",
        "mapData" => "Map data",
        "mapData.roadGraph" => "road graph",
        "mapData.landcover" => "ground cover",
        "mapData.regions" => "regions",
        "mapData.labels" => "labels",
        "mapData.roads" => "roads",
        "cells" => "Cell drawing",
        "cells.prep" => "preparation",
        "lowZoom" => "Low zooms",
        "ytd" => "Minimap (ytd)",
        "export" => "Export",
        _ => id[(id.IndexOf('.') + 1)..],
    };

    static (List<string> Positional, Dictionary<string, string?> Options) Split(IEnumerable<string> args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(list[i]); continue; }
            var key = list[i][2..];
            bool flag = key is "json" or "recalibrate" or "no-game" or "zip" or "no-minimap" or "no-tiles" or "all" or "clear";
            if (!flag && (i + 1 >= list.Count || list[i + 1].StartsWith("--", StringComparison.Ordinal))) throw new UsageException($"--{key} needs a value");
            options[key] = flag ? null : list[++i];
        }
        return (positional, options);
    }

    /// <summary><c>--workers n</c>, or <c>--level name</c> as that share of the processors; null when neither is given.</summary>
    static int? Workers(Dictionary<string, string?> o, int processors)
    {
        if (o.TryGetValue("workers", out var w))
            return int.TryParse(w, out var n) && n > 0 ? n : throw new UsageException("--workers needs a positive number");
        if (o.TryGetValue("level", out var lv))
            return ParallelLevels.TryParse(lv, out var l) ? l.Workers(processors) : throw new UsageException($"unknown level '{lv}' (full, strong, normal or light)");
        return null;
    }

    static void Allow(Dictionary<string, string?> options, params string[] names)
    {
        foreach (var k in options.Keys)
            if (!names.Contains(k)) throw new UsageException($"unknown option --{k}");
    }

    sealed class UsageException(string message) : Exception(message);
}

/// <summary>Estimate ranges for people: seconds, minutes or hours, both ends in the same unit.</summary>
public static class Durations
{
    public static string Range(double low, double high)
    {
        if (high <= 0) return "0";
        var (div, unit, digits) = high < 90 ? (1.0, "s", 0) : high < 90 * 60 ? (60.0, "min", 0) : (3600.0, "h", 1);
        string F(double v) => (v / div).ToString(digits == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);
        var a = F(Math.Max(low, 0));
        var b = F(high);
        if (digits == 0 && a == "0") a = "<1";
        if (digits == 0 && b == "0") b = "<1"; // both ends under half a second
        return a == b ? $"{b} {unit}" : $"{a}-{b} {unit}";
    }
}
