using System.Globalization;
using System.Text;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Export;

/// <summary>The steps of an export, in order.</summary>
public static class ExportStages
{
    /// <summary>
    /// <see cref="ExportStage"/> (the web tiles, the minimap resource, the folder's record), then, when the export
    /// writes layered files, <see cref="EditableLayersStage"/> (when an atlas or road map is among them) and
    /// <see cref="EditableFilesStage"/>.
    /// </summary>
    /// <param name="game">Where the minimap resource's parts from the game's files come from (null: this PC's game).</param>
    public static IReadOnlyList<Stage> For(ExportOptions options, string version, MinimapGameFiles? game = null)
    {
        var stages = new List<Stage> { new ExportStage(options, version, game) };
        if (options.Editable is not { Maps.Count: > 0 } editable) return stages;
        if (editable.Maps.Any(m => MapSet.TryParse(m, out var map) && map.IsCellMap)) stages.Add(new EditableLayersStage(options));
        stages.Add(new EditableFilesStage(options));
        return stages;
    }
}

/// <summary>Where the layered files of an export are made: <c>editable/.work/</c> in the export folder, gone when they are written.</summary>
static class EditableWork
{
    /// <summary>A file in a map's folder there that says every cell of the map is drawn.</summary>
    public const string Drawn = "drawn";

    public static string Root(string exportFolder) => Path.Combine(exportFolder, EditableChoice.Folder, ".work");

    /// <summary>The folder of a map's layers as packed blocks: <c>&lt;file name&gt;/</c>.</summary>
    public static string Of(string exportFolder, MapSet map, int zoom) => Path.Combine(Root(exportFolder), EditableChoice.FileName(map, zoom));

    public static void Remove(string exportFolder)
    {
        var root = Root(exportFolder);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

/// <summary>
/// The first step of an export's layered files (<see cref="EditableChoice"/>), one unit per cell of each atlas or road
/// map chosen: the cell's blocks are drawn layer by layer (<see cref="CellPainter.DrawLayers(int, int, int, int)"/>),
/// a block's layers are made as small as the block is at the files' zoom level so that put together they are the
/// block's tile of that zoom (<see cref="LayeredBlock"/>, which shrinks the way the tiles of the lower zooms are made,
/// <see cref="Shrink"/>), and kept as packed rows (<see cref="PackedBlocks"/>) in the export folder's
/// <c>editable/.work/</c>. <see cref="EditableFilesStage"/> then joins them into each map's files. When no format
/// chosen holds the roads and the labels as pictures (<see cref="EditableChoice.Svg"/> alone), those layers are not
/// made small.
/// </summary>
public sealed class EditableLayersStage(ExportOptions options) : Stage
{
    sealed record MapWork(MapSet Map, MapStyle Style, IReadOnlyList<PlacedLabel> Labels, IReadOnlyList<ResolvedPoi> Pois, PackedBlocks Blocks,
        Dictionary<string, Cell> Cells);

    readonly Dictionary<string, MapWork> _maps = new();
    RoadShapesFile.Contents? _roads;
    MapLayer? _last;
    bool _complete;

    public override string Id => "export.layers";
    public override string Row => "export";
    public override string UnitName => "cell";
    /// <summary>A cell's painter (as the cell drawing's) and the pictures of a block's layers.</summary>
    public override long MemoryPerUnit => 3L << 29;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        EditableWork.Remove(options.Folder);                   // what a stopped export left
        var units = new List<string>();
        if (options.Editable is not { } choice) return units;
        var project = ctx.Project;
        int side = EditableChoice.BlockPx(choice.Zoom);
        _last = choice.Written.Contains(EditableChoice.Psd) ? null : MapLayers.LastPicture;
        foreach (var id in choice.Maps)
        {
            var map = MapSet.Parse(id);
            if (!map.IsCellMap) continue;
            var style = project.StyleOf(map);
            _maps[id] = new MapWork(map, style, EditableFilesStage.LabelsOf(ctx.Folder, map, style), CellDrawStage.PoisOf(project, map),
                new PackedBlocks(EditableWork.Of(options.Folder, map, choice.Zoom), side), CellPlan.For(project.Range.Keys).ToDictionary(c => c.Id.Name));
            units.AddRange(_maps[id].Cells.Keys.Select(c => $"{id}/{c}"));
        }
        if (units.Count > 0) _roads = RoadShapesFile.Read(Path.Combine(ctx.Folder.Data, RoadShapesFile.FileName));
        ctx.Log($"layered files: {string.Join(", ", _maps.Keys)} at zoom {choice.Zoom}, {units.Count} cells to draw in layers"
            + (_last is { } last ? $" (up to the {MapLayers.Name(last)} layer)" : ""));
        return units;
    }

    public override int CountReady(Project project, StateStore state) => 0;

    public override void Run(UnitContext ctx)
    {
        int cut = ctx.Unit.IndexOf('/');
        var work = _maps[ctx.Unit[..cut]];
        var cell = work.Cells[ctx.Unit[(cut + 1)..]];
        int zoom = options.Editable!.Zoom;
        ctx.Report("read", 0);
        using var painter = CellDrawStage.Painter(ctx.Folder, work.Map, work.Style, cell, _roads, work.Labels, work.Pois, out var origin, out _);
        using var writer = work.Blocks.Write(cell.Id.Name);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < cell.Core.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Report("draw", 0.05 + 0.95 * i / cell.Core.Count);
            var b = cell.Core[i];
            var drawn = painter.DrawLayers((b.Bx - origin.Bx) * CellPainter.BlockPx, (b.By - origin.By) * CellPainter.BlockPx, CellPainter.BlockPx, CellPainter.BlockPx, out var shaded);
            var parts = LayeredBlock.Shrink(drawn, shaded, zoom, last: _last);
            for (int k = 0; k < parts.Length; k++)
                if (parts[k] is { } small) writer.Add(k, b, small);
        }
        ctx.Log($"{work.Map.Id} {cell.Id.Name}: {cell.Core.Count} blocks in layers; {clock.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>
    /// A block's picture at zoom 8 (1024 pixels a side, straight RGBA) made as small as the block is at
    /// <paramref name="zoom"/> (7: 512 pixels, 6: 256), the way the tiles of the lower zooms are made
    /// (<see cref="TileStore.BuildParent"/>): each quarter, four tiles of zoom 8, becomes a tile of zoom 7, and for zoom
    /// 6 those four become one. Null when every pixel of the result is clear.
    /// </summary>
    public static byte[]? Shrink(byte[] rgba, int zoom)
    {
        const int t = TileStore.TileSize;
        var z7 = new byte[2 * t * 2 * t * 4];
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                var tile = Lanczos.Resize(TileStore.Crop(rgba, 4 * t, i * 2 * t, j * 2 * t, 2 * t, 2 * t), 2 * t, 2 * t, t, t);
                for (int y = 0; y < t; y++) Buffer.BlockCopy(tile, y * t * 4, z7, ((j * t + y) * 2 * t + i * t) * 4, t * 4);
            }
        var o = zoom >= EditableChoice.FinestZoom ? z7 : Lanczos.Resize(z7, 2 * t, 2 * t, t, t);
        for (int a = 3; a < o.Length; a += 4)
            if (o[a] != 0) return o;
        return null;
    }

    public override void Finish(StageContext ctx, StageEnd end)
    {
        _complete = end.Complete;
        if (!_complete) return;
        foreach (var work in _maps.Values)
        {
            Directory.CreateDirectory(work.Blocks.Folder);
            File.WriteAllText(Path.Combine(work.Blocks.Folder, EditableWork.Drawn), "");
        }
    }

    /// <summary>Layers that are not all drawn make no file: what was drawn goes.</summary>
    public override void Cleanup(StageContext ctx)
    {
        if (!_complete) EditableWork.Remove(options.Folder);
    }
}

/// <summary>
/// The second step of an export's layered files, one unit per map chosen and format, written into the export folder's
/// <c>editable/</c>. The layers of an atlas or road map are the blocks drawn by <see cref="EditableLayersStage"/>; a
/// layer takes the rectangle of the blocks it has something in, and the blocks of the frame outside the range hold the
/// style's open sea in the water layer, as the map's tiles do (nothing when that sea is clear). A satellite map is one
/// layer, its tiles of the files' zoom level.
/// <list type="bullet">
/// <item><see cref="EditableChoice.Psd"/>: <c>&lt;map&gt;-z&lt;zoom&gt;.psd</c> (<see cref="PsdFile"/>; <c>.psb</c> when
///   the picture passes what a PSD file holds), every layer a picture joined row by row, with the map's merged picture
///   from its tiles, through a temporary file.</item>
/// <item><see cref="EditableChoice.Svg"/>: <c>&lt;map&gt;-z&lt;zoom&gt;.svg</c> (<see cref="SvgFile"/>) with the
///   folder <c>&lt;map&gt;-z&lt;zoom&gt;-svg</c> beside it: the layers from the ground to the railway as PNG pictures
///   the file links (<see cref="PngWriter"/>, a row of blocks at a time, the pictures side by side), the roads, the
///   labels and the points of interest as shapes and text from the map's data. SVG has nothing that stands for a
///   layer clipped to the one under it, so where water is see-through the two shading pictures hold only the pixels
///   the ground covers whole, and a pixel the ground covers in part has its shading in the ground picture's colour;
///   put together the pictures are what the PSD file's layers are. Both are made in <c>editable/.work/</c> and moved
///   into place when written, in place of a file and folder of the same name.</item>
/// </list>
/// The folder's record gets each file (<see cref="ExportRecord.AddEditable"/>).
/// </summary>
public sealed class EditableFilesStage(ExportOptions options) : Stage
{
    public override string Id => "export.files";
    public override string Row => "export";
    public override string UnitName => "file";
    /// <summary>The row lengths of every layer's blocks, and the rows of the blocks being joined.</summary>
    public override long MemoryPerUnit => 1L << 30;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        if (options.Editable is not { } choice) return [];
        foreach (var id in choice.Maps)
        {
            var map = MapSet.Parse(id);
            if (map.IsCellMap && !File.Exists(Path.Combine(EditableWork.Of(options.Folder, map, choice.Zoom), EditableWork.Drawn)))
                throw new InvalidOperationException($"the layers of the {id} map were not all drawn");
        }
        Directory.CreateDirectory(Path.Combine(options.Folder, EditableChoice.Folder));
        return choice.Maps.SelectMany(m => choice.Written.Select(f => $"{m}/{f}")).ToList();
    }

    public override int CountReady(Project project, StateStore state) => 0;

    public override void Run(UnitContext ctx)
    {
        int cut = ctx.Unit.LastIndexOf('/');
        var map = MapSet.Parse(ctx.Unit[..cut]);
        if (ctx.Unit[(cut + 1)..] == EditableChoice.Svg) WriteSvg(ctx, map);
        else WritePsd(ctx, map);
    }

    /// <summary>The placed labels a map draws: those of its language and its style's placement (an atlas map with labels), else none.</summary>
    internal static IReadOnlyList<PlacedLabel> LabelsOf(WorkFolder folder, MapSet map, MapStyle style) =>
        map.Kind == MapKind.Atlas && style.Labels is { } ls
            ? LabelsFile.Read(Path.Combine(folder.Data, LabelsFile.FileName(map.Language!, ls.PlacementKey))).Labels
            : [];

    /// <summary>
    /// A layer of an atlas or road map as its blocks: the rectangle of the blocks it has something in, and what the
    /// blocks it has not got hold (<paramref name="Flat"/>; null: nothing).
    /// </summary>
    sealed record LayerBlocks(MapLayer Layer, Dictionary<BlockId, PackedBlocks.Block> Blocks, int X0, int Y0, int Nx, int Ny,
        Func<BlockId, (byte R, byte G, byte B, byte A)?>? Flat);

    /// <summary>
    /// The layers up to <paramref name="last"/> that have something, bottom first. The water layer also takes the
    /// blocks of the frame outside the range, with the style's open sea in them, when that sea is not clear.
    /// </summary>
    static List<LayerBlocks> LayersOf(Project project, MapStyle style, PackedBlocks store, MapLayer last)
    {
        var frame = project.Frame;
        var range = project.Range.Keys.ToHashSet();
        var outside = frame.Blocks.Where(b => !range.Contains(b)).ToList();
        var sea = (style.OpenSea.R, style.OpenSea.G, style.OpenSea.B, style.OpenSeaAlpha);
        var o = new List<LayerBlocks>();
        foreach (var layer in MapLayers.All)
        {
            if (layer > last) break;
            var blocks = store.Read((int)layer);
            bool openSea = layer == MapLayer.Water && style.OpenSeaAlpha > 0 && outside.Count > 0;
            var has = openSea ? blocks.Keys.Concat(outside).ToList() : blocks.Keys.ToList();
            if (has.Count == 0) continue;
            int x0 = has.Min(b => b.Bx), y0 = has.Min(b => b.By), nx = has.Max(b => b.Bx) - x0 + 1, ny = has.Max(b => b.By) - y0 + 1;
            o.Add(new LayerBlocks(layer, blocks, x0, y0, nx, ny, openSea ? b => range.Contains(b) ? null : sea : null));
        }
        return o;
    }

    // ---------------------------------------------------------------- PSD

    void WritePsd(UnitContext ctx, MapSet map)
    {
        var choice = options.Editable!;
        int zoom = choice.Zoom, side = EditableChoice.BlockPx(zoom);
        var frame = ctx.Project.Frame;
        var store = new PackedBlocks(EditableWork.Of(options.Folder, map, zoom), side);
        // a satellite map has no layers drawn before this step: nothing an earlier export left may be read as its picture
        if (!map.IsCellMap && Directory.Exists(store.Folder)) Directory.Delete(store.Folder, recursive: true);

        // the merged picture: the map's tiles of the zoom level, a row of blocks a loop item
        ctx.Report("compose", 0);
        var tiles = new TileStore(ctx.Folder.Tiles(map.Id));
        var rows = Enumerable.Range(frame.By0, frame.BlocksY).ToList();
        int clear = 0, done = 0;
        ctx.Parallel.ForEach(rows, by =>
        {
            ctx.Token.ThrowIfCancellationRequested();
            using var writer = store.Write(string.Create(CultureInfo.InvariantCulture, $"row_{by}"));
            for (int bx = frame.Bx0; bx < frame.Bx1; bx++)
            {
                var picture = BlockOfTiles(tiles, zoom, bx, by, out bool opaque);
                if (!opaque) Volatile.Write(ref clear, 1);
                if (picture is null) continue;
                if (map.IsCellMap && !opaque) OnWhite(picture);
                writer.Add(PackedBlocks.Merged, new BlockId(bx, by), picture);
            }
            ctx.Report("compose", 0.4 * Interlocked.Increment(ref done) / rows.Count);
        });
        int width = frame.BlocksX * side, height = frame.BlocksY * side;
        var fromTiles = store.Read(PackedBlocks.Merged);
        // a block without tiles is clear: white under no opacity in the merged picture (see OnWhite)
        var merged = new PackedBlocks.Rows(store, fromTiles, frame.Bx0, frame.By0, frame.BlocksX, frame.BlocksY, _ => (255, 255, 255, 0));

        var layers = new List<PsdLayer>();
        if (!map.IsCellMap)
            layers.Add(new PsdLayer(SatelliteName(choice.Language), SatelliteName("en"), PsdFile.Normal, 0, 0, width, height,
                new PackedBlocks.Rows(store, fromTiles, frame.Bx0, frame.By0, frame.BlocksX, frame.BlocksY)));
        else
            foreach (var l in LayersOf(ctx.Project, ctx.Project.StyleOf(map), store, MapLayers.All[^1]))
                layers.Add(new PsdLayer(MapLayers.Name(l.Layer, choice.Language), MapLayers.Name(l.Layer), BlendKey(MapLayers.Blend(l.Layer)),
                    (l.X0 - frame.Bx0) * side, (l.Y0 - frame.By0) * side, l.Nx * side, l.Ny * side,
                    new PackedBlocks.Rows(store, l.Blocks, l.X0, l.Y0, l.Nx, l.Ny, l.Flat), MapLayers.Clipped(l.Layer)));

        ctx.Report("compose", 0.4);
        var file = new PsdFile(width, height, layers, merged, mergedAlpha: clear != 0);
        var layout = file.Measure();
        string dir = Path.Combine(options.Folder, EditableChoice.Folder), name = EditableChoice.FileName(map, zoom);
        string path = Path.Combine(dir, name + (layout.Big ? ".psb" : ".psd")), tmp = Path.Combine(dir, name + ".tmp");
        try
        {
            using (var to = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                file.Write(to, layout, at =>
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    ctx.Report("write", 0.45 + 0.55 * at / layout.Length);
                });
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
        // a file of the other format under the same name is this map's earlier one
        File.Delete(Path.Combine(dir, name + (layout.Big ? ".psd" : ".psb")));
        ExportRecord.AddEditable(options.Folder, new ExportEditable(DateTime.UtcNow, map.Id, zoom, EditableChoice.Psd, Path.GetFileName(path), layout.Length,
            width, height, layers.Select(l => l.Name).ToList()));
        ctx.Log($"{Path.GetFileName(path)}: {width} x {height} px, {layers.Count} layers, {layout.Length / 1048576.0:n0} MB");
    }

    /// <summary>The name of a satellite map's one layer in a language of the screens (<c>ja</c>; any other: English).</summary>
    static string SatelliteName(string language) => language == "ja" ? "衛星地図" : "Satellite map";

    /// <summary>
    /// Makes a picture with see-through pixels what a PSD file's merged picture holds: its colours over white, as much
    /// as each pixel is see-through (the opacity stays). Paint programs write the merged picture so, and the readers
    /// that show it take the white out again.
    /// </summary>
    static void OnWhite(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) rgba[i + c] = (byte)((rgba[i + c] * a + 255 * (255 - a) + 127) / 255);
        }
    }

    static string BlendKey(LayerBlend blend) => blend switch
    {
        LayerBlend.Multiply => PsdFile.Multiply,
        LayerBlend.ColorDodge => PsdFile.ColorDodge,
        _ => PsdFile.Normal,
    };

    /// <summary>
    /// A block as a map's tiles of a zoom level have it (one tile at zoom 6, four at zoom 7), straight RGBA; null without
    /// any of its tiles. <paramref name="opaque"/>: every tile is there and no pixel is see-through.
    /// </summary>
    static byte[]? BlockOfTiles(TileStore tiles, int zoom, int bx, int by, out bool opaque)
    {
        const int t = TileStore.TileSize;
        int n = 1 << (zoom - EditableChoice.DefaultZoom), side = n * t;
        byte[]? o = null;
        opaque = true;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var tile = tiles.Read(zoom, n * bx + i, n * by + j);
                if (tile is null)
                {
                    opaque = false;
                    continue;
                }
                o ??= new byte[side * side * 4];
                for (int y = 0; y < t; y++) Buffer.BlockCopy(tile, y * t * 4, o, ((j * t + y) * side + i * t) * 4, t * 4);
            }
        if (o is null) return null;
        for (int a = 3; opaque && a < o.Length; a += 4) opaque = o[a] == 255;
        return o;
    }

    // ---------------------------------------------------------------- SVG

    void WriteSvg(UnitContext ctx, MapSet map)
    {
        var choice = options.Editable!;
        int zoom = choice.Zoom, side = EditableChoice.BlockPx(zoom);
        var project = ctx.Project;
        var frame = project.Frame;
        int width = frame.BlocksX * side, height = frame.BlocksY * side;
        string dir = Path.Combine(options.Folder, EditableChoice.Folder), name = EditableChoice.FileName(map, zoom), pictures = EditableChoice.PicturesFolder(map, zoom);
        // made beside the layers' blocks, moved into place when whole
        string work = Path.Combine(EditableWork.Root(options.Folder), pictures), tmp = Path.Combine(EditableWork.Root(options.Folder), name + ".svg");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        string Href(string file) => pictures + "/" + file;

        ctx.Report("compose", 0);
        var links = new List<SvgPicture>();
        SvgDrawing? drawing = null;
        if (!map.IsCellMap)
        {
            // the photo: the map's tiles of the zoom level, a row of blocks at a time
            var tiles = new TileStore(ctx.Folder.Tiles(map.Id));
            string file = EditableChoice.SatellitePicture + ".png";
            using (var png = PngWriter.Create(Path.Combine(work, file), width, height))
            {
                var rows = new byte[width * side * 4];
                for (int j = 0; j < frame.BlocksY; j++)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    Array.Clear(rows);
                    for (int i = 0; i < frame.BlocksX; i++)
                    {
                        if (BlockOfTiles(tiles, zoom, frame.Bx0 + i, frame.By0 + j, out _) is not { } block) continue;
                        for (int y = 0; y < side; y++) Buffer.BlockCopy(block, y * side * 4, rows, (y * width + i * side) * 4, side * 4);
                    }
                    png.Write(rows);
                    ctx.Report("compose", 0.9 * (j + 1) / frame.BlocksY);
                }
                png.Finish();
            }
            links.Add(new SvgPicture(SvgFile.Id(SatelliteName("en")), SatelliteName(choice.Language), Href(file), 0, 0, width, height));
        }
        else
        {
            var style = project.StyleOf(map);
            var store = new PackedBlocks(EditableWork.Of(options.Folder, map, zoom), side);
            var layers = LayersOf(project, style, store, MapLayers.LastPicture);
            // see-through water cuts the ground: the shading is then kept to the pixels the ground covers whole
            var ground = layers.FirstOrDefault(l => l.Layer == MapLayer.Ground);
            var shading = style.SeeThroughWater ? layers.Where(l => MapLayers.Clipped(l.Layer)).ToList() : [];
            int total = layers.Sum(l => l.Ny), done = 0;
            ctx.Parallel.ForEach(layers, l =>
            {
                string path = Path.Combine(work, EditableChoice.PictureName(l.Layer) + ".png");
                IReadOnlyList<LayerBlocks> with = l.Layer == MapLayer.Ground ? shading
                    : MapLayers.Clipped(l.Layer) && shading.Count > 0 && ground is not null ? [ground] : [];
                WritePicture(ctx, store, l, with, path, () => ctx.Report("compose", 0.9 * Interlocked.Increment(ref done) / total));
            });
            foreach (var l in layers)
                links.Add(new SvgPicture(SvgFile.Id(MapLayers.Name(l.Layer)), MapLayers.Name(l.Layer, choice.Language), Href(EditableChoice.PictureName(l.Layer) + ".png"),
                    (l.X0 - frame.Bx0) * side, (l.Y0 - frame.By0) * side, l.Nx * side, l.Ny * side, MapLayers.Blend(l.Layer)));

            // the PNG icons of the POI styles the map's points draw, copied beside the pictures
            var pois = CellDrawStage.PoisOf(project, map);
            var icons = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in pois.Select(p => p.Style).Where(s => s.Look == "icon" && s.Image is not null && s.ImagePath is not null).DistinctBy(s => s.Id))
            {
                if (!File.Exists(s.ImagePath)) continue;
                string file = "poi-" + string.Concat(s.Id.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')) + ".png";
                File.Copy(s.ImagePath!, Path.Combine(work, file), overwrite: true);
                icons[s.Id] = Href(file);
            }
            drawing = new SvgDrawing
            {
                Style = style,
                Roads = RoadShapesFile.Read(Path.Combine(ctx.Folder.Data, RoadShapesFile.FileName)),
                Labels = LabelsOf(ctx.Folder, map, style),
                Pois = pois,
                Language = map.Language ?? "en",
                Range = project.Range.Keys.ToHashSet(),
                PoiPictures = icons,
            };
        }

        ctx.Token.ThrowIfCancellationRequested();
        ctx.Report("write", 0.9);
        IReadOnlyList<string> names;
        using (var to = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1 << 16) { NewLine = "\n" })
            names = SvgFile.Write(to, frame, zoom, choice.Language, links, drawing, string.Create(CultureInfo.InvariantCulture, $"{map.Id}, zoom {zoom}: one unit is one pixel"));

        // into place: the pictures first, so the file never points at pictures that are not there
        string folder = Path.Combine(dir, pictures), svg = Path.Combine(dir, name + ".svg");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.Move(work, folder);
        File.Move(tmp, svg, overwrite: true);
        long bytes = new FileInfo(svg).Length + new DirectoryInfo(folder).EnumerateFiles().Sum(f => f.Length);
        ExportRecord.AddEditable(options.Folder, new ExportEditable(DateTime.UtcNow, map.Id, zoom, EditableChoice.Svg, Path.GetFileName(svg), bytes, width, height, names.ToList()));
        ctx.Log($"{Path.GetFileName(svg)}: {width} x {height} px, {names.Count} layers ({links.Count} of them pictures), {new FileInfo(svg).Length / 1048576.0:n1} MB and {(bytes - new FileInfo(svg).Length) / 1048576.0:n0} MB of pictures");
    }

    /// <summary>
    /// Writes a layer's blocks as a PNG picture of the layer's rectangle, a row of blocks at a time. With
    /// <paramref name="with"/>, what SVG cannot say of a layer clipped to the ground is put into the pixels: the ground
    /// (<paramref name="with"/>: the shading layers) takes the shading into the colour of every pixel it covers only in
    /// part, as the layers put together give it (the dark layer multiplies, the light layer colour-dodges); a shading
    /// layer (<paramref name="with"/>: the ground) keeps only the pixels the ground covers whole.
    /// </summary>
    static void WritePicture(UnitContext ctx, PackedBlocks store, LayerBlocks layer, IReadOnlyList<LayerBlocks> with, string path, Action rowDone)
    {
        int side = store.Side, width = layer.Nx * side;
        using var pixels = new PackedBlocks.Pixels(store, layer.Blocks, layer.X0, layer.Y0, layer.Nx, layer.Ny, layer.Flat);
        var others = with.Select(o => (o.Layer, Pixels: new PackedBlocks.Pixels(store, o.Blocks, layer.X0, layer.Y0, layer.Nx, layer.Ny, o.Flat), Rows: new byte[width * side * 4])).ToList();
        try
        {
            using var png = PngWriter.Create(path, width, layer.Ny * side);
            var rows = new byte[width * side * 4];
            for (int j = 0; j < layer.Ny; j++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                pixels.Read(j, rows);
                foreach (var o in others) o.Pixels.Read(j, o.Rows);
                if (layer.Layer == MapLayer.Ground && others.Count > 0)
                    ShadeInGround(rows, others.Where(o => o.Layer == MapLayer.ShadeDark).Select(o => o.Rows).FirstOrDefault(),
                        others.Where(o => o.Layer == MapLayer.ShadeLight).Select(o => o.Rows).FirstOrDefault());
                else if (others.Count > 0) KeepToWholeGround(rows, others[0].Rows);
                png.Write(rows);
                rowDone();
            }
            png.Finish();
        }
        finally
        {
            foreach (var o in others) o.Pixels.Dispose();
        }
    }

    /// <summary>
    /// Puts the shading into the colour of every pixel the ground covers only in part (straight RGBA; a null shading
    /// layer has nothing): the colour the ground and the two shading layers clipped to it give there, the dark layer
    /// multiplied with it, the light layer colour-dodged (as <see cref="LayeredBlock"/> finds them). The opacity stays.
    /// </summary>
    internal static void ShadeInGround(Span<byte> ground, ReadOnlySpan<byte> dark, ReadOnlySpan<byte> light)
    {
        for (int i = 0; i < ground.Length; i += 4)
        {
            if (ground[i + 3] is 0 or 255) continue;
            bool darker = !dark.IsEmpty && dark[i + 3] != 0, lighter = !light.IsEmpty && light[i + 3] != 0;
            if (!darker && !lighter) continue;
            for (int c = 0; c < 3; c++)
            {
                double x = ground[i + c] / 255.0 * (darker ? dark[i + c] / 255.0 : 1);
                if (lighter && light[i + c] > 0 && x > 0) x = light[i + c] >= 255 ? 1 : Math.Min(1, x / (1 - light[i + c] / 255.0));
                ground[i + c] = (byte)Math.Round(255 * x);
            }
        }
    }

    /// <summary>Clears every pixel of a shading layer that the ground does not cover whole (straight RGBA).</summary>
    internal static void KeepToWholeGround(Span<byte> shading, ReadOnlySpan<byte> ground)
    {
        for (int i = 0; i < shading.Length; i += 4)
            if (ground[i + 3] != 255) shading[i] = shading[i + 1] = shading[i + 2] = shading[i + 3] = 0;
    }

    public override void Cleanup(StageContext ctx) => EditableWork.Remove(options.Folder);
}
