using System.Buffers.Binary;
using System.Runtime.InteropServices;
using RageLib.Resources.GTA5;
using RageLib.Resources.GTA5.PC.Nodes;

namespace FxMapGenerator.GameData.Paths;

/// <summary>
/// A path node file <c>nodesNNN.ynd</c> (one 512 m area of the vehicle and pedestrian path graph). The RSC7 resource is
/// opened with gta-toolkit, which reads the five arrays; the fields inside them are decoded here
/// (<c>Docs/spec/ynd-format.ja.md</c>):
/// <code>
/// node 40 B:     0x10 u16 area, 0x12 u16 node, 0x14 u32 street name hash, 0x1a u16 first link, 0x1c i16 x*4, 0x1e i16 y*4,
///                0x20 u8 f0, 0x21 u8 f1, 0x22 i16 z*32, 0x24 u8 f2, 0x25 u8 (link count &lt;&lt; 3 | speed &lt;&lt; 1 | has junction),
///                0x26 u8 f3, 0x27 u8 f4
/// link 8 B:      0x00 u16 area, 0x02 u16 node, 0x04 u8 f0, 0x05 u8 f1, 0x06 u8 f2, 0x07 u8 length
/// junction 12 B: 0x00 i16 max z*32, 0x02 i16 x0*4, 0x04 i16 y0*4, 0x06 i16 min z*32, 0x08 u16 offset into the height bytes,
///                0x0a u8 nx, 0x0b u8 ny
/// junction ref 8 B: 0x00 u16 area, 0x02 u16 node, 0x04 u16 junction, 0x06 u16
/// header:        the node count covers the vehicle nodes (first) and the pedestrian nodes
/// </code>
/// A node is named by the file's area and its place in the node array, as the links name it: the area and node number
/// written in the node (0x10, 0x12) are only compared (<see cref="OtherAreaNodes"/>, <see cref="OtherNumberNodes"/>).
/// </summary>
public sealed class YndFile
{
    public const int NodeSize = 40, LinkSize = 8, JunctionSize = 12, JunctionRefSize = 8;

    public sealed record Node(int Area, int Id, uint Street, int LinkFirst, int LinkCount, double X, double Y, double Z,
        byte F0, byte F1, byte F2, byte F3, byte F4, byte B25);
    public sealed record Link(int Area, int Node, byte F0, byte F1, byte F2, byte Length);
    /// <summary>A junction record: a height grid of <see cref="Nx"/> x <see cref="Ny"/> samples every 2 m from (X0, Y0), each byte 0..255 = MinZ..MaxZ.</summary>
    public sealed record Junction(int RefArea, int RefNode, int Id, int Unk0, double X0, double Y0, double MinZ, double MaxZ, int Nx, int Ny, byte[] Heights);

    public int Area { get; }
    public int VehicleNodes { get; }
    /// <summary>Nodes whose written area is not the file's.</summary>
    public int OtherAreaNodes { get; }
    /// <summary>Nodes whose written number is not their place in the node array.</summary>
    public int OtherNumberNodes { get; }
    public List<Node> Nodes { get; } = new();
    public List<Link> Links { get; } = new();
    public List<Junction> Junctions { get; } = new();

    /// <summary>The area number of a file name (<c>nodes464.ynd</c> -> 464), or -1.</summary>
    public static int AreaOf(string fileName)
    {
        var name = System.IO.Path.GetFileName(fileName);
        if (!name.StartsWith("nodes", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".ynd", StringComparison.OrdinalIgnoreCase)) return -1;
        var digits = name.AsSpan(5, name.Length - 9);
        return digits.Length > 0 && int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : -1;
    }

    const uint Rsc7 = 0x37435352, Rsc8 = 0x38435352, Fxap = 0x50415846;

    /// <summary>Why the bytes are not a path node file this reader takes (by their first four bytes), or null.</summary>
    public static string? NotReadable(ReadOnlySpan<byte> file)
    {
        if (file.Length < 16) return $"too short to be a resource ({file.Length} bytes)";
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(file);
        return magic switch
        {
            Rsc7 => null,
            Rsc8 => "an RSC8 resource (GTA V Enhanced); FiveM's files are RSC7",
            Fxap => "encrypted by the Cfx asset escrow (FXAP)",
            _ => $"not an RSC7 resource (starts with {file[0]:x2} {file[1]:x2} {file[2]:x2} {file[3]:x2})",
        };
    }

    /// <summary>
    /// Reads a whole <c>.ynd</c> file as stored (RSC7 header + data). Bytes that are not such a file, or whose data does
    /// not unpack, are refused with the reason (<see cref="InvalidDataException"/>).
    /// </summary>
    public static YndFile Read(int area, byte[] rsc7)
    {
        if (NotReadable(rsc7) is { } why) throw new InvalidDataException($"nodes{area}.ynd: {why}");
        var res = new Resource7<NodesFile>();
        try
        {
            using var ms = new MemoryStream(rsc7);
            res.Load(ms);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidDataException($"nodes{area}.ynd: damaged, the resource does not unpack ({e.GetType().Name}: {e.Message})", e);
        }
        var f = res.ResourceData;
        return new YndFile(area, (int)f.Unknown_1Ch, Bytes(f.Nodes), Bytes(f.Unknown_28h_Data), Bytes(f.Unknown_38h_Data),
            f.Unknown_40h_Data?.ToArray() ?? Array.Empty<byte>(), Bytes(f.Unknown_50h_Data));
    }

    static byte[] Bytes<T>(RageLib.Resources.Common.SimpleArray<T>? a) where T : unmanaged =>
        a == null ? Array.Empty<byte>() : MemoryMarshal.AsBytes(a.AsSpan()).ToArray();

    /// <summary>From the five arrays as raw bytes (nodes, links, junctions, junction heights, junction refs).</summary>
    public YndFile(int area, int vehicleNodes, byte[] nodes, byte[] links, byte[] junctions, byte[] heights, byte[] junctionRefs)
    {
        Area = area;
        VehicleNodes = vehicleNodes;
        for (int o = 0; o + NodeSize <= nodes.Length; o += NodeSize)
        {
            var s = nodes.AsSpan(o, NodeSize);
            if (U16(s, 0x10) != area) OtherAreaNodes++;
            if (U16(s, 0x12) != Nodes.Count) OtherNumberNodes++;
            Nodes.Add(new Node(area, Nodes.Count, BinaryPrimitives.ReadUInt32LittleEndian(s[0x14..]), U16(s, 0x1a), s[0x25] >> 3,
                I16(s, 0x1c) / 4.0, I16(s, 0x1e) / 4.0, I16(s, 0x22) / 32.0, s[0x20], s[0x21], s[0x24], s[0x26], s[0x27], s[0x25]));
        }
        for (int o = 0; o + LinkSize <= links.Length; o += LinkSize)
        {
            var s = links.AsSpan(o, LinkSize);
            Links.Add(new Link(U16(s, 0), U16(s, 2), s[4], s[5], s[6], s[7]));
        }
        for (int o = 0; o + JunctionRefSize <= junctionRefs.Length; o += JunctionRefSize)
        {
            var r = junctionRefs.AsSpan(o, JunctionRefSize);
            int jid = U16(r, 4);
            if ((jid + 1) * JunctionSize > junctions.Length) throw new InvalidDataException($"nodes{area}.ynd: junction ref to record {jid} of {junctions.Length / JunctionSize}");
            var j = junctions.AsSpan(jid * JunctionSize, JunctionSize);
            int nx = j[0x0a], ny = j[0x0b], off = U16(j, 0x08);
            if (off + nx * ny > heights.Length) throw new InvalidDataException($"nodes{area}.ynd: junction {jid} heights outside the height bytes");
            Junctions.Add(new Junction(U16(r, 0), U16(r, 2), jid, U16(r, 6), I16(j, 0x02) / 4.0, I16(j, 0x04) / 4.0, I16(j, 0x06) / 32.0, I16(j, 0x00) / 32.0,
                nx, ny, heights.AsSpan(off, nx * ny).ToArray()));
        }
    }

    static int U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
    static int I16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt16LittleEndian(s[o..]);

    // ------------------------------------------------------------ decoded flags

    public static readonly IReadOnlyDictionary<int, string> SpecialNames = new Dictionary<int, string>
    {
        [0] = "none", [2] = "parkingSpace", [10] = "pedRoadCrossing", [14] = "pedAssistedMovement", [15] = "trafficLightStop",
        [16] = "stopSign", [17] = "caution", [18] = "pedRoadCrossingNoWait", [19] = "emergencyVehiclesOnly", [20] = "offroadJunction",
    };
    public static readonly IReadOnlyList<string> SpeedNames = ["slow", "normal", "fast", "faster"];

    /// <summary>The node's special kind (bits 3..7 of f1).</summary>
    public static int Special(Node n) => (n.F1 >> 3) & 31;
    public static string SpecialName(Node n) => SpecialNames.TryGetValue(Special(n), out var s) ? s : Special(n).ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Pedestrian nodes are the three pedestrian special kinds.</summary>
    public static bool IsPed(Node n) => Special(n) is 10 or 14 or 18;
}
