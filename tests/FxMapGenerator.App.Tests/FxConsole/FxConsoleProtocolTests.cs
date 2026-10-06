// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author); only the namespaces differ.
using System.Text;
using FxMapGenerator.App.FxConsole;

namespace FxMapGenerator.App.Tests.FxConsole;

public class FxConsoleProtocolTests
{
    [Fact]
    public void EncodeCommandProducesTheFxcommandsFrameLayout()
    {
        var frame = FxConsoleProtocol.EncodeCommand("e wave");

        byte[] expected =
        [
            (byte)'C', (byte)'M', (byte)'N', (byte)'D', // magic
            0x00, 0xD3,                                 // protocol 211
            0x00, 0x00, 0x00, 0x08,                     // length = "e wave" (6) + '\n' + '\0'
            0x00, 0x00,                                 // padding
            (byte)'e', (byte)' ', (byte)'w', (byte)'a', (byte)'v', (byte)'e',
            (byte)'\n',
            0x00,
        ];
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void EncodeCommandCountsUtf8BytesNotChars()
    {
        const string command = "say こんにちは"; // 4 ASCII + 5 × 3-byte characters = 19 bytes
        var frame = FxConsoleProtocol.EncodeCommand(command);

        var utf8 = Encoding.UTF8.GetByteCount(command);
        Assert.Equal(19, utf8);
        Assert.Equal(FxConsoleProtocol.HeaderSize + utf8 + 2, frame.Length);
        Assert.Equal((byte)0, frame[6]);
        Assert.Equal((byte)0, frame[7]);
        Assert.Equal((byte)0, frame[8]);
        Assert.Equal((byte)(utf8 + 2), frame[9]);
        Assert.Equal(command, Encoding.UTF8.GetString(frame, FxConsoleProtocol.HeaderSize, utf8));
        Assert.Equal((byte)'\n', frame[^2]);
        Assert.Equal((byte)0, frame[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void EncodeCommandRejectsInvalidInput(string command)
    {
        Assert.Throws<ArgumentException>(() => FxConsoleProtocol.EncodeCommand(command));
    }

    [Fact]
    public void EncodeCommandRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => FxConsoleProtocol.EncodeCommand(null!));
    }

    [Theory]
    [InlineData("PRNT", FxConsoleFrameType.Print)]
    [InlineData("CHAN", FxConsoleFrameType.Channel)]
    [InlineData("CVAR", FxConsoleFrameType.Cvar)]
    [InlineData("AINF", FxConsoleFrameType.AppInfo)]
    public void RecognisesIncomingMagics(string magic, FxConsoleFrameType expected)
    {
        Assert.True(FxConsoleProtocol.TryGetFrameType(Encoding.ASCII.GetBytes(magic + "trailing"), out var type));
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData("CMND")]
    [InlineData("PPCR")]
    [InlineData("XXXX")]
    [InlineData("PRN")]
    [InlineData("")]
    public void RejectsUnknownMagics(string magic)
    {
        Assert.False(FxConsoleProtocol.TryGetFrameType(Encoding.ASCII.GetBytes(magic), out _));
    }

    [Fact]
    public void DecodePrintTextStripsNulPaddingAndWhitespace()
    {
        var frame = new byte[FxConsoleProtocol.PrintPayloadOffset + 16];
        Encoding.UTF8.GetBytes("  hello 世界 \n").CopyTo(frame, FxConsoleProtocol.PrintPayloadOffset);

        Assert.Equal("hello 世界", FxConsoleProtocol.DecodePrintText(frame));
    }

    [Fact]
    public void DecodePrintTextOfHeaderOnlyFrameIsEmpty()
    {
        Assert.Equal(string.Empty, FxConsoleProtocol.DecodePrintText(new byte[FxConsoleProtocol.PrintPayloadOffset]));
        Assert.Equal(string.Empty, FxConsoleProtocol.DecodePrintText(new byte[FxConsoleProtocol.HeaderSize]));
    }

    [Fact]
    public void ConstantsMatchTheReverseEngineeredProtocol()
    {
        Assert.Equal(29200, FxConsoleProtocol.DefaultPort);
        Assert.Equal(12, FxConsoleProtocol.HeaderSize);
        Assert.Equal(40, FxConsoleProtocol.PrintPayloadOffset);
        Assert.Equal(0x00D3, FxConsoleProtocol.ProtocolVersion);
        Assert.Equal(TimeSpan.FromMilliseconds(25), FxConsoleProtocol.MinSendGap);
        Assert.Equal(new byte[] { 0x50, 0x50, 0x43, 0x52 }, FxConsoleProtocol.Handshake.ToArray());
    }
}
