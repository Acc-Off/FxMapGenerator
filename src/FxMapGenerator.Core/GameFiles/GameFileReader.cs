using System.Text.RegularExpressions;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;
using FxMapGenerator.GameData.Archives;
using FxMapGenerator.GameData.Gta;
using FxMapGenerator.GameData.Paths;
using FxMapGenerator.GameData.Text;

namespace FxMapGenerator.Core.GameFiles;

/// <summary>
/// Reads what the maps need from the game files: the path node files of the map's areas (the game's own, then the
/// server's replacing whole areas) and the name tables of the languages. One pass over the archives for both.
/// </summary>
public static partial class GameFileReader
{
    /// <summary>Area grid of the path node files: 32 x 32 areas of 512 m from (-8192, -8192), id = row * 32 + column.</summary>
    public const double AreaSize = 512, AreaOrigin = -8192;
    public const int AreaColumns = 32, AreaRows = 32;

    /// <summary>
    /// How far above an area's number the Cayo Perico island's road file for that area is numbered (nodes1177.ynd holds
    /// area 153's island roads; its nodes carry area 153). The game reads these files in place of the areas' own near the
    /// island; the read takes them with <see cref="ProjectFile.CayoPerico"/>.
    /// </summary>
    public const int IslandFileOffset = 1024;

    /// <summary>The game languages read, by the language key used in the name files.</summary>
    public static readonly IReadOnlyDictionary<string, string> Languages = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["en"] = "american",
        ["ja"] = "japanese",
    };

    /// <summary>
    /// The areas of the grid under the frame, row by row from the south (every id, also those without a file). The grid
    /// ends at 8192 m: the frame's strip north of it (and any cells added past that) has no areas.
    /// </summary>
    public static IReadOnlyList<int> MapAreas(MapFrame frame)
    {
        int cx0 = Math.Max(0, (int)Math.Floor((frame.X0 - AreaOrigin) / AreaSize)), cx1 = Math.Min(AreaColumns - 1, (int)Math.Floor((frame.X1 - AreaOrigin) / AreaSize));
        int cy0 = Math.Max(0, (int)Math.Floor((frame.Y1 - AreaOrigin) / AreaSize)), cy1 = Math.Min(AreaRows - 1, (int)Math.Floor((frame.Y0 - AreaOrigin) / AreaSize));
        var list = new List<int>();
        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++) list.Add(cy * AreaColumns + cx);
        return list;
    }

    /// <param name="Island">The areas read from the island's road files (<see cref="IslandFileOffset"/>; empty unless asked for).</param>
    /// <param name="ServerFileErrors">The server's files that were not read, each with the reason (the area keeps the file it had).</param>
    /// <param name="NumberMismatches">The files whose nodes carry another area or node number than the file's name and their place give (the name and the place count).</param>
    public sealed record Result(
        IReadOnlyList<int> Areas,
        IReadOnlyDictionary<int, YndFile> Files,
        IReadOnlyDictionary<int, string> Sources,
        IReadOnlyList<Replacement> Replaced,
        IReadOnlyList<string> Ties,
        IReadOnlyList<string> Overridden,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, string>> Names,
        IReadOnlyDictionary<string, int> NameFiles,
        int Archives,
        IReadOnlyList<string> ArchiveErrors,
        IReadOnlyList<int> Island,
        IReadOnlyList<string> ServerFileErrors,
        IReadOnlyList<string> NumberMismatches);

    /// <summary>A server file that took an area's place (<paramref name="Replaces"/>: the game's file it replaced, or null for a new area).</summary>
    public sealed record Replacement(int Area, string Path, string? Replaces);

    /// <summary>
    /// Reads the path node files of the frame's areas (<see cref="MapAreas"/>) and the name tables. Game files need the
    /// GTA V folder and the keys; with <paramref name="gameFolder"/> null only the server's files are read (and no names).
    /// The server's files replace whole areas, <paramref name="serverResources"/> in order and each in
    /// <see cref="LooseYnd.Read"/>'s order: of two files for one area the later wins (as a resource ensured later on the
    /// server). With <paramref name="island"/>, every area that has an island road file (<see cref="IslandFileOffset"/>, the
    /// game's or a server's, taken the same way) is read from it and never from its own number's files: the island's
    /// areas all come from one set, as the game streams them together. A server's file that cannot be read (encrypted,
    /// another game's format, damaged) is passed over with its reason and the area keeps the file it had.
    /// </summary>
    public static Result Read(string? gameFolder, string? keysFolder, IReadOnlyList<string> serverResources, MapFrame frame, bool island = false,
        Action<string>? log = null, CancellationToken token = default)
    {
        var areas = MapAreas(frame);
        var want = areas.ToHashSet();
        // the file numbers read: the areas', and with the island its files for those areas
        bool Wanted(int number) => want.Contains(number) || (island && number >= IslandFileOffset && want.Contains(number - IslandFileOffset));
        var files = new Dictionary<int, YndFile>();
        var sources = new Dictionary<int, string>();
        var ties = new List<string>();
        var tables = Languages.Keys.ToDictionary(k => k, _ => new Dictionary<uint, string>());
        var nameFiles = Languages.Keys.ToDictionary(k => k, _ => 0);
        int archives = 0;
        var errors = new List<string>();
        if (gameFolder is not null)
        {
            if (keysFolder is null) throw new InvalidOperationException("the RPF keys are needed to read the game files");
            GtaKeys.Install(keysFolder);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var ix = RpfIndex.Open(gameFolder, n => YndFile.AreaOf(n) >= 0 || n.EndsWith(".gxt2", StringComparison.OrdinalIgnoreCase), token);
            archives = ix.ArchiveCount;
            errors.AddRange(ix.Errors);
            log?.Invoke($"{ix.ArchiveCount} archives indexed in {sw.ElapsedMilliseconds} ms ({ix.Entries.Count} path node / text files)");
            foreach (var group in ix.Entries.Where(e => YndFile.AreaOf(e.Name) >= 0).GroupBy(e => YndFile.AreaOf(e.Name)).Where(g => Wanted(g.Key)))
            {
                token.ThrowIfCancellationRequested();
                int top = group.Max(e => Priority(e.Path));
                var best = group.Where(e => Priority(e.Path) == top).ToList();
                if (best.Count > 1) ties.Add($"area {group.Key}: {best.Count} files at the same priority, the first taken: {string.Join(" | ", best.Select(e => e.Path))}");
                files[group.Key] = YndFile.Read(AreaOfNumber(group.Key), RpfIndex.Read(best[0].File));
                sources[group.Key] = best[0].Path;
            }
            foreach (var (key, language) in Languages)
            {
                var rx = new Regex(@"\\lang\\" + Regex.Escape(language) + @"(_rel|dlc)?\.rpf\\", RegexOptions.IgnoreCase);
                foreach (var e in ix.Entries.Where(e => e.Name.EndsWith(".gxt2", StringComparison.OrdinalIgnoreCase) && rx.IsMatch(e.Path)))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var (h, t) in Gxt2File.Read(RpfIndex.Read(e.File))) tables[key][h] = t;
                    nameFiles[key]++;
                }
                log?.Invoke($"names {key} ({language}): {tables[key].Count} entries from {nameFiles[key]} files");
            }
        }
        var replaced = new SortedDictionary<int, Replacement>();
        var overridden = new List<string>();
        var serverErrors = new List<string>();
        var game = new Dictionary<int, string>(sources);
        foreach (var source in serverResources)
            foreach (var found in LooseYnd.Read(source, token))
            {
                if (!Wanted(found.Area)) continue;
                YndFile file;
                try { file = YndFile.Read(AreaOfNumber(found.Area), found.Bytes); }
                catch (InvalidDataException e)
                {
                    serverErrors.Add($"{found.Path}: {e.Message}");
                    continue;
                }
                if (replaced.TryGetValue(found.Area, out var earlier))
                    overridden.Add($"area {found.Area}: {earlier.Path} overridden by {found.Path} (later in the order)");
                replaced[found.Area] = new Replacement(found.Area, found.Path, game.GetValueOrDefault(found.Area));
                files[found.Area] = file;
                sources[found.Area] = found.Path;
            }

        // the island's files take their areas' places, all of them (links cross from one island area to the next)
        var islandAreas = new List<int>();
        if (island)
        {
            foreach (int number in files.Keys.Where(n => n >= IslandFileOffset).Order().ToList())
            {
                int area = number - IslandFileOffset;
                if (sources.TryGetValue(area, out var own)) overridden.Add($"area {area}: {own} not read, the island's {sources[number]} takes its place");
                replaced.Remove(area);
                files[area] = files[number];
                sources[area] = sources[number];
                files.Remove(number);
                sources.Remove(number);
                islandAreas.Add(area);
            }
            log?.Invoke(islandAreas.Count == 0
                ? "Cayo Perico: no island road file for the areas of the map's frame"
                : $"Cayo Perico: {islandAreas.Count} areas read from the island's road files ({string.Join(", ", islandAreas)})");
        }
        foreach (var r in replaced.Values) log?.Invoke($"area {r.Area} <- {r.Path}" + (r.Replaces is null ? " (new area)" : $" (replaces {r.Replaces})"));
        foreach (var o in overridden) log?.Invoke("  " + o);
        foreach (var e in serverErrors) log?.Invoke("  server file not read: " + e);
        var mismatches = new List<string>();
        foreach (var (area, f) in files.OrderBy(kv => kv.Key))
        {
            if (f.OtherAreaNodes == 0 && f.OtherNumberNodes == 0) continue;
            mismatches.Add($"area {area}: {sources[area]}: {f.OtherAreaNodes} of {f.Nodes.Count} nodes carry another area, {f.OtherNumberNodes} another node number; the file's name and the nodes' order count");
        }
        foreach (var m in mismatches) log?.Invoke("  " + m);
        return new Result(areas, files, sources, replaced.Values.ToList(), ties, overridden,
            tables.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<uint, string>)kv.Value), nameFiles, archives, errors, islandAreas, serverErrors, mismatches);
    }

    /// <summary>The area a path node file number holds (an island file's number less <see cref="IslandFileOffset"/>).</summary>
    static int AreaOfNumber(int number) => number >= IslandFileOffset ? number - IslandFileOffset : number;

    /// <summary>Which copy of an area counts: update.rpf 3 &gt; the update's DLC packs 2 &gt; other DLC packs 1 &gt; the base archives 0.</summary>
    public static int Priority(string path) =>
        UpdateRpf().IsMatch(path) ? 3 : UpdateDlc().IsMatch(path) ? 2 : AnyDlc().IsMatch(path) ? 1 : 0;

    [GeneratedRegex(@"(^|\\)update\\update\.rpf\\", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateRpf();
    [GeneratedRegex(@"(^|\\)update\\x64\\dlcpacks\\", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateDlc();
    [GeneratedRegex(@"\\dlcpacks\\", RegexOptions.IgnoreCase)]
    private static partial Regex AnyDlc();
}
