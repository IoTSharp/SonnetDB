using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace SonnetDB.Sql.Execution;

/// <summary>数据库级受控 SQL 并行 worker 槽位。</summary>
internal sealed class SqlParallelCoordinator : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private int _activeWorkers;
    private int _maxObservedWorkers;
    private int _disposed;

    internal SqlParallelCoordinator(int maxWorkers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWorkers);
        _slots = new SemaphoreSlim(maxWorkers, maxWorkers);
    }

    internal int ActiveWorkers => Volatile.Read(ref _activeWorkers);

    internal int MaxObservedWorkers => Volatile.Read(ref _maxObservedWorkers);

    internal bool TryAcquire(CancellationToken cancellationToken, out IDisposable lease)
    {
        lease = NullLease.Instance;
        if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
            return false;

        try
        {
            if (!_slots.Wait(0))
                return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        int active = Interlocked.Increment(ref _activeWorkers);
        while (active > Volatile.Read(ref _maxObservedWorkers))
        {
            int observed = Volatile.Read(ref _maxObservedWorkers);
            if (Interlocked.CompareExchange(ref _maxObservedWorkers, active, observed) == observed)
                break;
        }

        lease = new WorkerLease(this);
        return true;
    }

    private void Release()
    {
        Interlocked.Decrement(ref _activeWorkers);
        try
        {
            _slots.Release();
        }
        catch (ObjectDisposedException)
        {
            // 数据库正在关闭；活动查询仍会通过自己的 finally 释放其他资源。
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _slots.Dispose();
    }

    private sealed class WorkerLease(SqlParallelCoordinator owner) : IDisposable
    {
        private SqlParallelCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private sealed class NullLease : IDisposable
    {
        internal static NullLease Instance { get; } = new();

        public void Dispose() { }
    }
}

/// <summary>一次 SQL 查询的估算/实际输入反馈；目录有界且不保存敏感值。</summary>
internal sealed class SqlRuntimeFeedbackStore
{
    private const int MaxEntries = 1_024;
    private readonly ConcurrentDictionary<string, SqlRuntimeFeedbackSnapshot> _entries = new(StringComparer.Ordinal);

    internal void Record(string fingerprint, long estimatedRows, long actualRows)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || estimatedRows < 0 || actualRows < 0)
            return;

        _entries.AddOrUpdate(
            fingerprint,
            _ => new SqlRuntimeFeedbackSnapshot(fingerprint, estimatedRows, actualRows, 1, DateTimeOffset.UtcNow),
            (_, previous) =>
            {
                long estimate = previous.EstimatedRows == 0
                    ? estimatedRows
                    : AverageNonNegative(previous.EstimatedRows, estimatedRows);
                long actual = previous.ActualRows == 0
                    ? actualRows
                    : AverageNonNegative(previous.ActualRows, actualRows);
                return previous with
                {
                    EstimatedRows = estimate,
                    ActualRows = actual,
                    SampleCount = checked(previous.SampleCount + 1),
                    RecordedAtUtc = DateTimeOffset.UtcNow,
                };
            });

        if (_entries.Count <= MaxEntries)
            return;

        foreach (var candidate in _entries.Values.OrderBy(static value => value.RecordedAtUtc).Take(_entries.Count - MaxEntries))
            _entries.TryRemove(candidate.Fingerprint, out _);
    }

    private static long AverageNonNegative(long left, long right)
        => checked(left / 2 + right / 2 + (left % 2 + right % 2) / 2);

    internal bool TryGet(string fingerprint, out SqlRuntimeFeedbackSnapshot snapshot)
        => _entries.TryGetValue(fingerprint, out snapshot!);

    internal long CorrectEstimate(string fingerprint, long estimatedRows)
    {
        if (estimatedRows <= 0 || !TryGet(fingerprint, out SqlRuntimeFeedbackSnapshot snapshot))
            return Math.Max(0, estimatedRows);

        double ratio = snapshot.ActualToEstimatedRatio;
        if (!double.IsFinite(ratio) || ratio <= 0)
            return estimatedRows;

        double corrected = estimatedRows * ratio;
        return corrected >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1L, (long)Math.Round(corrected, MidpointRounding.AwayFromZero));
    }

    internal IReadOnlyList<SqlRuntimeFeedbackSnapshot> Snapshot()
        => _entries.Values.OrderBy(static value => value.Fingerprint, StringComparer.Ordinal).ToArray();
}

/// <summary>单个查询 fingerprint 的滚动行数反馈。</summary>
internal sealed record SqlRuntimeFeedbackSnapshot(
    string Fingerprint,
    long EstimatedRows,
    long ActualRows,
    long SampleCount,
    DateTimeOffset RecordedAtUtc)
{
    /// <summary>实际/估算行数比率；估算为零时返回 1。</summary>
    internal double ActualToEstimatedRatio
        => EstimatedRows <= 0 ? 1 : (double)ActualRows / EstimatedRows;
}

/// <summary>在有界 worker 数内对输入执行并行映射，结果顺序严格与输入一致。</summary>
internal static class SqlParallelExecution
{
    internal static IReadOnlyList<TResult> MapOrdered<TInput, TResult>(
        IReadOnlyList<TInput> inputs,
        Func<TInput, TResult> selector,
        string operatorName,
        long estimatedRows,
        string? forcedFallbackReason = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);

        SqlQueryResources? resources = SqlQueryResources.Current;
        int workerCount = forcedFallbackReason is null
            ? resources?.ChooseParallelWorkerCount(inputs.Count, estimatedRows) ?? 1
            : 1;
        if (workerCount <= 1)
        {
            resources?.RecordParallelDecision(
                operatorName,
                false,
                1,
                forcedFallbackReason ?? "benefit_or_resource_gate");
            var serial = new TResult[inputs.Count];
            for (int index = 0; index < inputs.Count; index++)
            {
                resources?.ThrowIfCancellationRequested();
                serial[index] = selector(inputs[index]);
            }
            return serial;
        }

        var leases = new List<SqlQueryResources.SqlParallelWorkerLease>(workerCount);
        try
        {
            for (int index = 0; index < workerCount; index++)
            {
                if (!resources!.TryAcquireParallelWorker(out var lease))
                    break;
                leases.Add(lease);
            }

            if (leases.Count <= 1)
            {
                resources?.RecordParallelDecision(operatorName, false, 1, "parallel_permit_or_memory_unavailable");
                foreach (var lease in leases)
                    lease.Dispose();
                var serial = new TResult[inputs.Count];
                for (int index = 0; index < inputs.Count; index++)
                {
                    resources?.ThrowIfCancellationRequested();
                    serial[index] = selector(inputs[index]);
                }
                return serial;
            }

            resources!.RecordParallelDecision(operatorName, true, leases.Count, null);
            var output = new TResult[inputs.Count];
            int next = -1;
            var tasks = new Task[leases.Count];
            for (int worker = 0; worker < tasks.Length; worker++)
            {
                tasks[worker] = Task.Run(() =>
                {
                    while (true)
                    {
                        int index = Interlocked.Increment(ref next);
                        if (index >= inputs.Count)
                            return;
                        resources.ThrowIfCancellationRequested();
                        output[index] = selector(inputs[index]);
                    }
                }, resources.CancellationToken);
            }

            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException exception)
            {
                IReadOnlyList<Exception> errors = exception.Flatten().InnerExceptions;
                Exception? actual = null;
                for (int errorIndex = 0; errorIndex < errors.Count; errorIndex++)
                {
                    if (errors[errorIndex] is OperationCanceledException)
                    {
                        actual = errors[errorIndex];
                        break;
                    }
                }

                actual ??= errors.Count == 0 ? exception : errors[0];
                ExceptionDispatchInfo.Capture(actual).Throw();
                throw new UnreachableException();
            }

            resources.RecordParallelCompleted(operatorName, inputs.Count);
            return output;
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }
}
