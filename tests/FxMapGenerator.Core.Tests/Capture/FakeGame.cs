using System.Globalization;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Tests.Satellite;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Capture;

/// <summary>What goes wrong with one tile request of a block.</summary>
internal enum TileFault
{
    /// <summary>No READY or FAIL at all.</summary>
    Silent,
    /// <summary>The READY comes only when the next tile request arrives (after the wait for it timed out), just before that one's own lines.</summary>
    LateReady,
    /// <summary>FAIL reason=timeout.</summary>
    Unsettled,
    /// <summary>FAIL reason=refused why=players.</summary>
    Refused,
    /// <summary>The height grid stops with HMAP ABORT.</summary>
    HmapAbort,
}

/// <summary>
/// What is wrong with one frame taken from the window. Stale: the frame of the block before, still with its ready beacon.
/// Resize: the window becomes 1280 x 720 from then on.
/// </summary>
internal enum ShotFault { Black, Notification, Stale, Resize }

/// <summary>
/// A fake game for the capture. Its console answers the fxmapgen commands the way the resource does (the lines of
/// protocol 1, a few milliseconds later, in order) and <c>stop</c> / <c>ensure</c> of resources; its window shows the
/// made-up world of <see cref="SyntheticCapture"/> over the block in place, with the resource's beacon. Its scans are of
/// a plain block (tarmac 40 m high, no water, one street and zone, a road along the first row). Faults can be put on
/// single blocks.
/// <para>
/// Settling: the game's streaming requests reach 0 <see cref="TileMs"/> after a tile request; with
/// <see cref="HonourQuiet"/> READY comes the request's quietMs after that (else at once). For <see cref="CoarseMs"/> after
/// the requests reached 0 the picture is still coarse (patches of the town not drawn yet), then sharp.
/// </para>
/// </summary>
internal sealed class FakeGame : IGameAccess, IGameWindow, IDisposable
{
    readonly object _sync = new();
    readonly List<string> _received = new();
    readonly Dictionary<string, byte[]> _frames = new();
    readonly List<(long Due, string Line)> _outbox = new();
    readonly Thread _emitter;
    readonly CancellationTokenSource _stop = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    FakeConsole? _console;
    bool _env, _ready;
    int _seq;
    BlockId? _block;
    (BlockId Block, int Seq)? _previous;
    string? _lateReady;
    long _order;
    long _zeroAt;
    int _captures;

    public FakeGame(double qx = 1, double qy = 1)
    {
        Qx = qx;
        Qy = qy;
        _emitter = new Thread(Emit) { IsBackground = true, Name = "fake game" };
        _emitter.Start();
    }

    public double Qx { get; }
    public double Qy { get; }
    public string Version { get; set; } = "0.1.0";
    public int Proto { get; set; } = 1;
    /// <summary>The capture number HELLO says (what the capture takes); null leaves the key out, as a resource that does not say it.</summary>
    public int? CaptureNumber { get; set; } = 1;
    public bool ServerAnswers { get; set; } = true;
    public bool Ace { get; set; } = true;
    public int Players { get; set; } = 1;
    public bool Connectable { get; set; } = true;
    public const string CaptureName = "fxmapgen-capture";
    /// <summary>The capture resource runs (else the console knows no fxmapgen command, as FiveM's does).</summary>
    public bool CaptureRunning { get; set; } = true;
    /// <summary>The capture resource's folder is on the server (ensure can start it).</summary>
    public bool CaptureOnServer { get; set; } = true;
    /// <summary>The player may stop the capture resource (false: denied, it keeps running).</summary>
    public bool CanStopCapture { get; set; } = true;
    /// <summary>An ensure of the capture resource says nothing and starts nothing (as a server that drops a command silently).</summary>
    public bool EnsureSilent { get; set; }
    public (int Width, int Height)? Size { get; set; } = (SyntheticCapture.W, SyntheticCapture.H);
    /// <summary>How long the resource takes for a tile, before READY.</summary>
    public int TileMs { get; set; } = 15;
    /// <summary>Blocks of open sea: an even sea colour with a little ripple instead of the made-up land.</summary>
    public HashSet<string> Sea { get; } = new() { "z8_0_0" };
    /// <summary>Something that still draws on the screen (a HUD left running): a white rectangle while the capture environment is on.</summary>
    public (int X, int Y, int Width, int Height)? Hud { get; set; }
    public string Weather { get; set; } = "EXTRASUNNY";
    public int Hour { get; set; } = 12;
    public int NearPeds { get; set; }
    public Dictionary<string, string> Resources { get; } = new()
    {
        ["qbx_hud"] = "started", ["chat"] = "started", ["Renewed-Weathersync"] = "started", ["qbx_density"] = "started",
    };
    public Dictionary<string, Queue<TileFault>> TileFaults { get; } = new();
    public Dictionary<string, Queue<ShotFault>> ShotFaults { get; } = new();
    public string? DrawPerf { get; private set; }
    /// <summary>Where the player stands before env on (STATUS px / py); the default is in Los Santos.</summary>
    public (double X, double Y) Player { get; set; } = (200.0, -900.0);
    /// <summary>READY waits the tile request's quietMs after the streaming requests reached 0.</summary>
    public bool HonourQuiet { get; set; }
    /// <summary>How long after the streaming requests reached 0 the picture is still coarse.</summary>
    public int CoarseMs { get; set; }
    /// <summary>A few pixels that change from frame to frame (far too few to count as a changed frame).</summary>
    public bool Speckle { get; set; }
    /// <summary>Blocks whose picture never stops changing (something big moving).</summary>
    public HashSet<string> NeverStill { get; } = new();
    /// <summary>Water points of the resource's 5 x 5 grid per block (open sea blocks 25, else 0).</summary>
    public Dictionary<string, int> Water { get; } = new();
    public double Fps { get; set; } = 60.0;
    /// <summary>
    /// Plays back an earlier visit: a capture folder (<c>&lt;block&gt;.png</c>, <c>.cam.txt</c>, <c>.hmap</c>) whose frames,
    /// READY lines and height grids the game returns for the blocks it has (the made-up world for the others).
    /// </summary>
    public string? Replay { get; init; }
    /// <summary>Between the lines of a height grid (a real game sends a block's 850 lines in about a second).</summary>
    public int HmapLineMs { get; set; } = 5;
    /// <summary>Scans of a block that end with MSCAN ABORT instead of DONE (the count left per block).</summary>
    public Dictionary<string, int> ScanAborts { get; } = new();
    /// <summary>
    /// Ground scans of a block whose hz row 0 the game cuts as a line too long (the count left per block): its first part
    /// ends in a lone "-", the rest of the part is lost.
    /// </summary>
    public Dictionary<string, int> ScanCuts { get; } = new();
    int _scanSeq;

    public bool Env { get { lock (_sync) return _env; } }

    /// <summary>Every command the game got, keepalives left out.</summary>
    public IReadOnlyList<string> Received
    {
        get { lock (_sync) return _received.Where(c => c != "fxmapgen ping").ToList(); }
    }

    public int Pings { get { lock (_sync) return _received.Count(c => c == "fxmapgen ping"); } }

    public void Fault(string block, params TileFault[] faults)
    {
        lock (_sync) TileFaults[block] = new Queue<TileFault>(faults);
    }

    public void Fault(string block, params ShotFault[] faults)
    {
        lock (_sync) ShotFaults[block] = new Queue<ShotFault>(faults);
    }

    // ------------------------------------------------------------------ IGameAccess / IGameWindow

    public IGameConsole OpenConsole(string host, int port) => _console = new FakeConsole(this);

    public IGameWindow Window => this;

    public (int Width, int Height)? ClientSize() => Size;

    public Frame? Capture()
    {
        if (Size is null) return null;
        // the frame shows the game at the moment it was asked for (drawing a block's first frame takes a while)
        long at = _clock.ElapsedMilliseconds;
        lock (_sync)
        {
            var rgba = _block is { } b ? (byte[])FrameOf(b).Clone() : new byte[SyntheticCapture.W * SyntheticCapture.H * 4];
            var fault = _block is { } cur && ShotFaults.TryGetValue(cur.Name, out var q) && q.Count > 0 ? q.Dequeue() : (ShotFault?)null;
            if (fault == ShotFault.Black) return new Frame(SyntheticCapture.W, SyntheticCapture.H, new byte[rgba.Length]);
            if (fault == ShotFault.Stale && _previous is { } prev)
            {
                var old = (byte[])FrameOf(prev.Block).Clone();
                Beacon(old, true, prev.Seq);
                return new Frame(SyntheticCapture.W, SyntheticCapture.H, old);
            }
            if (fault == ShotFault.Resize) Size = (1280, 720);
            if (Size != (SyntheticCapture.W, SyntheticCapture.H)) return new Frame(Size!.Value.Width, Size.Value.Height, new byte[Size.Value.Width * Size.Value.Height * 4]);
            if (_block is { } shown && !Sea.Contains(shown.Name)) Unsettled(rgba, shown, at);
            if (_env) Beacon(rgba, _ready, _seq);
            if (_env && Hud is { } hud)
                for (int y = hud.Y; y < hud.Y + hud.Height; y++)
                    for (int x = hud.X; x < hud.X + hud.Width; x++) Put(rgba, x, y, 240, 240, 240);
            if (fault == ShotFault.Notification)
                for (int y = 1010; y < 1060; y++)
                    for (int x = 800; x < 1120; x++) Put(rgba, x, y, 84, 212, 140);
            return new Frame(SyntheticCapture.W, SyntheticCapture.H, rgba);
        }
    }

    byte[] FrameOf(BlockId b)
    {
        if (_frames.TryGetValue(b.Name, out var f)) return f;
        if (Replayed(b, ".png") is { } png)
        {
            _frames.Clear();                        // recorded frames are big: only the block in place
            var rgba = Core.Imaging.Images.LoadRgba(png, out var w, out var h);
            if ((w, h) != (SyntheticCapture.W, SyntheticCapture.H)) throw new InvalidDataException($"{png} is {w} x {h}");
            return _frames[b.Name] = rgba;
        }
        return _frames[b.Name] = Sea.Contains(b.Name) ? SeaFrame() : SyntheticCapture.Render(b, Qx, Qy);
    }

    /// <summary>The recorded file of the block with this extension, when playing back and it is there.</summary>
    string? Replayed(BlockId b, string extension) =>
        Replay is not null && File.Exists(Path.Combine(Replay, b.Name + extension)) ? Path.Combine(Replay, b.Name + extension) : null;

    /// <summary>A recorded line with the request number of now.</summary>
    static string WithSeq(string line, int seq) => System.Text.RegularExpressions.Regex.Replace(line, @"\bseq=\d+", $"seq={seq}");

    /// <summary>
    /// What the settling leaves on a land frame: dark patches (a town not drawn yet) for <see cref="CoarseMs"/> after the
    /// streaming requests reached 0, a big square that moves every frame on <see cref="NeverStill"/> blocks, a small
    /// one with <see cref="Speckle"/>.
    /// </summary>
    void Unsettled(byte[] rgba, BlockId b, long at)
    {
        int n = _captures++;
        if (at - _zeroAt < CoarseMs)
            for (int i = 0; i < 12; i++)
            {
                int x0 = 450 + (i % 4) * 260, y0 = 120 + (i / 4) * 300;
                Fill(rgba, x0, y0, 90, 90, 30, 30, 30);
            }
        if (NeverStill.Contains(b.Name)) Fill(rgba, 450 + (n * 97) % 900, 200 + (n * 61) % 600, 120, 120, 250, 250, 250);
        if (Speckle) Fill(rgba, 500 + (n * 37) % 800, 300 + (n * 53) % 500, 6, 6, 255, 0, 255);
    }

    static void Fill(byte[] rgba, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++) Put(rgba, x, y, r, g, b);
    }

    /// <summary>Open sea at noon with the fixed exposure: about (4, 43, 45), rippling by a few steps.</summary>
    static byte[] SeaFrame()
    {
        var rgba = new byte[SyntheticCapture.W * SyntheticCapture.H * 4];
        for (int y = 0; y < SyntheticCapture.H; y++)
            for (int x = 0; x < SyntheticCapture.W; x++)
            {
                int d = (int)(6 * Math.Sin(x * 0.21) * Math.Sin(y * 0.17));
                Put(rgba, x, y, (byte)Math.Max(0, 4 + d / 2), (byte)(43 + d), (byte)(45 + d));
            }
        return rgba;
    }

    /// <summary>The resource's beacon: [ready green / red][seq bit 0][1][2] as 4 x 4 px squares, white 1, black 0.</summary>
    public static void Beacon(byte[] rgba, bool ready, int seq)
    {
        for (int i = 0; i < 4; i++)
        {
            (byte r, byte g, byte b) c = i == 0 ? (ready ? ((byte)0, (byte)255, (byte)0) : ((byte)255, (byte)0, (byte)0))
                : ((seq >> (i - 1)) & 1) == 1 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0);
            for (int y = 0; y < 4; y++)
                for (int x = 4 * i; x < 4 * i + 4; x++) Put(rgba, x, y, c.r, c.g, c.b);
        }
    }

    static void Put(byte[] rgba, int x, int y, byte r, byte g, byte b)
    {
        int o = (y * SyntheticCapture.W + x) * 4;
        rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
    }

    // ------------------------------------------------------------------ commands

    void Handle(string command)
    {
        var w = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var inv = CultureInfo.InvariantCulture;
        lock (_sync)
        {
            _received.Add(command);
            if (_lateReady is not null && command.StartsWith("fxmapgen tile ", StringComparison.Ordinal))
            {
                Say(0, _lateReady);
                _lateReady = null;
            }
            switch (w[0])
            {
                // the capture resource itself: server commands need the permission (FiveM's words)
                case "refresh":
                    Say(2, Ace ? "Found 0 new resources." : "Access denied for command refresh.", raw: true);
                    return;
                case "stop" or "ensure" when w.Length > 1 && w[1] == CaptureName:
                    if (w[0] == "ensure" && EnsureSilent) { }
                    else if (!Ace || (w[0] == "stop" && !CanStopCapture)) Say(1, $"Access denied for command {w[0]}.", raw: true);
                    else if (w[0] == "stop")
                    {
                        CaptureRunning = false;
                        Say(2, "Stopping resource " + CaptureName, raw: true);
                    }
                    else if (!CaptureOnServer) Say(2, $"^3Couldn't find resource {CaptureName}.^7", raw: true);   // as a Qbox server prints it
                    else
                    {
                        CaptureRunning = true;
                        Say(2, "Started resource " + CaptureName, raw: true);
                    }
                    return;
                case "fxmapgen" when !CaptureRunning:
                    Say(1, "No such command fxmapgen.", raw: true);
                    return;
                case "stop":
                    Resources[w[1]] = "stopped";
                    Say(2, "Stopping resource " + w[1], raw: true);
                    return;
                case "ensure":
                    Resources[w[1]] = "started";
                    Say(2, "Started resource " + w[1], raw: true);
                    return;
                case "cl_drawPerf":
                    DrawPerf = w[1];
                    return;
                case not "fxmapgen":
                    Say(1, $"No such command {w[0]}.", raw: true);
                    return;
            }
            switch (w.Length > 1 ? w[1] : "")
            {
                case "ping":
                    return;
                case "hello":
                    Say(ServerAnswers ? 8 : 60, $"HELLO proto={Proto} ver={Version} res=fxmapgen-capture server={(ServerAnswers ? Version : "none")} " +
                        $"ace={(ServerAnswers && Ace ? 1 : 0)} players={(ServerAnswers ? Players : -1)} build=3258" + (CaptureNumber is { } cap ? $" capture={cap}" : ""));
                    return;
                case "status":
                {
                    var (px, py) = _env && _block is { } at ? at.Center : Player;
                    Say(3, string.Format(inv, "STATUS env={0} ready={1} busy=0 hmap=0 scan=0 seq={2} weather={3} hour={4} minute=0 peds={5} vehicles=0 " +
                        "near_peds={5} near_vehicles=0 health=200 dead=0 frozen={0} px={6:F1} py={7:F1} pz=38.0 block={8}",
                        B(_env), B(_ready), _seq, Weather, Hour, NearPeds, px, py, _block?.Name ?? "none"));
                    return;
                }
                case "res":
                    Say(2, $"RES BEGIN n={w.Length - 2}");
                    foreach (var n in w.Skip(2)) Say(2, $"RES {n} {Resources.GetValueOrDefault(n, "missing")}");
                    Say(2, "RES END");
                    return;
                case "env" when w.Length > 2 && w[2] == "on":
                    _env = true;
                    Say(2, "ENV on");
                    return;
                case "env" when w.Length > 2 && w[2] == "off":
                    if (!_env) { Say(2, "ENV off already=1"); return; }
                    _env = false;
                    _ready = false;
                    Say(20, "ENV off safe=1 x=200.0 y=-900.0 z=38.0");
                    Say(25, ServerAnswers && Ace ? "REFILL ok=1 via=qbx_core" : "REFILL ok=0 via=none error=permission");
                    return;
                case "tile":
                    Tile(BlockId.Parse($"z8_{w[3]}_{w[4]}"), double.Parse(w[5], inv), double.Parse(w[6], inv), w.Length > 7 ? int.Parse(w[7], inv) : 1500);
                    return;
                case "hmap":
                    Hmap(double.Parse(w[2], inv));
                    return;
                case "scan" when w.Length >= 6 && w[2] is "ground" or "roads":
                    Scan(w[2] == "ground", BlockId.Parse($"z8_{w[4]}_{w[5]}"), w.Length > 6 && w[6] == "canopy");
                    return;
                default:
                    Say(1, "ERROR usage: fxmapgen hello | status | ...");
                    return;
            }
        }
    }

    void Tile(BlockId b, double fov, double margin, int quiet)
    {
        if (_block is { } before) _previous = (before, _seq);
        _env = true;
        _seq++;
        _ready = false;
        _block = b;
        _zeroAt = _clock.ElapsedMilliseconds + TileMs;
        var fault = TileFaults.TryGetValue(b.Name, out var q) && q.Count > 0 ? q.Peek() : (TileFault?)null;
        if (fault is not TileFault.HmapAbort && fault is not null) q!.Dequeue();
        var inv = CultureInfo.InvariantCulture;
        var (cx, cy) = b.Center;
        double h = margin * (WorldGrid.BlockSize / 2) / Math.Tan(fov * Math.PI / 180 / 2);
        int water = Water.TryGetValue(b.Name, out var wp) ? wp : Sea.Contains(b.Name) ? 25 : 0;
        int wait = HonourQuiet ? quiet : 0;
        var ready = string.Format(inv, "READY seq={0} x={1:F4} y={2:F4} gz=0.000 h={3:F3} fov={4:F3} margin={5:F3} scene=400 coll=200 settle={6} quiet={7} " +
            "fps={8:F1} maxreq=4 water={9} veh=0 peds=0 block={10}", _seq, cx, cy, h, fov, margin, 160 + quiet, quiet, Fps, water, b.Name);
        if (Replayed(b, ".cam.txt") is { } cam)
            ready = WithSeq(File.ReadAllLines(cam)[0].Replace(ProtocolLine.Prefix, "", StringComparison.Ordinal), _seq);
        switch (fault)
        {
            case TileFault.Silent:
                return;
            case TileFault.LateReady:
                _ready = true;
                _lateReady = ready;
                return;
            case TileFault.Unsettled:
                Say(TileMs, $"FAIL seq={_seq} reason=timeout scene=400 coll=200 settle=15000");
                return;
            case TileFault.Refused:
                Say(TileMs, $"FAIL seq={_seq} reason=refused why=players players=2");
                return;
        }
        int seq = _seq;
        Say(TileMs + wait, ready, after: () => { if (_seq == seq) _ready = true; });
    }

    void Hmap(double step)
    {
        if (_block is not { } b || !_ready) { Say(1, "ERROR hmap: no block in place (fxmapgen tile first)"); return; }
        var inv = CultureInfo.InvariantCulture;
        bool abort = TileFaults.TryGetValue(b.Name, out var q) && q.Count > 0 && q.Peek() == TileFault.HmapAbort;
        if (abort) q!.Dequeue();
        if (Replayed(b, ".hmap") is { } recorded)
        {
            foreach (var line in File.ReadLines(recorded)) Say(HmapLineMs, WithSeq(line, _seq));
            return;
        }
        int n = (int)Math.Floor(WorldGrid.BlockSize / step + 0.5) + 1;
        var (x0, y0, _, _) = b.Rect;
        Say(HmapLineMs, string.Format(inv, "HMAP BEGIN seq={0} z=8 tx={1} ty={2} x0={3:F4} y0={4:F4} size={5:F4} step={6:F3} n={7} block={8}",
            _seq, b.Tx, b.Ty, x0, y0, WorldGrid.BlockSize, step, n, b.Name));
        for (int j = 0; j < n; j++)
        {
            if (abort && j == 5) { Say(HmapLineMs, "HMAP ABORT"); return; }
            for (int k = 0; k * 100 < n; k++)
                Say(HmapLineMs, $"HMAP j={j} k={k} " + string.Join(' ', Enumerable.Repeat("0.0", Math.Min(100, n - k * 100))));
        }
        Say(HmapLineMs, "HMAP END nohit=0 ms=900");
    }

    /// <summary>The MSCAN lines of a scan of the block (the resource's form, run-length coded rows).</summary>
    void Scan(bool ground, BlockId b, bool canopy)
    {
        var inv = CultureInfo.InvariantCulture;
        _env = true;
        int seq = ++_scanSeq;
        var (x0, y0, _, _) = b.Rect;
        bool abort = ScanAborts.TryGetValue(b.Name, out var left) && left > 0;
        if (abort) ScanAborts[b.Name] = left - 1;
        int cuts = ground ? ScanCuts.GetValueOrDefault(b.Name) : 0;
        bool cut = cuts > 0;
        if (cut) ScanCuts[b.Name] = cuts - 1;
        string kind = ground ? "ground" : "roads";
        string head = string.Format(inv, "block={0} z=8 tx={1} ty={2} x0={3:F4} y0={4:F4} size={5:F4}", b.Name, b.Tx, b.Ty, x0, y0, WorldGrid.BlockSize);
        if (ground)
        {
            Say(2, $"MSCAN BEGIN v=1 kind=mat seq={seq} {head} step=1.000 n=282 flags=1 fol=1 chunks=2 pflags=128{(canopy ? " pflags2=256" : "")} settle=300 zmin=-500");
            Say(0, "MSCAN dict mat 1 282940568");
            for (int c = 0; c < 4; c++) Say(0, $"MSCAN chunk {c % 2},{c / 2} surf=40.0 wait=900");
            for (int j = 0; j < 282; j++)
            {
                if (abort && j == 10) { Say(0, $"MSCAN ABORT seq={seq} kind={kind} block={b.Name}"); return; }
                Say(0, $"MSCAN mat j={j} k=0 1*282");
                if (cut && j == 0) { Say(0, "MSCAN hz j=0 k=0 40.0*100 -"); Say(0, "MSCAN hz j=0 k=1 40.0*140"); }
                else Say(0, $"MSCAN hz j={j} k=0 40.0*282");
                Say(0, $"MSCAN water j={j} k=0 .*282");
                Say(0, $"MSCAN fol j={j} k=0 x*282");
                if (canopy) Say(0, $"MSCAN fol2 j={j} k=0 x*282");
            }
            Say(0, $"MSCAN END kind=mat seq={seq} n=282 cells=79524 nohit=0 water=0 fol=0 fol2=0 mats=1 scan_ms=900 emit_ms=100 ms=4500");
        }
        else
        {
            Say(2, $"MSCAN BEGIN v=1 kind=road seq={seq} {head} step=4.000 n=71 pstep=1.000 pn=282");
            Say(0, "MSCAN dict street 1 -1234567 Fake St");
            Say(0, "MSCAN dict zone 1 LEGSQU Legion Square");
            for (int j = 0; j < 71; j++)
            {
                if (abort && j == 10) { Say(0, $"MSCAN ABORT seq={seq} kind={kind} block={b.Name}"); return; }
                Say(0, $"MSCAN street j={j} k=0 1*71");
                Say(0, $"MSCAN zone j={j} k=0 1*71");
            }
            for (int j = 0; j < 282; j++) Say(0, $"MSCAN onroad j={j} k=0 {(j == 0 ? 1 : 0)}*282");
            Say(0, $"MSCAN END kind=road seq={seq} n=71 streets=1 zones=1 onroad=282 pn=282 wait=900 scan_ms=600 ms=1500");
        }
        Say(0, $"MSCAN DONE seq={seq} kind={kind} block={b.Name} ms=5000");
    }

    static int B(bool v) => v ? 1 : 0;

    /// <summary>Queues a line (with the prefix unless raw) to come out <paramref name="ms"/> from now, after the ones queued before.</summary>
    void Say(int ms, string line, bool raw = false, Action? after = null)
    {
        long due = _clock.ElapsedMilliseconds + ms;
        if (_outbox.Count > 0) due = Math.Max(due, _outbox[^1].Due);
        _outbox.Add((due, (raw ? "" : ProtocolLine.Prefix) + line));
        if (after is not null) _afters[_outbox.Count - 1 + _order] = after;
    }

    readonly Dictionary<long, Action> _afters = new();

    void Emit()
    {
        while (!_stop.IsCancellationRequested)
        {
            string? line = null;
            Action? after = null;
            lock (_sync)
            {
                if (_outbox.Count > 0 && _outbox[0].Due <= _clock.ElapsedMilliseconds)
                {
                    line = _outbox[0].Line;
                    _outbox.RemoveAt(0);
                    if (_afters.Remove(_order, out var a)) after = a;
                    _order++;
                    after?.Invoke();
                }
            }
            if (line is null) { Thread.Sleep(1); continue; }
            _console?.Raise(line);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _emitter.Join();
    }

    sealed class FakeConsole(FakeGame game) : IGameConsole
    {
        bool _started;

        public bool Connected => _started && game.Connectable;

        public event Action<string>? LineReceived;

        public void Start() => _started = true;

        public Task<bool> SendAsync(string command, CancellationToken token = default)
        {
            if (!Connected) return Task.FromResult(false);
            game.Handle(command);
            return Task.FromResult(true);
        }

        public void Raise(string line) => LineReceived?.Invoke(line);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
