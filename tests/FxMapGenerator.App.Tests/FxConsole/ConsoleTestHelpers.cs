// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author); the namespace and the class name differ.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FxMapGenerator.App.FxConsole;

namespace FxMapGenerator.App.Tests.FxConsole;

internal static class ConsoleTestHelpers
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> elapses.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? DefaultTimeout;
        while (deadline.Elapsed < limit)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    public static async Task WaitForAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        Assert.True(await WaitUntilAsync(condition, timeout), $"Timed out waiting for: {what}");
    }

    /// <summary>Returns a loopback port that is currently free.</summary>
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static FxConsoleEmulatorOptions EmulatorOptions(int port = 0, TimeSpan? idleTimeout = null) => new()
    {
        Port = port,
        IdleTimeout = idleTimeout ?? TimeSpan.Zero,
    };

    /// <summary>Client options tuned so reconnect tests finish quickly.</summary>
    public static FxConsoleClientOptions ClientOptions(int port) => new()
    {
        Port = port,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ReconnectDelayAfterDrop = TimeSpan.FromMilliseconds(50),
        ReconnectDelayInitial = TimeSpan.FromMilliseconds(100),
        ReconnectDelayMax = TimeSpan.FromMilliseconds(300),
    };

    public static TimeSpan Between(long earlier, long later) => Stopwatch.GetElapsedTime(earlier, later);
}
