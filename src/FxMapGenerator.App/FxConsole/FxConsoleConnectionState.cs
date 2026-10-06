// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author): the console socket client.
// Only the namespace differs; keep the two in step.
namespace FxMapGenerator.App.FxConsole;

/// <summary>Connection state towards the game's console socket.</summary>
public enum FxConsoleConnectionState
{
    /// <summary>Not connected (the game is probably not running). Reconnection attempts continue in the background.</summary>
    Disconnected,

    /// <summary>Connecting for the first time, or reconnecting right after an established connection dropped.</summary>
    Connecting,

    /// <summary>Connected and handshake sent; commands can be delivered.</summary>
    Connected,
}

public sealed class FxConsoleStateChangedEventArgs : EventArgs
{
    public FxConsoleStateChangedEventArgs(FxConsoleConnectionState previous, FxConsoleConnectionState current)
    {
        Previous = previous;
        Current = current;
    }

    public FxConsoleConnectionState Previous { get; }

    public FxConsoleConnectionState Current { get; }
}

public sealed class FxConsoleLineEventArgs : EventArgs
{
    public FxConsoleLineEventArgs(string line)
    {
        Line = line;
    }

    /// <summary>One line of console output (PRNT), already trimmed.</summary>
    public string Line { get; }
}
