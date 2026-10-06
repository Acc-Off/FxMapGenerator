// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author): the console socket client.
// Only the namespace differs; keep the two in step.
using System.Buffers.Binary;
using System.Text;

namespace FxMapGenerator.App.FxConsole;

/// <summary>
/// Wire format of the FiveM/RedM console socket.
/// This is an <b>unofficial</b> protocol reverse-engineered from fxcommands' connection-manager.ts;
/// every byte-level detail must stay inside this namespace.
/// </summary>
public static class FxConsoleProtocol
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 29200;

    /// <summary>magic(4) + protocol(2) + length(4) + padding(2).</summary>
    public const int HeaderSize = 12;

    /// <summary>PRNT frames carry 28 bytes of channel metadata between the header and the text.</summary>
    public const int PrintPayloadOffset = 40;

    /// <summary>Upper bound on a single incoming frame; anything larger is treated as a misread length.</summary>
    public const int MaxFrameSize = 1 << 20;

    /// <summary>Protocol field of every frame (211, big-endian).</summary>
    public const ushort ProtocolVersion = 0x00D3;

    /// <summary>Minimum gap between outgoing frames; the game silently drops frames sent faster than this.</summary>
    public static readonly TimeSpan MinSendGap = TimeSpan.FromMilliseconds(25);

    /// <summary>Raw 4 bytes sent right after connecting. Not a frame.</summary>
    public static ReadOnlySpan<byte> Handshake => "PPCR"u8;

    public static ReadOnlySpan<byte> CommandMagic => "CMND"u8;
    public static ReadOnlySpan<byte> PrintMagic => "PRNT"u8;
    public static ReadOnlySpan<byte> ChannelMagic => "CHAN"u8;
    public static ReadOnlySpan<byte> CvarMagic => "CVAR"u8;
    public static ReadOnlySpan<byte> AppInfoMagic => "AINF"u8;

    /// <summary>
    /// Builds an outgoing CMND frame.
    /// Layout: CMND | 00 D3 | length (BE, = payload bytes) | 00 00 | UTF-8 command | '\n' | '\0'.
    /// Note the length counts only the payload (command bytes + 2), not the header.
    /// </summary>
    /// <exception cref="ArgumentException">The command is empty or contains NUL / line-break characters.</exception>
    public static byte[] EncodeCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0)
        {
            throw new ArgumentException("A console command must not contain NUL or line-break characters.", nameof(command));
        }

        var commandBytes = Encoding.UTF8.GetByteCount(command);
        var payloadLength = commandBytes + 2; // '\n' + '\0'
        var frame = new byte[HeaderSize + payloadLength];

        CommandMagic.CopyTo(frame);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), ProtocolVersion);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(6, 4), (uint)payloadLength);
        // bytes 10..11 stay 0x00 0x00 (padding)
        Encoding.UTF8.GetBytes(command, frame.AsSpan(HeaderSize, commandBytes));
        frame[HeaderSize + commandBytes] = (byte)'\n';
        frame[HeaderSize + commandBytes + 1] = 0;
        return frame;
    }

    /// <summary>Maps the leading 4 bytes of an incoming frame to its type.</summary>
    public static bool TryGetFrameType(ReadOnlySpan<byte> magic, out FxConsoleFrameType type)
    {
        if (magic.Length >= 4)
        {
            magic = magic[..4];
            if (magic.SequenceEqual(PrintMagic)) { type = FxConsoleFrameType.Print; return true; }
            if (magic.SequenceEqual(ChannelMagic)) { type = FxConsoleFrameType.Channel; return true; }
            if (magic.SequenceEqual(CvarMagic)) { type = FxConsoleFrameType.Cvar; return true; }
            if (magic.SequenceEqual(AppInfoMagic)) { type = FxConsoleFrameType.AppInfo; return true; }
        }

        type = default;
        return false;
    }

    /// <summary>
    /// Extracts the text of a complete PRNT frame: bytes from offset 40 to the end,
    /// trailing NUL padding removed, then trimmed.
    /// </summary>
    public static string DecodePrintText(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= PrintPayloadOffset)
        {
            return string.Empty;
        }

        var payload = frame[PrintPayloadOffset..];
        var end = payload.Length;
        while (end > 0 && payload[end - 1] == 0)
        {
            end--;
        }

        return Encoding.UTF8.GetString(payload[..end]).Trim();
    }
}
