using System.Globalization;
using System.Text;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Capture;

/// <summary>
/// The visit, in the game and one block at a time: for each block of the range that misses a data item its maps need,
/// the items it misses in turn. For the shot or the height grid the camera goes over it (<c>fxmapgen tile</c>), the
/// frame is taken from the game window once the resource says READY and the beacon on the frame agrees, the height grid
/// comes from the resource (<c>fxmapgen hmap</c>); then the ground scan (<c>fxmapgen scan ground</c>, with the canopy
/// probe when a map needs it) and the road scan (<c>fxmapgen scan roads</c>), for which the resource moves the character
/// itself (a block that only misses scans needs no camera). <c>capture/&lt;block&gt;.png</c>, <c>.cam.txt</c>,
/// <c>.hmap</c> and <c>scan/&lt;block&gt;.txt</c> are written (a scan taken again replaces its part of the scan file). A
/// failed block is tried again with the capture environment put on again. Blocks go row by row from the north, each row
/// the other way round (neighbouring blocks keep the streaming warm).
/// <para>
/// Before the blocks: the console connection, the resource (protocol, version, its server side, the permission, nobody
/// else on), the window size, the resources to stop (the project's list, else its server preset's; only those running
/// are stopped), the performance overlay off and the capture environment on. After them, also when stopped at once:
/// env off (the resource sets the character down) and the stopped resources started again. The conversation with the
/// game goes into <c>console.log</c> in the run's folder.
/// </para>
/// <para>
/// Each block waits until the game's streaming requests have stayed at 0 for the settle wait the pre-check measured on
/// this PC (<see cref="SettleStore"/>), or <see cref="SettleProbe.DefaultWaitMs"/> without a measurement.
/// </para>
/// <para>
/// A <see cref="Follower"/> gets every block whose shot and the heights its photo is placed with
/// (<see cref="Satellite.SurfaceHeights.PhotoItems"/>) are in, and works on it beside the visit (the
/// satellite map's provisional orthorectification, one helper worker while the level allows two or more).
/// </para>
/// </summary>
public sealed class VisitStage(IGameAccess game, VisitStage.Options? options = null) : Stage
{
    public sealed record Options
    {
        /// <summary>Vertical field of view of the shots, degrees.</summary>
        public double Fov { get; init; } = 2.0;
        /// <summary>The frame covers the block and this much around it (the orthorectification needs the edge).</summary>
        public double Margin { get; init; } = 1.06;
        public double HmapStep { get; init; } = 1.0;
        /// <summary>Tries of a block after the first.</summary>
        public int Retries { get; init; } = 2;
        /// <summary>The resource version that goes with this program (hello must say it); null: any.</summary>
        public string? ResourceVersion { get; init; }
        public int FrameWidth { get; init; } = 1920;
        public int FrameHeight { get; init; } = 1080;
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan TileTimeout { get; init; } = TimeSpan.FromSeconds(75);
        public TimeSpan HmapTimeout { get; init; } = TimeSpan.FromSeconds(120);
        public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(180);
        /// <summary>After READY, before the first frame.</summary>
        public TimeSpan SettleBeforeShot { get; init; } = TimeSpan.FromMilliseconds(300);
        /// <summary>Between frames when a frame is black or late.</summary>
        public TimeSpan ShotRetryWait { get; init; } = TimeSpan.FromMilliseconds(300);
        /// <summary>When a notification is on the screen (up to 3 times).</summary>
        public TimeSpan NotificationWait { get; init; } = TimeSpan.FromSeconds(5);
        /// <summary>After the capture environment is put on again for another try.</summary>
        public TimeSpan RetryWait { get; init; } = TimeSpan.FromSeconds(1);
        /// <summary>
        /// When every block of the run is taken (none failed, not stopped), stop the capture resource at the end: the work in
        /// the game is over, FiveM and the server can be closed. A run stopped half-way leaves it running for the rest.
        /// </summary>
        public bool StopAtEnd { get; init; } = true;
        /// <summary>How long a stopped resource is given to go quiet (it answers HELLO no more).</summary>
        public TimeSpan StopCheck { get; init; } = TimeSpan.FromSeconds(4);
    }

    readonly Options _o = options ?? new Options();
    IReadOnlyList<BlockItem> _items = Array.Empty<BlockItem>();
    IReadOnlyList<BlockItem> _photo = [BlockItem.Height];
    GameLink? _link;
    StreamWriter? _transcript;
    readonly List<string> _stopped = new();
    bool _envOn;
    bool _complete;         // every block taken: the resource is stopped at the end
    string? _resource;      // the name the resource answered under (HELLO res=)
    int _quietMs = SettleProbe.DefaultWaitMs;
    int? _capture;          // what the resource's capture takes (HELLO capture=), recorded with every item taken

    /// <summary>Work that follows the visit block by block; null: none.</summary>
    public IVisitFollower? Follower { get; init; }

    public override string Id => "visit";
    public override string Row => "visit";
    public override bool UsesGame => true;
    public override int Helpers => Follower is null ? 0 : 1;
    public override string UnitName => "block";
    public override IReadOnlyList<string> Rows => ["visit", "visit.prepare", "visit.shot", "visit.height", "visit.scanGround", "visit.scanRoads",
        "visit.scanCanopy", "visit.cleanup"];
    public override long MemoryPerUnit => 64L << 20;

    /// <summary>
    /// The data items a visit takes for the project's maps: the shot for the satellite map, the heights (the height grid,
    /// unless the ground scan stands in), the ground and road scans for the atlas and the road map, the canopy for an atlas
    /// style that draws it.
    /// </summary>
    public static IReadOnlyList<BlockItem> ItemsFor(Project project)
    {
        var maps = project.Maps;
        return BlockItems.All.Where(i => maps.Any(m => Planner.NeedsItem(project, m, i))).ToList();
    }

    /// <summary>Row by row from the north; even rows west to east, odd rows east to west.</summary>
    public static IReadOnlyList<BlockId> Order(IEnumerable<BlockId> blocks) =>
        blocks.GroupBy(b => b.By).OrderBy(g => g.Key)
            .SelectMany(g => g.Key % 2 == 0 ? g.OrderBy(b => b.Bx) : g.OrderByDescending(b => b.Bx)).ToList();

    static IReadOnlyList<BlockId> Left(Project project, StateStore state, IReadOnlyList<BlockItem> items) =>
        items.Count == 0 ? Array.Empty<BlockId>() : project.Range.Keys.Where(b => items.Any(i => !state.Has(b, i))).ToList();

    public override int CountReady(Project project, StateStore state) => Left(project, state, ItemsFor(project)).Count;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        _items = ItemsFor(ctx.Project);
        _photo = Satellite.SurfaceHeights.PhotoItems(ctx.Project);
        var left = Order(Left(ctx.Project, ctx.State, _items));
        if (left.Count == 0) return Array.Empty<string>();
        _transcript = new StreamWriter(new FileStream(Path.Combine(ctx.RunPath, "console.log"), FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
        var console = ctx.Project.File.Console;
        _link = new GameLink(game.OpenConsole(console.Host, console.Port), _transcript) { TileTimeout = _o.TileTimeout, HmapTimeout = _o.HmapTimeout, ScanTimeout = _o.ScanTimeout };
        Sync(() => BeginAsync(ctx, _items.Contains(BlockItem.Shot)));
        if (_items.Contains(BlockItem.Shot)) Follower?.Start(ctx, _o.Fov);
        return left.Select(b => b.Name).ToList();
    }

    async Task BeginAsync(StageContext ctx, bool shots)
    {
        var link = _link!;
        var token = ctx.Token;
        var console = ctx.Project.File.Console;
        if (!await link.ConnectAsync(_o.ConnectTimeout, token).ConfigureAwait(false))
            throw new InvalidOperationException($"no connection to the game console at {console.Host}:{console.Port}: start FiveM and join the server " +
                "(only one program can use the console at a time: close FxDeck and the like)");
        var hello = await link.HelloAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the fxmapgen-capture resource did not answer: start it on the server (ensure fxmapgen-capture)");
        ctx.Log($"visit: resource {hello.Resource} {hello.Version} (protocol {hello.Proto}, server side {hello.ServerVersion}), game build {hello.Build}, " +
            $"{hello.Players} player(s) on");
        if (Problem(hello, _o.ResourceVersion) is { } problem) throw new InvalidOperationException(problem);
        // it answered with this program's version: it is on the server (the mark the pre-check and the resource start leave)
        if (_o.ResourceVersion is { } version && hello.Version == version)
            new ResourcePlacement(version, ResourcePlacement.Answered, DateTime.UtcNow).Write(ctx.Folder);
        _capture = hello.Capture;
        _resource = hello.Resource;
        if (shots)
        {
            var size = game.Window.ClientSize() ?? throw new InvalidOperationException("the game window was not found");
            if (size != (_o.FrameWidth, _o.FrameHeight))
                throw new InvalidOperationException($"the game window's drawing area is {size.Width} x {size.Height}; the shots need {_o.FrameWidth} x {_o.FrameHeight} in a window");
        }
        var (wait, from) = new SettleStore(ctx.Folder).Wait();
        _quietMs = wait;
        ctx.Log(string.Format(CultureInfo.InvariantCulture, "visit: settle wait {0:0.00} s ", wait / 1000.0) + (from is null
            ? "(the default: no measurement; the pre-check measures it)"
            : string.Format(CultureInfo.InvariantCulture, "(measured {0:yyyy-MM-dd HH:mm} over the {1} district: frames unchanged after {2:0.00} s, {3:0.#} fps)",
                from.AtUtc.ToLocalTime(), from.District, from.StableMs / 1000.0, from.Fps)));
        var names = ctx.Project.StopResources;
        var states = await link.ResourcesAsync(names, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the resource did not report the state of the resources to stop");
        foreach (var name in names.Where(n => states.GetValueOrDefault(n) == "started"))
        {
            await link.SendAsync("stop " + name, token).ConfigureAwait(false);
            _stopped.Add(name);
        }
        File.WriteAllLines(Path.Combine(ctx.RunPath, "stopped-resources.txt"), _stopped);
        ctx.Log(_stopped.Count == 0 ? "visit: no resource to stop" : "visit: stopped " + string.Join(", ", _stopped));
        if (shots) await link.SendAsync("cl_drawPerf 0", token).ConfigureAwait(false);
        _envOn = true;
        if (!await link.EnvOnAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("the capture environment did not go on (no ENV on)");
    }

    /// <summary>Why the resource cannot capture now, in words; null when it can.</summary>
    /// <summary>The same checks as <see cref="Problem"/> as a code: protocol, version, noServer, noAce, players; null when fine.</summary>
    public static string? ProblemCode(HelloReply hello, string? version) =>
        hello.Proto != GameLink.Protocol ? "protocol"
        : version is not null && hello.Version != version ? "version"
        : !hello.ServerAnswered ? "noServer"
        : !hello.Ace ? "noAce"
        : hello.Players > 1 ? "players"
        : null;

    public static string? Problem(HelloReply hello, string? version)
    {
        if (hello.Proto != GameLink.Protocol)
            return $"the resource speaks protocol {hello.Proto}, this program {GameLink.Protocol}: install the fxmapgen-capture that came with this program";
        if (version is not null && hello.Version != version)
            return $"the resource on the server is version {hello.Version}, this program {version}: install the fxmapgen-capture that came with this program";
        if (!hello.ServerAnswered)
            return "the resource's server side did not answer: restart fxmapgen-capture on the server";
        if (!hello.Ace)
            return "this player may not clear the world: allow command.fxmapgen (admins allowed \"command\" have it)";
        if (hello.Players > 1)
            return $"{hello.Players} players are on the server; the capture deletes every vehicle and NPC, so it only runs with nobody else on";
        return null;
    }

    public override void Run(UnitContext ctx) => Sync(() => VisitAsync(ctx));

    async Task VisitAsync(UnitContext ctx)
    {
        var link = _link!;
        var token = ctx.Token;
        var block = BlockId.Parse(ctx.Unit);
        var need = _items.Where(i => !ctx.State.Has(block, i)).ToList();
        if (need.Count == 0) return;
        bool tileNeeded = need.Contains(BlockItem.Shot) || need.Contains(BlockItem.Height);
        // the canopy rides along with the ground scan whenever a map needs it; taken again for the canopy alone, the
        // ground scan is taken whole
        bool canopy = _items.Contains(BlockItem.ScanCanopy);
        bool groundNeeded = need.Contains(BlockItem.ScanGround) || need.Contains(BlockItem.ScanCanopy);
        bool roadsNeeded = need.Contains(BlockItem.ScanRoads);
        string why = "";
        for (int attempt = 0; attempt <= _o.Retries; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (!link.Connected && !await link.ConnectAsync(_o.ConnectTimeout, token).ConfigureAwait(false))
            {
                ctx.StopRun("the connection to the game console is gone");
                throw new InvalidOperationException("no connection to the game console");
            }
            int afterSeq = link.LastSeq;
            if (attempt > 0)
            {
                ctx.Log($"visit {block}: try {attempt + 1} ({why})");
                await link.EnvOnAsync(token).ConfigureAwait(false);
                await link.PauseAsync(_o.RetryWait, token).ConfigureAwait(false);
                // a request of the try before may still be on its way: take nothing up to the resource's number now
                if (tileNeeded && await link.StatusAsync(token).ConfigureAwait(false) is { } status) afterSeq = Math.Max(afterSeq, status.Seq);
            }
            TileReply? tile = null;
            if (tileNeeded)
            {
                ctx.Report("tile", 0.05);
                tile = await link.TileAsync(block, _o.Fov, _o.Margin, afterSeq, _quietMs, token).ConfigureAwait(false);
                if (tile.Outcome != TileOutcome.Ready)
                {
                    why = tile.Reason;
                    if (tile.Outcome == TileOutcome.Fail && tile.Line!.Get("reason") == "refused")
                    {
                        ctx.StopRun(why);
                        throw new InvalidOperationException(why);
                    }
                    continue;
                }
            }
            Frame? frame = null;
            if (need.Contains(BlockItem.Shot))
            {
                ctx.Report("shot", 0.25);
                (frame, var shotProblem) = await ShotAsync(ctx, block, tile!.Seq).ConfigureAwait(false);
                if (frame is null)
                {
                    why = shotProblem!;
                    var size = game.Window.ClientSize();
                    if (size != (_o.FrameWidth, _o.FrameHeight))
                    {
                        // every block after this one would fail the same way
                        ctx.StopRun(size is null ? "the game window is gone" : $"the game window changed to {size.Value.Width} x {size.Value.Height}");
                        throw new InvalidOperationException(why);
                    }
                    continue;
                }
            }
            IReadOnlyList<string>? heights = null;
            if (need.Contains(BlockItem.Height))
            {
                ctx.Report("height", 0.35);
                var hmap = await link.HmapAsync(_o.HmapStep, token).ConfigureAwait(false);
                if (hmap.Lines is null)
                {
                    why = hmap.Failure!;
                    continue;
                }
                heights = hmap.Lines;
            }
            IReadOnlyList<string>? ground = null, roads = null;
            if (groundNeeded)
            {
                ctx.Report("ground", 0.5);
                var scan = await link.ScanAsync(Scan.ScanKind.Ground, block, canopy, token).ConfigureAwait(false);
                if (scan.Lines is null)
                {
                    why = scan.Failure!;
                    continue;
                }
                ground = scan.Lines;
            }
            if (roadsNeeded)
            {
                ctx.Report("roads", 0.8);
                var scan = await link.ScanAsync(Scan.ScanKind.Roads, block, false, token).ConfigureAwait(false);
                if (scan.Lines is null)
                {
                    why = scan.Failure!;
                    continue;
                }
                roads = scan.Lines;
            }
            ctx.Report("save", 0.95);
            token.ThrowIfCancellationRequested();
            if (tile is not null) Save(ctx.Folder, block, tile.Line!, frame, heights);
            if (ground is not null || roads is not null) SaveScan(ctx.Folder, block, ground, roads);
            var taken = need.ToList();
            var dropped = new List<BlockItem>();
            if (ground is not null)
            {
                // a new ground scan brings the canopy with it, or leaves the canopy of the one before behind
                if (!taken.Contains(BlockItem.ScanGround)) taken.Add(BlockItem.ScanGround);
                if (canopy) { if (!taken.Contains(BlockItem.ScanCanopy)) taken.Add(BlockItem.ScanCanopy); }
                else dropped.Add(BlockItem.ScanCanopy);
            }
            ctx.State.TakeItems(block, taken, DateTime.UtcNow, dropped, _capture);
            if (attempt > 0) ctx.Log($"visit {block}: done on try {attempt + 1}");
            if (Satellite.OrthoStage.Captured(ctx.State, block, _photo)) Follower?.Taken(block);
            return;
        }
        throw new InvalidOperationException(why);
    }

    /// <summary>
    /// The frame of request <paramref name="seq"/>: after a short wait, up to 4 frames until one shows the ready beacon with
    /// the request's number (the window can hand out a black or an earlier frame); a notification band is waited out up to
    /// 3 times. Null and the reason when none would do.
    /// </summary>
    async Task<(Frame?, string?)> ShotAsync(UnitContext ctx, BlockId block, int seq)
    {
        var token = ctx.Token;
        await Task.Delay(_o.SettleBeforeShot, token).ConfigureAwait(false);
        string why = "";
        int notifications = 0, again = 0;
        for (int tries = 0; tries < 4; tries++)
        {
            var frame = game.Window.Capture();
            if (frame is null) why = "the game window could not be taken";
            else if (frame.Width != _o.FrameWidth || frame.Height != _o.FrameHeight)
                why = $"the frame is {frame.Width} x {frame.Height}, not {_o.FrameWidth} x {_o.FrameHeight}";
            else if (!FrameChecks.ReadyFor(frame, seq)) why = "the frame does not show the ready beacon of this block (a black or late frame)";
            else if (FrameChecks.NotificationBand(frame))
            {
                why = "a notification stayed on the screen";
                if (notifications++ < 3)
                {
                    ctx.Log($"visit {block}: a notification is on the screen, waiting {_o.NotificationWait.TotalSeconds:0.#} s");
                    _link!.Note($"(a notification is on the screen: waiting {_o.NotificationWait.TotalSeconds:0.#} s)");
                    await _link.PauseAsync(_o.NotificationWait, token).ConfigureAwait(false);
                    tries--;
                    continue;
                }
                break;
            }
            else
            {
                if (again > 0) ctx.Log($"visit {block}: {again} frame(s) taken again");
                return (frame, null);
            }
            again++;
            _link!.Note($"(frame {again}: {why}; taking it again)");
            await Task.Delay(_o.ShotRetryWait, token).ConfigureAwait(false);
        }
        return (null, why);
    }

    /// <summary>The shot (with its READY line) and the height grid, each written whole or not at all.</summary>
    static void Save(WorkFolder folder, BlockId block, ProtocolLine ready, Frame? frame, IReadOnlyList<string>? heights)
    {
        Directory.CreateDirectory(folder.Capture);
        if (frame is not null)
        {
            WriteWhole(folder.CapturePng(block), tmp => Images.SavePng(tmp, frame.Rgba, frame.Width, frame.Height));
            WriteWhole(folder.CaptureCamera(block), tmp => File.WriteAllText(tmp, ready.Text + "\n"));
        }
        if (heights is not null)
            WriteWhole(folder.CaptureHeights(block), tmp => File.WriteAllText(tmp, string.Join('\n', heights) + "\n"));
    }

    /// <summary>The block's scan file with the new ground and / or road lines; the other scan's lines kept from the file there.</summary>
    static void SaveScan(WorkFolder folder, BlockId block, IReadOnlyList<string>? ground, IReadOnlyList<string>? roads)
    {
        Directory.CreateDirectory(folder.Scan);
        var path = folder.ScanFile(block);
        var existing = (ground is null || roads is null) && File.Exists(path) ? File.ReadAllLines(path) : null;
        var lines = Scan.ScanLines.Merge(existing, ground, roads);
        WriteWhole(path, tmp => File.WriteAllText(tmp, string.Join('\n', lines) + "\n"));
    }

    static void WriteWhole(string path, Action<string> write)
    {
        var tmp = path + ".tmp";
        write(tmp);
        File.Move(tmp, path, overwrite: true);
    }

    public override void Cleanup(StageContext ctx)
    {
        try { CleanUpGame(ctx); }
        // the block in hand beside the visit is finished; blocks it did not get to are left to the stages after the visit
        finally { Follower?.Finish(); }
    }

    void CleanUpGame(StageContext ctx)
    {
        var link = _link;
        if (link is null) return;
        try
        {
            Sync(async () =>
            {
                if (_envOn)
                {
                    var off = await link.EnvOffAsync(CancellationToken.None).ConfigureAwait(false);
                    ctx.Log(off is null
                        ? "visit: env off got no answer: type \"fxmapgen env off\" in the game console (F8)"
                        : off.Safe || off.Already
                            ? $"visit: capture environment off, character set down at {off.X.ToString("0.0", CultureInfo.InvariantCulture)}, {off.Y.ToString("0.0", CultureInfo.InvariantCulture)}" +
                              (off.Refilled == true ? $", hunger and thirst filled ({off.RefillVia})" : "")
                            : "visit: capture environment off, but the character could not be set down: type \"fxmapgen safe\" in the game console (F8)");
                }
                foreach (var name in Enumerable.Reverse(_stopped))
                    await link.SendAsync("ensure " + name, CancellationToken.None).ConfigureAwait(false);
                if (_stopped.Count > 0) ctx.Log("visit: started again " + string.Join(", ", _stopped));
                if (_complete) await EndAsync(ctx, link).ConfigureAwait(false);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ctx.Log($"visit: clean-up in the game failed ({ex.Message}); in the game console: fxmapgen env off, then ensure {string.Join(", ", _stopped)}");
        }
        finally
        {
            Sync(async () => await link.DisposeAsync().ConfigureAwait(false));
            _transcript?.Dispose();
            _link = null;
        }
    }

    public override void Finish(StageContext ctx, StageEnd end) => _complete = end.Complete;

    /// <summary>
    /// Every block is taken: the work in the game is over. Stops the capture resource (under the name it answered with)
    /// and makes sure it answers no more; without the permission the server keeps it running, which does no harm (it only
    /// acts on commands). Either way the person watching is told that FiveM and the server can be closed.
    /// </summary>
    async Task EndAsync(StageContext ctx, GameLink link)
    {
        var name = string.IsNullOrEmpty(_resource) ? ResourceStart.ResourceName : _resource;
        string stopped;
        if (!_o.StopAtEnd) stopped = "kept";
        else if (!link.Connected) stopped = "unknown";
        else
        {
            await link.SendAsync("stop " + name, CancellationToken.None).ConfigureAwait(false);
            bool answers = await link.AskAsync("fxmapgen hello", l => l.Word == "HELLO", _o.StopCheck, CancellationToken.None).ConfigureAwait(false) is not null;
            stopped = answers ? "0" : "1";
            // the connection check made before holds no longer: the next visit starts the resource and checks again
            if (!answers) new ResourceStop(DateTime.UtcNow, name).Write(ctx.Folder);
        }
        ctx.Log(stopped switch
        {
            "1" => $"visit: every block taken; the capture resource {name} stopped (FiveM and the server can be closed)",
            "0" => $"visit: every block taken, but the capture resource {name} still answers after stop (no permission?): stop it on the server " +
                   $"(stop {name}) or leave it, it does nothing without commands",
            _ => $"visit: every block taken; the capture resource {name} left as it is",
        });
        ctx.Announce("gameDone", new Dictionary<string, string>
        {
            ["resource"] = name,
            ["stopped"] = stopped,
            ["restarted"] = string.Join(",", _stopped),
        });
    }

    static void Sync(Func<Task> work) => work().GetAwaiter().GetResult();
}

/// <summary>Work that follows the visit block by block, beside it (the satellite map's provisional orthorectification).</summary>
public interface IVisitFollower
{
    /// <summary>Before the first block, once the game is ready; the context stays in use until <see cref="Finish"/>.</summary>
    void Start(StageContext ctx, double fov);

    /// <summary>A block's shot and the heights its photo is placed with were saved.</summary>
    void Taken(BlockId block);

    /// <summary>After the last block, also when the run stops: finishes the block in hand and leaves the rest.</summary>
    void Finish();
}
