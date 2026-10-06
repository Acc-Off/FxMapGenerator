using System.Globalization;
using System.IO.Compression;
using System.Text;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Export;

/// <summary>
/// Writes an export into its folder:
/// <code>
/// fxmapgen-export.json          what was written (marks the folder as an export's, so the next one may write over it)
/// web/                          or web.zip with the same contents
///   index.html, leaflet/        the viewer
///   tiles/&lt;map&gt;/{z}/{x}/{y}.png
///   lb-phone.lua, README.txt, CREDITS.txt
/// fxmapgen-minimap-&lt;date&gt;/     the minimap resource (see <see cref="MinimapResource"/>)
/// </code>
/// Units: the tiles of a map by zoom level (the big levels by bands of columns), the viewer files, the minimap
/// resource; as a zip, the web part is one unit. An earlier web export in the folder is removed first. The web tiles
/// go up to the chosen maximum zoom (<see cref="ExportOptions.MaxZoom"/>). A complete export removes the earlier tiles
/// the work folder kept of the maps it wrote (<see cref="WorkFolder.Before"/>): what was written out is the "before" of
/// the next comparison. The minimap resource carries the game's interior maps and, with Cayo Perico's roads read, the
/// island map drawing nothing, both read from this PC's game when the resource is written (<see cref="MinimapGameFiles"/>).
/// </summary>
/// <param name="game">Where the minimap resource's parts from the game's files come from (null: this PC's game).</param>
public sealed class ExportStage(ExportOptions options, string version, MinimapGameFiles? game = null) : Stage
{
    readonly MinimapGameFiles _game = game ?? MinimapGameFiles.Of();
    /// <summary>Tile columns per unit on the big zoom levels.</summary>
    const int ColumnsPerUnit = 16;
    const string Web = "web", WebZip = "web.zip";

    readonly ExportOptions _o = options;
    MapFrame _frame;
    IReadOnlyList<WebMap> _maps = [];
    ExportRecord? _before;
    bool _resourceWritten;
    string _resource = "";
    string _projectName = "";
    string _cellCredits = "";
    int _tiles, _files;
    long _bytes;

    public override string Id => "export";
    public override string Row => "export";
    public override string UnitName => "part";
    public override long MemoryPerUnit => 64L << 20;

    /// <summary>The minimap resource's name (after <see cref="Prepare"/>).</summary>
    public string ResourceName => _resource;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        var project = ctx.Project;
        _frame = project.Frame;
        _projectName = project.File.Name.ReplaceLineEndings(" ");
        var inventory = ExportInventory.Of(project, ctx.State, _game);
        var problems = inventory.Check(_o);
        _files = inventory.Maps.Where(m => _o.Maps.Contains(m.Map)).Sum(m => m.UpTo(_o.MaxZoom).Tiles);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("; ", problems.Select(p => p.Message)));
        Directory.CreateDirectory(_o.Folder);
        ConvertWork.Remove(_o.Folder);                         // what a conversion stopped between its steps left
        _before = ExportRecord.Read(_o.Folder);
        _resource = !_o.Minimap ? "" : _o.ResourceName ?? ExportOptions.DefaultResourceName(_o.Folder, DateTime.Now);
        var units = new List<string>();
        if (_o.Maps.Count > 0)
        {
            // the web part is written fresh: tiles of an earlier export that are not written again must not stay
            var web = Path.Combine(_o.Folder, Web);
            if (Directory.Exists(web)) Directory.Delete(web, recursive: true);
            File.Delete(Path.Combine(_o.Folder, WebZip));
            _maps = _o.Maps.Select(id => WebExport.MapOf(project, ctx.Folder, id, _o.MaxZoom)).ToList();
            _cellCredits = WebExport.CellMapCredits(project, _o.Maps.Select(MapSet.Parse));
            if (_o.Zip) units.Add(WebZip);
            else
            {
                foreach (var m in _o.Maps)
                    foreach (var z in Zooms(ctx.Folder, m, _o.MaxZoom))
                    {
                        // zooms made again from the tiles written at zoom 3 are that unit's (after it wrote them)
                        if (!ExportGrid.Copied(_frame, z)) continue;
                        int columns = ExportGrid.Size(_frame, z).Columns;
                        if (columns <= ColumnsPerUnit || (ExportGrid.Renumbers(_frame) && z == ExportGrid.FirstKept)) units.Add($"tiles/{m}/{z}");
                        else
                            for (int x0 = 0; x0 < columns; x0 += ColumnsPerUnit)
                                units.Add($"tiles/{m}/{z}/{x0}-{Math.Min(columns, x0 + ColumnsPerUnit) - 1}");
                    }
                units.Add("viewer");
            }
        }
        if (_o.Minimap) units.Add("minimap");
        Record(ctx, complete: false);
        ctx.Log($"export to {_o.Folder}: web tiles {(_o.Maps.Count == 0 ? "none" : string.Join(", ", _o.Maps) + $" up to zoom {_o.MaxZoom}" + (_o.Zip ? " as web.zip" : " in web/"))}, " +
            $"minimap {(_o.Minimap ? _resource : "none")}");
        return units;
    }

    public override int CountReady(Project project, StateStore state) => 0;

    public override void Run(UnitContext ctx)
    {
        var u = ctx.Unit;
        if (u == WebZip) WriteZip(ctx);
        else if (u == "viewer") WriteViewer(Path.Combine(_o.Folder, Web), ctx.Project);
        else if (u == "minimap") WriteMinimap(ctx);
        else CopyTiles(ctx, u);
    }

    public override void Finish(StageContext ctx, StageEnd end)
    {
        Record(ctx, end.Complete);
        // the maps written out are what the comparison takes as "before" from now on: their earlier tiles go
        if (end.Complete)
            foreach (var m in _o.Maps)
            {
                var before = ctx.Folder.Before(m);
                if (Directory.Exists(before)) Directory.Delete(before, recursive: true);
            }
        ctx.Log($"export {(end.Complete ? "written" : "stopped part way")}:" + (_o.Maps.Count > 0 ? $" {_tiles} tiles ({_bytes / 1048576.0:n0} MB)" : "") +
            (_o.Minimap ? $", minimap resource {_resource}" : "") + $" in {_o.Folder}");
    }

    /// <summary>The folder's record: this export's web part (or the earlier one when it writes none), the resources there.</summary>
    void Record(StageContext ctx, bool complete)
    {
        var now = DateTime.UtcNow;
        var web = _o.Maps.Count > 0
            ? new ExportWeb(now, _o.Maps, _o.Zip, Volatile.Read(ref _tiles), Interlocked.Read(ref _bytes), _o.BaseUrl, complete, _o.MaxZoom)
            : _before?.Web;
        var resources = (_before?.Resources ?? []).Where(r => r.Name != _resource && Directory.Exists(Path.Combine(_o.Folder, r.Name))).ToList();
        if (Volatile.Read(ref _resourceWritten))
        {
            var mm = ctx.Project.File.Minimap;
            resources.Add(new ExportResource(now, _resource, mm.Map!, MinimapSheets.Size));
        }
        new ExportRecord(now, version, ctx.Project.FilePath, web, resources, _before?.EditableIn(_o.Folder), _before?.WebEditedIn(_o.Folder)).Write(_o.Folder);
    }

    /// <summary>
    /// <c>tiles/&lt;map&gt;/&lt;z&gt;</c> or <c>tiles/&lt;map&gt;/&lt;z&gt;/&lt;x0&gt;-&lt;x1&gt;</c>: the tile columns of one zoom level,
    /// in the numbers written out (<see cref="ExportGrid"/>). When the numbers move, the unit of zoom 3 then makes zooms 2 to 0.
    /// </summary>
    void CopyTiles(UnitContext ctx, string unit)
    {
        var parts = unit.Split('/');
        string map = parts[1];
        int z = int.Parse(parts[2], CultureInfo.InvariantCulture);
        var source = Path.Combine(ctx.Folder.Tiles(map), Num(z));
        var root = Path.Combine(_o.Folder, Web, "tiles", MapSet.Parse(map).ExportName);
        var target = Path.Combine(root, Num(z));
        var (cols, rows) = ExportGrid.Size(_frame, z);
        var (sx, sy) = ExportGrid.Shift(_frame, z);
        var (x0, x1) = parts.Length == 4
            ? (int.Parse(parts[3].Split('-')[0], CultureInfo.InvariantCulture), int.Parse(parts[3].Split('-')[1], CultureInfo.InvariantCulture))
            : (0, cols - 1);
        var columns = Enumerable.Range(x0, x1 - x0 + 1).ToList();
        for (int i = 0; i < columns.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Report("copy", i / (double)columns.Count);
            var dir = new DirectoryInfo(Path.Combine(source, Num(columns[i] + sx)));
            if (!dir.Exists) continue;
            var to = Path.Combine(target, Num(columns[i]));
            Directory.CreateDirectory(to);
            foreach (var f in dir.EnumerateFiles("*.png"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(f.Name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y) || y - sy < 0 || y - sy >= rows) continue;
                f.CopyTo(Path.Combine(to, Num(y - sy) + ".png"), overwrite: true);
                Interlocked.Increment(ref _tiles);
                Interlocked.Add(ref _bytes, f.Length);
            }
        }
        if (ExportGrid.Renumbers(_frame) && z == ExportGrid.FirstKept)
        {
            ctx.Report("lower zooms", 0.95);
            var (n, bytes) = MakeBelowFirstKept(root, _frame, ctx.Token);
            Interlocked.Add(ref _tiles, n);
            Interlocked.Add(ref _bytes, bytes);
        }
    }

    /// <summary>
    /// Makes the tiles of zooms 2 to 0 of a map written out with moved numbers (<see cref="ExportGrid"/>) from its tiles of
    /// zoom 3 in <paramref name="root"/> (<c>&lt;root&gt;/{z}/{x}/{y}.png</c>, as the lower zooms steps make theirs). Returns
    /// the tiles and bytes written.
    /// </summary>
    public static (int Tiles, long Bytes) MakeBelowFirstKept(string root, MapFrame frame, CancellationToken token = default)
    {
        var store = new Satellite.TileStore(root);
        int n = 0;
        long bytes = 0;
        for (int z = ExportGrid.FirstKept - 1; z >= 0; z--)
        {
            var (cols, rows) = ExportGrid.Size(frame, z);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!store.BuildParent(z, x, y)) continue;
                    n++;
                    bytes += new FileInfo(store.PathOf(z, x, y)).Length;
                }
        }
        return (n, bytes);
    }

    static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A tile file of the work folder (<c>{z}/{x}/{y}.png</c>) lies in the project's frame (a larger frame's leave none behind once its lower zooms are made again).</summary>
    bool InFrame(int z, FileInfo f)
    {
        var (x0, y0, x1, y1) = _frame.Tiles(z);
        return int.TryParse(f.Directory?.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(Path.GetFileNameWithoutExtension(f.Name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y)
            && x >= x0 && x < x1 && y >= y0 && y < y1;
    }

    void WriteViewer(string web, Project project)
    {
        Directory.CreateDirectory(web);
        foreach (var (path, bytes) in ViewerParts(project))
        {
            var file = Path.Combine(web, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }
    }

    IEnumerable<(string Path, byte[] Bytes)> ViewerParts(Project project)
    {
        var utf8 = new UTF8Encoding(false);
        yield return ("index.html", utf8.GetBytes(WebExport.ViewerHtml(_projectName, _maps, version, _frame)));
        foreach (var f in WebExport.ViewerFiles()) yield return f;
        yield return ("lb-phone.lua", utf8.GetBytes(WebExport.LbPhoneExample(_projectName, _maps, _o.BaseUrl, _o.MaxZoom, _frame)));
        yield return ("README.txt", utf8.GetBytes(WebExport.Readme(_projectName, _maps, _files, version, _o.MaxZoom, _frame)));
        yield return ("CREDITS.txt", utf8.GetBytes(WebExport.Credits(_projectName, _maps, version, _cellCredits)));
    }

    /// <summary>
    /// The web part as one zip: the viewer files first, then every tile (stored as they are: PNGs do not shrink). With
    /// moved numbers (<see cref="ExportGrid"/>), the tiles of zooms 2 to 0 are made in a temporary folder first.
    /// </summary>
    void WriteZip(UnitContext ctx)
    {
        var path = Path.Combine(_o.Folder, WebZip);
        var tmp = path + ".tmp";
        string? lower = ExportGrid.Renumbers(_frame) ? Path.Combine(Path.GetTempPath(), "fxmapgen-export-" + Guid.NewGuid().ToString("N")) : null;
        List<(FileInfo File, string Entry)> tiles;
        if (lower is null)
            tiles = _o.Maps.SelectMany(m =>
            {
                var root = new DirectoryInfo(ctx.Folder.Tiles(m));
                return Zooms(ctx.Folder, m, _o.MaxZoom).SelectMany(z => new DirectoryInfo(Path.Combine(root.FullName, z.ToString())).EnumerateFiles("*.png", SearchOption.AllDirectories)
                        .Where(f => InFrame(z, f)))
                    .Select(f => (File: f, Entry: $"tiles/{MapSet.Parse(m).ExportName}/{Path.GetRelativePath(root.FullName, f.FullName).Replace('\\', '/')}"));
            }).ToList();
        else
        {
            tiles = [];
            foreach (var m in _o.Maps)
            {
                var name = MapSet.Parse(m).ExportName;
                var made = Path.Combine(lower, name);
                foreach (var z in Zooms(ctx.Folder, m, _o.MaxZoom).Where(z => ExportGrid.Copied(_frame, z)))
                {
                    var (cols, rows) = ExportGrid.Size(_frame, z);
                    var (sx, sy) = ExportGrid.Shift(_frame, z);
                    foreach (var xDir in new DirectoryInfo(Path.Combine(ctx.Folder.Tiles(m), Num(z))).EnumerateDirectories())
                    {
                        if (!int.TryParse(xDir.Name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x) || x - sx < 0 || x - sx >= cols) continue;
                        foreach (var f in xDir.EnumerateFiles("*.png"))
                        {
                            if (!int.TryParse(Path.GetFileNameWithoutExtension(f.Name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y) || y - sy < 0 || y - sy >= rows) continue;
                            tiles.Add((f, $"tiles/{name}/{Num(z)}/{Num(x - sx)}/{Num(y - sy)}.png"));
                            if (z != ExportGrid.FirstKept) continue;
                            var copy = Path.Combine(made, Num(z), Num(x - sx), Num(y - sy) + ".png");
                            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                            f.CopyTo(copy, overwrite: true);
                        }
                    }
                }
                ctx.Report("lower zooms", 0);
                MakeBelowFirstKept(made, _frame, ctx.Token);
                for (int z = 0; z < ExportGrid.FirstKept; z++)
                {
                    var dir = new DirectoryInfo(Path.Combine(made, Num(z)));
                    if (dir.Exists)
                        tiles.AddRange(dir.EnumerateFiles("*.png", SearchOption.AllDirectories)
                            .Select(f => (f, $"tiles/{name}/{Path.GetRelativePath(made, f.FullName).Replace('\\', '/')}")));
                }
            }
        }
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var (name, bytes) in ViewerParts(ctx.Project))
                {
                    using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    s.Write(bytes);
                }
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (i % 256 == 0)
                    {
                        ctx.Token.ThrowIfCancellationRequested();
                        ctx.Report("zip", i / (double)tiles.Count);
                    }
                    var (file, entry) = tiles[i];
                    zip.CreateEntryFromFile(file.FullName, entry, CompressionLevel.NoCompression);
                    Interlocked.Increment(ref _tiles);
                    Interlocked.Add(ref _bytes, file.Length);
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
        finally
        {
            if (lower is not null && Directory.Exists(lower)) Directory.Delete(lower, recursive: true);
        }
    }

    void WriteMinimap(UnitContext ctx)
    {
        ctx.Report("resource", 0);
        var mm = ctx.Project.File.Minimap;
        var map = MapSet.Parse(mm.Map!);
        var (interiors, island) = _game.Read(ctx);
        ctx.Report("resource", 0.2);
        MinimapResource.Write(_o.Folder, _resource, ctx.Folder.Minimap(map.Id), map, map.Title(ctx.Project), _projectName, version,
            MinimapExtraTiles.Cells(ctx.Project.Range.Keys), interiors, island, WebExport.CellMapCredits(ctx.Project, [map]));
        Volatile.Write(ref _resourceWritten, true);
    }

    /// <summary>The zoom levels of a map's tiles up to <paramref name="maxZoom"/>.</summary>
    static IEnumerable<int> Zooms(WorkFolder folder, string map, int maxZoom)
    {
        var root = folder.Tiles(map);
        return Enumerable.Range(0, Math.Min(maxZoom, WorldGrid.Zoom) + 1).Where(z => Directory.Exists(Path.Combine(root, z.ToString())));
    }

}
