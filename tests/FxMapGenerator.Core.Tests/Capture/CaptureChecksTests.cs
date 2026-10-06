using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Tests.Satellite;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Capture;

public sealed class ProtocolLineTests
{
    [Fact]
    public void ReadsTheWordAndTheValuesOfAResourceLine()
    {
        var l = ProtocolLine.Parse("[fxmapgen] READY seq=12 x=219.3750 y=-1021.8750 gz=57.194 water=1 block=z8_60_132")!;
        Assert.Equal("READY", l.Word);
        Assert.Equal(12, l.Int("seq"));
        Assert.Equal(-1021.875, l.Number("y"));
        Assert.Equal("z8_60_132", l.Get("block"));
        Assert.Null(l.Get("h"));
        Assert.True(ProtocolLine.Parse("[fxmapgen] HMAP END nohit=3 ms=900")!.Is("HMAP", "END"));
        Assert.False(ProtocolLine.Parse("[fxmapgen] HMAP ENDS")!.Is("HMAP", "END"));
    }

    [Theory]
    [InlineData("[fxmapgen] ground probe failed with the ped at z=300", "")]
    [InlineData("[fxmapgen] ENV on", "ENV")]
    [InlineData("[fxmapgen] RES qbx_hud started", "RES")]
    public void NotesHaveNoWord(string line, string word) => Assert.Equal(word, ProtocolLine.Parse(line)!.Word);

    [Fact]
    public void OtherConsoleLinesAreNotResourceLines()
    {
        Assert.Null(ProtocolLine.Parse("Stopping resource qbx_hud"));
        Assert.Null(ProtocolLine.Parse("[another] READY seq=1"));
    }
}

public sealed class FrameChecksTests
{
    static Frame Sea(int ripple = 6)
    {
        var rgba = new byte[1920 * 1080 * 4];
        for (int y = 0; y < 1080; y++)
            for (int x = 0; x < 1920; x++)
            {
                int o = (y * 1920 + x) * 4, d = (int)(ripple * Math.Sin(x * 0.21) * Math.Sin(y * 0.17));
                rgba[o] = (byte)(4 + Math.Abs(d) / 2); rgba[o + 1] = (byte)(43 + d); rgba[o + 2] = (byte)(45 + d); rgba[o + 3] = 255;
            }
        return new Frame(1920, 1080, rgba);
    }

    static void Fill(Frame f, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                int o = (y * f.Width + x) * 4;
                f.Rgba[o] = r; f.Rgba[o + 1] = g; f.Rgba[o + 2] = b;
            }
    }

    [Fact]
    public void TheBeaconGivesReadinessAndTheLowBitsOfTheRequest()
    {
        var f = Sea();
        FakeGame.Beacon(f.Rgba, true, 13);
        Assert.Equal((true, 5), FrameChecks.Beacon(f));
        Assert.True(FrameChecks.ReadyFor(f, 13));
        Assert.True(FrameChecks.ReadyFor(f, 5));
        Assert.False(FrameChecks.ReadyFor(f, 14));
        FakeGame.Beacon(f.Rgba, false, 13);
        Assert.Equal((false, 5), FrameChecks.Beacon(f));
        Assert.False(FrameChecks.ReadyFor(f, 13));
        Assert.Null(FrameChecks.Beacon(new Frame(1920, 1080, new byte[1920 * 1080 * 4])));   // black
        Assert.Null(FrameChecks.Beacon(Sea()));                                              // no beacon drawn
    }

    [Fact]
    public void ANotificationBandIsAFlatGreenInTheBottomMiddle()
    {
        var f = Sea();
        Assert.False(FrameChecks.NotificationBand(f));
        Fill(f, 800, 1010, 320, 50, 84, 212, 140);           // the colour measured on txAdmin's notification
        Assert.True(FrameChecks.NotificationBand(f));
        var land = new Frame(1920, 1080, SyntheticCapture.Render(BlockId.Parse("z8_60_132")));
        Assert.False(FrameChecks.NotificationBand(land));
    }

    [Fact]
    public void OpenSeaIsCleanAndWhatDrawsOnItIsBoxed()
    {
        var f = Sea();
        FakeGame.Beacon(f.Rgba, true, 1);                     // the beacon is not something left on the screen
        Assert.Empty(FrameChecks.OverSea(f));
        Fill(f, 100, 900, 60, 20, 240, 240, 240);             // a HUD element
        Fill(f, 1700, 40, 150, 30, 200, 60, 60);              // and a red one elsewhere
        var boxes = FrameChecks.OverSea(f);
        Assert.Equal(2, boxes.Count);
        var hud = boxes.Single(b => b.X < 960);
        Assert.True(hud.X <= 100 && hud.Y <= 900 && hud.X + hud.Width >= 160 && hud.Y + hud.Height >= 920, hud.ToString());
        Assert.True(hud.Width <= 60 + 32 && hud.Height <= 20 + 32, "cell-aligned, not much larger: " + hud);
        var marked = FrameChecks.Marked(f, boxes);
        Assert.Equal((byte)255, marked.Rgba[((hud.Y - 2) * 1920 + hud.X) * 4]);  // red outline above the box
        Assert.Equal((byte)0, marked.Rgba[((hud.Y - 2) * 1920 + hud.X) * 4 + 1]);
    }

    [Fact]
    public void OnlyTheMiddleOfTheFrameReachesTheMap()
    {
        // 1080 / 1.06 = 1019 px of block in the middle of the 1920 px wide frame, 64 px of lean around it
        var area = FrameChecks.MapArea(1920, 1080, 1.06);
        Assert.Equal(new Area(386, 0, 1147, 1080), area);
        var f = Sea();
        Fill(f, 100, 900, 60, 20, 240, 240, 240);             // left of the map's part (like the radar)
        Fill(f, 1000, 500, 40, 40, 240, 240, 240);            // in it
        Fill(f, 1500, 300, 60, 20, 240, 240, 240);            // off the square but within the lean: still in
        var boxes = FrameChecks.OverSea(f, area);
        Assert.Equal(3, boxes.Count);
        Assert.False(boxes.Single(b => b.X < 386).InMap);
        Assert.True(boxes.Single(b => b.X is > 900 and < 1100).InMap);
        Assert.True(boxes.Single(b => b.X > 1400).InMap);
        Assert.All(FrameChecks.OverSea(f), b => Assert.True(b.InMap));  // without an area every box counts
    }

    [Fact]
    public void ChangedCountsThePixelsThatDifferByMoreThanTheLevels()
    {
        var a = new Frame(10, 10, Enumerable.Repeat((byte)100, 400).ToArray());
        var rgba = (byte[])a.Rgba.Clone();
        rgba[0] = 125;          // pixel 0: red +25
        rgba[4 * 5 + 2] = 76;   // pixel 5: blue -24 (not more than 24)
        rgba[4 * 9 + 3] = 0;    // pixel 9: alpha only
        rgba[4 * 7 + 1] = 200;  // pixel 7: green +100
        var b = new Frame(10, 10, rgba);
        Assert.Equal(0.02, FrameChecks.Changed(a, b, 24), 10);
        Assert.Equal(0.03, FrameChecks.Changed(a, b, 23), 10);
        Assert.Equal(0.0, FrameChecks.Changed(a, a, 0));
        Assert.Equal(new[] { 0.03, 0.02, 0.01 }, FrameChecks.Changed(a, b, [8, 24, 25]));
        Assert.Throws<ArgumentException>(() => FrameChecks.Changed(a, new Frame(5, 20, rgba), 24));
    }
}

public sealed class RenderSettingsTests
{
    // the layout FiveM writes (CRLF), shortened; values as on a PC set to low quality
    const string Low = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n\r\n<Settings>\r\n  <version value=\"27\" />\r\n  <configSource>SMC_AUTO</configSource>\r\n" +
        "  <graphics>\r\n    <Tessellation value=\"1\" />\r\n    <LodScale value=\"1.000000\" />\r\n    <ShadowQuality value=\"1\" />\r\n" +
        "    <ReflectionQuality value=\"0\" />\r\n    <SSAO value=\"1\" />\r\n    <AnisotropicFiltering value=\"4\" />\r\n    <MSAA value=\"0\" />\r\n" +
        "    <SamplingMode value=\"0\" />\r\n    <TextureQuality value=\"0\" />\r\n    <ParticleQuality value=\"0\" />\r\n    <WaterQuality value=\"0\" />\r\n" +
        "    <GrassQuality value=\"0\" />\r\n    <ShaderQuality value=\"0\" />\r\n    <FXAA_Enabled value=\"false\" />\r\n    <Shader_SSA value=\"false\" />\r\n" +
        "    <PostFX value=\"0\" />\r\n    <DoF value=\"false\" />\r\n    <HdStreamingInFlight value=\"false\" />\r\n    <MotionBlurStrength value=\"0.000000\" />\r\n" +
        "  </graphics>\r\n  <video>\r\n    <ScreenWidth value=\"1920\" />\r\n    <ScreenHeight value=\"1080\" />\r\n    <Windowed value=\"1\" />\r\n" +
        "    <VSync value=\"1\" />\r\n    <PauseOnFocusLoss value=\"0\" />\r\n  </video>\r\n</Settings>\r\n";

    [Fact]
    public void TheDifferenceNamesWhatTheShotsNeed()
    {
        var diff = RenderSettings.Compare(Low);
        Assert.Equal(new[] { "TextureQuality", "ShaderQuality", "ShadowQuality", "ReflectionQuality", "WaterQuality", "GrassQuality", "ParticleQuality",
            "Tessellation", "SSAO", "AnisotropicFiltering", "FXAA_Enabled", "MSAA", "Shader_SSA", "PostFX", "MaxLodScale", "HdStreamingInFlight" },
            diff.Select(d => d.Key));
        var lod = diff.Single(d => d.Key == "MaxLodScale");
        Assert.Null(lod.Current);                                   // not in this file
        Assert.Equal(("1.000000", "lod"), (lod.Wanted, lod.Why));
        Assert.Equal("0", diff.Single(d => d.Key == "TextureQuality").Current);
    }

    [Fact]
    public void VSyncOffIsADifference()
    {
        // VSync on keeps the frame rate steady (and the GPU below its limit), so the settle wait measured in the pre-check holds
        var off = Low.Replace("<VSync value=\"1\" />", "<VSync value=\"0\" />");
        var d = RenderSettings.Compare(off).Single(x => x.Key == "VSync");
        Assert.Equal(("0", "1", "stable"), (d.Current, d.Wanted, d.Why));
        Assert.Contains("    <VSync value=\"1\" />", RenderSettings.Apply(off));
    }

    [Fact]
    public void ApplyChangesOnlyTheValuesAndKeepsTheLayout()
    {
        var after = RenderSettings.Apply(Low);
        Assert.Empty(RenderSettings.Compare(after));
        Assert.DoesNotContain("\n", after.Replace("\r\n", ""));          // still CRLF only
        var before = Low.Split("\r\n");
        var lines = after.Split("\r\n");
        Assert.Equal(before.Length + 1, lines.Length);                   // one line more: MaxLodScale
        Assert.Equal("    <MaxLodScale value=\"1.000000\" />", lines[Array.IndexOf(lines, "  </graphics>") - 1]);
        Assert.Contains("    <TextureQuality value=\"2\" />", lines);
        Assert.Contains("    <FXAA_Enabled value=\"true\" />", lines);
        Assert.Equal(before.Where(l => !l.Contains("value=")), lines.Where(l => !l.Contains("value=")));
        Assert.Equal(after, RenderSettings.Apply(after));                 // applying twice changes nothing
    }

    [Fact]
    public void AScreenSizeOtherThan1920x1080IsADifference()
    {
        var wide = Low.Replace("<ScreenWidth value=\"1920\" />", "<ScreenWidth value=\"2560\" />");
        var d = RenderSettings.Compare(wide).Single(x => x.Key == "ScreenWidth");
        Assert.Equal(("2560", "1920", "window"), (d.Current, d.Wanted, d.Why));
    }

    [Fact]
    public void OtherFilesAreRefused() => Assert.Throws<InvalidDataException>(() => RenderSettings.Compare("<config><a/></config>"));
}

public sealed class GameLinkTests
{
    /// <summary>A console that answers from a script: command -> lines (printed after a few milliseconds).</summary>
    sealed class ScriptedConsole(Func<string, IEnumerable<string>> answer) : IGameConsole
    {
        public List<string> Sent { get; } = new();
        public bool Connected { get; set; } = true;
        public event Action<string>? LineReceived;
        public void Start() { }

        public Task<bool> SendAsync(string command, CancellationToken token = default)
        {
            lock (Sent) Sent.Add(command);
            var lines = answer(command).ToList();
            _ = Task.Run(async () =>
            {
                await Task.Delay(5);
                foreach (var l in lines) LineReceived?.Invoke(l);
            });
            return Task.FromResult(Connected);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ATileTakesOnlyItsOwnReadyOrFail()
    {
        var console = new ScriptedConsole(c => c.StartsWith("fxmapgen tile") ? new[]
        {
            "Stopping resource chat",
            "[fxmapgen] FAIL seq=4 reason=superseded",                          // an earlier request
            "[fxmapgen] READY seq=5 x=1 y=2 gz=0 h=1 fov=2 margin=1.06 block=z8_64_132", // another block
            "[fxmapgen] READY seq=3 x=1 y=2 gz=0 h=1 fov=2 margin=1.06 block=z8_60_132", // late, before afterSeq
            "[fxmapgen] READY seq=6 x=1 y=2 gz=0 h=1 fov=2 margin=1.06 block=z8_60_132",
        } : Array.Empty<string>());
        await using var link = new GameLink(console);
        var reply = await link.TileAsync(BlockId.Parse("z8_60_132"), 2, 1.06, afterSeq: 4, 1500, CancellationToken.None);
        Assert.Equal(TileOutcome.Ready, reply.Outcome);
        Assert.Equal(6, reply.Seq);
        Assert.Equal("fxmapgen tile 8 60 132 2 1.06 1500", Assert.Single(console.Sent));
        Assert.Equal(6, link.LastSeq);
    }

    [Fact]
    public async Task ATileTakesTheFailOfItsOwnRequestAlsoWhenSuperseded()
    {
        // someone typed another tile into the game console: this request is cancelled, so it is tried again at once
        var console = new ScriptedConsole(c => c.StartsWith("fxmapgen tile") ? new[] { "[fxmapgen] FAIL seq=5 reason=superseded" } : Array.Empty<string>());
        await using var link = new GameLink(console);
        var reply = await link.TileAsync(BlockId.Parse("z8_60_132"), 2, 1.06, afterSeq: 4, 1500, CancellationToken.None);
        Assert.Equal((TileOutcome.Fail, 5), (reply.Outcome, reply.Seq));
        Assert.Equal("another request to the resource took this one's place", reply.Reason);
    }

    [Fact]
    public async Task KeepalivesGoOutWhileWaiting()
    {
        var console = new ScriptedConsole(_ => Array.Empty<string>());
        await using var link = new GameLink(console) { KeepaliveEvery = TimeSpan.FromMilliseconds(100) };
        var heard = await link.ListenAsync("fxmapgen status", _ => GameLink.Take.No, TimeSpan.FromMilliseconds(1600), CancellationToken.None);
        Assert.False(heard.Complete);
        Assert.InRange(console.Sent.Count(c => c == "fxmapgen ping"), 1, 20);
    }

    [Fact]
    public async Task TheHeightGridIsCollectedFromBeginToEnd()
    {
        var console = new ScriptedConsole(c => c.StartsWith("fxmapgen hmap") ? new[]
        {
            "[fxmapgen] HMAP j=9 k=0 1.0",                                          // before BEGIN: not ours
            "[fxmapgen] HMAP BEGIN seq=1 z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=140.625 n=3 block=z8_60_132",
            "[fxmapgen] HMAP j=0 k=0 1.0 2.0 x", "[fxmapgen] note in between", "[fxmapgen] HMAP j=1 k=0 1.0 2.0 3.0", "[fxmapgen] HMAP j=2 k=0 1.0 2.0 3.0",
            "[fxmapgen] HMAP END nohit=1 ms=10",
        } : Array.Empty<string>());
        var transcript = new StringWriter();
        await using var link = new GameLink(console, transcript);
        var h = await link.HmapAsync(140.625, CancellationToken.None);
        Assert.Null(h.Failure);
        Assert.Equal(5, h.Lines!.Count);
        Assert.StartsWith("HMAP BEGIN", h.Lines[0]);
        Assert.Equal("HMAP END nohit=1 ms=10", h.Lines[^1]);
        Assert.DoesNotContain("HMAP j=", transcript.ToString());               // the rows stay out of the transcript
        Assert.Contains("> fxmapgen hmap 140.625", transcript.ToString());
    }
}
