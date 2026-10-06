using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Capture;

/// <param name="Ms">When the frame was taken, milliseconds after READY came in.</param>
/// <param name="Changed">
/// Share of the pixels in the part of the frame the map uses that differ from the last frame by more than
/// <see cref="SettleProbe.Options.Levels"/> in a channel (0..1); null for a frame without the ready beacon of the request.
/// </param>
public sealed record SettleSample(int Ms, double? Changed);

/// <param name="StableMs">
/// From the frame taken at this time on, the frames no longer changed: what the block needed after its streaming
/// requests had reached 0. Null when it could not be measured (<paramref name="Problem"/>).
/// </param>
/// <param name="Fps">Frames the game drew per second while settling (from READY).</param>
/// <param name="Problem">
/// Null when measured; <c>tile</c> (no READY, <paramref name="Reason"/> says why), <c>water</c> (the block has water,
/// which never stops moving), <c>frames</c> (too few frames with the request's beacon), <c>unsettled</c> (the frames
/// still changed at the end).
/// </param>
public sealed record SettleBlock(string Block, int? StableMs, double Fps, int SettleMs, int MaxReq, int Water, IReadOnlyList<SettleSample> Samples,
    string? Problem, string? Reason = null);

/// <summary>A settle measurement (<see cref="SettleProbe"/>): where, each block tried, and the wait the visit takes from it.</summary>
/// <param name="District"><c>north</c> (Paleto Bay) or <c>south</c> (Los Santos downtown).</param>
/// <param name="StableMs">The longest time of the measured blocks; null when fewer than needed could be measured.</param>
/// <param name="WaitMs">
/// How long the visit has the streaming requests stay at 0 before READY: <see cref="SettleProbe.Options.Factor"/> times
/// <paramref name="StableMs"/>, or <see cref="SettleProbe.DefaultWaitMs"/> without a measurement.
/// </param>
/// <param name="Fps">Mean frames per second over the measured blocks (over every READY when none was measured).</param>
public sealed record SettleMeasurement(DateTime AtUtc, string District, IReadOnlyList<SettleBlock> Blocks, int? StableMs, int WaitMs, double? Fps)
{
    public bool Ok => StableMs is not null;
}

/// <summary>
/// <c>state/settle.json</c>: the last settle measurement of the work folder that succeeded. The visit takes its wait from
/// it; without one it waits <see cref="SettleProbe.DefaultWaitMs"/>. Each pre-check measures again and replaces it, or
/// removes it when the measurement failed.
/// </summary>
public sealed class SettleStore(WorkFolder folder)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    string PathOf => Path.Combine(folder.State, "settle.json");

    public SettleMeasurement? Load()
    {
        if (!File.Exists(PathOf)) return null;
        try { return JsonSerializer.Deserialize<SettleMeasurement>(File.ReadAllText(PathOf), Json); }
        catch (JsonException) { return null; }
    }

    public void Save(SettleMeasurement m)
    {
        Directory.CreateDirectory(folder.State);
        File.WriteAllText(PathOf, JsonSerializer.Serialize(m, Json), new UTF8Encoding(false));
    }

    public void Clear()
    {
        if (File.Exists(PathOf)) File.Delete(PathOf);
    }

    /// <summary>The wait for the visit and the measurement it comes from (null: the default).</summary>
    public (int WaitMs, SettleMeasurement? From) Wait() => Load() is { Ok: true } m ? (m.WaitMs, m) : (SettleProbe.DefaultWaitMs, null);
}

/// <summary>
/// Measures how long this PC's game still changes the picture after its streaming requests have reached 0 (distant
/// detail fading in, textures sharpening), with the capture environment on. Over built-up blocks of the district
/// farther from where the player stood (a place the game has not loaded yet), the resource says READY as soon as the
/// requests are 0 (<c>quietMs</c> 0), and frames are taken from the window every <see cref="Options.Every"/> for
/// <see cref="Options.For"/>. Each frame is compared with the last one over the part the map uses; the first frame from
/// which on no frame differs any more (fewer than <see cref="Options.Limit"/> of its pixels by more than
/// <see cref="Options.Levels"/>) gives the block's time. The first block is measured cold, the next one next to it; the
/// visit then waits <see cref="Options.Factor"/> times the longer time.
/// </summary>
public sealed class SettleProbe(GameLink link, IGameWindow window, SettleProbe.Options? options = null)
{
    /// <summary>The wait without a measurement (the resource's default too): 90 frames at 60 frames per second.</summary>
    public const int DefaultWaitMs = 1500;
    /// <summary>The longest wait the resource takes.</summary>
    public const int MaxWaitMs = 10000;

    public sealed record Options
    {
        public double Fov { get; init; } = 2.0;
        public double Margin { get; init; } = 1.06;
        /// <summary>Between the frames taken after READY.</summary>
        public TimeSpan Every { get; init; } = TimeSpan.FromMilliseconds(200);
        /// <summary>How long frames are taken after READY.</summary>
        public TimeSpan For { get; init; } = TimeSpan.FromSeconds(3);
        /// <summary>Blocks to measure (candidates of the district are tried in order until this many are measured).</summary>
        public int Blocks { get; init; } = 2;
        /// <summary>A pixel counts as changed when a channel differs by more than this.</summary>
        public int Levels { get; init; } = 24;
        /// <summary>A frame counts as unchanged when fewer than this share of its pixels changed.</summary>
        public double Limit { get; init; } = 0.0005;
        /// <summary>The visit's wait over the longest time measured.</summary>
        public double Factor { get; init; } = 1.5;
        /// <summary>Frames with the beacon a block needs to be measured.</summary>
        public int MinFrames { get; init; } = 5;
    }

    /// <param name="Id"><c>north</c> or <c>south</c>.</param>
    /// <param name="Blocks">Built-up blocks without water, the ones with the most streaming first, next to each other.</param>
    public sealed record District(string Id, IReadOnlyList<BlockId> Blocks)
    {
        public (double X, double Y) Center => (Blocks.Average(b => b.Center.X), Blocks.Average(b => b.Center.Y));
    }

    /// <summary>
    /// Paleto Bay's town in the north and Los Santos downtown in the south: the blocks with the most streaming requests
    /// there in a whole-map capture, and no water on the resource's 5 x 5 grid.
    /// </summary>
    public static readonly IReadOnlyList<District> Districts =
    [
        new("north", [BlockId.Parse("z8_52_28"), BlockId.Parse("z8_56_28"), BlockId.Parse("z8_52_32"), BlockId.Parse("z8_60_28")]),
        new("south", [BlockId.Parse("z8_60_124"), BlockId.Parse("z8_60_128"), BlockId.Parse("z8_56_124"), BlockId.Parse("z8_56_128")]),
    ];

    /// <summary>The district farther from (x, y); the north one when the player's place is not known (players start in Los Santos).</summary>
    public static District Farther(double? x, double? y)
    {
        if (x is not { } px || y is not { } py) return Districts[0];
        return Districts.MaxBy(d => Math.Pow(d.Center.X - px, 2) + Math.Pow(d.Center.Y - py, 2))!;
    }

    readonly Options _o = options ?? new Options();

    /// <summary>
    /// Measures in the district farther from (<paramref name="playerX"/>, <paramref name="playerY"/>). The capture
    /// environment has to be on. With <paramref name="folder"/>, the first and the last frame of each measured block
    /// (the part the map uses) go there as <c>settle-&lt;block&gt;-first.png</c> / <c>-last.png</c>.
    /// </summary>
    public async Task<SettleMeasurement> RunAsync(double? playerX, double? playerY, string? folder, CancellationToken token)
    {
        var at = DateTime.UtcNow;
        var district = Farther(playerX, playerY);
        var blocks = new List<SettleBlock>();
        foreach (var block in district.Blocks)
        {
            if (blocks.Count(b => b.StableMs is not null) >= _o.Blocks) break;
            blocks.Add(await MeasureAsync(block, folder, token).ConfigureAwait(false));
        }
        var measured = blocks.Where(b => b.StableMs is not null).ToList();
        int? stable = measured.Count >= _o.Blocks ? measured.Max(b => b.StableMs!.Value) : null;
        int wait = stable is { } s ? Math.Min(MaxWaitMs, (int)Math.Ceiling(_o.Factor * s / 10.0) * 10) : DefaultWaitMs;
        var withFps = (measured.Count > 0 ? measured : blocks.Where(b => b.Problem != "tile").ToList()).Select(b => b.Fps).ToList();
        return new SettleMeasurement(at, district.Id, blocks, stable, wait, withFps.Count > 0 ? Math.Round(withFps.Average(), 1) : null);
    }

    async Task<SettleBlock> MeasureAsync(BlockId block, string? folder, CancellationToken token)
    {
        var tile = await link.TileAsync(block, _o.Fov, _o.Margin, link.LastSeq, 0, token).ConfigureAwait(false);
        if (tile.Outcome != TileOutcome.Ready)
            return new SettleBlock(block.Name, null, 0, 0, 0, 0, [], "tile", tile.Reason);
        var ready = tile.Line!;
        double fps = ready.Number("fps") ?? 0;
        int settle = ready.Int("settle") ?? 0, maxReq = ready.Int("maxreq") ?? 0, water = ready.Int("water") ?? 0;
        if (water > 0) return new SettleBlock(block.Name, null, fps, settle, maxReq, water, [], "water");

        // frames every Every from READY on, only the part the map uses of those with this request's beacon
        var ms = new List<int>();
        var maps = new List<Frame?>();
        var sw = Stopwatch.StartNew();
        int count = (int)Math.Round(_o.For / _o.Every) + 1;
        (int W, int H)? size = null;
        Area area = default;
        for (int i = 0; i < count; i++)
        {
            var left = _o.Every * i - sw.Elapsed;
            if (left > TimeSpan.Zero) await link.PauseAsync(left, token).ConfigureAwait(false);
            ms.Add((int)sw.ElapsedMilliseconds);
            var f = window.Capture();
            if (f is not null && size is null) { size = (f.Width, f.Height); area = FrameChecks.MapArea(f.Width, f.Height, _o.Margin); }
            maps.Add(f is not null && (f.Width, f.Height) == size && FrameChecks.ReadyFor(f, tile.Seq) ? Crop(f, area) : null);
        }
        var valid = Enumerable.Range(0, count).Where(i => maps[i] is not null).ToList();
        if (valid.Count < _o.MinFrames)
            return new SettleBlock(block.Name, null, fps, settle, maxReq, water, ms.Select(m => new SettleSample(m, null)).ToList(), "frames");

        var last = maps[valid[^1]]!;
        var changed = new double?[count];
        // the transcript also has the shares for a finer and a coarser level, to review the levels on real frames
        int finer = Math.Max(1, _o.Levels / 3), coarser = _o.Levels * 2;
        var notes = new List<string>();
        for (int i = 0; i < count; i++)
        {
            if (maps[i] is not { } map) { notes.Add($"{ms[i]}ms=-"); continue; }
            var shares = FrameChecks.Changed(map, last, [finer, _o.Levels, coarser]);
            changed[i] = shares[1];
            notes.Add(string.Format(CultureInfo.InvariantCulture, "{0}ms={1:0.###}/{2:0.###}/{3:0.###}%", ms[i], shares[0] * 100, shares[1] * 100, shares[2] * 100));
        }
        var samples = Enumerable.Range(0, count).Select(i => new SettleSample(ms[i], changed[i])).ToList();
        // the first frame from which on every frame (before the last) is unchanged; the one before the last has to be
        int stableAt = valid.Count - 1;
        for (int k = valid.Count - 2; k >= 0 && changed[valid[k]] < _o.Limit; k--) stableAt = k;
        link.Note(string.Format(CultureInfo.InvariantCulture, "(settle {0}: pixels changed from the last frame by more than {1}/{2}/{3} levels: {4})",
            block.Name, finer, _o.Levels, coarser, string.Join(' ', notes)));
        if (folder is not null)
        {
            var first = maps[valid[0]]!;
            Images.SavePng(Path.Combine(folder, $"settle-{block.Name}-first.png"), first.Rgba, first.Width, first.Height);
            Images.SavePng(Path.Combine(folder, $"settle-{block.Name}-last.png"), last.Rgba, last.Width, last.Height);
        }
        return stableAt == valid.Count - 1
            ? new SettleBlock(block.Name, null, fps, settle, maxReq, water, samples, "unsettled")
            : new SettleBlock(block.Name, ms[valid[stableAt]], fps, settle, maxReq, water, samples, null);
    }

    static Frame Crop(Frame f, Area a)
    {
        var rgba = new byte[a.Width * a.Height * 4];
        for (int y = 0; y < a.Height; y++)
            Buffer.BlockCopy(f.Rgba, ((a.Y + y) * f.Width + a.X) * 4, rgba, y * a.Width * 4, a.Width * 4);
        return new Frame(a.Width, a.Height, rgba);
    }
}
