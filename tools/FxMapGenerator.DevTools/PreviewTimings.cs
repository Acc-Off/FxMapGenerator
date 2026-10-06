using System.Diagnostics;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Preview;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.DevTools;

/// <summary>
/// How long the style editor's preview takes to draw a window again for each kind of change of a style: the first drawing
/// (the window's data and every part), nothing changed, a colour, the ground's values, the shading's, the layers' (depth
/// bands), the labels' (a size), the region colours (a style whose ground is made of them), another language, another
/// place, then the larger windows (1, 2 and 4 km) and a colour change on the largest, and telling where a point's colour
/// comes from. Prints the parts each drawing made and their seconds; writes the first drawing's z8 tiles into
/// <c>&lt;out&gt;/tiles/8/&lt;x&gt;/&lt;y&gt;.png</c> (to set against the project's own tiles).
/// </summary>
public static class PreviewTimings
{
    public static int Run(string projectPath, double x, double y, string outFolder, int workers)
    {
        var project = Project.Load(projectPath);
        var preview = new StylePreview(project);
        var parallel = new FixedParallel(workers);
        Directory.CreateDirectory(outFolder);
        var postal = MapStyle.Builtin(AtlasPresets.PostalCodeMap).Source;
        var regional = MapStyle.Builtin(AtlasPresets.Regional).Source;
        MapStyle Style(JsonObject o) => MapStyle.Parse(o.ToJsonString(), "preview");
        JsonObject With(JsonObject o, string key, JsonNode value) => StyleChanges.With(o, key, value);
        var roadFill = With(postal, "paint.roads.road.fill", JsonValue.Create("#ffe0a0"));
        var steps = new (string Name, JsonObject Style, string Language, double X, double Y, double Size)[]
        {
            ("first", postal, "en", x, y, 500),
            ("same again", postal, "en", x, y, 500),
            ("colour (road fill)", roadFill, "en", x, y, 500),
            ("ground (blur width 5 -> 10)", With(roadFill, "groundRaster.blur.width", JsonValue.Create(10)), "en", x, y, 500),
            ("shading (blur 5 -> 8)", With(postal, "shade.blur", JsonValue.Create(8)), "en", x, y, 500),
            ("layers (depth bands)", With(postal, "sea.bands.0", JsonValue.Create(0.3)), "en", x, y, 500),
            ("labels (postal size 24.41 -> 28)", With(postal, "labels.postal.size", JsonValue.Create(28)), "en", x, y, 500),
            ("language (ja)", postal, "ja", x, y, 500),
            ("regional (first)", regional, "en", x, y, 500),
            ("region colours (blur 150 -> 200)", With(regional, "regions.blur", JsonValue.Create(200)), "en", x, y, 500),
            ("region colour (city)", With(regional, "regions.colors.city", JsonValue.Create("#e0d0b0")), "en", x, y, 500),
            ("another place (+600 m east)", postal, "en", x + 600, y, 500),
            ("1 km (first)", postal, "en", x, y, 1000),
            ("2 km (first)", postal, "en", x, y, 2000),
            ("4 km (first)", postal, "en", x, y, 4000),
            ("4 km same again", postal, "en", x, y, 4000),
            ("4 km colour (road fill)", roadFill, "en", x, y, 4000),
        };
        int i = 0;
        foreach (var (name, style, lang, px, py, size) in steps)
        {
            var drawing = preview.Draw(Style(style), lang, StylePreview.Window.Around(px, py, size), parallel);
            if (i++ == 0)
                foreach (var ((tx, ty), png) in drawing.Tiles)
                {
                    var dir = Path.Combine(outFolder, "tiles", "8", tx.ToString());
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, $"{ty}.png"), png);
                }
            long bytes = drawing.Tiles.Values.Sum(t => (long)t.Length);
            using var me = Process.GetCurrentProcess();
            Console.WriteLine($"{name,-36} {string.Join("  ", preview.Parts.Select(p => $"{p.Part} {p.Seconds:0.000}"))}  ({drawing.Tiles.Count} tiles, {bytes / 1024:N0} KB; memory {me.WorkingSet64 >> 20:N0} MB, peak {me.PeakWorkingSet64 >> 20:N0} MB)");
        }
        var sw = Stopwatch.StartNew();
        var pick = preview.Pick(Style(roadFill), "en", StylePreview.Window.Around(x, y, 4000), x, y, parallel);
        Console.WriteLine($"{"pick (4 km window)",-36} all {sw.Elapsed.TotalSeconds:0.000}  ({pick.Steps.Count} steps: {string.Join(", ", pick.Steps.Select(s => s.Tag.Kind))})");
        return 0;
    }
}
