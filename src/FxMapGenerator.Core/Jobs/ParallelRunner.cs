using System.Runtime.ExceptionServices;

namespace FxMapGenerator.Core.Jobs;

/// <summary>Runs the items of a loop side by side; how many at once is up to the implementation.</summary>
public interface IParallelRunner
{
    CancellationToken Token { get; }

    /// <summary>Calls <paramref name="body"/> for every item and returns when all are done; rethrows the first error.</summary>
    void ForEach<T>(IReadOnlyList<T> items, Action<T> body);
}

/// <summary>A fixed number of workers (tools and tests; jobs use the worker gate).</summary>
public sealed class FixedParallel(int workers, CancellationToken token = default) : IParallelRunner
{
    public CancellationToken Token => token;

    public void ForEach<T>(IReadOnlyList<T> items, Action<T> body) =>
        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = token }, body);
}

/// <summary>
/// A loop inside a job. The calling thread already holds a worker slot and works through the items itself; helpers
/// join whenever the gate has a free slot. When the level is lowered, helpers leave after their current item, so the
/// change applies from the next item on.
/// </summary>
sealed class GateParallel(WorkerGate gate, CancellationToken token) : IParallelRunner
{
    public CancellationToken Token => token;

    public void ForEach<T>(IReadOnlyList<T> items, Action<T> body)
    {
        if (items.Count == 0) return;
        int next = -1;
        Exception? error = null;
        using var finished = CancellationTokenSource.CreateLinkedTokenSource(token);
        var helpers = new List<Task>();

        void Work(WorkerGate.Slot? slot)
        {
            while (!token.IsCancellationRequested && Volatile.Read(ref error) is null)
            {
                if (slot is not null && slot.RetireIfOverLimit()) return;
                int i = Interlocked.Increment(ref next);
                if (i >= items.Count) return;
                try { body(items[i]); }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref error, ex, null);
                    return;
                }
            }
        }

        bool ItemsLeft() => Volatile.Read(ref next) < items.Count - 1;

        var recruiter = Task.Run(async () =>
        {
            while (ItemsLeft() && !finished.IsCancellationRequested)
            {
                WorkerGate.Slot slot;
                try { slot = await gate.AcquireAsync(finished.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (!ItemsLeft()) { slot.Dispose(); return; }
                lock (helpers) helpers.Add(Task.Run(() => { using (slot) Work(slot); }));
            }
        });

        Work(null);
        finished.Cancel();
        recruiter.Wait();
        Task[] all;
        lock (helpers) all = helpers.ToArray();
        Task.WaitAll(all);
        if (error is not null) ExceptionDispatchInfo.Throw(error);
        token.ThrowIfCancellationRequested();
    }
}
