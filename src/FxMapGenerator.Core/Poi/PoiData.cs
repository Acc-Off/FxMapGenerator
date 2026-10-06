using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;
using SkiaSharp;

namespace FxMapGenerator.Core.Poi;

/// <summary>
/// How a point of interest is drawn: <c>look</c> <c>text</c> (its label as text), <c>badge</c> (its label in a filled
/// circle), <c>dot</c> (a filled circle), <c>icon</c> (an MDI icon, <see cref="Icon"/>, or a PNG, <see cref="Image"/>); the
/// colour (of the text, the dot or the icon; a point's own colour wins), the size (m: the text's em, the dot's diameter,
/// the icon's height; a point's own size wins), the text's weight, an outline (colour and width, m), the badge's circle
/// colour (also a circle behind an icon), whether the label goes beside a dot or icon and its size (m;
/// <see cref="PoiLayout.LabelScale"/> times the size when left out). The name is the style's in the lists (a bundled
/// style's in English and Japanese).
/// </summary>
/// <param name="Image">The PNG as the POI styles file names it (a path in the file's folder).</param>
/// <param name="ImageSha256">The PNG's SHA-256 (what the records compare: another picture under the same name draws again).</param>
/// <param name="ImageAspect">The PNG's width over its height (1 without a PNG).</param>
/// <param name="ImagePath">Where the PNG is read from now (not compared: a run reads a copy of it).</param>
public sealed record PoiStyle(string Id, ItemName Name, string Look, Rgb Color, double Size, string Weight,
    Rgb? Outline, double OutlineWidth, Rgb? BadgeColor, bool ShowLabel, string? Icon, string? Image = null, double? LabelSize = null,
    string? ImageSha256 = null, double ImageAspect = 1, [property: JsonIgnore] string? ImagePath = null);

/// <summary>Which maps show a point: the atlas maps, the road map. (The minimap is made from the map it is set to, points and all.)</summary>
public sealed record PoiShow(bool Atlas, bool Roadmap)
{
    /// <summary>What a point shows when neither it, its group nor a folder says: the atlas maps only.</summary>
    public static readonly PoiShow Default = new(true, false);
}

/// <summary>
/// A folder of the POI folder (<see cref="Path"/>: its path in the POI folder with '/', "" for the POI folder itself) with
/// the settings of its <c>_.json</c>; without one, the defaults and the folder's own name.
/// </summary>
public sealed record PoiFolder(string Path, ItemName Name, int Order, string? Style, PoiShow? Show, bool Visible, bool Locked)
{
    /// <summary>The folder it is in; null for the POI folder itself.</summary>
    public string? Parent => Path.Length == 0 ? null : Path.LastIndexOf('/') is var i and >= 0 ? Path[..i] : "";
}

/// <summary>
/// A group of points: one <c>.json</c> file of the POI folder (<see cref="Path"/>: its path without <c>.json</c>), with the
/// credit the exported maps that show its points carry (<c>credit</c>, optional).
/// </summary>
public sealed record PoiGroup(string Path, string Folder, ItemName Name, int Order, string? Style, PoiShow? Show,
    bool Visible, bool Locked, IReadOnlyList<PoiPoint> Points, string? Credit);

/// <summary>
/// A point: its id in its group, its name (in the lists and the search; one text, may be empty), its label (the text the
/// maps draw, per language: <c>en</c>, <c>ja</c>; the English one where a language has none), its place and the values it
/// sets itself.
/// </summary>
/// <param name="Group">The path of the group (file) it is in.</param>
public sealed record PoiPoint(string Id, string Group, string Name, IReadOnlyDictionary<string, string> Label, double X, double Y, string? Style,
    PoiShow? Show, Rgb? Color, double? Size, bool? Visible, bool? Locked);

/// <summary>A point with every inherited value settled.</summary>
public sealed record ResolvedPoi(PoiPoint Point, PoiStyle Style, PoiShow Show, Rgb Color, double Size, bool Visible, bool Locked)
{
    /// <summary>The label in a language (English when that one is empty).</summary>
    public string LabelIn(string lang) =>
        Point.Label.TryGetValue(lang, out var n) && n.Length > 0 ? n : Point.Label.TryGetValue("en", out var en) ? en : "";

    /// <summary>The size of the label beside a dot or icon (m).</summary>
    public double LabelSize => Style.LabelSize ?? PoiLayout.LabelScale * Size;

    /// <summary>
    /// What the maps draw of the point, in a fixed form (its labels, place, look and the maps it shows on; not its id,
    /// name, group, its style's id or name, which draw nothing): what the records compare.
    /// </summary>
    public string Drawn()
    {
        static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var s = Style;
        var sb = new StringBuilder();
        // every language's label, a missing one as an empty one (the same text for the same drawing)
        sb.AppendJoin(',', AtlasPresets.Languages.Select(l => l + "=" + Point.Label.GetValueOrDefault(l, "")))
          .Append('\t').Append(R(Point.X)).Append('\t').Append(R(Point.Y))
          .Append('\t').Append(s.Look).Append('\t').Append(Color).Append('\t').Append(R(Size)).Append('\t').Append(s.Weight)
          .Append('\t').Append(s.Outline?.ToString() ?? "-").Append('\t').Append(R(s.OutlineWidth)).Append('\t').Append(s.BadgeColor?.ToString() ?? "-")
          .Append('\t').Append(s.ShowLabel).Append('\t').Append(s.Icon ?? "-");
        // written only when used, so that a point drawn as before keeps its text (the label's size under its earlier word)
        if (s.ImageSha256 is { } img) sb.Append("\timage=").Append(img);
        if (s.ShowLabel) sb.Append("\tnameSize=").Append(R(LabelSize));
        sb.Append('\t').Append(Show.Atlas).Append(Show.Roadmap).Append('\t').Append(Visible);
        return sb.ToString();
    }
}

/// <summary>
/// Where the parts of a point go around it (m), for the drawing and for the space the other labels leave it: the mark
/// (the dot, the icon or the circle behind it) centred on the point, and the label beside it to the right, its middle at
/// the point's height.
/// </summary>
public static class PoiLayout
{
    /// <summary>The circle behind an icon (a style's <c>badgeColor</c>): its diameter over the icon's height.</summary>
    public const double IconBadge = 1.5;
    /// <summary>The label beside a dot or icon: its size over the point's size when the style gives none.</summary>
    public const double LabelScale = 0.6;
    /// <summary>The gap between the mark and the label beside it, over the label's size.</summary>
    public const double LabelGap = 0.3;

    /// <summary>The mark of a dot or icon without its outline: width and height (m).</summary>
    public static (double W, double H) Mark(ResolvedPoi p)
    {
        var s = p.Style;
        if (s.Look == "dot") return (p.Size, p.Size);
        double w = s.Image is null ? p.Size : p.Size * s.ImageAspect;
        if (s.BadgeColor is not null) return (IconBadge * Math.Max(w, p.Size), IconBadge * Math.Max(w, p.Size));
        return (w, p.Size);
    }

    /// <summary>
    /// The space a dot or icon takes (m, square or its box), outline included: a dot as the labels have always left it
    /// (its size and twice the outline); an icon its box or circle and twice the outline.
    /// </summary>
    public static (double W, double H) Space(ResolvedPoi p)
    {
        var (w, h) = Mark(p);
        return (w + 2 * p.Style.OutlineWidth, h + 2 * p.Style.OutlineWidth);
    }

    /// <summary>The left edge of the label beside a dot or icon (m, east of the point).</summary>
    public static double LabelLeft(ResolvedPoi p) => p.Point.X + Mark(p).W / 2 + p.Style.OutlineWidth / 2 + LabelGap * p.LabelSize;

    /// <summary>How far east of its point a point can draw at most (m): its mark's half width, and with a label beside, a label of one em a character.</summary>
    public static double Reach(ResolvedPoi p, string label) =>
        p.Style.ShowLabel && label.Length > 0 ? LabelLeft(p) - p.Point.X + label.Length * p.LabelSize : Space(p).W / 2;
}

/// <summary>
/// The points of interest of a project and the POI styles. The points are a folder on disk (the project's <c>poi</c>;
/// null = the bundled <c>data/poi/</c>: <c>highway-markers.json</c>, the 26 Highway One markers A-Z, and
/// <c>colored-dots.json</c>, the 5 coloured dots of the postal code map by Virus_City, both locked): each folder in it is a
/// POI folder (its settings in <c>_.json</c>, which may be left out), each other <c>.json</c> file a group of points with
/// its settings, so a group or a folder can be copied to another project as files. The styles are the bundled ones
/// (<c>data/poi-styles.json</c>: the markers', the dots' and the facility names') and those of the project's
/// <c>poiStyles</c> file (their PNG icons in that file's folder). The style and the maps a point shows on come from the
/// point, else its group, else its folder, else the folder above, and so on (the nearest wins); a hidden group or folder
/// hides everything in it, a locked one locks everything in it. See <c>Docs/spec/poi-format.ja.md</c>.
/// </summary>
public sealed class PoiData
{
    /// <summary>The name of a folder's settings file.</summary>
    public const string FolderFile = "_.json";
    public const string BundledStyles = "poi-styles.json";

    /// <summary>The folders, each before the folders in it (the POI folder itself first).</summary>
    public required IReadOnlyList<PoiFolder> Folders { get; init; }
    /// <summary>The groups in their order: in each folder, its groups and folders by <c>order</c>, then by name.</summary>
    public required IReadOnlyList<PoiGroup> Groups { get; init; }
    public required IReadOnlyList<PoiStyle> Styles { get; init; }

    public IEnumerable<PoiPoint> Points => Groups.SelectMany(g => g.Points);

    /// <summary>The project's points and styles (its own folder and file, else the bundled ones).</summary>
    /// <exception cref="PoiException">A file or the folder is missing, or not usable.</exception>
    public static PoiData Of(Project project)
    {
        var (files, where, styles, stylesWhere) = Sources(project);
        if (files is null) throw new PoiException($"{where}: folder not found");
        if (project.PoiStylesPath is not null && styles is null) throw new PoiException($"{stylesWhere}: not found");
        return Load(files, where, styles, stylesWhere, project.PoiStylesPath is { } sp ? Path.GetDirectoryName(sp) : null);
    }

    /// <summary>The bundled points and styles.</summary>
    public static PoiData Default => Load(BundledFiles(), "data/poi", null, "");

    /// <summary>The bundled POI folder's files by their path in it ('/'), and the bundled styles file.</summary>
    public static IReadOnlyList<(string Path, string Text)> BundledPoints => BundledFiles();
    public static string BundledStylesText => Bundled(BundledStyles);

    /// <summary>
    /// The SHA-256 of what the maps draw from the points of interest: every point's settled values in drawing order (so
    /// editing a name of a group or folder, a credit or a comment makes nothing again). When they cannot be read, of the
    /// files as they are.
    /// </summary>
    public static string Sha256(Project project)
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var p in Of(project).Resolve()) sb.Append(p.Drawn()).Append('\n');
        }
        catch (PoiException)
        {
            var (files, _, styles, _) = Sources(project);
            sb.Clear().Append("not readable\n");
            foreach (var (path, text) in files ?? []) sb.Append(path).Append('\n').Append(text).Append('\n');
            sb.Append("styles\n").Append(styles ?? "");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The POI folder's files (null when the folder is missing) and the project's styles (null for none or missing).</summary>
    static (List<(string Path, string Text)>? Files, string Where, string? Styles, string StylesWhere) Sources(Project project)
    {
        List<(string, string)>? files;
        string where;
        if (project.PoiFolderPath is { } p)
        {
            where = p;
            files = Directory.Exists(where) ? ReadFolder(where) : null;
        }
        else (files, where) = (BundledFiles(), "data/poi");
        string? styles = null, stylesWhere = "";
        if (project.PoiStylesPath is { } s)
        {
            stylesWhere = s;
            if (File.Exists(stylesWhere)) styles = File.ReadAllText(stylesWhere);
        }
        return (files, where, styles, stylesWhere);
    }

    /// <summary>Every <c>.json</c> file in the folder and the folders in it, by its path in the folder ('/'), in path order.</summary>
    public static List<(string Path, string Text)> ReadFolder(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText(f)))
            .OrderBy(e => e.Item1, StringComparer.Ordinal).ToList();

    static List<(string, string)> BundledFiles() =>
        EmbeddedData.Names("poi/").Select(n => (n["poi/".Length..], Bundled(n))).OrderBy(e => e.Item1, StringComparer.Ordinal).ToList();

    static string Bundled(string name)
    {
        using var s = EmbeddedData.Open(name);
        return new StreamReader(s).ReadToEnd();
    }

    /// <param name="files">The POI folder's files by their path in it ('/').</param>
    /// <param name="where">The POI folder, for the messages.</param>
    /// <param name="stylesJson">The project's styles (added to the bundled ones), or null.</param>
    /// <param name="stylesFolder">The folder of the project's styles file, where their PNG icons are (null: they cannot name one).</param>
    public static PoiData Load(IEnumerable<(string Path, string Text)> files, string where, string? stylesJson, string stylesWhere, string? stylesFolder = null)
    {
        var problems = new List<string>();
        var styles = ParseStyles(Bundled(BundledStyles), "data/" + BundledStyles, null, problems);
        if (stylesJson is not null)
        {
            var own = ParseStyles(stylesJson, stylesWhere, stylesFolder, problems);
            foreach (var s in own.Where(s => styles.Any(b => b.Id == s.Id)))
                problems.Add($"{stylesWhere}: style '{s.Id}' is a bundled style (give yours another id)");
            styles.AddRange(own.Where(s => styles.All(b => b.Id != s.Id)));
        }

        // folders (every folder above a file, with the settings of its _.json) and groups (the other files)
        var settings = new Dictionary<string, (JsonObject Json, string File)>(StringComparer.Ordinal);
        var groupFiles = new List<(string Path, string Folder, JsonObject Json, string File)>();
        var folderPaths = new SortedSet<string>(StringComparer.Ordinal) { "" };
        foreach (var (path, text) in files)
        {
            string file = $"{where}/{path}";
            int slash = path.LastIndexOf('/');
            string folder = slash < 0 ? "" : path[..slash], name = path[(slash + 1)..];
            for (var f = folder; f.Length > 0; f = f.LastIndexOf('/') is var i and >= 0 ? f[..i] : "") folderPaths.Add(f);
            var json = Root(text, file, problems);
            if (name == FolderFile) settings[folder] = (json, file);
            else groupFiles.Add((path[..^".json".Length], folder, json, file));
        }
        var folders = new Dictionary<string, PoiFolder>(StringComparer.Ordinal);
        foreach (var f in folderPaths)
        {
            var (json, file) = settings.TryGetValue(f, out var s) ? s : (new JsonObject(), $"{where}/{(f.Length > 0 ? f + "/" : "")}{FolderFile}");
            var (name, order, style, show, visible, locked) = Settings(json, file, f.Length == 0 ? "" : f[(f.LastIndexOf('/') + 1)..], problems);
            folders[f] = new PoiFolder(f, name, order, style, show, visible, locked);
            if (style is not null && styles.All(x => x.Id != style)) problems.Add($"{file}: no POI style '{style}'");
        }
        var groups = new List<PoiGroup>();
        foreach (var (path, folder, json, file) in groupFiles)
        {
            var (name, order, style, show, visible, locked) = Settings(json, file, path[(path.LastIndexOf('/') + 1)..], problems);
            if (style is not null && styles.All(x => x.Id != style)) problems.Add($"{file}: no POI style '{style}'");
            var points = ParsePoints(json, path, file, styles, problems);
            groups.Add(new PoiGroup(path, folder, name, order, style, show, visible, locked, points, OptStr(json, "credit", file, problems)));
        }

        var (orderedFolders, orderedGroups) = Ordered(folders, groups);
        var data = new PoiData { Folders = orderedFolders, Groups = orderedGroups, Styles = styles };
        if (problems.Count == 0)
            foreach (var g in data.Groups)
                foreach (var p in g.Points)
                    if (data.StyleIdOf(g, p) is null) problems.Add($"{where}/{g.Path}.json: point '{p.Id}': neither it, its group nor a folder above names a POI style");
        if (problems.Count > 0) throw new PoiException(string.Join("; ", problems));
        return data;
    }

    /// <summary>The folders and groups in the order of a walk from the POI folder: in each folder, its groups and folders by order, then name.</summary>
    static (List<PoiFolder> Folders, List<PoiGroup> Groups) Ordered(Dictionary<string, PoiFolder> folders, List<PoiGroup> groups)
    {
        var fo = new List<PoiFolder>();
        var go = new List<PoiGroup>();
        static string Last(string path) => path[(path.LastIndexOf('/') + 1)..];
        void Visit(PoiFolder f)
        {
            fo.Add(f);
            var entries = groups.Where(g => g.Folder == f.Path).Select(g => (g.Order, Name: Last(g.Path), Kind: 0, Group: g, Folder: (PoiFolder?)null))
                .Concat(folders.Values.Where(x => x.Parent == f.Path).Select(x => (x.Order, Name: Last(x.Path), Kind: 1, Group: (PoiGroup?)null, Folder: (PoiFolder?)x)))
                .OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Kind);
            foreach (var e in entries)
                if (e.Group is { } g) go.Add(g);
                else Visit(e.Folder!);
        }
        Visit(folders[""]);
        return (fo, go);
    }

    string? StyleIdOf(PoiGroup g, PoiPoint p) => p.Style ?? g.Style ?? Chain(g.Folder).Select(f => f.Style).FirstOrDefault(s => s is not null);

    /// <summary>The folder and the folders above it, nearest first.</summary>
    IEnumerable<PoiFolder> Chain(string folder)
    {
        for (string? f = folder; f is not null; f = Folders.First(x => x.Path == f).Parent)
            yield return Folders.First(x => x.Path == f);
    }

    /// <summary>Every point with its inherited values, group by group in their order, each in the order of its file.</summary>
    public List<ResolvedPoi> Resolve()
    {
        var o = new List<ResolvedPoi>();
        foreach (var g in Groups)
        {
            var chain = Chain(g.Folder).ToList();
            foreach (var p in g.Points)
            {
                var style = Styles.First(s => s.Id == StyleIdOf(g, p));
                var show = p.Show ?? g.Show ?? chain.Select(f => f.Show).FirstOrDefault(s => s is not null) ?? PoiShow.Default;
                bool visible = (p.Visible ?? true) && g.Visible && chain.All(f => f.Visible);
                bool locked = (p.Locked ?? false) || g.Locked || chain.Any(f => f.Locked);
                o.Add(new ResolvedPoi(p, style, show, p.Color ?? style.Color, p.Size ?? style.Size, visible, locked));
            }
        }
        return o;
    }

    // ------------------------------------------------------------------------------------------------ parsing

    static JsonObject Root(string json, string where, List<string> problems)
    {
        try
        {
            if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o) return o;
            problems.Add($"{where}: not a JSON object");
        }
        catch (JsonException ex) { problems.Add($"{where}: not JSON ({ex.Message})"); }
        return new JsonObject();
    }

    /// <param name="folder">The styles file's folder, where the PNG icons it names are (null: a style naming one is refused).</param>
    static List<PoiStyle> ParseStyles(string json, string where, string? folder, List<string> problems)
    {
        var o = new List<PoiStyle>();
        if (Root(json, where, problems)["styles"] is not JsonArray a) { problems.Add($"{where}: 'styles' must be a list"); return o; }
        foreach (var n in a)
        {
            if (n is not JsonObject s) { problems.Add($"{where}: every style is an object"); continue; }
            string id = Str(s, "id", where, problems) ?? "";
            string look = Str(s, "look", where, problems) ?? "";
            if (look is not ("text" or "badge" or "dot" or "icon")) problems.Add($"{where}: style '{id}': look '{look}' (text, badge, dot or icon)");
            string weight = s["weight"]?.GetValue<string>() ?? "normal";
            if (weight is not ("normal" or "bold")) problems.Add($"{where}: style '{id}': weight '{weight}' (normal or bold)");
            string? icon = OptStr(s, "icon", where, problems), image = OptStr(s, "image", where, problems);
            if (look == "icon" && (icon is null) == (image is null)) problems.Add($"{where}: style '{id}': an icon style names an MDI icon ('icon') or a PNG ('image'), one of them");
            if (look != "icon" && (icon is not null || image is not null)) problems.Add($"{where}: style '{id}': 'icon' and 'image' are for the look icon");
            if (icon is not null && MdiIcons.PathOf(icon) is null) problems.Add($"{where}: style '{id}': no MDI icon '{icon}'");
            string? imageSha = null, imagePath = null;
            double aspect = 1;
            if (image is not null && look == "icon")
                (imageSha, imagePath, aspect) = ReadImage(image, folder, $"{where}: style '{id}'", problems);
            bool showLabel = Bool(s, "showLabel", where, problems) ?? false;
            if (showLabel && look is not ("dot" or "icon")) problems.Add($"{where}: style '{id}': 'showLabel' is for the looks dot and icon");
            double? labelSize = s["labelSize"] is null ? null : Num(s, "labelSize", where, problems, positive: true);
            o.Add(new PoiStyle(id, Name(s, where, problems), look, Color(s, "color", where, problems) ?? default, Num(s, "size", where, problems, positive: true),
                weight, Color(s, "outline", where, problems, optional: true), s["outlineWidth"] is null ? 0 : Num(s, "outlineWidth", where, problems),
                Color(s, "badgeColor", where, problems, optional: true), showLabel, icon, image, labelSize, imageSha, aspect, imagePath));
        }
        foreach (var g in o.GroupBy(x => x.Id).Where(g => g.Count() > 1)) problems.Add($"{where}: two styles named '{g.Key}'");
        return o;
    }

    /// <summary>A style's PNG: a file in the styles file's folder (no way out of it), a PNG the app can read.</summary>
    static (string? Sha, string? Path, double Aspect) ReadImage(string image, string? folder, string what, List<string> problems)
    {
        var parts = image.Replace('\\', '/').Split('/');
        if (folder is null || Path.IsPathRooted(image) || parts.Any(p => p is "" or "." or ".."))
        {
            problems.Add($"{what}: 'image' must be a file in the folder of the POI styles file ({image})");
            return (null, null, 1);
        }
        var path = Path.Combine([folder, .. parts]);
        if (!File.Exists(path))
        {
            problems.Add($"{what}: image not found: {image}");
            return (null, null, 1);
        }
        var bytes = File.ReadAllBytes(path);
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.Info.Width < 1 || codec.Info.Height < 1)
        {
            problems.Add($"{what}: {image} is not a PNG");
            return (null, null, 1);
        }
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), path, codec.Info.Width / (double)codec.Info.Height);
    }

    /// <summary>The settings a folder's <c>_.json</c> and a group's file share; the name defaults to the folder's or file's own name.</summary>
    static (ItemName Name, int Order, string? Style, PoiShow? Show, bool Visible, bool Locked) Settings(
        JsonObject o, string where, string ownName, List<string> problems)
    {
        var name = o["name"] is null ? ItemName.Of(ownName) : Name(o, where, problems);
        int order = 0;
        if (o["order"] is { } on && !(on is JsonValue ov && ov.TryGetValue<int>(out order))) problems.Add($"{where}: 'order' must be a whole number");
        return (name, order, OptStr(o, "style", where, problems), Show(o, where, problems), Bool(o, "visible", where, problems) ?? true, Bool(o, "locked", where, problems) ?? false);
    }

    static List<PoiPoint> ParsePoints(JsonObject root, string group, string where, IReadOnlyList<PoiStyle> styles, List<string> problems)
    {
        var points = new List<PoiPoint>();
        if (root["points"] is not JsonArray pa) { problems.Add($"{where}: 'points' must be a list"); return points; }
        foreach (var n in pa)
        {
            if (n is not JsonObject p) { problems.Add($"{where}: every point is an object"); continue; }
            points.Add(new PoiPoint(Str(p, "id", where, problems) ?? "", group, PointName(p, where, problems), Label(p, where, problems),
                Num(p, "x", where, problems), Num(p, "y", where, problems), OptStr(p, "style", where, problems), Show(p, where, problems),
                Color(p, "color", where, problems, optional: true), p["size"] is null ? null : Num(p, "size", where, problems, positive: true),
                Bool(p, "visible", where, problems), Bool(p, "locked", where, problems)));
        }
        foreach (var g in points.GroupBy(x => x.Id).Where(g => g.Count() > 1)) problems.Add($"{where}: two points named '{g.Key}'");
        foreach (var p in points)
        {
            if (p.Style is { } s && styles.All(x => x.Id != s)) problems.Add($"{where}: point '{p.Id}': no POI style '{s}'");
            if (!MapFrame.Outer.Contains(p.X, p.Y))
                problems.Add(FormattableString.Invariant($"{where}: point '{p.Id}' ({p.X}, {p.Y}) is off the map"));
        }
        return points;
    }

    static string? Str(JsonObject o, string key, string where, List<string> problems)
    {
        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
        problems.Add($"{where}: '{key}' must be text ({o["id"]?.ToString() ?? "?"})");
        return null;
    }

    static string? OptStr(JsonObject o, string key, string where, List<string> problems)
    {
        if (o[key] is null) return null;
        return Str(o, key, where, problems);
    }

    static bool? Bool(JsonObject o, string key, string where, List<string> problems)
    {
        if (o[key] is null) return null;
        if (o[key] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        problems.Add($"{where}: '{key}' must be true or false{(o["id"] is { } id ? $" ({id})" : "")}");
        return null;
    }

    static double Num(JsonObject o, string key, string where, List<string> problems, bool positive = false)
    {
        if (o[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) && (!positive || d > 0)) return d;
        problems.Add($"{where}: '{key}' must be a{(positive ? " positive" : "")} number ({o["id"]?.ToString() ?? "?"})");
        return 0;
    }

    static Rgb? Color(JsonObject o, string key, string where, List<string> problems, bool optional = false)
    {
        var n = o[key];
        if (n is null) { if (!optional) problems.Add($"{where}: '{key}' is needed ({o["id"]})"); return null; }
        try { return Rgb.Parse(n.GetValue<string>()); }
        catch (Exception) { problems.Add($"{where}: '{key}' of '{o["id"]}' is not a colour (#rrggbb)"); return null; }
    }

    /// <summary>A name of a folder, group or style: a text, or <c>{"en", "ja"}</c> (as the bundled ones have it).</summary>
    static ItemName Name(JsonObject o, string where, List<string> problems)
    {
        if (ItemName.FromNode(o["name"]) is { } n) return n;
        problems.Add($"{where}: {(o["id"] is { } id ? $"'{id}': " : "")}'name' must be a text");
        return ItemName.Of("");
    }

    /// <summary>A point's name (a text; none: empty).</summary>
    static string PointName(JsonObject o, string where, List<string> problems)
    {
        if (o["name"] is null) return "";
        if (o["name"] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        problems.Add($"{where}: point '{o["id"]}': 'name' must be a text");
        return "";
    }

    /// <summary>A point's label: <c>{"en", "ja"}</c>, the texts per language (none: no label).</summary>
    static IReadOnlyDictionary<string, string> Label(JsonObject o, string where, List<string> problems)
    {
        var label = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o["label"] is null) return label;
        if (o["label"] is not JsonObject l) { problems.Add($"{where}: point '{o["id"]}': 'label' must be {{ \"en\", \"ja\" }}"); return label; }
        foreach (var (lang, v) in l)
        {
            if (!AtlasPresets.Languages.Contains(lang)) problems.Add($"{where}: point '{o["id"]}': a label in '{lang}' ({string.Join(", ", AtlasPresets.Languages)})");
            else if (v is JsonValue tv && tv.TryGetValue<string>(out var t)) label[lang] = t;
            else problems.Add($"{where}: point '{o["id"]}': the label in '{lang}' must be a text");
        }
        return label;
    }

    static PoiShow? Show(JsonObject o, string where, List<string> problems)
    {
        if (o["show"] is null) return null;
        if (o["show"] is not JsonObject s) { problems.Add($"{where}: 'show'{(o["id"] is { } id ? $" of '{id}'" : "")} must be {{ atlas, roadmap }}"); return null; }
        return new PoiShow(s["atlas"]?.GetValue<bool>() ?? PoiShow.Default.Atlas, s["roadmap"]?.GetValue<bool>() ?? PoiShow.Default.Roadmap);
    }
}

public sealed class PoiException(string message) : Exception(message);
