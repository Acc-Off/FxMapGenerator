using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Export;

/// <summary>
/// What one conversion of an edited picture writes: a map's layered file (<see cref="EditableChoice"/>) edited in a paint
/// program and written out as one PNG picture of the whole frame becomes web tiles and a minimap resource of its own,
/// without the project taking the picture in (<see cref="ConvertStages"/>).
/// </summary>
/// <param name="Folder">The output folder, an export folder (made when missing).</param>
/// <param name="Picture">The PNG file: the project's whole frame at zoom 6 or 7 (<see cref="EditedPicture.Size"/>).</param>
/// <param name="Map">
/// The id of the map the picture was made from. The output has that map's names (the tile folder, the title), the colour
/// of its open sea (behind the web tiles, under the minimap's small whole map) and its credits.
/// </param>
/// <param name="Tiles">Write the web tiles, with the viewer and the lb-phone example, into <see cref="WebFolder"/>.</param>
/// <param name="Minimap">Write a minimap resource whose pictures are the picture's.</param>
/// <param name="ResourceName">The minimap resource's name; null = <see cref="ExportOptions.DefaultResourceName"/>.</param>
/// <param name="BaseUrl">Where the web tiles will be served from, for the lb-phone example; null = a placeholder.</param>
public sealed record ConvertOptions(string Folder, string Picture, string Map, bool Tiles, bool Minimap, string? ResourceName = null, string? BaseUrl = null)
{
    /// <summary>
    /// The folder of an export folder that holds the web tiles of edited pictures: <c>web-edited/&lt;name&gt;/</c>, a web
    /// part of its own (as <c>web/</c>: the viewer, <c>tiles/&lt;name&gt;/</c>, the lb-phone example, a README, the
    /// credits), &lt;name&gt; being the tile folder's name of the picture's map (<see cref="MapSet.ExportName"/>).
    /// </summary>
    public const string WebFolder = "web-edited";
}

/// <summary>What a file is as an edited picture of a project (<see cref="EditedPicture.Look"/>).</summary>
/// <param name="File">The file's full path.</param>
/// <param name="Width">Its pixels across; 0 when it could not be read.</param>
/// <param name="Zoom">The zoom level its size is the project's frame at (6 or 7); null when its size is neither.</param>
/// <param name="Problem">
/// Why it cannot be converted: <c>NOT_FOUND</c>, <c>NOT_PNG</c>, <c>INTERLACED</c>, <c>BAD_SIZE</c>; null = it can.
/// </param>
public sealed record PictureInfo(string File, int Width, int Height, int? Zoom, string? Problem);

/// <summary>An edited picture: a PNG file of a project's whole frame at the zoom level of a layered file.</summary>
public static class EditedPicture
{
    public const string NotFound = "NOT_FOUND", BadSize = "BAD_SIZE";

    /// <summary>The pixels of a frame at a zoom level of the layered files (the standard frame: 8,192 x 12,288 at zoom 6).</summary>
    public static (int Width, int Height) Size(MapFrame frame, int zoom) =>
        (frame.BlocksX * EditableChoice.BlockPx(zoom), frame.BlocksY * EditableChoice.BlockPx(zoom));

    /// <summary>The zoom level a picture of that size is the frame at; null when it is the frame at neither.</summary>
    public static int? ZoomOf(MapFrame frame, int width, int height)
    {
        for (int z = EditableChoice.DefaultZoom; z <= EditableChoice.FinestZoom; z++)
            if (Size(frame, z) == (width, height)) return z;
        return null;
    }

    /// <summary>Looks at a file's header: its size, the zoom level it stands for, and why it cannot be converted.</summary>
    public static PictureInfo Look(string path, MapFrame frame)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) return new PictureInfo(path, 0, 0, null, NotFound);
        try
        {
            var (width, height) = PngRows.Size(path);
            var zoom = ZoomOf(frame, width, height);
            return new PictureInfo(path, width, height, zoom, zoom is null ? BadSize : null);
        }
        catch (PngException ex) { return new PictureInfo(path, 0, 0, null, ex.Code); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new PictureInfo(path, 0, 0, null, NotFound); }
    }

    /// <summary>
    /// The map of the project a picture's file name tells: the one whose layered file's name it starts with
    /// (<c>&lt;map&gt;-z&lt;zoom&gt;</c>, <see cref="EditableChoice.FileName"/>: a paint program offers the name of the file it
    /// opened for the picture it writes). Null when the name tells none.
    /// </summary>
    public static MapSet? MapOf(Project project, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var map in project.Maps)
            for (int z = EditableChoice.DefaultZoom; z <= EditableChoice.FinestZoom; z++)
                if (name.StartsWith(EditableChoice.FileName(map, z), StringComparison.OrdinalIgnoreCase)) return map;
        return null;
    }

    /// <summary>
    /// The map taken for a picture when none is named: the one its file name tells (<see cref="MapOf"/>), else the
    /// minimap's map, else the first of the project's maps; null when the project makes no map.
    /// </summary>
    public static string? DefaultMap(Project project, string? path) =>
        (string.IsNullOrWhiteSpace(path) ? null : MapOf(project, path)?.Id)
        ?? (project.File.Minimap.Map is { } minimap && project.Maps.Any(m => m.Id == minimap) ? minimap : null)
        ?? project.Maps.FirstOrDefault()?.Id;

    /// <summary>The problem of a picture in a sentence (for logs and the command line).</summary>
    public static string Sentence(PictureInfo picture, MapFrame frame)
    {
        var (w6, h6) = Size(frame, EditableChoice.DefaultZoom);
        var (w7, h7) = Size(frame, EditableChoice.FinestZoom);
        return picture.Problem switch
        {
            null => $"{picture.File}: {picture.Width} x {picture.Height} px (zoom {picture.Zoom})",
            NotFound => $"the picture {picture.File} is not there",
            PngException.Interlaced => $"the picture {picture.File} is an interlaced PNG: write it without interlacing",
            BadSize => $"the picture {picture.File} is {picture.Width} x {picture.Height} px: the map takes {w6} x {h6} px (zoom 6) or {w7} x {h7} px (zoom 7)",
            _ => $"the picture {picture.File} cannot be read as a PNG picture",
        };
    }
}

/// <summary>Whether a conversion can run as asked, and its steps.</summary>
public static class ConvertStages
{
    /// <summary>
    /// Why the conversion cannot run as asked; empty = it can. Codes (as an export's, <see cref="ExportProblem"/>):
    /// <c>NO_FOLDER</c>, <c>NOT_EMPTY</c> (the folder holds other files), <c>NOTHING</c> (neither output chosen),
    /// <c>BAD_MAP</c> (not one of the project's maps), <c>BAD_PICTURE</c> (see <see cref="PictureInfo.Problem"/>),
    /// <c>MINIMAP_NO_GTA</c> / <c>MINIMAP_NO_KEYS</c> (what the minimap resource takes from the game's files cannot be
    /// read from this PC's game), <c>BAD_NAME</c> (resource name), <c>BAD_URL</c>.
    /// </summary>
    /// <param name="picture">The picture as looked at (<see cref="EditedPicture.Look"/>); null: looked at here.</param>
    /// <param name="game">Where the minimap resource's parts from the game's files come from (null: this PC's game).</param>
    public static IReadOnlyList<ExportProblem> Check(Project project, ConvertOptions o, PictureInfo? picture = null, MinimapGameFiles? game = null)
    {
        var p = new List<ExportProblem>();
        if (string.IsNullOrWhiteSpace(o.Folder)) p.Add(new("NO_FOLDER", "choose the output folder"));
        if (!o.Tiles && !o.Minimap) p.Add(new("NOTHING", "choose the web tiles or the minimap resource"));
        if (!project.Maps.Any(m => m.Id == o.Map)) p.Add(new("BAD_MAP", $"'{o.Map}' is not one of the project's maps (the map the picture was made from)"));
        if (string.IsNullOrWhiteSpace(o.Picture)) p.Add(new("BAD_PICTURE", "choose the picture"));
        else
        {
            picture ??= EditedPicture.Look(o.Picture, project.Frame);
            if (picture.Problem is not null) p.Add(new("BAD_PICTURE", EditedPicture.Sentence(picture, project.Frame)));
        }
        if (o.Minimap)
        {
            if (MinimapGameFiles.Problem((game ?? MinimapGameFiles.Of()).Missing) is { } noGame) p.Add(noGame);
            if (o.ResourceName is not null && !ExportOptions.IsResourceName(o.ResourceName))
                p.Add(new("BAD_NAME", $"'{o.ResourceName}' is not a resource name (lower-case letters, digits, '-' and '_')"));
        }
        if (o.BaseUrl is not null && !Project.IsWebAddress(o.BaseUrl)) p.Add(new("BAD_URL", $"'{o.BaseUrl}' is not an http(s) address"));
        if (!string.IsNullOrWhiteSpace(o.Folder) && Directory.Exists(o.Folder) && ExportRecord.Read(o.Folder) is null
            && Directory.EnumerateFileSystemEntries(o.Folder).Any())
            p.Add(new("NOT_EMPTY", $"{o.Folder} holds other files; choose an empty folder or one an earlier export wrote"));
        return p;
    }

    /// <summary>
    /// The steps of a conversion, in order: <see cref="ConvertTilesStage"/> (the picture cut into tiles, the lower zooms),
    /// <see cref="ConvertTexturesStage"/> when the minimap resource is written (the minimap's texture dictionaries from
    /// those tiles), <see cref="ConvertFilesStage"/> (the web tiles' folder, the resource, the folder's record).
    /// </summary>
    /// <param name="game">Where the minimap resource's parts from the game's files come from (null: this PC's game).</param>
    public static IReadOnlyList<Stage> For(ConvertOptions options, string version, MinimapGameFiles? game = null)
    {
        var stages = new List<Stage> { new ConvertTilesStage(options, game) };
        if (options.Minimap) stages.Add(new ConvertTexturesStage(options));
        stages.Add(new ConvertFilesStage(options, version, game));
        return stages;
    }
}

/// <summary>Where a conversion makes its tiles and textures: <c>.convert/</c> in the output folder, gone when it is over.</summary>
static class ConvertWork
{
    /// <summary>A file there that says the picture's tiles are all made, with the picture's zoom level in it.</summary>
    public const string Made = "made";

    public static string Root(string folder) => Path.Combine(folder, ".convert");

    /// <summary>The picture's tiles (<c>{z}/{x}/{y}.png</c>, numbered as a work folder's).</summary>
    public static string Tiles(string folder) => Path.Combine(Root(folder), "tiles");

    /// <summary>The minimap's texture dictionaries made from them.</summary>
    public static string Textures(string folder) => Path.Combine(Root(folder), "ytd");

    /// <summary>The zoom level of the tiles made, or null when they are not all made.</summary>
    public static int? MadeZoom(string folder)
    {
        var path = Path.Combine(Root(folder), Made);
        return File.Exists(path) && int.TryParse(File.ReadAllText(path), out int zoom) ? zoom : null;
    }

    public static void Remove(string folder)
    {
        var root = Root(folder);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
