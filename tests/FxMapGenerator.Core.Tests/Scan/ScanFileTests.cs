using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Scan;

public sealed class ScanFileTests
{
    /// <summary>A 3 x 3 ground pass and a 2 x 2 road pass of block z8_60_132, as the resource prints them.</summary>
    static string[] Lines(string prefix = "[fxmapgen]", bool canopy = false) =>
    [
        $"{prefix} MSCAN BEGIN v=1 kind=mat z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=1.000 n=3 flags=1 fol=1 chunks=2 pflags=128" + (canopy ? " pflags2=256" : ""),
        $"{prefix} MSCAN dict mat 1 282940568",
        $"{prefix} MSCAN dict mat 2 -1775485061",
        $"{prefix} MSCAN mat j=0 k=1 2",
        $"{prefix} MSCAN mat j=0 k=0 1*2",                  // parts arrive out of order
        $"{prefix} MSCAN mat j=1 k=0 0 1 2",
        $"{prefix} MSCAN hz j=0 k=0 30.5 x 31.25",
        $"{prefix} MSCAN hz j=1 k=0 x*3",
        $"{prefix} MSCAN hz j=2 k=0 29.0*2 . 99",          // one value past n is cut
        $"{prefix} MSCAN water j=0 k=0 x*3",
        $"{prefix} MSCAN fol j=0 k=0 x 28.5 x",
        canopy ? $"{prefix} MSCAN fol2 j=0 k=0 40*3" : $"{prefix} MSCAN chunk 0,0 surf=28.8 wait=1250",
        $"{prefix} MSCAN END kind=mat n=3 cells=9 nohit=4",
        $"{prefix} MSCAN BEGIN v=1 kind=road z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=4.000 n=2 pstep=1.000 pn=3 ring=0",
        $"{prefix} MSCAN dict street 1 -2068448071 Vespucci Blvd",
        $"{prefix} MSCAN dict zone 1 PBOX Pillbox Hill",
        $"{prefix} MSCAN dict zone 2 LEGSQU Legion Square",
        $"{prefix} MSCAN street j=0 k=0 1 0",
        $"{prefix} MSCAN street j=1 k=0 1*2",
        $"{prefix} MSCAN zone j=0 k=0 1 2",
        $"{prefix} MSCAN zone j=1 k=0 2*2",
        $"{prefix} MSCAN onroad j=0 k=0 1*3",
        $"{prefix} MSCAN onroad j=1 k=0 0 1 0",
        $"{prefix} MSCAN edge 1 2 3 4 5 6 1 1 5.5 0 0 0 0 0",
        $"{prefix} MSCAN END kind=road n=2 edges=1",
        $"{prefix} MSCAN DONE z=8 tx=60 ty=132 ms=9257 abort=false",
        "some other console line",
    ];

    [Fact]
    public void GroundAndRoadPassesBecomeGrids()
    {
        var s = ScanFile.Parse(Lines());
        Assert.Equal((8, 60, 132, 3, 1.0, 78.75, -881.25), (s.Z, s.Tx, s.Ty, s.N, s.Step, s.X0, s.Y0));
        uint tarmac = 282940568, other = unchecked((uint)-1775485061);
        Assert.Equal(new[] { tarmac, tarmac, other, 0u, tarmac, other, 0u, 0u, 0u }, s.Material);
        Assert.Equal(30.5f, s.HitZ[0]);
        Assert.True(float.IsNaN(s.HitZ[1]));
        Assert.Equal(new[] { 29f, 29f }, s.HitZ[6..8]);
        Assert.True(float.IsNaN(s.HitZ[8]));
        Assert.Equal(28.5f, s.Probe[1]);
        Assert.True(float.IsNaN(s.Probe[3]));                // row 1 never came
        Assert.Null(s.Canopy);
        Assert.Equal(1, s.Missing["mat"]);                   // row 2
        Assert.Equal(2, s.Missing["water"]);
        Assert.Equal(0, s.Missing["hz"]);
        Assert.Equal(1, s.Uneven["hz"]);                     // row 2 has a value too many
        Assert.Equal(0, s.Uneven["mat"]);

        Assert.Equal((2, 4.0, 3, 1.0), (s.RoadN, s.RoadStep, s.OnRoadN, s.OnRoadStep));
        uint vespucci = unchecked((uint)-2068448071);
        Assert.Equal(new[] { vespucci, 0u, vespucci, vespucci }, s.Street);
        Assert.Equal("Vespucci Blvd", s.StreetNames[vespucci]);
        Assert.Equal(new short[] { 1, 2, 2, 2 }, s.Zone);
        Assert.Equal(new[] { ("PBOX", "Pillbox Hill"), ("LEGSQU", "Legion Square") }, s.Zones);
        Assert.Equal(new byte[] { 1, 1, 1, 0, 1, 0, 0, 0, 0 }, s.OnRoad);
        Assert.Equal("9", s.End["mat"]["cells"]);
        Assert.Equal("false", s.Done!["abort"]);
    }

    [Fact]
    public void LinesTheGameCutAreAProblemOfTheScan()
    {
        // a whole 2 x 2 ground pass over a deep sea bottom
        static List<string> Pass() =>
        [
            "MSCAN BEGIN v=1 kind=mat z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=140.625 n=2 flags=1 fol=1",
            "MSCAN dict mat 1 282940568",
            "MSCAN mat j=0 k=0 1*2", "MSCAN hz j=0 k=0 -257.1 -257.2", "MSCAN water j=0 k=0 .*2", "MSCAN fol j=0 k=0 x*2",
            "MSCAN mat j=1 k=0 1*2", "MSCAN hz j=1 k=0 -257.3 -257.4", "MSCAN water j=1 k=0 .*2", "MSCAN fol j=1 k=0 x*2",
            "MSCAN END kind=mat n=2", "MSCAN DONE seq=1 kind=ground block=z8_60_132 ms=10",
        ];
        Assert.Null(ScanFile.Problem(Pass()));
        var inToken = Pass();                                // cut in a token: a lone "-" ends the part
        inToken[3] = "MSCAN hz j=0 k=0 -257.1 -";
        inToken.Insert(4, "MSCAN hz j=0 k=1 -257.2");
        Assert.StartsWith("a line could not be read (", ScanFile.Problem(inToken));
        var between = Pass();                                // cut between two tokens: the row is a point short
        between[3] = "MSCAN hz j=0 k=0 -257.1";
        Assert.Equal("1 hz rows without all their points", ScanFile.Problem(between));
        var gone = Pass();                                   // the hz row 1 never came
        gone.RemoveAt(7);
        Assert.Equal("1 hz rows missing", ScanFile.Problem(gone));
    }

    [Fact]
    public void ThePrefixDoesNotMatterAndTheCanopyComesWithItsHeader()
    {
        var a = ScanFile.Parse(Lines("[mapscan]", canopy: true));
        Assert.Equal(new[] { 40f, 40f, 40f }, a.Canopy![0..3]);
        Assert.Equal(256, a.Pflags2);
        Assert.Equal(ScanFile.Parse(Lines()).Material, a.Material);
    }

    [Fact]
    public void WithoutTheProbeItsGridIsEmpty()
    {
        var lines = Lines().Select(l => l.Replace(" fol=1", " fol=0")).ToArray();
        var s = ScanFile.Parse(lines);
        Assert.All(s.Probe, v => Assert.True(float.IsNaN(v)));
        Assert.False(ScanFile.Parse(["[fxmapgen] MSCAN BEGIN v=1 kind=road z=8 tx=0 ty=0 x0=0 y0=0 size=281.25 step=4 n=1 pstep=1 pn=1"]).HasGround);
    }

    [Fact]
    public void MaterialClasses()
    {
        var m = Materials.Default;
        Assert.Equal("none", m.Classes[m.ClassOf(0)]);
        Assert.Equal("tarmac", m.Classes[m.ClassOf(282940568)]);         // TARMAC
        Assert.Equal("unknown", m.Classes[m.ClassOf(12345)]);
        Assert.Equal(new[] { "none", "unknown", "tarmac" }, m.Classes.Take(3));
        Assert.Equal(m.Names.Count(kv => kv.Value == "DEFAULT"), m.Names.Count(kv => kv.Value == "DEFAULT" && m.Classes[m.ClassOf(kv.Key)] is "default" or "other"));
        var g = m.ClassGrid([0, 282940568, 12345]);
        Assert.Equal(new byte[] { (byte)m.IndexOf("none"), (byte)m.IndexOf("tarmac"), (byte)m.IndexOf("unknown") }, g);
    }

    [Fact]
    public void TheAreaLooksUpTheBlocksByWorldPosition()
    {
        var a = ScanFile.Parse(Lines());
        var b = ScanFile.Parse(Lines().Select(l => l.Replace("tx=60 ", "tx=64 ").Replace("x0=78.7500", "x0=360.0000")).ToArray());
        var area = new ScanArea([a, b]);
        Assert.Equal((60, 132, 2, 1), (area.Tx0, area.Ty0, area.Nbx, area.Nby));
        Assert.Equal((78.75, -881.25), (area.X0, area.Y0));
        // cell (0, 0) of block a is tarmac: its sample sits at the corner, the nearest sample of positions within 0.5 m
        Assert.True(area.Tarmac(78.75 + 0.49, -881.25 - 0.49));
        Assert.False(area.Tarmac(78.75 + 2.2, -881.25 - 0.2));            // (0, 2): the other material
        Assert.True(area.Tarmac(78.75 + 281.25 + 0.2, -881.25 - 0.2));   // block b
        Assert.False(area.Inside(78.75 - 1, -881.25));
        Assert.Equal(unchecked((uint)-2068448071), area.Street(78.75 + 1, -881.25 - 1));
        Assert.True(area.OnRoad(78.75, -881.25));
        Assert.Equal((-4140.5, -4139.5, -4139.5, 12.5, -0.5), (ScanArea.Lattice(-4140), ScanArea.Lattice(-4139.75), ScanArea.Lattice(-4139.5), ScanArea.Lattice(12.3), ScanArea.Lattice(0)));
        Assert.Equal(WorldGrid.BlockSize * 2, area.W);
    }
}
