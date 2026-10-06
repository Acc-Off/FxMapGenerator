using System.Text.Json;
using FxMapGenerator.Core.Geometry;

namespace FxMapGenerator.Core.RoadGraph;

/// <summary>
/// <c>data/roads.json</c>: the road graph (<c>Docs/spec/project-format.ja.md</c>).
/// <code>
/// {"roads": [{"class", "width", "divided", "street", "oneWay", "length", "lanes": [forward, back], "points": [[x, y], ...]}, ...]}
/// </code>
/// Widths and points are rounded to 0.01 m, the length to 0.1 m and the lanes to 0.1 (on the exact value, a tie to the
/// even digit).
/// </summary>
public static class RoadGraphFile
{
    public const string Roads = "roads.json";

    public sealed record Road(RoadClass Class, double Width, bool Divided, uint Street, bool OneWay, double Length,
        double LanesForward, double LanesBack, IReadOnlyList<P2> Points);

    public static void Write(string path, IReadOnlyList<MapRoad> roads) =>
        GameFiles.GameFilesOutput.WriteAtomically(path, fs =>
        {
            using var w = new Utf8JsonWriter(fs);
            w.WriteStartObject();
            w.WriteStartArray("roads");
            foreach (var r in roads)
            {
                w.WriteStartObject();
                w.WriteString("class", r.Class.Name());
                w.WriteNumber("width", Num.Round(r.Width, 2));
                w.WriteBoolean("divided", r.Bundle);
                w.WriteNumber("street", r.Attr.Street);
                w.WriteBoolean("oneWay", r.Attr.OneWay);
                w.WriteNumber("length", Num.Round(r.Length, 1));
                w.WriteStartArray("lanes");
                w.WriteNumberValue(Num.Round(r.Attr.LanesForward, 1));
                w.WriteNumberValue(Num.Round(r.Attr.LanesBack, 1));
                w.WriteEndArray();
                w.WriteStartArray("points");
                foreach (var p in r.Points)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(Num.Round(p.X, 2));
                    w.WriteNumberValue(Num.Round(p.Y, 2));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

    public static IReadOnlyList<Road> Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var o = new List<Road>();
        foreach (var r in doc.RootElement.GetProperty("roads").EnumerateArray())
        {
            var lanes = r.GetProperty("lanes");
            o.Add(new Road(RoadClasses.Parse(r.GetProperty("class").GetString()!), r.GetProperty("width").GetDouble(), r.GetProperty("divided").GetBoolean(),
                r.GetProperty("street").GetUInt32(), r.GetProperty("oneWay").GetBoolean(), r.GetProperty("length").GetDouble(),
                lanes[0].GetDouble(), lanes[1].GetDouble(),
                r.GetProperty("points").EnumerateArray().Select(p => new P2(p[0].GetDouble(), p[1].GetDouble())).ToList()));
        }
        return o;
    }
}
