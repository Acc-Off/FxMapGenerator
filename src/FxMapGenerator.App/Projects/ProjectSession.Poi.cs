using System.Text;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.App.Projects;

/// <summary>What the POI screen starts from.</summary>
/// <param name="Folder">The project's POI folder as its file names it (null: the bundled points).</param>
/// <param name="StylesFile">The project's POI styles file as its file names it (null: none yet).</param>
/// <param name="Set">The points and the project's own POI styles to edit; null when the files cannot be read (<see cref="Problem"/>).</param>
/// <param name="BundledStyles">The bundled POI styles (read only).</param>
/// <param name="BundledGroups">The paths of the groups that come with the app (<c>data/poi/</c>).</param>
/// <param name="Fonts">The fonts the maps write the labels with (the first atlas style's labels), by language.</param>
/// <param name="Background">That style's background colour.</param>
public sealed record PoiEditorDto(string? Folder, string? StylesFile, PoiEditSet? Set, IReadOnlyList<PoiEditStyle> BundledStyles,
    IReadOnlyList<string> BundledGroups, IReadOnlyDictionary<string, string> Fonts, string Background, string? Problem);

/// <param name="Label">The point's label per language (the text the sample writes).</param>
/// <param name="Zoom">8, 7 or 6 (8 when left out).</param>
public sealed record PoiSampleRequest(PoiEditStyle? Style, Dictionary<string, string>? Label, string? Language, int? Zoom);

/// <param name="Name">The file's name (its extension tells a CSV from JSON).</param>
public sealed record PoiImportRequest(string? Name, string? Text);

/// <param name="Image">The PNG's path as a POI style names it (in the folder of the POI styles file).</param>
public sealed record PoiImageDto(string Image);

/// <param name="Name">The file's name without its extension (the new group's name).</param>
public sealed record PoiImportDto(IReadOnlyList<PoiEditPoint> Points, IReadOnlyList<PoiImport.Skipped> Skipped, string Name);

/// <summary>The points of interest of the open project, as the POI screen edits them.</summary>
public sealed partial class ProjectSession
{
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>The project's points of interest and POI styles to edit, or why they cannot be read.</summary>
    public PoiEditorDto Poi() => PoiOf(Require());

    static PoiEditorDto PoiOf(Project project)
    {
        var map = PoiMapStyle(project);
        var fonts = new Dictionary<string, string> { ["en"] = map.Labels?.Font("en") ?? "Bahnschrift", ["ja"] = map.Labels?.Font("ja") ?? "Bahnschrift" };
        var bundledStyles = PoiFiles.ReadStyles(PoiData.BundledStylesText);
        var bundledGroups = PoiData.BundledPoints.Select(p => p.Path[..^".json".Length]).ToList();
        try
        {
            PoiData.Of(project);
            var files = project.PoiFolderPath is { } folder ? PoiData.ReadFolder(folder) : PoiData.BundledPoints.ToList();
            var styles = project.PoiStylesPath is { } s ? File.ReadAllText(s) : null;
            return new PoiEditorDto(project.File.Poi, project.File.PoiStyles, PoiFiles.Read(files, styles), bundledStyles, bundledGroups, fonts,
                map.Background.ToString(), null);
        }
        catch (PoiException ex)
        {
            return new PoiEditorDto(project.File.Poi, project.File.PoiStyles, null, bundledStyles, bundledGroups, fonts, map.Background.ToString(), ex.Message);
        }
    }

    /// <summary>The style the POI screen draws with: the project's first atlas map's, else the PostalCodeMap style.</summary>
    static MapStyle PoiMapStyle(Project project)
    {
        var atlas = project.Maps.FirstOrDefault(m => m.Kind == MapKind.Atlas);
        try { return atlas is null ? MapStyle.Builtin(AtlasPresets.PostalCodeMap) : project.StyleOf(atlas); }
        catch (ProjectException) { return MapStyle.Builtin(AtlasPresets.PostalCodeMap); }
    }

    /// <summary>
    /// Saves the points of interest and the project's own POI styles: into the project's POI folder and styles file, or,
    /// when it has none yet, into new ones beside the project file (<c>poi/</c> starting from the bundled files, and
    /// <c>poi-styles.json</c>; another name when taken) which the project then names. A folder is made only when the points
    /// differ from the bundled ones, a styles file only when there are styles of its own. The names in English and
    /// Japanese that a new folder or styles file copies (the bundled groups') take their name in <paramref name="language"/>
    /// (the screens'). Only files that say something else are written. <see cref="ProjectException"/> INVALID with every
    /// problem (nothing written); <see cref="JobException"/> LOCKED while a running stage of this project reads the points.
    /// </summary>
    public PoiEditorDto SavePoi(PoiEditSet set, string language = "en")
    {
        Project project;
        lock (_sync)
        {
            project = Load();
            Check(project, InputKeys.Poi);
            var problems = PoiFiles.PathProblems(set);
            if (problems.Count > 0) throw new ProjectException("INVALID", string.Join("; ", problems));

            bool newFolder = project.PoiFolderPath is null;
            string folder = project.PoiFolderPath ?? Path.Combine(project.Folder, PoiFiles.FreeName(project.Folder, PoiFiles.FolderName));
            string? stylesPath = project.PoiStylesPath
                ?? (set.Styles.Count > 0 ? Path.Combine(project.Folder, PoiFiles.FreeName(project.Folder, PoiFiles.StylesName)) : null);
            var bundled = PoiFiles.Files(PoiFiles.Read(PoiData.BundledPoints, null), []);
            // the copy the project keeps from now on names the bundled names' in the screens' language
            bool sameAsBundled = newFolder && PoiFiles.Files(set, []).SequenceEqual(bundled);
            var copied = PoiFiles.Copied(set, language);
            if (newFolder) set = set with { Folders = copied.Folders, Groups = copied.Groups };
            if (project.PoiStylesPath is null) set = set with { Styles = copied.Styles };
            var files = PoiFiles.Files(set, !newFolder && Directory.Exists(folder) ? PoiData.ReadFolder(folder).Select(e => e.Path) : []);
            var stylesText = stylesPath is null ? null : PoiFiles.StylesText(set.Styles);
            // read as the maps will read them, before anything is written
            try { PoiData.Load(files.Select(kv => (kv.Key, kv.Value)), folder, stylesText, stylesPath ?? "", stylesPath is null ? null : Path.GetDirectoryName(stylesPath)); }
            catch (PoiException ex) { throw new ProjectException("INVALID", ex.Message); }

            bool projectChanged = false;
            if (!sameAsBundled)
            {
                PoiFiles.WriteFolder(folder, files);
                if (newFolder)
                {
                    project.File.Poi = Path.GetFileName(folder);
                    projectChanged = true;
                }
            }
            if (stylesPath is not null && stylesText is not null)
            {
                var now = File.Exists(stylesPath) ? File.ReadAllText(stylesPath) : null;
                if (now is null || PoiFiles.StylesText(PoiFiles.ReadStyles(now)) != stylesText) File.WriteAllText(stylesPath, stylesText, Utf8NoBom);
                if (project.File.PoiStyles is null)
                {
                    project.File.PoiStyles = Path.GetFileName(stylesPath);
                    projectChanged = true;
                }
            }
            if (projectChanged) project.Save();
            _project = project;
        }
        // the project screen shows what the maps now need made again
        Publish(project);
        return PoiOf(project);
    }

    /// <summary>
    /// Keeps a PNG (a file chosen on the screen, <paramref name="name"/> its name) in the <c>poi-icons/</c> folder beside
    /// the project's POI styles file (beside the project file when it has none yet, where that file will be made), under
    /// its own name (another when a different picture has it). <see cref="ProjectException"/> INVALID when it is not a PNG.
    /// </summary>
    public PoiImageDto CopyPoiImage(byte[] bytes, string? name)
    {
        var project = Require();
        using (var codec = SkiaSharp.SKCodec.Create(new MemoryStream(bytes)))
            if (codec is null || codec.EncodedFormat != SkiaSharp.SKEncodedImageFormat.Png) throw new ProjectException("INVALID", $"{name} is not a PNG");
        var stylesFolder = project.PoiStylesPath is { } s ? Path.GetDirectoryName(s)! : project.Folder;
        var folder = Path.Combine(stylesFolder, PoiFiles.ImagesFolder);
        Directory.CreateDirectory(folder);
        var stem = PoiFiles.SafeName(Path.GetFileNameWithoutExtension(name ?? "icon"));
        var file = stem + ".png";
        for (int i = 2; File.Exists(Path.Combine(folder, file)) && !File.ReadAllBytes(Path.Combine(folder, file)).AsSpan().SequenceEqual(bytes); i++)
            file = $"{stem}-{i}.png";
        var target = Path.Combine(folder, file);
        if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
        return new PoiImageDto(PoiFiles.ImagesFolder + "/" + file);
    }

    /// <summary>
    /// A PNG a POI style names (a path in the folder of the project's POI styles file, or of the project file when it has
    /// none yet), for the screen to show. <see cref="ProjectException"/> INVALID (a path out of the folder), NOT_FOUND.
    /// </summary>
    public byte[] PoiImage(string? path)
    {
        var project = Require();
        var p = (path ?? "").Replace('\\', '/');
        if (!PoiFiles.SafeRelative(p) || !p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ProjectException("INVALID", $"not a PNG in the folder of the POI styles: {path}");
        var stylesFolder = project.PoiStylesPath is { } s ? Path.GetDirectoryName(s)! : project.Folder;
        var full = Path.Combine([stylesFolder, .. p.Split('/')]);
        return File.Exists(full) ? File.ReadAllBytes(full) : throw new ProjectException("NOT_FOUND", $"image not found: {path}");
    }

    /// <summary>Points read from a CSV or JSON file chosen on the screen, for a new group.</summary>
    public PoiImportDto ImportPoi(PoiImportRequest request)
    {
        var project = Require();
        var name = request.Name ?? "";
        var r = PoiImport.Read(request.Text ?? "", Path.GetExtension(name).Equals(".csv", StringComparison.OrdinalIgnoreCase), project.Frame);
        return new PoiImportDto(r.Points, r.Skipped, Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>
    /// A POI style drawn as the maps draw it, as a PNG: the project's style if it names a PNG of the project, the point's
    /// label in a language. <see cref="ProjectException"/> INVALID when the style cannot be drawn (every reason).
    /// </summary>
    public byte[] PoiSample(PoiSampleRequest request)
    {
        var project = Require();
        if (request.Style is not { } edit) throw new ProjectException("INVALID", "no style");
        var stylesFolder = project.PoiStylesPath is { } s ? Path.GetDirectoryName(s)! : project.Folder;
        PoiStyle style;
        try
        {
            // read as a styles file of the project (a PNG from its folder); an id of a bundled style is fine here
            var sample = edit with { Id = "sample" };
            style = PoiData.Load([], "sample", PoiFiles.StylesText([sample]), "the style", stylesFolder).Styles.Single(x => x.Id == "sample");
        }
        catch (PoiException ex) { throw new ProjectException("INVALID", ex.Message); }
        var label = request.Label is { Count: > 0 } n ? n : new Dictionary<string, string> { ["en"] = "Label" };
        int zoom = request.Zoom is 6 or 7 ? request.Zoom.Value : 8;
        return FxMapGenerator.Core.Poi.PoiSample.Png(style, label, request.Language is "ja" ? "ja" : "en", PoiMapStyle(project), zoom);
    }
}
