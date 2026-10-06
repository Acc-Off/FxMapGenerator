using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FxMapGenerator.GameData.Scaleform;

/// <summary>
/// Scaleform GFX files: the SWF layout with a "GFX" signature ("CFX" when the body after the 8-byte header is
/// zlib-compressed): the frame header, then tags (a 16-bit code and length, a 32-bit length after it when that is 63).
/// Reads the top-level tags and writes a copy whose chosen shapes draw nothing.
/// </summary>
public static class GfxFile
{
    /// <summary>A top-level tag: its code, where its header starts, where its data starts, and the data's length.</summary>
    public sealed record Tag(int Code, int Start, int DataStart, int Length);

    /// <summary>The shape definitions: DefineShape, DefineShape2, DefineShape3, DefineShape4.</summary>
    public static readonly IReadOnlySet<int> ShapeTags = new HashSet<int> { 2, 22, 32, 83 };

    /// <summary>The file with its body inflated when it is compressed ("CFX" as "GFX", "CWS" as "FWS"), else the file itself.</summary>
    /// <exception cref="InvalidDataException">Not a GFX or SWF file.</exception>
    public static byte[] Inflate(byte[] file)
    {
        if (file.Length < 8) throw new InvalidDataException("not a GFX file: too short");
        var sig = Encoding.ASCII.GetString(file, 0, 3);
        if (sig is "GFX" or "FWS") return file;
        if (sig is not ("CFX" or "CWS")) throw new InvalidDataException($"not a GFX file (signature '{sig}')");
        using var z = new ZLibStream(new MemoryStream(file, 8, file.Length - 8), CompressionMode.Decompress);
        var ms = new MemoryStream();
        ms.Write(file, 0, 8);
        z.CopyTo(ms);
        var body = ms.ToArray();
        body[0] = sig == "CFX" ? (byte)'G' : (byte)'F';
        return body;
    }

    static int RectBytes(byte[] b, int pos) => (5 + 4 * (b[pos] >> 3) + 7) / 8;

    /// <summary>The top-level tags of an inflated file, up to and with its end tag; <paramref name="first"/> is where they start.</summary>
    public static List<Tag> Tags(byte[] body, out int first)
    {
        first = 8 + RectBytes(body, 8) + 4;                 // the frame's RECT, the frame rate and the frame count
        var tags = new List<Tag>();
        int pos = first;
        while (pos + 2 <= body.Length)
        {
            int head = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos));
            int code = head >> 6, length = head & 0x3F, start = pos;
            pos += 2;
            if (length == 0x3F)
            {
                length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos));
                pos += 4;
            }
            if (length < 0 || pos + length > body.Length) throw new InvalidDataException($"the tag at {start} runs past the end of the file");
            tags.Add(new Tag(code, start, pos, length));
            pos += length;
            if (code == 0) break;
        }
        return tags;
    }

    /// <summary>The id of a shape definition tag.</summary>
    public static int ShapeId(byte[] body, Tag tag) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(tag.DataStart));

    /// <summary>
    /// An uncompressed GFX file of <paramref name="file"/> in which the shapes of these ids draw nothing: each keeps its id
    /// and bounds (and a DefineShape4 its edge bounds and flags) and gets no fill style, no line style and only the end
    /// record. Every other tag (sprites and their placements, ActionScript, exports) is copied byte for byte, in its place;
    /// the file length in the header is set again.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a GFX file, or it has no shape of one of the ids.</exception>
    public static byte[] EmptyShapes(byte[] file, IReadOnlySet<int> ids)
    {
        var body = Inflate(file);
        var tags = Tags(body, out int first);
        using var o = new MemoryStream();
        o.Write(body, 0, first);
        var done = new HashSet<int>();
        int end = first;
        foreach (var t in tags)
        {
            end = t.DataStart + t.Length;
            if (ShapeTags.Contains(t.Code) && ids.Contains(ShapeId(body, t)))
            {
                int keep = 2 + RectBytes(body, t.DataStart + 2);                    // the id and the shape's bounds
                if (t.Code == 83) keep += RectBytes(body, t.DataStart + keep) + 1;   // the edge bounds and the flags
                var data = new byte[keep + 4];                                        // no fills, no lines, 0 / 0 bits, the end record
                Array.Copy(body, t.DataStart, data, 0, keep);
                WriteTag(o, t.Code, data);
                done.Add(ShapeId(body, t));
                continue;
            }
            o.Write(body, t.Start, t.DataStart + t.Length - t.Start);
        }
        o.Write(body, end, body.Length - end);                                        // anything after the end tag
        var missing = ids.Where(id => !done.Contains(id)).Order().ToList();
        if (missing.Count > 0) throw new InvalidDataException($"no shape with the id {string.Join(", ", missing)}");
        var bytes = o.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length);
        return bytes;
    }

    static void WriteTag(Stream s, int code, byte[] data)
    {
        Span<byte> head = stackalloc byte[6];
        if (data.Length < 0x3F)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)((code << 6) | data.Length));
            s.Write(head[..2]);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)((code << 6) | 0x3F));
            BinaryPrimitives.WriteUInt32LittleEndian(head[2..], (uint)data.Length);
            s.Write(head);
        }
        s.Write(data);
    }
}
