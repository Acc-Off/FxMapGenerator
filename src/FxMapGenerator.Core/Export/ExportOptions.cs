using System.Globalization;
using System.Text.RegularExpressions;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Export;

/// <summary>What one export writes.</summary>
/// <param name="Folder">The output folder (made when missing; an earlier export there is written over).</param>
/// <param name="Maps">The maps whose web tiles are written (none = no web tiles).</param>
/// <param name="Zip">The web tiles, viewer and lb-phone example as <c>web.zip</c> instead of the folder <c>web/</c>.</param>
/// <param name="BaseUrl">Where the web tiles will be served from, for the lb-phone example; null = a placeholder.</param>
/// <param name="Minimap">Write the minimap resource.</param>
/// <param name="ResourceName">The minimap resource's name (with its version); null = <see cref="DefaultResourceName"/>.</param>
/// <param name="MaxZoom">
/// The finest zoom of the web tiles, <see cref="LowestMaxZoom"/>..8: fewer levels are far fewer files (8: 32,769 for a
/// whole map, 7: 8,193, 6: 2,049), for hosts that limit the files of a site; viewers enlarge the last level.
/// </param>
/// <param name="Editable">The layered files for editing in a paint program; null = none.</param>
public sealed partial record ExportOptions(string Folder, IReadOnlyList<string> Maps, bool Zip, string? BaseUrl, bool Minimap, string? ResourceName = null,
    int MaxZoom = WorldGrid.Zoom, EditableChoice? Editable = null)
{
    public const string ResourcePrefix = "fxmapgen-minimap-";
    /// <summary>The coarsest choice of <see cref="MaxZoom"/> (a pixel of 1.1 m).</summary>
    public const int LowestMaxZoom = 6;

    public static bool IsMaxZoom(int z) => z is >= LowestMaxZoom and <= WorldGrid.Zoom;

    /// <summary>
    /// <c>fxmapgen-minimap-&lt;date&gt;</c>, with <c>-2</c>, <c>-3</c>, ... when the folder already holds one of that name:
    /// FiveM keeps streamed files of a resource name in its caches, so a new picture needs a new name.
    /// </summary>
    public static string DefaultResourceName(string folder, DateTime now)
    {
        var name = ResourcePrefix + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var pick = name;
        for (int n = 2; Directory.Exists(Path.Combine(folder, pick)); n++) pick = $"{name}-{n}";
        return pick;
    }

    /// <summary>Lower-case letters, digits, '-' and '_' (a resource name that works in every server.cfg).</summary>
    public static bool IsResourceName(string? s) => !string.IsNullOrEmpty(s) && s.Length <= 64 && ResourceNameRegex().IsMatch(s);

    /// <summary>
    /// The options from the project's saved export choices (the maps: those with tiles when none are saved). The layered
    /// files are never among them: they are written only when asked for (their zoom and formats are kept for that).
    /// </summary>
    public static ExportOptions FromProject(Project project, IEnumerable<string> mapsWithTiles)
    {
        var e = project.File.Export;
        var maps = !e.Tiles ? new List<string>() : e.Maps is { } chosen ? chosen.Where(mapsWithTiles.Contains).ToList() : mapsWithTiles.ToList();
        return new ExportOptions(DefaultFolder(project), maps, e.Zip, string.IsNullOrWhiteSpace(e.BaseUrl) ? null : e.BaseUrl.Trim(), e.Minimap && project.File.Minimap.Map is not null,
            MaxZoom: e.MaxZoom);
    }

    /// <summary>The saved folder (relative to the project file's folder), else <c>export</c> next to the project file.</summary>
    public static string DefaultFolder(Project project) =>
        Path.GetFullPath(Path.Combine(project.Folder, string.IsNullOrWhiteSpace(project.File.Export.Folder) ? "export" : project.File.Export.Folder));

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex ResourceNameRegex();
}

/// <summary>
/// The layered files of an export: a map as one picture of the whole frame, its drawing split into layers
/// (<see cref="Render.MapLayer"/>), for editing in a paint or drawing program (<see cref="EditableLayersStage"/>,
/// <see cref="EditableFilesStage"/>). They go into the folder <see cref="Folder"/> of the export folder, a file a map
/// and format: <c>&lt;map&gt;-z&lt;zoom&gt;.psd</c> (<c>.psb</c> when the picture passes what a PSD file holds), every
/// layer a picture; <c>&lt;map&gt;-z&lt;zoom&gt;.svg</c> (<see cref="SvgFile"/>), the roads and the labels as shapes and
/// text and the other layers as PNG pictures in the folder <c>&lt;map&gt;-z&lt;zoom&gt;-svg</c> beside it.
/// </summary>
/// <param name="Maps">The maps written (none = no such files).</param>
/// <param name="Zoom">The zoom level of the pictures: 6 (a pixel of 1.1 m, the minimap's own) or 7.</param>
/// <param name="Formats">The file formats (<see cref="Psd"/>, <see cref="Svg"/>); null = <see cref="Psd"/>; empty = none chosen.</param>
/// <param name="Language">The language of the layers' names: <c>en</c> or <c>ja</c>.</param>
public sealed record EditableChoice(IReadOnlyList<string> Maps, int Zoom = EditableChoice.DefaultZoom, IReadOnlyList<string>? Formats = null, string Language = "en")
{
    public const string Folder = "editable";
    public const int DefaultZoom = 6, FinestZoom = 7;
    public const string Psd = "psd", Svg = "svg";
    public static readonly IReadOnlyList<string> AllFormats = [Psd, Svg];
    /// <summary>The name of a satellite map's one picture among an SVG file's pictures.</summary>
    public const string SatellitePicture = "satellite";

    public static bool IsZoom(int z) => z is >= DefaultZoom and <= FinestZoom;

    /// <summary>The formats written: the ones chosen (each once), <see cref="Psd"/> when none is said.</summary>
    public IReadOnlyList<string> Written => Formats is null ? [Psd] : Formats.Distinct().ToList();

    /// <summary>The pixels of a block's side at a zoom level (256 at zoom 6, 512 at zoom 7).</summary>
    public static int BlockPx(int zoom) => Satellite.TileStore.TileSize << (zoom - DefaultZoom);

    /// <summary>A map's file name without its extension: <c>atlas-postalcodemap-z6</c>.</summary>
    public static string FileName(MapSet map, int zoom) => string.Create(CultureInfo.InvariantCulture, $"{map.ExportName}-z{zoom}");

    /// <summary>The folder of the pictures a map's SVG file links, beside the file: <c>atlas-postalcodemap-z6-svg</c>.</summary>
    public static string PicturesFolder(MapSet map, int zoom) => FileName(map, zoom) + "-svg";

    /// <summary>The name of a layer's picture among an SVG file's pictures (without <c>.png</c>): the layer's English name in small letters.</summary>
    public static string PictureName(Render.MapLayer layer) => layer switch
    {
        Render.MapLayer.Ground => "ground",
        Render.MapLayer.ShadeDark => "shading-dark",
        Render.MapLayer.ShadeLight => "shading-light",
        Render.MapLayer.Canopy => "tree-canopy",
        Render.MapLayer.Water => "water",
        Render.MapLayer.Buildings => "buildings",
        Render.MapLayer.Contours => "contours",
        Render.MapLayer.Rail => "railway",
        _ => throw new ArgumentException($"the {layer} layer is not a picture of an SVG file"),
    };
}
