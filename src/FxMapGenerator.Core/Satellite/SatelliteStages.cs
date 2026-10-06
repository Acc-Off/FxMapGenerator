using System.Text.Json;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Satellite;

/// <summary>
/// Ortho + satellite tiles, one unit per block. First the scale correction (given, stored for the field of view, or
/// measured from all captured blocks); when it differs from the one the ortho records were made with, all records are
/// dropped at once, so a run stopped half-way continues with the blocks still left. A measured correction that agrees
/// with the one the records were made with (<see cref="ScaleStore.Agrees"/>: the visit orthorectifies each block right
/// away with a provisional correction, see <see cref="ProvisionalOrtho"/>) keeps those records and becomes the project's.
/// A block is left when its record is older than its shot or the heights its photo is placed with
/// (<see cref="SurfaceHeights.PhotoItems"/>). Each unit orthorectifies the capture and
/// writes its 4 x 4 z8 tiles.
/// </summary>
public sealed class OrthoStage(OrthoStage.Options? options = null) : Stage
{
    public sealed record Options((double Qx, double Qy)? FixedScale = null, bool Recalibrate = false);

    readonly Options _o = options ?? new Options();
    Dictionary<BlockId, ScaleCalibration.Capture> _captures = new();
    TileStore _tiles = null!;
    double _qx = 1, _qy = 1;
    IReadOnlyList<BlockItem> _photo = [BlockItem.Height];

    public override string Id => "ortho";
    public override string Row => "ortho";
    public override string? RecordKey => StageKeys.Ortho;
    public override string UnitName => "block";
    /// <summary>About 70 MB measured (the capture, its height grid, the 1080 px block and its tiles), with room to spare.</summary>
    public override long MemoryPerUnit => 128L << 20;

    /// <summary>The correction used and where it came from (after <see cref="Prepare"/>).</summary>
    public (double Qx, double Qy, string Source)? Scale { get; private set; }

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        var state = ctx.State;
        var folder = ctx.Folder;
        _tiles = TileStore.Keeping(folder, MapSet.Satellite.Id);
        _photo = SurfaceHeights.PhotoItems(ctx.Project);
        var captured = ctx.Project.Range.Keys.Where(b => Captured(state, b, _photo)).ToList();
        if (captured.Count == 0)
        {
            ctx.Log("ortho: no captured blocks in the range");
            return Array.Empty<string>();
        }
        // a block whose files cannot be read stays in the units and fails there with the reason; the rest goes on
        _captures = new Dictionary<BlockId, ScaleCalibration.Capture>();
        foreach (var b in captured)
        {
            try { _captures[b] = CaptureOf(folder, b, _photo); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { ctx.Log($"ortho: {b.Name} cannot be read: {ex.Message}"); }
        }
        if (_captures.Count == 0) return captured.Where(b => Stale(state, b, _photo)).Select(b => b.Name).ToList();
        double fov = _captures.Values.First().Camera.Fov;
        var store = new ScaleStore(folder);
        string source;
        if (_o.FixedScale is { } f) { (_qx, _qy) = f; source = "given"; }
        else if (!_o.Recalibrate && store.Get(fov) is { } saved) { (_qx, _qy) = (saved.Qx, saved.Qy); source = $"stored ({saved.Pairs} pairs)"; }
        else
        {
            var (_, final) = ScaleCalibration.Calibrate(_captures, ctx.Parallel, ctx.Log);
            int pairs = final.X.Used + final.Y.Used;
            if (pairs == 0)
            {
                // nothing to measure (a single block, or flat water): the default for the field of view, measured next time
                (_qx, _qy) = ScaleStore.Default(fov) ?? (1, 1);
                source = ScaleStore.Default(fov) is null ? "no overlapping pairs, none" : "no overlapping pairs, the default";
            }
            else
            {
                (_qx, _qy) = (final.Qx, final.Qy);
                source = $"measured ({final.X.Used} + {final.Y.Used} pairs)";
                if (store.TilesScale() is { } before && ScaleStore.Agrees((before.Qx, before.Qy), (_qx, _qy)))
                {
                    // the tiles made while capturing, with a provisional correction, draw the same map: they stay
                    source += string.Format(System.Globalization.CultureInfo.InvariantCulture, " as qx {0:F5} qy {1:F5}, which agrees with the correction the tiles were made with", _qx, _qy);
                    (_qx, _qy) = (before.Qx, before.Qy);
                }
                store.Set(fov, new ScaleStore.Entry(_qx, _qy, pairs, DateTime.UtcNow));
            }
        }
        Scale = (_qx, _qy, source);
        if (store.TilesScale() is not { } made || made.Qx != _qx || made.Qy != _qy)
        {
            // tiles made with another (or an unrecorded) correction: every block has to be made again
            state.ClearStage(StageKeys.Ortho);
            store.SetTilesScale(_qx, _qy);
        }
        var todo = captured.Where(b => Stale(state, b, _photo)).ToList();
        ctx.Log($"scale correction qx {_qx:F5} qy {_qy:F5} ({source}); {todo.Count} of {captured.Count} captured blocks to orthorectify");
        return todo.Select(b => b.Name).ToList();
    }

    public override void Run(UnitContext ctx)
    {
        var b = BlockId.Parse(ctx.Unit);
        ctx.Report("load", 0);
        var cap = _captures.TryGetValue(b, out var c) ? c : CaptureOf(ctx.Folder, b, _photo);
        Orthorectify(cap, b, _tiles, _qx, _qy, ctx.Token, ctx.Report);
    }

    /// <summary>The capture files of a block, the photo placed with <paramref name="heights"/> (default: the height grid; <see cref="SurfaceHeights.PhotoItems"/>).</summary>
    public static ScaleCalibration.Capture CaptureOf(WorkFolder folder, BlockId b, IReadOnlyList<BlockItem>? heights = null) =>
        new(folder.CapturePng(b), CameraLine.Read(folder.CaptureCamera(b)), SurfaceHeights.PathsOf(folder, b, heights ?? [BlockItem.Height]));

    /// <summary>A block with its shot and every height item its photo is placed with.</summary>
    public static bool Captured(StateStore state, BlockId b, IReadOnlyList<BlockItem> photo) =>
        state.Has(b, BlockItem.Shot) && photo.All(i => state.Has(b, i));

    /// <summary>Orthorectifies one captured block with the correction (qx, qy) and writes its 4 x 4 z8 tiles.</summary>
    public static void Orthorectify(ScaleCalibration.Capture cap, BlockId b, TileStore tiles, double qx, double qy, CancellationToken token, Action<string?, double> report)
    {
        var grid = SurfaceHeights.Read(cap.Heights);
        grid.FillMissing(cap.Camera.GroundZ);
        var rgba = Images.LoadRgba(cap.Png, out var w, out var h);
        token.ThrowIfCancellationRequested();
        report("ortho", 0.3);
        var square = Orthorectifier.Render(rgba, w, h, cap.Camera, grid, qx, qy);
        token.ThrowIfCancellationRequested();
        report("tiles", 0.8);
        tiles.WriteBlock(WorldGrid.Zoom, b.Tx, b.Ty, square, Orthorectifier.OutPx);
    }

    /// <summary>Captured blocks whose ortho record is missing or older than the shot or the heights.</summary>
    public override int CountReady(Project project, StateStore state)
    {
        var photo = SurfaceHeights.PhotoItems(project);
        return project.Range.Keys.Count(b => Captured(state, b, photo) && Stale(state, b, photo));
    }

    /// <summary>A captured block whose ortho record is missing or older than its shot or one of its height items.</summary>
    public static bool Stale(StateStore state, BlockId b, IReadOnlyList<BlockItem> photo) => state.StageDone(StageKeys.Ortho, b.Name) is not { } done
        || photo.Prepend(BlockItem.Shot).Any(i => state.ItemTime(b, i) is not { } t || t > done);
}

/// <summary>
/// The satellite set's lower zooms, one unit: open sea painted on every z8 tile of the project's frame outside the range,
/// then z7..z0 from their children (<see cref="LowZooms"/>). Left when it was never made, an ortho record is newer than it
/// or the frame changed.
/// </summary>
public sealed class SatelliteLowZoomStage : Stage
{
    public override string Id => "lowZoom.satellite";
    public override string Row => "lowZoom.satellite";
    public override string? RecordKey => StageKeys.LowZoom;
    public override string UnitName => "set";
    public override long MemoryPerUnit => 64L << 20;

    public override IReadOnlyList<string> Prepare(StageContext ctx)
    {
        if (ctx.State.NewestStageDone(StageKeys.Ortho, ctx.Project.Range.Keys.Select(b => b.Name)) is null)
        {
            ctx.Log("sea and lower zooms: no satellite tiles yet");
            return Array.Empty<string>();
        }
        if (IsStale(ctx.State, ctx.Project.Range.Keys) || FrameChanged(ctx.Project)) return new[] { MapSet.Satellite.Id };
        ctx.Log("sea and lower zooms: up to date");
        return Array.Empty<string>();
    }

    /// <summary>One set when there are satellite tiles and the lower zooms are older than them (or were never made).</summary>
    public override int CountReady(Project project, StateStore state) =>
        state.NewestStageDone(StageKeys.Ortho, project.Range.Keys.Select(b => b.Name)) is not null && (IsStale(state, project.Range.Keys) || FrameChanged(project)) ? 1 : 0;

    /// <summary>The lower zooms were made for another frame than the project's.</summary>
    public static bool FrameChanged(Project project) => LowZooms.Recorded(new WorkFolder(project.WorkFolderPath), MapSet.Satellite.Id) != project.Frame;

    /// <summary>True when the lower zooms were never made or a block's ortho tiles are newer than them.</summary>
    public static bool IsStale(StateStore state, IEnumerable<BlockId> range)
    {
        var done = state.StageDone(StageKeys.LowZoom, MapSet.Satellite.Id);
        if (done is null) return true;
        var newest = state.NewestStageDone(StageKeys.Ortho, range.Select(b => b.Name));
        return newest is not null && newest > done;
    }

    public override void Run(UnitContext ctx)
    {
        var range = ctx.Project.Range;
        var tiles = TileStore.Keeping(ctx.Folder, MapSet.Satellite.Id);
        ctx.Report("sea", 0);
        var sea = SeaColour(range, tiles);
        var seaPng = TileStore.EncodePng(Flat(sea), TileStore.TileSize, TileStore.TileSize);
        var frame = ctx.Project.Frame;
        int removed = LowZooms.Prepare(ctx.Folder, MapSet.Satellite.Id, tiles, frame);
        if (removed > 0) ctx.Log($"{removed} tiles outside the map's frame removed");
        var seaTiles = LowZooms.SeaTiles(frame, range.Keys.ToHashSet());
        ctx.Parallel.ForEach(seaTiles, t => tiles.WriteBytes(WorldGrid.Zoom, t.X, t.Y, seaPng));
        int parents = LowZooms.BuildParents(tiles, frame, ctx.Parallel, (s, f) => ctx.Report(s, f), ctx.Token);
        LowZooms.Record(ctx.Folder, MapSet.Satellite.Id, frame);
        ctx.Log($"sea colour {sea} on {seaTiles.Count} tiles, {parents} tiles at z7..z0");
    }

    /// <summary>
    /// The colour of open sea: the per-channel median over the 16 tiles of the range's first water block (north-west
    /// first), i.e. the coastal sea as captured. Falls back to a dark sea blue.
    /// </summary>
    public static (byte R, byte G, byte B) SeaColour(IReadOnlyDictionary<BlockId, BlockClass> range, TileStore tiles)
    {
        var hist = new long[3, 256];
        long count = 0;
        foreach (var b in range.Where(kv => kv.Value == BlockClass.Water).Select(kv => kv.Key).OrderBy(b => b))
        {
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                {
                    var t = tiles.Read(WorldGrid.Zoom, b.Tx + i, b.Ty + j);
                    if (t is null) continue;
                    for (int p = 0; p < t.Length; p += 4) { hist[0, t[p]]++; hist[1, t[p + 1]]++; hist[2, t[p + 2]]++; count++; }
                }
            if (count > 0) break;
        }
        if (count == 0) return (3, 41, 42);
        byte Median(int ch)
        {
            long lo = (count - 1) / 2, hi = count / 2, seen = 0;
            int a = -1, c = -1;
            for (int v = 0; v < 256; v++)
            {
                seen += hist[ch, v];
                if (a < 0 && seen > lo) a = v;
                if (c < 0 && seen > hi) { c = v; break; }
            }
            return (byte)((a + c) / 2);
        }
        return (Median(0), Median(1), Median(2));
    }

    static byte[] Flat((byte R, byte G, byte B) c)
    {
        var t = new byte[TileStore.TileSize * TileStore.TileSize * 4];
        for (int i = 0; i < t.Length; i += 4) { t[i] = c.R; t[i + 1] = c.G; t[i + 2] = c.B; t[i + 3] = 255; }
        return t;
    }
}

/// <summary>
/// <c>state/scale.json</c>: the scale correction measured per field of view (<c>fov2</c>, ...) and the one the ortho
/// records (and so the satellite tiles) belong to (<c>tiles</c>).
/// </summary>
public sealed class ScaleStore(WorkFolder folder)
{
    public sealed record Entry(double Qx, double Qy, int Pairs, DateTime MeasuredUtc);

    const string TilesKey = "tiles";

    /// <summary>
    /// The correction per field of view measured over the whole default range (935 east-west and 954 north-south pairs,
    /// 1920 x 1080 frames, game build 3258). It comes from how the game projects the picture, not from the PC; the visit
    /// orthorectifies with it until the project has measured its own, and the ortho step checks it against that.
    /// </summary>
    static readonly Dictionary<string, (double Qx, double Qy)> Defaults = new() { ["fov2"] = (0.99667, 0.98868) };

    /// <summary>The bundled correction for a field of view, or null when there is none.</summary>
    public static (double Qx, double Qy)? Default(double fov) => Defaults.TryGetValue(Key(fov), out var q) ? q : null;

    /// <summary>
    /// The largest difference of two corrections that still draw the same map: a block's edge (half a block from the
    /// camera) moves by less than a tenth of a z8 pixel, 3 cm (0.1 x 70.3125 / 256 m over 140.625 m: 1.95e-4).
    /// </summary>
    public const double AgreeLimit = 0.1 * WorldGrid.TileSize / 256 / (WorldGrid.BlockSize / 2);

    /// <summary>True when the two corrections differ by less than <see cref="AgreeLimit"/> on both axes.</summary>
    public static bool Agrees((double Qx, double Qy) a, (double Qx, double Qy) b) =>
        Math.Abs(a.Qx - b.Qx) < AgreeLimit && Math.Abs(a.Qy - b.Qy) < AgreeLimit;

    public Entry? TilesScale() => Load().GetValueOrDefault(TilesKey);

    public void SetTilesScale(double qx, double qy) => Put(TilesKey, new Entry(qx, qy, 0, DateTime.UtcNow));

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    string PathOf => Path.Combine(folder.State, "scale.json");

    static string Key(double fov) => "fov" + fov.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public Entry? Get(double fov) => Load().GetValueOrDefault(Key(fov));

    /// <summary>True when a correction was measured for some field of view (the ortho step then takes it instead of measuring).</summary>
    public bool Measured() => Load().Keys.Any(k => k.StartsWith("fov", StringComparison.Ordinal));

    public void Set(double fov, Entry e) => Put(Key(fov), e);

    void Put(string key, Entry e)
    {
        var all = Load();
        all[key] = e;
        Directory.CreateDirectory(folder.State);
        File.WriteAllText(PathOf, JsonSerializer.Serialize(all, Json));
    }

    Dictionary<string, Entry> Load() => File.Exists(PathOf)
        ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(PathOf), Json) ?? new()
        : new();
}
