using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Export;

/// <summary>
/// The pictures of a map's blocks, layer by layer, as packed rows (<see cref="PackBits"/>) in a folder of temporary
/// files: what the workers of a layered export draw block by block, kept in the form a PSD file holds its rows in, so
/// the file is put together by joining rows and the whole picture is never in memory. A layer has a folder; every
/// worker writes one file there (<see cref="Write"/>), its blocks one after the other:
/// <code>
/// int32 bx, int32 by                      the block
/// uint16 x 4 x side                       the bytes of every packed row: the red plane's rows, the green's, the blue's, the alpha's
/// the packed rows, in that order
/// </code>
/// (little-endian). A block a layer has nothing in is not written.
/// </summary>
/// <param name="folder">The folder of the temporary files (made when written to).</param>
/// <param name="side">The pixels of a block's side.</param>
public sealed class PackedBlocks(string folder, int side)
{
    /// <summary>The number that stands for the merged picture among the layers.</summary>
    public const int Merged = -1;

    public string Folder { get; } = folder;
    public int Side { get; } = side;

    string FolderOf(int layer) => Path.Combine(Folder, layer == Merged ? "merged" : layer.ToString(CultureInfo.InvariantCulture));

    /// <summary>A writer of one worker's blocks; <paramref name="name"/> names its files (a cell, a row of blocks).</summary>
    public Writer Write(string name) => new(this, name);

    public sealed class Writer : IDisposable
    {
        readonly PackedBlocks _store;
        readonly string _name;
        readonly Dictionary<int, FileStream> _files = new();
        readonly byte[] _row, _packed, _head;
        readonly ushort[] _lengths;

        internal Writer(PackedBlocks store, string name)
        {
            (_store, _name) = (store, name);
            int s = store.Side;
            _row = new byte[s];
            _packed = new byte[4 * s * PackBits.MaxPacked(s)];
            _lengths = new ushort[4 * s];
            _head = new byte[8];
        }

        /// <summary>Adds a block's picture (straight RGBA, <see cref="Side"/> pixels a side) to a layer.</summary>
        public void Add(int layer, BlockId block, byte[] rgba)
        {
            int s = _store.Side, at = 0;
            if (rgba.Length != s * s * 4) throw new ArgumentException($"a block picture of {rgba.Length} bytes, not {s} x {s} pixels");
            for (int p = 0; p < 4; p++)
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0, i = y * s * 4 + p; x < s; x++, i += 4) _row[x] = rgba[i];
                    int n = PackBits.Pack(_row, _packed.AsSpan(at));
                    _lengths[p * s + y] = (ushort)n;
                    at += n;
                }
            if (!_files.TryGetValue(layer, out var file))
            {
                var dir = _store.FolderOf(layer);
                Directory.CreateDirectory(dir);
                _files[layer] = file = new FileStream(Path.Combine(dir, _name + ".blocks"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            }
            BinaryPrimitives.WriteInt32LittleEndian(_head, block.Bx);
            BinaryPrimitives.WriteInt32LittleEndian(_head.AsSpan(4), block.By);
            file.Write(_head);
            file.Write(MemoryMarshal.AsBytes(_lengths.AsSpan()));
            file.Write(_packed, 0, at);
        }

        public void Dispose()
        {
            foreach (var f in _files.Values) f.Dispose();
            _files.Clear();
        }
    }

    /// <summary>A block of a layer in the temporary files: where its packed rows start and how long each is.</summary>
    public sealed record Block(string File, long Data, ushort[] Lengths);

    /// <summary>The blocks a layer has, from every worker's file.</summary>
    public Dictionary<BlockId, Block> Read(int layer)
    {
        var blocks = new Dictionary<BlockId, Block>();
        var dir = FolderOf(layer);
        if (!Directory.Exists(dir)) return blocks;
        var head = new byte[8];
        foreach (var path in Directory.EnumerateFiles(dir, "*.blocks").Order(StringComparer.Ordinal))
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            while (f.Position < f.Length)
            {
                f.ReadExactly(head);
                var lengths = new ushort[4 * Side];
                f.ReadExactly(MemoryMarshal.AsBytes(lengths.AsSpan()));
                long data = f.Position, bytes = 0;
                foreach (ushort n in lengths) bytes += n;
                blocks[new BlockId(BinaryPrimitives.ReadInt32LittleEndian(head), BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4)))] = new Block(path, data, lengths);
                f.Position = data + bytes;
            }
        }
        return blocks;
    }

    /// <summary>
    /// The pixels of a rectangle of blocks of one layer, a row of blocks at a time (straight RGBA): what
    /// <see cref="Rows"/> gives packed, for the formats that take pixels. A block the layer has not got is clear, or
    /// painted in one colour where <paramref name="flat"/> gives one. One reader serves one thread.
    /// </summary>
    public sealed class Pixels(PackedBlocks store, IReadOnlyDictionary<BlockId, Block> blocks, int bx0, int by0, int blocksX, int blocksY,
        Func<BlockId, (byte R, byte G, byte B, byte A)?>? flat = null) : IDisposable
    {
        readonly Dictionary<string, FileStream> _files = new();
        readonly byte[] _row = new byte[store.Side];
        byte[] _packed = [];

        /// <summary>The pixels of a row of the picture, and the rows of a row of blocks.</summary>
        public int Width => blocksX * store.Side;
        public int Side => store.Side;
        /// <summary>The rows of blocks.</summary>
        public int BlockRows => blocksY;

        /// <summary>
        /// The row of blocks <paramref name="j"/> (0 the top one) into <paramref name="rgba"/>: <see cref="Side"/> rows of
        /// <see cref="Width"/> pixels.
        /// </summary>
        public void Read(int j, Span<byte> rgba)
        {
            int s = store.Side, width = Width;
            if (rgba.Length != width * s * 4) throw new ArgumentException($"{rgba.Length} bytes for a row of blocks of {width} x {s} pixels");
            for (int i = 0; i < blocksX; i++)
            {
                var id = new BlockId(bx0 + i, by0 + j);
                if (!blocks.TryGetValue(id, out var b))
                {
                    var c = flat?.Invoke(id) ?? default;
                    for (int y = 0; y < s; y++)
                    {
                        var line = rgba.Slice((y * width + i * s) * 4, s * 4);
                        if (c.A == 0 && c.R == 0 && c.G == 0 && c.B == 0) line.Clear();
                        else
                            for (int x = 0; x < s; x++) (line[4 * x], line[4 * x + 1], line[4 * x + 2], line[4 * x + 3]) = (c.R, c.G, c.B, c.A);
                    }
                    continue;
                }
                int bytes = 0;
                foreach (ushort n in b.Lengths) bytes += n;
                if (_packed.Length < bytes) _packed = new byte[bytes];
                if (!_files.TryGetValue(b.File, out var f))
                    _files[b.File] = f = new FileStream(b.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                f.Position = b.Data;
                f.ReadExactly(_packed, 0, bytes);
                for (int p = 0, at = 0; p < 4; p++)
                    for (int y = 0; y < s; y++)
                    {
                        int n = b.Lengths[p * s + y];
                        PackBits.Unpack(_packed.AsSpan(at, n), _row);
                        at += n;
                        var line = rgba.Slice((y * width + i * s) * 4, s * 4);
                        for (int x = 0; x < s; x++) line[4 * x + p] = _row[x];
                    }
            }
        }

        public void Dispose()
        {
            foreach (var f in _files.Values) f.Dispose();
            _files.Clear();
        }
    }

    /// <summary>
    /// The packed rows of a rectangle of blocks of one layer, the blocks side by side: a block the layer has not got is
    /// clear, or painted in one colour where <paramref name="flat"/> gives one.
    /// </summary>
    public sealed class Rows(PackedBlocks store, IReadOnlyDictionary<BlockId, Block> blocks, int bx0, int by0, int blocksX, int blocksY,
        Func<BlockId, (byte R, byte G, byte B, byte A)?>? flat = null) : IPsdRows
    {
        readonly byte[]?[] _flat = new byte[256][];

        /// <summary>A block's row painted in one value.</summary>
        byte[] Flat(byte value) => _flat[value] ??= PackBits.Flat(value, store.Side);

        byte ValueOf(BlockId block, int plane) =>
            flat?.Invoke(block) is { } c ? plane switch { 0 => c.R, 1 => c.G, 2 => c.B, _ => c.A } : (byte)0;

        public int[] Lengths(int plane)
        {
            int s = store.Side;
            var o = new int[blocksY * s];
            for (int j = 0; j < blocksY; j++)
                for (int i = 0; i < blocksX; i++)
                {
                    var id = new BlockId(bx0 + i, by0 + j);
                    if (blocks.TryGetValue(id, out var b))
                        for (int y = 0; y < s; y++) o[j * s + y] += b.Lengths[plane * s + y];
                    else
                    {
                        int n = Flat(ValueOf(id, plane)).Length;
                        for (int y = 0; y < s; y++) o[j * s + y] += n;
                    }
                }
            return o;
        }

        public void Write(int plane, Stream to)
        {
            int s = store.Side;
            var files = new Dictionary<string, FileStream>();
            try
            {
                var data = new byte[blocksX][];
                var at = new int[blocksX];
                var lengths = new ushort[blocksX][];
                for (int j = 0; j < blocksY; j++)
                {
                    for (int i = 0; i < blocksX; i++)
                    {
                        var id = new BlockId(bx0 + i, by0 + j);
                        at[i] = 0;
                        if (!blocks.TryGetValue(id, out var b))
                        {
                            (data[i], lengths[i]) = (Flat(ValueOf(id, plane)), null!);
                            continue;
                        }
                        long start = b.Data;
                        int bytes = 0;
                        for (int k = 0; k < plane * s; k++) start += b.Lengths[k];
                        for (int y = 0; y < s; y++) bytes += b.Lengths[plane * s + y];
                        if (!files.TryGetValue(b.File, out var f))
                            files[b.File] = f = new FileStream(b.File, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                        var buffer = new byte[bytes];
                        f.Position = start;
                        f.ReadExactly(buffer);
                        (data[i], lengths[i]) = (buffer, b.Lengths);
                    }
                    for (int y = 0; y < s; y++)
                        for (int i = 0; i < blocksX; i++)
                        {
                            if (lengths[i] is null)
                            {
                                to.Write(data[i]);
                                continue;
                            }
                            int n = lengths[i][plane * s + y];
                            to.Write(data[i], at[i], n);
                            at[i] += n;
                        }
                }
            }
            finally
            {
                foreach (var f in files.Values) f.Dispose();
            }
        }
    }
}
