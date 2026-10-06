using System.Text.Json;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Poi;

/// <summary>
/// The icons a POI style can draw (<c>data/mdi-icons.json</c>): Material Design Icons by Pictogrammers (Apache License
/// 2.0), made from the npm package <c>@mdi/svg</c> by <c>devtools mdi-icons</c>. Each icon is one SVG path in a 24 x 24
/// box, drawn with <c>SKPath.ParseSvgPathData</c>; the aliases and tags are for the screen's icon search.
/// </summary>
public static class MdiIcons
{
    public const string FileName = "mdi-icons.json";
    /// <summary>The side of the box the paths are drawn in.</summary>
    public const double Box = 24.0;

    static readonly Lazy<byte[]> _bytes = new(() =>
    {
        using var s = EmbeddedData.Open(FileName);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    });

    static readonly Lazy<(string Version, Dictionary<string, string> Paths)> _data = new(() =>
    {
        using var doc = JsonDocument.Parse(_bytes.Value);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var icon in doc.RootElement.GetProperty("icons").EnumerateObject())
            paths[icon.Name] = icon.Value.GetProperty("path").GetString()!;
        return (doc.RootElement.GetProperty("version").GetString()!, paths);
    });

    /// <summary>The file as bundled (for the screen's icon list).</summary>
    public static byte[] Bytes => _bytes.Value;

    /// <summary>The version of <c>@mdi/svg</c> the icons come from.</summary>
    public static string Version => _data.Value.Version;

    /// <summary>The line the credits of an exported map carry when it draws any of the icons.</summary>
    public static string Credit => $"Icons: Material Design Icons by Pictogrammers (@mdi/svg {Version}), https://pictogrammers.com/library/mdi/ (Apache License 2.0).";

    public static int Count => _data.Value.Paths.Count;

    /// <summary>The SVG path data of an icon, or null when there is no icon of that name.</summary>
    public static string? PathOf(string name) => _data.Value.Paths.GetValueOrDefault(name);
}
