// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author): the console socket client.
// Only the namespace differs; keep the two in step.
namespace FxMapGenerator.App.FxConsole;

public sealed class FxConsoleClientOptions
{
    /// <summary>Game host. Only differs from loopback for remote clients started with <c>-devcon</c>.</summary>
    public string Host { get; set; } = FxConsoleProtocol.DefaultHost;

    public int Port { get; set; } = FxConsoleProtocol.DefaultPort;

    /// <summary>Minimum gap between two outgoing frames.</summary>
    public TimeSpan SendGap { get; set; } = FxConsoleProtocol.MinSendGap;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Delay before reconnecting after an established connection dropped (e.g. the game's idle timeout).</summary>
    public TimeSpan ReconnectDelayAfterDrop { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long <see cref="IFxConsoleClient.SendAsync"/> waits for a connection while the client is
    /// <see cref="FxConsoleConnectionState.Connecting"/> (start-up, or reconnecting after the game's idle timeout).
    /// While <see cref="FxConsoleConnectionState.Disconnected"/> sends fail immediately.
    /// </summary>
    public TimeSpan SendConnectWait { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>First retry delay when connecting fails (game not running). Doubles up to <see cref="ReconnectDelayMax"/>.</summary>
    public TimeSpan ReconnectDelayInitial { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectDelayMax { get; set; } = TimeSpan.FromSeconds(5);
}
