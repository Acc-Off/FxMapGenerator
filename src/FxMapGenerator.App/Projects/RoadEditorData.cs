using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.App.Projects;

/// <summary>
/// What the road editor reads from the open project's work folder, kept while the files stay the same: the game's path
/// data (<c>game/paths.json</c>, with the street names of <c>game/names.json</c>) in a compact form for the screen and
/// the text of each node and link for their values, the road shapes last made (<c>data/road-shapes.grid</c>) with an
/// index for drawing tiles, and the ground scans of the blocks read last.
/// </summary>
public sealed class RoadEditorData
{
    const int MaxScans = 48;

    readonly object _sync = new();
    Paths? _paths;
    Shapes? _shapes;
    readonly Dictionary<string, ScanFile> _scans = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _scanOrder = new();

    /// <summary>The path data as read: the file's bytes, the parsed file, the compact form and where each node and link is in the text.</summary>
    public sealed class Paths
    {
        public required string File { get; init; }
        public required string Version { get; init; }
        public required byte[] Bytes { get; init; }
        /// <summary>As the road graph and road shapes read it (never changed here: the edits are only checked against it).</summary>
        public required PathFile Parsed { get; init; }
        public required RoadPathsDto Dto { get; init; }
        public required Dictionary<string, (int Start, int Length)> NodeText { get; init; }
        public required Dictionary<(string, string), List<(int Start, int Length)>> LinkText { get; init; }
        public required Dictionary<uint, StreetNameDto> Streets { get; init; }

        public string Text((int Start, int Length) at) => System.Text.Encoding.UTF8.GetString(Bytes, at.Start, at.Length);
    }

    /// <summary>
    /// Road shapes computed from edits not saved yet (the right map follows the edits), by id. <see cref="Provisional"/>:
    /// made while blocks of the range had no landcover yet (roads on water are not left out there, and paving is the
    /// path file's flag).
    /// </summary>
    public sealed class Preview
    {
        public required string Id { get; init; }
        public required RoadShapesIndex Index { get; init; }
        public required double Seconds { get; init; }
        public required bool Provisional { get; init; }
    }

    /// <summary>The landcover's water and surface layers over the whole map, as the road shapes read them (the preview's).</summary>
    sealed class Grids
    {
        public required string Key { get; init; }
        public required RoadNet.WorldRaster World { get; init; }
        /// <summary>Blocks of the range had no landcover when the layers were read.</summary>
        public required bool Provisional { get; init; }
        public required DateTime ReadUtc { get; init; }
    }

    Grids? _grids;
    readonly List<Preview> _previews = new();
    CancellationTokenSource? _previewCancel;
    int _previewCount;
    /// <summary>Previews kept (the tiles of the one before may still load while the next is shown).</summary>
    const int KeepPreviews = 3;

    /// <summary>
    /// The road shapes the next run would make with <paramref name="edits"/> in place of the saved ones, made the way the
    /// road shapes step makes them (<see cref="RoadsStage.Make"/>) from the path data and the landcover's layers (read
    /// once and kept). A newer request cancels one still being made (<see cref="OperationCanceledException"/>). Null
    /// before the game files are read. While blocks of the range have no landcover yet (before the capture is over), the
    /// shapes are provisional: roads on such blocks are all kept and their paving is the path file's flag.
    /// </summary>
    public Preview? PreviewOf(Project project, RoadEditSet edits, CancellationToken aborted)
    {
        var paths = PathsOf(project);
        if (paths is null) return null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        lock (_sync)
        {
            _previewCancel?.Cancel();
            _previewCancel = cts;
        }
        try
        {
            var grids = GridsOf(project, cts.Token);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var p = paths.Parsed.Clone();
            edits.ApplyTo(p);
            var made = RoadsStage.Make(p, Path.Combine(new WorkFolder(project.WorkFolderPath).Game, GameFilesOutput.Names), grids.World, cts.Token);
            var index = new RoadShapesIndex(RoadShapesFile.ContentsOf(made.Shapes, made.Tunnels));
            lock (_sync)
            {
                cts.Token.ThrowIfCancellationRequested();
                var preview = new Preview { Id = "preview-" + ++_previewCount, Index = index, Seconds = Math.Round(sw.Elapsed.TotalSeconds, 2), Provisional = grids.Provisional };
                _previews.Add(preview);
                if (_previews.Count > KeepPreviews) _previews.RemoveAt(0);
                return preview;
            }
        }
        finally
        {
            lock (_sync)
                if (_previewCancel == cts) _previewCancel = null;
            cts.Dispose();
        }
    }

    /// <summary>A preview made last (by id), or null when it is gone.</summary>
    public Preview? PreviewById(string id)
    {
        lock (_sync) return _previews.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>How long layers read while blocks had no landcover are kept as they are (a run's landcover step adds blocks one by one).</summary>
    static readonly TimeSpan ProvisionalKept = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The water and surface layers of the range's landcover, read in parallel once per landcover (the road shapes' version
    /// marks it). Blocks without landcover yet are land without a class (<see cref="RoadNet.WorldGrids"/>), and the layers
    /// are read again as more blocks get theirs.
    /// </summary>
    Grids GridsOf(Project project, CancellationToken token)
    {
        var folder = new WorkFolder(project.WorkFolderPath);
        var blocks = project.Range.Keys.OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        var made = blocks.Where(b => File.Exists(Core.Landcover.LandcoverFile.PathOf(folder.Data, b))).ToList();
        var notMade = blocks.Except(made).ToList();
        var frame = project.Frame;
        var range = folder.Data + "|" + frame + "|" + string.Join(",", blocks.Select(b => b.Name));
        var key = range + "|" + (VersionOf(Path.Combine(folder.Data, RoadShapesFile.FileName)) ?? "none") + "|" + notMade.Count;
        lock (_sync)
            if (_grids is { } g && (g.Key == key || (g.Provisional && notMade.Count > 0 && g.Key.StartsWith(range + "|", StringComparison.Ordinal) && DateTime.UtcNow - g.ReadUtc < ProvisionalKept)))
                return g;
        var world = RoadNet.WorldGrids(folder.Data, made, frame, new Core.Jobs.FixedParallel(Math.Max(1, Environment.ProcessorCount / 2), token), token, notMade);
        lock (_sync)
        {
            _grids = new Grids { Key = key, World = world, Provisional = notMade.Count > 0, ReadUtc = DateTime.UtcNow };
            return _grids;
        }
    }

    /// <summary>The road shapes last made and their index.</summary>
    public sealed class Shapes
    {
        public required string File { get; init; }
        public required string Version { get; init; }
        public required RoadShapesIndex Index { get; init; }
    }

    /// <summary>A file's version for the screen (length and time), or null when it is not there.</summary>
    public static string? VersionOf(string path)
    {
        var f = new FileInfo(path);
        return f.Exists ? $"{f.Length:x}-{f.LastWriteTimeUtc.Ticks:x}" : null;
    }

    /// <summary>The project's path data, or null before the game files are read.</summary>
    public Paths? PathsOf(Project project)
    {
        var folder = new WorkFolder(project.WorkFolderPath);
        var file = Path.Combine(folder.Game, GameFilesOutput.Paths);
        var names = Path.Combine(folder.Game, GameFilesOutput.Names);
        if (VersionOf(file) is not { } v) return null;
        var version = v + "/" + (VersionOf(names) ?? "none");
        lock (_sync)
        {
            if (_paths is { } p && string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase) && p.Version == version) return p;
            _paths = null;
            _paths = Read(file, names, version);
            return _paths;
        }
    }

    /// <summary>The road shapes last made, or null before the road shapes step ran.</summary>
    public Shapes? ShapesOf(Project project)
    {
        var file = Path.Combine(new WorkFolder(project.WorkFolderPath).Data, RoadShapesFile.FileName);
        if (VersionOf(file) is not { } version) return null;
        lock (_sync)
        {
            if (_shapes is { } s && string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase) && s.Version == version) return s;
            _shapes = null;
            _shapes = new Shapes { File = file, Version = version, Index = new RoadShapesIndex(RoadShapesFile.Read(file)) };
            return _shapes;
        }
    }

    /// <summary>The ground scan of a block of the project, or null when there is none (kept for the blocks read last).</summary>
    public ScanFile? ScanOf(Project project, BlockId b)
    {
        var file = new WorkFolder(project.WorkFolderPath).ScanFile(b);
        if (VersionOf(file) is not { } version) return null;
        var key = file + "|" + version;
        lock (_sync)
        {
            if (_scans.TryGetValue(key, out var s))
            {
                _scanOrder.Remove(key);
                _scanOrder.AddFirst(key);
                return s;
            }
        }
        var read = ScanFile.Read(file);
        lock (_sync)
        {
            if (!_scans.ContainsKey(key))
            {
                _scans[key] = read;
                _scanOrder.AddFirst(key);
                while (_scanOrder.Count > MaxScans)
                {
                    _scans.Remove(_scanOrder.Last!.Value);
                    _scanOrder.RemoveLast();
                }
            }
        }
        return read;
    }

    /// <summary>Node flags in <see cref="RoadPathsDto.Flags"/>.</summary>
    public const int Junction = 1, Highway = 2, Tunnel = 4, Unpaved = 8, SwitchedOff = 16;
    /// <summary>Link flags in <see cref="RoadPathsDto.LinkFlags"/>.</summary>
    public const int Narrow = 1, DontUseForNavigation = 2, Shortcut = 4;

    static Paths Read(string file, string namesFile, string version)
    {
        var bytes = File.ReadAllBytes(file);
        var parsed = PathFile.Parse(bytes);
        var (nodeText, linkText) = Offsets(bytes);
        var index = new Dictionary<string, int>(parsed.Nodes.Count, StringComparer.Ordinal);
        int n = parsed.Nodes.Count;
        var keys = new string[n];
        double[] x = new double[n], y = new double[n], z = new double[n];
        var street = new uint[n];
        var flags = new int[n];
        for (int i = 0; i < n; i++)
        {
            var v = parsed.Nodes[i];
            index[v.Key] = i;
            (keys[i], x[i], y[i], z[i], street[i]) = (v.Key, v.X, v.Y, v.Z, v.Street);
            flags[i] = (v.Junction ? Junction : 0) | (v.Highway ? Highway : 0) | (v.Tunnel ? Tunnel : 0) | (v.Unpaved ? Unpaved : 0) | (v.SwitchedOff ? SwitchedOff : 0);
        }
        // one entry a connection, the way its first record in the file goes (as the road edits compare lanes)
        var pairs = new Dictionary<(string, string), int>();
        List<int> a = new(), b = new(), lf = new(), lb = new(), lflags = new();
        foreach (var l in parsed.Links)
        {
            if (!index.TryGetValue(l.From, out var ia) || !index.TryGetValue(l.To, out var ib)) continue;
            var pair = string.CompareOrdinal(l.From, l.To) < 0 ? (l.From, l.To) : (l.To, l.From);
            int f = (l.Narrow ? Narrow : 0) | (l.DontUseForNavigation ? DontUseForNavigation : 0) | (l.Shortcut ? Shortcut : 0);
            if (pairs.TryGetValue(pair, out var k))
            {
                lflags[k] |= f;
                continue;
            }
            pairs[pair] = a.Count;
            a.Add(ia); b.Add(ib); lf.Add(l.LanesForward); lb.Add(l.LanesBack); lflags.Add(f);
        }
        var streets = ReadStreets(namesFile);
        var dto = new RoadPathsDto(version, keys, x, y, z, street, flags, a.ToArray(), b.ToArray(), lf.ToArray(), lb.ToArray(), lflags.ToArray(),
            streets.Values.OrderBy(s => s.En, StringComparer.OrdinalIgnoreCase).ToList());
        return new Paths { File = file, Version = version, Bytes = bytes, Parsed = parsed, Dto = dto, NodeText = nodeText, LinkText = linkText, Streets = streets };
    }

    /// <summary>The game's street names (hash -> English, Japanese) of <c>game/names.json</c>; empty without it.</summary>
    static Dictionary<uint, StreetNameDto> ReadStreets(string path)
    {
        var o = new Dictionary<uint, StreetNameDto>();
        if (!File.Exists(path)) return o;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("streets", out var list)) return o;
            foreach (var e in list.EnumerateObject())
                if (uint.TryParse(e.Name, out var h))
                {
                    string? Name(string lang) => e.Value.TryGetProperty(lang, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    o[h] = new StreetNameDto(h, Name("en") ?? e.Name, Name("ja"));
                }
        }
        catch (JsonException) { }
        return o;
    }

    /// <summary>Where each node's object and each link record lies in the file's text.</summary>
    static (Dictionary<string, (int, int)>, Dictionary<(string, string), List<(int, int)>>) Offsets(byte[] json)
    {
        var nodes = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var links = new Dictionary<(string, string), List<(int, int)>>();
        var r = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return (nodes, links);
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            var name = r.GetString();
            r.Read();
            if (name == "nodes" && r.TokenType == JsonTokenType.StartObject)
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    var key = r.GetString()!;
                    r.Read();
                    int start = (int)r.TokenStartIndex;
                    r.Skip();
                    nodes[key] = (start, (int)r.BytesConsumed - start);
                }
            else if (name == "links" && r.TokenType == JsonTokenType.StartArray)
                while (r.Read() && r.TokenType == JsonTokenType.StartObject)
                {
                    int start = (int)r.TokenStartIndex, depth = r.CurrentDepth;
                    string? from = null, to = null;
                    while (r.Read() && !(r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == depth))
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        var p = r.GetString();
                        r.Read();
                        if (p == "from") from = r.GetString();
                        else if (p == "to") to = r.GetString();
                        else r.Skip();
                    }
                    if (from is null || to is null) continue;
                    var pair = string.CompareOrdinal(from, to) < 0 ? (from, to) : (to, from);
                    if (!links.TryGetValue(pair, out var list)) links[pair] = list = new List<(int, int)>();
                    list.Add((start, (int)r.BytesConsumed - start));
                }
            else r.Skip();
        }
        return (nodes, links);
    }
}

/// <summary>A street name of the game: its hash, English and Japanese (null when the game files have none).</summary>
public sealed record StreetNameDto(uint Hash, string En, string? Ja);

/// <summary>
/// <c>GET /api/project/road-editor/paths</c>: the game's path data for drawing, as columns. Nodes: <c>keys</c>
/// (<c>area:node</c>), position, street name hash (0 none), flags (1 junction, 2 highway, 4 tunnel, 8 unpaved,
/// 16 switched off). Links: one per connection, from <c>linkA</c> to <c>linkB</c> (node indexes, the way the
/// connection's first record in the file goes), lanes that way and back, flags (1 narrow, 2 not for navigation, 4
/// shortcut). The street names to choose from.
/// </summary>
public sealed record RoadPathsDto(string Version, string[] Keys, double[] X, double[] Y, double[] Z, uint[] Street, int[] Flags,
    int[] LinkA, int[] LinkB, int[] LanesForward, int[] LanesBack, int[] LinkFlags, IReadOnlyList<StreetNameDto> Streets);
