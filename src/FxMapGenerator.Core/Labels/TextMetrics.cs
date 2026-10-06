using System.Text;
using SkiaSharp;

namespace FxMapGenerator.Core.Labels;

/// <summary>Sizes of text in world metres (1 m = 1 unit of the font size), as the label placement measures them.</summary>
public interface ITextMetrics
{
    /// <summary>
    /// The width (the advance of the whole text; with <paramref name="spacing"/>, the glyphs' advances plus the spacing
    /// between them) and the height (the ink height of the text, at least 0.6 em).
    /// </summary>
    (double Width, double Height) Size(string text, string font, string weight, double em, double spacing = 0.0);

    /// <summary>The advance of every glyph (one per character, <see cref="Glyphs"/>).</summary>
    double[] Advances(string text, string font, string weight, double em);

    /// <summary>The characters of a text, one string per Unicode scalar value.</summary>
    static List<string> Glyphs(string text) => text.EnumerateRunes().Select(r => r.ToString()).ToList();
}

/// <summary>Text sizes from the fonts installed on this PC (SkiaSharp, no hinting, no kerning).</summary>
public sealed class SkiaTextMetrics : ITextMetrics, IDisposable
{
    readonly Dictionary<(string, string), SKTypeface> faces = new();
    readonly object gate = new();

    /// <summary>Fonts asked for that are not installed (the default font was used for them).</summary>
    public List<string> Missing { get; } = new();

    /// <summary>True when a font family is installed on this PC (what the labels are measured and drawn with; else the default font).</summary>
    public static bool IsInstalled(string family)
    {
        // not disposed: SkiaSharp hands out one object per typeface, which the text measuring may hold as well
        var face = SKTypeface.FromFamilyName(family);
        return face is not null && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase);
    }

    SKFont Font(string font, string weight, double em)
    {
        SKTypeface face;
        lock (gate)
        {
            if (!faces.TryGetValue((font, weight), out face!))
            {
                face = SKTypeface.FromFamilyName(font, weight == "bold" ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
                if (!string.Equals(face.FamilyName, font, StringComparison.OrdinalIgnoreCase) && !Missing.Contains(font)) Missing.Add(font);
                faces[(font, weight)] = face;
            }
        }
        return new SKFont(face, (float)em) { Hinting = SKFontHinting.None, Subpixel = true, LinearMetrics = true, Edging = SKFontEdging.Antialias };
    }

    public (double Width, double Height) Size(string text, string font, string weight, double em, double spacing = 0.0)
    {
        using var f = Font(font, weight, em);
        double w;
        if (spacing != 0)
        {
            var glyphs = ITextMetrics.Glyphs(text);
            w = glyphs.Sum(g => (double)f.MeasureText(g)) + spacing * (glyphs.Count - 1);
        }
        else w = f.MeasureText(text);
        f.MeasureText(text, out var bounds);
        return (w, Math.Max(bounds.Height, 0.6 * em));
    }

    public double[] Advances(string text, string font, string weight, double em)
    {
        using var f = Font(font, weight, em);
        return ITextMetrics.Glyphs(text).Select(g => (double)f.MeasureText(g)).ToArray();
    }

    public void Dispose()
    {
        foreach (var face in faces.Values) face.Dispose();
        faces.Clear();
    }
}
