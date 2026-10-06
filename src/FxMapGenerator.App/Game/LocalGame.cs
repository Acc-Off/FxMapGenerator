using FxMapGenerator.App.FxConsole;
using FxMapGenerator.Core.Capture;

namespace FxMapGenerator.App.Game;

/// <summary>
/// The game on this PC: its console socket (through the client copied from FxDeck) and the FiveM game window. The game
/// takes one console connection at a time, so only one console of this program is open at a time: the pre-check and a
/// run's visit do not overlap.
/// </summary>
public sealed class LocalGame(IGameWindow? window = null) : IGameAccess
{
    readonly object _sync = new();
    GameConsole? _open;

    /// <summary>The FiveM game window (tests put another in).</summary>
    public IGameWindow Window { get; } = window ?? GameWindow.Instance;

    /// <exception cref="InvalidOperationException">Another pre-check or run of this program is using the console.</exception>
    public IGameConsole OpenConsole(string host, int port)
    {
        lock (_sync)
        {
            if (_open is not null) throw new InvalidOperationException("the game console is in use by a pre-check or a run of this program; wait for it to end");
            var client = new TcpFxConsoleClient(new FxConsoleClientOptions { Host = host, Port = port });
            return _open = new GameConsole(client, closed => { lock (_sync) if (_open == closed) _open = null; });
        }
    }

    sealed class GameConsole : IGameConsole
    {
        readonly TcpFxConsoleClient _client;
        readonly Action<GameConsole> _closed;
        int _disposed;

        public GameConsole(TcpFxConsoleClient client, Action<GameConsole> closed)
        {
            _client = client;
            _closed = closed;
            _client.LineReceived += (_, e) => LineReceived?.Invoke(e.Line);
        }

        public bool Connected => _client.State == FxConsoleConnectionState.Connected;

        public event Action<string>? LineReceived;

        public void Start() => _client.Start();

        public Task<bool> SendAsync(string command, CancellationToken token = default) => _client.SendAsync(command, token);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await _client.DisposeAsync().ConfigureAwait(false); }
            finally { _closed(this); }
        }
    }
}
