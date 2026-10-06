using System.Buffers.Binary;
using System.IO.Compression;
using FxMapGenerator.GameData.Scaleform;

namespace FxMapGenerator.Core.Tests.Export;

public sealed class GfxFileTests
{
    static byte[] Tag(int code, byte[] data)
    {
        var o = new List<byte>();
        if (data.Length < 0x3F) o.AddRange(BitConverter.GetBytes((ushort)((code << 6) | data.Length)));
        else
        {
            o.AddRange(BitConverter.GetBytes((ushort)((code << 6) | 0x3F)));
            o.AddRange(BitConverter.GetBytes(data.Length));
        }
        o.AddRange(data);
        return o.ToArray();
    }

    /// <summary>A RECT of 0..20 x 0..20 twips in 5-bit values: 5 + 4 x 5 bits = 25 bits, 4 bytes.</summary>
    static readonly byte[] Rect = [0x28, 0x00, 0x14, 0x00];

    static byte[] Shape(int id, int fillerBytes)
    {
        var d = new List<byte> { (byte)id, 0 };
        d.AddRange(Rect);
        d.AddRange(Enumerable.Range(0, fillerBytes).Select(i => (byte)(i * 7 + 1)));   // styles and edges, not read
        return d.ToArray();
    }

    /// <summary>A small GFX: background colour, shapes 1 (DefineShape3, long), 2 (DefineShape2), 3 (DefineShape4), a placement, end.</summary>
    static byte[] Sample(out byte[] placement)
    {
        var body = new List<byte>();
        body.AddRange("GFX"u8.ToArray());
        body.Add(8);
        body.AddRange(new byte[4]);                                   // the length, set below
        body.AddRange(Rect);                                          // the frame
        body.AddRange(new byte[] { 0, 30, 1, 0 });                    // frame rate, frame count
        body.AddRange(Tag(9, [1, 2, 3]));
        body.AddRange(Tag(32, Shape(1, 200)));
        body.AddRange(Tag(22, Shape(2, 20)));
        var s4 = new List<byte> { 3, 0 };
        s4.AddRange(Rect);
        s4.AddRange(Rect);                                            // edge bounds
        s4.Add(0x01);                                                 // flags
        s4.AddRange(Enumerable.Repeat((byte)0x55, 90));
        body.AddRange(Tag(83, s4.ToArray()));
        placement = Tag(26, [6, 1, 0, 1, 0]);
        body.AddRange(placement);
        body.AddRange(Tag(0, []));
        var bytes = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length);
        return bytes;
    }

    [Fact]
    public void ChosenShapesDrawNothingAndEveryOtherTagStaysAsItWas()
    {
        var src = Sample(out var placement);
        var tags = GfxFile.Tags(src, out int first);
        Assert.Equal(new[] { 9, 32, 22, 83, 26, 0 }, tags.Select(t => t.Code));
        var o = GfxFile.EmptyShapes(src, new HashSet<int> { 1, 3 });
        Assert.Equal((uint)o.Length, BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(4)));
        var back = GfxFile.Tags(o, out int first2);
        Assert.Equal(first, first2);
        Assert.Equal(tags.Select(t => t.Code), back.Select(t => t.Code));
        byte[] Data(byte[] b, GfxFile.Tag t) => b.AsSpan(t.DataStart, t.Length).ToArray();
        Assert.Equal(new byte[] { 1, 0 }.Concat(Rect).Concat(new byte[4]).ToArray(), Data(o, back[1]));                          // id, bounds, nothing
        Assert.Equal(Data(src, tags[2]), Data(o, back[2]));                                                 // shape 2 as it was
        Assert.Equal(new byte[] { 3, 0 }.Concat(Rect).Concat(Rect).Concat(new byte[] { 0x01, 0, 0, 0, 0 }).ToArray(), Data(o, back[3]));            // + edge bounds, flags
        Assert.Equal(placement, o.AsSpan(back[4].Start, back[4].DataStart + back[4].Length - back[4].Start).ToArray());
        Assert.Throws<InvalidDataException>(() => GfxFile.EmptyShapes(src, new HashSet<int> { 4 }));
    }

    [Fact]
    public void ACompressedFileIsReadAndWrittenUncompressed()
    {
        var src = Sample(out _);
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(src, 8, src.Length - 8);
        var cfx = src[..8].Concat(z.ToArray()).ToArray();
        cfx[0] = (byte)'C';
        Assert.Equal(src, GfxFile.Inflate(cfx));
        Assert.Equal(GfxFile.EmptyShapes(src, new HashSet<int> { 1 }), GfxFile.EmptyShapes(cfx, new HashSet<int> { 1 }));
        Assert.Throws<InvalidDataException>(() => GfxFile.Inflate("PNG12345"u8.ToArray()));
    }
}
