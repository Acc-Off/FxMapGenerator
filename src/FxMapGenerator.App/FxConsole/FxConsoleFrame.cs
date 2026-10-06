// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author): the console socket client.
// Only the namespace differs; keep the two in step.
namespace FxMapGenerator.App.FxConsole;

/// <summary>Kinds of frames the game sends to us.</summary>
public enum FxConsoleFrameType
{
    /// <summary>PRNT — a line of console output.</summary>
    Print,

    /// <summary>CHAN — channel metadata. Ignored.</summary>
    Channel,

    /// <summary>CVAR — console variable update. Ignored.</summary>
    Cvar,

    /// <summary>AINF — acknowledgement of the PPCR handshake. Ignored.</summary>
    AppInfo,
}

/// <summary>A complete incoming frame, header included.</summary>
public sealed class FxConsoleFrame
{
    public FxConsoleFrame(FxConsoleFrameType type, byte[] data)
    {
        Type = type;
        Data = data;
    }

    public FxConsoleFrameType Type { get; }

    /// <summary>The whole frame as received (header + body).</summary>
    public byte[] Data { get; }

    /// <summary>Decoded console text for <see cref="FxConsoleFrameType.Print"/> frames; <c>null</c> otherwise.</summary>
    public string? Text => Type == FxConsoleFrameType.Print ? FxConsoleProtocol.DecodePrintText(Data) : null;
}
