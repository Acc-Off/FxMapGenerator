using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FxMapGenerator.Core.State;

/// <summary>
/// Digests of what a step read or wrote (the first 128 bits of SHA-256 as hex), so that an input whose record is newer
/// only counts as changed when its contents are: a unit is made again when what it read differs from what it recorded.
/// A file's digest is kept in memory while its length and last write time stay the same.
/// </summary>
public static class ContentDigest
{
    static readonly ConcurrentDictionary<string, (long Length, long Ticks, string Digest)> Files = new(StringComparer.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static string Hex(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash[..16]);

    /// <summary>The digest of a file's bytes, or null when there is no such file.</summary>
    public static string? OfFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        long length = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
        if (Files.TryGetValue(info.FullName, out var e) && e.Length == length && e.Ticks == ticks) return e.Digest;
        string digest;
        using (var s = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            digest = Hex(SHA256.HashData(s));
        Files[info.FullName] = (length, ticks, digest);
        return digest;
    }

    /// <summary>One digest of several files, each by its name (missing ones count as missing).</summary>
    public static string OfFiles(IEnumerable<string> paths) =>
        Of(string.Join('\n', paths.Select(p => $"{Path.GetFileName(p)}:{OfFile(p) ?? "-"}")));

    public static string Of(ReadOnlySpan<byte> data) => Hex(SHA256.HashData(data));

    public static string Of(string text) => Of(Encoding.UTF8.GetBytes(text));

    /// <summary>The digest of a value written as JSON (records with all their fields; numbers round-trip exactly).</summary>
    public static string OfJson<T>(T value) => Of(JsonSerializer.SerializeToUtf8Bytes(value, Json));
}
