using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Poi;

/// <summary>
/// Points read from a file to add to the POI data as a new group: a CSV (the first line names the columns: <c>x</c> and
/// <c>y</c> needed, <c>label</c> (the English label, the text the maps draw), <c>labelJa</c> (the Japanese label),
/// <c>name</c> (the name in the lists), <c>color</c>, <c>size</c> may be there, other columns are not read; comma
/// separated, UTF-8 with or without a BOM) or a JSON file (a group file of this app, or a list of points in the same form).
/// Positions are the game's x and y (m); a line off the map or not readable is left out with its reason.
/// </summary>
public static class PoiImport
{
    /// <summary>
    /// Why a line is left out, for the screen to say in its words: <c>x</c> / <c>y</c> (not a number), <c>offMap</c> (off the
    /// project's frame; the value: "x, y"), <c>color</c> (not <c>#rrggbb</c>; the value), <c>size</c> (not a positive number); for the file:
    /// <c>empty</c>, <c>columns</c> (the first line lacks x or y), <c>json</c> (not JSON; the value: the parser's message),
    /// <c>list</c> (neither a list of points nor a group file), <c>point</c> (not an object).
    /// </summary>
    public sealed record Reason(string Code, string? Value = null)
    {
        public override string ToString() => Value is null ? Code : $"{Code} ({Value})";
    }

    /// <summary>A line (1-based, of the CSV; the point's place in the list for JSON; 0 for the file) left out, and why.</summary>
    public sealed record Skipped(int Line, IReadOnlyList<Reason> Reasons);

    public sealed record Result(List<PoiEditPoint> Points, List<Skipped> Skipped);

    /// <summary>Reads a file by its extension (<c>.csv</c>, else JSON); points off <paramref name="frame"/> are left out.</summary>
    public static Result Read(string path, MapFrame frame = default) => Read(File.ReadAllText(path), Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase), frame);

    /// <summary>Reads the text of a file (CSV or JSON); points off <paramref name="frame"/> (the project's) are left out.</summary>
    public static Result Read(string text, bool csv, MapFrame frame = default)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return csv ? Csv(text, frame) : Json(text, frame);
    }

    // ------------------------------------------------------------------------------------------------ CSV

    static Result Csv(string text, MapFrame frame)
    {
        var rows = Rows(text);
        var points = new List<PoiEditPoint>();
        var skipped = new List<Skipped>();
        if (rows.Count == 0) { skipped.Add(new(1, [new("empty")])); return new(points, skipped); }
        var head = rows[0].Fields.Select(h => h.Trim()).ToList();
        int Col(string name) => head.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int cx = Col("x"), cy = Col("y"), cLabel = Col("label"), cJa = Col("labelJa"), cName = Col("name"), cColor = Col("color"), cSize = Col("size");
        if (cx < 0 || cy < 0) { skipped.Add(new(1, [new("columns")])); return new(points, skipped); }
        foreach (var (line, fields) in rows.Skip(1))
        {
            if (fields.All(f => f.Trim().Length == 0)) continue;
            string F(int c) => c >= 0 && c < fields.Count ? fields[c].Trim() : "";
            var reasons = new List<Reason>();
            double? x = Number(F(cx)), y = Number(F(cy));
            if (x is null) reasons.Add(new("x"));
            if (y is null) reasons.Add(new("y"));
            string? color = F(cColor) is { Length: > 0 } c ? c : null;
            if (color is not null && !IsColour(color)) reasons.Add(new("color", color));
            double? size = F(cSize) is { Length: > 0 } s ? Number(s) : null;
            if (F(cSize).Length > 0 && size is not > 0) reasons.Add(new("size"));
            if (x is { } px && y is { } py && !frame.Contains(px, py)) reasons.Add(new("offMap", FormattableString.Invariant($"{px}, {py}")));
            if (reasons.Count > 0) { skipped.Add(new(line, reasons)); continue; }
            var label = new Dictionary<string, string>(StringComparer.Ordinal);
            if (F(cLabel).Length > 0) label["en"] = F(cLabel);
            if (F(cJa).Length > 0) label["ja"] = F(cJa);
            points.Add(new PoiEditPoint((points.Count + 1).ToString(CultureInfo.InvariantCulture), F(cName), label, x!.Value, y!.Value,
                Color: color?.ToLowerInvariant(), Size: size));
        }
        return new(points, skipped);
    }

    /// <summary>The rows of a CSV with the line each starts on: fields in double quotes may hold commas, line breaks and doubled quotes.</summary>
    static List<(int Line, List<string> Fields)> Rows(string text)
    {
        var rows = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        int line = 1, start = 1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': fields.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    rows.Add((start, fields));
                    fields = new List<string>();
                    any = false;
                    start = ++line;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (any || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add((start, fields));
        }
        return rows;
    }

    // ------------------------------------------------------------------------------------------------ JSON

    static Result Json(string text, MapFrame frame)
    {
        var points = new List<PoiEditPoint>();
        var skipped = new List<Skipped>();
        JsonNode? root;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { skipped.Add(new(0, [new("json", ex.Message)])); return new(points, skipped); }
        var list = root as JsonArray ?? (root as JsonObject)?["points"] as JsonArray;
        if (list is null) { skipped.Add(new(0, [new("list")])); return new(points, skipped); }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int n = 0;
        foreach (var node in list)
        {
            n++;
            if (node is not JsonObject p) { skipped.Add(new(n, [new("point")])); continue; }
            var reasons = new List<Reason>();
            double? x = Num(p["x"]), y = Num(p["y"]);
            if (x is null) reasons.Add(new("x"));
            if (y is null) reasons.Add(new("y"));
            if (x is { } px && y is { } py && !frame.Contains(px, py)) reasons.Add(new("offMap", FormattableString.Invariant($"{px}, {py}")));
            string name = p["name"] is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns : "";
            // the label per language, or one text as the English label
            var label = p["label"] switch
            {
                JsonObject o => o.Where(kv => AtlasPresets.Languages.Contains(kv.Key) && kv.Value is JsonValue v && v.TryGetValue<string>(out var t) && t.Length > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal),
                JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = s },
                _ => new Dictionary<string, string>(StringComparer.Ordinal),
            };
            string? color = p["color"] is JsonValue cv && cv.TryGetValue<string>(out var c) ? c : null;
            if (color is not null && !IsColour(color)) reasons.Add(new("color", color));
            double? size = p["size"] is null ? null : Num(p["size"]);
            if (p["size"] is not null && size is not > 0) reasons.Add(new("size"));
            if (reasons.Count > 0) { skipped.Add(new(n, reasons)); continue; }
            // the file's own id when there is one and it is free, else the next number
            string? id = p["id"] is JsonValue iv && iv.TryGetValue<string>(out var own) && own.Length > 0 && !ids.Contains(own) ? own : null;
            if (id is null)
            {
                int k = points.Count + 1;
                while (ids.Contains(k.ToString(CultureInfo.InvariantCulture))) k++;
                id = k.ToString(CultureInfo.InvariantCulture);
            }
            ids.Add(id);
            points.Add(new PoiEditPoint(id, name, label, x!.Value, y!.Value, Color: color?.ToLowerInvariant(), Size: size));
        }
        return new(points, skipped);
    }

    static double? Num(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d
        : n is JsonValue s && s.TryGetValue<string>(out var t) ? Number(t) : null;

    static double? Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    static bool IsColour(string s)
    {
        try { Rgb.Parse(s); return true; }
        catch (Exception) { return false; }
    }


}
