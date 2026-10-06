// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author); the namespace differs and Responder is added.
namespace FxMapGenerator.App.Tests.FxConsole;

public sealed class FxConsoleEmulatorOptions
{
    /// <summary>IP address to bind. Use 0.0.0.0 to accept connections from another machine.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Port to listen on. 0 picks a free port (see <see cref="FxConsoleEmulator.Port"/>).</summary>
    public int Port { get; set; } = 29200;

    /// <summary>
    /// The real game closes the socket after ~5 s without traffic; the emulator does the same.
    /// <see cref="TimeSpan.Zero"/> disables it.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Reply to every received command with a PRNT frame.</summary>
    public bool ReplyToCommands { get; set; } = true;

    /// <summary>Delay before a reply is sent.</summary>
    public TimeSpan ReplyDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Prefix replies with junk bytes in the same TCP chunk, to exercise frame resync.</summary>
    public bool PrefixGarbage { get; set; }

    /// <summary>Send each reply in two TCP chunks, to exercise buffering.</summary>
    public bool SplitReplies { get; set; }

    /// <summary>
    /// A scripted game (added in FxMapGenerator): the lines to print for a command, in order, instead of the echo reply.
    /// </summary>
    public Func<string, IEnumerable<string>>? Responder { get; set; }

    /// <summary>Where to write the human-readable log. <c>null</c> = silent.</summary>
    public TextWriter? Log { get; set; }
}
