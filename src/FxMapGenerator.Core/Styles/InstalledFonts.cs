using SkiaSharp;

namespace FxMapGenerator.Core.Styles;

/// <summary>The font families this PC has, as the drawing finds them by name (a style's labels name their fonts so).</summary>
public static class InstalledFonts
{
    static readonly Lazy<IReadOnlyList<string>> Families = new(() =>
        SKFontManager.Default.GetFontFamilies().Where(f => !string.IsNullOrWhiteSpace(f) && !f.StartsWith('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList());

    /// <summary>The families in name order (vertical-writing variants, named with a leading @, left out).</summary>
    public static IReadOnlyList<string> Names() => Families.Value;
}
