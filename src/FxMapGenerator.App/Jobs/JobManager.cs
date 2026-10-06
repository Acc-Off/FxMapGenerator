using FxMapGenerator.App.Services;
using FxMapGenerator.App.Web;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App.Jobs;

/// <summary>The stages of a build; tests put their own in.</summary>
public interface IBuildStages
{
    BuildPlan For(Project project, StateStore state, BuildStages.Options options);
}

/// <summary>
/// The stages with the game of this PC for the visit (unless the options name another), and the app's GTA V and key
/// folders for projects that leave them empty.
/// </summary>
public sealed class DefaultBuildStages(IGameAccess game, AppOptions app, SettingsStore settings) : IBuildStages
{
    public BuildPlan For(Project project, StateStore state, BuildStages.Options options)
    {
        var o = options.GameFiles is null ? options with { GameFiles = GameFilesDefaults.Of(app, settings.Current) } : options;
        return BuildStages.For(project, state, o.Game is null
            ? o with { Game = game, Visit = o.Visit ?? new VisitStage.Options { ResourceVersion = AppVersion.Value } }
            : o);
    }
}

/// <summary>The app's GTA V and key folders: the command line (<c>--gta</c>, <c>--keys</c>) before the settings.</summary>
public static class GameFilesDefaults
{
    public static GameFilesLocation.Defaults Of(AppOptions app, AppSettings settings) =>
        new(app.GtaFolderOverride ?? settings.GtaFolder, app.KeysFolderOverride ?? settings.KeysFolder);

    /// <summary>Where a minimap resource's parts from the game's files come from: the game of those folders, unless the options give the parts.</summary>
    public static MinimapGameFiles Minimap(AppOptions app, AppSettings settings) => app.MinimapGameFiles ?? MinimapGameFiles.Of(Of(app, settings));
}

/// <summary>
/// The one run at a time inside the app. Progress goes out as the SSE event <c>job</c> (the whole snapshot, at most
/// four times a second) and <c>jobLog</c> (each log line). The run goes on when the browser closes; closing the app
/// stops it at once (the running units are cancelled, what is done stays recorded).
/// It also keeps who uses the game: the game takes one console connection, so a run with work in the game, the
/// connection check and the start of the capture resource never overlap (<see cref="UseGame"/>; the SSE event
/// <c>gameUse</c> tells the screens what uses the game by hand).
/// </summary>
public sealed class JobManager : IHostedService
{
    /// <summary>What uses the game by hand: the connection check.</summary>
    public const string Precheck = "precheck";
    /// <summary>What uses the game by hand: the start of the capture resource.</summary>
    public const string ResourceStart = "resourceStart";

    static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(20);

    readonly object _sync = new();
    readonly EventHub _hub;
    readonly IBuildStages _stages;
    readonly IMemoryProbe _memory;
    readonly ILogger<JobManager> _logger;
    JobRunner? _runner;
    Task? _task;
    JobSnapshot? _last;
    int _publishPending;
    string? _gameByHand;

    public JobManager(EventHub hub, IBuildStages stages, IMemoryProbe memory, ILogger<JobManager> logger)
    {
        _hub = hub;
        _stages = stages;
        _memory = memory;
        _logger = logger;
    }

    public bool IsRunning
    {
        get { lock (_sync) return _task is { IsCompleted: false }; }
    }

    /// <summary>A run is going on with a stage in the game still to come or running (the pre-check waits for it).</summary>
    public bool UsesGame => Running()?.Snapshot() is { } s && s.Stages.Any(st => st.UsesGame && st.State is StageState.Waiting or StageState.Running);

    /// <summary>What uses the game by hand now (<see cref="Precheck"/>, <see cref="ResourceStart"/>), or null.</summary>
    public string? GameByHand
    {
        get { lock (_sync) return _gameByHand; }
    }

    /// <summary>
    /// Takes the game for work by hand (the connection check, the start of the capture resource) until the returned
    /// value is disposed. Refused with <see cref="JobException"/> while a run still has work in the game (GAME_BUSY) or
    /// other work by hand goes on (GAME_CHECKING, GAME_STARTING); a run with work in the game does not start meanwhile
    /// (<see cref="Start(string, int?, BuildStages.Options)"/>).
    /// </summary>
    public IDisposable UseGame(string kind)
    {
        lock (_sync)
        {
            if (UsesGame) throw new JobException("GAME_BUSY", "a run is using the game; wait until its work in the game is over");
            if (_gameByHand is { } other) throw ByHand(other);
            _gameByHand = kind;
            _hub.Publish("gameUse", new GameUseDto(kind));
        }
        return new GameHold(this);
    }

    static JobException ByHand(string kind) => kind == Precheck
        ? new JobException("GAME_CHECKING", "the connection check is using the game; wait until it is over")
        : new JobException("GAME_STARTING", "the capture resource is being started; wait until that is over");

    sealed class GameHold(JobManager owner) : IDisposable
    {
        int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._sync)
            {
                owner._gameByHand = null;
                owner._hub.Publish("gameUse", new GameUseDto(null));
            }
        }
    }

    /// <summary>The running job's snapshot, else the last one's, else null.</summary>
    public JobSnapshot? Current
    {
        get
        {
            lock (_sync) return _task is { IsCompleted: false } && _runner is not null ? _runner.Snapshot() : _last;
        }
    }

    /// <summary>The work folder state held by the running job, if it uses this work folder.</summary>
    public StateStore? RunningState(string workFolder)
    {
        lock (_sync)
            return _task is { IsCompleted: false } && _runner is not null && string.Equals(_runner.WorkFolderPath, Path.GetFullPath(workFolder), StringComparison.OrdinalIgnoreCase)
                ? _runner.State : null;
    }

    /// <summary>
    /// Starts a build of the project. <see cref="JobException"/> RUNNING / BUSY, GAME_CHECKING / GAME_STARTING when the
    /// build has work in the game while the game is used by hand; <see cref="ProjectException"/> for a bad project.
    /// </summary>
    /// <param name="workers">Null: the project's default.</param>
    public JobSnapshot Start(string projectPath, int? workers, BuildStages.Options options) =>
        Start(projectPath, workers, (p, s) =>
        {
            var plan = _stages.For(p, s, options);
            // called inside the lock: nothing takes the game by hand between this and the run counting as running
            if (_gameByHand is { } kind && plan.Stages.Any(st => st.UsesGame)) throw ByHand(kind);
            return plan;
        });

    /// <summary>Starts an export of the project (one stage; see <see cref="ExportStage"/>), as a run like a build.</summary>
    /// <param name="game">Where the minimap resource's parts from the game's files come from (null: this PC's game).</param>
    public JobSnapshot StartExport(string projectPath, int? workers, ExportOptions options, MinimapGameFiles? game = null) =>
        Start(projectPath, workers, (_, _) => new BuildPlan(ExportStages.For(options, AppVersion.Value, game), []));

    /// <summary>Starts the conversion of an edited picture (see <see cref="ConvertStages"/>), as a run like an export.</summary>
    /// <param name="game">Where the minimap resource's parts from the game's files come from (null: this PC's game).</param>
    public JobSnapshot StartConvert(string projectPath, int? workers, ConvertOptions options, MinimapGameFiles? game = null) =>
        Start(projectPath, workers, (_, _) => new BuildPlan(ConvertStages.For(options, AppVersion.Value, game), []));

    JobSnapshot Start(string projectPath, int? workers, Func<Project, StateStore, BuildPlan> stages)
    {
        lock (_sync)
        {
            if (_task is { IsCompleted: false }) throw new JobException("RUNNING", "a run is going on already; stop it first");
            var runner = JobRunner.Create(new JobSetup
            {
                ProjectPath = projectPath,
                Workers = workers,
                Stages = stages,
                Memory = _memory,
                Version = AppVersion.Value,
                Log = line => _logger.LogInformation("{Line}", line),
            });
            runner.Changed += SchedulePublish;
            runner.Logged += line => _hub.Publish("jobLog", new JobLogLine(runner.RunId, DateTime.UtcNow, line));
            _runner = runner;
            _task = Task.Run(async () =>
            {
                try
                {
                    var end = await runner.RunAsync().ConfigureAwait(false);
                    lock (_sync) _last = end;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Run {Run} ended with an error", runner.RunId);
                    lock (_sync) _last = runner.Snapshot();
                }
                Publish();
            });
            var snapshot = runner.Snapshot();
            _hub.Publish("job", snapshot);
            return snapshot;
        }
    }

    public JobSnapshot Stop(StopMode mode)
    {
        var runner = Running() ?? throw new JobException("NO_JOB", "no run is going on");
        runner.RequestStop(mode);
        return runner.Snapshot();
    }

    public JobSnapshot SetWorkers(int workers)
    {
        var runner = Running() ?? throw new JobException("NO_JOB", "no run is going on");
        runner.SetWorkers(workers);
        return runner.Snapshot();
    }

    /// <summary>The stages of the running job that still read <paramref name="inputKey"/>; null when it may be edited.</summary>
    public IReadOnlyList<string>? LockedBy(string inputKey) => Running()?.LockedBy(inputKey);

    /// <summary>The rows of the stages a running job of this project file is in now (stages run in turn: one); empty without one.</summary>
    public IReadOnlyList<string> RunningRows(string projectPath)
    {
        var snapshot = Running()?.Snapshot();
        return snapshot is not null && SamePath(snapshot.Project, projectPath)
            ? snapshot.Stages.Where(s => s.State == StageState.Running).Select(s => s.Row).ToList()
            : [];
    }

    /// <summary>Like <see cref="LockedBy(string)"/>, but only when the running job builds this project file.</summary>
    public IReadOnlyList<string>? LockedBy(string projectPath, string inputKey)
    {
        var runner = Running();
        return runner is not null && SamePath(runner.Snapshot().Project, projectPath) ? runner.LockedBy(inputKey) : null;
    }

    /// <summary>The units per stage of the running (or last) job, for the map; null when there was none.</summary>
    public IReadOnlyList<StageUnits>? Units()
    {
        lock (_sync) return _runner?.Units();
    }

    /// <summary>
    /// What the to-do table had left per row when the running (or last) job of this project file started, so the job list
    /// can show the parts that are not stages of their own (the visit's items) counting down; null without such a job.
    /// </summary>
    public RunStart? AtStart(string projectPath)
    {
        JobRunner? runner;
        lock (_sync) runner = _runner;
        if (runner is null) return null;
        var snapshot = runner.Snapshot();
        if (!SamePath(snapshot.Project, projectPath)) return null;
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(TodoRow r)
        {
            remaining[r.Id] = r.Needed ? r.Remaining : 0;
            foreach (var c in r.Children) Add(c);
        }
        foreach (var r in runner.PlanAtStart.Rows) Add(r);
        return new RunStart(snapshot.RunId, remaining);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Waits for the running job (tests and shutdown).</summary>
    public Task WaitAsync()
    {
        lock (_sync) return _task ?? Task.CompletedTask;
    }

    JobRunner? Running()
    {
        lock (_sync) return _task is { IsCompleted: false } ? _runner : null;
    }

    void SchedulePublish()
    {
        if (Interlocked.Exchange(ref _publishPending, 1) != 0) return;
        _ = Task.Delay(PublishEvery).ContinueWith(_ =>
        {
            Volatile.Write(ref _publishPending, 0);
            Publish();
        }, TaskScheduler.Default);
    }

    void Publish()
    {
        var snapshot = Current;
        if (snapshot is not null) _hub.Publish("job", snapshot);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var runner = Running();
        if (runner is null) return;
        _logger.LogInformation("The app is closing: stopping run {Run} now", runner.RunId);
        runner.RequestStop(StopMode.Now);
        await Task.WhenAny(WaitAsync(), Task.Delay(ShutdownWait, cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>SSE <c>jobLog</c>: one line of a run's log.</summary>
public sealed record JobLogLine(string RunId, DateTime AtUtc, string Line);

/// <summary>The units left per to-do row (and part) when a run started.</summary>
public sealed record RunStart(string RunId, IReadOnlyDictionary<string, int> Remaining);

/// <summary>SSE <c>gameUse</c>: what uses the game by hand (<c>precheck</c>, <c>resourceStart</c>), or null.</summary>
public sealed record GameUseDto(string? Kind);
