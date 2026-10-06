using System.Globalization;
using System.Threading.Channels;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Satellite;

/// <summary>
/// The satellite map while the visit goes on: each block the visit took is orthorectified right away, beside the visit
/// on a worker slot of its own (<see cref="StageContext.RunBesideAsync"/>), with a provisional scale correction: the
/// given one, else the project's own, else the default for the field of view (<see cref="ScaleStore.Default"/>). It
/// writes the block's z8 tiles and the tiles above it (z7..z0), and records the block's orthorectification, so the map
/// shows the capture as it goes.
/// <para>
/// After the visit the ortho stage confirms the correction: a measured one that agrees with it keeps these tiles
/// (<see cref="ScaleStore.Agrees"/>), another one makes every block again; the lower zooms are then made whole (the sea
/// outside the range, every parent). Best effort: blocks it did not get to (no free slot, the visit ended first) are
/// left to the ortho stage.
/// </para>
/// </summary>
/// <param name="fixedScale">The correction given for the run (the ortho stage uses it as well).</param>
/// <param name="defaultScale">In place of the bundled default (tests).</param>
public sealed class ProvisionalOrtho((double Qx, double Qy)? fixedScale = null, (double Qx, double Qy)? defaultScale = null) : IVisitFollower
{
    /// <summary>How the work is named among the units being processed and in <c>units.csv</c>.</summary>
    public const string Unit = "ortho.provisional";

    StageContext? _ctx;
    TileStore _tiles = null!;
    double _qx, _qy;
    Channel<BlockId>? _queue;
    CancellationTokenSource? _stop;
    Task? _loop;
    int _done, _taken;

    /// <summary>The correction the tiles are made with (after <see cref="Start"/>); null when it does not run.</summary>
    public (double Qx, double Qy)? Scale { get; private set; }

    public void Start(StageContext ctx, double fov)
    {
        var store = new ScaleStore(ctx.Folder);
        string from;
        (double Qx, double Qy) q;
        if (fixedScale is { } f) (q, from) = (f, "given");
        else if (store.Get(fov) is { } saved) (q, from) = ((saved.Qx, saved.Qy), "the project's");
        else if ((defaultScale ?? ScaleStore.Default(fov)) is { } d) (q, from) = (d, "the default, to be confirmed after the capture");
        else
        {
            ctx.Log("visit: no scale correction is known for this field of view; the satellite tiles are made after the capture");
            return;
        }
        (_qx, _qy) = q;
        Scale = q;
        // tiles made with another correction: they are all made again (as the ortho stage does)
        if (store.TilesScale() is not { } made || made.Qx != _qx || made.Qy != _qy)
        {
            ctx.State.ClearStage(StageKeys.Ortho);
            store.SetTilesScale(_qx, _qy);
        }
        _ctx = ctx;
        _tiles = TileStore.Keeping(ctx.Folder, MapSet.Satellite.Id);
        _queue = Channel.CreateUnbounded<BlockId>(new UnboundedChannelOptions { SingleReader = true });
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        _loop = Task.Run(LoopAsync);
        ctx.Log(string.Format(CultureInfo.InvariantCulture, "visit: each block is orthorectified once taken, with the scale correction qx {0:F5} qy {1:F5} ({2})", _qx, _qy, from));
    }

    public void Taken(BlockId block)
    {
        if (_queue is null) return;
        Interlocked.Increment(ref _taken);
        _queue.Writer.TryWrite(block);
    }

    public void Finish()
    {
        if (_queue is null || _loop is null || _stop is null) return;
        _queue.Writer.TryComplete();
        _stop.Cancel();
        try { _loop.Wait(); }
        catch (AggregateException) { }
        _stop.Dispose();
        _ctx!.Log($"visit: {_done} of {_taken} blocks orthorectified beside the visit" + (_done < _taken ? "; the ortho step does the rest" : ""));
        _queue = null;
    }

    async Task LoopAsync()
    {
        var ctx = _ctx!;
        var stop = _stop!.Token;
        try
        {
            await foreach (var b in _queue!.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                // false while the level is one worker: the block is left to the ortho step
                try { await ctx.RunBesideAsync(Unit, b.Name, (report, token) => Orthorectify(b, report, token), stop).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Log($"visit: {b} was not orthorectified beside the visit ({ex.Message}); the ortho step tries it");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    void Orthorectify(BlockId b, Action<string?, double> report, CancellationToken token)
    {
        report("load", 0);
        OrthoStage.Orthorectify(OrthoStage.CaptureOf(_ctx!.Folder, b, SurfaceHeights.PhotoItems(_ctx.Project)), b, _tiles, _qx, _qy, token, report);
        token.ThrowIfCancellationRequested();
        // the tiles above the block, so the whole map shows it (the lower zooms stage makes them whole after the capture)
        report("parents", 0.9);
        for (int y = b.Ty / 2; y < b.Ty / 2 + 2; y++)
            for (int x = b.Tx / 2; x < b.Tx / 2 + 2; x++) _tiles.BuildParent(WorldGrid.Zoom - 1, x, y);
        for (int z = WorldGrid.Zoom - 2; z >= 0; z--) _tiles.BuildParent(z, b.Bx >> (WorldGrid.Zoom - 2 - z), b.By >> (WorldGrid.Zoom - 2 - z));
        _ctx.State.SetStageDone(StageKeys.Ortho, b.Name, DateTime.UtcNow);
        Interlocked.Increment(ref _done);
    }
}
