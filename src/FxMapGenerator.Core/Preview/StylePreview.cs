using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Regions;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.RoadEdits;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Preview;

/// <summary>
/// A window of a project drawn with a style for the style editor, the way the cells are drawn (<see cref="CellPainter"/>,
/// the z8 scale) and without writing files: the map's z8 tiles over a square of one of <see cref="Sizes"/> around a
/// point (those with a block of the range), drawn a block at a time on the workers and kept as PNG for the browser,
/// which makes them smaller when it shows the window from further out. The window's data is made from the blocks around
/// it (a margin for the blurs) as the cells' data is, and kept: the grids and heights of the window (whatever the style),
/// and per set of the style's values its ground picture, its shading, its layers and the region colours around it; the
/// labels of the whole project per placement key and language (a project's labels file when there is one). So a change
/// of a colour or a width draws again, a change of the ground's values makes the window's ground picture again, and so
/// on (<see cref="Parts"/> says what the last drawing made and how long each took). <see cref="Pick"/> tells where the
/// colour of a point comes from.
/// </summary>
public sealed class StylePreview
{
    /// <summary>The sides of the windows the style editor offers (m).</summary>
    public static IReadOnlyList<double> Sizes { get; } = [500, 1000, 2000, 4000];
    /// <summary>The blocks read around a window reach this far beyond it (m): the blurs of the ground and the shading.</summary>
    const double Margin = 160;
    /// <summary>The region colours are computed this many of their blurs around a window.</summary>
    const double RegionReach = 3;
    /// <summary>Windows, and made values per kind, kept.</summary>
    const int Keep = 4;
    /// <summary>
    /// The windows' data kept holds at most this many blocks in all (the newest window always stays): a 4 km window of a
    /// town takes some 250 blocks and 5 to 7 GB.
    /// </summary>
    const int KeepBlocks = 300;

    /// <summary>
    /// The places the style editor recommends on a project's own data (the game's names): a town, a desert, hills, a
    /// mountain, a beach and a forest.
    /// </summary>
    public static IReadOnlyList<(string Name, double X, double Y)> Recommended { get; } =
    [
        ("Legion Square", 195, -934),
        ("Sandy Shores", 1850, 3700),
        ("Vinewood Hills", -992, 928),
        ("Mount Chiliad", 501, 5604),
        ("Vespucci Beach", -1310, -1500),
        ("Paleto Forest", -552, 5684),
    ];

    /// <summary>
    /// What a project still lacks for the preview (null: nothing): the road shapes and the zones of the whole range, and
    /// a block with its data (its scans and landcover; only such blocks are drawn).
    /// </summary>
    public static string? Waiting(Project project)
    {
        var folder = new WorkFolder(project.WorkFolderPath);
        if (!File.Exists(Path.Combine(folder.Data, RoadShapesFile.FileName))) return "the road shapes";
        if (!File.Exists(Path.Combine(folder.Data, ZoneGrid.FileName))) return "the zones";
        return project.Range.Keys.Any(b => HasData(folder, b)) ? null : "the landcover";
    }

    static bool HasData(WorkFolder folder, BlockId b) => File.Exists(LandcoverFile.PathOf(folder.Data, b)) && File.Exists(folder.ScanFile(b));

    HashSet<BlockId>? _ready;

    /// <summary>The blocks of the range the preview can draw: those with their data (taken when first asked).</summary>
    public IReadOnlySet<BlockId> ReadyBlocks
    {
        get
        {
            lock (_sync) return _ready ??= _project.Range.Keys.Where(b => HasData(_folder, b)).ToHashSet();
        }
    }
    /// <summary>Painters kept (the last drawings, so a point of them is told at once).</summary>
    const int KeepPainters = 2;
    /// <summary>The tiles' PNG level: quick (they are made again at every change and never stored).</summary>
    const int TileQuality = 10;

    readonly Project _project;
    readonly WorkFolder _folder;
    readonly object _sync = new();
    readonly List<(string Key, PlaceData Data)> _places = new();
    readonly Dictionary<string, List<(string Key, object Value)>> _made = new();
    readonly List<(string Key, CellPainter Painter)> _painters = new();
    RoadShapesFile.Contents? _roads;
    List<ResolvedPoi>? _pois;
    ZoneGrid? _zones;
    GameNames? _names;
    /// <summary>The workers of the call under way (set per call, under the lock).</summary>
    IParallelRunner? _parallel;

    public StylePreview(Project project)
    {
        _project = project;
        _folder = new WorkFolder(project.WorkFolderPath);
    }

    /// <summary>What the last drawing made (the window's data, ground, shading, layers, region colours, labels, painter, tiles) and the seconds each took.</summary>
    public IReadOnlyList<(string Part, double Seconds)> Parts { get; private set; } = [];

    /// <summary>A window: the map's z8 tiles (<paramref name="Tx0"/>, <paramref name="Ty0"/>) to (<paramref name="Tx1"/>, <paramref name="Ty1"/>), both ends in.</summary>
    public readonly record struct Window(int Tx0, int Ty0, int Tx1, int Ty1)
    {
        /// <summary>The window's west, north, east and south edges (m).</summary>
        public double X0 => WorldGrid.Left + Tx0 * WorldGrid.TileSize;
        public double Y0 => WorldGrid.Top - Ty0 * WorldGrid.TileSize;
        public double X1 => WorldGrid.Left + (Tx1 + 1) * WorldGrid.TileSize;
        public double Y1 => WorldGrid.Top - (Ty1 + 1) * WorldGrid.TileSize;

        /// <summary>The z8 tiles a square of <paramref name="size"/> m around (<paramref name="cx"/>, <paramref name="cy"/>) reaches into.</summary>
        public static Window Around(double cx, double cy, double size)
        {
            double h = size / 2, t = WorldGrid.TileSize;
            return new((int)Math.Floor((cx - h - WorldGrid.Left) / t), (int)Math.Floor((WorldGrid.Top - (cy + h)) / t),
                (int)Math.Ceiling((cx + h - WorldGrid.Left) / t) - 1, (int)Math.Ceiling((WorldGrid.Top - (cy - h)) / t) - 1);
        }

        public override string ToString() => $"{Tx0},{Ty0},{Tx1},{Ty1}";
    }

    /// <summary>A window drawn: its z8 tiles as PNG by (x, y) (the tiles without a block of the range are left out).</summary>
    public sealed record Drawing(Window Window, IReadOnlyDictionary<(int X, int Y), byte[]> Tiles);

    /// <summary>The window drawn with a style (labels in a language) on the workers of <paramref name="parallel"/>.</summary>
    public Drawing Draw(MapStyle style, string language, Window window, IParallelRunner? parallel = null, CancellationToken token = default)
    {
        lock (_sync)
        {
            _parallel = parallel;
            try
            {
                var sw = Stopwatch.StartNew();
                var parts = new List<(string, double)>();
                var p = Prepare(style, language, window, parts, token);
                var painter = Painter(p, style, language, parts);
                token.ThrowIfCancellationRequested();
                var area = p.Place.Area;
                // a piece a block of the window's data: its tiles inside the window
                var pieces = new List<Window>();
                foreach (var b in area.Blocks)
                {
                    var q = new Window(Math.Max(b.Tx, window.Tx0), Math.Max(b.Ty, window.Ty0), Math.Min(b.Tx + 3, window.Tx1), Math.Min(b.Ty + 3, window.Ty1));
                    if (q.Tx0 <= q.Tx1 && q.Ty0 <= q.Ty1) pieces.Add(q);
                }
                var tiles = new ConcurrentDictionary<(int X, int Y), byte[]>();
                const int T = TileStore.TileSize;
                void Piece(Window q)
                {
                    token.ThrowIfCancellationRequested();
                    int w = (q.Tx1 - q.Tx0 + 1) * T, h = (q.Ty1 - q.Ty0 + 1) * T;
                    var rgba = painter.DrawArea((q.Tx0 - area.Tx0) * T, (q.Ty0 - area.Ty0) * T, w, h);
                    for (int ty = q.Ty0; ty <= q.Ty1; ty++)
                        for (int tx = q.Tx0; tx <= q.Tx1; tx++)
                            tiles[(tx, ty)] = Images.EncodePng(TileStore.Crop(rgba, w, (tx - q.Tx0) * T, (ty - q.Ty0) * T, T, T), T, T, TileQuality);
                }
                var t = Stopwatch.StartNew();
                if (_parallel is not null) _parallel.ForEach(pieces, Piece);
                else foreach (var q in pieces) Piece(q);
                parts.Add(("tiles", Math.Round(t.Elapsed.TotalSeconds, 3)));
                parts.Add(("all", Math.Round(sw.Elapsed.TotalSeconds, 3)));
                Parts = parts;
                return new Drawing(window, tiles);
            }
            finally { _parallel = null; }
        }
    }

    /// <summary>
    /// Where the colour of the point (<paramref name="x"/>, <paramref name="y"/>) of the window drawn with a style comes
    /// from (<see cref="CellPainter.Pick"/>): the steps that paint its pixel from the top down to the first one that
    /// covers it whole (the shading does not hide what is under it), the building colour's rule, what the ground
    /// picture is made of there (<see cref="GroundRaster.Explain"/>), and the point's zone and ground.
    /// </summary>
    public PreviewPick Pick(MapStyle style, string language, Window window, double x, double y, IParallelRunner? parallel = null, CancellationToken token = default)
    {
        lock (_sync)
        {
            _parallel = parallel;
            try
            {
                var p = Prepare(style, language, window, new List<(string, double)>(), token);
                var painter = Painter(p, style, language, null);
                var area = p.Place.Area;
                var g = p.Place.Grids;
                int px = (int)Math.Floor((x - area.X0) * CellPainter.Ppm), py = (int)Math.Floor((area.Y0 - y) * CellPainter.Ppm);
                int col = (int)Math.Round(x - area.Gx0, MidpointRounding.ToEven), row = (int)Math.Round(area.Gy0 - y, MidpointRounding.ToEven);
                bool inside = col >= 0 && col < area.W && row >= 0 && row < area.H && g.Ok[row, col];
                if (!inside) throw new PreviewException("no block of the range at the point");
                var (colour, all) = painter.Pick(px, py);
                string zone = g.ZoneCodes[g.Zone[row, col]];
                var steps = new List<PreviewPickStep>();
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    var (tag, cover, factor) = all[i];
                    string? rule = null, region = null;
                    if (tag.Kind == "building")
                    {
                        if (style.Buildings.ByZone.TryGetValue(zone, out var k) && k.Length > 0) rule = "zone";
                        else if (style.Regions.Zones.TryGetValue(zone, out var reg) && style.Buildings.ByRegion.ContainsKey(reg)) (rule, region) = ("region", reg);
                        else rule = "default";
                    }
                    steps.Add(new PreviewPickStep(tag, Math.Round(cover, 3), Math.Round(factor, 3), rule, region));
                    if (tag.Kind != "shade" && cover >= 0.999) break;
                }
                IReadOnlyList<GroundPart> ground = steps.Any(s => s.Tag.Kind == "ground") && style.GroundRaster is not null
                    ? GroundRaster.Explain(g, style, p.Regions?.Field, p.Regions?.Land, col, row).Select(q => q with { Share = Math.Round(q.Share, 3) }).ToList()
                    : [];
                _names ??= File.Exists(Path.Combine(_folder.Game, GameFilesOutput.Names)) ? GameNames.Read(Path.Combine(_folder.Game, GameFilesOutput.Names)) : null;
                var lc = g.Landcover[row, col];
                var point = new PreviewPoint(zone, _names?.Zone(zone, "en"), _names?.Zone(zone, "ja"),
                    lc < GroundClasses.Names.Count ? GroundClasses.Names[lc] : GroundClasses.Names[0], g.Water[row, col], g.Buildings[row, col]);
                return new PreviewPick(colour, steps, point, ground);
            }
            finally { _parallel = null; }
        }
    }

    // ---------------------------------------------------------------- the parts

    /// <summary>The grids and heights of the blocks around a window (the range's blocks within its margin).</summary>
    sealed class PlaceData
    {
        public required string Key { get; init; }
        public required CellArea Area { get; init; }
        public required CellGrids Grids { get; init; }
        public required Grid<double>? ShadeZ { get; init; }
    }

    /// <summary>The region colours around a window and the frame's 4 m land they were computed over.</summary>
    sealed record RegionsPart(RegionField Field, Grid<bool> Land);

    /// <summary>What a drawing of a window with a style reads.</summary>
    sealed record Prepared(PlaceData Place, byte[]? Ground, (Grid<byte> Light, double Flat)? Shade, IReadOnlyList<CellLayer> Layers,
        IReadOnlyList<PlacedLabel> Labels, RegionsPart? Regions);

    static T Timed<T>(List<(string, double)>? parts, string part, Func<T> make)
    {
        var t = Stopwatch.StartNew();
        var v = make();
        parts?.Add((part, Math.Round(t.Elapsed.TotalSeconds, 3)));
        return v;
    }

    Prepared Prepare(MapStyle style, string language, Window window, List<(string, double)> parts, CancellationToken token)
    {
        // the window's data: per set of blocks (a larger window over the same blocks reads the same)
        var near = NearBlocks(window);
        var key = string.Join(" ", near);
        if (_places.All(e => e.Key != key)) Free(near.Count);
        var place = Cached(_places, key, () => Timed(parts, "place", () => MakePlace(near, token)));
        token.ThrowIfCancellationRequested();
        _roads ??= Timed(parts, "roads", () => RoadShapesFile.Read(Path.Combine(_folder.Data, RoadShapesFile.FileName)));
        _pois ??= PoiData.Of(_project).Resolve().Where(p => p.Visible && p.Show.Atlas).ToList();

        // the ground picture: per ground values (and region colours, for a ground made of them)
        byte[]? ground = null;
        RegionsPart? regions = null;
        if (style.GroundRaster is not null)
        {
            var values = CellPrepStage.GroundValues(style);
            if (style.GroundRaster.Mode == "regions")
                regions = Made("regions", place.Key + style.Source["regions"]!.ToJsonString(), () => Timed(parts, "regions", () => Regions(place, style.Regions)));
            var field = regions?.Field;
            ground = Made("ground", place.Key + values.ToJsonString(), () => Timed(parts, "ground", () => GroundRaster.Make(place.Grids, style, field, _parallel)));
        }
        token.ThrowIfCancellationRequested();
        // the shading: per light values (the strength is the drawing's)
        (Grid<byte> Light, double Flat)? shade = null;
        if (style.Shade is { } ss && place.ShadeZ is { } zs)
            shade = Made("shade", place.Key + CellPrepStage.ShadeValues(ss).ToJsonString(), () => Timed(parts, "shade", () =>
            {
                var (light, flat) = CellHeights.Light(zs, ss, _parallel);
                return (CellHeights.Quantize(light), flat);
            }));
        token.ThrowIfCancellationRequested();
        // the layers: per layer sets
        var sets = string.Join("|", CellLayers.Sets.Ground(style), CellLayers.Sets.Canopy(style), CellLayers.Sets.Sea(style), CellLayers.Sets.Buildings(style));
        var layers = Made("layers", place.Key + sets, () => Timed(parts, "layers", () =>
            CellLayers.Trace(CellLayers.Masks(place.Grids, [style], null, Materials.Default, _parallel), place.Area, _parallel)));
        token.ThrowIfCancellationRequested();
        // the labels of the whole project: per placement key and language
        var labels = style.Labels is { } ls
            ? Made("labels", ls.PlacementKey + "/" + language, () => Timed(parts, "labels", () => Labels(ls, language)))
            : [];
        token.ThrowIfCancellationRequested();
        return new Prepared(place, ground, shade, layers, labels, regions);
    }

    /// <summary>The painter of a window with a style's values (kept: the same values draw and pick with the same one).</summary>
    CellPainter Painter(Prepared p, MapStyle style, string language, List<(string, double)>? parts)
    {
        var key = $"{p.Place.Key}|{style.Digest}|{language}";
        int i = _painters.FindIndex(e => e.Key == key);
        if (i >= 0)
        {
            var hit = _painters[i];
            _painters.RemoveAt(i);
            _painters.Add(hit);
            return hit.Painter;
        }
        var area = p.Place.Area;
        var info = new CellLayerFileInfo(area.Id.Name, area.Rect, area.Gx0, area.Gy0, area.W, area.H, area.Blocks);
        var painter = Timed(parts, "painter", () =>
        {
            ShadeLayer? shading = style.Shade is { } s2 && p.Shade is { } sh
                ? new ShadeLayer(sh.Light, sh.Flat, s2.Strength, CellPainter.Ppm, (info.GridX0 - info.Rect.X0) * CellPainter.Ppm, (info.Rect.Y0 - info.GridY0) * CellPainter.Ppm)
                : null;
            return new CellPainter(new CellDrawInput
            {
                Info = info, Layers = p.Layers, Style = style, Shade = shading, Ground = p.Ground is null ? null : (p.Ground, area.W, area.H), Roads = _roads,
                Labels = CellInputs.LabelsOf(p.Labels, area.Rect), Pois = CellInputs.PoisOf(_pois!, area.Rect), Language = language,
            });
        });
        _painters.Add((key, painter));
        if (_painters.Count > KeepPainters)
        {
            _painters[0].Painter.Dispose();
            _painters.RemoveAt(0);
        }
        return painter;
    }

    /// <summary>
    /// Lets the oldest windows' data go (with the values and painters made for them) until a window of
    /// <paramref name="blocks"/> more blocks keeps the data within <see cref="KeepBlocks"/>.
    /// </summary>
    void Free(int blocks)
    {
        while (_places.Count > 0 && _places.Sum(e => e.Data.Area.Blocks.Count) + blocks > KeepBlocks)
        {
            var gone = _places[0].Data.Key;
            _places.RemoveAt(0);
            foreach (var list in _made.Values) list.RemoveAll(e => e.Key.StartsWith(gone, StringComparison.Ordinal));
            foreach (var (_, painter) in _painters.Where(e => e.Key.StartsWith(gone, StringComparison.Ordinal))) painter.Dispose();
            _painters.RemoveAll(e => e.Key.StartsWith(gone, StringComparison.Ordinal));
        }
    }

    /// <summary>A kept value of a kind, else made (and kept; the oldest of the kind goes).</summary>
    T Made<T>(string kind, string key, Func<T> make) where T : notnull
    {
        if (!_made.TryGetValue(kind, out var list)) _made[kind] = list = new();
        return (T)Cached(list, key, () => (object)make());
    }

    static T Cached<T>(List<(string Key, T Value)> list, string key, Func<T> make)
    {
        int i = list.FindIndex(e => e.Key == key);
        if (i >= 0)
        {
            var hit = list[i];
            list.RemoveAt(i);
            list.Add(hit);
            return hit.Value;
        }
        var v = make();
        list.Add((key, v));
        if (list.Count > Keep) list.RemoveAt(0);
        return v;
    }

    /// <summary>The range's blocks with their data within the margin of a window.</summary>
    List<BlockId> NearBlocks(Window window)
    {
        var ready = ReadyBlocks;
        var near = new List<BlockId>();
        var a = BlockId.At(window.X0 - Margin, window.Y0 + Margin);
        var b = BlockId.At(window.X1 + Margin, window.Y1 - Margin);
        for (int ty = a.Ty; ty <= b.Ty; ty += 4)
            for (int tx = a.Tx; tx <= b.Tx; tx += 4)
            {
                var id = new BlockId(tx / 4, ty / 4);
                if (ready.Contains(id)) near.Add(id);
            }
        if (near.Count == 0) throw new PreviewException("no block of the range with its data is near the place");
        return near;
    }

    PlaceData MakePlace(List<BlockId> near, CancellationToken token)
    {
        var area = new CellArea(new CellId(0, 0), near);
        var items = SurfaceHeights.LandcoverItems(_project);
        var loaded = new ConcurrentDictionary<BlockId, (CellGrids.BlockInput Input, float[] Heights)>();
        void Load(BlockId id)
        {
            token.ThrowIfCancellationRequested();
            var scan = ScanFile.Read(_folder.ScanFile(id));
            var hg = SurfaceHeights.Of(_folder, id, items, scan);
            loaded[id] = (new CellGrids.BlockInput(GridFile.Load(LandcoverFile.PathOf(_folder.Data, id)), scan), CellPrepStage.Cut(hg.Values.Select(v => (float)v).ToArray(), hg.N, scan.N));
        }
        if (_parallel is not null) _parallel.ForEach(area.Blocks, Load);
        else foreach (var id in area.Blocks) Load(id);
        // in the blocks' order, as a cell reads them
        var inputs = area.Blocks.ToDictionary(id => id, id => loaded[id].Input);
        var heights = area.Blocks.ToDictionary(id => id, id => loaded[id].Heights);
        int n = inputs[area.Blocks[0]].Scan.N;
        var g = CellGrids.Build(area, inputs, Materials.Default);
        Grid<double>? zs = null;
        if (CellHeights.Surface(area, heights, n) is { } dsm)
            zs = CellHeights.ShadeHeights(dsm, CellHeights.Wide(dsm), g.Buildings, g.Landcover, _parallel);
        return new PlaceData { Key = $"{area.X0:R},{area.Y0:R},{area.Blocks.Count}|", Area = area, Grids = g, ShadeZ = zs };
    }

    /// <summary>The region colours around a window: a window of the frame (the window's blocks and its blurs' reach) computed as the regions step does.</summary>
    RegionsPart Regions(PlaceData place, RegionsStyle style)
    {
        _zones ??= ZoneGrid.Load(Path.Combine(_folder.Data, ZoneGrid.FileName));
        var r = place.Area.Rect;
        double reach = RegionReach * style.Blur + 2 * RegionField.Step;
        var (fc0, fr0, fw, fh) = RegionField.Extent(_project.Frame);
        int c0 = Math.Max(fc0, (int)Math.Floor((r.X0 - reach - WorldGrid.Left) / RegionField.Step));
        int r0 = Math.Max(fr0, (int)Math.Floor((WorldGrid.Top - (r.Y0 + reach)) / RegionField.Step));
        int c1 = Math.Min(fc0 + fw, (int)Math.Ceiling((r.X1 + reach - WorldGrid.Left) / RegionField.Step));
        int r1 = Math.Min(fr0 + fh, (int)Math.Ceiling((WorldGrid.Top - (r.Y1 - reach)) / RegionField.Step));
        var land = RegionField.Land4(_folder.Data, ReadyBlocks, c0, r0, c1 - c0, r1 - r0, _parallel);
        var zones = RegionField.ZonesOnFrame(_zones, c0, r0, c1 - c0, r1 - r0);
        var f = RegionField.Compute(zones, _zones.Codes, land, style, _parallel);
        var field = new RegionField
        {
            Red = f.Red, Green = f.Green, Blue = f.Blue, Region = f.Region, Zone = f.Zone, Codes = f.Codes,
            OriginX = WorldGrid.Left + c0 * RegionField.Step, OriginY = WorldGrid.Top - r0 * RegionField.Step,
        };
        return new RegionsPart(field, land);
    }

    /// <summary>The labels of the whole project for a labels section and language: the project's labels file for its key, else placed as the labels step places them.</summary>
    List<PlacedLabel> Labels(LabelsStyle style, string language)
    {
        var file = Path.Combine(_folder.Data, LabelsFile.FileName(language, style.PlacementKey));
        if (File.Exists(file)) return LabelsFile.Read(file).Labels;
        var postals = PostalCodes.Parse(File.ReadAllText(Path.Combine(_folder.Data, PostalCodes.FileName)), "data/" + PostalCodes.FileName);
        _zones ??= ZoneGrid.Load(Path.Combine(_folder.Data, ZoneGrid.FileName));
        var added = RoadEditsFile.StreetsOf(_project);
        var names = GameNames.Read(Path.Combine(_folder.Game, GameFilesOutput.Names)).WithStreets(added.Select(s => (s.Hash, s.En, s.Ja)));
        var roads = RoadGraphFile.Read(Path.Combine(_folder.Data, RoadGraphFile.Roads));
        using var metrics = new SkiaTextMetrics();
        var input = new LabelInput
        {
            Frame = (_zones.X0, _zones.Y0, _zones.X0 + _zones.FrameWidth, _zones.Y0 - _zones.FrameHeight),
            Postals = postals, Pois = _pois ?? [], Zones = _zones, Names = names, Roads = roads, Routes = Routes.Bundled,
            Style = style, Language = language, Metrics = metrics, UnnamedZones = LabelsStage.UnnamedZones(_project),
        };
        return LabelPlacer.Place(input, _ => { });
    }
}

/// <summary>
/// Where the colour of a point of a preview comes from (<see cref="StylePreview.Pick"/>): the pixel's colour, the steps
/// that paint it from the top (with the part of the pixel each covers, the shading's factor, and for a building the rule
/// its colour came from: <c>zone</c>, <c>region</c> with the region, or <c>default</c>), what the ground picture is made
/// of there, and the point itself.
/// </summary>
public sealed record PreviewPick(Rgb Color, IReadOnlyList<PreviewPickStep> Steps, PreviewPoint Point, IReadOnlyList<GroundPart> Ground);

public sealed record PreviewPickStep(StepTag Tag, double Cover, double Factor, string? Rule = null, string? Region = null);

/// <summary>A point of a preview: its zone (the code and the game's names), its kind of ground, and whether it is water or a building.</summary>
public sealed record PreviewPoint(string Zone, string? ZoneEn, string? ZoneJa, string Ground, bool Water, bool Building);

public sealed class PreviewException(string message) : Exception(message);
