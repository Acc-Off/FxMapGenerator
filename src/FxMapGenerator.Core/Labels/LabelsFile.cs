using System.Text.Json;

namespace FxMapGenerator.Core.Labels;

/// <summary>
/// <c>data/labels-&lt;language&gt;-&lt;key&gt;.json</c>: the labels of one language placed with the labels section whose
/// placement key is <c>key</c> (<see cref="Styles.LabelsStyle.PlacementKey"/>; the maps whose styles share it share the
/// file). Positions in world metres, rotations in degrees, sizes (em) in metres.
/// <code>
/// {"format": 1, "language": "en", "key": "...", "frame": [x0, y0, x1, y1],
///  "labels": [{"kind", "text", "x", "y", "rotation", "size", "font", "weight", "spacing",
///              "chars": [{"char", "x", "y", "rotation", "advance"}, ...] (street names only)}, ...]}
/// </code>
/// </summary>
public static class LabelsFile
{
    public const int Format = 1;

    public static string FileName(string language, string key) => $"labels-{language}-{key}.json";

    public static void Write(string path, string language, string key, (double X0, double Y0, double X1, double Y1) frame, IReadOnlyList<PlacedLabel> labels) =>
        GameFiles.GameFilesOutput.WriteAtomically(path, fs =>
        {
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            w.WriteStartObject();
            w.WriteNumber("format", Format);
            w.WriteString("language", language);
            w.WriteString("key", key);
            w.WriteStartArray("frame");
            foreach (var v in (ReadOnlySpan<double>)[frame.X0, frame.Y0, frame.X1, frame.Y1]) w.WriteNumberValue(v);
            w.WriteEndArray();
            w.WriteStartArray("labels");
            foreach (var l in labels)
            {
                w.WriteStartObject();
                w.WriteString("kind", l.Kind);
                w.WriteString("text", l.Text);
                w.WriteNumber("x", l.X);
                w.WriteNumber("y", l.Y);
                w.WriteNumber("rotation", l.Rotation);
                w.WriteNumber("size", l.Size);
                w.WriteString("font", l.Font);
                w.WriteString("weight", l.Weight);
                w.WriteNumber("spacing", l.Spacing);
                if (l.Chars is { } chars)
                {
                    w.WriteStartArray("chars");
                    foreach (var g in chars)
                    {
                        w.WriteStartObject();
                        w.WriteString("char", g.Char);
                        w.WriteNumber("x", g.X);
                        w.WriteNumber("y", g.Y);
                        w.WriteNumber("rotation", g.Rotation);
                        w.WriteNumber("advance", g.Advance);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

    public static (string Language, string Key, (double X0, double Y0, double X1, double Y1) Frame, List<PlacedLabel> Labels) Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var f = root.GetProperty("frame");
        var labels = new List<PlacedLabel>();
        foreach (var l in root.GetProperty("labels").EnumerateArray())
        {
            List<LabelLines.Glyph>? chars = null;
            if (l.TryGetProperty("chars", out var cs))
                chars = cs.EnumerateArray().Select(g => new LabelLines.Glyph(g.GetProperty("char").GetString()!, g.GetProperty("x").GetDouble(), g.GetProperty("y").GetDouble(),
                    g.GetProperty("rotation").GetDouble(), g.GetProperty("advance").GetDouble())).ToList();
            labels.Add(new PlacedLabel(l.GetProperty("kind").GetString()!, l.GetProperty("text").GetString()!, l.GetProperty("x").GetDouble(), l.GetProperty("y").GetDouble(),
                l.GetProperty("rotation").GetDouble(), l.GetProperty("size").GetDouble(), l.GetProperty("font").GetString()!, l.GetProperty("weight").GetString()!,
                l.GetProperty("spacing").GetDouble(), chars));
        }
        return (root.GetProperty("language").GetString()!, root.GetProperty("key").GetString()!, (f[0].GetDouble(), f[1].GetDouble(), f[2].GetDouble(), f[3].GetDouble()), labels);
    }
}
