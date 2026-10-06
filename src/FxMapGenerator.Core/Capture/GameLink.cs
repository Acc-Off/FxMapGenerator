using System.Diagnostics;
using System.Globalization;
using FxMapGenerator.Core.Scan;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Capture;

/// <summary>
/// Talks to the fxmapgen-capture resource through the game console: sends a command and waits for the lines that answer
/// it, keeps the connection alive meanwhile (the game drops it after about 5 s without traffic), and writes the
/// conversation to a transcript (the rows of height grids only by their first and last line: the grid files hold them).
/// </summary>
public sealed class GameLink : IAsyncDisposable
{
    public const int Protocol = 1;
    const string Keepalive = "fxmapgen ping";
    /// <summary>How long a tile may take: the resource's own limits add up to about 56 s (scene 20, hover 7.5, collision 10, server 3, settle 15).</summary>
    public TimeSpan TileTimeout { get; init; } = TimeSpan.FromSeconds(75);
    /// <summary>How long a height grid may take (about 1.5 s in the game).</summary>
    public TimeSpan HmapTimeout { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>A scan (ground or roads) from the command to its DONE line.</summary>
    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(180);
    /// <summary>While waiting, a keepalive goes out when nothing was sent for this long.</summary>
    public TimeSpan KeepaliveEvery { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>How long HELLO may take (the resource waits up to 3 s for its server side).</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(8);

    readonly IGameConsole _console;
    readonly TextWriter? _transcript;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly object _sync = new();
    readonly List<Listener> _listeners = new();
    TimeSpan _lastSend = TimeSpan.Zero;
    int _lastSeq = -1;

    public GameLink(IGameConsole console, TextWriter? transcript = null)
    {
        _console = console;
        _transcript = transcript;
        _console.LineReceived += OnLine;
    }

    public bool Connected => _console.Connected;

    /// <summary>The highest request number seen in a READY or FAIL line (-1 before any).</summary>
    public int LastSeq
    {
        get { lock (_sync) return _lastSeq; }
    }

    /// <summary>Starts the connection and waits for it; false when it did not come within the timeout.</summary>
    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken token)
    {
        _console.Start();
        var sw = Stopwatch.StartNew();
        while (!_console.Connected)
        {
            if (sw.Elapsed >= timeout) return false;
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Sends a command that gets no answer (such as <c>stop</c> / <c>ensure</c> of a resource); false without a connection.</summary>
    public async Task<bool> SendAsync(string command, CancellationToken token)
    {
        if (command != Keepalive) Note("> " + command);
        bool ok = await _console.SendAsync(command, token).ConfigureAwait(false);
        lock (_sync) _lastSend = _clock.Elapsed;
        if (!ok) Note("  (not sent: no connection)");
        return ok;
    }

    public enum Take { No, Keep, Last }

    /// <summary>What a command got back: the lines taken, and whether the last one came before the timeout.</summary>
    public sealed record Heard(IReadOnlyList<ProtocolLine> Lines, bool Complete, bool Sent);

    /// <summary>
    /// Sends the command (none: only listens) and collects the resource's lines after it that <paramref name="take"/>
    /// keeps, until it marks one the last or the timeout. Keepalives go out while waiting.
    /// </summary>
    public async Task<Heard> ListenAsync(string? command, Func<ProtocolLine, Take> take, TimeSpan timeout, CancellationToken token)
    {
        var listener = new Listener(take);
        lock (_sync) _listeners.Add(listener);
        try
        {
            if (command is not null && !await SendAsync(command, token).ConfigureAwait(false))
                return new Heard(listener.Lines, false, false);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var left = timeout - sw.Elapsed;
                if (left <= TimeSpan.Zero) return new Heard(listener.Lines, false, true);
                if (await listener.WaitAsync(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), token).ConfigureAwait(false))
                    return new Heard(listener.Lines, true, true);
                await KeepAliveAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_sync) _listeners.Remove(listener);
        }
    }

    /// <summary>Sends the command and returns the first line after it that answers it; null when none came in time.</summary>
    public async Task<ProtocolLine?> AskAsync(string command, Func<ProtocolLine, bool> answers, TimeSpan timeout, CancellationToken token)
    {
        var heard = await ListenAsync(command, l => answers(l) ? Take.Last : Take.No, timeout, token).ConfigureAwait(false);
        return heard.Complete ? heard.Lines[^1] : null;
    }

    /// <summary>Waits, sending keepalives meanwhile (a plain wait of 5 s would let the game drop the connection).</summary>
    public async Task PauseAsync(TimeSpan time, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var left = time - sw.Elapsed;
            if (left <= TimeSpan.Zero) return;
            await Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            await KeepAliveAsync(token).ConfigureAwait(false);
        }
    }

    async Task KeepAliveAsync(CancellationToken token)
    {
        bool due;
        lock (_sync) due = _clock.Elapsed - _lastSend >= KeepaliveEvery;
        if (due) await SendAsync(Keepalive, token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ the commands

    public async Task<HelloReply?> HelloAsync(CancellationToken token)
    {
        var l = await AskAsync("fxmapgen hello", x => x.Word == "HELLO", HelloTimeout, token).ConfigureAwait(false);
        return l is null ? null : new HelloReply(l.Int("proto") ?? 0, l.Get("ver") ?? "", l.Get("res") ?? "", l.Get("server") ?? "none",
            l.Flag("ace"), l.Int("players") ?? -1, l.Int("build") ?? 0, l.Int("capture"));
    }

    public async Task<StatusReply?> StatusAsync(CancellationToken token)
    {
        var l = await AskAsync("fxmapgen status", x => x.Word == "STATUS", TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        return l is null ? null : StatusReply.From(l);
    }

    /// <summary>The state of the named resources on the game client (started, stopped, missing, ...); null without an answer.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> ResourcesAsync(IReadOnlyList<string> names, CancellationToken token)
    {
        if (names.Count == 0) return new Dictionary<string, string>();
        var heard = await ListenAsync("fxmapgen res " + string.Join(' ', names),
            l => l.Is("RES", "END") ? Take.Last : l.Word == "RES" && !l.Is("RES", "BEGIN") ? Take.Keep : Take.No,
            TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (!heard.Complete) return null;
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in heard.Lines)
        {
            var parts = l.Body.Split(' ');
            if (parts.Length == 3) states[parts[1]] = parts[2];
        }
        return states;
    }

    public async Task<bool> EnvOnAsync(CancellationToken token) =>
        await AskAsync("fxmapgen env on", x => x.Is("ENV", "on"), TimeSpan.FromSeconds(5), token).ConfigureAwait(false) is not null;

    /// <summary>env off: the resource sets the character down (up to 10 s) and asks its server side to refill; null without an answer.</summary>
    public async Task<EnvOffReply?> EnvOffAsync(CancellationToken token)
    {
        var heard = await ListenAsync("fxmapgen env off", l =>
            l.Is("ENV", "off") ? (l.Flag("already") ? Take.Last : Take.Keep)
            : l.Word == "REFILL" ? Take.Last : Take.No, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        var off = heard.Lines.FirstOrDefault(l => l.Is("ENV", "off"));
        if (off is null) return null;
        var refill = heard.Lines.FirstOrDefault(l => l.Word == "REFILL");
        return new EnvOffReply(off.Flag("already"), off.Flag("safe"), off.Number("x") ?? 0, off.Number("y") ?? 0, off.Number("z") ?? 0,
            refill is null ? null : refill.Flag("ok"), refill?.Get("via"));
    }

    /// <summary>
    /// Puts the camera over the block and waits for READY or FAIL of this request. Its request number is higher than
    /// <paramref name="afterSeq"/> (the resource's number before it), so a line of an earlier request (a READY that came
    /// after its wait timed out, the FAIL of one this request superseded) is not taken for it; a FAIL of this request, also
    /// "superseded" by a request someone else made, is. The resource says READY once the game's streaming requests have
    /// stayed at 0 for <paramref name="quietMs"/> (see <see cref="SettleProbe"/>).
    /// </summary>
    public async Task<TileReply> TileAsync(BlockId block, double fov, double margin, int afterSeq, int quietMs, CancellationToken token)
    {
        var command = string.Format(CultureInfo.InvariantCulture, "fxmapgen tile {0} {1} {2} {3} {4} {5}", WorldGrid.Zoom, block.Tx, block.Ty, fov, margin, quietMs);
        var heard = await ListenAsync(command, x =>
            (x.Word == "READY" && x.Int("seq") > afterSeq && x.Get("block") == block.Name)
            || (x.Word == "FAIL" && x.Int("seq") > afterSeq)
            || x.Word == "ERROR" ? Take.Last : Take.No, TileTimeout, token).ConfigureAwait(false);
        if (!heard.Sent) return new TileReply(TileOutcome.NotSent, null, "not sent: no connection to the game");
        if (!heard.Complete) return new TileReply(TileOutcome.Timeout, null, $"no READY within {TileTimeout.TotalSeconds:0.#} s");
        var line = heard.Lines[^1];
        return line.Word switch
        {
            "READY" => new TileReply(TileOutcome.Ready, line, ""),
            "FAIL" => new TileReply(TileOutcome.Fail, line, FailReason(line)),
            _ => new TileReply(TileOutcome.Error, line, line.Body),
        };
    }

    /// <summary>The height grid of the block in place: the HMAP lines from BEGIN to END.</summary>
    public async Task<HmapReply> HmapAsync(double step, CancellationToken token)
    {
        bool begun = false;
        var heard = await ListenAsync("fxmapgen hmap " + step.ToString(CultureInfo.InvariantCulture), l =>
        {
            if (!begun && l.Word == "ERROR") return Take.Last;
            if (l.Word != "HMAP") return Take.No;
            if (l.Is("HMAP", "BEGIN")) { begun = true; return Take.Keep; }
            if (!begun) return Take.No;
            return l.Is("HMAP", "END") || l.Is("HMAP", "ABORT") ? Take.Last : Take.Keep;
        }, HmapTimeout, token).ConfigureAwait(false);
        if (!heard.Sent) return new HmapReply(null, "not sent: no connection");
        if (!heard.Complete)
            return new HmapReply(null, heard.Lines.Count == 0 ? $"no height grid within {HmapTimeout.TotalSeconds:0.#} s" : $"the height grid stopped after {heard.Lines.Count} lines");
        var last = heard.Lines[^1];
        if (last.Word == "ERROR") return new HmapReply(null, last.Body);
        if (last.Is("HMAP", "ABORT")) return new HmapReply(null, "the height grid was cut off (HMAP ABORT)");
        var lines = heard.Lines.Select(l => l.Body).ToList();
        if (Satellite.HeightGrid.Problem(lines) is string problem) return new HmapReply(null, "the height grid came incomplete: " + problem);
        return new HmapReply(lines, null);
    }

    /// <summary>
    /// A scan of the block (<c>fxmapgen scan ground|roads</c>; the ground scan with the canopy probe when asked): its
    /// MSCAN lines from BEGIN to DONE, without the prefix. The resource streams the block itself (no tile needed).
    /// </summary>
    public async Task<ScanReply> ScanAsync(ScanKind kind, BlockId block, bool canopy, CancellationToken token)
    {
        string name = kind == ScanKind.Ground ? "ground" : "roads", lineKind = kind == ScanKind.Ground ? "mat" : "road";
        var command = string.Format(CultureInfo.InvariantCulture, "fxmapgen scan {0} {1} {2} {3}{4}", name, WorldGrid.Zoom, block.Tx, block.Ty,
            canopy && kind == ScanKind.Ground ? " canopy" : "");
        bool begun = false;
        var heard = await ListenAsync(command, l =>
        {
            if (!begun && l.Word == "ERROR") return Take.Last;
            if (l.Word != "MSCAN") return Take.No;
            if (!begun)
            {
                if (l.Is("MSCAN", "BEGIN") && l.Get("kind") == lineKind && l.Get("block") == block.Name) { begun = true; return Take.Keep; }
                return Take.No;
            }
            return (l.Is("MSCAN", "DONE") || l.Is("MSCAN", "ABORT")) && l.Get("block") == block.Name ? Take.Last : Take.Keep;
        }, ScanTimeout, token).ConfigureAwait(false);
        if (!heard.Sent) return new ScanReply(null, "not sent: no connection");
        if (!heard.Complete)
            return new ScanReply(null, heard.Lines.Count == 0 ? $"no {name} scan within {ScanTimeout.TotalSeconds:0.#} s" : $"the {name} scan stopped after {heard.Lines.Count} lines");
        var last = heard.Lines[^1];
        if (last.Word == "ERROR") return new ScanReply(null, last.Body);
        if (last.Is("MSCAN", "ABORT")) return new ScanReply(null, $"the {name} scan was cut off (MSCAN ABORT)");
        var lines = heard.Lines.Select(l => l.Body).ToList();
        if (Scan.ScanFile.Problem(lines) is string problem) return new ScanReply(null, $"the {name} scan came incomplete: {problem}");
        return new ScanReply(lines, null);
    }

    static string FailReason(ProtocolLine l) => l.Get("reason") switch
    {
        "timeout" => $"the scene did not settle in time (scene {l.Get("scene")} ms, collision {l.Get("coll")} ms, settle {l.Get("settle")} ms)",
        "refused" => l.Get("why") == "players"
            ? $"the server would not clear the world: {l.Get("players")} players are on"
            : "the server would not clear the world: no permission (command.fxmapgen)",
        "noserver" => "the resource's server side did not answer",
        "superseded" => "another request to the resource took this one's place",
        var r => "FAIL " + r,
    };

    // ------------------------------------------------------------------ lines

    void OnLine(string line)
    {
        var parsed = ProtocolLine.Parse(line);
        // the rows of a height grid or a scan stay out of the transcript (hundreds of lines a block)
        if (parsed is null || !(parsed.Body.StartsWith("HMAP j=", StringComparison.Ordinal) || IsScanRow(parsed.Body))) Note(line);
        if (parsed is null) return;
        Listener[] now;
        lock (_sync)
        {
            if (parsed.Word is "READY" or "FAIL" && parsed.Int("seq") is int seq && seq > _lastSeq) _lastSeq = seq;
            now = _listeners.ToArray();
        }
        foreach (var l in now) l.Offer(parsed);
    }

    /// <summary>A row of a scan: <c>MSCAN &lt;kind&gt; j=&lt;row&gt; ...</c>.</summary>
    static bool IsScanRow(string body)
    {
        if (!body.StartsWith("MSCAN ", StringComparison.Ordinal)) return false;
        int sp = body.IndexOf(' ', 6);
        return sp > 6 && char.IsLower(body[6]) && string.CompareOrdinal(body, sp + 1, "j=", 0, 2) == 0;
    }

    /// <summary>Writes a line of its own into the transcript (with the time since the link opened).</summary>
    public void Note(string text)
    {
        if (_transcript is null) return;
        lock (_transcript)
            _transcript.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,9:0.000} {1}", _clock.Elapsed.TotalSeconds, text));
    }

    public async ValueTask DisposeAsync()
    {
        _console.LineReceived -= OnLine;
        await _console.DisposeAsync().ConfigureAwait(false);
        if (_transcript is not null)
            lock (_transcript) _transcript.Flush();
    }

    sealed class Listener(Func<ProtocolLine, Take> take)
    {
        readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly List<ProtocolLine> _lines = new();

        public IReadOnlyList<ProtocolLine> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public void Offer(ProtocolLine line)
        {
            if (_done.Task.IsCompleted) return;
            var t = take(line);
            if (t == Take.No) return;
            lock (_lines) _lines.Add(line);
            if (t == Take.Last) _done.TrySetResult();
        }

        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            if (_done.Task.IsCompleted) return true;
            await Task.WhenAny(_done.Task, Task.Delay(timeout, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return _done.Task.IsCompleted;
        }
    }
}

/// <param name="Capture">What the resource's capture takes (raised with every change of the captured content); null when it does not say.</param>
public sealed record HelloReply(int Proto, string Version, string Resource, string ServerVersion, bool Ace, int Players, int Build, int? Capture = null)
{
    public bool ServerAnswered => ServerVersion != "none";
}

public sealed record StatusReply(bool Env, bool Ready, bool Busy, bool Hmap, int Seq, string Weather, int Hour, int Minute, int Peds, int Vehicles,
    int NearPeds, int NearVehicles, int Health, bool Dead, bool Frozen, double X, double Y, double Z, string Block)
{
    public static StatusReply From(ProtocolLine l) => new(l.Flag("env"), l.Flag("ready"), l.Flag("busy"), l.Flag("hmap"), l.Int("seq") ?? -1,
        l.Get("weather") ?? "", l.Int("hour") ?? -1, l.Int("minute") ?? -1, l.Int("peds") ?? -1, l.Int("vehicles") ?? -1,
        l.Int("near_peds") ?? -1, l.Int("near_vehicles") ?? -1, l.Int("health") ?? -1, l.Flag("dead"), l.Flag("frozen"),
        l.Number("px") ?? 0, l.Number("py") ?? 0, l.Number("pz") ?? 0, l.Get("block") ?? "none");
}

public sealed record EnvOffReply(bool Already, bool Safe, double X, double Y, double Z, bool? Refilled, string? RefillVia);

public enum TileOutcome { Ready, Fail, Error, Timeout, NotSent }

/// <param name="Reason">Why it is not READY, in words for the log and the failure list; empty for READY.</param>
public sealed record TileReply(TileOutcome Outcome, ProtocolLine? Line, string Reason)
{
    public int Seq => Line?.Int("seq") ?? -1;
}

public sealed record HmapReply(IReadOnlyList<string>? Lines, string? Failure);

/// <summary>The scan's MSCAN lines (BEGIN to DONE, without the prefix), or why there are none.</summary>
public sealed record ScanReply(IReadOnlyList<string>? Lines, string? Failure);
