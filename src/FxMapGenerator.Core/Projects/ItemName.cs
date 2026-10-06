using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FxMapGenerator.Core.Projects;

/// <summary>
/// The name of something a project keeps (a POI folder, group or style, a style, a group of road edits): one text in
/// the user's own words (<see cref="Text"/>). What the app bundles carries an English and a Japanese name instead
/// (<see cref="En"/>, <see cref="Ja"/>), shown in the language of the screens; a copy of it takes the name of the screens'
/// language at that moment (<see cref="Copied"/>), and from then on that one. In a file: a text, or <c>{"en", "ja"}</c>.
/// Names are never drawn on the maps (labels are).
/// </summary>
[JsonConverter(typeof(ItemNameConverter))]
public sealed record ItemName(string? Text, string? En = null, string? Ja = null)
{
    public static ItemName Of(string text) => new(text);

    /// <summary>A bundled name in both languages.</summary>
    public static ItemName Bundled(string en, string? ja) => new(null, en, string.IsNullOrEmpty(ja) ? null : ja);

    /// <summary>A bundled name (English and Japanese) rather than one text.</summary>
    public bool IsBundled => Text is null;

    /// <summary>The name as a screen in <paramref name="language"/> shows it (a bundled name's English one when it has no Japanese one).</summary>
    public string In(string language) => Text ?? (language == "ja" && !string.IsNullOrEmpty(Ja) ? Ja : En ?? "");

    /// <summary>The name a copy takes: one text, a bundled name's in <paramref name="language"/>.</summary>
    public ItemName Copied(string language) => IsBundled ? Of(In(language)) : this;

    /// <summary>Empty (no text, no English name).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text ?? En);

    public override string ToString() => In("en");

    /// <summary>A name as a file holds it: a text, or an object of <c>en</c> (needed) and <c>ja</c>; null when it is neither.</summary>
    public static ItemName? FromNode(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return Of(s);
        if (node is JsonObject o && o["en"] is JsonValue ev && ev.TryGetValue<string>(out var en))
            return Bundled(en, o["ja"] is JsonValue jv && jv.TryGetValue<string>(out var ja) ? ja : null);
        return null;
    }

    /// <summary>The name as a file holds it.</summary>
    public JsonNode ToNode()
    {
        if (Text is not null) return JsonValue.Create(Text)!;
        var o = new JsonObject { ["en"] = En ?? "" };
        if (!string.IsNullOrEmpty(Ja)) o["ja"] = Ja;
        return o;
    }
}

/// <summary>Reads and writes an <see cref="ItemName"/> as a JSON text or <c>{"en", "ja"}</c>.</summary>
public sealed class ItemNameConverter : JsonConverter<ItemName>
{
    public override ItemName? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var node = JsonNode.Parse(ref reader);
        return ItemName.FromNode(node) ?? throw new JsonException("a name is a text or {\"en\", \"ja\"}");
    }

    public override void Write(Utf8JsonWriter writer, ItemName value, JsonSerializerOptions options) => value.ToNode().WriteTo(writer, options);
}
