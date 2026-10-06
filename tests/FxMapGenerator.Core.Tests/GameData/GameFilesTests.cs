using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;
using FxMapGenerator.GameData.Gta;
using FxMapGenerator.GameData.Paths;
using FxMapGenerator.GameData.Text;
using RageLib.Resources.Common;
using RageLib.Resources.GTA5;
using RageLib.Resources.GTA5.PC.Nodes;

namespace FxMapGenerator.Core.Tests.GameData;

/// <summary>A small path node file written with gta-toolkit, for reading it back.</summary>
static class SyntheticYnd
{
    public sealed record N(int Id, double X, double Y, double Z, uint Street = 0, byte F0 = 0, byte F1 = 0, byte F2 = 0, byte F3 = 0, byte F4 = 0, int Speed = 1, bool HasJunction = false);
    public sealed record L(int Area, int Node, byte F0 = 0, byte F1 = 0, byte F2 = 0, byte Length = 10);

    /// <summary>Nodes of <paramref name="area"/> with their links (in node order); junctions as (node, x0, y0, minz, maxz, nx, ny, heights).</summary>
    public static byte[] Build(int area, IReadOnlyList<(N Node, L[] Links)> nodes, IReadOnlyList<(int Node, double X0, double Y0, double MinZ, double MaxZ, int Nx, int Ny, byte[] H)>? junctions = null)
    {
        var nb = new byte[nodes.Count * YndFile.NodeSize];
        var links = new List<byte>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var (n, ls) = nodes[i];
            var s = nb.AsSpan(i * YndFile.NodeSize, YndFile.NodeSize);
            BinaryPrimitives.WriteUInt16LittleEndian(s[0x10..], (ushort)area);
            BinaryPrimitives.WriteUInt16LittleEndian(s[0x12..], (ushort)n.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], n.Street);
            BinaryPrimitives.WriteUInt16LittleEndian(s[0x1a..], (ushort)(links.Count / YndFile.LinkSize));
            BinaryPrimitives.WriteInt16LittleEndian(s[0x1c..], (short)Math.Round(n.X * 4));
            BinaryPrimitives.WriteInt16LittleEndian(s[0x1e..], (short)Math.Round(n.Y * 4));
            s[0x20] = n.F0; s[0x21] = n.F1;
            BinaryPrimitives.WriteInt16LittleEndian(s[0x22..], (short)Math.Round(n.Z * 32));
            s[0x24] = n.F2;
            s[0x25] = (byte)((ls.Length << 3) | (n.Speed << 1) | (n.HasJunction ? 1 : 0));
            s[0x26] = n.F3; s[0x27] = n.F4;
            foreach (var l in ls)
            {
                var lb = new byte[YndFile.LinkSize];
                BinaryPrimitives.WriteUInt16LittleEndian(lb, (ushort)l.Area);
                BinaryPrimitives.WriteUInt16LittleEndian(lb.AsSpan(2), (ushort)l.Node);
                lb[4] = l.F0; lb[5] = l.F1; lb[6] = l.F2; lb[7] = l.Length;
                links.AddRange(lb);
            }
        }
        var jb = new List<byte>();
        var hb = new List<byte>();
        var rb = new List<byte>();
        foreach (var (node, x0, y0, minz, maxz, nx, ny, h) in junctions ?? [])
        {
            var j = new byte[YndFile.JunctionSize];
            BinaryPrimitives.WriteInt16LittleEndian(j, (short)Math.Round(maxz * 32));
            BinaryPrimitives.WriteInt16LittleEndian(j.AsSpan(2), (short)Math.Round(x0 * 4));
            BinaryPrimitives.WriteInt16LittleEndian(j.AsSpan(4), (short)Math.Round(y0 * 4));
            BinaryPrimitives.WriteInt16LittleEndian(j.AsSpan(6), (short)Math.Round(minz * 32));
            BinaryPrimitives.WriteUInt16LittleEndian(j.AsSpan(8), (ushort)hb.Count);
            j[10] = (byte)nx; j[11] = (byte)ny;
            var r = new byte[YndFile.JunctionRefSize];
            BinaryPrimitives.WriteUInt16LittleEndian(r, (ushort)area);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(2), (ushort)node);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), (ushort)(jb.Count / YndFile.JunctionSize));
            jb.AddRange(j);
            hb.AddRange(h);
            rb.AddRange(r);
        }
        var file = new NodesFile
        {
            Unknown_1Ch = (uint)nodes.Count,
            Unknown_48h = 1,
            Nodes = Array<Node>(nb),
            Unknown_28h_Data = Array<Unknown_ND_002>(links.ToArray()),
            Unknown_38h_Data = jb.Count > 0 ? Array<Unknown_ND_003>(jb.ToArray()) : null,
            Unknown_40h_Data = hb.Count > 0 ? new SimpleArray<byte>(hb.ToArray()) : null,
            Unknown_50h_Data = rb.Count > 0 ? Array<Unknown_ND_004>(rb.ToArray()) : null,
        };
        var res = new Resource7<NodesFile> { ResourceData = file, Version = 1 };
        using var ms = new MemoryStream();
        res.Save(ms);
        return ms.ToArray();
    }

    static SimpleArray<T> Array<T>(byte[] bytes) where T : unmanaged => new(MemoryMarshal.Cast<byte, T>(bytes).ToArray());
}

public sealed class GameFilesTests
{
    static readonly DateTime T0 = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    static byte[] Gxt2(params (uint Hash, string Text)[] entries)
    {
        var head = new List<byte>(Encoding.ASCII.GetBytes("2TXG"));
        head.AddRange(BitConverter.GetBytes(entries.Length));
        var strings = new List<byte>();
        int start = 8 + entries.Length * 8;
        foreach (var (h, t) in entries)
        {
            head.AddRange(BitConverter.GetBytes(h));
            head.AddRange(BitConverter.GetBytes(start + strings.Count));
            strings.AddRange(Encoding.UTF8.GetBytes(t));
            strings.Add(0);
        }
        return head.Concat(strings).ToArray();
    }

    [Fact]
    public void Gxt2TableReadsEveryEntry()
    {
        var b = Gxt2((1, "Grove St"), (0xB779A091, "グローブ・ストリート"));
        Assert.Equal(new[] { (1u, "Grove St"), (0xB779A091u, "グローブ・ストリート") }, Gxt2File.Read(b).ToArray());
        Assert.Throws<InvalidDataException>(() => Gxt2File.Read(Encoding.ASCII.GetBytes("GXT2....")).ToArray());
        var cut = Gxt2((1, "x"));
        BinaryPrimitives.WriteInt32LittleEndian(cut.AsSpan(4), 50);
        Assert.Throws<InvalidDataException>(() => Gxt2File.Read(cut).ToArray());
    }

    [Fact]
    public void JoaatMatchesKnownHashes()
    {
        Assert.Equal(0xB779A091u, Gxt2File.Joaat("adder"));
        Assert.NotEqual(Gxt2File.Joaat("PBOX"), Gxt2File.Joaat("pbox"));   // no lower-casing inside
    }

    [Theory]
    [InlineData("nodes464.ynd", 464)]
    [InlineData(@"stream\Nodes7.YND", 7)]
    [InlineData("nodes.ynd", -1)]
    [InlineData("nodes12a.ynd", -1)]
    [InlineData("nodes12.ydr", -1)]
    public void AreaOfFileName(string name, int area) => Assert.Equal(area, YndFile.AreaOf(name));

    [Fact]
    public void PathNodeFileDecodesNodesLinksAndJunctions()
    {
        var bytes = SyntheticYnd.Build(7,
        [
            (new SyntheticYnd.N(0, -100.25, 50.5, 12.5, Street: 0xB779A091, F0: 1 << 3, F1: 14 << 3, F2: (1 << 6) | (1 << 2), F3: 1, F4: 0x23, Speed: 2, HasJunction: true),
                [new SyntheticYnd.L(7, 1, F0: 1, F1: 0b1011_0010, F2: (2 << 5) | (1 << 2) | 1, Length: 12)]),
            (new SyntheticYnd.N(1, -90, 50.5, 12.75), [new SyntheticYnd.L(7, 0, Length: 12)]),
        ],
        [(0, -104.0, 46.0, 10.0, 18.0, 3, 2, new byte[] { 0, 51, 102, 153, 204, 255 })]);
        var y = YndFile.Read(7, bytes);

        Assert.Equal(2, y.VehicleNodes);
        Assert.Equal(2, y.Nodes.Count);
        var n = y.Nodes[0];
        Assert.Equal((7, 0, 0xB779A091u), (n.Area, n.Id, n.Street));
        Assert.Equal((-100.25, 50.5, 12.5), (n.X, n.Y, n.Z));
        Assert.Equal((0, 1), (n.LinkFirst, n.LinkCount));
        Assert.Equal("pedAssistedMovement", YndFile.SpecialName(n));
        Assert.True(YndFile.IsPed(n));
        Assert.Equal("fast", YndFile.SpeedNames[(n.B25 >> 1) & 3]);
        Assert.Equal(1, y.Nodes[1].LinkFirst);
        var l = y.Links[0];
        Assert.Equal((7, 1, (byte)12), (l.Area, l.Node, l.Length));
        var j = Assert.Single(y.Junctions);
        Assert.Equal((7, 0, -104.0, 46.0, 10.0, 18.0, 3, 2), (j.RefArea, j.RefNode, j.X0, j.Y0, j.MinZ, j.MaxZ, j.Nx, j.Ny));
        Assert.Equal(new byte[] { 0, 51, 102, 153, 204, 255 }, j.Heights);
    }

    [Fact]
    public void JunctionRefOutsideTheRecordsIsRefused()
    {
        var refs = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(refs.AsSpan(4), 3);
        Assert.Throws<InvalidDataException>(() => new YndFile(7, 0, [], [], new byte[12], [], refs));
    }

    static byte[] OneNode(int area, int id, double x = 0) => SyntheticYnd.Build(area, [(new SyntheticYnd.N(id, x, 0, 0), [])]);

    [Fact]
    public void ServerFilesComeFromFoldersZipsAndSingleFiles()
    {
        using var tmp = new TempFolder();
        Directory.CreateDirectory(tmp.File(@"res\[maps]\pack\stream"));
        File.WriteAllBytes(tmp.File(@"res\[maps]\pack\stream\nodes431.ynd"), OneNode(431, 0));
        File.WriteAllBytes(tmp.File(@"res\[maps]\pack\stream\nodes.ynd"), OneNode(1, 0));
        File.WriteAllBytes(tmp.File(@"res\other.ydr"), [1, 2, 3]);
        var zip = tmp.File("pack.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var s = z.CreateEntry("pack/stream/nodes464.ynd").Open()) s.Write(OneNode(464, 0));

        var fromFolder = LooseYnd.Read(tmp.File("res")).ToList();
        Assert.Equal(new[] { 431 }, fromFolder.Select(f => f.Area));
        var fromZip = Assert.Single(LooseYnd.Read(zip));
        Assert.Equal((464, "pack.zip!pack/stream/nodes464.ynd"), (fromZip.Area, fromZip.Path));
        Assert.Equal(431, Assert.Single(LooseYnd.Read(tmp.File(@"res\[maps]\pack\stream\nodes431.ynd"))).Area);
        Assert.Throws<FileNotFoundException>(() => LooseYnd.Read(tmp.File("nowhere")).ToList());
        Assert.Throws<InvalidDataException>(() => LooseYnd.Read(tmp.File(@"res\other.ydr")).ToList());
    }

    [Fact]
    public void MapAreasCoverTheMapRectangle()
    {
        var areas = GameFileReader.MapAreas(MapFrame.Standard);
        Assert.Equal(19 * 26, areas.Count);            // the frame's strip north of 8192 m lies outside the area grid
        Assert.Equal(6 * 32 + 7, areas[0]);           // south-west corner of the map rectangle
        Assert.Equal(31 * 32 + 25, areas[^1]);        // the grid's last row
        Assert.Contains(14 * 32 + 15, areas);          // Legion Square's area (464)
    }

    [Fact]
    public void MapAreasOfALargerFrameStayInsideTheAreaGrid()
    {
        // two cells above: past the grid's north edge, nothing more (a row numbered 32 or more would take the island's files for areas)
        var north = GameFileReader.MapAreas(new MapFrame(2, 0, 0, 0));
        Assert.Equal(GameFileReader.MapAreas(MapFrame.Standard), north);
        Assert.All(north, a => Assert.True(a < GameFileReader.IslandFileOffset));
        // a cell to the right and below (Cayo Perico): the island's areas 153..250 are in
        var cayo = GameFileReader.MapAreas(new MapFrame(0, 1, 0, 1));
        Assert.Equal(23 * 31, cayo.Count);
        Assert.Equal(1 * 32 + 7, cayo[0]);
        Assert.Contains(153, cayo);
        Assert.Contains(250, cayo);
        // four cells to the left: the grid's west edge (-8192 m) is not reached
        Assert.Equal(-4140 - 4 * 2250, new MapFrame(0, 0, 4, 0).X0);
        Assert.Equal(6 * 32 + 0, GameFileReader.MapAreas(new MapFrame(0, 0, 4, 0))[0]);
    }

    [Fact]
    public void TheIslandsRoadFilesTakeTheirAreasOnlyWhenAskedFor()
    {
        using var tmp = new TempFolder();
        Directory.CreateDirectory(tmp.File(@"res\a"));
        Directory.CreateDirectory(tmp.File(@"res\b"));
        // area 215 (inside the standard frame) and 153 (south of it): their own files, and the island's (1024 above, nodes of the same areas)
        File.WriteAllBytes(tmp.File(@"res\a\nodes215.ynd"), OneNode(215, 0, x: 1));
        File.WriteAllBytes(tmp.File(@"res\a\nodes1239.ynd"), OneNode(215, 0, x: 5));
        File.WriteAllBytes(tmp.File(@"res\a\nodes153.ynd"), OneNode(153, 0, x: 2));
        File.WriteAllBytes(tmp.File(@"res\a\nodes1177.ynd"), OneNode(153, 0, x: 6));

        // not asked for: the areas' own files; the island's are never read
        var r = GameFileReader.Read(null, null, [tmp.File("res")], new MapFrame(0, 1, 0, 1));
        Assert.Equal(new[] { 153, 215 }, r.Files.Keys.Order());
        Assert.Equal((2, 1), (r.Files[153].Nodes[0].X, r.Files[215].Nodes[0].X));
        Assert.Empty(r.Island);

        // asked for: every area with an island file reads it, under the area's number, and never its own
        r = GameFileReader.Read(null, null, [tmp.File("res")], new MapFrame(0, 1, 0, 1), island: true);
        Assert.Equal(new[] { 153, 215 }, r.Files.Keys.Order());
        Assert.Equal((6, 5), (r.Files[153].Nodes[0].X, r.Files[215].Nodes[0].X));
        Assert.Equal(new[] { 153, 215 }, r.Island);
        Assert.EndsWith(@"a\nodes1177.ynd", r.Sources[153]);
        Assert.Equal(2, r.Overridden.Count(o => o.Contains("the island's", StringComparison.Ordinal)));
        Assert.DoesNotContain(r.Replaced, x => x.Area is 153 or 215);        // the areas' own server files were set aside

        // the standard frame: only the island's areas inside it
        r = GameFileReader.Read(null, null, [tmp.File("res")], MapFrame.Standard, island: true);
        Assert.Equal(new[] { 215 }, r.Files.Keys);
        Assert.Equal(new[] { 215 }, r.Island);

        // of two island files for one area, the later in the order wins, as for any area
        File.WriteAllBytes(tmp.File(@"res\b\nodes1239.ynd"), OneNode(215, 0, x: 9));
        r = GameFileReader.Read(null, null, [tmp.File("res")], MapFrame.Standard, island: true);
        Assert.Equal(9, r.Files[215].Nodes[0].X);
    }

    [Fact]
    public void InsideAFolderTheResourcesCountInDictionaryOrder()
    {
        var paths = new[] { @"B\stream\nodes1.ynd", @"a b\stream\nodes1.ynd", @"a\stream\nodes1.ynd", @"a\nodes1.ynd", @"[maps]\z\nodes1.ynd" };
        Assert.Equal(new[] { @"[maps]\z\nodes1.ynd", @"a\nodes1.ynd", @"a\stream\nodes1.ynd", @"a b\stream\nodes1.ynd", @"B\stream\nodes1.ynd" },
            paths.Order(LooseYnd.Order));
    }

    [Fact]
    public void ServerFilesReplaceWholeAreasAndTheLaterWins()
    {
        using var tmp = new TempFolder();
        Directory.CreateDirectory(tmp.File(@"res\a\stream"));
        Directory.CreateDirectory(tmp.File(@"res\B\stream"));
        File.WriteAllBytes(tmp.File(@"res\a\stream\nodes464.ynd"), OneNode(464, 0, x: 1));
        File.WriteAllBytes(tmp.File(@"res\B\stream\nodes464.ynd"), OneNode(464, 0, x: 2));
        File.WriteAllBytes(tmp.File(@"res\B\stream\nodes0.ynd"), OneNode(0, 0));    // outside the map: not read

        var r = GameFileReader.Read(null, null, [tmp.File("res")], MapFrame.Standard);
        Assert.Equal(new[] { 464 }, r.Files.Keys);
        Assert.Equal(2, r.Files[464].Nodes[0].X);                                   // B after a (dictionary order, any case)
        var rep = Assert.Single(r.Replaced);
        Assert.Equal((464, (string?)null), (rep.Area, rep.Replaces));
        Assert.EndsWith(@"B\stream\nodes464.ynd", rep.Path);
        Assert.Contains("overridden by", Assert.Single(r.Overridden));
        Assert.Empty(r.Ties);
        Assert.All(r.Names.Values, t => Assert.Empty(t));

        // several folders: the later in the list wins, whatever their names
        r = GameFileReader.Read(null, null, [tmp.File(@"res\B"), tmp.File(@"res\a")], MapFrame.Standard);
        Assert.Equal(1, r.Files[464].Nodes[0].X);
        Assert.EndsWith(@"a\stream\nodes464.ynd", Assert.Single(r.Replaced).Path);
    }

    [Fact]
    public void NodesAreNamedByTheFilesAreaAndTheirOrder()
    {
        // written for area 401 with node numbers 5 and 9, in a file named for area 400: the name and the order count,
        // as the links name a node by them
        var bytes = SyntheticYnd.Build(401,
        [
            (new SyntheticYnd.N(5, 1, 2, 3), [new SyntheticYnd.L(400, 1)]),
            (new SyntheticYnd.N(9, 4, 5, 6), [new SyntheticYnd.L(400, 0)]),
        ]);
        var y = YndFile.Read(400, bytes);
        Assert.Equal(new[] { (400, 0), (400, 1) }, y.Nodes.Select(n => (n.Area, n.Id)));
        Assert.Equal((2, 2), (y.OtherAreaNodes, y.OtherNumberNodes));
        var same = YndFile.Read(401, SyntheticYnd.Build(401, [(new SyntheticYnd.N(0, 1, 2, 3), [])]));
        Assert.Equal((0, 0), (same.OtherAreaNodes, same.OtherNumberNodes));

        using var tmp = new TempFolder();
        Directory.CreateDirectory(tmp.File("res"));
        File.WriteAllBytes(tmp.File(@"res\nodes464.ynd"), SyntheticYnd.Build(465,
        [
            (new SyntheticYnd.N(0, 1, 2, 3), [new SyntheticYnd.L(464, 1)]),
            (new SyntheticYnd.N(7, 4, 5, 6), [new SyntheticYnd.L(464, 0)]),
        ]));
        File.WriteAllBytes(tmp.File(@"res\nodes431.ynd"), OneNode(431, 0));
        var log = new List<string>();
        var r = GameFileReader.Read(null, null, [tmp.File("res")], MapFrame.Standard, log: log.Add);
        var m = Assert.Single(r.NumberMismatches);
        Assert.StartsWith("area 464: ", m);
        Assert.Contains("2 of 2 nodes carry another area, 1 another node number", m);
        Assert.Contains(log, l => l.Contains(m, StringComparison.Ordinal));

        // the links find their nodes: both nodes and both links are written under the file's area
        Assert.Equal((3, 2, 0), GameFilesOutput.WritePaths(tmp.File("paths.json"), r));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(tmp.File("paths.json")));
        Assert.Equal(new[] { "431:0", "464:0", "464:1" }, doc.RootElement.GetProperty("nodes").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { ("464:0", "464:1"), ("464:1", "464:0") },
            doc.RootElement.GetProperty("links").EnumerateArray().Select(l => (l.GetProperty("from").GetString()!, l.GetProperty("to").GetString()!)));
    }

    [Fact]
    public void AServerFileThatCannotBeReadIsPassedOverWithItsReason()
    {
        Assert.Null(YndFile.NotReadable(OneNode(464, 0)));
        var escrow = Encoding.ASCII.GetBytes("FXAP").Concat(new byte[60]).ToArray();
        var enhanced = Encoding.ASCII.GetBytes("RSC8").Concat(new byte[60]).ToArray();
        Assert.Contains("Cfx asset escrow", YndFile.NotReadable(escrow));
        Assert.Contains("RSC8", YndFile.NotReadable(enhanced));
        Assert.Contains("too short", YndFile.NotReadable(new byte[3]));
        Assert.Contains("starts with 01 02 03 04", YndFile.NotReadable(new byte[] { 1, 2, 3, 4 }.Concat(new byte[60]).ToArray()));
        var cut = OneNode(464, 0).AsSpan(0, 24).ToArray();                       // an RSC7 header, the data cut off
        Assert.Contains("damaged", Assert.Throws<InvalidDataException>(() => YndFile.Read(464, cut)).Message);
        Assert.Contains("nodes464.ynd", Assert.Throws<InvalidDataException>(() => YndFile.Read(464, escrow)).Message);

        using var tmp = new TempFolder();
        Directory.CreateDirectory(tmp.File(@"res\a"));
        Directory.CreateDirectory(tmp.File(@"res\b"));
        Directory.CreateDirectory(tmp.File(@"res\c"));
        File.WriteAllBytes(tmp.File(@"res\a\nodes464.ynd"), OneNode(464, 0, x: 1));
        File.WriteAllBytes(tmp.File(@"res\b\nodes464.ynd"), escrow);             // later in the order, but not readable: a's stays
        File.WriteAllBytes(tmp.File(@"res\b\nodes431.ynd"), enhanced);           // no other file for the area: none read
        File.WriteAllBytes(tmp.File(@"res\c\nodes432.ynd"), cut);
        File.WriteAllBytes(tmp.File(@"res\c\nodes0.ynd"), escrow);               // outside the map: not looked at
        var log = new List<string>();
        var r = GameFileReader.Read(null, null, [tmp.File("res")], MapFrame.Standard, log: log.Add);

        Assert.Equal(new[] { 464 }, r.Files.Keys);
        Assert.Equal(1, r.Files[464].Nodes[0].X);
        Assert.EndsWith(@"a\nodes464.ynd", Assert.Single(r.Replaced).Path);
        Assert.Empty(r.Overridden);
        Assert.Equal(3, r.ServerFileErrors.Count);
        Assert.Contains(r.ServerFileErrors, e => e.Contains(@"b\nodes431.ynd", StringComparison.Ordinal) && e.Contains("RSC8", StringComparison.Ordinal));
        Assert.Contains(r.ServerFileErrors, e => e.Contains(@"b\nodes464.ynd", StringComparison.Ordinal) && e.Contains("Cfx asset escrow", StringComparison.Ordinal));
        Assert.Contains(r.ServerFileErrors, e => e.Contains(@"c\nodes432.ynd", StringComparison.Ordinal) && e.Contains("damaged", StringComparison.Ordinal));
        Assert.Equal(3, log.Count(l => l.Contains("server file not read: ", StringComparison.Ordinal)));
        Assert.Empty(r.NumberMismatches);
    }

    [Fact]
    public void OutputFilesHaveTheirShape()
    {
        using var tmp = new TempFolder();
        var bytes = SyntheticYnd.Build(464,
        [
            (new SyntheticYnd.N(0, 10, 20, 30, Street: 5, F0: 1 << 7, F1: 1 << 2, F2: 1 << 6, F3: (37 << 1) | 1, F4: 0x35),
                [new SyntheticYnd.L(464, 1, F1: 0b1001_0000, F2: (1 << 5) | (1 << 2)), new SyntheticYnd.L(464, 2)]),
            (new SyntheticYnd.N(1, 12, 20, 30, Street: 5, F1: 16 << 3), [new SyntheticYnd.L(464, 0)]),
            (new SyntheticYnd.N(2, 12, 24, 30, F1: 10 << 3), [new SyntheticYnd.L(464, 0)]),         // a pedestrian crossing: left out
        ], [(1, 8, 18, 29, 31, 3, 2, new byte[] { 128, 128, 128, 128, 128, 128 }), (2, 8, 22, 29, 31, 1, 1, new byte[] { 128 })]);
        var file = YndFile.Read(464, bytes);
        var r = new GameFileReader.Result(GameFileReader.MapAreas(MapFrame.Standard), new Dictionary<int, YndFile> { [464] = file }, new Dictionary<int, string> { [464] = "x.ynd" },
            [], [], [], new Dictionary<string, IReadOnlyDictionary<uint, string>>(), new Dictionary<string, int>(), 0, [], [], [], []);

        Assert.Equal((2, 2, 1), GameFilesOutput.WritePaths(tmp.File("paths.json"), r));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(tmp.File("paths.json")));
        var nodes = doc.RootElement.GetProperty("nodes");
        Assert.Equal(new[] { "464:0", "464:1" }, nodes.EnumerateObject().Select(p => p.Name));
        var n0 = nodes.GetProperty("464:0");
        Assert.Equal((10.0, true, 5u, false), (n0.GetProperty("x").GetDouble(), n0.GetProperty("highway").GetBoolean(), n0.GetProperty("street").GetUInt32(),
            n0.GetProperty("junction").GetBoolean()));
        Assert.Equal(("none", "normal", 5, 3), (n0.GetProperty("special").GetString(), n0.GetProperty("speed").GetString(),
            n0.GetProperty("trafficDensity").GetInt32(), n0.GetProperty("deadEnd").GetInt32()));
        Assert.False(n0.TryGetProperty("junctionArea", out _));
        Assert.Equal((true, false, true, 37, true), (n0.GetProperty("cannotGoLeft").GetBoolean(), n0.GetProperty("indicateKeepLeft").GetBoolean(),
            n0.GetProperty("indicateKeepRight").GetBoolean(), n0.GetProperty("heuristic").GetInt32(), n0.GetProperty("tunnel").GetBoolean()));
        var n1 = nodes.GetProperty("464:1");
        Assert.Equal((false, false, false, 0), (n1.GetProperty("cannotGoLeft").GetBoolean(), n1.GetProperty("indicateKeepLeft").GetBoolean(),
            n1.GetProperty("indicateKeepRight").GetBoolean(), n1.GetProperty("heuristic").GetInt32()));
        Assert.Equal("stopSign", n1.GetProperty("special").GetString());
        Assert.Equal(new[] { 8.0, 18.0, 12.0, 20.0 }, n1.GetProperty("junctionArea").EnumerateArray().Select(v => v.GetDouble()));   // 3 x 2 samples every 2 m
        var links = doc.RootElement.GetProperty("links");
        Assert.Equal(2, links.GetArrayLength());
        var l0 = links[0];
        Assert.Equal(("464:0", "464:1", 1, 1), (l0.GetProperty("from").GetString(), l0.GetProperty("to").GetString(), l0.GetProperty("lanesForward").GetInt32(),
            l0.GetProperty("lanesBack").GetInt32()));
        Assert.Equal(-0.071, l0.GetProperty("laneOffset").GetDouble());   // 1/7 of half a lane, towards the left
        Assert.Equal((false, false, 10), (l0.GetProperty("shortcut").GetBoolean(), l0.GetProperty("dontUseForNavigation").GetBoolean(), l0.GetProperty("length").GetInt32()));
        Assert.False(File.Exists(tmp.File("paths.json.tmp")));

        var tables = new Dictionary<string, IReadOnlyDictionary<uint, string>>
        {
            ["en"] = new Dictionary<uint, string> { [5] = "Main St", [Gxt2File.Joaat("pbox")] = "Pillbox Hill" },
            ["ja"] = new Dictionary<uint, string> { [5] = "メイン・ストリート" },
        };
        var miss = GameFilesOutput.WriteNames(tmp.File("names.json"), tables, [5, 6, 5], ["PBOX"]);
        Assert.Equal((1, 0), miss);
        using var names = JsonDocument.Parse(File.ReadAllBytes(tmp.File("names.json")));
        Assert.Equal("メイン・ストリート", names.RootElement.GetProperty("streets").GetProperty("5").GetProperty("ja").GetString());
        Assert.Equal(0, names.RootElement.GetProperty("streets").GetProperty("6").EnumerateObject().Count());
        Assert.Equal("Pillbox Hill", names.RootElement.GetProperty("zones").GetProperty("PBOX").GetProperty("en").GetString());
        Assert.Contains("メイン", File.ReadAllText(tmp.File("names.json")));   // not escaped
        Assert.Equal(new[] { "streets", "zones" }, names.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void ScanNamesComeFromTheDictionaryLines()
    {
        using var tmp = new TempFolder();
        File.WriteAllLines(tmp.File("a.txt"), [
            "[mapscan] MSCAN dict street 1 -2068448071 ベスプッチ大通り",
            "[mapscan] MSCAN dict zone 1 PBOX ピルボックス・ヒル",
            "[mapscan] MSCAN street j=0 k=0 1*7",
        ]);
        File.WriteAllLines(tmp.File("b.txt"), ["[fxmapgen] MSCAN dict street 1 127435642 Elgin Ave", "[fxmapgen] MSCAN dict zone 2 LEGSQU Legion Square"]);
        var (streets, zones) = ScanNames.Collect([tmp.File("a.txt"), tmp.File("b.txt")], new FxMapGenerator.Core.Jobs.FixedParallel(2));
        Assert.Equal(new[] { 127435642u, unchecked((uint)-2068448071) }, streets);
        Assert.Equal(new[] { "LEGSQU", "PBOX" }, zones);
    }

    [Fact]
    public void AnIncompleteKeyFolderGivenIsNotReplacedByAnother()
    {
        using var tmp = new TempFolder();
        File.WriteAllBytes(tmp.File(GtaKeys.AesKeyFile), new byte[32]);
        var found = GtaKeys.Find(tmp.Path);
        Assert.Null(found.Folder);
        var tried = Assert.Single(found.TriedFolders);
        Assert.Equal(3, tried.Missing.Count);
        Assert.Equal(("given", tmp.Path), GtaKeys.Candidates(tmp.Path)[0]);
        Assert.Equal(GtaKeys.SharedKeyFolder, GtaKeys.Candidates(null)[^1].Folder);
    }

    sealed class StageSetup : IDisposable
    {
        readonly TempFolder _tmp = new();
        public Project Project { get; }
        public StateStore State { get; }
        public WorkFolder Folder { get; }

        public StageSetup()
        {
            Project = Project.Create(_tmp.File("t.fxmapgen.json"));
            Project.File.Maps = new MapsSetting { Satellite = false, Atlas = new AtlasSetting { Enabled = true } };
            Project.File.Range = new RangeSetting { Base = "none", Add = ["z8_60_132", "z8_64_132"] };
            Folder = new WorkFolder(Project.WorkFolderPath);
            State = StateStore.Open(Folder);
        }

        public void Files(params string[] serverResources)
        {
            Directory.CreateDirectory(Folder.Game);
            foreach (var f in new[] { GameFilesOutput.Paths, GameFilesOutput.Names })
                File.WriteAllText(Path.Combine(Folder.Game, f), "{}");
            var record = new GameFilesStage.SourcesRecord("gta", "keys", serverResources, 0, [], 0, 0, 0, 0, [], [], [], new Dictionary<string, int>(), 0, 0, 0, 0, 0, 0, 0);
            File.WriteAllText(Path.Combine(Folder.Game, GameFilesOutput.Record), JsonSerializer.Serialize(record, Project.Json));
        }

        public void Dispose() => _tmp.Dispose();
    }

    [Fact]
    public void GameFilesAreReadAgainAfterANewerScanOrAnotherServerFolder()
    {
        using var s = new StageSetup();
        Assert.True(GameFilesStage.IsStale(s.Project, s.State));          // never read

        s.Files();
        s.State.SetStageDone(StageKeys.GameFiles, StageKeys.World, T0);
        s.State.SetItem(BlockId.Parse("z8_60_132"), BlockItem.ScanRoads, T0.AddMinutes(-5));
        Assert.False(GameFilesStage.IsStale(s.Project, s.State));
        Assert.Equal(0, Planner.Build(s.Project, s.State, 1, 1).Rows.Single(r => r.Id == "gameFiles").Remaining);

        s.State.SetItem(BlockId.Parse("z8_64_132"), BlockItem.ScanRoads, T0.AddMinutes(5));
        Assert.True(GameFilesStage.IsStale(s.Project, s.State));          // a road scan came after them
        Assert.Equal(1, Planner.Build(s.Project, s.State, 1, 1).Rows.Single(r => r.Id == "gameFiles").Remaining);

        s.State.SetStageDone(StageKeys.GameFiles, StageKeys.World, T0.AddMinutes(10));
        Assert.False(GameFilesStage.IsStale(s.Project, s.State));
        s.Project.File.GameFiles.ServerResources = ["base", "maps"];
        Assert.True(GameFilesStage.IsStale(s.Project, s.State));          // other server folders
        s.Files(s.Project.ResolvePath("base"), s.Project.ResolvePath("maps"));
        Assert.False(GameFilesStage.IsStale(s.Project, s.State));
        s.Project.File.GameFiles.ServerResources = ["maps", "base"];
        Assert.True(GameFilesStage.IsStale(s.Project, s.State));          // the same folders in another order
        s.Project.File.GameFiles.ServerResources = ["base", "maps"];
        File.Delete(Path.Combine(s.Folder.Game, GameFilesOutput.Names));
        Assert.True(GameFilesStage.IsStale(s.Project, s.State));          // a file is gone
    }

    [Fact]
    public void TheGameFoldersAreTheAppsAndTheServerResourcesTheProjects()
    {
        using var s = new StageSetup();
        using var keys = new TempFolder();
        foreach (var f in GtaKeys.RequiredFiles) File.WriteAllBytes(keys.File(f), [0]);
        s.Project.File.GameFiles.ServerResources = ["maps"];
        var where = GameFilesLocation.Resolve(s.Project, new GameFilesLocation.Defaults(@"C:\gta-of-the-app", keys.Path));
        Assert.Equal((@"C:\gta-of-the-app", "app"), (where.GtaFolder, where.GtaSource));
        Assert.False(where.GtaFound);
        Assert.Equal((keys.Path, "app"), (where.KeysFolder, where.KeysSource));
        Assert.Equal([s.Project.ResolvePath("maps")], where.ServerResources);
    }
}
