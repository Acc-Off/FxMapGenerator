using System.Globalization;
using System.Text.RegularExpressions;

namespace FxMapGenerator.Core.Capture;

/// <summary>
/// The game's graphics settings for the capture (FiveM keeps them in <c>%APPDATA%\CitizenFX\gta5_settings.xml</c>):
/// the values the shots need, the difference to a settings file, and the file with those values put in. Only the values
/// change; the rest of the file, its layout and line ends stay as they are.
/// </summary>
public static class RenderSettings
{
    /// <param name="Section">graphics or video.</param>
    /// <param name="Why">
    /// detail (texture and shading quality), lod (distant detail), window (the frame the capture expects), still (no blur),
    /// stable (VSync on: the game draws at the screen's rate and leaves the GPU room, so the frame rate stays the same when
    /// other programs use the GPU, and the settle wait measured in the pre-check holds during the visit).
    /// </param>
    public sealed record Entry(string Section, string Key, string Value, string Why);

    public static readonly IReadOnlyList<Entry> ForCapture =
    [
        new("graphics", "TextureQuality", "2", "detail"),
        new("graphics", "ShaderQuality", "2", "detail"),
        new("graphics", "ShadowQuality", "2", "detail"),
        new("graphics", "ReflectionQuality", "2", "detail"),
        new("graphics", "WaterQuality", "2", "detail"),
        new("graphics", "GrassQuality", "2", "detail"),
        new("graphics", "ParticleQuality", "1", "detail"),
        new("graphics", "Tessellation", "3", "detail"),
        new("graphics", "SSAO", "2", "detail"),
        new("graphics", "AnisotropicFiltering", "16", "detail"),
        new("graphics", "FXAA_Enabled", "true", "detail"),
        new("graphics", "MSAA", "4", "detail"),
        new("graphics", "Shader_SSA", "true", "detail"),
        new("graphics", "PostFX", "1", "detail"),
        new("graphics", "SamplingMode", "0", "detail"),
        new("graphics", "LodScale", "1.000000", "lod"),
        new("graphics", "MaxLodScale", "1.000000", "lod"),
        new("graphics", "HdStreamingInFlight", "true", "lod"),
        new("graphics", "DoF", "false", "still"),
        new("graphics", "MotionBlurStrength", "0.000000", "still"),
        new("video", "Windowed", "1", "window"),
        new("video", "ScreenWidth", "1920", "window"),
        new("video", "ScreenHeight", "1080", "window"),
        new("video", "PauseOnFocusLoss", "0", "window"),
        new("video", "VSync", "1", "stable"),
    ];

    /// <param name="Current">The value in the file; null when the file does not have the key.</param>
    public sealed record Difference(string Section, string Key, string? Current, string Wanted, string Why);

    /// <summary>The values of <see cref="ForCapture"/> the file does not have yet.</summary>
    /// <exception cref="InvalidDataException">Not a settings file.</exception>
    public static IReadOnlyList<Difference> Compare(string xml)
    {
        Check(xml);
        var list = new List<Difference>();
        foreach (var e in ForCapture)
        {
            var current = ValueOf(xml, e.Key);
            if (!Same(current, e.Value)) list.Add(new Difference(e.Section, e.Key, current, e.Value, e.Why));
        }
        return list;
    }

    /// <summary>The file with every value of <see cref="ForCapture"/> put in (a missing key goes at the end of its section).</summary>
    /// <exception cref="InvalidDataException">Not a settings file, or a section is missing.</exception>
    public static string Apply(string xml)
    {
        Check(xml);
        string nl = xml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var e in ForCapture)
        {
            var m = KeyPattern(e.Key).Match(xml);
            if (m.Success)
            {
                var g = m.Groups["value"];
                xml = xml[..g.Index] + e.Value + xml[(g.Index + g.Length)..];
                continue;
            }
            var close = new Regex("^([ \\t]*)</" + e.Section + ">", RegexOptions.Multiline).Match(xml);
            if (!close.Success) throw new InvalidDataException($"the settings file has no <{e.Section}> section");
            string indent = close.Groups[1].Value + "  ";
            xml = xml[..close.Index] + $"{indent}<{e.Key} value=\"{e.Value}\" />{nl}" + xml[close.Index..];
        }
        return xml;
    }

    public static string? ValueOf(string xml, string key)
    {
        var m = KeyPattern(key).Match(xml);
        return m.Success ? m.Groups["value"].Value : null;
    }

    static void Check(string xml)
    {
        if (!xml.Contains("<Settings>", StringComparison.Ordinal) || !xml.Contains("<graphics>", StringComparison.Ordinal))
            throw new InvalidDataException("not a game settings file (no <Settings> with <graphics>)");
    }

    /// <summary>Equal as numbers when both are numbers ("1.0" = "1.000000"), else as text ignoring case (true / True).</summary>
    static bool Same(string? current, string wanted)
    {
        if (current is null) return false;
        if (double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(wanted, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return a == b;
        return string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase);
    }

    static Regex KeyPattern(string key) => new("<" + Regex.Escape(key) + "\\s+value=\"(?<value>[^\"]*)\"");
}
