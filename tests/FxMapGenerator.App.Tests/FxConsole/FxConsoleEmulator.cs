// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author); the namespace differs and Responder is added.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FxMapGenerator.App.Tests.FxConsole;

public sealed class EmulatorCommandEventArgs : EventArgs
{
    public EmulatorCommandEventArgs(int connectionId, string command)
    {
        ConnectionId = connectionId;
        Command = command;
        Timestamp = Stopwatch.GetTimestamp();
    }

    public int ConnectionId { get; }

    public string Command { get; }

    /// <summary><see cref="Stopwatch"/> timestamp taken when the command was decoded.</summary>
    public long Timestamp { get; }
}

/// <summary>
/// Speaks enough of the FiveM/RedM console protocol to test the console client without the game:
/// decodes CMND frames, answers the PPCR handshake with AINF, replies to commands with PRNT
/// and drops idle connections like the real client does.
/// <para>
/// This is deliberately an independent implementation of the wire format (it does not reference
/// the client's codec) so that integration tests catch symmetric encoding bugs.
/// </para>
/// </summary>
public sealed class FxConsoleEmulator : IAsyncDisposable
{
    private const int HeaderSize = 12;
    private const int PrintPayloadOffset = 40;
    private const int MaxPayloadSize = 1 << 20;

    private static readonly byte[] Handshake = "PPCR"u8.ToArray();
    private static readonly byte[] CommandMagic = "CMND"u8.ToArray();
    private static readonly byte[] PrintMagic = "PRNT"u8.ToArray();
    private static readonly byte[] AppInfoMagic = "AINF"u8.ToArray();
    private static readonly byte[] Garbage = "!!junk!!not-a-frame!!"u8.ToArray();

    private readonly FxConsoleEmulatorOptions _options;
    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private readonly List<string> _received = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _nextConnectionId;

    public FxConsoleEmulator(FxConsoleEmulatorOptions? options = null)
    {
        _options = options ?? new FxConsoleEmulatorOptions();
    }

    /// <summary>Port actually bound (differs from the option when it was 0).</summary>
    public int Port { get; private set; }

    public int ActiveConnections => _connections.Count;

    /// <summary>Connections accepted since construction (including closed ones).</summary>
    public int TotalConnections => Volatile.Read(ref _nextConnectionId);

    public IReadOnlyList<string> ReceivedCommands
    {
        get
        {
            lock (_received)
            {
                return _received.ToArray();
            }
        }
    }

    public event EventHandler<int>? ClientConnected;

    public event EventHandler<int>? ClientDisconnected;

    public event EventHandler<int>? HandshakeReceived;

    public event EventHandler<EmulatorCommandEventArgs>? CommandReceived;

    /// <exception cref="SocketException">The port is already in use.</exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The emulator is already running.");
        }

        var listener = new TcpListener(IPAddress.Parse(_options.Host), _options.Port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token), cancellationToken);
        Log($"Listening on {_options.Host}:{Port}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null)
        {
            return;
        }

        var cts = _cts!;
        cts.Cancel();
        listener.Stop();
        await DisconnectAllAsync().ConfigureAwait(false);

        try
        {
            await _acceptLoop!.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        cts.Dispose();
        _cts = null;
        _acceptLoop = null;
        Log("Stopped");
    }

    /// <summary>Closes every client connection (simulates the game exiting) while keeping the listener open.</summary>
    public async Task DisconnectAllAsync()
    {
        var connections = _connections.Values.ToArray();
        foreach (var connection in connections)
        {
            connection.Close();
        }

        await Task.WhenAll(connections.Select(c => c.Task)).ConfigureAwait(false);
    }

    /// <summary>Sends a PRNT frame with <paramref name="text"/> to every connected client.</summary>
    public async Task BroadcastPrintAsync(string text)
    {
        foreach (var connection in _connections.Values)
        {
            await SendFrameAsync(connection, PrintMagic, text).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            var id = Interlocked.Increment(ref _nextConnectionId);
            var connection = new Connection(id, tcp);
            _connections[id] = connection;
            connection.Task = HandleConnectionAsync(connection, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(Connection connection, CancellationToken cancellationToken)
    {
        Log($"[+] Connected #{connection.Id}: {connection.RemoteEndPoint}");
        ClientConnected?.Invoke(this, connection.Id);

        var buffer = new byte[8192];
        var pending = new byte[8192];
        var pendingLength = 0;

        try
        {
            while (true)
            {
                int read;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    if (_options.IdleTimeout > TimeSpan.Zero)
                    {
                        readCts.CancelAfter(_options.IdleTimeout);
                    }

                    try
                    {
                        read = await connection.Stream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Log($"[~] Idle for {_options.IdleTimeout.TotalSeconds:0.#}s, closing #{connection.Id}");
                        break;
                    }
                }

                if (read == 0)
                {
                    break;
                }

                if (pendingLength + read > pending.Length)
                {
                    Array.Resize(ref pending, Math.Max(pending.Length * 2, pendingLength + read));
                }

                Buffer.BlockCopy(buffer, 0, pending, pendingLength, read);
                pendingLength += read;

                var consumed = ProcessMessages(connection, pending.AsSpan(0, pendingLength));
                if (consumed > 0)
                {
                    Buffer.BlockCopy(pending, consumed, pending, 0, pendingLength - consumed);
                    pendingLength -= consumed;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // connection torn down
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            connection.Dispose();
            Log($"[-] Disconnected #{connection.Id}");
            ClientDisconnected?.Invoke(this, connection.Id);
        }
    }

    /// <summary>Decodes every complete message in <paramref name="data"/> and returns the number of bytes consumed.</summary>
    private int ProcessMessages(Connection connection, ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var remaining = data[offset..];

            // The client opens with a bare 4-byte PPCR handshake, not a framed message.
            if (remaining.Length >= 4 && remaining[..4].SequenceEqual(Handshake))
            {
                offset += 4;
                OnHandshake(connection);
                continue;
            }

            if (remaining.Length < HeaderSize)
            {
                break;
            }

            if (!remaining[..4].SequenceEqual(CommandMagic))
            {
                // Unknown byte: skip it so a stray byte cannot swallow the frames that follow.
                offset++;
                continue;
            }

            var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(remaining.Slice(6, 4));
            if (payloadLength > MaxPayloadSize)
            {
                offset++;
                continue;
            }

            var frameSize = HeaderSize + (int)payloadLength;
            if (remaining.Length < frameSize)
            {
                break; // incomplete, wait for more
            }

            // payload = UTF-8 command + '\n' + '\0'
            var body = remaining.Slice(HeaderSize, (int)payloadLength);
            if (body.Length > 0 && body[^1] == 0)
            {
                body = body[..^1];
            }

            var command = Encoding.UTF8.GetString(body).TrimEnd('\n');
            offset += frameSize;
            OnCommand(connection, command);
        }

        return offset;
    }

    private void OnHandshake(Connection connection)
    {
        Log($"[{DateTime.Now:HH:mm:ss}] #{connection.Id} handshake (PPCR) -> AINF");
        HandshakeReceived?.Invoke(this, connection.Id);
        _ = SendFrameAsync(connection, AppInfoMagic, "emulator");
    }

    private void OnCommand(Connection connection, string command)
    {
        Log($"[{DateTime.Now:HH:mm:ss}] #{connection.Id} > {command}");
        lock (_received)
        {
            _received.Add(command);
        }

        CommandReceived?.Invoke(this, new EmulatorCommandEventArgs(connection.Id, command));

        if (_options.Responder is { } respond)
        {
            var lines = respond(command).ToList();
            var previous = connection.Replies;
            connection.Replies = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                foreach (var line in lines) await SendFrameAsync(connection, PrintMagic, line).ConfigureAwait(false);
            });
            return;
        }

        if (!_options.ReplyToCommands)
        {
            return;
        }

        // the replies go out in the order of their commands (each waits for the one before it): two commands read at
        // once would otherwise be answered by two tasks in whichever order the thread pool ran them
        var reply = $"emulator: {command}";
        var before = connection.Replies;
        connection.Replies = Task.Run(async () =>
        {
            if (_options.ReplyDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.ReplyDelay).ConfigureAwait(false);
            }

            await before.ConfigureAwait(false);
            await SendFrameAsync(connection, PrintMagic, reply).ConfigureAwait(false);
            Log($"[{DateTime.Now:HH:mm:ss}] #{connection.Id} < {reply}");
        });
    }

    /// <summary>
    /// Builds an incoming (game → client) frame. The length field is the TOTAL frame size, header included —
    /// the opposite of the outgoing CMND convention.
    /// </summary>
    private static byte[] BuildFrame(byte[] magic, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text + "\0");
        var total = PrintPayloadOffset + payload.Length;
        var frame = new byte[total];
        magic.CopyTo(frame, 0);
        frame[4] = 0x00;
        frame[5] = 0xD3;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(6, 4), (uint)total);
        payload.CopyTo(frame, PrintPayloadOffset);
        return frame;
    }

    private async Task SendFrameAsync(Connection connection, byte[] magic, string text)
    {
        var frame = BuildFrame(magic, text);
        if (_options.PrefixGarbage)
        {
            // The junk must share a TCP chunk with the frame to exercise the client's resync path.
            frame = [.. Garbage, .. frame];
        }

        await connection.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_options.SplitReplies && frame.Length >= 8)
            {
                var cut = frame.Length / 2;
                await connection.Stream.WriteAsync(frame.AsMemory(0, cut)).ConfigureAwait(false);
                await Task.Delay(10).ConfigureAwait(false);
                await connection.Stream.WriteAsync(frame.AsMemory(cut)).ConfigureAwait(false);
            }
            else
            {
                await connection.Stream.WriteAsync(frame).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // client went away
        }
        finally
        {
            connection.WriteLock.Release();
        }
    }

    private void Log(string message) => _options.Log?.WriteLine(message);

    private sealed class Connection : IDisposable
    {
        private readonly TcpClient _tcp;

        public Connection(int id, TcpClient tcp)
        {
            Id = id;
            _tcp = tcp;
            RemoteEndPoint = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
            Stream = tcp.GetStream();
        }

        public int Id { get; }

        public string RemoteEndPoint { get; }

        public NetworkStream Stream { get; }

        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>The scripted replies so far, so that the replies to consecutive commands come out in order.</summary>
        public Task Replies { get; set; } = Task.CompletedTask;

        public void Close() => _tcp.Close();

        public void Dispose()
        {
            _tcp.Dispose();
            WriteLock.Dispose();
        }
    }
}
