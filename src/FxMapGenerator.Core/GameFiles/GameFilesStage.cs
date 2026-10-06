using System.Diagnostics;
using System.Text.Json;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;
using FxMapGenerator.GameData.Gta;
using FxMapGenerator.GameData.Paths;

namespace FxMapGenerator.Core.GameFiles;

/// <summary>
/// Reads the game files once for the whole map (<see cref="GameFilesOutput"/>): the path graph with its junction
/// records, the server's replacement areas, and the names of the streets and zones in English and Japanese. The street
/// and zone names the scans met come from the scans, so the stage runs after the visit and again when a scan is newer.
/// </summary>
public sealed class GameFilesStage(GameFilesLocation.Defaults? defaults = null) : Stage
{
    public override string Id => "gameFiles";
    public override string Row => "gameFiles";
    public override IReadOnlyList<string> Rows => ["gameFiles", "gameFiles.paths", "gameFiles.names"];
    public override string? RecordKey => StageKeys.GameFiles;
    public override string UnitName => "world";
    public override long MemoryPerUnit => 1L << 30;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        int waiting = WithoutRoadScan(ctx.Project, ctx.State);
        if (waiting > 0)
        {
            ctx.Log($"game files: waiting for the road scans ({waiting} blocks of the range have none; the street and zone names come from them)");
            return [];
        }
        if (IsStale(ctx.Project, ctx.State)) return [StageKeys.World];
        ctx.Log("game files: up to date");
        return [];
    }

    public override int CountReady(Project project, StateStore state) => WithoutRoadScan(project, state) == 0 && IsStale(project, state) ? 1 : 0;

    /// <summary>Blocks of the range without their road scan (the stage waits for them: a visit earlier in the same run takes them).</summary>
    public static int WithoutRoadScan(Project project, StateStore state) => project.Range.Keys.Count(b => !state.Has(b, BlockItem.ScanRoads));

    /// <summary>
    /// True when the files were never read, a road scan of the range is newer than them, the server's resources of the
    /// project are not the ones they were read with (the same entries in the same order), the map's frame or the Cayo
    /// Perico choice changed (<see cref="ReadFor"/>), or a file is gone.
    /// </summary>
    public static bool IsStale(Project project, StateStore state)
    {
        var done = state.StageDone(StageKeys.GameFiles, StageKeys.World);
        if (done is null) return true;
        var folder = new WorkFolder(project.WorkFolderPath);
        foreach (var f in new[] { GameFilesOutput.Paths, GameFilesOutput.Names })
            if (!File.Exists(Path.Combine(folder.Game, f))) return true;
        foreach (var b in project.Range.Keys)
            if (state.ItemTime(b, BlockItem.ScanRoads) is { } t && t > done) return true;
        var record = ReadRecord(folder);
        var server = GameFilesLocation.ServerResourcesOf(project);
        return !(record?.ServerResources ?? []).SequenceEqual(server, StringComparer.OrdinalIgnoreCase) || !ReadFor(record, project);
    }

    /// <summary>
    /// True when the files were read before, but with other server resources than the project names now, for another
    /// frame or another Cayo Perico choice (the road data read can change the road graph everywhere).
    /// </summary>
    public static bool ServerResourcesChanged(Project project)
    {
        var record = ReadRecord(new WorkFolder(project.WorkFolderPath));
        return record is not null && (!(record.ServerResources ?? []).SequenceEqual(GameFilesLocation.ServerResourcesOf(project), StringComparer.OrdinalIgnoreCase)
            || !ReadFor(record, project));
    }

    /// <summary>
    /// Whether the files were read for the project's frame and its Cayo Perico choice (a record without them was read for
    /// the standard frame without the island).
    /// </summary>
    static bool ReadFor(SourcesRecord? record, Project project) =>
        FrameCells(project.Frame).SequenceEqual(record?.ExtraCells ?? FrameCells(MapFrame.Standard)) && (record?.CayoPerico ?? false) == project.File.CayoPerico;

    /// <summary>The frame's added cells as the record keeps them: top, bottom, left, right.</summary>
    static int[] FrameCells(MapFrame f) => [f.CellsTop, f.CellsBottom, f.CellsLeft, f.CellsRight];

    public override void Run(UnitContext ctx) =>
        Read(ctx.Project, ctx.State, defaults, ctx.Folder.Game, ctx.Parallel, ctx.Log, ctx.Report, ctx.Token);

    /// <summary>What <see cref="ReadNow"/> read, and whether the path graph or the names differ from the files there were.</summary>
    public sealed record ReadNowResult(bool Changed, bool PathsChanged, int Areas, int Nodes, int Links, double Seconds);

    /// <summary>
    /// Reads the game files now, outside a run (the road editor's button; also the way to read them again after the game
    /// or a server's road data changed, which the step does not notice): the files the step writes, with the names of the
    /// scans there are so far. Files that come out the same as those there are stay as they are and nothing else changes.
    /// When they differ and the step had run before, the step counts as run now, so the next run makes the road steps
    /// again; a step that never ran stays to do (it runs once the scans are in, as before).
    /// </summary>
    public static ReadNowResult ReadNow(Project project, StateStore state, GameFilesLocation.Defaults? defaults, IParallelRunner parallel, Action<string> log,
        CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        var game = new WorkFolder(project.WorkFolderPath).Game;
        var fresh = Path.Combine(game, ".reading");
        if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
        Directory.CreateDirectory(fresh);
        try
        {
            var r = Read(project, state, defaults, fresh, parallel, log, (_, _) => { }, token);
            bool Differs(string name)
            {
                var old = Path.Combine(game, name);
                return !File.Exists(old) || !File.ReadAllBytes(old).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(fresh, name)));
            }
            bool paths = Differs(GameFilesOutput.Paths), changed = paths || Differs(GameFilesOutput.Names);
            if (changed)
            {
                foreach (var name in new[] { GameFilesOutput.Paths, GameFilesOutput.Names, GameFilesOutput.Record })
                    File.Move(Path.Combine(fresh, name), Path.Combine(game, name), overwrite: true);
                if (state.StageDone(StageKeys.GameFiles, StageKeys.World) is not null) state.SetStageDone(StageKeys.GameFiles, StageKeys.World, DateTime.UtcNow);
            }
            log($"game files: read outside a run, {(changed ? "the files are replaced" : "the same as the files there are")}");
            return new ReadNowResult(changed, paths, r.Areas, r.Nodes, r.Links, Math.Round(sw.Elapsed.TotalSeconds, 2));
        }
        finally
        {
            try { Directory.Delete(fresh, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>Reads the game files and writes the path graph, the names and the record into <paramref name="game"/>.</summary>
    static SourcesRecord Read(Project project, StateStore state, GameFilesLocation.Defaults? defaults, string game, IParallelRunner parallel, Action<string> log,
        Action<string, double> report, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var folder = new WorkFolder(project.WorkFolderPath);
        var where = GameFilesLocation.Resolve(project, defaults);
        if (!where.GtaFound)
            throw new InvalidOperationException(where.GtaFolder is null
                ? "GTA V was not found on this PC: set its folder in the settings or the project"
                : $"not a GTA V folder (no {GtaLocator.ExecutableName}): {where.GtaFolder}");
        if (!where.KeysFound)
            throw new InvalidOperationException("the RPF keys were not found (make them with EmotePreviewerKeyTool); looked in: "
                + string.Join("; ", where.KeysTried.Select(t => $"{t.Folder} (missing {string.Join(", ", t.Missing)})")));
        log($"game files: GTA V {where.GtaFolder} ({where.GtaSource}), keys {where.KeysFolder} ({where.KeysSource})"
            + (where.ServerResources.Count == 0 ? "" : $", server resources (a later one wins) {string.Join(" | ", where.ServerResources)}"));

        report("read", 0);
        var r = GameFileReader.Read(where.GtaFolder, where.KeysFolder, where.ServerResources, project.Frame, project.File.CayoPerico, log, token);
        foreach (var e in r.ArchiveErrors.Take(10)) log("  archive not read: " + e);
        foreach (var t in r.Ties) log("  " + t);

       report("paths", 0.5);
        var (nodes, links, junctions) = GameFilesOutput.WritePaths(Path.Combine(game, GameFilesOutput.Paths), r);

        report("names", 0.8);
        var scans = project.Range.Keys.Where(b => state.Has(b, BlockItem.ScanRoads)).Select(folder.ScanFile).Where(File.Exists).ToList();
        var (scanStreets, zones) = ScanNames.Collect(scans, parallel, token);
        var pathStreets = r.Files.Values.SelectMany(f => f.Nodes).Where(n => !YndFile.IsPed(n)).Select(n => n.Street).Where(h => h != 0).ToHashSet();
        var streets = pathStreets.Union(scanStreets).ToList();
        var names = GameFilesOutput.WriteNames(Path.Combine(game, GameFilesOutput.Names), r.Names, streets, zones);

        var frame = project.Frame;
        var record = new SourcesRecord(where.GtaFolder!, where.KeysFolder!, where.ServerResources, r.Archives, r.ArchiveErrors,
            r.Files.Count, nodes, links, junctions, r.Replaced, r.Ties, r.Overridden, r.NameFiles,
            scans.Count, streets.Count, names.StreetsMissing, pathStreets.Count, scanStreets.Count, zones.Count, names.ZonesMissing,
            frame.IsStandard ? null : FrameCells(frame), project.File.CayoPerico ? true : null, project.File.CayoPerico ? r.Island : null,
            r.ServerFileErrors.Count > 0 ? r.ServerFileErrors : null, r.NumberMismatches.Count > 0 ? r.NumberMismatches : null);
        GameFilesOutput.WriteAtomically(Path.Combine(game, GameFilesOutput.Record), fs => JsonSerializer.Serialize(fs, record, Project.Json));
        log($"game files: {r.Files.Count} areas, {nodes} vehicle nodes, {links} directed links, {junctions} junction squares"
            + (r.Replaced.Count > 0 ? $", {r.Replaced.Count} areas from the server" : "")
            + (r.ServerFileErrors.Count > 0 ? $", {r.ServerFileErrors.Count} server files not read" : "")
            + $"; names: {streets.Count} streets ({names.StreetsMissing} without text; {pathStreets.Count} of the path nodes, {scanStreets.Count} met by {scans.Count} scans)"
            + $" and {zones.Count} zones ({names.ZonesMissing} without text); {sw.Elapsed.TotalSeconds:0.0} s");
        return record;
    }

    /// <summary>
    /// <c>game/sources.json</c>: where the files came from and what they hold. <paramref name="ExtraCells"/> (top, bottom,
    /// left, right) and <paramref name="CayoPerico"/> with its <paramref name="IslandAreas"/> are written only when the frame
    /// has cells added or the island's roads were read; <paramref name="ServerFileErrors"/> (server files passed over, with
    /// the reason) and <paramref name="NumberMismatches"/> (files whose nodes carry another area or number than the file's
    /// name and their order give) only when there are any.
    /// </summary>
    public sealed record SourcesRecord(string GtaFolder, string KeysFolder, IReadOnlyList<string> ServerResources, int Archives, IReadOnlyList<string> ArchiveErrors,
        int Areas, int Nodes, int Links, int Junctions, IReadOnlyList<GameFileReader.Replacement> Replaced, IReadOnlyList<string> Ties, IReadOnlyList<string> Overridden,
        IReadOnlyDictionary<string, int> NameFiles, int Scans, int Streets, int StreetsWithoutText, int PathStreets, int ScanStreets, int Zones, int ZonesWithoutText,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int[]? ExtraCells = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? CayoPerico = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? IslandAreas = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ServerFileErrors = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? NumberMismatches = null);

    public static SourcesRecord? ReadRecord(WorkFolder folder)
    {
        var path = Path.Combine(folder.Game, GameFilesOutput.Record);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SourcesRecord>(File.ReadAllText(path), Project.Json); }
        catch (JsonException) { return null; }
    }
}
