using System.Globalization;
using System.Text;
using System.Text.Json;
using FxMapGenerator.Core.Labels;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Export;

/// <summary>A map in the web export: its id, its tile folder's name (<see cref="MapSet.ExportName"/>), its title, the colour behind its tiles and its finest zoom.</summary>
public sealed record WebMap(string Id, string Name, string Title, string Background, int MaxZoom);

/// <summary>
/// How the web tiles written out are numbered: from the north-west corner of the project's frame. Without cells added above
/// or to the left that is the work folder's numbering, the same grid as the loaf-scripts map tiles. With them, the tiles
/// of zoom 3 and finer keep their pictures under numbers moved by the cells added (a cell is one tile of zoom 3), and the
/// tiles of zooms 2 to 0, which would straddle the frame's edge, are made again from the tiles written at zoom 3.
/// </summary>
public static class ExportGrid
{
    /// <summary>The coarsest zoom whose tiles are written from the work folder's when the numbers move.</summary>
    public const int FirstKept = 3;

    /// <summary>Whether the numbers move (cells added above or to the left of the standard frame).</summary>
    public static bool Renumbers(MapFrame frame) => frame.CellsLeft > 0 || frame.CellsTop > 0;

    /// <summary>The work folder's tile (x, y) of a zoom written out as (x - X, y - Y) (zoom <see cref="FirstKept"/> and finer).</summary>
    public static (int X, int Y) Shift(MapFrame frame, int z)
    {
        var t = frame.Tiles(z);
        return (t.X0, t.Y0);
    }

    /// <summary>The columns and rows of the tiles written at a zoom (the standard frame at zoom 8: 128 x 192).</summary>
    public static (int Columns, int Rows) Size(MapFrame frame, int z)
    {
        int k = WorldGrid.Zoom - z, d = (1 << k) - 1;
        return ((4 * frame.BlocksX + d) >> k, (4 * frame.BlocksY + d) >> k);
    }

    /// <summary>Whether a zoom's tiles are written from the work folder's (else they are made again from zoom <see cref="FirstKept"/>).</summary>
    public static bool Copied(MapFrame frame, int z) => !Renumbers(frame) || z >= FirstKept;
}

/// <summary>
/// The files that go with the web tiles: the Leaflet viewer (<c>index.html</c> + <c>leaflet/</c>, tiles read by relative
/// path), the lb-phone <c>Config.CustomMaps</c> example, a README and the credits.
/// </summary>
public static class WebExport
{
    /// <summary>
    /// The frame in pixels at the finest zoom written (half per level below 8): lb-phone derives its zoom levels and scale
    /// from this "resolution" (the standard frame: 32768 x 49152 at 8, 16384 x 24576 at 7).
    /// </summary>
    public static (int X, int Y) Resolution(MapFrame frame, int maxZoom) =>
        ((frame.BlocksX * 4 * 256) >> (WorldGrid.Zoom - maxZoom), (frame.BlocksY * 4 * 256) >> (WorldGrid.Zoom - maxZoom));

    /// <summary>Metres per pixel of the tiles at a zoom (8: 0.27 m).</summary>
    public static double MetresPerPixel(int zoom) => WorldGrid.TileSize * (1 << (WorldGrid.Zoom - zoom)) / 256.0;

    public static WebMap MapOf(Project project, WorkFolder folder, string id, int maxZoom = WorldGrid.Zoom)
    {
        var set = MapSet.Parse(id);
        return new WebMap(id, set.ExportName, set.Title(project), Background(project, folder, set), maxZoom);
    }

    /// <summary>The colour behind a map's tiles: the open sea, as the tiles outside the range are painted.</summary>
    static string Background(Project project, WorkFolder folder, MapSet set)
    {
        if (set.Kind != MapKind.Satellite) return project.StyleOf(set).OpenSea.ToString();
        var (r, g, b) = SatelliteLowZoomStage.SeaColour(project.Range, new TileStore(folder.Tiles(set.Id)));
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    /// <summary>
    /// The credits of the atlas and road maps among <paramref name="maps"/>: the game world they are made from, then the
    /// credits of the data they are made with (<see cref="DataCredits"/>), each text once.
    /// </summary>
    public static string CellMapCredits(Project project, IEnumerable<MapSet> maps)
    {
        var cellMaps = maps.Where(m => m.IsCellMap).ToList();
        if (cellMaps.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append(string.Join(", ", cellMaps.Select(m => m.Title(project)))).Append(": made from the world of Grand Theft Auto V (Rockstar Games) as scanned in\n")
          .Append("FiveM on the server of \"").Append(project.File.Name.ReplaceLineEndings(" ")).Append("\", with the game's roads and street and zone names.\n");
        foreach (var credit in cellMaps.SelectMany(m => DataCredits(project, m)).Distinct(StringComparer.Ordinal))
            sb.Append(credit.ReplaceLineEndings("\n").TrimEnd('\n')).Append('\n');
        return sb.Append('\n').ToString();
    }

    /// <summary>
    /// The credits of the data a map is made with, in this order: its style's; the postal codes' and the route numbers' when
    /// its labels hold any; those of the groups of points of interest it shows; the icons' when a point it shows draws an
    /// MDI icon (<see cref="MdiIcons.Credit"/>). Data without a credit adds nothing (a project's own postal code list or
    /// points say nothing unless their files give one, nor do a project's own PNG icons).
    /// </summary>
    public static List<string> DataCredits(Project project, MapSet map)
    {
        var o = new List<string>();
        var style = project.StyleOf(map);
        if (style.Credit is { } sc) o.Add(sc);
        if (map.Kind == MapKind.Atlas && style.Labels is { } ls
            && LabelsStage.Record(project, map.Language!)?.Files.FirstOrDefault(f => f.Key == ls.PlacementKey) is { } labels)
        {
            if (labels.Postal > 0 && LabelsStage.PostalsCredit(project) is { } pc) o.Add(pc);
            if (labels.Routes > 0 && Routes.Credit is { } rc) o.Add(rc);
        }
        PoiData? poi = null;
        try { poi = PoiData.Of(project); }
        catch (PoiException) { }
        if (poi is not null)
        {
            var drawn = poi.Resolve().Where(p => p.Visible && (map.Kind == MapKind.Atlas ? p.Show.Atlas : p.Show.Roadmap)).ToList();
            var shown = drawn.Select(p => p.Point.Group).ToHashSet(StringComparer.Ordinal);
            o.AddRange(poi.Groups.Where(g => g.Credit is not null && shown.Contains(g.Path)).Select(g => g.Credit!));
            if (drawn.Any(p => p.Style.Look == "icon" && p.Style.Icon is not null)) o.Add(MdiIcons.Credit);
        }
        return o;
    }

    /// <summary>The viewer page for these maps (the first is shown first) over the project's frame.</summary>
    public static string ViewerHtml(string title, IReadOnlyList<WebMap> maps, string version, MapFrame frame = default)
    {
        var template = Read("viewer/index.html");
        if (!frame.IsStandard)
        {
            var standard = """
                // The map frame in game metres (x east, y north): x -4140..4860, y 8400..-5100. A z8 tile is 70.3125 m (256 px),
                // the same grid as the loaf-scripts tiles; Leaflet's lat is the game's y, lng its x.
                const TILE = 70.3125, LEFT = -4140, TOP = 8400, RIGHT = 4860, BOTTOM = -5100;
                """.Replace("\r\n", "\n", StringComparison.Ordinal);
            var lf = template.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!lf.Contains(standard, StringComparison.Ordinal)) throw new InvalidDataException("viewer/index.html: the map frame's lines are not the expected ones");
            var grid = ExportGrid.Renumbers(frame) ? "numbered from its north-west corner" : "the same grid as the loaf-scripts tiles";
            template = lf.Replace(standard, $$"""
                // The map frame in game metres (x east, y north): x {{F(frame.X0)}}..{{F(frame.X1)}}, y {{F(frame.Y0)}}..{{F(frame.Y1)}}. A z8 tile is 70.3125 m (256 px),
                // {{grid}}; Leaflet's lat is the game's y, lng its x.
                const TILE = 70.3125, LEFT = {{F(frame.X0)}}, TOP = {{F(frame.Y0)}}, RIGHT = {{F(frame.X1)}}, BOTTOM = {{F(frame.Y1)}};
                """.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        }
        var config = JsonSerializer.Serialize(new
        {
            maps = maps.Select(m => new { id = m.Name, title = m.Title, background = m.Background, maxZoom = m.MaxZoom }),
            credit = $"FxMapGenerator {version}",
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return template.Replace("__TITLE__", Escape(title), StringComparison.Ordinal).Replace("/*CONFIG*/null", config, StringComparison.Ordinal);
    }

    /// <summary>The Leaflet files of the viewer: relative path -> bytes.</summary>
    public static IReadOnlyList<(string Path, byte[] Bytes)> ViewerFiles() =>
    [
        ("leaflet/leaflet.js", ReadBytes("viewer/leaflet.js")),
        ("leaflet/leaflet.css", ReadBytes("viewer/leaflet.css")),
        ("leaflet/LICENSE.txt", ReadBytes("viewer/leaflet-LICENSE.txt")),
    ];

    /// <summary>
    /// An entry for lb-phone's <c>Config.CustomMaps</c> (lb-phone/config/config.lua): one style per map, the style name
    /// is the map's tile folder (the <c>{layer}</c> of the address). lb-phone takes its zoom levels from the resolution,
    /// so the resolution is the map's size at the finest zoom exported.
    /// </summary>
    public static string LbPhoneExample(string label, IReadOnlyList<WebMap> maps, string? baseUrl, int maxZoom = WorldGrid.Zoom, MapFrame frame = default)
    {
        var url = (baseUrl?.TrimEnd('/') ?? "https://example.com/your-map") + "/tiles/{layer}/{z}/{x}/{y}.png";
        var (rx, ry) = Resolution(frame, maxZoom);
        var sb = new StringBuilder();
        sb.Append("-- lb-phone: add this entry to Config.CustomMaps in lb-phone/config/config.lua.\n");
        if (baseUrl is null) sb.Append("-- Put the web tiles on a static web host and replace https://example.com/your-map with their address.\n");
        sb.Append("-- lb-phone works out the zoom levels from resolution (the map at z").Append(maxZoom).Append(": ").Append(rx).Append(" x ").Append(ry)
          .Append(" px, zoom 0-").Append(maxZoom).Append(").\n");
        sb.Append("{\n");
        sb.Append("    label       = ").Append(Lua(label)).Append(",\n");
        sb.Append("    url         = ").Append(Lua(url)).Append(",\n");
        sb.Append("    center      = { 1650, 450 },\n");
        sb.Append("    topLeft     = { ").Append(F(frame.X0)).Append(", ").Append(F(frame.Y0)).Append(" },\n");
        sb.Append("    bottomRight = { ").Append(F(frame.X1)).Append(", ").Append(F(frame.Y1)).Append(" },\n");
        sb.Append("    resolution  = { ").Append(rx).Append(", ").Append(ry).Append(" },\n");
        sb.Append("    zoom        = { default = ").Append(Math.Min(3, maxZoom)).Append(", max = ").Append(maxZoom).Append(", min = 0 },\n");
        sb.Append("    styles = {\n");
        foreach (var m in maps) sb.Append("        { name = ").Append(Lua(m.Name)).Append(", background = ").Append(Lua(m.Background)).Append(" },\n");
        sb.Append("    },\n");
        sb.Append("},\n");
        return sb.ToString();
    }

    /// <param name="tiles">Tile files written (hosts may limit the files of a site).</param>
    /// <param name="maxZoom">The finest zoom written.</param>
    /// <param name="frame">The project's frame (the standard frame says what it always said).</param>
    public static string Readme(string project, IReadOnlyList<WebMap> maps, int tiles, string version, int maxZoom = WorldGrid.Zoom, MapFrame frame = default)
    {
        var text = ReadmeText(project, maps, tiles, version, maxZoom);
        if (frame.IsStandard) return text;
        const string standard = """
            The tiles cover the GTA V map frame x -4140..4860, y 8400..-5100 (game metres). A zoom-8 tile is 70.3125 m and
            each zoom below doubles it, the same grid as the loaf-scripts map tiles, so the tiles line up with maps made for them.

            """;
        var lf = standard.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.Contains(lf, StringComparison.Ordinal)) throw new InvalidDataException("README: the map frame's lines are not the expected ones");
        var cover = $"The tiles cover the map frame x {F(frame.X0)}..{F(frame.X1)}, y {F(frame.Y0)}..{F(frame.Y1)} (game metres): the GTA V map frame\n"
            + $"x -4140..4860, y 8400..-5100 with cells of 2250 m added ({Cells(frame)}). A zoom-8 tile is 70.3125 m and each zoom\n"
            + "below doubles it";
        cover += ExportGrid.Renumbers(frame)
            ? ". The tile numbers start at the frame's north-west corner, so they do not line up with maps made for the\n"
              + "loaf-scripts map tiles; use the viewer and the lb-phone entry here, which are made for these tiles.\n"
            : ", the same grid as the loaf-scripts map tiles, so the tiles line up with maps made for them (the cells\n"
              + "added reach past those maps).\n";
        return text.Replace(lf, cover, StringComparison.Ordinal);
    }

    /// <summary>The cells a frame adds, in words: "1 below and 1 to the right".</summary>
    static string Cells(MapFrame frame)
    {
        var parts = new List<string>();
        void Add(int n, string where)
        {
            if (n > 0) parts.Add($"{n} {where}");
        }
        Add(frame.CellsTop, "above");
        Add(frame.CellsBottom, "below");
        Add(frame.CellsLeft, "to the left");
        Add(frame.CellsRight, "to the right");
        return parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }

    static string ReadmeText(string project, IReadOnlyList<WebMap> maps, int tiles, string version, int maxZoom) => $$"""
        Web map of {{project}}, made with FxMapGenerator {{version}}.

        index.html     a viewer: open it from the disk or on a web host (it reads the tiles next to it)
        tiles/         the tiles, tiles/<map>/<z>/<x>/<y>.png, 256 px, zoom 0-{{maxZoom}} ({{MetresPerPixel(maxZoom).ToString("0.00", CultureInfo.InvariantCulture)}} m a pixel at zoom {{maxZoom}})
                       maps: {{string.Join(", ", maps.Select(m => $"{m.Name} ({m.Title})"))}}
        lb-phone.lua   an entry for lb-phone's Config.CustomMaps
        CREDITS.txt    where the parts come from

        The tiles cover the GTA V map frame x -4140..4860, y 8400..-5100 (game metres). A zoom-8 tile is 70.3125 m and
        each zoom below doubles it, the same grid as the loaf-scripts map tiles, so the tiles line up with maps made for them.
        To serve them, put this folder on any static web host; the tile address is <host>/tiles/<map>/{z}/{x}/{y}.png.
        The tiles are {{tiles:N0}} files. Some hosts limit the files of one site (Cloudflare Pages: 20,000 on the free
        plan, 100,000 on paid plans); an export up to zoom 7 has about a quarter of the files of one up to zoom 8.

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <param name="cellMaps">The credits of the atlas and road maps (<see cref="CellMapCredits"/>), or empty.</param>
    public static string Credits(string project, IReadOnlyList<WebMap> maps, string version, string cellMaps = "")
    {
        var sb = new StringBuilder();
        sb.Append("Made with FxMapGenerator ").Append(version).Append(" (MIT License), https://github.com/Acc-Off/FxMapGenerator\n\n");
        if (maps.Any(m => MapSet.Parse(m.Id).Kind == MapKind.Satellite))
            sb.Append("Satellite map: pictures of the world of Grand Theft Auto V (Rockstar Games), taken in FiveM on the server of \"")
              .Append(project).Append("\".\n\n");
        sb.Append(cellMaps);
        sb.Append("Viewer: Leaflet 1.9.4, BSD 2-Clause License, (c) 2010-2023 Volodymyr Agafonkin, (c) 2010-2011 CloudMade.\n");
        sb.Append("        See leaflet/LICENSE.txt.\n");
        return sb.ToString();
    }

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static string Lua(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    static string Escape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    internal static string Read(string name)
    {
        using var r = new StreamReader(EmbeddedData.Open(name), Encoding.UTF8);
        return r.ReadToEnd();
    }

    internal static byte[] ReadBytes(string name)
    {
        using var s = EmbeddedData.Open(name);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}
