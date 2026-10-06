namespace FxMapGenerator.Core.Jobs;

public enum JobState { Running, Stopping, Stopped, Done, Failed }

public enum StopMode { None, Boundary, Now }

public enum StageState { Waiting, Running, Done, Stopped, Failed }

/// <summary>
/// The whole picture of a run for the screen (SSE <c>job</c>) and for <c>run.json</c>. Times are UTC; durations and
/// estimates in seconds.
/// </summary>
/// <param name="Running">Worker slots in use now.</param>
/// <param name="Retiring">Workers above the (lowered) limit that stop once their unit is done.</param>
/// <param name="MemoryLimitedAt">Workers in use when free memory held back another one; null when memory is not the limit.</param>
/// <param name="Locks">Project inputs read by stages not over yet, with those stages' rows.</param>
/// <param name="StopReason">Why a stage stopped the run (the game is gone, another player came on); null otherwise.</param>
public sealed record JobSnapshot(
    string RunId,
    string RunFolder,
    string Project,
    string ProjectName,
    JobState State,
    StopMode StopMode,
    int Workers,
    int Processors,
    int Running,
    int Retiring,
    int? MemoryLimitedAt,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    double Elapsed,
    string? Stage,
    IReadOnlyList<StageProgress> Stages,
    IReadOnlyList<ActiveUnit> Active,
    int FailureCount,
    IReadOnlyList<UnitFailure> Failures,
    double RemainingLow,
    double RemainingHigh,
    double Cpu,
    long MemoryAvailable,
    long MemoryTotal,
    long ProcessMemory,
    IReadOnlyList<InputLock> Locks,
    IReadOnlyList<string> Notes,
    string? Error,
    string? StopReason,
    IReadOnlyList<Announcement> Announcements);

/// <summary>
/// Something a stage tells the person watching, apart from its progress: <c>gameDone</c> = the work in the game is over
/// (values: <c>resource</c>, <c>stopped</c> 1 / 0, <c>restarted</c> = the resources started again, comma-separated).
/// </summary>
public sealed record Announcement(string Key, IReadOnlyDictionary<string, string> Values, DateTime AtUtc);

/// <param name="Preparing">The stage is finding its units (and measuring what it needs first, like the scale correction).</param>
/// <param name="Total">Units the stage found left when it started (0 while waiting).</param>
/// <param name="Interrupted">Units cut off by "stop now" (not recorded; they run again next time).</param>
public sealed record StageProgress(
    string Id,
    string Row,
    string Unit,
    StageState State,
    bool UsesGame,
    bool Preparing,
    int Total,
    int Done,
    int Failed,
    int Interrupted,
    DateTime? StartedUtc,
    DateTime? EndedUtc,
    double RemainingLow,
    double RemainingHigh,
    string? Error);

/// <summary>A unit being worked on: which step it is at (<see cref="Phase"/>, <see cref="Fraction"/> 0..1).</summary>
public sealed record ActiveUnit(string Stage, string Unit, string? Phase, double Fraction, DateTime StartedUtc, double Seconds);

public sealed record UnitFailure(string Stage, string Unit, string Message, DateTime AtUtc);

public sealed record InputLock(string Key, IReadOnlyList<string> Stages);

/// <summary>A change of the number of workers during the run (the first entry is the start).</summary>
public sealed record WorkersChange(DateTime AtUtc, int Workers);

/// <summary>A stage's units for the map: those it found left, and which are done, failed or interrupted so far.</summary>
public sealed record StageUnits(
    string Id,
    string Row,
    string Unit,
    StageState State,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Done,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Interrupted);
