using System.Globalization;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.Core.Tests.Styles;

/// <summary>
/// The style editor's table (<c>data/style-schema.json</c>) against the bundled atlas styles and the stages' own rules:
/// every value of a style has a row, every row finds its value and range, and what a row says saving a change makes the
/// maps rebuild is what the stages decide when that one value changes.
/// </summary>
public sealed class StyleSchemaTests
{
    static readonly string[] Groups = ["general", "ground", "regions", "shade", "water", "buildings", "roads", "labels", "zones"];
    static readonly string[] Controls = ["color", "number", "choice", "check", "checks", "font", "text", "table"];
    static readonly string[] Units = ["m", "m2", "deg", "px", "em", "percent"];
    static readonly string[] Rebuilds = ["cells", "cells.prep", "mapData.regions", "mapData.labels"];

    /// <summary>The places a table's columns hold, by the table row's key (the screen's table editors know the same).</summary>
    static readonly Dictionary<string, string[]> TablePlaces = new()
    {
        ["regions.zones"] = ["regions.zones", "buildings.byZone", "labels.postal.sizeByZone"],
        ["sea.bands"] = ["sea.bands", "paint.sea.sand", "paint.sea.rock", "paint.sea.opacity"],
        ["shade.lights"] = ["shade.lights"],
        ["labels.street.expand"] = ["labels.street.expand"],
    };

    /// <summary>The values of an atlas style no row edits: the file's own, the kind of ground (fixed by the style made from), the tree canopy (not on the screen).</summary>
    static readonly string[] NotOnScreen = ["format", "id", "name", "groundRaster.mode", "canopy"];

    static JsonObject Schema() => JsonNode.Parse(StyleSchema.Bytes)!.AsObject();

    static IEnumerable<(string Group, JsonObject Item)> Items() =>
        Schema()["groups"]!.AsArray().SelectMany(g => g!["items"]!.AsArray().Select(i => (g["id"]!.GetValue<string>(), i!.AsObject())));

    static string Key(JsonObject item) => item["key"]!.GetValue<string>();

    static bool Shown(JsonObject item, JsonObject style) =>
        item["when"] is not JsonObject w || StyleChanges.At(style, w["key"]!.GetValue<string>())?.ToString() == w["equals"]!.GetValue<string>();

    static HashSet<string> Marks(JsonObject o) => o["rebuilds"]!.AsArray().Select(r => r!.GetValue<string>()).ToHashSet();

    public static TheoryData<string> AtlasStyles => new(AtlasPresets.BuiltIn);

    [Fact]
    public void TheTableIsWellFormed()
    {
        var schema = Schema();
        Assert.Equal(1, schema["format"]!.GetValue<int>());
        Assert.Equal(Groups, schema["groups"]!.AsArray().Select(g => g!["id"]!.GetValue<string>()));
        foreach (var g in schema["groups"]!.AsArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(g!["name"]!["en"]?.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(g["name"]!["ja"]?.GetValue<string>()));
        }
        foreach (var (_, item) in Items())
        {
            var where = Key(item);
            CheckEntry(item, where);
            if (item["heading"] is JsonObject h)
            {
                Assert.False(string.IsNullOrWhiteSpace(h["en"]?.GetValue<string>()), where);
                Assert.False(string.IsNullOrWhiteSpace(h["ja"]?.GetValue<string>()), where);
            }
            if (item["control"]!.GetValue<string>() == "table")
            {
                Assert.True(TablePlaces.ContainsKey(where), $"{where}: a table the screen has no editor for");
                var columns = item["columns"]!.AsArray().Select(c => c!.AsObject()).ToList();
                Assert.NotEmpty(columns);
                foreach (var c in columns) CheckEntry(c, $"{where} / {c["id"]}");
                // the row's marks are those of its columns together
                Assert.Equal(columns.SelectMany(Marks).ToHashSet(), Marks(item));
            }
            else Assert.Null(item["columns"]);
        }
    }

    /// <summary>A row or a column: names and help in both languages, a known control, range and choices where it needs them, known marks.</summary>
    static void CheckEntry(JsonObject o, string where)
    {
        foreach (var field in new[] { "name", "description" })
        {
            Assert.False(string.IsNullOrWhiteSpace(o[field]?["en"]?.GetValue<string>()), $"{where}: {field}.en");
            Assert.False(string.IsNullOrWhiteSpace(o[field]?["ja"]?.GetValue<string>()), $"{where}: {field}.ja");
        }
        var control = o["control"]!.GetValue<string>();
        Assert.Contains(control, Controls);
        if (control == "number")
        {
            double min = o["min"]!.GetValue<double>(), max = o["max"]!.GetValue<double>(), step = o["step"]!.GetValue<double>();
            Assert.True(min < max && step > 0 && step <= max - min, where);
        }
        else Assert.True(o["min"] is null && o["max"] is null && o["step"] is null, $"{where}: a range on a {control}");
        if (o["unit"] is { } unit) Assert.Contains(unit.GetValue<string>(), Units);
        if (control is "choice" or "checks")
        {
            var choices = o["choices"]!.AsArray().Select(c => c!.AsObject()).ToList();
            Assert.True(choices.Count >= 2, where);
            Assert.Equal(choices.Count, choices.Select(c => c["value"]!.GetValue<string>()).Distinct().Count());
            foreach (var c in choices)
            {
                Assert.False(string.IsNullOrWhiteSpace(c["name"]?["en"]?.GetValue<string>()), where);
                Assert.False(string.IsNullOrWhiteSpace(c["name"]?["ja"]?.GetValue<string>()), where);
            }
        }
        else Assert.Null(o["choices"]);
        Assert.All(Marks(o), m => Assert.Contains(m, Rebuilds));
    }

    [Theory]
    [MemberData(nameof(AtlasStyles))]
    public void EveryRowShownFindsItsValueInItsRange(string id)
    {
        var style = MapStyle.Builtin(id).Source;
        var shownKeys = new List<string>();
        foreach (var (_, item) in Items().Where(i => Shown(i.Item, style)))
        {
            var key = Key(item);
            shownKeys.Add(key);
            var value = StyleChanges.At(style, key);
            var control = item["control"]!.GetValue<string>();
            if (control == "text") continue;                                     // a text may be missing (no credit)
            Assert.True(value is not null, $"{id}: no value at {key}");
            switch (control)
            {
                case "number":
                    double v = value!.GetValue<double>();
                    Assert.InRange(v, item["min"]!.GetValue<double>(), item["max"]!.GetValue<double>());
                    break;
                case "choice":
                    Assert.Contains(value!.GetValue<string>(), item["choices"]!.AsArray().Select(c => c!["value"]!.GetValue<string>()));
                    break;
                case "checks":
                    var values = item["choices"]!.AsArray().Select(c => c!["value"]!.GetValue<string>()).ToList();
                    Assert.All(value!.AsArray(), x => Assert.Contains(x!.GetValue<string>(), values));
                    break;
                case "color":
                    Assert.Matches("^#[0-9a-f]{6}$", value!.GetValue<string>());
                    break;
            }
        }
        // one row a place: rows of the same key never show together
        Assert.Equal(shownKeys.Count, shownKeys.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(AtlasStyles))]
    public void EveryValueOfTheStyleHasARow(string id)
    {
        var style = MapStyle.Builtin(id).Source;
        var keys = Items().Select(i => Key(i.Item)).ToList();
        var places = keys.Concat(TablePlaces.Values.SelectMany(p => p)).ToList();
        foreach (var leaf in Leaves(style, ""))
        {
            bool covered = places.Any(p => leaf == p || leaf.StartsWith(p + ".", StringComparison.Ordinal) || p.StartsWith(leaf + ".", StringComparison.Ordinal))
                || NotOnScreen.Any(p => leaf == p || leaf.StartsWith(p + ".", StringComparison.Ordinal));
            Assert.True(covered, $"{id}: no row for {leaf}");
        }
    }

    /// <summary>The places of a style's values: objects looked into, a list or plain value one place.</summary>
    static IEnumerable<string> Leaves(JsonNode? node, string prefix) => node is JsonObject o
        ? o.SelectMany(kv => Leaves(kv.Value, prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key))
        : [prefix];

    /// <summary>
    /// The rows and table columns shown for the style, each with the style changed in that one value: what the row says
    /// saving it rebuilds must be what the stages decide (the cells' drawing digest, the cell data's needs, the region
    /// colours' section, the labels' placement key).
    /// </summary>
    [Theory]
    [MemberData(nameof(AtlasStyles))]
    public void EveryRowsMarksAreWhatTheStagesDecide(string id)
    {
        var source = MapStyle.Builtin(id).Source;
        var before = MapStyle.Builtin(id);
        int checkedCount = 0;
        foreach (var (_, item) in Items().Where(i => Shown(i.Item, source)))
        {
            var key = Key(item);
            if (item["control"]!.GetValue<string>() == "table")
                foreach (var column in item["columns"]!.AsArray().Select(c => c!.AsObject()))
                {
                    var changed = ChangeTable(source, key, column["id"]!.GetValue<string>());
                    Assert.Equal(Marks(column), RebuildsOf(before, Parse(changed, $"{key} / {column["id"]}")));
                    checkedCount++;
                }
            else
            {
                var changed = StyleChanges.With(source, key, OtherValue(item, StyleChanges.At(source, key)));
                var marks = Marks(item);
                Assert.True(marks.SetEquals(RebuildsOf(before, Parse(changed, key))),
                    $"{id} {key}: the row says [{string.Join(", ", marks)}], the stages [{string.Join(", ", RebuildsOf(before, Parse(changed, key)))}]");
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 90, $"{id}: only {checkedCount} rows checked");
    }

    static MapStyle Parse(JsonObject style, string what) => MapStyle.Parse(style.ToJsonString(), what);

    /// <summary>What the stages make again when a style becomes another: its cells' drawing, cell data, region colours, labels.</summary>
    static HashSet<string> RebuildsOf(MapStyle before, MapStyle after)
    {
        var s = new HashSet<string>();
        if (CellDrawStage.DrawingText(before) != CellDrawStage.DrawingText(after)) s.Add("cells");
        var (sets1, shade1, grounds1) = CellPrepStage.Needs([before]);
        var (sets2, shade2, grounds2) = CellPrepStage.Needs([after]);
        bool groundsSame = grounds1.Count == grounds2.Count && grounds1.All(kv => grounds2.TryGetValue(kv.Key, out var v) && JsonNode.DeepEquals(kv.Value, v));
        bool shadesSame = shade1.Count == shade2.Count && shade1.Zip(shade2).All(p => JsonNode.DeepEquals(p.First, p.Second));
        if (!sets1.SequenceEqual(sets2) || !shadesSame || !groundsSame) s.Add("cells.prep");
        if (after.GroundRaster?.Mode == "regions" && !JsonNode.DeepEquals(before.Source["regions"], after.Source["regions"])) s.Add("mapData.regions");
        if (before.Labels?.PlacementKey != after.Labels?.PlacementKey) s.Add("mapData.labels");
        return s;
    }

    /// <summary>A valid value of the row other than <paramref name="now"/>.</summary>
    static JsonNode OtherValue(JsonObject item, JsonNode? now)
    {
        switch (item["control"]!.GetValue<string>())
        {
            case "color":
                return OtherColor(now!.GetValue<string>());
            case "number":
            {
                double v = now!.GetValue<double>(), step = item["step"]!.GetValue<double>(), max = item["max"]!.GetValue<double>();
                return JsonValue.Create(v + step <= max ? v + step : v - step);
            }
            case "choice":
            {
                var v = now!.GetValue<string>();
                return JsonValue.Create(item["choices"]!.AsArray().Select(c => c!["value"]!.GetValue<string>()).First(c => c != v));
            }
            case "checks":
            {
                var chosen = now!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                var all = item["choices"]!.AsArray().Select(c => c!["value"]!.GetValue<string>()).ToList();
                var missing = all.FirstOrDefault(c => !chosen.Contains(c));
                var next = missing is not null ? chosen.Append(missing).ToList() : chosen.Skip(1).ToList();
                return new JsonArray(next.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
            }
            case "check":
                return JsonValue.Create(!now!.GetValue<bool>());
            case "font":
                return JsonValue.Create(now?.GetValue<string>() == "Arial" ? "Segoe UI" : "Arial");
            case "text":
                return JsonValue.Create((now?.GetValue<string>() ?? "") + " (changed)");
            default:
                throw new InvalidOperationException(item["control"]!.GetValue<string>());
        }
    }

    static JsonNode OtherColor(string hex)
    {
        uint v = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture) ^ 0x102030;
        return JsonValue.Create($"#{v:x6}");
    }

    /// <summary>The style with one cell of a table's column changed (the first row, or the last band's depth).</summary>
    static JsonObject ChangeTable(JsonObject style, string table, string column)
    {
        switch (table, column)
        {
            case ("shade.lights", "azimuth"):
                return StyleChanges.With(style, "shade.lights.0.azimuth", JsonValue.Create(StyleChanges.At(style, "shade.lights.0.azimuth")!.GetValue<double>() + 1));
            case ("shade.lights", "weight"):
                return StyleChanges.With(style, "shade.lights.0.weight", JsonValue.Create(StyleChanges.At(style, "shade.lights.0.weight")!.GetValue<double>() + 0.05));
            case ("sea.bands", "depth"):
            {
                var bands = style["sea"]!["bands"]!.AsArray();
                return StyleChanges.With(style, $"sea.bands.{bands.Count - 1}", JsonValue.Create(bands[^1]!.GetValue<double>() + 10));
            }
            case ("sea.bands", "sand"):
                return StyleChanges.With(style, "paint.sea.sand.0", OtherColor(StyleChanges.At(style, "paint.sea.sand.0")!.GetValue<string>()));
            case ("sea.bands", "rock"):
                return StyleChanges.With(style, "paint.sea.rock.0", OtherColor(StyleChanges.At(style, "paint.sea.rock.0")!.GetValue<string>()));
            case ("sea.bands", "opacity"):
            {
                double o = StyleChanges.At(style, "paint.sea.opacity.0")!.GetValue<double>();
                return StyleChanges.With(style, "paint.sea.opacity.0", JsonValue.Create(o >= 0.5 ? o - 0.25 : o + 0.25));
            }
            case ("labels.street.expand", "short"):
            {
                var expand = style["labels"]!["street"]!["expand"]!.AsObject();
                var first = expand.First();
                var changed = StyleChanges.With(style, "labels.street.expand." + first.Key, null);
                return StyleChanges.With(changed, "labels.street.expand." + first.Key + "x", first.Value!.DeepClone());
            }
            case ("labels.street.expand", "long"):
            {
                var first = style["labels"]!["street"]!["expand"]!.AsObject().First();
                return StyleChanges.With(style, "labels.street.expand." + first.Key, JsonValue.Create(first.Value!.GetValue<string>() + "s"));
            }
            case ("regions.zones", "region"):
            {
                var first = style["regions"]!["zones"]!.AsObject().First();
                var other = style["regions"]!["colors"]!.AsObject().Select(kv => kv.Key).First(r => r != first.Value!.GetValue<string>());
                return StyleChanges.With(style, "regions.zones." + first.Key, JsonValue.Create(other));
            }
            case ("regions.zones", "building"):
            {
                var first = style["buildings"]!["byZone"]!.AsObject().First();
                var other = style["paint"]!["buildings"]!.AsObject().Select(kv => kv.Key).First(c => c != first.Value!.GetValue<string>());
                return StyleChanges.With(style, "buildings.byZone." + first.Key, JsonValue.Create(other));
            }
            case ("regions.zones", "postal"):
            {
                var first = style["labels"]!["postal"]!["sizeByZone"]!.AsObject().First();
                return StyleChanges.With(style, "labels.postal.sizeByZone." + first.Key, JsonValue.Create(first.Value!.GetValue<double>() + 1));
            }
            default:
                throw new InvalidOperationException($"no change for the table {table}, column {column}");
        }
    }
}
