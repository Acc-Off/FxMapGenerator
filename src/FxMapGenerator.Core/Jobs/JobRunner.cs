using System.Collections.Concurrent;
using System.Diagnostics;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;

namespace FxMapGenerator.Core.Jobs;

/// <summary>The stages a run goes through for a project, and notes on what it leaves out.</summary>
public sealed record BuildPlan(IReadOnlyList<Stage> Stages, IReadOnlyList<string> Notes);

public sealed class JobSetup
{
    public required string ProjectPath { get; init; }
    /// <summary>The stages for the project as copied at the start of the run, and the work folder's state.</summary>
    public required Func<Project, StateStore, BuildPlan> Stages { get; init; }
    /// <summary>Workers of the stages without the game; null: the project's default. Limited to <see cref="Processors"/>.</summary>
    public int? Workers { get; init; }
    public int Processors { get; init; } = Environment.ProcessorCount;
    public IMemoryProbe Memory { get; init; } = SystemMemory.Instance;
    /// <summary>The work folder's state when someone else holds it open; null: read here.</summary>
    public StateStore? State { get; init; }
    /// <summary>Written into run.json.</summary>
    public string Version { get; init; } = "";
    /// <summary>Also receives every log line (the console).</summary>
    public Action<string>? Log { get; init; }
}

public sealed class JobException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Runs the stages of one build in order. Units go to workers as the <see cref="WorkerGate"/> allows; the level can
/// change while running. Two ways to stop: at a boundary (running units finish, no new ones start) and now (running
/// units are cancelled and not recorded; the stage's clean-up still runs). What was recorded stays, so the next run
/// continues from there. Each run keeps <c>logs/run-&lt;time&gt;/</c> (see <see cref="RunFolder"/>).
/// </summary>
public sealed class JobRunner
{
    const int RecordBatch = 50;
    static readonly TimeSpan RecordEvery = TimeSpan.FromSeconds(2);
    const int SnapshotFailures = 200;

    readonly object _sync = new();
    readonly Project _project;
    readonly string _projectPath;
    readonly WorkFolder _folder;
    readonly StateStore _state;
    readonly RunFolder _run;
    readonly WorkFolderLock _lock;
    readonly IReadOnlyList<Stage> _stages;
    readonly StageInfo[] _info;
    readonly IReadOnlyList<string> _notes;
    readonly WorkerGate _gate;
    readonly IMemoryProbe _memory;
    readonly ProcessLoad _load = new();
    readonly int _processors;
    readonly TodoTable _plan;
    readonly string _version;
    readonly Action<string>? _logSink;
    readonly CancellationTokenSource _now = new();
    readonly CancellationTokenSource _dispatch = new();
    readonly ConcurrentDictionary<long, ActiveEntry> _active = new();
    readonly List<UnitFailure> _failures = new();
    readonly List<Announcement> _announcements = new();
    readonly List<WorkersChange> _levels = new();
    readonly List<string> _pendingRecords = new();
    readonly Stopwatch _sinceRecord = Stopwatch.StartNew();
    long _activeSeq;
    int _workers;
    StopMode _stopMode;
    JobState _jobState = JobState.Running;
    int _current = -1;
    DateTime _startedUtc = DateTime.UtcNow;
    DateTime? _endedUtc;
    string? _error;
    string? _stopReason;

    JobRunner(Project project, string projectPath, WorkFolder folder, StateStore state, RunFolder run, WorkFolderLock workLock, BuildPlan build,
        int workers, int processors, IMemoryProbe memory, TodoTable plan, string version, Action<string>? logSink)
    {
        _project = project;
        _projectPath = projectPath;
        _folder = folder;
        _state = state;
        _run = run;
        _lock = workLock;
        _stages = build.Stages;
        _notes = build.Notes;
        _workers = workers;
        _processors = processors;
        _memory = memory;
        _plan = plan;
        _version = version;
        _logSink = logSink;
        _gate = new WorkerGate(workers, memory);
        _info = _stages.Select(s => new StageInfo(s)).ToArray();
        foreach (var i in _info) i.Plan = PlanOf(plan, i.Stage.Row);
        _levels.Add(new WorkersChange(_startedUtc, workers));
    }

    /// <summary>Something changed (a unit started or ended, a step, the level, the state). Called on worker threads; keep it short.</summary>
    public event Action? Changed;

    /// <summary>A log line was written (also into run.log).</summary>
    public event Action<string>? Logged;

    public string RunId => _run.Id;
    public string RunPath => _run.Path;
    public string WorkFolderPath => _folder.Root;
    public StateStore State => _state;
    public TodoTable PlanAtStart => _plan;

    /// <summary>
    /// Takes the work folder, creates the run folder, copies the project file into its <c>inputs/</c> and reads the
    /// project from that copy; the road edits file, the project's own styles the maps use and its points of interest too,
    /// which the run then reads from its copies.
    /// Throws <see cref="ProjectException"/> for a bad project and <see cref="JobException"/> (<c>BUSY</c>) when another
    /// run uses the work folder.
    /// </summary>
    public static JobRunner Create(JobSetup setup)
    {
        var original = Project.Load(setup.ProjectPath);
        var folder = new WorkFolder(original.WorkFolderPath);
        var workLock = WorkFolderLock.TryAcquire(folder, out var holder)
            ?? throw new JobException("BUSY", $"another run is using the work folder {folder.Root} ({holder})");
        RunFolder? run = null;
        try
        {
            run = RunFolder.Create(folder, DateTime.Now);
            var copy = run.CopyInput(original.FilePath);
            var project = Project.LoadCopy(copy, original.FilePath);
            if (project.RoadEditsPath is { } edits && File.Exists(edits)) project.UseRoadEditsCopy(run.CopyInput(edits));
            CopyStyles(project, run);
            CopyPoi(project, run);
            var state = setup.State ?? StateStore.Open(folder);
            int workers = Math.Clamp(setup.Workers ?? project.File.Parallel, 1, Math.Max(1, setup.Processors));
            var build = setup.Stages(project, state);
            var plan = Planner.Build(project, state, workers, setup.Processors);
            return new JobRunner(project, original.FilePath, folder, state, run, workLock, build, workers, setup.Processors, setup.Memory, plan, setup.Version, setup.Log);
        }
        catch
        {
            workLock.Dispose();
            if (run is not null)
            {
                run.Dispose();
                try { Directory.Delete(run.Path, recursive: true); } catch (IOException) { }
            }
            throw;
        }
    }

    /// <summary>
    /// Copies the project's own styles its maps use into <c>inputs/styles/</c> (a missing one is left for the maps to
    /// name), which the run then reads instead of the project's folder.
    /// </summary>
    static void CopyStyles(Project project, RunFolder run)
    {
        if (project.StylesFolder is not { } folder) return;
        var copies = Path.Combine(run.Inputs, Styles.ProjectStyles.FolderName);
        Directory.CreateDirectory(copies);
        foreach (var id in project.Maps.Where(m => m.Kind == MapKind.Atlas).Select(m => m.Preset!).Distinct().Where(id => !Styles.MapStyle.BuiltIn.Contains(id)))
            if (Styles.ProjectStyles.PathOf(folder, id) is var path && File.Exists(path)) File.Copy(path, Styles.ProjectStyles.PathOf(copies, id));
        project.UseStylesCopy(copies);
    }

    /// <summary>
    /// Copies the project's POI folder (its <c>.json</c> files) into <c>inputs/poi/</c>, and its POI styles file into
    /// <c>inputs/poi-styles.json</c> with the PNG icons its styles name at the same paths beside it; the run then reads
    /// those. A missing folder or file is left for the steps to name.
    /// </summary>
    static void CopyPoi(Project project, RunFolder run)
    {
        string? folder = null, styles = null;
        if (project.PoiFolderPath is { } source && Directory.Exists(source))
        {
            folder = Path.Combine(run.Inputs, "poi");
            Directory.CreateDirectory(folder);
            foreach (var (path, _) in Poi.PoiData.ReadFolder(source))
            {
                var target = Path.Combine(folder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(source, path), target);
            }
        }
        if (project.PoiStylesPath is { } file && File.Exists(file))
        {
            styles = Path.Combine(run.Inputs, "poi-styles.json");
            File.Copy(file, styles);
            foreach (var image in Poi.PoiFiles.ImagesOf(File.ReadAllText(file)))
            {
                var from = Path.Combine(Path.GetDirectoryName(file)!, image);
                var to = Path.Combine(run.Inputs, image);
                if (!File.Exists(from) || File.Exists(to)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
            }
        }
        project.UsePoiCopy(folder, styles);
    }

    // ------------------------------------------------------------------ control

    /// <summary>Boundary: running units finish, nothing new starts. Now: running units are cancelled as well. Now may follow boundary.</summary>
    public void RequestStop(StopMode mode)
    {
        if (mode == StopMode.None) return;
        lock (_sync)
        {
            if (_jobState is not (JobState.Running or JobState.Stopping)) return;
            if (_stopMode == StopMode.Now || _stopMode == mode) return;
            _stopMode = mode;
            _jobState = JobState.Stopping;
        }
        Log(mode == StopMode.Now ? "stop now requested" : "stop requested: after the running units");
        _dispatch.Cancel();
        if (mode == StopMode.Now) _now.Cancel();
        OnChanged();
    }

    /// <summary>A stage met a condition no unit can get past: stop at the boundary, saying why.</summary>
    void StopFromStage(string reason)
    {
        lock (_sync) _stopReason ??= reason;
        Log("stopping: " + reason);
        RequestStop(StopMode.Boundary);
    }

    /// <summary>Changes the number of workers (limited to the processors); stages that have not started get new estimates.</summary>
    public void SetWorkers(int workers)
    {
        workers = Math.Clamp(workers, 1, Math.Max(1, _processors));
        lock (_sync)
        {
            if (_workers == workers) return;
            _workers = workers;
            _levels.Add(new WorkersChange(DateTime.UtcNow, workers));
        }
        ApplyLimit();
        Replan(workers);
        Log($"workers {workers} of {_processors}");
        OnChanged();
    }

    /// <summary>New estimates for the stages still waiting, from the state as it is now (a stage may have changed what is left after it).</summary>
    void Replan(int workers)
    {
        try
        {
            var plan = Planner.Build(_project, _state, workers, _processors);
            lock (_sync)
                foreach (var i in _info.Where(i => i.State == StageState.Waiting)) i.Plan = PlanOf(plan, i.Stage.Row);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { }
    }

    /// <summary>
    /// The gate's limit: the level; for a stage in the game, its one unit and the helpers it may have beside it (the
    /// units themselves still go one at a time, see <see cref="RunStage"/>).
    /// </summary>
    void ApplyLimit()
    {
        int cur = Volatile.Read(ref _current);
        var game = cur >= 0 && cur < _stages.Count && _stages[cur].UsesGame ? _stages[cur] : null;
        _gate.SetLimit(game is null ? _workers : Math.Min(_workers, 1 + game.Helpers));
    }

    // ------------------------------------------------------------------ run

    public async Task<JobSnapshot> RunAsync()
    {
        _startedUtc = DateTime.UtcNow;
        Log($"run {_run.Id}: {_project.File.Name} ({_projectPath}), maps {string.Join(", ", _project.Maps.Select(m => m.Id))}, " +
            $"{_workers} workers of {_processors} processors");
        Log($"work folder {_folder.Root}");
        foreach (var n in _notes) Log(n);
        WriteRecord();
        try
        {
            for (int i = 0; i < _stages.Count; i++)
            {
                if (_stopMode != StopMode.None) break;
                await RunStage(i).ConfigureAwait(false);
                if (_info[i].State == StageState.Failed) break;
            }
        }
        finally
        {
            lock (_sync)
            {
                _endedUtc = DateTime.UtcNow;
                _jobState = _error is not null ? JobState.Failed
                    : _info.Any(i => i.State is StageState.Stopped or StageState.Waiting) ? JobState.Stopped
                    : JobState.Done;
                _current = -1;
            }
            var s = Snapshot();
            Log($"run {_jobState.ToString().ToLowerInvariant()} after {s.Elapsed:0.0} s: " +
                string.Join(", ", _info.Select(i => $"{i.Stage.Id} {i.Done}/{i.Total}{(i.Failed > 0 ? $" ({i.Failed} failed)" : "")}")));
            WriteRecord();
            _run.Dispose();
            _lock.Dispose();
            OnChanged();
        }
        return Snapshot();
    }

    async Task RunStage(int index)
    {
        var stage = _stages[index];
        var info = _info[index];
        bool takeRoadEdits;
        lock (_sync)
        {
            takeRoadEdits = stage.Reads.Contains(InputKeys.RoadEdits) && !Started(InputKeys.RoadEdits);
            info.State = StageState.Running;
            info.StartedUtc = DateTime.UtcNow;
            info.Clock.Restart();
            _current = index;
        }
        if (takeRoadEdits) TakeRoadEdits();
        _gate.SetUnitMemory(stage.MemoryPerUnit);
        ApplyLimit();
        var ctx = new StageContext(_project, _folder, _state, new GateParallel(_gate, _now.Token), Log, () => _gate.Limit, _run.Path, StopFromStage, RunBesideAsync, Announce);
        OnChanged();
        try
        {
            IReadOnlyList<string> units;
            lock (_sync) info.Preparing = true;
            OnChanged();
            using (await _gate.AcquireAsync(_now.Token).ConfigureAwait(false))
                units = await Task.Run(() => stage.Prepare(ctx)).ConfigureAwait(false);
            lock (_sync)
            {
                info.Preparing = false;
                info.Total = units.Count;
                info.Units = units;
            }
            Replan(_workers);
            Log($"{stage.Id}: {units.Count} {stage.UnitName}{(units.Count == 1 ? "" : "s")} left");
            OnChanged();

            var running = new List<Task>();
            foreach (var unit in units)
            {
                if (_stopMode != StopMode.None) break;
                WorkerGate.Slot slot;
                try { slot = await _gate.AcquireAsync(_dispatch.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (_stopMode != StopMode.None) { slot.Dispose(); break; }
                var run = Task.Run(() => RunUnit(stage, info, ctx, unit, slot));
                // the game takes one block at a time, whatever slots its helpers leave free
                if (stage.UsesGame) await run.ConfigureAwait(false);
                else running.Add(run);
            }
            await Task.WhenAll(running).ConfigureAwait(false);
            FlushRecords(stage);

            if (_now.IsCancellationRequested)
            {
                lock (_sync) info.State = StageState.Stopped;
            }
            else
            {
                StageEnd end;
                lock (_sync) end = new StageEnd(info.Total, info.Done, info.Failed, _stopMode == StopMode.Boundary && info.Done + info.Failed < info.Total);
                using (await _gate.AcquireAsync(_now.Token).ConfigureAwait(false))
                    await Task.Run(() => stage.Finish(ctx, end)).ConfigureAwait(false);
                lock (_sync) info.State = end.StoppedAtBoundary ? StageState.Stopped : StageState.Done;
            }
        }
        catch (OperationCanceledException) when (_now.IsCancellationRequested)
        {
            lock (_sync) info.State = StageState.Stopped;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                info.State = StageState.Failed;
                info.Error = ex.Message;
                _error = $"{stage.Id}: {ex.Message}";
            }
            Log($"{stage.Id} failed: {ex}");
        }
        finally
        {
            FlushRecords(stage);
            Replan(_workers);
            try { stage.Cleanup(new StageContext(_project, _folder, _state, new FixedParallel(1), Log, () => 1, _run.Path, StopFromStage, announce: Announce)); }
            catch (Exception ex) { Log($"{stage.Id} clean-up failed: {ex.Message}"); }
            lock (_sync) info.EndedUtc = DateTime.UtcNow;
            Log($"{stage.Id}: {info.State.ToString().ToLowerInvariant()}, {info.Done} of {info.Total} done" +
                (info.Failed > 0 ? $", {info.Failed} failed" : "") + (info.Interrupted > 0 ? $", {info.Interrupted} interrupted" : "") +
                $" ({info.Clock.Elapsed.TotalSeconds:0.0} s)");
            WriteRecord();
            OnChanged();
        }
    }

    void RunUnit(Stage stage, StageInfo info, StageContext ctx, string unit, WorkerGate.Slot slot)
    {
        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        int workers = _gate.Limit;
        long key = Interlocked.Increment(ref _activeSeq);
        var entry = new ActiveEntry(stage.Id, unit, started);
        _active[key] = entry;
        OnChanged();
        string result;
        double seconds = 0;
        try
        {
            var uctx = new UnitContext(ctx, unit, new GateParallel(_gate, _now.Token), (phase, fraction) =>
            {
                entry.Phase = phase;
                entry.Fraction = fraction;
                OnChanged();
            });
            stage.Run(uctx);
            lock (_sync)
            {
                info.Done++;
                info.DoneSeconds += sw.Elapsed.TotalSeconds;
                info.DoneUnits.Add(unit);
            }
            if (stage.RecordKey is not null) Record(stage, unit);
            result = "done";
            OnChanged();          // before the slot is given back: a stop asked for now comes before the next unit
        }
        catch (OperationCanceledException) when (_now.IsCancellationRequested)
        {
            lock (_sync)
            {
                info.Interrupted++;
                info.InterruptedUnits.Add(unit);
            }
            result = "interrupted";
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                info.Failed++;
                info.FailedUnits.Add(unit);
                _failures.Add(new UnitFailure(stage.Id, unit, ex.Message, DateTime.UtcNow));
            }
            Log($"{stage.Id} {unit} failed: {ex.Message}");
            result = "failed";
        }
        finally
        {
            seconds = sw.Elapsed.TotalSeconds;       // measured while the slot is still held
            _active.TryRemove(key, out _);
            slot.Dispose();
        }
        _run.Unit(stage.Id, unit, started, seconds, result, workers);
        OnChanged();
    }

    /// <summary>
    /// Work beside the running stage's units (<see cref="StageContext.RunBesideAsync"/>) on a slot of its own: listed
    /// with the units being processed and written to units.csv under <paramref name="stageId"/>. Not while the level is
    /// one worker: the game's unit has it.
    /// </summary>
    async Task<bool> RunBesideAsync(string stageId, string unit, BesideWork work, CancellationToken wait)
    {
        if (Volatile.Read(ref _workers) < 2) return false;
        WorkerGate.Slot slot;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(_now.Token, wait))
            slot = await _gate.AcquireAsync(waiting.Token).ConfigureAwait(false);
        using var held = slot;
        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        int workers = _gate.Limit;
        long key = Interlocked.Increment(ref _activeSeq);
        var entry = new ActiveEntry(stageId, unit, started);
        _active[key] = entry;
        OnChanged();
        string result = "failed";
        try
        {
            await Task.Run(() => work((phase, fraction) =>
            {
                entry.Phase = phase;
                entry.Fraction = Math.Clamp(fraction, 0, 1);
                OnChanged();
            }, _now.Token)).ConfigureAwait(false);
            result = "done";
            return true;
        }
        catch (OperationCanceledException) when (_now.IsCancellationRequested)
        {
            result = "interrupted";
            throw;
        }
        finally
        {
            double seconds = sw.Elapsed.TotalSeconds;
            _active.TryRemove(key, out _);
            _run.Unit(stageId, unit, started, seconds, result, workers);
            OnChanged();
        }
    }

    void Record(Stage stage, string unit)
    {
        List<string>? flush = null;
        lock (_pendingRecords)
        {
            _pendingRecords.Add(unit);
            if (_pendingRecords.Count >= RecordBatch || _sinceRecord.Elapsed >= RecordEvery)
            {
                flush = new List<string>(_pendingRecords);
                _pendingRecords.Clear();
                _sinceRecord.Restart();
            }
        }
        if (flush is not null) _state.SetStageDone(stage.RecordKey!, flush, DateTime.UtcNow);
    }

    void FlushRecords(Stage stage)
    {
        List<string> flush;
        lock (_pendingRecords)
        {
            flush = new List<string>(_pendingRecords);
            _pendingRecords.Clear();
            _sinceRecord.Restart();
        }
        if (flush.Count > 0 && stage.RecordKey is not null) _state.SetStageDone(stage.RecordKey, flush, DateTime.UtcNow);
    }

    // ------------------------------------------------------------------ reporting

    void Log(string line)
    {
        _run.Log(line);
        _logSink?.Invoke(line);
        Logged?.Invoke(line);
    }

    void OnChanged() => Changed?.Invoke();

    void Announce(string key, IReadOnlyDictionary<string, string> values)
    {
        lock (_sync) _announcements.Add(new Announcement(key, values, DateTime.UtcNow));
        Log($"{key}: " + string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value}")));
        OnChanged();
    }

    public JobSnapshot Snapshot()
    {
        var memory = _memory.Read();
        double cpu = _load.Cpu();
        long working = _load.WorkingSet();
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            int workers = _gate.Limit;
            var stages = _info.Select(i => i.ToProgress(i.State == StageState.Running ? EffectiveWorkers(i) : workers)).ToList();
            var active = _active.Values.OrderBy(a => a.StartedUtc)
                .Select(a => new ActiveUnit(a.Stage, a.Unit, a.Phase, a.Fraction, a.StartedUtc, (now - a.StartedUtc).TotalSeconds)).ToList();
            var locks = _info.Where(i => i.State is StageState.Waiting or StageState.Running)
                .SelectMany(i => i.Stage.Reads.Where(Held).Select(k => (Key: k, i.Stage.Row)))
                .GroupBy(x => x.Key)
                .Select(g => new InputLock(g.Key, g.Select(x => x.Row).Distinct().ToList()))
                .OrderBy(l => l.Key, StringComparer.Ordinal).ToList();
            var ended = _endedUtc;
            int cur = _current;
            return new JobSnapshot(_run.Id, _run.Path, _projectPath, _project.File.Name, _jobState, _stopMode, workers, _processors,
                _gate.Running, _gate.Retiring, _gate.MemoryLimitedAt, _startedUtc, ended, ((ended ?? now) - _startedUtc).TotalSeconds,
                cur >= 0 ? _stages[cur].Id : null, stages, active, _failures.Count, _failures.TakeLast(SnapshotFailures).ToList(),
                stages.Sum(s => s.RemainingLow), stages.Sum(s => s.RemainingHigh), cpu, memory.Available, memory.Total, working, locks, _notes, _error, _stopReason,
                _announcements.ToList());
        }
    }

    /// <summary>Per stage: the units it found left, and which of them are done, failed or were interrupted so far.</summary>
    public IReadOnlyList<StageUnits> Units()
    {
        lock (_sync)
            return _info.Select(i => new StageUnits(i.Stage.Id, i.Stage.Row, i.Stage.UnitName, i.State, i.Units.ToList(), i.DoneUnits.ToList(),
                i.FailedUnits.ToList(), i.InterruptedUnits.ToList())).ToList();
    }

    int EffectiveWorkers(StageInfo i) => i.Stage.UsesGame ? 1 : Math.Max(1, _gate.MemoryLimitedAt ?? _gate.Limit);

    /// <summary>The reason an input cannot be edited now (the stages still reading it), or null when it can.</summary>
    public IReadOnlyList<string>? LockedBy(string inputKey)
    {
        lock (_sync)
        {
            var rows = _info.Where(i => i.State is StageState.Waiting or StageState.Running)
                .Where(i => i.Stage.Reads.Any(k => InputKeys.Overlap(k, inputKey) && Held(k))).Select(i => i.Stage.Row).Distinct().ToList();
            return rows.Count > 0 ? rows : null;
        }
    }

    /// <summary>
    /// Whether the stages still to read an input hold it now (call under the lock). The run takes its inputs at its start,
    /// so they are held from then on; the road edits it takes when the first stage reading them starts
    /// (<see cref="TakeRoadEdits"/>), so they can be saved while the stages before it run (the capture above all) and are
    /// held from that stage's start to the end of the last one reading them.
    /// </summary>
    bool Held(string key) => key != InputKeys.RoadEdits || Started(key);

    /// <summary>Whether a stage reading the input has started (call under the lock).</summary>
    bool Started(string key) => _info.Any(i => i.State != StageState.Waiting && i.Stage.Reads.Contains(key));

    /// <summary>
    /// The run's copy of the road edits, taken again as the first stage that reads them starts: the edits saved while the
    /// earlier stages ran are the ones this run builds with. When the file cannot be read now, the copy of the run's start
    /// stays.
    /// </summary>
    void TakeRoadEdits()
    {
        try
        {
            if (Project.Load(_projectPath).RoadEditsPath is not { } edits || !File.Exists(edits)) return;
            _project.UseRoadEditsCopy(_run.CopyInput(edits, again: true));
            Log("road edits: taken as they are saved now (the first stage reading them starts)");
        }
        catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException)
        {
            Log($"road edits: not taken again ({ex.Message}); the run reads its copy of the start");
        }
    }

    void WriteRecord()
    {
        var snapshot = Snapshot();
        List<UnitFailure> failures;
        List<WorkersChange> levels;
        lock (_sync)
        {
            failures = _failures.ToList();
            levels = _levels.ToList();
        }
        try
        {
            _run.WriteRecord(new
            {
                version = _version,
                snapshot,
                workerChanges = levels,
                failures,
                peakProcessMemory = _load.PeakWorkingSet(),
                planAtStart = _plan,
            });
        }
        catch (IOException ex) { _logSink?.Invoke($"could not write run.json: {ex.Message}"); }
    }

    static (double Low, double High) PlanOf(TodoTable plan, string row)
    {
        var r = plan.Rows.SelectMany(Flatten).FirstOrDefault(x => x.Id == row);
        return r is null || !r.Needed ? (0, 0) : (r.Low, r.High);
        static IEnumerable<TodoRow> Flatten(TodoRow x) => x.Children.SelectMany(Flatten).Prepend(x);
    }

    sealed class ActiveEntry(string stage, string unit, DateTime startedUtc)
    {
        public string Stage { get; } = stage;
        public string Unit { get; } = unit;
        public DateTime StartedUtc { get; } = startedUtc;
        public volatile string? Phase;
        public double Fraction;
    }

    sealed class StageInfo(Stage stage)
    {
        public Stage Stage { get; } = stage;
        public StageState State = StageState.Waiting;
        public int Total, Done, Failed, Interrupted;
        public bool Preparing;
        public double DoneSeconds;
        public DateTime? StartedUtc, EndedUtc;
        public string? Error;
        public (double Low, double High) Plan;
        public readonly Stopwatch Clock = new();
        public IReadOnlyList<string> Units = Array.Empty<string>();
        public readonly List<string> DoneUnits = new(), FailedUnits = new(), InterruptedUnits = new();

        /// <summary>
        /// Time left: the plan while waiting; while running, the measured mean unit time over the workers once a unit is
        /// done (else the plan less the time spent); nothing once over.
        /// </summary>
        public StageProgress ToProgress(int workers)
        {
            double low = 0, high = 0;
            if (State == StageState.Waiting) (low, high) = Plan;
            else if (State == StageState.Running)
            {
                int left = Math.Max(0, Total - Done - Failed);
                if (Done > 0 && Total > 1)
                {
                    // equal units on the workers: whole rounds of the mean unit time
                    double mean = DoneSeconds / Done;
                    low = high = mean * Math.Ceiling(left / (double)Math.Max(1, Math.Min(workers, Math.Max(left, 1))));
                }
                else
                {
                    double spent = Clock.Elapsed.TotalSeconds;
                    low = Math.Max(0, Plan.Low - spent);
                    high = Math.Max(0, Plan.High - spent);
                }
            }
            string unit = Stage.UnitName;
            return new StageProgress(Stage.Id, Stage.Row, unit, State, Stage.UsesGame, Preparing && State == StageState.Running, Total, Done, Failed, Interrupted, StartedUtc, EndedUtc, low, high, Error);
        }
    }
}
