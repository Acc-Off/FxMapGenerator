using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FxMapGenerator.Core.Grids;

/// <summary>
/// The work folder's grid file (<c>Docs/spec/project-format.ja.md</c>): named 2-D arrays with a little metadata.
/// <code>
/// "FXGRID1\n"                       8 bytes
/// u32 little-endian                 length of the header
/// header                            UTF-8 JSON: { "meta": {…}, "arrays": [ { "name", "type", "width", "height", "bytes" } ] }
/// the arrays, in header order       each the row-major little-endian values, zlib-compressed ("bytes" long)
/// </code>
/// Types: <c>bool</c> (one byte 0 / 1), <c>u8</c>, <c>i16</c>, <c>u16</c>, <c>i32</c>, <c>u32</c>, <c>f32</c>, <c>f64</c>.
/// </summary>
public sealed class GridFile
{
    static readonly byte[] Magic = "FXGRID1\n"u8.ToArray();

    readonly List<(string Name, string Type, int Width, int Height, byte[] Raw)> _arrays = new();

    /// <summary>Free-form values kept with the grids (how they were made, the block, ...).</summary>
    public JsonObject Meta { get; } = new();

    public IEnumerable<string> Names => _arrays.Select(a => a.Name);

    static string TypeOf<T>() => typeof(T) switch
    {
        var t when t == typeof(bool) => "bool",
        var t when t == typeof(byte) => "u8",
        var t when t == typeof(short) => "i16",
        var t when t == typeof(ushort) => "u16",
        var t when t == typeof(int) => "i32",
        var t when t == typeof(uint) => "u32",
        var t when t == typeof(float) => "f32",
        var t when t == typeof(double) => "f64",
        _ => throw new NotSupportedException("grid file type " + typeof(T).Name),
    };

    public void Add<T>(string name, Grid<T> grid) where T : unmanaged
    {
        if (_arrays.Any(a => a.Name == name)) throw new ArgumentException("grid file: two arrays named " + name);
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("big-endian");
        _arrays.Add((name, TypeOf<T>(), grid.Width, grid.Height, MemoryMarshal.AsBytes(grid.Data.AsSpan()).ToArray()));
    }

    public bool Has(string name) => _arrays.Any(a => a.Name == name);

    public Grid<T> Get<T>(string name) where T : unmanaged
    {
        var a = _arrays.FirstOrDefault(x => x.Name == name);
        if (a.Name is null) throw new KeyNotFoundException("grid file: no array " + name);
        if (a.Type != TypeOf<T>()) throw new InvalidDataException($"grid file: {name} is {a.Type}, not {TypeOf<T>()}");
        var data = MemoryMarshal.Cast<byte, T>(a.Raw).ToArray();
        return new Grid<T>(a.Width, a.Height, data);
    }

    /// <summary>Writes through a temporary file next to the target (the arrays compressed at the given level).</summary>
    public void Save(string path, CompressionLevel level = CompressionLevel.Fastest)
    {
        var blobs = _arrays.Select(a => Compress(a.Raw, level)).ToList();
        var header = new JsonObject
        {
            ["meta"] = JsonNode.Parse(Meta.ToJsonString()),
            ["arrays"] = new JsonArray(_arrays.Select((a, i) => (JsonNode)new JsonObject
            {
                ["name"] = a.Name, ["type"] = a.Type, ["width"] = a.Width, ["height"] = a.Height, ["bytes"] = blobs[i].Length,
            }).ToArray()),
        };
        var head = Encoding.UTF8.GetBytes(header.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            fs.Write(Magic);
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)head.Length);
            fs.Write(len);
            fs.Write(head);
            foreach (var b in blobs) fs.Write(b);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static GridFile Load(string path) => Parse(File.ReadAllBytes(path), path);

    /// <summary>A grid file from its bytes (<paramref name="path"/> names it in errors).</summary>
    public static GridFile Parse(byte[] bytes, string path)
    {
        if (bytes.Length < 12 || !bytes.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException($"{path}: not a grid file");
        int headLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        if (12 + (long)headLen > bytes.Length) throw new InvalidDataException($"{path}: cut short");
        var header = JsonNode.Parse(bytes.AsSpan(12, headLen))!.AsObject();
        var g = new GridFile();
        if (header["meta"] is JsonObject meta)
            foreach (var kv in meta) g.Meta[kv.Key] = kv.Value?.DeepClone();
        long at = 12 + headLen;
        foreach (var node in header["arrays"]!.AsArray())
        {
            var a = node!.AsObject();
            int n = (int)a["bytes"]!;
            if (at + n > bytes.Length) throw new InvalidDataException($"{path}: cut short");
            var raw = Decompress(bytes.AsSpan((int)at, n));
            at += n;
            g._arrays.Add(((string)a["name"]!, (string)a["type"]!, (int)a["width"]!, (int)a["height"]!, raw));
        }
        return g;
    }

    static byte[] Compress(byte[] raw, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var src = new MemoryStream(data.ToArray());
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }
}
