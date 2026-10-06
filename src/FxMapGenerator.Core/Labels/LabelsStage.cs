using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Regions;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Labels;

/// <summary>
/// The labels of the atlas maps, one unit per language: postal codes, zone names and street names placed once for the
/// whole map (<see cref="LabelPlacer"/>) into <c>data/labels-&lt;language&gt;-&lt;key&gt;.json</c>, one file per labels
/// section among the language's atlas maps (styles with the same placement share it). The postal codes come from the
/// project's source (a file, a URL, or nearest-postal's table by default), copied into <c>data/postals.json</c> once
/// (<c>data/postals-record.json</c> says from where); the points of interest shown on the atlas maps take their space
/// first. The street names are the game's and those the road edits add. The name of the Cayo Perico island's zone is
/// placed only in a project that reads the island's roads (<see cref="UnnamedZones"/>).
/// <c>data/labels-&lt;language&gt;-record.json</c> holds what a language was made with. Runs again when the road graph,
/// the zones or the game names are newer, a labels section, the postal codes, the points of interest, the street names
/// the road edits add or the project's reading of Cayo Perico's roads changed.
/// </summary>
public sealed class LabelsStage : Stage
{
    public const string PostalsRecord = "postals-record.json";

    /// <summary>The game's zone of the Cayo Perico island. It reaches over the sea around the island, west to the aircraft carrier.</summary>
    public const string IslandZone = "ISHEIST";

    /// <summary>
    /// The zones whose names the project's labels leave out: the Cayo Perico island's, unless the project reads the
    /// island's roads (<see cref="ProjectFile.CayoPerico"/>: the server runs the island). Part of that zone is sea the
    /// default range holds, where its name would stand on a map without the island.
    /// </summary>
    public static IReadOnlyCollection<string> UnnamedZones(Project project) => project.File.CayoPerico ? [] : [IslandZone];

    public override string Id => "labels";
    public override string Row => "mapData.labels";
    public override string? RecordKey => StageKeys.Labels;
    public override string UnitName => "language";
    public override long MemoryPerUnit => 768L << 20;

    /// <summary>How the text is measured (tests give fixed sizes); null = the fonts installed on this PC.</summary>
    public static Func<ITextMetrics>? Metrics { get; set; }

    /// <summary>How a URL is fetched (tests give a fake); null = HTTP.</summary>
    public static Func<string, CancellationToken, string>? Fetch { get; set; }

    static readonly object PostalGate = new();

    /// <summary>The languages of the atlas maps, in the maps' order.</summary>
    public static IReadOnlyList<string> Languages(Project project) =>
        project.Maps.Where(m => m.Kind == MapKind.Atlas && project.StyleOf(m).Labels is not null).Select(m => m.Language!).Distinct().ToList();

    /// <summary>The labels sections of a language's atlas maps, one per placement key (first map first), with the maps that use each.</summary>
    public static List<(string Key, LabelsStyle Style, List<string> Maps)> Sections(Project project, string language)
    {
        var o = new List<(string, LabelsStyle, List<string>)>();
        foreach (var m in project.Maps.Where(m => m.Kind == MapKind.Atlas && m.Language == language))
        {
            if (project.StyleOf(m).Labels is not { } st) continue;
            int i = o.FindIndex(x => x.Item1 == st.PlacementKey);
            if (i < 0) o.Add((st.PlacementKey, st, [m.Id]));
            else o[i].Item3.Add(m.Id);
        }
        return o;
    }

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        if (Waiting(ctx.Folder) is { } why)
        {
            ctx.Log("labels: waiting for " + why);
            return [];
        }
        var left = Languages(ctx.Project).Where(l => IsStale(ctx.Project, ctx.State, l)).ToList();
        if (left.Count == 0) ctx.Log("labels: up to date");
        return left;
    }

    public override int CountReady(Project project, StateStore state) =>
        Waiting(new WorkFolder(project.WorkFolderPath)) is null ? Languages(project).Count(l => IsStale(project, state, l)) : 0;

    /// <summary>What the labels still wait for (the road graph, the zones, the game names), or null.</summary>
    public static string? Waiting(WorkFolder folder)
    {
        if (!File.Exists(Path.Combine(folder.Data, RoadGraphFile.Roads))) return "the road graph";
        if (!File.Exists(Path.Combine(folder.Data, ZoneGrid.FileName))) return "the zones";
        if (!File.Exists(Path.Combine(folder.Game, GameFilesOutput.Names))) return "the names of the game files";
        return null;
    }

    // ------------------------------------------------------------------ postal codes

    /// <summary>Where the project's postal codes come from: its file (full path) or URL, or the default URL.</summary>
    public static string PostalSource(Project project) =>
        project.File.Postals is not { } p ? PostalCodes.DefaultUrl : Projects.Project.IsWebAddress(p) ? p.Trim() : project.ResolvePath(p);

    /// <summary><c>data/postals-record.json</c>: where the copy came from, its SHA-256 and number of codes.</summary>
    public sealed record PostalsCopy(string Source, string Sha256, int Count, DateTime FetchedUtc);

    static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Project.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The copy as it is now, or null when it must be made again (none, another source, the source file edited).</summary>
    public static PostalsCopy? CurrentCopy(Project project)
    {
        var folder = new WorkFolder(project.WorkFolderPath);
        var rec = ReadJson<PostalsCopy>(Path.Combine(folder.Data, PostalsRecord));
        var copy = Path.Combine(folder.Data, PostalCodes.FileName);
        var src = PostalSource(project);
        if (rec is null || !File.Exists(copy) || rec.Source != src) return null;
        if (!Projects.Project.IsWebAddress(src))
        {
            if (!File.Exists(src)) return null;
            if (Sha(File.ReadAllText(src)) != rec.Sha256) return null;
        }
        return rec;
    }

    /// <summary>Makes the copy of the postal codes when it is needed (fetching a URL once), then returns it.</summary>
    /// <exception cref="LabelsException">The source cannot be read or is not a postal code list.</exception>
    public static PostalsCopy EnsurePostals(Project project, Action<string> log, CancellationToken token)
    {
        lock (PostalGate)
        {
            if (CurrentCopy(project) is { } rec) return rec;
            var folder = new WorkFolder(project.WorkFolderPath);
            var src = PostalSource(project);
            string text;
            if (Projects.Project.IsWebAddress(src))
            {
                try { text = (Fetch ?? HttpFetch)(src, token); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    throw new LabelsException($"could not fetch the postal codes from {src} ({ex.Message}); give a file in the project's postal code setting");
                }
            }
            else
            {
                if (!File.Exists(src)) throw new LabelsException($"{src}: not found (the project's postal codes)");
                text = File.ReadAllText(src);
            }
            var codes = PostalCodes.Parse(text, src);
            Directory.CreateDirectory(folder.Data);
            GameFilesOutput.WriteAtomically(Path.Combine(folder.Data, PostalCodes.FileName), fs => { using var w = new StreamWriter(fs); w.Write(text); });
            rec = new PostalsCopy(src, Sha(text), codes.Count, DateTime.UtcNow);
            GameFilesOutput.WriteAtomically(Path.Combine(folder.Data, PostalsRecord), fs => JsonSerializer.Serialize(fs, rec, Project.Json));
            log($"postal codes: {codes.Count} from {src}");
            return rec;
        }
    }

    static string HttpFetch(string url, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return http.GetStringAsync(url, token).GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------ records

    /// <summary>A labels file of a language: the placement key, the maps that use it, the labels of each kind, the street names
    /// with a route number, and per cell of the range the digest of the labels its drawing takes (<see cref="Render.CellInputs.LabelsDigest"/>).</summary>
    public sealed record LabelsKey(string Key, IReadOnlyList<string> Maps, int Postal, int Zone, int Street, int Routes, IReadOnlyDictionary<string, string>? Cells = null);

    /// <summary>
    /// <c>data/labels-&lt;language&gt;-record.json</c>; <c>StreetsSha256</c>: the digest of the street names the road edits add
    /// (<see cref="RoadEditsFile.StreetsDigestOf"/>, null: none); <c>CayoPerico</c>: whether the project read Cayo Perico's
    /// roads when the labels were placed (the island's zone name goes with it; labels whose record does not say are placed again).
    /// </summary>
    public sealed record LabelsRecord(string Language, IReadOnlyList<LabelsKey> Files, string PostalsSha256, string PoiSha256, IReadOnlyList<string> MissingFonts, double Seconds,
        string? StreetsSha256 = null, bool? CayoPerico = null);

    public static string RecordPath(WorkFolder folder, string language) => Path.Combine(folder.Data, $"labels-{language}-record.json");

    /// <summary>The record of a language's labels, or null when they were never made.</summary>
    public static LabelsRecord? Record(Project project, string language) => ReadJson<LabelsRecord>(RecordPath(new WorkFolder(project.WorkFolderPath), language));

    /// <summary>The credit of the postal codes the labels were placed with (the copy in <c>data/</c>), or null (none, or the list carries none).</summary>
    public static string? PostalsCredit(Project project)
    {
        var folder = new WorkFolder(project.WorkFolderPath);
        var rec = ReadJson<PostalsCopy>(Path.Combine(folder.Data, PostalsRecord));
        var copy = Path.Combine(folder.Data, PostalCodes.FileName);
        return rec is null || !File.Exists(copy) ? null : PostalCodes.Credit(File.ReadAllText(copy), rec.Source);
    }

    /// <summary>The SHA-256 of the points of interest and their styles the project uses (its folder and file, or the bundled ones).</summary>
    public static string PoiSha(Project project) => PoiData.Sha256(project);

    public static bool IsStale(Project project, StateStore state, string language)
    {
        if (state.StageDone(StageKeys.Labels, language) is not { } done) return true;
        foreach (var (key, unit) in new[] { (StageKeys.RoadGraph, StageKeys.World), (StageKeys.Regions, StageKeys.World), (StageKeys.GameFiles, StageKeys.World) })
            if (state.StageDone(key, unit) is { } t && t > done) return true;
        var folder = new WorkFolder(project.WorkFolderPath);
        var rec = ReadJson<LabelsRecord>(RecordPath(folder, language));
        if (rec is null) return true;
        foreach (var (key, _, _) in Sections(project, language))
            if (!rec.Files.Any(f => f.Key == key) || !File.Exists(Path.Combine(folder.Data, LabelsFile.FileName(language, key)))) return true;
        if (CurrentCopy(project) is not { } copy || copy.Sha256 != rec.PostalsSha256) return true;
        if (RoadEditsFile.StreetsDigestOf(project) != rec.StreetsSha256) return true;
        if (rec.CayoPerico != project.File.CayoPerico) return true;
        return PoiSha(project) != rec.PoiSha256;
    }

    public override void Run(UnitContext ctx)
    {
        var sw = Stopwatch.StartNew();
        string lang = ctx.Unit;
        var folder = ctx.Folder;
        ctx.Report("postal codes", 0);
        var copy = EnsurePostals(ctx.Project, ctx.Log, ctx.Token);
        var postals = PostalCodes.Parse(File.ReadAllText(Path.Combine(folder.Data, PostalCodes.FileName)), "data/" + PostalCodes.FileName);
        ctx.Report("read", 0.1);
        var zones = ZoneGrid.Load(Path.Combine(folder.Data, ZoneGrid.FileName));
        IReadOnlyList<StreetName> added;
        try { added = RoadEditsFile.StreetsOf(ctx.Project); }
        catch (RoadEditsException ex) { throw new LabelsException("the street names of the road edits: " + ex.Message); }
        var names = GameNames.Read(Path.Combine(folder.Game, GameFilesOutput.Names)).WithStreets(added.Select(s => (s.Hash, s.En, s.Ja)));
        var roads = RoadGraphFile.Read(Path.Combine(folder.Data, RoadGraphFile.Roads));
        PoiData poi;
        try { poi = PoiData.Of(ctx.Project); }
        catch (PoiException ex) { throw new LabelsException("points of interest: " + ex.Message); }
        var pois = poi.Resolve().Where(p => p.Show.Atlas && p.Visible).ToList();
        var metrics = Metrics?.Invoke() ?? new SkiaTextMetrics();
        var files = new List<LabelsKey>();
        try
        {
            var sections = Sections(ctx.Project, lang);
            for (int i = 0; i < sections.Count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var (key, style, maps) = sections[i];
                ctx.Report("place", 0.2 + 0.8 * i / sections.Count);
                var input = new LabelInput
                {
                    Frame = (zones.X0, zones.Y0, zones.X0 + zones.FrameWidth, zones.Y0 - zones.FrameHeight),
                    Postals = postals, Pois = pois, Zones = zones, Names = names, Roads = roads, Routes = Routes.Bundled,
                    Style = style, Language = lang, Metrics = metrics, UnnamedZones = UnnamedZones(ctx.Project),
                };
                var labels = LabelPlacer.Place(input, ctx.Log);
                var path = Path.Combine(folder.Data, LabelsFile.FileName(lang, key));
                LabelsFile.Write(path, lang, key, input.Frame, labels);
                // what each cell's drawing takes of the labels, as the drawing reads them: a cell is drawn again only when its part changed
                var written = LabelsFile.Read(path).Labels;
                var cells = CellPlan.For(ctx.Project.Range.Keys).ToDictionary(c => c.Id.Name, c => Render.CellInputs.LabelsDigest(written, new Cells.CellArea(c).Rect));
                files.Add(new LabelsKey(key, maps, labels.Count(l => l.Kind == "postal"), labels.Count(l => l.Kind == "zone"), labels.Count(l => l.Kind == "street"),
                    labels.Count(l => l.Route is not null), cells));
            }
        }
        finally
        {
            (metrics as IDisposable)?.Dispose();
        }
        var missing = (metrics as SkiaTextMetrics)?.Missing.ToList() ?? [];
        if (missing.Count > 0) ctx.Log($"labels ({lang}): fonts not installed, the default font was used instead: {string.Join(", ", missing)}");
        var rec = new LabelsRecord(lang, files, copy.Sha256, PoiSha(ctx.Project), missing, Math.Round(sw.Elapsed.TotalSeconds, 1), RoadEditsFile.StreetsDigest(added),
            ctx.Project.File.CayoPerico);
        GameFilesOutput.WriteAtomically(RecordPath(folder, lang), fs => JsonSerializer.Serialize(fs, rec, Project.Json));
        ctx.Log($"labels ({lang}): {string.Join("; ", files.Select(f => $"{LabelsFile.FileName(lang, f.Key)} for {string.Join(", ", f.Maps)}: postal {f.Postal}, zone {f.Zone}, street {f.Street} (route numbers {f.Routes})"))};"
            + (added.Count == 0 ? "" : $" {added.Count} street names of the road edits;") + $" {sw.Elapsed.TotalSeconds:0.0} s");
    }
}
