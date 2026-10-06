namespace FxMapGenerator.Core.Export;

/// <summary>
/// The row compression of PSD files (PackBits). A packet is a count byte n followed, for n = 0..127, by n + 1 bytes as
/// they are, or, for n = 129..255, by one byte that stands for 257 - n equal bytes (128 is not written). The packets of
/// a row's pieces one after the other are that row packed, so pieces packed apart can be joined as they are.
/// </summary>
public static class PackBits
{
    /// <summary>The most bytes <paramref name="count"/> bytes can take packed.</summary>
    public static int MaxPacked(int count) => count + (count + 127) / 128;

    /// <summary>
    /// Packs <paramref name="source"/> into <paramref name="packed"/> (at least <see cref="MaxPacked"/> long) and returns
    /// the bytes written. Three or more equal bytes become a run; the others go as they are.
    /// </summary>
    public static int Pack(ReadOnlySpan<byte> source, Span<byte> packed)
    {
        int n = source.Length, i = 0, o = 0;
        while (i < n)
        {
            int run = RunAt(source, i);
            if (run >= 3)
            {
                packed[o++] = (byte)(257 - run);
                packed[o++] = source[i];
                i += run;
                continue;
            }
            int start = i;
            i += run;
            while (i < n && i - start < 128)
            {
                run = RunAt(source, i);
                if (run >= 3) break;
                i += run;
            }
            int count = Math.Min(i - start, 128);
            i = start + count;
            packed[o++] = (byte)(count - 1);
            source.Slice(start, count).CopyTo(packed[o..]);
            o += count;
        }
        return o;
    }

    /// <summary>The equal bytes from <paramref name="i"/> on, 128 at most.</summary>
    static int RunAt(ReadOnlySpan<byte> s, int i)
    {
        var rest = s.Slice(i + 1, Math.Min(127, s.Length - i - 1));
        int other = rest.IndexOfAnyExcept(s[i]);
        return 1 + (other < 0 ? rest.Length : other);
    }

    /// <summary>The packets of <paramref name="count"/> bytes of one <paramref name="value"/>.</summary>
    public static byte[] Flat(byte value, int count)
    {
        var o = new byte[2 * ((count + 127) / 128)];
        for (int at = 0, left = count; left > 0; at += 2, left -= 128)
        {
            int run = Math.Min(left, 128);
            // a single byte left over goes as it is (a run is two bytes or more)
            o[at] = run == 1 ? (byte)0 : (byte)(257 - run);
            o[at + 1] = value;
        }
        return o;
    }

    /// <summary>Unpacks <paramref name="packed"/> into <paramref name="row"/>, which the packets must fill exactly.</summary>
    /// <exception cref="InvalidDataException">The packets do not make up the row.</exception>
    public static void Unpack(ReadOnlySpan<byte> packed, Span<byte> row)
    {
        int i = 0, o = 0;
        while (i < packed.Length)
        {
            int n = packed[i++];
            if (n == 128) continue;
            if (n < 128)
            {
                int count = n + 1;
                if (i + count > packed.Length || o + count > row.Length) throw new InvalidDataException("packed row: the bytes run past the row");
                packed.Slice(i, count).CopyTo(row[o..]);
                i += count;
                o += count;
            }
            else
            {
                int count = 257 - n;
                if (i >= packed.Length || o + count > row.Length) throw new InvalidDataException("packed row: a run runs past the row");
                row.Slice(o, count).Fill(packed[i++]);
                o += count;
            }
        }
        if (o != row.Length) throw new InvalidDataException($"packed row: {o} bytes for a row of {row.Length}");
    }
}
