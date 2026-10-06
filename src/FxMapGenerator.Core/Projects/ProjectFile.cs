namespace FxMapGenerator.Core.Projects;

/// <summary>
/// Contents of <c>&lt;name&gt;.fxmapgen.json</c>: the settings of one server's map project. Readable JSON; paths are
/// relative to the project file's folder unless absolute. What belongs to the PC rather than the server (the GTA V and
/// key folders, the language of the screens) is in the app's settings.
/// </summary>
public sealed class ProjectFile
{
    public const int CurrentFormat = 1;
    public const string Extension = ".fxmapgen.json";

    public int Format { get; set; } = CurrentFormat;
    public string Name { get; set; } = "";
    /// <summary>Captures, scans, intermediate data and tiles. Default: the project file's folder.</summary>
    public string WorkFolder { get; set; } = ".";
    public MapsSetting Maps { get; set; } = new();
    public MinimapSetting Minimap { get; set; } = new();
    public RangeSetting Range { get; set; } = new();
    public ServerSetting Server { get; set; } = new();
    public ConsoleSetting Console { get; set; } = new();
    public GameFilesSetting GameFiles { get; set; } = new();
    /// <summary>
    /// The server runs the Cayo Perico island: the game files step reads the island's road files (the game's path node
    /// files numbered 1024 above the areas they replace) in place of the areas' own, as the game does near the island,
    /// the labels place the name of the island's zone (<see cref="Labels.LabelsStage.UnnamedZones"/>), and the minimap
    /// resource carries the island map (<see cref="Export.IslandMap"/>).
    /// </summary>
    public bool CayoPerico { get; set; }
    /// <summary>
    /// The height quality (what the visit takes for the surface heights): <c>speed</c> (the height data only; the satellite
    /// map alone), <c>balance</c> (the ground scan only), <c>quality</c> (both, per point the higher, for the photos, the
    /// landcover and the cells' shade and contours); null = the default for the maps (speed for the satellite map alone,
    /// quality with an atlas or road map, also without the satellite map). See <see cref="Satellite.SurfaceHeights"/>.
    /// </summary>
    public string? HeightQuality { get; set; }
    /// <summary>
    /// The road edits: a file of the nodes and links the map's roads add to, move, change and hide in the game's path
    /// data (<see cref="RoadEdits.RoadEditsFile"/>); null = none.
    /// </summary>
    public string? RoadEdits { get; set; }
    /// <summary>
    /// The folder of the project's own styles (a file per style, <see cref="Styles.UserStyleFile"/>); null = none. The first
    /// style made makes <c>styles</c> beside the project file.
    /// </summary>
    public string? Styles { get; set; }
    /// <summary>The places the style editor's preview adds to its recommended ones (a name and a middle, m); null = none.</summary>
    public List<PreviewPlace>? PreviewPlaces { get; set; }
    /// <summary>
    /// The postal codes of the atlas maps: a file or an http(s) address of a list in the nearest-postal form
    /// (<c>[{"code", "x", "y"}, ...]</c>, an optional <c>"size"</c>); null = nearest-postal's table for the postal code map.
    /// </summary>
    public string? Postals { get; set; }
    /// <summary>
    /// The points of interest: a folder whose folders are POI folders (settings in <c>_.json</c>) and whose other
    /// <c>.json</c> files are groups of points (<c>{"name", ..., "points": [...]}</c>); null = the bundled markers and dots.
    /// </summary>
    public string? Poi { get; set; }
    /// <summary>The project's POI styles (<c>{"styles": [...]}</c>), added to the bundled ones; null = the bundled ones only.</summary>
    public string? PoiStyles { get; set; }
    /// <summary>Workers of new runs for the stages without the game (limited to the PC's logical processors when used).</summary>
    public int Parallel { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    /// <summary>The choices of the last export (the export screen starts from them).</summary>
    public ExportSetting Export { get; set; } = new();
}

/// <summary>The three kinds of maps. The atlas keeps its style list while it is switched off.</summary>
public sealed class MapsSetting
{
    public bool Satellite { get; set; } = true;
    public AtlasSetting Atlas { get; set; } = new();
    public bool Roadmap { get; set; }
}

/// <summary>The atlas maps: every style of the list in every language of the list (a map per style and language).</summary>
public sealed class AtlasSetting
{
    public bool Enabled { get; set; }
    /// <summary>The styles of the atlas maps (a bundled style's id or one of the project's). Default: the postal code map look.</summary>
    public List<string> Styles { get; set; } = new() { AtlasPresets.PostalCodeMap };
    /// <summary>The languages of the maps' labels: English always (first), Japanese when listed (<see cref="AtlasPresets.Languages"/>).</summary>
    public List<string> Languages { get; set; } = new() { "en" };
}

/// <summary>A place of the style editor's preview kept in the project: its name and the middle of its window (game metres).</summary>
public sealed class PreviewPlace
{
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class MinimapSetting
{
    /// <summary>The map put into the in-game minimap (a map id such as <c>satellite</c>); null = none.</summary>
    public string? Map { get; set; }
    /// <summary>
    /// The blocks outside the range: <c>map</c> (painted as the map's tiles have them: an atlas or road map's open sea, the
    /// deepest colour of its style's sea; the satellite map's open sea measured from the capture) or <c>transparent</c>
    /// (left see-through: the game's pause map shows the game behind them).
    /// </summary>
    public string Outside { get; set; } = MinimapOutside.Map;
}

public static class MinimapOutside
{
    public const string Map = "map", Transparent = "transparent";

    public static bool IsValid(string? s) => s is Map or Transparent;
}

public sealed class RangeSetting
{
    /// <summary><c>default</c> (the bundled 1,045 blocks) or <c>none</c>.</summary>
    public string Base { get; set; } = "default";
    /// <summary>Blocks added to the base (<c>z8_&lt;tx&gt;_&lt;ty&gt;</c>).</summary>
    public List<string> Add { get; set; } = new();
    /// <summary>Blocks taken out of the base.</summary>
    public List<string> Remove { get; set; } = new();
    /// <summary>
    /// Areas outside the standard map: whole cells (2250 m) added above, below, left and right of it (at most 2 above and
    /// below together, 4 left and right together); null = the standard map only. The blocks added above or to the left
    /// have negative numbers (the numbers keep the standard map's north-west corner as their origin).
    /// </summary>
    public ExtraCellsSetting? ExtraCells { get; set; }
}

/// <summary>The cells added on each side of the standard map (<see cref="RangeSetting.ExtraCells"/>).</summary>
public sealed class ExtraCellsSetting
{
    public int Top { get; set; }
    public int Bottom { get; set; }
    public int Left { get; set; }
    public int Right { get; set; }
}

public sealed class ServerSetting
{
    /// <summary>Server preset, one of the bundled <c>data/presets/</c>: <c>qbox</c> or <c>qbcore</c>.</summary>
    public string Preset { get; set; } = "qbox";
    /// <summary>Resources stopped on the game client during the visit; null = the preset's list.</summary>
    public List<string>? StopResources { get; set; }
}

public sealed class ConsoleSetting
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 29200;
}

public sealed class GameFilesSetting
{
    /// <summary>
    /// Where the path node files the server streams itself are found: resources folders (or copies), pack zips or single
    /// ynd files, in the order the server ensures them. A later entry wins over an earlier one for the same area (inside a
    /// folder, the files in the dictionary order of their folder names, a later one winning).
    /// </summary>
    public List<string> ServerResources { get; set; } = new();
}

public sealed class ExportSetting
{
    /// <summary>Where the export goes; null = <c>export</c> next to the project file.</summary>
    public string? Folder { get; set; }
    /// <summary>Write the web tiles (with the viewer and the lb-phone example).</summary>
    public bool Tiles { get; set; } = true;
    /// <summary>The maps whose tiles are written; null = every map of the project that has tiles.</summary>
    public List<string>? Maps { get; set; }
    /// <summary>The web tiles as one zip file instead of a folder.</summary>
    public bool Zip { get; set; }
    /// <summary>Where the web tiles will be served from (for the lb-phone example), e.g. <c>https://example.pages.dev</c>.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>The finest zoom of the web tiles: 8 (all), 7 or 6 (far fewer files, for hosts that limit them).</summary>
    public int MaxZoom { get; set; } = World.WorldGrid.Zoom;
    /// <summary>Write the minimap resource (when the project has a minimap).</summary>
    public bool Minimap { get; set; } = true;
    /// <summary>The layered files for editing (<see cref="Export.EditableChoice"/>): how they were last written.</summary>
    public EditableSetting Editable { get; set; } = new();
    /// <summary>The edited picture last converted (<see cref="Export.ConvertOptions"/>).</summary>
    public PictureSetting Picture { get; set; } = new();
}

/// <summary>
/// The edited picture of the last conversion: where it is and the map it was made from. The conversion itself is never
/// among the saved choices: it runs only when asked for.
/// </summary>
public sealed class PictureSetting
{
    /// <summary>The PNG file; null = none yet.</summary>
    public string? File { get; set; }
    /// <summary>The id of the map the picture was made from; null = none chosen yet.</summary>
    public string? Map { get; set; }
}

/// <summary>
/// How the layered files of an export were last written. Which maps is not kept: they are written only when an export
/// asks for them.
/// </summary>
public sealed class EditableSetting
{
    /// <summary>The zoom level of the pictures: 6 (a pixel of 1.1 m, the minimap's own) or 7.</summary>
    public int Zoom { get; set; } = Export.EditableChoice.DefaultZoom;
    /// <summary>The file formats: <c>psd</c>, <c>svg</c>.</summary>
    public List<string> Formats { get; set; } = new() { Export.EditableChoice.Psd };
}
