namespace FxMapGenerator.Core.Labels;

/// <summary>
/// A set of pixels (x, y) that is walked in the order CPython 3.11's <c>set</c> of <c>(x, y)</c> tuples walks it: its
/// hash table (tuple hash of the xxHash kind, linear probes of 9, perturbed steps, growth at 3/5 full, dummies left by
/// deletions) is kept slot for slot. The street centre lines are cut into pieces in that order, and the order of the
/// pieces decides how they are joined again, so it is part of the result. Written after CPython's <c>set_add_entry</c>,
/// <c>set_insert_clean</c>, <c>set_table_resize</c>, <c>set_merge</c> and <c>set_difference</c>
/// (<c>Objects/setobject.c</c>) and <c>tuplehash</c> (<c>Objects/tupleobject.c</c>); PSF-2.0, see THIRD-PARTY-NOTICES.md.
/// </summary>
public sealed class PixelSet
{
    const int MinSize = 8, LinearProbes = 9, PerturbShift = 5;
    const long Empty = long.MinValue, Dummy = long.MinValue + 1;

    long[] keys;            // slot -> key (x << 32 | y), or Empty / Dummy
    long[] hashes;          // slot -> hash (0 for empty, -1 for dummy)
    long mask;
    int fill, used;

    public PixelSet()
    {
        keys = new long[MinSize];
        hashes = new long[MinSize];
        Array.Fill(keys, Empty);
        mask = MinSize - 1;
    }

    public int Count => used;

    static long Key(int x, int y) => ((long)x << 32) | (uint)y;
    static (int X, int Y) Unkey(long k) => ((int)(k >> 32), (int)(uint)k);

    /// <summary><c>hash((x, y))</c> for whole numbers (CPython 3.11 <c>tuplehash</c>; <c>hash(n) = n</c>, -1 hashes as -2).</summary>
    public static long Hash(int x, int y)
    {
        const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P5 = 2870177450012600261UL;
        ulong acc = P5;
        foreach (long v in (ReadOnlySpan<long>)[x, y])
        {
            ulong lane = (ulong)(v == -1 ? -2 : v);
            acc += lane * P2;
            acc = (acc << 31) | (acc >> 33);
            acc *= P1;
        }
        acc += 2UL ^ (P5 ^ 3527539UL);
        return acc == ulong.MaxValue ? 1546275796 : (long)acc;
    }

    public bool Contains(int x, int y) => Find(Key(x, y), Hash(x, y)) >= 0;

    /// <summary>The slot of a key, or -1.</summary>
    long Find(long key, long hash)
    {
        ulong perturb = (ulong)hash;
        ulong i = (ulong)hash & (ulong)mask;
        while (true)
        {
            ulong j = i;
            int probes = i + LinearProbes <= (ulong)mask ? LinearProbes : 0;
            do
            {
                if (keys[j] == Empty) return -1;
                if (hashes[j] == hash && keys[j] == key) return (long)j;
                j++;
            } while (probes-- > 0);
            perturb >>= PerturbShift;
            i = (i * 5 + 1 + perturb) & (ulong)mask;
        }
    }

    /// <summary><c>set.add</c> (<c>set_add_entry</c>).</summary>
    public void Add(int x, int y) => AddEntry(Key(x, y), Hash(x, y));

    void AddEntry(long key, long hash)
    {
        ulong perturb = (ulong)hash;
        ulong i = (ulong)hash & (ulong)mask;
        long freeslot = -1;
        while (true)
        {
            ulong j = i;
            int probes = i + LinearProbes <= (ulong)mask ? LinearProbes : 0;
            do
            {
                if (keys[j] == Empty)
                {
                    if (freeslot >= 0)
                    {
                        used++;
                        keys[freeslot] = key;
                        hashes[freeslot] = hash;
                        return;
                    }
                    fill++;
                    used++;
                    keys[j] = key;
                    hashes[j] = hash;
                    if ((ulong)fill * 5 < (ulong)mask * 3) return;
                    Resize(used > 50000 ? used * 2 : used * 4);
                    return;
                }
                if (hashes[j] == hash && keys[j] == key) return;
                if (keys[j] == Dummy && freeslot < 0) freeslot = (long)j;
                j++;
            } while (probes-- > 0);
            perturb >>= PerturbShift;
            i = (i * 5 + 1 + perturb) & (ulong)mask;
        }
    }

    static void InsertClean(long[] keys, long[] hashes, long mask, long key, long hash)
    {
        ulong perturb = (ulong)hash;
        ulong i = (ulong)hash & (ulong)mask;
        while (true)
        {
            if (keys[i] == Empty) { keys[i] = key; hashes[i] = hash; return; }
            if (i + LinearProbes <= (ulong)mask)
                for (ulong j = 1; j <= LinearProbes; j++)
                    if (keys[i + j] == Empty) { keys[i + j] = key; hashes[i + j] = hash; return; }
            perturb >>= PerturbShift;
            i = (i * 5 + 1 + perturb) & (ulong)mask;
        }
    }

    /// <summary><c>set_table_resize</c>: the smallest power of two above <paramref name="minUsed"/>, entries moved in slot order.</summary>
    void Resize(long minUsed)
    {
        long size = MinSize;
        while (size <= minUsed) size <<= 1;
        var (oldKeys, oldHashes) = (keys, hashes);
        if (size == MinSize && oldKeys.Length == MinSize && fill == used) return;
        keys = new long[size];
        hashes = new long[size];
        Array.Fill(keys, Empty);
        mask = size - 1;
        fill = used;
        for (int s = 0; s < oldKeys.Length; s++)
            if (oldKeys[s] != Empty && oldKeys[s] != Dummy) InsertClean(keys, hashes, mask, oldKeys[s], oldHashes[s]);
    }

    /// <summary><c>set.discard</c>: the slot becomes a dummy.</summary>
    void Discard(long key, long hash)
    {
        long s = Find(key, hash);
        if (s < 0) return;
        keys[s] = Dummy;
        hashes[s] = -1;
        used--;
    }

    /// <summary>The pixels in the set's walking order.</summary>
    public IEnumerable<(int X, int Y)> Items()
    {
        for (int s = 0; s < keys.Length; s++)
            if (keys[s] != Empty && keys[s] != Dummy) yield return Unkey(keys[s]);
    }

    IEnumerable<(long Key, long Hash)> Entries()
    {
        for (int s = 0; s < keys.Length; s++)
            if (keys[s] != Empty && keys[s] != Dummy) yield return (keys[s], hashes[s]);
    }

    /// <summary><c>set(iterable)</c>: the pixels added one by one.</summary>
    public static PixelSet Of(IEnumerable<(int X, int Y)> pixels)
    {
        var s = new PixelSet();
        foreach (var (x, y) in pixels) s.Add(x, y);
        return s;
    }

    /// <summary><c>a - b</c> (<c>set_difference</c>).</summary>
    public static PixelSet Difference(PixelSet a, PixelSet b)
    {
        if ((a.used >> 2) > b.used)
        {
            // set_copy_and_difference: a copy of a (set_merge into an empty set), then b's keys discarded
            var r = new PixelSet();
            r.Merge(a);
            if ((b.used >> 3) > r.used) throw new NotSupportedException("the intersection path of difference_update");
            foreach (var (k, h) in b.Entries()) r.Discard(k, h);
            if ((ulong)(r.fill - r.used) > (ulong)r.mask / 4) r.Resize(r.used > 50000 ? r.used * 2 : r.used * 4);
            return r;
        }
        var o = new PixelSet();
        foreach (var (k, h) in a.Entries())
            if (b.Find(k, h) < 0) o.AddEntry(k, h);
        return o;
    }

    /// <summary><c>set_merge</c> of <paramref name="other"/> into this set (empty here).</summary>
    void Merge(PixelSet other)
    {
        if (other.used == 0) return;
        if ((long)(fill + other.used) * 5 >= mask * 3) Resize((long)(used + other.used) * 2);
        if (fill == 0 && mask == other.mask && other.fill == other.used)
        {
            for (int s = 0; s < other.keys.Length; s++)
                if (other.keys[s] != Empty) { keys[s] = other.keys[s]; hashes[s] = other.hashes[s]; }
            fill = other.fill;
            used = other.used;
            return;
        }
        if (fill == 0)
        {
            fill = used = other.used;
            foreach (var (k, h) in other.Entries()) InsertClean(keys, hashes, mask, k, h);
            return;
        }
        foreach (var (k, h) in other.Entries()) AddEntry(k, h);
    }
}
