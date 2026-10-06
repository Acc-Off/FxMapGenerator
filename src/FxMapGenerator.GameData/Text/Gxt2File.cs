using System.Buffers.Binary;
using System.Text;

namespace FxMapGenerator.GameData.Text;

/// <summary>
/// A GXT2 text table (<c>Docs/spec/gxt2-format.ja.md</c>): "2TXG", u32 count, count x (u32 hash, u32 offset from the
/// file start), then NUL-terminated UTF-8 strings. The street hash that GET_STREET_NAME_AT_COORD returns is such a
/// key; zone names are keyed by joaat(zone code).
/// </summary>
public static class Gxt2File
{
    public static IEnumerable<(uint Hash, string Text)> Read(byte[] b)
    {
        if (b.Length < 8 || b[0] != (byte)'2' || b[1] != (byte)'T' || b[2] != (byte)'X' || b[3] != (byte)'G')
            throw new InvalidDataException("not a GXT2 table");
        int count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
        if (count < 0 || 8 + (long)count * 8 > b.Length) throw new InvalidDataException("GXT2 table: the entry count runs past the end");
        for (int i = 0; i < count; i++)
        {
            var e = b.AsSpan(8 + i * 8, 8);
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(e);
            long off = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            if (off >= b.Length) throw new InvalidDataException($"GXT2 table: entry {i} points past the end");
            int end = (int)off;
            while (end < b.Length && b[end] != 0) end++;
            yield return (hash, Encoding.UTF8.GetString(b, (int)off, end - (int)off));
        }
    }

    /// <summary>Jenkins one-at-a-time over the UTF-8 bytes as given (no lower-casing).</summary>
    public static uint Joaat(string s)
    {
        uint h = 0;
        foreach (var c in Encoding.UTF8.GetBytes(s))
        {
            h += c; h += h << 10; h ^= h >> 6;
        }
        h += h << 3; h ^= h >> 11; h += h << 15;
        return h;
    }
}
