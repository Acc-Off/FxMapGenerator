namespace FxMapGenerator.Core.Capture;

/// <summary>
/// The game's console as the capture uses it: one connection that sends commands and hears every line the game prints.
/// It connects again by itself when the game drops the connection (the game closes it after about 5 s without traffic).
/// </summary>
public interface IGameConsole : IAsyncDisposable
{
    bool Connected { get; }

    /// <summary>Every non-empty line the game prints, in order, on a background thread.</summary>
    event Action<string>? LineReceived;

    /// <summary>Starts connecting; calling it again does nothing.</summary>
    void Start();

    /// <summary>Sends one command; false when there is no connection.</summary>
    Task<bool> SendAsync(string command, CancellationToken token = default);
}

/// <summary>The game's window.</summary>
public interface IGameWindow
{
    /// <summary>The size of the window's drawing area; null when the game window is not there.</summary>
    (int Width, int Height)? ClientSize();

    /// <summary>The drawing area as it is now, taken without bringing the window to the front; null when it cannot be taken.</summary>
    Frame? Capture();
}

/// <summary>What the capture needs from the game: the program passes the real game, tests a fake one.</summary>
public interface IGameAccess
{
    /// <summary>A new console connection (not started yet). Only one can talk to the game at a time.</summary>
    IGameConsole OpenConsole(string host, int port);

    IGameWindow Window { get; }
}

/// <summary>An RGBA picture, rows from the top.</summary>
public sealed class Frame(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgba { get; } = rgba.Length == width * height * 4 ? rgba : throw new ArgumentException("RGBA size does not match the frame");

    public (byte R, byte G, byte B) Pixel(int x, int y)
    {
        int o = (y * Width + x) * 4;
        return (Rgba[o], Rgba[o + 1], Rgba[o + 2]);
    }
}
