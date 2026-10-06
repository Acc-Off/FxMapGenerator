using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Export;

/// <param name="UpToDate">Nothing is left for this map in the to-do table (else the tiles lag behind the data).</param>
/// <param name="TilesPerZoom">Tiles per zoom level (index = zoom, 0..8).</param>
/// <param name="BytesPerZoom">Bytes per zoom level (index = zoom, 0..8).</param>
public sealed record TileSetInfo(string Map, int Tiles, long Bytes, IReadOnlyList<int> Zooms, bool UpToDate, IReadOnlyList<int> TilesPerZoom, IReadOnlyList<long> BytesPerZoom)
{
    /// <summary>The tiles and bytes of the levels up to <paramref name="maxZoom"/>.</summary>
    public (int Tiles, long Bytes) UpTo(int maxZoom) =>
        (TilesPerZoom.Take(maxZoom + 1).Sum(), BytesPerZoom.Take(maxZoom + 1).Sum());
}

/// <param name="Map">The map chosen for the minimap; null = none.</param>
/// <param name="Made">
/// Textures made and up to date: the sheets whose two dictionaries exist (of <see cref="MinimapSheets.All"/>), the small
/// whole map (<see cref="MinimapLod"/>) and the textures beyond the standard frame (<see cref="MinimapExtraTiles"/>),
/// <see cref="Total"/> in all.
/// </param>
/// <param name="Extra">The textures beyond the standard frame the range needs (one per cell).</param>
/// <param name="IslandMap">The resource carries Cayo Perico's island map drawing nothing (the project reads the island's roads).</param>
/// <param name="IslandLandMissing">Cayo Perico's land blocks the range does not hold (with <paramref name="IslandMap"/>).</param>
/// <param name="Game">
/// Why this PC cannot read what the resource takes from the game's files (the interior maps, the island map;
/// <see cref="MinimapGameFiles"/>): <c>gta</c> (GTA V not found), <c>keys</c> (the RPF keys not found); null = it can.
/// </param>
public sealed record MinimapInfo(string? Map, int Size, int Made, long Bytes, int Extra = 0, bool IslandMap = false, int IslandLandMissing = 0,
    string? Game = null)
{
    /// <summary>The sheets and the small whole map.</summary>
    public const int Standard = 7;
    public int Total => Standard + Extra;
    public bool Ready => Map is not null && Made == Total;
}

/// <summary>A map that can be written as a layered file for editing (<see cref="EditableChoice"/>).</summary>
/// <param name="Ready">
/// What the file is made of is at hand: the map's tiles with their lower zooms (the merged picture), and for an atlas
/// or road map the data of every cell of the range, the road shapes and the labels (the layers).
/// </param>
/// <param name="UpToDate">Nothing is left for this map in the to-do table.</param>
public sealed record EditableMapInfo(string Map, bool Ready, bool UpToDate);

/// <summary>The maps that can be written as layered files, and the blocks of the frame's sides (a picture's pixels: 256 a block at zoom 6).</summary>
public sealed record EditableInfo(IReadOnlyList<EditableMapInfo> Maps, int BlocksX, int BlocksY);

/// <summary>A layered file in an export folder's <see cref="EditableChoice.Folder"/>.</summary>
/// <param name="Format"><see cref="EditableChoice.Psd"/> or <see cref="EditableChoice.Svg"/>.</param>
/// <param name="File">The file's name there.</param>
/// <param name="Bytes">The file's bytes; an SVG file's with the pictures it links.</param>
/// <param name="Layers">The layers' names, bottom first.</param>
public sealed record ExportEditable(DateTime AtUtc, string Map, int Zoom, string Format, string File, long Bytes, int Width, int Height, IReadOnlyList<string> Layers);

/// <summary>The web part of an export folder (written fresh by each export that writes it).</summary>
/// <param name="Complete">False while it is being written or when the export was stopped part way.</param>
/// <param name="MaxZoom">The finest zoom level written.</param>
public sealed record ExportWeb(DateTime AtUtc, IReadOnlyList<string> Maps, bool Zip, int Tiles, long Bytes, string? BaseUrl, bool Complete, int MaxZoom);

/// <summary>A minimap resource in an export folder.</summary>
/// <param name="FromPicture">Its pictures were made from an edited picture (<see cref="ConvertOptions"/>), not from the map's tiles.</param>
public sealed record ExportResource(DateTime AtUtc, string Name, string Map, int Size, bool FromPicture = false);

/// <summary>The web tiles of an edited picture in an export folder's <see cref="ConvertOptions.WebFolder"/>.</summary>
/// <param name="Name">The folder's name there: the tile folder's name of the map the picture was made from.</param>
/// <param name="Map">The id of that map.</param>
/// <param name="Picture">The picture's file name.</param>
/// <param name="Zoom">The picture's zoom level, the finest of the tiles.</param>
/// <param name="Complete">False while it is being written or when the conversion was stopped part way.</param>
public sealed record ExportWebEdited(DateTime AtUtc, string Name, string Map, string Picture, int Zoom, int Tiles, long Bytes, bool Complete);

/// <summary>
/// What an export folder holds, in <c>fxmapgen-export.json</c>: the web part as last written, every minimap resource
/// written there (each export adds one under a new name; those whose folder is gone drop out), the layered files
/// for editing (a file written again takes its earlier entry's place; those whose file is gone drop out) and the web
/// tiles made from edited pictures (one entry a folder of <see cref="ConvertOptions.WebFolder"/>; those whose folder is
/// gone drop out).
/// </summary>
public sealed record ExportRecord(DateTime AtUtc, string Version, string Project, ExportWeb? Web, IReadOnlyList<ExportResource> Resources,
    IReadOnlyList<ExportEditable>? Editable = null, IReadOnlyList<ExportWebEdited>? WebEdited = null)
{
    public const string FileName = "fxmapgen-export.json";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ExportRecord? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ExportRecord>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    public void Write(string folder) => File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Json) + "\n");

    /// <summary>The layered files of the record that are still in the folder.</summary>
    public IReadOnlyList<ExportEditable> EditableIn(string folder) =>
        (Editable ?? []).Where(e => File.Exists(Path.Combine(folder, EditableChoice.Folder, e.File))).ToList();

    /// <summary>The web tiles of edited pictures of the record that are still in the folder.</summary>
    public IReadOnlyList<ExportWebEdited> WebEditedIn(string folder) =>
        (WebEdited ?? []).Where(e => Directory.Exists(Path.Combine(folder, ConvertOptions.WebFolder, e.Name))).ToList();

    /// <summary>The minimap resources of the record that are still in the folder.</summary>
    public IReadOnlyList<ExportResource> ResourcesIn(string folder) =>
        Resources.Where(r => Directory.Exists(Path.Combine(folder, r.Name))).ToList();

    static readonly object Adding = new();

    /// <summary>
    /// Puts a layered file into the folder's record, in place of an earlier entry of the same file; entries whose file
    /// is gone drop out.
    /// </summary>
    public static void AddEditable(string folder, ExportEditable file)
    {
        lock (Adding)
        {
            if (Read(folder) is not { } record) return;
            var files = record.EditableIn(folder).Where(e => !string.Equals(e.File, file.File, StringComparison.OrdinalIgnoreCase)).Append(file).ToList();
            (record with { AtUtc = file.AtUtc, Editable = files }).Write(folder);
        }
    }
}

/// <param name="Code">
/// <c>NOTHING</c> (nothing chosen), <c>NO_TILES</c> (a chosen map has no tiles), <c>MINIMAP_NOT_READY</c>,
/// <c>NO_MINIMAP</c> (the project has none), <c>MINIMAP_NO_GTA</c> / <c>MINIMAP_NO_KEYS</c> (what the minimap resource
/// takes from the game's files cannot be read from this PC's game), <c>NOT_EMPTY</c> (the folder holds other files), <c>BAD_NAME</c> (resource name), <c>BAD_URL</c>,
/// <c>BAD_ZOOM</c> (maximum zoom), <c>NO_FOLDER</c>, <c>EDITABLE_NOT_READY</c> (a map chosen for a layered file misses
/// what the file is made of), <c>BAD_EDITABLE</c> (the zoom level or a format of the layered files),
/// <c>NO_EDITABLE_FORMAT</c> (maps are chosen for layered files and no format is).
/// </param>
public sealed record ExportProblem(string Code, string Message);

/// <summary>What the work folder holds for an export: the tiles per map, the minimap dictionaries, the maps that can be written in layers.</summary>
public sealed record ExportInventory(IReadOnlyList<TileSetInfo> Maps, MinimapInfo Minimap, EditableInfo Editable)
{
    /// <param name="game">Where the minimap resource's parts from the game's files come from (null: this PC's game).</param>
    public static ExportInventory Of(Project project, StateStore state, MinimapGameFiles? game = null)
    {
        var folder = new WorkFolder(project.WorkFolderPath);
        var table = Planner.Build(project, state, project.File.Parallel, Environment.ProcessorCount);
        var rows = table.Rows.SelectMany(r => r.Children.Prepend(r)).ToDictionary(r => r.Id);
        int Left(string id) => rows.TryGetValue(id, out var r) && r.Needed ? r.Remaining : 0;
        var maps = new List<TileSetInfo>();
        foreach (var m in project.Maps)
        {
            var root = folder.Tiles(m.Id);
            if (!Directory.Exists(root)) continue;
            var perZoom = new int[WorldGrid.Zoom + 1];
            var bytesPerZoom = new long[WorldGrid.Zoom + 1];
            foreach (var zdir in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (!int.TryParse(zdir.Name, out var z) || z < 0 || z > WorldGrid.Zoom) continue;
                foreach (var f in zdir.EnumerateFiles("*.png", SearchOption.AllDirectories)) { perZoom[z]++; bytesPerZoom[z] += f.Length; }
            }
            if (perZoom.Sum() == 0) continue;
            // written with moved numbers, zooms 2 to 0 are made again over the frame: every place of theirs has a tile
            if (ExportGrid.Renumbers(project.Frame))
                for (int z = 0; z < ExportGrid.FirstKept; z++)
                {
                    var (columns, lines) = ExportGrid.Size(project.Frame, z);
                    perZoom[z] = columns * lines;
                }
            int left = Left("lowZoom." + m.Id) + (m.Kind == MapKind.Satellite ? Left("ortho") : Left("cells." + m.Id));
            maps.Add(new TileSetInfo(m.Id, perZoom.Sum(), bytesPerZoom.Sum(), Enumerable.Range(0, perZoom.Length).Where(z => perZoom[z] > 0).ToList(), left == 0,
                perZoom, bytesPerZoom));
        }
        var mm = project.File.Minimap.Map;
        IReadOnlyList<CellId> extra = mm is null ? [] : MinimapExtraTiles.Cells(project.Range.Keys);
        int made = 0;
        long mmBytes = 0;
        if (mm is not null)
        {
            var dir = folder.Minimap(mm);
            foreach (var s in MinimapSheets.All)
            {
                var files = new[] { s.SeaTexture, s.Texture }.Select(n => new FileInfo(Path.Combine(dir, n + ".ytd"))).ToList();
                if (files.All(f => f.Exists) && !MinimapStage.IsStale(project, state, mm, s)) made++;
                mmBytes += files.Where(f => f.Exists).Sum(f => f.Length);
            }
            // the small whole map counts as one more, and so does every texture beyond the standard frame
            var lod = new FileInfo(Path.Combine(dir, MinimapLod.Texture + ".ytd"));
            if (lod.Exists && !MinimapStage.IsLodStale(project, state, mm)) made++;
            if (lod.Exists) mmBytes += lod.Length;
            foreach (var cell in extra)
            {
                var f = new FileInfo(Path.Combine(dir, MinimapExtraTiles.Texture(cell) + ".ytd"));
                if (f.Exists && !MinimapStage.IsExtraStale(project, state, mm, cell)) made++;
                if (f.Exists) mmBytes += f.Length;
            }
        }
        bool island = mm is not null && project.File.CayoPerico;
        string? missing = mm is null ? null : (game ?? MinimapGameFiles.Of()).Missing;
        var frame = project.Frame;
        var editable = project.Maps.Select(m => new EditableMapInfo(m.Id, EditableReady(project, state, folder, m),
            maps.FirstOrDefault(t => t.Map == m.Id)?.UpToDate ?? false)).ToList();
        return new ExportInventory(maps, new MinimapInfo(mm, MinimapSheets.Size, made, mmBytes, extra.Count, island,
            island ? IslandMap.LandMissing(project.Range) : 0, missing), new EditableInfo(editable, frame.BlocksX, frame.BlocksY));
    }

    /// <summary>
    /// Whether a map can be written as a layered file: its lower zooms are made, and for an atlas or road map every cell
    /// of the range has its data and the drawing waits for nothing (the road shapes, the labels).
    /// </summary>
    static bool EditableReady(Project project, StateStore state, WorkFolder folder, MapSet map)
    {
        if (state.StageDone(StageKeys.LowZoom, map.Id) is null) return false;
        if (!map.IsCellMap) return true;
        try
        {
            if (Render.CellDrawStage.Waiting(project, state, map) is not null) return false;
        }
        catch (ProjectException) { return false; }             // the map's style cannot be read
        return CellPlan.For(project.Range.Keys).All(c => File.Exists(Path.Combine(Cells.CellFiles.Folder(folder, c.Id), Cells.CellFiles.Layers)));
    }

    /// <summary>Why the export cannot run as asked; empty = it can.</summary>
    public IReadOnlyList<ExportProblem> Check(ExportOptions o)
    {
        var p = new List<ExportProblem>();
        if (string.IsNullOrWhiteSpace(o.Folder)) p.Add(new("NO_FOLDER", "choose the output folder"));
        var layered = o.Editable?.Maps ?? [];
        if (o.Maps.Count == 0 && !o.Minimap && layered.Count == 0)
            p.Add(new("NOTHING", "choose the web tiles of a map, the minimap resource or a map to write in layers"));
        foreach (var m in layered)
            if (Editable.Maps.FirstOrDefault(e => e.Map == m) is not { Ready: true })
                p.Add(new("EDITABLE_NOT_READY", $"the {m} map cannot be written in layers yet: run the job list first"));
        if (o.Editable is { } editable)
        {
            if (!EditableChoice.IsZoom(editable.Zoom))
                p.Add(new("BAD_EDITABLE", $"the zoom level {editable.Zoom} of the editable files is not {EditableChoice.DefaultZoom} or {EditableChoice.FinestZoom}"));
            foreach (var format in editable.Written)
                if (!EditableChoice.AllFormats.Contains(format))
                    p.Add(new("BAD_EDITABLE", $"'{format}' is not a format of the editable files ({string.Join(", ", EditableChoice.AllFormats)})"));
            if (layered.Count > 0 && editable.Written.Count == 0)
                p.Add(new("NO_EDITABLE_FORMAT", $"choose a format for the editable files ({string.Join(", ", EditableChoice.AllFormats)})"));
        }
        foreach (var m in o.Maps)
            if (!Maps.Any(t => t.Map == m)) p.Add(new("NO_TILES", $"the {m} map has no tiles yet: run the job list first"));
        if (o.Minimap)
        {
            if (Minimap.Map is null) p.Add(new("NO_MINIMAP", "the project has no minimap map chosen"));
            else if (!Minimap.Ready)
                p.Add(new("MINIMAP_NOT_READY", $"{Minimap.Made} of {Minimap.Total} minimap textures (the sheets, the small whole map"
                    + (Minimap.Extra > 0 ? " and the textures beyond the standard map" : "") + ") are made: run the job list first"));
            if (MinimapGameFiles.Problem(Minimap.Game) is { } noGame) p.Add(noGame);
            if (o.ResourceName is not null && !ExportOptions.IsResourceName(o.ResourceName))
                p.Add(new("BAD_NAME", $"'{o.ResourceName}' is not a resource name (lower-case letters, digits, '-' and '_')"));
        }
        if (o.BaseUrl is not null && !Project.IsWebAddress(o.BaseUrl)) p.Add(new("BAD_URL", $"'{o.BaseUrl}' is not an http(s) address"));
        if (!ExportOptions.IsMaxZoom(o.MaxZoom)) p.Add(new("BAD_ZOOM", $"the maximum zoom {o.MaxZoom} is not {ExportOptions.LowestMaxZoom}-{WorldGrid.Zoom}"));
        if (!string.IsNullOrWhiteSpace(o.Folder) && Directory.Exists(o.Folder) && ExportRecord.Read(o.Folder) is null
            && Directory.EnumerateFileSystemEntries(o.Folder).Any())
            p.Add(new("NOT_EMPTY", $"{o.Folder} holds other files; choose an empty folder or one an earlier export wrote"));
        return p;
    }
}
