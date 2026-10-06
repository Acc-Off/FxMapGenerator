using FxMapGenerator.Core.Jobs;

namespace FxMapGenerator.Core.Tests.Jobs;

/// <summary>
/// A stage of numbered units (<c>u00</c>, <c>u01</c>, ...) recorded under <c>fake-&lt;id&gt;</c>; the units left are
/// those without a record. The body decides what a unit does; the stage counts how many run at once.
/// </summary>
internal sealed class FakeStage(string id, int units, string row = "ortho", Action<UnitContext>? body = null) : Stage
{
    readonly object _sync = new();
    int _concurrent;

    public override string Id => id;
    public override string Row => row;
    public override string? RecordKey => "fake-" + id;
    public long Memory { get; init; } = 0;
    public override long MemoryPerUnit => Memory;

    public int Peak { get; private set; }
    public int Concurrent { get { lock (_sync) return _concurrent; } }
    public List<string> Ran { get; } = new();
    /// <summary>For each unit started: how many ran at that moment, itself included.</summary>
    public List<int> AtStart { get; } = new();
    public int RanCount { get { lock (_sync) return Ran.Count; } }
    public List<int> AtStartFrom(int index) { lock (_sync) return AtStart.Skip(index).ToList(); }
    public int CleanupCalls { get; private set; }
    public StageEnd? Ended { get; private set; }

    public static string Name(int i) => $"u{i:00}";

    public override IReadOnlyList<string> Prepare(StageContext ctx) =>
        Enumerable.Range(0, units).Select(Name).Where(u => ctx.State.StageDone(RecordKey!, u) is null).ToList();

    public override int CountReady(FxMapGenerator.Core.Projects.Project project, FxMapGenerator.Core.State.StateStore state) =>
        Enumerable.Range(0, units).Select(Name).Count(u => state.StageDone(RecordKey!, u) is null);

    public override void Run(UnitContext ctx)
    {
        lock (_sync)
        {
            _concurrent++;
            Peak = Math.Max(Peak, _concurrent);
            AtStart.Add(_concurrent);
            Ran.Add(ctx.Unit);
        }
        try { (body ?? (_ => Thread.Sleep(10)))(ctx); }
        finally { lock (_sync) _concurrent--; }
    }

    public override void Finish(StageContext ctx, StageEnd end) => Ended = end;

    public override void Cleanup(StageContext ctx) => CleanupCalls++;
}

/// <summary>Memory that never runs out, or a fixed amount.</summary>
internal sealed class FakeMemory(long available = long.MaxValue / 4) : IMemoryProbe
{
    public long Available { get; set; } = available;
    public MemoryStatus Read() => new(Available, Math.Max(Available, 64L << 30));
}
