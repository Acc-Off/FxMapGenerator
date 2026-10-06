// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author); the namespaces differ, and the two
// tests of the send gap measure it where the client sends (FxDeck's measure it where the emulator decodes).
using FxMapGenerator.App.FxConsole;
using static FxMapGenerator.App.Tests.FxConsole.ConsoleTestHelpers;

namespace FxMapGenerator.App.Tests.FxConsole;

/// <summary>Integration tests of <see cref="TcpFxConsoleClient"/> against the in-process emulator.</summary>
public class TcpFxConsoleClientTests
{
    private static async Task<FxConsoleEmulator> StartEmulatorAsync(FxConsoleEmulatorOptions? options = null)
    {
        var emulator = new FxConsoleEmulator(options ?? EmulatorOptions());
        await emulator.StartAsync();
        return emulator;
    }

    private static async Task<TcpFxConsoleClient> StartConnectedClientAsync(FxConsoleEmulator emulator)
    {
        var client = new TcpFxConsoleClient(ClientOptions(emulator.Port));
        client.Start();
        await WaitForAsync(() => client.State == FxConsoleConnectionState.Connected, "client connected");
        return client;
    }

    [Fact]
    public async Task ConnectsAndSendsTheHandshake()
    {
        await using var emulator = await StartEmulatorAsync();
        var handshakes = 0;
        emulator.HandshakeReceived += (_, _) => Interlocked.Increment(ref handshakes);
        var states = new List<FxConsoleConnectionState>();

        await using var client = new TcpFxConsoleClient(ClientOptions(emulator.Port));
        client.StateChanged += (_, e) => { lock (states) states.Add(e.Current); };
        Assert.Equal(FxConsoleConnectionState.Disconnected, client.State);

        client.Start();

        await WaitForAsync(() => client.State == FxConsoleConnectionState.Connected, "client connected");
        await WaitForAsync(() => Volatile.Read(ref handshakes) == 1, "PPCR received by the emulator");
        Assert.Equal([FxConsoleConnectionState.Connecting, FxConsoleConnectionState.Connected], states);
        Assert.Equal(1, emulator.ActiveConnections);
    }

    [Fact]
    public async Task SendDeliversTheCommandToTheGame()
    {
        await using var emulator = await StartEmulatorAsync();
        await using var client = await StartConnectedClientAsync(emulator);

        Assert.True(await client.SendAsync("e wave"));

        await WaitForAsync(() => emulator.ReceivedCommands.Contains("e wave"), "command received");
        Assert.Equal(["e wave"], emulator.ReceivedCommands);
    }

    [Fact]
    public async Task SendRoundTripsMultibyteText()
    {
        await using var emulator = await StartEmulatorAsync();
        await using var client = await StartConnectedClientAsync(emulator);
        const string command = "say こんにちは 👋 ünïcödé";

        Assert.True(await client.SendAsync(command));

        await WaitForAsync(() => emulator.ReceivedCommands.Count == 1, "command received");
        Assert.Equal(command, emulator.ReceivedCommands[0]);
    }

    [Fact]
    public async Task ConsecutiveSendsAreSpacedByTheProtocolGap()
    {
        await using var emulator = await StartEmulatorAsync();
        var received = new List<EmulatorCommandEventArgs>();
        emulator.CommandReceived += (_, e) => { lock (received) received.Add(e); };
        await using var client = await StartConnectedClientAsync(emulator);

        var called = new long[5];
        var returned = new long[5];
        for (var i = 0; i < 5; i++)
        {
            called[i] = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.True(await client.SendAsync($"cmd {i}"));
            returned[i] = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        await WaitForAsync(() => received.Count == 5, "all commands received");
        Assert.Equal(["cmd 0", "cmd 1", "cmd 2", "cmd 3", "cmd 4"], received.Select(r => r.Command));
        // The gap is measured where the client sends, not where the emulator decodes: on a busy machine the reader can
        // find two commands waiting in the socket and decode them one right after the other. A command goes out no
        // earlier than its send was called and no later than that send returned, so from the call of one send to the
        // return of the next at least one gap has passed, however busy the machine is.
        for (var i = 1; i < 5; i++)
        {
            var gap = Between(called[i - 1], returned[i]);
            Assert.True(gap >= TimeSpan.FromMilliseconds(20), $"gap between command {i - 1} and {i} was {gap.TotalMilliseconds:0.0} ms");
        }

        var total = Between(called[0], returned[4]);
        Assert.True(total >= TimeSpan.FromMilliseconds(4 * 20), $"five sends took only {total.TotalMilliseconds:0.0} ms");
    }

    [Fact]
    public async Task ConcurrentSendsAreSerialisedAndSpaced()
    {
        await using var emulator = await StartEmulatorAsync();
        var received = new List<EmulatorCommandEventArgs>();
        emulator.CommandReceived += (_, e) => { lock (received) received.Add(e); };
        await using var client = await StartConnectedClientAsync(emulator);

        var called = System.Diagnostics.Stopwatch.GetTimestamp();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => client.SendAsync($"parallel {i}")));
        var returned = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.All(results, Assert.True);
        await WaitForAsync(() => received.Count == 4, "all commands received");
        // serialised: each command arrives whole and once; spaced: four sends one after the other cannot all have
        // returned before three gaps have passed (measured where the client sends, as above)
        Assert.Equal(Enumerable.Range(0, 4).Select(i => $"parallel {i}"), received.Select(r => r.Command).Order());
        var total = Between(called, returned);
        Assert.True(total >= TimeSpan.FromMilliseconds(3 * 20), $"four sends took only {total.TotalMilliseconds:0.0} ms");
    }

    [Fact]
    public async Task ReceivesPrintLinesFromTheGame()
    {
        await using var emulator = await StartEmulatorAsync();
        await using var client = await StartConnectedClientAsync(emulator);
        var lines = new List<string>();
        client.LineReceived += (_, e) => { lock (lines) lines.Add(e.Line); };

        await emulator.BroadcastPrintAsync("  [script] hello world  ");
        await client.SendAsync("e wave");

        await WaitForAsync(() => lines.Count == 2, "two PRNT lines");
        Assert.Equal(["[script] hello world", "emulator: e wave"], lines);
    }

    [Fact]
    public async Task IgnoresHandshakeAckButExposesItAsAFrame()
    {
        await using var emulator = await StartEmulatorAsync();
        var frames = new List<FxConsoleFrameType>();
        var lines = new List<string>();
        await using var client = new TcpFxConsoleClient(ClientOptions(emulator.Port));
        client.FrameReceived += (_, f) => { lock (frames) frames.Add(f.Type); };
        client.LineReceived += (_, e) => { lock (lines) lines.Add(e.Line); };

        client.Start();

        await WaitForAsync(() => frames.Contains(FxConsoleFrameType.AppInfo), "AINF frame received");
        Assert.Empty(lines);
    }

    [Fact]
    public async Task SurvivesGarbageAndSplitReplies()
    {
        var options = EmulatorOptions();
        options.PrefixGarbage = true;
        options.SplitReplies = true;
        await using var emulator = await StartEmulatorAsync(options);
        await using var client = await StartConnectedClientAsync(emulator);
        var lines = new List<string>();
        client.LineReceived += (_, e) => { lock (lines) lines.Add(e.Line); };

        await client.SendAsync("e wave");
        await client.SendAsync("e dance");

        await WaitForAsync(() => lines.Count == 2, "both replies decoded");
        Assert.Equal(["emulator: e wave", "emulator: e dance"], lines);
    }

    [Fact]
    public async Task SendFailsFastWhenTheGameIsNotRunning()
    {
        var port = GetFreePort();
        var states = new List<FxConsoleConnectionState>();
        await using var client = new TcpFxConsoleClient(ClientOptions(port));
        client.StateChanged += (_, e) => { lock (states) states.Add(e.Current); };

        client.Start();
        await WaitForAsync(() => client.State == FxConsoleConnectionState.Disconnected && states.Count >= 2, "first connect attempt failed");

        Assert.Equal([FxConsoleConnectionState.Connecting, FxConsoleConnectionState.Disconnected], states.Take(2));
        Assert.False(await client.SendAsync("e wave"));
    }

    [Fact]
    public async Task SendRightAfterStartWaitsForTheConnection()
    {
        await using var emulator = await StartEmulatorAsync();
        await using var client = new TcpFxConsoleClient(ClientOptions(emulator.Port));

        client.Start();
        var sent = await client.SendAsync("e wave");

        Assert.True(sent);
        await WaitForAsync(() => emulator.ReceivedCommands.Contains("e wave"), "command received");
    }

    [Fact]
    public async Task SendDuringReconnectWaitsForTheNewConnection()
    {
        await using var emulator = await StartEmulatorAsync();
        var received = new List<EmulatorCommandEventArgs>();
        emulator.CommandReceived += (_, e) => { lock (received) received.Add(e); };
        await using var client = await StartConnectedClientAsync(emulator);

        await emulator.DisconnectAllAsync();
        await WaitForAsync(() => client.State == FxConsoleConnectionState.Connecting || emulator.TotalConnections == 2, "client noticed the drop");
        var sent = await client.SendAsync("e wave");

        Assert.True(sent);
        await WaitForAsync(() => received.Count == 1, "command received");
        Assert.Equal(2, received[0].ConnectionId);
    }

    [Fact]
    public async Task SendGivesUpWhenTheConnectionDoesNotComeBackInTime()
    {
        // A non-routable address keeps the connect attempt pending (or fails at once); either way the send must not hang.
        var options = ClientOptions(29200);
        options.Host = "10.255.255.1";
        options.ConnectTimeout = TimeSpan.FromSeconds(10);
        options.SendConnectWait = TimeSpan.FromMilliseconds(200);
        await using var client = new TcpFxConsoleClient(options);
        client.Start();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sent = await client.SendAsync("e wave");

        Assert.False(sent);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"send took {watch.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task ReconnectsAfterTheGamesIdleTimeout()
    {
        await using var emulator = await StartEmulatorAsync(EmulatorOptions(idleTimeout: TimeSpan.FromMilliseconds(200)));
        await using var client = await StartConnectedClientAsync(emulator);

        await WaitForAsync(() => emulator.TotalConnections >= 3, "several idle reconnects");
        await WaitForAsync(() => client.State == FxConsoleConnectionState.Connected, "client connected again");

        Assert.True(await client.SendAsync("e wave"));
        await WaitForAsync(() => emulator.ReceivedCommands.Contains("e wave"), "command received after reconnect");
    }

    [Fact]
    public async Task ReconnectsWhenTheGameDropsTheConnection()
    {
        await using var emulator = await StartEmulatorAsync();
        await using var client = await StartConnectedClientAsync(emulator);

        await emulator.DisconnectAllAsync();

        await WaitForAsync(() => emulator.TotalConnections == 2 && client.State == FxConsoleConnectionState.Connected, "reconnected");
        Assert.True(await client.SendAsync("e wave"));
        await WaitForAsync(() => emulator.ReceivedCommands.Contains("e wave"), "command received after reconnect");
    }

    [Fact]
    public async Task ReconnectsWhenTheGameIsRestarted()
    {
        var port = GetFreePort();
        var first = await StartEmulatorAsync(EmulatorOptions(port));
        await using var client = await StartConnectedClientAsync(first);

        await first.StopAsync();
        await WaitForAsync(() => client.State != FxConsoleConnectionState.Connected, "client noticed the game exited");
        Assert.False(await client.SendAsync("e wave"));
        await Task.Delay(300); // let a few reconnect attempts fail

        await using var second = await StartEmulatorAsync(EmulatorOptions(port));
        await WaitForAsync(() => client.State == FxConsoleConnectionState.Connected, "client reconnected to the restarted game");

        Assert.True(await client.SendAsync("e dance"));
        await WaitForAsync(() => second.ReceivedCommands.Contains("e dance"), "command received by the restarted game");
        Assert.Empty(first.ReceivedCommands);
    }

    [Fact]
    public async Task StopClosesTheConnectionAndStopsReconnecting()
    {
        await using var emulator = await StartEmulatorAsync();
        await using var client = await StartConnectedClientAsync(emulator);

        await client.StopAsync();

        Assert.Equal(FxConsoleConnectionState.Disconnected, client.State);
        await WaitForAsync(() => emulator.ActiveConnections == 0, "emulator saw the disconnect");
        Assert.False(await client.SendAsync("e wave"));
        await Task.Delay(300);
        Assert.Equal(1, emulator.TotalConnections);
    }

    [Fact]
    public async Task StartIsIdempotent()
    {
        await using var emulator = await StartEmulatorAsync();
        await using var client = await StartConnectedClientAsync(emulator);

        client.Start();
        client.Start();
        await Task.Delay(200);

        Assert.Equal(1, emulator.TotalConnections);
    }

    [Fact]
    public async Task SendRejectsInvalidCommandsEvenWhenDisconnected()
    {
        await using var client = new TcpFxConsoleClient(ClientOptions(GetFreePort()));

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync("a\nb"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(""));
    }

    [Fact]
    public async Task DisposedClientRejectsUse()
    {
        var client = new TcpFxConsoleClient(ClientOptions(GetFreePort()));
        await client.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(client.Start);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync("e wave"));
    }
}
