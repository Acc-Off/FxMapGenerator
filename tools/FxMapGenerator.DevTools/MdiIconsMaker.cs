using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FxMapGenerator.DevTools;

/// <summary>
/// Writes <c>data/mdi-icons.json</c> from the npm package <c>@mdi/svg</c> (Material Design Icons by Pictogrammers,
/// Apache License 2.0): every icon that is not deprecated, by name, with its path data (a 24 x 24 box, one path per
/// icon), its aliases and its tags (for the icon search), and the package's version.
/// </summary>
public static partial class MdiIconsMaker
{
    [GeneratedRegex("<path[^>]*\\sd=\"([^\"]+)\"")]
    private static partial Regex PathData();

    [GeneratedRegex("<(circle|rect|g|polygon|ellipse|line)\\b")]
    private static partial Regex OtherShapes();

    /// <returns>The number of icons written.</returns>
    public static int Write(string package, string outFile)
    {
        var pkg = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "package.json")))!;
        if (pkg["name"]?.GetValue<string>() != "@mdi/svg") throw new InvalidOperationException($"{package} is not the @mdi/svg package");
        string version = pkg["version"]!.GetValue<string>();
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "meta.json")))!.AsArray();
        var icons = new SortedDictionary<string, (string Path, List<string> Aliases, List<string> Tags)>(StringComparer.Ordinal);
        foreach (var m in meta)
        {
            if (m!["deprecated"]?.GetValue<bool>() == true) continue;
            string name = m["name"]!.GetValue<string>();
            string svg = File.ReadAllText(Path.Combine(package, "svg", name + ".svg"));
            if (!svg.Contains("viewBox=\"0 0 24 24\"", StringComparison.Ordinal)) throw new InvalidOperationException($"{name}: not a 24 x 24 icon");
            var paths = PathData().Matches(svg);
            if (paths.Count != 1 || OtherShapes().IsMatch(svg)) throw new InvalidOperationException($"{name}: not one path");
            icons[name] = (paths[0].Groups[1].Value,
                m["aliases"]?.AsArray().Select(a => a!.GetValue<string>()).ToList() ?? [],
                m["tags"]?.AsArray().Select(a => a!.GetValue<string>()).ToList() ?? []);
        }
        // one icon a line, so that a later version's changes read as lines
        var sb = new StringBuilder();
        sb.Append("{\n  \"format\": 1,\n  \"version\": ").Append(JsonSerializer.Serialize(version)).Append(",\n  \"icons\": {\n");
        int i = 0;
        foreach (var (name, (path, aliases, tags)) in icons)
        {
            sb.Append("    ").Append(JsonSerializer.Serialize(name)).Append(": {\"path\": ").Append(JsonSerializer.Serialize(path))
              .Append(", \"aliases\": ").Append(JsonSerializer.Serialize(aliases)).Append(", \"tags\": ").Append(JsonSerializer.Serialize(tags)).Append('}')
              .Append(++i < icons.Count ? ",\n" : "\n");
        }
        sb.Append("  }\n}\n");
        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
        return icons.Count;
    }
}
