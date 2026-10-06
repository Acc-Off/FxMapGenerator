// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author): the console socket client.
// Only the namespace differs; keep the two in step.
namespace FxMapGenerator.App.FxConsole;

/// <summary>
/// Connection to the FiveM/RedM console socket.
/// Implementations keep the connection alive in the background and reconnect automatically.
/// Events are raised on thread-pool threads.
/// </summary>
public interface IFxConsoleClient : IAsyncDisposable
{
    FxConsoleConnectionState State { get; }

    event EventHandler<FxConsoleStateChangedEventArgs>? StateChanged;

    /// <summary>Raised for every non-empty PRNT line received from the game.</summary>
    event EventHandler<FxConsoleLineEventArgs>? LineReceived;

    /// <summary>Starts the background connect / reconnect loop. Idempotent.</summary>
    void Start();

    /// <summary>Closes the connection and stops reconnecting.</summary>
    Task StopAsync();

    /// <summary>Changes the game endpoint; the current connection is dropped and re-established against the new one.</summary>
    void UpdateEndpoint(string host, int port);

    /// <summary>
    /// Sends a single console command. Returns <c>false</c> immediately when <see cref="State"/> is
    /// <see cref="FxConsoleConnectionState.Disconnected"/> (game not running); while
    /// <see cref="FxConsoleConnectionState.Connecting"/> it waits briefly for the connection before giving up.
    /// Consecutive sends are spaced by at least the protocol's minimum gap.
    /// </summary>
    /// <exception cref="ArgumentException">The command is empty or contains NUL / line-break characters.</exception>
    Task<bool> SendAsync(string command, CancellationToken cancellationToken = default);
}
