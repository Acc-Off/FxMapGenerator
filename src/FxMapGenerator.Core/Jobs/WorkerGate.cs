using System.Diagnostics;

namespace FxMapGenerator.Core.Jobs;

/// <summary>
/// Hands out worker slots for the stages that run without the game. The limit can change at any time: raising it lets
/// waiting work start at once; lowering it never interrupts running work, new work simply waits until fewer slots are
/// taken. Beyond the first slot, a slot is only given when the free memory covers one more unit of the current stage
/// (units started in the last few seconds count as not yet grown to their size) plus a reserve for the rest of the PC.
/// </summary>
public sealed class WorkerGate
{
    /// <summary>Memory left to the rest of the PC.</summary>
    public const long Reserve = 1L << 30;
    static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(3);
    static readonly TimeSpan MemoryPoll = TimeSpan.FromMilliseconds(500);

    readonly object _sync = new();
    readonly IMemoryProbe _memory;
    readonly List<long> _starts = new();
    int _limit;
    long _unitBytes;
    int? _memoryLimitedAt;
    TaskCompletionSource _changed = NewSignal();

    public WorkerGate(int limit, IMemoryProbe memory)
    {
        _limit = Math.Max(1, limit);
        _memory = memory;
    }

    public int Limit { get { lock (_sync) return _limit; } }

    public int Running { get { lock (_sync) return _starts.Count; } }

    /// <summary>Slots in use above the limit: work that finishes and is not replaced.</summary>
    public int Retiring { get { lock (_sync) return Math.Max(0, _starts.Count - _limit); } }

    /// <summary>The number of slots in use when free memory last held back another one; null when it does not.</summary>
    public int? MemoryLimitedAt { get { lock (_sync) return _memoryLimitedAt; } }

    public void SetLimit(int limit)
    {
        lock (_sync)
        {
            _limit = Math.Max(1, limit);
            Pulse();
        }
    }

    /// <summary>Memory one unit of the stage about to run needs at most (bytes).</summary>
    public void SetUnitMemory(long bytes)
    {
        lock (_sync)
        {
            _unitBytes = Math.Max(0, bytes);
            _memoryLimitedAt = null;
            Pulse();
        }
    }

    /// <summary>Waits for a free slot. Dispose the slot to give it back.</summary>
    public async Task<Slot> AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (CanStart())
                {
                    var t = Stopwatch.GetTimestamp();
                    _starts.Add(t);
                    return new Slot(this, t);
                }
                changed = _changed.Task;
            }
            // memory frees up without anyone telling us: look again now and then
            await Task.WhenAny(changed, Task.Delay(MemoryPoll, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    bool CanStart()
    {
        if (_starts.Count >= _limit) return false;
        if (_starts.Count == 0 || _unitBytes == 0) { _memoryLimitedAt = null; return true; }
        var now = Stopwatch.GetTimestamp();
        int warming = _starts.Count(s => Stopwatch.GetElapsedTime(s, now) < WarmUp);
        long need = _unitBytes * (1 + warming) + Reserve;
        if (_memory.Read().Available < need)
        {
            _memoryLimitedAt = _starts.Count;
            return false;
        }
        _memoryLimitedAt = null;
        return true;
    }

    void Release(long start)
    {
        lock (_sync)
        {
            _starts.Remove(start);
            Pulse();
        }
    }

    /// <summary>Gives the slot back if more slots are taken than the limit allows (a lowered level).</summary>
    bool ReleaseIfOverLimit(long start)
    {
        lock (_sync)
        {
            if (_starts.Count <= _limit) return false;
            _starts.Remove(start);
            Pulse();
            return true;
        }
    }

    void Pulse()
    {
        var old = _changed;
        _changed = NewSignal();
        old.TrySetResult();
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class Slot : IDisposable
    {
        readonly WorkerGate _gate;
        readonly long _start;
        int _released;

        internal Slot(WorkerGate gate, long start)
        {
            _gate = gate;
            _start = start;
        }

        /// <summary>True (and the slot is given back) when the gate is over its limit.</summary>
        public bool RetireIfOverLimit()
        {
            if (Volatile.Read(ref _released) != 0 || !_gate.ReleaseIfOverLimit(_start)) return false;
            Volatile.Write(ref _released, 1);
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _gate.Release(_start);
        }
    }
}
