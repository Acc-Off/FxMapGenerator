// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author); only the namespaces differ.
using System.Buffers.Binary;
using System.Text;
using FxMapGenerator.App.FxConsole;

namespace FxMapGenerator.App.Tests.FxConsole;

public class FxConsoleFrameParserTests
{
    /// <summary>Builds a game → client frame the way the game does (length = total size, text at offset 40).</summary>
    private static byte[] Frame(string magic, string text, uint? declaredLength = null)
    {
        var payload = Encoding.UTF8.GetBytes(text + "\0");
        var total = FxConsoleProtocol.PrintPayloadOffset + payload.Length;
        var frame = new byte[total];
        Encoding.ASCII.GetBytes(magic).CopyTo(frame, 0);
        frame[4] = 0x00;
        frame[5] = 0xD3;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(6, 4), declaredLength ?? (uint)total);
        payload.CopyTo(frame, FxConsoleProtocol.PrintPayloadOffset);
        return frame;
    }

    private static List<FxConsoleFrame> Feed(FxConsoleFrameParser parser, params byte[][] chunks)
    {
        var frames = new List<FxConsoleFrame>();
        foreach (var chunk in chunks)
        {
            parser.Feed(chunk, frames);
        }

        return frames;
    }

    [Fact]
    public void ParsesASinglePrintFrame()
    {
        var frames = Feed(new FxConsoleFrameParser(), Frame("PRNT", "hello"));

        var frame = Assert.Single(frames);
        Assert.Equal(FxConsoleFrameType.Print, frame.Type);
        Assert.Equal("hello", frame.Text);
    }

    [Fact]
    public void ParsesSeveralFramesFromOneChunk()
    {
        var chunk = Frame("PRNT", "one").Concat(Frame("AINF", "emulator")).Concat(Frame("PRNT", "two")).ToArray();

        var frames = Feed(new FxConsoleFrameParser(), chunk);

        Assert.Equal([FxConsoleFrameType.Print, FxConsoleFrameType.AppInfo, FxConsoleFrameType.Print], frames.Select(f => f.Type));
        Assert.Equal(["one", null, "two"], frames.Select(f => f.Text));
    }

    [Fact]
    public void ReassemblesAFrameFedOneByteAtATime()
    {
        var parser = new FxConsoleFrameParser();
        var frame = Frame("PRNT", "byte by byte");
        var frames = new List<FxConsoleFrame>();

        for (var i = 0; i < frame.Length; i++)
        {
            parser.Feed(frame.AsSpan(i, 1), frames);
            if (i < frame.Length - 1)
            {
                Assert.Empty(frames);
            }
        }

        Assert.Equal("byte by byte", Assert.Single(frames).Text);
        Assert.Equal(0, parser.BufferedBytes);
    }

    [Fact]
    public void WaitsForTheRestOfAnIncompleteFrame()
    {
        var parser = new FxConsoleFrameParser();
        var frame = Frame("PRNT", "split");

        var frames = Feed(parser, frame[..20]);
        Assert.Empty(frames);
        Assert.Equal(20, parser.BufferedBytes);

        frames = Feed(parser, frame[20..]);
        Assert.Equal("split", Assert.Single(frames).Text);
    }

    [Fact]
    public void SkipsGarbageBeforeAFrame()
    {
        var chunk = "!!junk!!not-a-frame!!"u8.ToArray().Concat(Frame("PRNT", "after junk")).ToArray();

        var frames = Feed(new FxConsoleFrameParser(), chunk);

        Assert.Equal("after junk", Assert.Single(frames).Text);
    }

    [Fact]
    public void SkipsGarbageThatArrivedInAnEarlierChunk()
    {
        var parser = new FxConsoleFrameParser();

        var frames = Feed(parser, "garbage-garbage-garbage"u8.ToArray());
        Assert.Empty(frames);
        Assert.True(parser.BufferedBytes <= 3, "only a short tail should be retained");

        frames = Feed(parser, Frame("PRNT", "recovered"));
        Assert.Equal("recovered", Assert.Single(frames).Text);
    }

    [Fact]
    public void RecognisesAMagicThatStraddlesTwoChunks()
    {
        var parser = new FxConsoleFrameParser();
        var frame = Frame("PRNT", "straddle");
        var first = "0123456789abc"u8.ToArray().Concat(frame[..2]).ToArray(); // junk + "PR"

        var frames = Feed(parser, first);
        Assert.Empty(frames);

        frames = Feed(parser, frame[2..]); // "NT" + rest
        Assert.Equal("straddle", Assert.Single(frames).Text);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(11u)]
    [InlineData(FxConsoleProtocol.MaxFrameSize + 1u)]
    [InlineData(uint.MaxValue)]
    public void ResyncsAfterABogusLength(uint bogusLength)
    {
        var chunk = Frame("PRNT", "broken", bogusLength).Concat(Frame("PRNT", "good")).ToArray();

        var frames = Feed(new FxConsoleFrameParser(), chunk);

        Assert.Equal("good", Assert.Single(frames).Text);
    }

    [Fact]
    public void ExposesChannelAndCvarFramesWithoutText()
    {
        var chunk = Frame("CHAN", "chan").Concat(Frame("CVAR", "cvar")).ToArray();

        var frames = Feed(new FxConsoleFrameParser(), chunk);

        Assert.Equal([FxConsoleFrameType.Channel, FxConsoleFrameType.Cvar], frames.Select(f => f.Type));
        Assert.All(frames, f => Assert.Null(f.Text));
        Assert.All(frames, f => Assert.Equal(FxConsoleProtocol.PrintPayloadOffset + 5, f.Data.Length));
    }

    [Fact]
    public void HeaderOnlyFrameIsAcceptedWithEmptyText()
    {
        var header = new byte[FxConsoleProtocol.HeaderSize];
        "PRNT"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6, 4), FxConsoleProtocol.HeaderSize);

        var frames = Feed(new FxConsoleFrameParser(), header);

        Assert.Equal(string.Empty, Assert.Single(frames).Text);
    }

    [Fact]
    public void ResetDiscardsBufferedBytes()
    {
        var parser = new FxConsoleFrameParser();
        Feed(parser, Frame("PRNT", "partial")[..30]);
        Assert.True(parser.BufferedBytes > 0);

        parser.Reset();

        Assert.Equal(0, parser.BufferedBytes);
        Assert.Equal("fresh", Assert.Single(Feed(parser, Frame("PRNT", "fresh"))).Text);
    }

    [Fact]
    public void HandlesLargeFrames()
    {
        var text = new string('x', 100_000);

        var frames = Feed(new FxConsoleFrameParser(), Frame("PRNT", text));

        Assert.Equal(text, Assert.Single(frames).Text);
    }
}
