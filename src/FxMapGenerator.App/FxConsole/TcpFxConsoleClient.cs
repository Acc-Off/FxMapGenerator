// Copied from FxDeck 0.7.0 (https://github.com/Acc-Off/FxDeck, MIT, same author): the console socket client.
// Only the namespace differs; keep the two in step.
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FxMapGenerator.App.FxConsole;

/// <summary>
/// TCP implementation of <see cref="IFxConsoleClient"/>.
/// Keeps one connection to the game open, sends PPCR on connect, enforces the 25 ms send gap,
/// parses incoming frames and reconnects whenever the socket drops.
/// </summary>
public sealed class TcpFxConsoleClient : IFxConsoleClient
{
    private static readonly byte[] HandshakeBytes = FxConsoleProtocol.Handshake.ToArray();

    private readonly FxConsoleClientOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile TcpClient? _tcp;
    private volatile NetworkStream? _stream;
    private long _lastSendTimestamp; // Stopwatch ticks, 0 = never sent
    private FxConsoleConnectionState _state = FxConsoleConnectionState.Disconnected;

    /// <summary>Resolves to <c>true</c> when the current connect attempt succeeds, <c>false</c> when it fails.</summary>
    private TaskCompletionSource<bool> _connectAttempt = CompletedAttempt(false);
    private bool _disposed;

    public TcpFxConsoleClient(FxConsoleClientOptions? options = null, ILogger<TcpFxConsoleClient>? logger = null)
    {
        _options = options ?? new FxConsoleClientOptions();
        _logger = logger ?? NullLogger<TcpFxConsoleClient>.Instance;
    }

    public FxConsoleConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public event EventHandler<FxConsoleStateChangedEventArgs>? StateChanged;

    public event EventHandler<FxConsoleLineEventArgs>? LineReceived;

    /// <summary>Raised for every complete incoming frame, including the ignored CHAN / CVAR / AINF ones.</summary>
    public event EventHandler<FxConsoleFrame>? FrameReceived;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_runTask is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // Transition synchronously (the lock is re-entrant) so a SendAsync issued right after Start()
            // sees Connecting and waits for the first attempt instead of failing as Disconnected.
            SetState(FxConsoleConnectionState.Connecting);
            _runTask = Task.Run(() => RunAsync(token));
        }
    }

    public async Task StopAsync()
    {
        Task? run;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            run = _runTask;
            cts = _cts;
            _runTask = null;
            _cts = null;
        }

        if (run is null || cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        finally
        {
            cts.Dispose();
        }
    }

    public void UpdateEndpoint(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (host == _options.Host && port == _options.Port)
        {
            return;
        }

        _options.Host = host;
        _options.Port = port;
        _logger.LogInformation("Game endpoint changed to {Host}:{Port}; reconnecting", host, port);
        _tcp?.Close(); // ends the receive loop; the run loop reconnects with the new options
    }

    public async Task<bool> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = FxConsoleProtocol.EncodeCommand(command);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream is null && !await WaitForConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("Not connected; dropping command {Command}", command);
                return false;
            }

            await WaitForSendGapAsync(cancellationToken).ConfigureAwait(false);

            var stream = _stream;
            if (stream is null)
            {
                _logger.LogDebug("Connection dropped while waiting for the send gap; dropping command {Command}", command);
                return false;
            }

            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            _lastSendTimestamp = Stopwatch.GetTimestamp();
            _logger.LogDebug("Sent {Command}", command);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Failed to send {Command}", command);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _sendLock.Dispose();
    }

    /// <summary>
    /// While a connect attempt is in flight (start-up or reconnect after an idle drop) waits up to
    /// <see cref="FxConsoleClientOptions.SendConnectWait"/> for it. Returns <c>false</c> at once when disconnected.
    /// </summary>
    private async Task<bool> WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        Task<bool> attempt;
        lock (_sync)
        {
            switch (_state)
            {
                case FxConsoleConnectionState.Connected:
                    return _stream is not null;
                case FxConsoleConnectionState.Disconnected:
                    return false;
                default:
                    attempt = _connectAttempt.Task;
                    break;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completed = await Task.WhenAny(attempt, Task.Delay(_options.SendConnectWait, timeout.Token)).ConfigureAwait(false);
        if (completed != attempt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Gave up waiting for the connection after {Wait} ms", _options.SendConnectWait.TotalMilliseconds);
            return false;
        }

        timeout.Cancel(); // stop the delay timer
        return attempt.Result && _stream is not null;
    }

    private async Task WaitForSendGapAsync(CancellationToken cancellationToken)
    {
        var last = _lastSendTimestamp;
        if (last == 0)
        {
            return;
        }

        var remaining = _options.SendGap - Stopwatch.GetElapsedTime(last);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var wasConnected = false;
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                _tcp = tcp;
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connectCts.CancelAfter(_options.ConnectTimeout);
                    await tcp.ConnectAsync(_options.Host, _options.Port, connectCts.Token).ConfigureAwait(false);
                }

                var stream = tcp.GetStream();
                await stream.WriteAsync(HandshakeBytes, cancellationToken).ConfigureAwait(false);

                consecutiveFailures = 0;
                wasConnected = true;
                _stream = stream;
                _logger.LogInformation("Connected to {Host}:{Port}", _options.Host, _options.Port);
                SetState(FxConsoleConnectionState.Connected);

                await ReceiveLoopAsync(stream, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Connection closed by the game");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (wasConnected)
                {
                    _logger.LogWarning("Connection lost: {Message}", ex.Message);
                }
                else
                {
                    consecutiveFailures++;
                    _logger.LogDebug("Connect to {Host}:{Port} failed: {Message}", _options.Host, _options.Port, ex.Message);
                }
            }
            finally
            {
                _stream = null;
                _tcp = null;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            TimeSpan delay;
            if (wasConnected)
            {
                // An established connection dropped (typically the game's ~5 s idle timeout): retry right away.
                SetState(FxConsoleConnectionState.Connecting);
                delay = _options.ReconnectDelayAfterDrop;
            }
            else
            {
                SetState(FxConsoleConnectionState.Disconnected);
                delay = BackoffDelay(consecutiveFailures);
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _stream = null;
        SetState(FxConsoleConnectionState.Disconnected);
    }

    private static TaskCompletionSource<bool> CompletedAttempt(bool connected)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(connected);
        return source;
    }

    private TimeSpan BackoffDelay(int failures)
    {
        var exponent = Math.Clamp(failures - 1, 0, 16);
        var delay = _options.ReconnectDelayInitial * Math.Pow(2, exponent);
        return delay > _options.ReconnectDelayMax ? _options.ReconnectDelayMax : delay;
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var parser = new FxConsoleFrameParser();
        var frames = new List<FxConsoleFrame>();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return; // remote closed
            }

            parser.Feed(buffer.AsSpan(0, read), frames);
            foreach (var frame in frames)
            {
                Dispatch(frame);
            }

            frames.Clear();
        }
    }

    private void Dispatch(FxConsoleFrame frame)
    {
        try
        {
            FrameReceived?.Invoke(this, frame);
            if (frame.Type == FxConsoleFrameType.Print)
            {
                var text = frame.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    LineReceived?.Invoke(this, new FxConsoleLineEventArgs(text));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A frame event handler threw");
        }
    }

    private void SetState(FxConsoleConnectionState state)
    {
        FxConsoleConnectionState previous;
        lock (_sync)
        {
            if (_state == state)
            {
                return;
            }

            previous = _state;
            _state = state;

            switch (state)
            {
                case FxConsoleConnectionState.Connecting:
                    _connectAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    break;
                case FxConsoleConnectionState.Connected:
                    _connectAttempt.TrySetResult(true);
                    break;
                default:
                    _connectAttempt.TrySetResult(false);
                    break;
            }
        }

        _logger.LogDebug("State {Previous} -> {Current}", previous, state);
        try
        {
            StateChanged?.Invoke(this, new FxConsoleStateChangedEventArgs(previous, state));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A StateChanged handler threw");
        }
    }
}
