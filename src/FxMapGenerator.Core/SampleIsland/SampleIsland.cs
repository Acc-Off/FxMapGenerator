using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.SampleIsland;

/// <summary>
/// The made-up land the style editor shows when a project has no map data of its own, on a regular grid: what a scan
/// would find at every node (node (r, c) at (<see cref="X0"/> + c step, <see cref="Y0"/> - r step), game metres).
/// </summary>
public sealed class IslandSurface
{
    public required double X0 { get; init; }
    public required double Y0 { get; init; }
    public required double Step { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>The ground (under water: the bed).</summary>
    public required float[] Ground { get; init; }
    /// <summary>The first thing hit from above (a roof, a deck, a pool's floor); null = the ground everywhere.</summary>
    public float[]? Top { get; init; }
    /// <summary>The water surface (sea, lakes, rivers), NaN where there is none.</summary>
    public required float[] Water { get; init; }
    /// <summary>The water-collision probe (pools, ponds), NaN where it finds none; null = none anywhere.</summary>
    public float[]? Probe { get; init; }
    /// <summary>The material hit, as an index into <see cref="MaterialNames"/> (the game's names; index 0 = no hit).</summary>
    public required byte[] Material { get; init; }
    public required IReadOnlyList<string> MaterialNames { get; init; }
    /// <summary>The zone, as an index into <see cref="ZoneCodes"/> (index 0 = no zone).</summary>
    public required byte[] Zone { get; init; }
    public required IReadOnlyList<string> ZoneCodes { get; init; }
    /// <summary>On a road (the road scan's on-road samples); null = nowhere.</summary>
    public bool[]? OnRoad { get; init; }
    /// <summary>The nearest street's name hash; null = none anywhere.</summary>
    public uint[]? Street { get; init; }
}

/// <summary>A vehicle path node of the made-up land, in the shape of the game files step's <c>paths.json</c>.</summary>
public sealed record IslandNode(string Key, double X, double Y, double Z, uint Street, bool Junction, bool Highway, bool Tunnel, bool Unpaved, bool SwitchedOff,
    double[]? JunctionArea = null);

/// <summary>A connection between two nodes (written once from each end).</summary>
public sealed record IslandLink(string From, string To, int LanesForward, int LanesBack, bool Narrow);

/// <summary>Everything of the made-up land: its surface, roads, names, postal codes and points of interest.</summary>
public sealed class IslandData
{
    public required IslandSurface Surface { get; init; }
    public IReadOnlyList<IslandNode> Nodes { get; init; } = [];
    public IReadOnlyList<IslandLink> Links { get; init; } = [];
    public IReadOnlyDictionary<uint, (string En, string Ja)> Streets { get; init; } = new Dictionary<uint, (string, string)>();
    public required IReadOnlyDictionary<string, (string En, string Ja)> Zones { get; init; }
    public IReadOnlyList<(string Code, double X, double Y)> Postals { get; init; } = [];
    public IReadOnlyList<(string Id, double X, double Y)> Markers { get; init; } = [];
    public IReadOnlyList<(string Id, double X, double Y, string Color)> Dots { get; init; } = [];
}

/// <summary>
/// Writes the made-up land as a project of its own: the scans a visit would have taken of the blocks it covers, the
/// path and name files the game files step would have written, the postal codes and the points of interest, so the
/// steps without the game make its maps the way they make any project's. Nothing of the game is in it.
/// </summary>
public static class SampleIsland
{
    /// <summary>The cell the land lies in (the top-left one of the map's frame, open sea in the game).</summary>
    public static CellId Cell { get; } = new(0, 0);

    public const string ProjectName = "sample-island";

    /// <summary>The cell's 8 x 8 blocks.</summary>
    public static IReadOnlyList<BlockId> Blocks { get; } = BlocksOf(Cell);

    /// <summary>A cell's 8 x 8 blocks, row by row.</summary>
    public static IReadOnlyList<BlockId> BlocksOf(CellId cell) =>
        Enumerable.Range(0, WorldGrid.CellBlocks * WorldGrid.CellBlocks)
            .Select(i => new BlockId(cell.C * WorldGrid.CellBlocks + i % WorldGrid.CellBlocks, cell.R * WorldGrid.CellBlocks + i / WorldGrid.CellBlocks)).ToList();

    /// <summary>The cell's west, north, east and south edges (game metres).</summary>
    public static (double X0, double Y0, double X1, double Y1) Rect { get; } =
        (WorldGrid.Left + Cell.C * WorldGrid.CellBlocks * WorldGrid.BlockSize, WorldGrid.Top - Cell.R * WorldGrid.CellBlocks * WorldGrid.BlockSize,
         WorldGrid.Left + (Cell.C + 1) * WorldGrid.CellBlocks * WorldGrid.BlockSize, WorldGrid.Top - (Cell.R + 1) * WorldGrid.CellBlocks * WorldGrid.BlockSize);

    /// <summary>
    /// Writes the land's project into <paramref name="folder"/> (its work folder is the same folder): the project file
    /// (atlas maps of the two bundled atlas styles in English and Japanese), the scans, <c>game/</c>, the postal codes and
    /// the points of interest (their groups named as the bundled ones are, in English and Japanese), and the state (every
    /// block scanned, the game files read) as of <paramref name="takenUtc"/> (default: two seconds ago; the game files a
    /// second later, never after now, so a run started at once finds the steps it makes newer than them).
    /// <paramref name="cell"/> is the cell whose blocks are scanned (default <see cref="Cell"/>; the land's data must lie
    /// there) and <paramref name="extraCells"/> the cells the project adds around the map (default none).
    /// Returns the project file's path.
    /// </summary>
    public static string Create(string folder, IslandData data, DateTime? takenUtc = null, CellId? cell = null, ExtraCellsSetting? extraCells = null)
    {
        var at = takenUtc ?? DateTime.UtcNow.AddSeconds(-2);
        var blocks = cell is { } c ? BlocksOf(c) : Blocks;
        var work = new WorkFolder(folder);
        Directory.CreateDirectory(work.Scan);
        Directory.CreateDirectory(work.Game);
        var writer = new ScanWriter(data, Materials.Default);
        Parallel.ForEach(blocks, new ParallelOptions { MaxDegreeOfParallelism = 4 }, b =>
        {
            var path = work.ScanFile(b);
            File.WriteAllText(path, writer.Text(b), Utf8);
            File.SetLastWriteTimeUtc(path, at);
        });
        WritePaths(Path.Combine(work.Game, GameFilesOutput.Paths), data);
        WriteNames(Path.Combine(work.Game, GameFilesOutput.Names), data);
        int areas = data.Nodes.Select(n => n.Key[..n.Key.IndexOf(':')]).Distinct().Count();
        var record = new GameFilesStage.SourcesRecord("", "", [], 0, [], areas, data.Nodes.Count, 2 * data.Links.Count, data.Nodes.Count(n => n.JunctionArea is not null),
            [], [], [], new Dictionary<string, int>(), blocks.Count, data.Streets.Count, 0, data.Streets.Count, data.Streets.Count, data.Zones.Count, 0,
            extraCells is null ? null : [extraCells.Top, extraCells.Bottom, extraCells.Left, extraCells.Right]);
        File.WriteAllText(Path.Combine(work.Game, GameFilesOutput.Record), JsonSerializer.Serialize(record, Project.Json), Utf8);
        File.WriteAllText(Path.Combine(folder, "postals.json"), PostalsJson(data), Utf8);
        Directory.CreateDirectory(Path.Combine(folder, "poi"));
        File.WriteAllText(Path.Combine(folder, "poi", "markers.json"), PointsJson("Markers", "マーカー", 0, "highway-marker", data.Markers.Select(m => (m.Id, m.Id, m.X, m.Y, (string?)null))), Utf8);
        File.WriteAllText(Path.Combine(folder, "poi", "dots.json"), PointsJson("Colored dots", "色付きの丸", 1, "colored-dot", data.Dots.Select(d => (d.Id, "", d.X, d.Y, (string?)d.Color))), Utf8);

        var projectPath = Path.Combine(folder, ProjectName + ProjectFile.Extension);
        var project = Project.Create(projectPath, "Sample island");
        var f = project.File;
        f.Maps.Satellite = false;
        f.Maps.Atlas.Enabled = true;
        f.Maps.Atlas.Styles = [AtlasPresets.PostalCodeMap, AtlasPresets.Regional];
        f.Maps.Atlas.Languages = ["en", "ja"];
        f.Maps.Roadmap = false;
        f.Range.Base = "none";
        f.Range.ExtraCells = extraCells;
        f.Range.Add = blocks.Select(b => b.Name).ToList();
        f.HeightQuality = "balance";
        f.Postals = "postals.json";
        f.Poi = "poi";
        project.Save();

        var state = StateStore.Open(work);
        state.SetItems(blocks.SelectMany(b => new[] { (b, BlockItem.ScanGround, at), (b, BlockItem.ScanRoads, at) }));
        state.SetStageDone(StageKeys.GameFiles, StageKeys.World, at.AddSeconds(1));
        return projectPath;
    }

    static readonly UTF8Encoding Utf8 = new(false);
    static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary><c>paths.json</c> in the game files step's shape (every connection once from each end).</summary>
    static void WritePaths(string path, IslandData data)
    {
        var degree = new Dictionary<string, int>();
        foreach (var l in data.Links)
        {
            degree[l.From] = degree.GetValueOrDefault(l.From) + 1;
            degree[l.To] = degree.GetValueOrDefault(l.To) + 1;
        }
        var byKey = data.Nodes.ToDictionary(n => n.Key);
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, Compact);
        w.WriteStartObject();
        w.WriteStartArray("areas");
        foreach (var a in data.Nodes.Select(n => int.Parse(n.Key[..n.Key.IndexOf(':')], CultureInfo.InvariantCulture)).Distinct().Order()) w.WriteNumberValue(a);
        w.WriteEndArray();
        w.WriteStartObject("nodes");
        foreach (var n in data.Nodes)
        {
            w.WriteStartObject(n.Key);
            w.WriteNumber("x", Math.Round(n.X, 3));
            w.WriteNumber("y", Math.Round(n.Y, 3));
            w.WriteNumber("z", Math.Round(n.Z, 3));
            w.WriteNumber("street", n.Street);
            w.WriteBoolean("junction", n.Junction);
            w.WriteBoolean("highway", n.Highway);
            w.WriteBoolean("tunnel", n.Tunnel);
            w.WriteBoolean("unpaved", n.Unpaved);
            w.WriteBoolean("switchedOff", n.SwitchedOff);
            w.WriteBoolean("noBigVehicles", false);
            w.WriteBoolean("cannotGoLeft", false);
            w.WriteBoolean("slipLane", false);
            w.WriteBoolean("indicateKeepLeft", false);
            w.WriteBoolean("indicateKeepRight", false);
            w.WriteBoolean("noGps", false);
            w.WriteString("special", "none");
            w.WriteNumber("trafficDensity", n.SwitchedOff ? 0 : 4);
            w.WriteString("speed", n.Highway ? "fast" : "normal");
            w.WriteNumber("deadEnd", degree.GetValueOrDefault(n.Key) <= 1 ? 1 : 0);
            w.WriteNumber("heuristic", 0);
            if (n.JunctionArea is { } ja)
            {
                w.WriteStartArray("junctionArea");
                foreach (var v in ja) w.WriteNumberValue(Math.Round(v, 3));
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteStartArray("links");
        foreach (var l in data.Links)
        {
            var (a, b) = (byKey[l.From], byKey[l.To]);
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
            foreach (var (from, to, fwd, back) in new[] { (a.Key, b.Key, l.LanesForward, l.LanesBack), (b.Key, a.Key, l.LanesBack, l.LanesForward) })
            {
                w.WriteStartObject();
                w.WriteString("from", from);
                w.WriteString("to", to);
                w.WriteNumber("lanesForward", fwd);
                w.WriteNumber("lanesBack", back);
                w.WriteBoolean("narrow", l.Narrow);
                w.WriteBoolean("dontUseForNavigation", false);
                w.WriteBoolean("shortcut", false);
                w.WriteNumber("laneOffset", 0);
                w.WriteBoolean("gpsBothWays", false);
                w.WriteNumber("length", Math.Min(255, (int)Math.Round(len)));
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary><c>names.json</c>: the made-up street and zone names in English and Japanese.</summary>
    static void WriteNames(string path, IslandData data)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, Indented);
        w.WriteStartObject();
        w.WriteStartObject("streets");
        foreach (var (h, (en, ja)) in data.Streets)
        {
            w.WriteStartObject(h.ToString(CultureInfo.InvariantCulture));
            w.WriteString("en", en);
            w.WriteString("ja", ja);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteStartObject("zones");
        foreach (var (code, (en, ja)) in data.Zones)
        {
            w.WriteStartObject(code);
            w.WriteString("en", en);
            w.WriteString("ja", ja);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    static string PostalsJson(IslandData data) =>
        "[\n" + string.Join(",\n", data.Postals.Select(p => $"  {{ \"code\": \"{p.Code}\", \"x\": {F(p.X)}, \"y\": {F(p.Y)} }}")) + "\n]\n";

    /// <summary>A group of points: its name in English and Japanese (as the bundled groups'), its points with their English label (none when empty).</summary>
    static string PointsJson(string en, string ja, int order, string style, IEnumerable<(string Id, string Label, double X, double Y, string? Color)> points)
    {
        var pts = string.Join(",\n", points.Select(p =>
            $"    {{ \"id\": \"{p.Id}\", {(p.Label.Length == 0 ? "" : $"\"label\": {{ \"en\": \"{p.Label}\" }}, ")}\"x\": {F(p.X)}, \"y\": {F(p.Y)}{(p.Color is null ? "" : $", \"color\": \"{p.Color}\"")} }}"));
        return $"{{\n  \"format\": 1,\n  \"name\": {{ \"en\": \"{en}\", \"ja\": \"{ja}\" }},\n  \"order\": {order},\n  \"style\": \"{style}\",\n  \"points\": [\n{pts}\n  ]\n}}\n";
    }

    /// <summary>One block's scan from the surface: the ground pass (materials, hit heights, water surface, water-collision probe) and the road pass (street, zone, on-road).</summary>
    sealed class ScanWriter(IslandData data, Materials materials)
    {
        readonly IslandSurface _s = data.Surface;
        readonly uint[] _hash = HashesOf(data.Surface.MaterialNames, materials);

        static uint[] HashesOf(IReadOnlyList<string> names, Materials materials)
        {
            var byName = materials.Names.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
            return names.Select(n => n.Length == 0 ? 0u : byName.TryGetValue(n, out var h) ? h : throw new InvalidOperationException("no material " + n)).ToArray();
        }

        /// <summary>The nearest node's index, or -1 outside the grid.</summary>
        int Nearest(double x, double y)
        {
            int c = (int)Math.Round((x - _s.X0) / _s.Step), r = (int)Math.Round((_s.Y0 - y) / _s.Step);
            return (uint)c < (uint)_s.Width && (uint)r < (uint)_s.Height ? r * _s.Width + c : -1;
        }

        /// <summary>A height between the four nodes around a point; NaN when one of them has none.</summary>
        float Bilinear(float[] a, double x, double y)
        {
            double fc = (x - _s.X0) / _s.Step, fr = (_s.Y0 - y) / _s.Step;
            int c = Math.Clamp((int)Math.Floor(fc), 0, _s.Width - 2), r = Math.Clamp((int)Math.Floor(fr), 0, _s.Height - 2);
            double tx = Math.Clamp(fc - c, 0, 1), ty = Math.Clamp(fr - r, 0, 1);
            int i = r * _s.Width + c;
            float a00 = a[i], a01 = a[i + 1], a10 = a[i + _s.Width], a11 = a[i + _s.Width + 1];
            return (float)((a00 * (1 - tx) + a01 * tx) * (1 - ty) + (a10 * (1 - tx) + a11 * tx) * ty);
        }

        /// <summary>A height at a point: between the nodes, or the nearest node's where the neighbours differ in kind (an edge of water, a roof, a deck).</summary>
        float Height(float[] a, double x, double y, int i)
        {
            float v = Bilinear(a, x, y);
            return float.IsNaN(v) || Math.Abs(v - a[i]) > 1.0f ? a[i] : v;
        }

        static string H(float v) => float.IsNaN(v) ? "x" : v.ToString("0.00", CultureInfo.InvariantCulture);

        public string Text(BlockId b)
        {
            var (x0, y0, _, _) = b.Rect;
            const int n = 282, rn = 71;
            var sb = new StringBuilder(1 << 20);
            void Line(string s) => sb.Append("MSCAN ").Append(s).Append('\n');
            string head = $"z=8 tx={b.Tx} ty={b.Ty} x0={x0.ToString("0.0000", CultureInfo.InvariantCulture)} y0={y0.ToString("0.0000", CultureInfo.InvariantCulture)} size={WorldGrid.BlockSize.ToString("0.0000", CultureInfo.InvariantCulture)}";
            Line($"BEGIN v=1 kind=mat seq=1 block={b.Name} {head} step=1.000 n={n} flags=1 fol=1 chunks=1 pflags=128");
            var top = _s.Top ?? _s.Ground;
            var idx = new int[n * n];
            var matIndex = new Dictionary<byte, int>();
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    int i = idx[r * n + c] = Nearest(x0 + c, y0 - r);
                    if (i >= 0 && !matIndex.ContainsKey(_s.Material[i])) matIndex[_s.Material[i]] = matIndex.Count + 1;
                }
            foreach (var (m, k) in matIndex) Line($"dict mat {k} {_hash[m]}");
            void Rows(string kind, int size, Func<int, int, string> value)
            {
                for (int r = 0; r < size; r++)
                {
                    sb.Append("MSCAN ").Append(kind).Append(" j=").Append(r).Append(" k=0");
                    string? last = null;
                    int count = 0;
                    for (int c = 0; c <= size; c++)
                    {
                        string? s = c < size ? value(r, c) : null;
                        if (s == last) { count++; continue; }
                        if (last is not null) sb.Append(' ').Append(last).Append(count > 1 ? "*" + count : "");
                        last = s;
                        count = 1;
                    }
                    sb.Append('\n');
                }
            }
            Rows("mat", n, (r, c) => idx[r * n + c] is var i and >= 0 ? matIndex[_s.Material[i]].ToString(CultureInfo.InvariantCulture) : "0");
            Rows("hz", n, (r, c) => idx[r * n + c] is var i and >= 0 ? H(Height(top, x0 + c, y0 - r, i)) : "x");
            Rows("water", n, (r, c) => idx[r * n + c] is var i and >= 0 ? H(float.IsNaN(_s.Water[i]) ? float.NaN : Height(_s.Water, x0 + c, y0 - r, i)) : "x");
            Rows("fol", n, (r, c) => idx[r * n + c] is var i and >= 0 && _s.Probe is { } p ? H(p[i]) : "x");
            Line($"END kind=mat n={n} cells={n * n} mats={matIndex.Count}");
            Line($"DONE kind=ground block={b.Name}");

            Line($"BEGIN v=1 kind=road seq=2 block={b.Name} {head} step=4.000 n={rn} pstep=1.000 pn={n}");
            var streetIndex = new Dictionary<uint, int>();
            var zoneIndex = new Dictionary<byte, int>();
            var roadRows = new StringBuilder();
            var keep = sb;
            sb = roadRows;
            Rows("street", rn, (r, c) =>
            {
                if (_s.Street is null || Nearest(x0 + 4 * c + 2, y0 - 4 * r - 2) is not (var i and >= 0) || _s.Street[i] == 0) return "0";
                var h = _s.Street[i];
                if (!streetIndex.TryGetValue(h, out var k)) streetIndex[h] = k = streetIndex.Count + 1;
                return k.ToString(CultureInfo.InvariantCulture);
            });
            Rows("zone", rn, (r, c) =>
            {
                if (Nearest(x0 + 4 * c + 2, y0 - 4 * r - 2) is not (var i and >= 0) || _s.Zone[i] == 0) return "0";
                var z = _s.Zone[i];
                if (!zoneIndex.TryGetValue(z, out var k)) zoneIndex[z] = k = zoneIndex.Count + 1;
                return k.ToString(CultureInfo.InvariantCulture);
            });
            Rows("onroad", n, (r, c) => _s.OnRoad is { } on && idx[r * n + c] is var i and >= 0 && on[i] ? "1" : "0");
            sb = keep;
            foreach (var (h, k) in streetIndex) Line($"dict street {k} {h} {data.Streets[h].En}");
            foreach (var (z, k) in zoneIndex)
            {
                var code = _s.ZoneCodes[z];
                Line($"dict zone {k} {code} {(data.Zones.TryGetValue(code, out var nm) ? nm.En : code)}");
            }
            sb.Append(roadRows);
            Line($"END kind=road n={rn} streets={streetIndex.Count} zones={zoneIndex.Count} pn={n}");
            Line($"DONE kind=roads block={b.Name}");
            return sb.ToString();
        }
    }
}
