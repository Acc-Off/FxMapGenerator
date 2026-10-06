using System.Globalization;

namespace FxMapGenerator.Core.Capture;

/// <summary>
/// A line printed by the fxmapgen-capture resource: <c>[fxmapgen] WORD key=value ...</c> (protocol 1, listed at the top
/// of the resource's <c>client/link.lua</c>). Lines for the program begin with an upper-case word (READY, FAIL, HMAP,
/// HELLO, STATUS, ENV, SAFE, RES, REFILL, ERROR); the other lines of the resource are notes for the log.
/// </summary>
public sealed class ProtocolLine
{
    public const string Prefix = "[fxmapgen] ";

    ProtocolLine(string text, string body, string word, Dictionary<string, string> values)
    {
        Text = text;
        Body = body;
        Word = word;
        Values = values;
    }

    /// <summary>The whole line.</summary>
    public string Text { get; }

    /// <summary>The line without the prefix.</summary>
    public string Body { get; }

    /// <summary>The upper-case word the line begins with; empty for a note.</summary>
    public string Word { get; }

    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>The line of the resource, or null for any other console line.</summary>
    public static ProtocolLine? Parse(string line)
    {
        var text = line.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var body = text[Prefix.Length..];
        int end = 0;
        while (end < body.Length && body[end] is >= 'A' and <= 'Z') end++;
        var word = end > 0 && (end == body.Length || body[end] == ' ') ? body[..end] : "";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq > 0 && !values.ContainsKey(token[..eq])) values[token[..eq]] = token[(eq + 1)..];
        }
        return new ProtocolLine(text, body, word, values);
    }

    /// <summary>True when the line begins with these words, e.g. <c>Is("HMAP", "END")</c>.</summary>
    public bool Is(params string[] words)
    {
        var parts = Body.Split(' ', words.Length + 1);
        if (parts.Length < words.Length) return false;
        for (int i = 0; i < words.Length; i++)
            if (parts[i] != words[i]) return false;
        return true;
    }

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public int? Int(string key) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public double? Number(string key) => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool Flag(string key) => Get(key) == "1";

    public override string ToString() => Text;
}
