using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;

namespace FxMapGenerator.Core.Jobs;

/// <summary>
/// One stage of a run. <see cref="Prepare"/> decides the units left (blocks, cells, a map set, or a single "world"),
/// then the job hands the units out to workers one by one; a stage done once for the whole world is a single unit
/// whose loops use <see cref="StageContext.Parallel"/>. Every call holds one worker slot.
/// </summary>
public abstract class Stage
{
    /// <summary>Name in logs and <c>units.csv</c>.</summary>
    public abstract string Id { get; }

    /// <summary>The to-do table row whose progress this stage is (<c>ortho</c>, <c>lowZoom.satellite</c>, ...).</summary>
    public abstract string Row { get; }

    /// <summary>The rows of the to-do table this stage does: its row, and those of its parts it takes on (the screen marks the rest as not available).</summary>
    public virtual IReadOnlyList<string> Rows => [Row];

    /// <summary>Project inputs read while the stage runs (they cannot be edited until it is over).</summary>
    public virtual IReadOnlyList<string> Reads => StageInputs.For(Row);

    /// <summary>
    /// Stage record (<c>state/stages.json</c>) the job writes for every unit done; null when the stage keeps its own
    /// records (the visit records data items).
    /// </summary>
    public virtual string? RecordKey => null;

    /// <summary>Runs in the game: one unit at a time whatever the parallel level.</summary>
    public virtual bool UsesGame => false;

    /// <summary>
    /// Worker slots a stage that uses the game may take beside its units while it runs, for work it hands on as it goes
    /// (<see cref="StageContext.RunBesideAsync"/>; the visit orthorectifies each block it took). Only while the level
    /// allows more than one worker.
    /// </summary>
    public virtual int Helpers => 0;

    /// <summary>Memory one unit needs at most, for holding back workers when memory runs short.</summary>
    public virtual long MemoryPerUnit => 256L << 20;

    /// <summary>What a unit is called in messages (<c>block</c>, <c>cell</c>, <c>set</c>, ...).</summary>
    public virtual string UnitName => "unit";

    /// <summary>The units left, in the order to hand them out. May measure or clean up first (one worker slot, plus helpers for its loops).</summary>
    public abstract IReadOnlyList<string> Prepare(StageContext ctx);

    /// <summary>
    /// A quick count of the units a run could do right now with the data at hand (no measuring, nothing written), for
    /// the run button: blocks without their capture are not counted, the visit comes first. Later stages that will
    /// have work once an earlier stage of the same run is done count what they have now.
    /// </summary>
    public abstract int CountReady(Project project, StateStore state);

    /// <summary>One unit. Throw to fail it; <see cref="StageContext.Token"/> is cancelled by "stop now".</summary>
    public abstract void Run(UnitContext ctx);

    /// <summary>After the units when the run was not stopped at once.</summary>
    public virtual void Finish(StageContext ctx, StageEnd end) { }

    /// <summary>Always last, also after "stop now" or an error; the token is never cancelled here (the visit leaves the game tidy).</summary>
    public virtual void Cleanup(StageContext ctx) { }
}

/// <summary>How the units of a stage ended.</summary>
public sealed record StageEnd(int Total, int Done, int Failed, bool StoppedAtBoundary)
{
    public bool Complete => !StoppedAtBoundary && Failed == 0 && Done == Total;
}

/// <summary>Work handed beside a stage's units (see <see cref="StageContext.RunBesideAsync"/>): it reports its step and share done, and stops on the token.</summary>
public delegate void BesideWork(Action<string?, double> report, CancellationToken token);

public class StageContext
{
    internal StageContext(Project project, WorkFolder folder, StateStore state, IParallelRunner parallel, Action<string> log, Func<int> workers,
        string runPath, Action<string> stopRun, Func<string, string, BesideWork, CancellationToken, Task<bool>>? beside = null,
        Action<string, IReadOnlyDictionary<string, string>>? announce = null)
    {
        Project = project;
        Folder = folder;
        State = state;
        Parallel = parallel;
        _log = log;
        _workers = workers;
        RunPath = runPath;
        _stopRun = stopRun;
        _beside = beside;
        _announce = announce;
    }

    readonly Action<string> _log;
    readonly Func<int> _workers;
    readonly Action<string> _stopRun;
    internal readonly Func<string, string, BesideWork, CancellationToken, Task<bool>>? _beside;
    internal readonly Action<string, IReadOnlyDictionary<string, string>>? _announce;

    /// <summary>The project as copied at the start of the run.</summary>
    public Project Project { get; }
    public WorkFolder Folder { get; }
    public StateStore State { get; }
    /// <summary>Loops inside a unit: the calling thread works, helpers join as slots come free.</summary>
    public IParallelRunner Parallel { get; }
    public CancellationToken Token => Parallel.Token;
    /// <summary>The worker limit now (it changes when the level does).</summary>
    public int Workers => _workers();

    /// <summary>The run's folder (<c>logs/run-&lt;time&gt;/</c>), for files of the stage's own such as a transcript.</summary>
    public string RunPath { get; }

    public void Log(string line) => _log(line);

    /// <summary>
    /// Stops the whole run at the next boundary, with the reason in the log: for a condition no other unit can get past
    /// (the game is gone, another player came on the server).
    /// </summary>
    public void StopRun(string reason) => _stopRun(reason);

    /// <summary>Tells the person watching something apart from the progress (see <see cref="Announcement"/>); nothing outside a job.</summary>
    public void Announce(string key, IReadOnlyDictionary<string, string> values) => _announce?.Invoke(key, values);

    /// <summary>
    /// Runs <paramref name="work"/> on a worker slot of its own beside the stage's units (a stage that uses the game may
    /// take <see cref="Stage.Helpers"/> such slots): waits for a free slot (until <paramref name="wait"/> gives up), lists
    /// the work among the units being processed as <paramref name="stage"/> / <paramref name="unit"/>, and gives the slot
    /// back after it. False, without running it, while the level is one worker (the stage's own unit takes it). "Stop
    /// now" cancels the wait and the work's token. Outside a job it simply runs the work.
    /// </summary>
    public async Task<bool> RunBesideAsync(string stage, string unit, BesideWork work, CancellationToken wait = default)
    {
        if (_beside is not null) return await _beside(stage, unit, work, wait).ConfigureAwait(false);
        await Task.Run(() => work((_, _) => { }, Token), wait).ConfigureAwait(false);
        return true;
    }
}

public sealed class UnitContext : StageContext
{
    readonly Action<string?, double> _report;

    internal UnitContext(StageContext stage, string unit, IParallelRunner parallel, Action<string?, double> report)
        : base(stage.Project, stage.Folder, stage.State, parallel, stage.Log, () => stage.Workers, stage.RunPath, stage.StopRun, stage._beside, stage._announce)
    {
        Unit = unit;
        _report = report;
    }

    public string Unit { get; }

    /// <summary>Where the unit is: a step name (<c>load</c>, <c>ortho</c>, <c>tiles</c>, ...) and how far (0..1).</summary>
    public void Report(string? phase, double fraction) => _report(phase, Math.Clamp(fraction, 0, 1));
}
