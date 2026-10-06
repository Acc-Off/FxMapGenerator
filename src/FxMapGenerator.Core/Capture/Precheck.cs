using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Capture;

/// <param name="Id">console, resource, window, resources, testShot, environment, settle.</param>
/// <param name="Ok">True / false; null when it was not checked (an earlier check failed).</param>
/// <param name="Message">What is wrong and what to do, in words; empty when fine.</param>
/// <param name="Codes">
/// What is wrong, for the screen to put in its own words (with <see cref="Values"/>): console <c>noConsole</c>; resource
/// <c>noAnswer</c>, <c>protocol</c>, <c>version</c>, <c>noServer</c>, <c>noAce</c>, <c>players</c>; window <c>noWindow</c>,
/// <c>windowSize</c>; resources <c>noStates</c>; testShot <c>envFailed</c>, <c>tileFailed</c> (value <c>reason</c>),
/// <c>noFrame</c>, <c>notification</c>, <c>boxes</c>; environment <c>noStatus</c>, <c>weather</c>, <c>time</c>, <c>npcs</c>;
/// settle <c>unsettled</c>, <c>notMeasured</c>. Empty when fine.
/// </param>
public sealed record PrecheckItem(string Id, bool? Ok, IReadOnlyDictionary<string, string> Values, string Message, IReadOnlyList<string>? Codes = null);

/// <param name="Folder">
/// logs/precheck-&lt;time&gt;/ with report.json, shot.png, shot-marked.png, console.log and the first and last frame of
/// each block of the settle measurement (settle-&lt;block&gt;-first.png / -last.png).
/// </param>
/// <param name="Boxes">
/// What stood out on the test shot (pixels of the 1920 x 1080 frame); only boxes in the part the map uses
/// (<see cref="Box.InMap"/>) fail the check.
/// </param>
/// <param name="Settle">The settle measurement; null when the check did not get that far.</param>
public sealed record PrecheckReport(DateTime AtUtc, string Folder, IReadOnlyList<PrecheckItem> Items, IReadOnlyList<Box> Boxes, bool Notification,
    SettleMeasurement? Settle = null)
{
    /// <summary>Every item passed (the ones a visit without shots does not need left aside).</summary>
    public bool Ok => Items.All(i => i.Ok == true || i.Codes?.Contains("notNeeded") == true);

    /// <summary>
    /// When the program stopped the capture resource after this check (<see cref="LatestFor"/> sets it; not in
    /// report.json): the check holds no longer, the screens and the prerequisites take it as not made.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ResourceStoppedUtc { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The newest report in the work folder's logs, or null.</summary>
    public static PrecheckReport? Latest(string logs)
    {
        if (!Directory.Exists(logs)) return null;
        foreach (var dir in Directory.GetDirectories(logs, "precheck-*").OrderDescending(StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, "report.json");
            if (!File.Exists(file)) continue;
            try { return JsonSerializer.Deserialize<PrecheckReport>(File.ReadAllText(file), Json); }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary>
    /// The newest report of a work folder as the screens and the prerequisites take it: with
    /// <see cref="ResourceStoppedUtc"/> set when the program stopped the capture resource after the check
    /// (<see cref="ResourceStop"/>).
    /// </summary>
    public static PrecheckReport? LatestFor(WorkFolder folder)
    {
        var report = Latest(folder.Logs);
        if (report is null) return null;
        return ResourceStop.Read(folder) is { } stop && stop.AtUtc > report.AtUtc ? report with { ResourceStoppedUtc = stop.AtUtc } : report;
    }
}

/// <summary>
/// The pre-check before a visit, with the game running: the console connection and the resource (protocol, version,
/// server side, permission, nobody else on), the window size, the state of the resources to stop, and a test shot over
/// open sea with everything set as for the visit (resources stopped, performance overlay off, capture environment on).
/// On the test shot, anything that stands out from the sea (HUD, text, a notification) is boxed; weather, time and NPCs
/// come from the resource. Then the settle wait of this PC is measured (<see cref="SettleProbe"/>) and kept in
/// <c>state/settle.json</c> for the visit. A visit without shots (scans and height grids only) needs no window, no test
/// shot, no environment and no settle measurement: those items say <c>notNeeded</c> (weather, time and NPCs only show in
/// the photos; the scans' rays meet the map's own collision), and the check ends after the resources' states without
/// touching the game. Afterwards the game is put back as it was found (env off, the resources started again).
/// Writes <c>logs/precheck-&lt;time&gt;/</c>.
/// </summary>
public sealed class Precheck(IGameAccess game, Precheck.Options? options = null)
{
    public sealed record Options
    {
        public string? ResourceVersion { get; init; }
        /// <summary>A block of open sea for the test shot (the north-west corner of the map).</summary>
        public BlockId TestBlock { get; init; } = new(0, 0);
        public int FrameWidth { get; init; } = 1920;
        public int FrameHeight { get; init; } = 1080;
        public double Fov { get; init; } = 2.0;
        public double Margin { get; init; } = 1.06;
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan TileTimeout { get; init; } = TimeSpan.FromSeconds(75);
        public TimeSpan SettleBeforeShot { get; init; } = TimeSpan.FromMilliseconds(300);
        public TimeSpan ShotRetryWait { get; init; } = TimeSpan.FromMilliseconds(300);
        public SettleProbe.Options Settle { get; init; } = new();
    }

    readonly Options _o = options ?? new Options();

    public async Task<PrecheckReport> RunAsync(Project project, CancellationToken token = default)
    {
        var at = DateTime.UtcNow;
        var baseName = Path.Combine(project.WorkFolderPath, "logs", "precheck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var folder = baseName;
        for (int n = 2; Directory.Exists(folder); n++) folder = $"{baseName}-{n}";
        Directory.CreateDirectory(folder);
        var items = new List<PrecheckItem>();
        var boxes = new List<Box>();
        bool notification = false;
        SettleMeasurement? settle = null;
        using var transcript = new StreamWriter(Path.Combine(folder, "console.log"), append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var console = project.File.Console;
        await using var link = new GameLink(game.OpenConsole(console.Host, console.Port), transcript) { TileTimeout = _o.TileTimeout };
        var stopped = new List<string>();
        bool envOn = false;
        var state = StateStore.Open(new WorkFolder(project.WorkFolderPath));
        bool shots = VisitStage.ItemsFor(project).Contains(BlockItem.Shot) && project.Range.Keys.Any(b => !state.Has(b, BlockItem.Shot));
        try
        {
            // the console and the resource
            if (!await link.ConnectAsync(_o.ConnectTimeout, token).ConfigureAwait(false))
            {
                items.Add(Fail("console", "noConsole", $"no connection to the game console at {console.Host}:{console.Port}: start FiveM and join the server " +
                    "(only one program can use the console at a time: close FxDeck and the like)", ("console", $"{console.Host}:{console.Port}")));
                return Finish();
            }
            items.Add(Pass("console", ("console", $"{console.Host}:{console.Port}")));
            var hello = await link.HelloAsync(token).ConfigureAwait(false);
            if (hello is null)
            {
                items.Add(Fail("resource", "noAnswer", "the fxmapgen-capture resource did not answer: start it on the server (ensure fxmapgen-capture)"));
                return Finish();
            }
            var problem = VisitStage.Problem(hello, _o.ResourceVersion);
            items.Add(new PrecheckItem("resource", problem is null, Values(("version", hello.Version), ("protocol", hello.Proto.ToString(CultureInfo.InvariantCulture)),
                ("server", hello.ServerVersion), ("ace", hello.Ace ? "1" : "0"), ("players", hello.Players.ToString(CultureInfo.InvariantCulture)),
                ("build", hello.Build.ToString(CultureInfo.InvariantCulture)), ("expected", _o.ResourceVersion ?? "")), problem ?? "",
                Codes(VisitStage.ProblemCode(hello, _o.ResourceVersion))));

            // the window (only for shots)
            bool windowOk = true;
            if (shots)
            {
                var size = game.Window.ClientSize();
                windowOk = size == (_o.FrameWidth, _o.FrameHeight);
                items.Add(new PrecheckItem("window", windowOk, size is { } s ? Values(("width", s.Width.ToString(CultureInfo.InvariantCulture)), ("height", s.Height.ToString(CultureInfo.InvariantCulture))) : Values(),
                    size is null ? "the game window was not found" : windowOk ? "" : $"the game window's drawing area is {size.Value.Width} x {size.Value.Height}; the shots need {_o.FrameWidth} x {_o.FrameHeight} in a window",
                    Codes(size is null ? "noWindow" : windowOk ? null : "windowSize")));
            }
            else items.Add(NotNeeded("window"));

            // the resources to stop
            var names = project.StopResources;
            var states = await link.ResourcesAsync(names, token).ConfigureAwait(false);
            items.Add(states is null
                ? Fail("resources", "noStates", "the resource did not report the state of the resources to stop")
                : new PrecheckItem("resources", true, names.ToDictionary(n => n, n => states.GetValueOrDefault(n, "missing")), "", []));
            if (problem is not null || !windowOk || states is null) return Finish();
            if (!shots)
            {
                items.Add(NotNeeded("testShot"));
                items.Add(NotNeeded("environment"));
                items.Add(NotNeeded("settle"));
                return Finish();
            }
            // where the player stands: the settle wait is measured in the district farther away
            var before = await link.StatusAsync(token).ConfigureAwait(false);

            // the test shot, set as for the visit
            foreach (var name in names.Where(n => states.GetValueOrDefault(n) == "started"))
            {
                await link.SendAsync("stop " + name, token).ConfigureAwait(false);
                stopped.Add(name);
            }
            await link.SendAsync("cl_drawPerf 0", token).ConfigureAwait(false);
            envOn = true;
            if (!await link.EnvOnAsync(token).ConfigureAwait(false))
            {
                items.Add(Fail("testShot", "envFailed", "the capture environment did not go on (no ENV on)"));
                return Finish();
            }
            var tile = await link.TileAsync(_o.TestBlock, _o.Fov, _o.Margin, link.LastSeq, SettleProbe.DefaultWaitMs, token).ConfigureAwait(false);
            if (tile.Outcome != TileOutcome.Ready)
            {
                items.Add(Fail("testShot", "tileFailed", tile.Reason, ("reason", tile.Reason)));
                return Finish();
            }
            await Task.Delay(_o.SettleBeforeShot, token).ConfigureAwait(false);
            Frame? frame = null;
            for (int tries = 0; tries < 4 && frame is null; tries++)
            {
                var f = game.Window.Capture();
                if (f is not null && FrameChecks.ReadyFor(f, tile.Seq)) frame = f;
                else await Task.Delay(_o.ShotRetryWait, token).ConfigureAwait(false);
            }
            if (frame is null)
            {
                items.Add(Fail("testShot", "noFrame", "no frame with the ready beacon could be taken from the game window"));
                return Finish();
            }
            // only the middle of a frame reaches the map: what stands out at its sides (FiveM's own watermark, a voice
            // indicator) is shown but does not fail the check
            var area = FrameChecks.MapArea(frame.Width, frame.Height, _o.Margin);
            boxes.AddRange(FrameChecks.OverSea(frame, area));
            notification = FrameChecks.NotificationBand(frame);
            Images.SavePng(Path.Combine(folder, "shot.png"), frame.Rgba, frame.Width, frame.Height);
            var marked = FrameChecks.Marked(frame, boxes, area);
            Images.SavePng(Path.Combine(folder, "shot-marked.png"), marked.Rgba, marked.Width, marked.Height);
            int inMap = boxes.Count(b => b.InMap), outside = boxes.Count - inMap;
            bool shotOk = inMap == 0 && !notification;
            items.Add(new PrecheckItem("testShot", shotOk, Values(("boxes", inMap.ToString(CultureInfo.InvariantCulture)),
                ("outside", outside.ToString(CultureInfo.InvariantCulture)), ("block", _o.TestBlock.Name)), shotOk ? ""
                : notification ? "a notification is on the screen; the visit waits for notifications, but wait for this one to go and check again"
                : $"{inMap} area(s) stand out in the part of the test shot the map uses (red boxes): something still draws on the screen; add the resource that draws it to the resources to stop",
                Codes(notification ? "notification" : inMap > 0 ? "boxes" : null)));

            // weather, time and NPCs as the resource sees them
            items.Add(Environment(await link.StatusAsync(token).ConfigureAwait(false)));

            // the settle wait of this PC, for the visit
            settle = await new SettleProbe(link, game.Window, _o.Settle with { Fov = _o.Fov, Margin = _o.Margin })
                .RunAsync(before?.X, before?.Y, folder, token).ConfigureAwait(false);
            var store = new SettleStore(new WorkFolder(project.WorkFolderPath));
            if (settle.Ok) store.Save(settle);
            else store.Clear();
            items.Add(SettleItem(settle, _o.Settle));
            return Finish();
        }
        finally
        {
            // put the game back as it was found
            if (envOn)
            {
                var off = await link.EnvOffAsync(CancellationToken.None).ConfigureAwait(false);
                link.Note(off is null ? "(env off got no answer)" : $"(env off: character set down {(off.Safe || off.Already ? "yes" : "NO")})");
            }
            foreach (var name in Enumerable.Reverse(stopped)) await link.SendAsync("ensure " + name, CancellationToken.None).ConfigureAwait(false);
        }

        PrecheckReport Finish()
        {
            var report = new PrecheckReport(at, folder, items, boxes, notification, settle);
            File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(report, PrecheckReport.Json), new UTF8Encoding(false));
            return report;
        }
    }

    /// <summary>
    /// The settle item: values district, blocks (the measured ones), stableMs, waitMs, fps, forMs (how long frames were
    /// taken), tried (every block tried).
    /// </summary>
    static PrecheckItem SettleItem(SettleMeasurement m, SettleProbe.Options o)
    {
        var inv = CultureInfo.InvariantCulture;
        var measured = m.Blocks.Where(b => b.StableMs is not null).ToList();
        var values = Values(("district", m.District), ("blocks", string.Join(' ', measured.Select(b => b.Block))),
            ("stableMs", m.StableMs?.ToString(inv) ?? ""), ("waitMs", m.WaitMs.ToString(inv)), ("fps", m.Fps?.ToString("0.#", inv) ?? ""),
            ("forMs", ((int)o.For.TotalMilliseconds).ToString(inv)), ("tried", string.Join(' ', m.Blocks.Select(b => b.Block))));
        if (m.Ok) return new PrecheckItem("settle", true, values, "", []);
        var wait = (m.WaitMs / 1000.0).ToString("0.0#", inv);
        bool unsettled = m.Blocks.Any(b => b.Problem == "unsettled");
        var why = string.Join(", ", m.Blocks.Where(b => b.Problem is not null).Select(b => $"{b.Block} {b.Problem}{(b.Reason is null ? "" : ": " + b.Reason)}"));
        return new PrecheckItem("settle", false, values, unsettled
            ? $"over the town the frames still changed {o.For.TotalSeconds:0.#} s after the streaming had ended ({why}); the visit waits the default {wait} s"
            : $"the settle wait could not be measured ({why}); the visit waits the default {wait} s",
            [unsettled ? "unsettled" : "notMeasured"]);
    }

    /// <summary>Weather, time and NPCs with the capture environment on: clear, noon, nobody near.</summary>
    static PrecheckItem Environment(StatusReply? status)
    {
        if (status is null) return Fail("environment", "noStatus", "the resource did not report its status");
        var wrong = new List<string>();
        var codes = new List<string>();
        if (status.Weather != "EXTRASUNNY")
        {
            wrong.Add($"the weather is {status.Weather}, not EXTRASUNNY: stop the resource that sets the weather");
            codes.Add("weather");
        }
        if (status.Hour != 12)
        {
            wrong.Add($"the time is {status.Hour}:{status.Minute:00}, not noon: stop the resource that sets the clock");
            codes.Add("time");
        }
        if (status.NearPeds > 0 || status.NearVehicles > 0)
        {
            wrong.Add($"{status.NearPeds} NPCs and {status.NearVehicles} vehicles are near: stop the resource that sets the traffic density");
            codes.Add("npcs");
        }
        return new PrecheckItem("environment", wrong.Count == 0, Values(("weather", status.Weather), ("hour", status.Hour.ToString(CultureInfo.InvariantCulture)),
            ("minute", status.Minute.ToString(CultureInfo.InvariantCulture)),
            ("nearPeds", status.NearPeds.ToString(CultureInfo.InvariantCulture)), ("nearVehicles", status.NearVehicles.ToString(CultureInfo.InvariantCulture)),
            ("peds", status.Peds.ToString(CultureInfo.InvariantCulture)), ("vehicles", status.Vehicles.ToString(CultureInfo.InvariantCulture))), string.Join("; ", wrong), codes);
    }

    /// <summary>An item a visit without shots does not need (not checked, code <c>notNeeded</c>).</summary>
    static PrecheckItem NotNeeded(string id) => new(id, null, Values(), "", ["notNeeded"]);

    static IReadOnlyDictionary<string, string> Values(params (string Key, string Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    static PrecheckItem Pass(string id, params (string, string)[] values) => new(id, true, Values(values), "", []);

    static PrecheckItem Fail(string id, string code, string message, params (string, string)[] values) => new(id, false, Values(values), message, [code]);

    static IReadOnlyList<string> Codes(string? code) => code is null ? [] : [code];
}
