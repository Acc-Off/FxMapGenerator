using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Projects;

/// <summary>A project file on disk with its resolved maps, range and work folder.</summary>
public sealed class Project
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    Project(string filePath, ProjectFile file)
    {
        FilePath = Path.GetFullPath(filePath);
        File = file;
    }

    public string FilePath { get; }
    public ProjectFile File { get; }
    public string Folder => Path.GetDirectoryName(FilePath)!;
    public string WorkFolderPath => Path.GetFullPath(Path.Combine(Folder, string.IsNullOrWhiteSpace(File.WorkFolder) ? "." : File.WorkFolder));

    /// <summary>A path of the project file made absolute (relative paths are relative to the project file's folder).</summary>
    public string ResolvePath(string path) => Path.GetFullPath(Path.Combine(Folder, path));

    string? _roadEditsCopy;

    /// <summary>The road edits file (<see cref="ProjectFile.RoadEdits"/>) made absolute, or null; in a run, the copy it took at its start.</summary>
    public string? RoadEditsPath => _roadEditsCopy ?? (File.RoadEdits is { } p ? ResolvePath(p) : null);

    /// <summary>The run reads the road edits from its copy (<c>logs/run-&lt;time&gt;/inputs/</c>) instead of the project's file.</summary>
    public void UseRoadEditsCopy(string path) => _roadEditsCopy = path;

    string? _stylesCopy;

    /// <summary>The folder of the project's own styles (<see cref="ProjectFile.Styles"/>) made absolute, or null; in a run, the copies it took at its start.</summary>
    public string? StylesFolder => _stylesCopy ?? (File.Styles is { } s ? ResolvePath(s) : null);

    /// <summary>The run reads the styles of its maps from its copies (<c>logs/run-&lt;time&gt;/inputs/styles/</c>) instead of the project's folder.</summary>
    public void UseStylesCopy(string folder) => _stylesCopy = folder;

    string? _poiCopy, _poiStylesCopy;

    /// <summary>The POI folder (<see cref="ProjectFile.Poi"/>) made absolute, or null for the bundled points; in a run, the copy it took at its start.</summary>
    public string? PoiFolderPath => File.Poi is { } p ? _poiCopy ?? ResolvePath(p) : null;

    /// <summary>The POI styles file (<see cref="ProjectFile.PoiStyles"/>) made absolute, or null; in a run, the copy it took at its start.</summary>
    public string? PoiStylesPath => File.PoiStyles is { } s ? _poiStylesCopy ?? ResolvePath(s) : null;

    /// <summary>
    /// The run reads the points of interest from its copies (<c>logs/run-&lt;time&gt;/inputs/poi/</c>, the styles file as
    /// <c>inputs/poi-styles.json</c> with the PNG icons it names beside it, as they are beside the project's) instead of the project's.
    /// </summary>
    public void UsePoiCopy(string? folder, string? styles) => (_poiCopy, _poiStylesCopy) = (folder, styles);

    /// <summary>The style a map is drawn with: a bundled style (the road map's, an atlas map's preset), or one of the project's styles.</summary>
    /// <exception cref="ProjectException">STYLE: the project has no readable style of that id.</exception>
    public Styles.MapStyle StyleOf(MapSet map) => map.Kind == MapKind.Atlas ? Style(map.Preset!) : Styles.MapStyle.Builtin("roadmap");

    /// <summary>A style by id: a bundled style, or one of the project's styles (read once per version of its file).</summary>
    /// <exception cref="ProjectException">STYLE: the project has no readable style of that id.</exception>
    public Styles.MapStyle Style(string id) => Styles.MapStyle.BuiltIn.Contains(id) ? Styles.MapStyle.Builtin(id) : Styles.ProjectStyles.Read(this, id);

    /// <summary>
    /// The maps to build, in a fixed order: satellite, the atlas maps (each style as listed, in each language: English,
    /// then Japanese), road map.
    /// </summary>
    public IReadOnlyList<MapSet> Maps
    {
        get
        {
            var list = new List<MapSet>();
            if (File.Maps.Satellite) list.Add(MapSet.Satellite);
            if (File.Maps.Atlas.Enabled)
                foreach (var style in File.Maps.Atlas.Styles ?? [])
                    foreach (var language in Languages)
                        if (MapSet.TryParse($"atlas-{style}-{language}", out var s) && !list.Contains(s)) list.Add(s);
            if (File.Maps.Roadmap) list.Add(MapSet.Roadmap);
            return list;
        }
    }

    /// <summary>The languages of the atlas maps: English, then the others listed (<see cref="AtlasSetting.Languages"/>) in the app's order.</summary>
    public IReadOnlyList<string> Languages =>
        AtlasPresets.Languages.Where(l => l == "en" || (File.Maps.Atlas.Languages ?? []).Contains(l)).ToList();

    /// <summary>Whether the project makes Japanese maps (atlas maps in Japanese): the screens then ask for and show the Japanese labels.</summary>
    public bool MakesJapanese => File.Maps.Atlas.Enabled && (File.Maps.Atlas.Languages ?? []).Contains("ja");

    /// <summary>Resources stopped on the game client during a visit: the project's own list, else its server preset's.</summary>
    public IReadOnlyList<string> StopResources => File.Server.StopResources ?? ServerPresets.Find(File.Server.Preset)?.StopResources ?? [];

    /// <summary>The map's frame: the standard frame with the cells the project adds around it (<see cref="RangeSetting.ExtraCells"/>).</summary>
    public MapFrame Frame => File.Range.ExtraCells is { } e ? new MapFrame(e.Top, e.Bottom, e.Left, e.Right) : MapFrame.Standard;

    /// <summary>The blocks of the range with their class (<see cref="RangePresets.Classify"/>), row by row.</summary>
    public IReadOnlyDictionary<BlockId, BlockClass> Range
    {
        get
        {
            var set = new HashSet<BlockId>();
            if (File.Range.Base == "default") set.UnionWith(DefaultRange.Blocks.Keys);
            foreach (var name in File.Range.Add)
                if (BlockId.TryParse(name, out var b)) set.Add(b);
            foreach (var name in File.Range.Remove)
                if (BlockId.TryParse(name, out var b)) set.Remove(b);
            return RangePresets.Classify(set);
        }
    }

    /// <summary>
    /// Puts a range preset's blocks into the range (those already in it stay as they are). Returns how many were added.
    /// </summary>
    /// <exception cref="ProjectException">INVALID: no preset of that id, or some of its blocks lie outside the project's frame.</exception>
    public int AddPreset(string id)
    {
        var preset = RangePresets.Find(id) ?? throw new ProjectException("INVALID", $"unknown range preset '{id}' ({string.Join(", ", RangePresets.All.Select(p => p.Id))})");
        var frame = Frame;
        int off = preset.Blocks.Keys.Count(b => !frame.Contains(b));
        if (off > 0)
        {
            var f = preset.Frame;
            throw new ProjectException("INVALID", $"{off} block(s) of the preset '{id}' lie outside the map's frame: add cells around the map first "
                + $"(the preset needs top {f.CellsTop}, bottom {f.CellsBottom}, left {f.CellsLeft}, right {f.CellsRight})");
        }
        return SetInRange(preset.Blocks.Keys, include: true);
    }

    /// <summary>
    /// Puts blocks into the range (<paramref name="include"/>) or takes them out, keeping the file in its
    /// "base + added - removed" form: a block of the base comes back by leaving the removed list, any other block
    /// goes into the added list (and the other way round). Blocks off the project's frame are ignored. Returns how many changed.
    /// </summary>
    public int SetInRange(IEnumerable<BlockId> blocks, bool include)
    {
        var range = File.Range;
        var inBase = range.Base == "default" ? DefaultRange.Blocks : (IReadOnlyDictionary<BlockId, BlockClass>)new Dictionary<BlockId, BlockClass>();
        var now = Range;
        var frame = Frame;
        int changed = 0;
        foreach (var b in blocks.Distinct())
        {
            if (!frame.Contains(b) || now.ContainsKey(b) == include) continue;
            changed++;
            if (include)
            {
                if (inBase.ContainsKey(b)) range.Remove.Remove(b.Name);
                else range.Add.Add(b.Name);
            }
            else
            {
                if (inBase.ContainsKey(b)) range.Remove.Add(b.Name);
                else range.Add.Remove(b.Name);
            }
        }
        range.Add = range.Add.Distinct().Select(BlockId.Parse).OrderBy(b => b).Select(b => b.Name).ToList();
        range.Remove = range.Remove.Distinct().Select(BlockId.Parse).OrderBy(b => b).Select(b => b.Name).ToList();
        return changed;
    }

    /// <summary>Back to the bundled default range (1,045 blocks), nothing added or removed.</summary>
    public void ResetRange()
    {
        File.Range.Base = "default";
        File.Range.Add.Clear();
        File.Range.Remove.Clear();
    }

    /// <summary>A new project with the defaults (satellite map, the default range, Qbox), not saved yet.</summary>
    public static Project Create(string filePath, string? name = null)
    {
        var file = new ProjectFile { Name = string.IsNullOrWhiteSpace(name) ? NameFromPath(filePath) : name.Trim() };
        return new Project(filePath, file);
    }

    public static string NameFromPath(string filePath)
    {
        var n = Path.GetFileName(filePath);
        return n.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase) ? n[..^ProjectFile.Extension.Length] : Path.GetFileNameWithoutExtension(n);
    }

    /// <exception cref="ProjectException">The file is missing, not JSON, from a newer version or invalid.</exception>
    public static Project Load(string filePath) => Load(filePath, filePath);

    /// <summary>
    /// Reads a copy of a project file (a run's <c>inputs/</c>) as the project at <paramref name="filePath"/>, so relative
    /// paths such as the work folder still resolve from the original's folder.
    /// </summary>
    public static Project LoadCopy(string copyPath, string filePath) => Load(copyPath, filePath);

    static Project Load(string readFrom, string filePath)
    {
        if (!System.IO.File.Exists(readFrom)) throw new ProjectException("NOT_FOUND", $"project file not found: {readFrom}");
        ProjectFile? file;
        try { file = JsonSerializer.Deserialize<ProjectFile>(System.IO.File.ReadAllText(readFrom, Encoding.UTF8), Json); }
        catch (JsonException ex) { throw new ProjectException("INVALID_JSON", $"{readFrom} is not a valid project file: {ex.Message}"); }
        if (file is null) throw new ProjectException("INVALID_JSON", $"{readFrom} is empty");
        if (file.Format > ProjectFile.CurrentFormat)
            throw new ProjectException("NEWER_FORMAT", $"{readFrom} was written by a newer FxMapGenerator (format {file.Format}, this version reads {ProjectFile.CurrentFormat})");
        var project = new Project(filePath, file);
        var problems = project.Validate();
        if (problems.Count > 0) throw new ProjectException("INVALID", $"{readFrom}: " + string.Join("; ", problems));
        return project;
    }

    /// <summary>Writes the file (UTF-8, indented) through a temporary file, so a crash never leaves half a project.</summary>
    public void Save()
    {
        var problems = Validate();
        if (problems.Count > 0) throw new ProjectException("INVALID", string.Join("; ", problems));
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(File, Json) + "\n", Utf8NoBom);
        Replace(tmp, FilePath);
    }

    /// <summary>How long <see cref="Save"/> keeps trying to put the new file in the old one's place.</summary>
    internal static readonly TimeSpan SavePatience = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Puts the temporary file in the project file's place. Windows refuses that while a reader holds the project file
    /// open (a run reading it again as its first road stage starts, the command line, another program), so a refusal is
    /// tried again for a short while; one that lasts is passed on as it is.
    /// </summary>
    static void Replace(string tmp, string path)
    {
        long giveUp = Environment.TickCount64 + (long)SavePatience.TotalMilliseconds;
        while (true)
        {
            try
            {
                System.IO.File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Environment.TickCount64 < giveUp)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>Everything that would make the project unusable, in plain sentences. Empty = fine.</summary>
    public List<string> Validate()
    {
        var p = new List<string>();
        var f = File;
        if (string.IsNullOrWhiteSpace(f.Name)) p.Add("the name is empty");
        if (f.Maps.Atlas.Styles is null) p.Add("the atlas styles must be a list");
        else
        {
            foreach (var style in f.Maps.Atlas.Styles)
                if (string.IsNullOrEmpty(style) || !AtlasPresets.IsKnown(style)) p.Add($"unknown atlas style '{style}'");
            foreach (var d in f.Maps.Atlas.Styles.GroupBy(s => s).Where(g => g.Count() > 1)) p.Add($"the atlas style '{d.Key}' is listed twice");
        }
        if (f.Maps.Atlas.Languages is null) p.Add("the languages of the atlas maps must be a list");
        else
        {
            foreach (var l in f.Maps.Atlas.Languages)
                if (!AtlasPresets.Languages.Contains(l)) p.Add($"unknown language '{l}' of the atlas maps ({string.Join(", ", AtlasPresets.Languages)})");
            if (!f.Maps.Atlas.Languages.Contains("en")) p.Add("the atlas maps are always made in English: the languages must list \"en\"");
            foreach (var d in f.Maps.Atlas.Languages.GroupBy(l => l).Where(g => g.Count() > 1)) p.Add($"the language '{d.Key}' is listed twice");
        }
        if (f.Minimap.Map is { } mm && !Maps.Any(m => m.Id == mm)) p.Add($"the minimap map '{mm}' is not one of the maps to build");
        if (!MinimapOutside.IsValid(f.Minimap.Outside)) p.Add($"the minimap outside the range '{f.Minimap.Outside}' (map or transparent)");
        if (f.Range.Base is not ("default" or "none")) p.Add($"range base '{f.Range.Base}' (default or none)");
        var frame = Frame;
        var frameProblems = frame.Problems().ToList();
        p.AddRange(frameProblems);
        foreach (var name in f.Range.Add.Concat(f.Range.Remove))
            if (!BlockId.TryParse(name, out _)) p.Add($"'{name}' is not a block name");
        if (frameProblems.Count == 0)
        {
            var outside = f.Range.Add.Where(n => BlockId.TryParse(n, out var b) && !frame.Contains(b)).ToList();
            if (outside.Count > 0)
                p.Add($"{outside.Count} block(s) of the range lie outside the map's frame ({string.Join(", ", outside.Take(5))}{(outside.Count > 5 ? ", ..." : "")}): add cells around the map or take them out of the range");
        }
        if (ServerPresets.Find(f.Server.Preset) is null)
            p.Add($"server preset '{f.Server.Preset}' ({string.Join(" or ", ServerPresets.All.Select(x => x.Id))})");
        foreach (var name in f.Server.StopResources ?? [])
            if (!ServerPresets.IsResourceName(name)) p.Add($"'{name}' is not a resource name (resources to stop)");
        if (f.Console.Port is < 1 or > 65535) p.Add($"console port {f.Console.Port}");
        if (f.GameFiles.ServerResources is null) p.Add("the server's resources must be a list ([] for none)");
        else if (f.GameFiles.ServerResources.Any(string.IsNullOrWhiteSpace)) p.Add("an entry of the server's resources is empty");
        if (f.HeightQuality is { } hq)
        {
            var (sat, cell) = Satellite.SurfaceHeights.MapsOf(f);
            if (!Satellite.SurfaceHeights.IsQuality(hq)) p.Add($"height quality '{hq}' (speed, balance or quality)");
            else if (Satellite.SurfaceHeights.Unavailable(hq, sat, cell) is { } why) p.Add($"height quality '{hq}' {why}");
        }
        if (f.RoadEdits is { } re && string.IsNullOrWhiteSpace(re)) p.Add("the road edits file is empty (null for none)");
        if (f.Styles is { } st && string.IsNullOrWhiteSpace(st)) p.Add("the styles folder is empty (null for none)");
        foreach (var place in f.PreviewPlaces ?? [])
        {
            if (string.IsNullOrWhiteSpace(place.Name)) p.Add("a preview place has no name");
            if (!double.IsFinite(place.X) || !double.IsFinite(place.Y)) p.Add($"the preview place '{place.Name}' is not a point");
        }
        if (f.Postals is { } pc && string.IsNullOrWhiteSpace(pc)) p.Add("the postal codes source is empty (null for nearest-postal's table)");
        if (f.Poi is { } poi && string.IsNullOrWhiteSpace(poi)) p.Add("the points of interest folder is empty (null for the bundled ones)");
        if (f.PoiStyles is { } ps && string.IsNullOrWhiteSpace(ps)) p.Add("the POI styles file is empty (null for the bundled ones)");
        if (f.Parallel < 1) p.Add($"parallel {f.Parallel} (at least 1 worker)");
        foreach (var id in f.Export.Maps ?? [])
            if (!MapSet.TryParse(id, out _)) p.Add($"unknown map '{id}' (maps to export)");
        if (!string.IsNullOrWhiteSpace(f.Export.BaseUrl) && !IsWebAddress(f.Export.BaseUrl)) p.Add($"'{f.Export.BaseUrl}' is not an http(s) address (export base URL)");
        if (!Export.ExportOptions.IsMaxZoom(f.Export.MaxZoom))
            p.Add($"export maximum zoom {f.Export.MaxZoom} ({Export.ExportOptions.LowestMaxZoom}-{WorldGrid.Zoom})");
        if (f.Export.Editable is null) p.Add("the export's editable files must be an object");
        else
        {
            if (!Export.EditableChoice.IsZoom(f.Export.Editable.Zoom))
                p.Add($"zoom level {f.Export.Editable.Zoom} of the editable files ({Export.EditableChoice.DefaultZoom} or {Export.EditableChoice.FinestZoom})");
            foreach (var format in f.Export.Editable.Formats ?? [])
                if (!Export.EditableChoice.AllFormats.Contains(format)) p.Add($"unknown format '{format}' of the editable files ({string.Join(", ", Export.EditableChoice.AllFormats)})");
        }
        if (f.Export.Picture is null) p.Add("the export's edited picture must be an object");
        else
        {
            if (f.Export.Picture.File is { } file && string.IsNullOrWhiteSpace(file)) p.Add("the edited picture's file is empty (null for none)");
            if (f.Export.Picture.Map is { } pm && !MapSet.TryParse(pm, out _)) p.Add($"unknown map '{pm}' (the map of the edited picture)");
        }
        return p;
    }

    /// <summary>An absolute http or https address.</summary>
    public static bool IsWebAddress(string s) =>
        Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}

public sealed class ProjectException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
