using System.Globalization;
using System.Text;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Export;

/// <summary>
/// The first step of a conversion (<see cref="ConvertOptions"/>), one unit: the picture is read row by row
/// (<see cref="PngRows"/>) and cut into the tiles of its zoom level (6 or 7, by its size), numbered as a work folder's
/// tiles are; then the zooms below it down to 0 are made from them as every map's are (<see cref="LowZooms.BuildParents"/>).
/// The tiles go into the output folder's <c>.convert/tiles/</c>, which the last step removes. The picture's pixels are
/// the tiles' pixels: nothing is drawn over or under them.
/// </summary>
/// <param name="game">Where the minimap resource's parts from the game's files come from (null: this PC's game), for the check of what the conversion needs.</param>
public sealed class ConvertTilesStage(ConvertOptions options, MinimapGameFiles? game = null) : Stage
{
    public override string Id => "convert.tiles";
    public override string Row => "export";
    public override string UnitName => "picture";
    /// <summary>A row of tiles of the widest picture as RGBA (34 MB), the tiles being encoded and the reader's rows.</summary>
    public override long MemoryPerUnit => 256L << 20;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        ConvertWork.Remove(options.Folder);                    // what a stopped conversion left
        var problems = ConvertStages.Check(ctx.Project, options, game: game);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.Message)));
        Directory.CreateDirectory(options.Folder);
        ctx.Log($"convert {options.Picture} (made from the {options.Map} map) to {options.Folder}: " +
            $"web tiles {(options.Tiles ? "yes" : "no")}, minimap resource {(options.Minimap ? "yes" : "no")}");
        return [Path.GetFileName(options.Picture)];
    }

    public override int CountReady(Project project, StateStore state) => 0;

    public override void Run(UnitContext ctx)
    {
        var frame = ctx.Project.Frame;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ctx.Report("read", 0);
        using var png = PngRows.Open(options.Picture);
        int zoom = EditedPicture.ZoomOf(frame, png.Width, png.Height)
            ?? throw new InvalidDataException(EditedPicture.Sentence(new PictureInfo(options.Picture, png.Width, png.Height, null, EditedPicture.BadSize), frame));
        var tiles = new TileStore(ConvertWork.Tiles(options.Folder));
        int written = Cut(png, frame, zoom, tiles, ctx.Parallel, f => ctx.Report("tiles", 0.8 * f));
        int parents = LowZooms.BuildParents(tiles, frame, ctx.Parallel, (s, f) => ctx.Report(s, 0.8 + 0.2 * f), ctx.Token, zoom);
        File.WriteAllText(Path.Combine(ConvertWork.Root(options.Folder), ConvertWork.Made), zoom.ToString(CultureInfo.InvariantCulture));
        ctx.Log($"{Path.GetFileName(options.Picture)}: {png.Width} x {png.Height} px, {written} tiles at z{zoom}, {parents} at z{zoom - 1}..z0; {clock.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>
    /// Cuts a picture of a frame at a zoom level into that zoom's tiles (256 px, <c>{z}/{x}/{y}.png</c> with the frame's
    /// own tile numbers), a row of tiles at a time. Returns the tiles written. <paramref name="report"/> gets the share done.
    /// </summary>
    public static int Cut(PngRows png, MapFrame frame, int zoom, TileStore tiles, IParallelRunner parallel, Action<double>? report = null)
    {
        const int t = TileStore.TileSize;
        int width = png.Width, columns = width / t, lines = png.Height / t;
        var (x0, y0, _, _) = frame.Tiles(zoom);
        var band = new byte[width * t * 4];
        var across = Enumerable.Range(0, columns).ToList();
        for (int j = 0; j < lines; j++)
        {
            parallel.Token.ThrowIfCancellationRequested();
            if (png.Read(band) != t) throw new InvalidDataException($"the picture ends at row {j * t}");
            int y = y0 + j;
            parallel.ForEach(across, i => tiles.Write(zoom, x0 + i, y, TileStore.Crop(band, width, i * t, 0, t, t)));
            report?.Invoke((j + 1) / (double)lines);
        }
        return columns * lines;
    }

    /// <summary>Tiles that are not all made make nothing: what was made goes.</summary>
    public override void Cleanup(StageContext ctx)
    {
        if (ConvertWork.MadeZoom(options.Folder) is null) ConvertWork.Remove(options.Folder);
    }
}

/// <summary>
/// The second step of a conversion that writes the minimap resource: the minimap's texture dictionaries from the
/// picture's tiles, made as the minimap step makes a map's (<see cref="MinimapTextures"/>): a unit per sheet, one for the
/// small whole map (over the open sea of the picture's map, <see cref="MinimapTextures.Under"/>) and one per cell beyond
/// the standard frame that holds blocks of the range. The project's <c>minimap.outside</c> counts as it does for a map:
/// with <c>transparent</c>, the blocks outside the range stay see-through, whatever the picture has there. The
/// dictionaries go into the output folder's <c>.convert/ytd/</c>.
/// </summary>
public sealed class ConvertTexturesStage(ConvertOptions options) : Stage
{
    IReadOnlySet<BlockId>? _only;
    MapSet _map = MapSet.Satellite;
    bool _complete;

    public override string Id => "convert.textures";
    public override string Row => "export";
    public override string UnitName => "sheet";
    /// <summary>A sheet as RGBA and both compressed copies (as the minimap step's unit).</summary>
    public override long MemoryPerUnit => 128L << 20;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        if (ConvertWork.MadeZoom(options.Folder) is null) throw new InvalidOperationException("the picture's tiles were not all made");
        _map = MapSet.Parse(options.Map);
        _only = ctx.Project.File.Minimap.Outside == MinimapOutside.Transparent ? ctx.Project.Range.Keys.ToHashSet() : null;
        var units = MinimapSheets.All.Select(s => s.Name).Append(MinimapLod.Unit).ToList();
        units.AddRange(MinimapExtraTiles.Cells(ctx.Project.Range.Keys).Select(c => c.Name));
        return units;
    }

    public override int CountReady(Project project, StateStore state) => 0;

    public override void Run(UnitContext ctx)
    {
        var tiles = new TileStore(ConvertWork.Tiles(options.Folder));
        var dir = ConvertWork.Textures(options.Folder);
        if (ctx.Unit == MinimapLod.Unit) MinimapTextures.WriteLod(dir, tiles, MinimapTextures.Under(ctx.Project, _map), ctx.Report);
        else if (CellId.TryParse(ctx.Unit, out var cell)) MinimapTextures.WriteExtra(dir, tiles, cell, _only, ctx.Parallel, ctx.Report);
        else if (SheetId.TryParse(ctx.Unit, out var sheet)) MinimapTextures.WriteSheet(dir, tiles, sheet, _only, ctx.Parallel, ctx.Report);
        else throw new ArgumentException($"not a texture unit: {ctx.Unit}");
    }

    public override void Finish(StageContext ctx, StageEnd end) => _complete = end.Complete;

    /// <summary>Dictionaries that are not all made make no resource: what was made goes.</summary>
    public override void Cleanup(StageContext ctx)
    {
        if (!_complete) ConvertWork.Remove(options.Folder);
    }
}

/// <summary>
/// The last step of a conversion, a unit per output: <c>web</c> writes the picture's tiles into the output folder's
/// <c>web-edited/&lt;name&gt;/</c> as a web part of its own (numbered as an export numbers its tiles,
/// <see cref="ExportGrid"/>; the viewer, the lb-phone example with the picture's zoom level as its finest, a README and
/// the credits; a folder of that name is replaced, the others stay), <c>resource</c> writes a minimap resource as an
/// export's (<see cref="MinimapResource"/>) with the dictionaries made from the picture. &lt;name&gt;, the title, the colour
/// behind the tiles and the credits are those of the map the picture was made from. The folder's record gets both
/// (<see cref="ExportRecord.WebEdited"/>, a resource with <see cref="ExportResource.FromPicture"/>); the export's own
/// <c>web/</c> is not touched. The tiles and dictionaries made for it (<c>.convert/</c>) are removed at the end.
/// </summary>
/// <param name="game">Where the minimap resource's parts from the game's files come from (null: this PC's game).</param>
public sealed class ConvertFilesStage(ConvertOptions options, string version, MinimapGameFiles? game = null) : Stage
{
    readonly MinimapGameFiles _game = game ?? MinimapGameFiles.Of();
    const string Web = "web", Resource = "resource";

    MapSet _map = MapSet.Satellite;
    MapFrame _frame;
    int _zoom, _tiles;
    long _bytes;
    string _resource = "", _projectName = "";
    bool _webWritten, _resourceWritten;

    public override string Id => "convert.files";
    public override string Row => "export";
    public override string UnitName => "part";
    public override long MemoryPerUnit => 64L << 20;

    /// <summary>The minimap resource's name (after <see cref="Prepare"/>).</summary>
    public string ResourceName => _resource;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        _zoom = ConvertWork.MadeZoom(options.Folder) ?? throw new InvalidOperationException("the picture's tiles were not all made");
        _map = MapSet.Parse(options.Map);
        _frame = ctx.Project.Frame;
        _projectName = ctx.Project.File.Name.ReplaceLineEndings(" ");
        _resource = !options.Minimap ? "" : options.ResourceName ?? ExportOptions.DefaultResourceName(options.Folder, DateTime.Now);
        var units = new List<string>();
        if (options.Tiles) units.Add(Web);
        if (options.Minimap) units.Add(Resource);
        return units;
    }

    public override int CountReady(Project project, StateStore state) => 0;

    public override void Run(UnitContext ctx)
    {
        if (ctx.Unit == Web) WriteWeb(ctx);
        else WriteResource(ctx);
    }

    void WriteWeb(UnitContext ctx)
    {
        string name = _map.ExportName, root = Path.Combine(options.Folder, ConvertOptions.WebFolder, name);
        Record(ctx, complete: false, web: true);               // from here on the folder is being written
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var (n, bytes) = CopyTiles(ConvertWork.Tiles(options.Folder), Path.Combine(root, "tiles", name), _frame, _zoom, ctx.Token, f => ctx.Report("copy", 0.95 * f));
        _tiles = n;
        _bytes = bytes;
        ctx.Report("write", 0.95);
        var maps = new[] { WebExport.MapOf(ctx.Project, ctx.Folder, _map.Id, _zoom) };
        var utf8 = new UTF8Encoding(false);
        var parts = new List<(string Path, byte[] Bytes)> { ("index.html", utf8.GetBytes(WebExport.ViewerHtml(_projectName, maps, version, _frame))) };
        parts.AddRange(WebExport.ViewerFiles());
        parts.Add(("lb-phone.lua", utf8.GetBytes(WebExport.LbPhoneExample(_projectName, maps, options.BaseUrl, _zoom, _frame))));
        parts.Add(("README.txt", utf8.GetBytes(WebExport.Readme(_projectName, maps, n, version, _zoom, _frame))));
        parts.Add(("CREDITS.txt", utf8.GetBytes(WebExport.Credits(_projectName, maps, version, WebExport.CellMapCredits(ctx.Project, [_map])))));
        foreach (var (path, content) in parts)
        {
            var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, content);
        }
        Volatile.Write(ref _webWritten, true);
    }

    /// <summary>
    /// Writes tiles numbered as a work folder's (<paramref name="from"/>, <c>{z}/{x}/{y}.png</c>) into
    /// <paramref name="to"/> numbered as an export's (<see cref="ExportGrid"/>), zooms 0 to <paramref name="zoom"/>: with
    /// moved numbers, zooms 2 to 0 are made again from the tiles written at zoom 3. Returns the tiles and bytes written.
    /// </summary>
    public static (int Tiles, long Bytes) CopyTiles(string from, string to, MapFrame frame, int zoom, CancellationToken token = default, Action<double>? report = null)
    {
        static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);
        int n = 0;
        long bytes = 0;
        for (int z = zoom; z >= 0; z--)
        {
            if (!ExportGrid.Copied(frame, z)) continue;
            var (columns, lines) = ExportGrid.Size(frame, z);
            var (sx, sy) = ExportGrid.Shift(frame, z);
            for (int x = 0; x < columns; x++)
            {
                token.ThrowIfCancellationRequested();
                var dir = Path.Combine(to, Num(z), Num(x));
                bool made = false;
                for (int y = 0; y < lines; y++)
                {
                    var file = new FileInfo(Path.Combine(from, Num(z), Num(x + sx), Num(y + sy) + ".png"));
                    if (!file.Exists) continue;
                    if (!made) Directory.CreateDirectory(dir);
                    made = true;
                    file.CopyTo(Path.Combine(dir, Num(y) + ".png"), overwrite: true);
                    n++;
                    bytes += file.Length;
                }
            }
            report?.Invoke((zoom - z + 1) / (double)(zoom + 1));
        }
        if (ExportGrid.Renumbers(frame))
        {
            var (made, madeBytes) = ExportStage.MakeBelowFirstKept(to, frame, token);
            n += made;
            bytes += madeBytes;
        }
        return (n, bytes);
    }

    void WriteResource(UnitContext ctx)
    {
        ctx.Report("resource", 0);
        var (interiors, island) = _game.Read(ctx);
        ctx.Report("resource", 0.2);
        MinimapResource.Write(options.Folder, _resource, ConvertWork.Textures(options.Folder), _map, _map.Title(ctx.Project), _projectName, version,
            MinimapExtraTiles.Cells(ctx.Project.Range.Keys), interiors, island, WebExport.CellMapCredits(ctx.Project, [_map]));
        Volatile.Write(ref _resourceWritten, true);
    }

    public override void Finish(StageContext ctx, StageEnd end)
    {
        Record(ctx, end.Complete, Volatile.Read(ref _webWritten));
        ctx.Log($"conversion {(end.Complete ? "written" : "stopped part way")}:" +
            (Volatile.Read(ref _webWritten) ? $" {_tiles} tiles ({_bytes / 1048576.0:n0} MB) in {ConvertOptions.WebFolder}/{_map.ExportName}" : "") +
            (Volatile.Read(ref _resourceWritten) ? $" minimap resource {_resource}" : "") + $" in {options.Folder}");
    }

    static readonly object Recording = new();

    /// <summary>
    /// The folder's record with this conversion's outputs: the web tiles' entry under its name (in place of an earlier one;
    /// <paramref name="web"/>: it is being or was written) and the resource once it is written. A folder without a record
    /// gets one (a conversion may be the first thing written there).
    /// </summary>
    void Record(StageContext ctx, bool complete, bool web)
    {
        lock (Recording)
        {
            var now = DateTime.UtcNow;
            var before = ExportRecord.Read(options.Folder) ?? new ExportRecord(now, version, ctx.Project.FilePath, null, []);
            var edited = before.WebEditedIn(options.Folder).Where(e => e.Name != _map.ExportName).ToList();
            if (options.Tiles && web)
                edited.Add(new ExportWebEdited(now, _map.ExportName, _map.Id, Path.GetFileName(options.Picture), _zoom, _tiles, _bytes, complete && Volatile.Read(ref _webWritten)));
            else edited.AddRange(before.WebEditedIn(options.Folder).Where(e => e.Name == _map.ExportName));
            var resources = before.ResourcesIn(options.Folder).Where(r => r.Name != _resource).ToList();
            if (Volatile.Read(ref _resourceWritten)) resources.Add(new ExportResource(now, _resource, _map.Id, MinimapSheets.Size, FromPicture: true));
            (before with { AtUtc = now, Version = version, Resources = resources, Editable = before.EditableIn(options.Folder), WebEdited = edited.OrderBy(e => e.Name, StringComparer.Ordinal).ToList() })
                .Write(options.Folder);
        }
    }

    public override void Cleanup(StageContext ctx) => ConvertWork.Remove(options.Folder);
}
