using System.Text.Json.Nodes;

namespace FxMapGenerator.Core.Styles;

/// <summary>
/// A style told as the changes of another (a user style over its bundled style): the changes sit at the same places as
/// in a style file; objects are put together key by key, any other value (lists too) takes the place of the one below
/// as a whole, and null takes that key away. An object whose order counts (<see cref="Whole"/>) goes whole, like a list.
/// </summary>
public static class StyleChanges
{
    /// <summary>
    /// The objects taken as one value: their order counts (the street names' short forms are written out in the table's
    /// order), so a change of any of it keeps the whole table in its order.
    /// </summary>
    public static readonly IReadOnlySet<string> Whole = new HashSet<string>(StringComparer.Ordinal) { "labels.street.expand" };

    /// <summary>A copy of <paramref name="below"/> with <paramref name="changes"/> put over it.</summary>
    public static JsonObject Apply(JsonObject below, JsonObject changes)
    {
        var o = below.DeepClone().AsObject();
        Into(o, changes, "");
        return o;
    }

    static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

    static void Into(JsonObject target, JsonObject changes, string path)
    {
        foreach (var (key, value) in changes)
        {
            var at = Join(path, key);
            if (value is null) target.Remove(key);
            else if (value is JsonObject co && target[key] is JsonObject to && !Whole.Contains(at)) Into(to, co, at);
            else target[key] = value.DeepClone();
        }
    }

    /// <summary>
    /// The changes that make <paramref name="style"/> of <paramref name="below"/> (<see cref="Apply"/> of them gives
    /// <paramref name="style"/> back): the keys whose values differ, objects looked into key by key, a key
    /// <paramref name="style"/> does not have null. <paramref name="skip"/> names top-level keys left out (the file's own).
    /// </summary>
    public static JsonObject Between(JsonObject below, JsonObject style, IReadOnlyCollection<string>? skip = null) => Between(below, style, skip, "");

    static JsonObject Between(JsonObject below, JsonObject style, IReadOnlyCollection<string>? skip, string path)
    {
        var o = new JsonObject();
        foreach (var (key, value) in style)
        {
            if (skip?.Contains(key) == true) continue;
            var had = below[key];
            var at = Join(path, key);
            if (value is JsonObject vo && had is JsonObject ho && !Whole.Contains(at))
            {
                var inner = Between(ho, vo, null, at);
                if (inner.Count > 0) o[key] = inner;
            }
            else if (!below.ContainsKey(key) || !Same(had, value, inOrder: Whole.Contains(at))) o[key] = value?.DeepClone();
        }
        foreach (var (key, _) in below)
            if (skip?.Contains(key) != true && !style.ContainsKey(key)) o[key] = null;
        return o;
    }

    /// <summary>
    /// Whether two values are the same: objects key by key, lists item by item, numbers by their value (<c>5.0</c> and
    /// <c>5</c> as a screen sends them back are one value), anything else as its JSON text.
    /// </summary>
    public static bool Same(JsonNode? a, JsonNode? b) => Same(a, b, inOrder: false);

    /// <param name="inOrder">Objects' keys must also come in the same order (a <see cref="Whole"/> object).</param>
    static bool Same(JsonNode? a, JsonNode? b, bool inOrder)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (JsonObject oa, JsonObject ob) when inOrder:
                return oa.Count == ob.Count && oa.Zip(ob).All(p => p.First.Key == p.Second.Key && Same(p.First.Value, p.Second.Value));
            case (JsonObject oa, JsonObject ob):
                return oa.Count == ob.Count && oa.All(kv => ob.ContainsKey(kv.Key) && Same(kv.Value, ob[kv.Key]));
            case (JsonArray la, JsonArray lb):
                return la.Count == lb.Count && la.Zip(lb).All(p => Same(p.First, p.Second));
            case (JsonValue va, JsonValue vb) when va.GetValueKind() == System.Text.Json.JsonValueKind.Number && vb.GetValueKind() == System.Text.Json.JsonValueKind.Number:
                return Number(va) is { } da && Number(vb) is { } db ? da == db : va.ToJsonString() == vb.ToJsonString();
            case (JsonValue va, JsonValue vb):
                return va.ToJsonString() == vb.ToJsonString();
            default:
                return false;
        }
    }

    /// <summary>A number's value from its JSON text (whatever the value is held as), or null when it does not fit.</summary>
    static decimal? Number(JsonValue v) =>
        decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>
    /// The value at a place (<c>paint.roads.road.fill</c>; a number part indexes a list, <c>paint.roads.tunnel.dash.0</c>),
    /// or null when the style has nothing there.
    /// </summary>
    public static JsonNode? At(JsonNode? node, string key)
    {
        foreach (var part in key.Split('.'))
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out int i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
        return node;
    }

    /// <summary>
    /// A copy of <paramref name="style"/> with the value at <paramref name="key"/> set (null takes it away): objects on
    /// the way are made when missing; a number part sets an item of a list that is there.
    /// </summary>
    public static JsonObject With(JsonObject style, string key, JsonNode? value)
    {
        var copy = style.DeepClone().AsObject();
        var parts = key.Split('.');
        JsonNode node = copy;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            JsonNode? next = node switch
            {
                JsonObject o => o[part] ?? (o[part] = new JsonObject()),
                JsonArray a when int.TryParse(part, out int k) && k >= 0 && k < a.Count => a[k],
                _ => null,
            };
            node = next ?? throw new ArgumentException($"no place '{key}' in the style");
        }
        var last = parts[^1];
        switch (node)
        {
            case JsonObject obj:
                if (value is null) obj.Remove(last);
                else obj[last] = value.DeepClone();
                break;
            case JsonArray arr when int.TryParse(last, out int k) && k >= 0 && k < arr.Count && value is not null:
                arr[k] = value.DeepClone();
                break;
            default:
                throw new ArgumentException($"no place '{key}' in the style");
        }
        return copy;
    }
}
