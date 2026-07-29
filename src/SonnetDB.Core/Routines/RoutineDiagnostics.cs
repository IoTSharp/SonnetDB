namespace SonnetDB.Routines;

/// <summary>一次过程或触发器调用的脱敏审计记录。</summary>
/// <param name="Sequence">进程内单调序号。</param>
/// <param name="Kind"><c>procedure</c> 或 <c>trigger</c>。</param>
/// <param name="Name">定义名称。</param>
/// <param name="Caller">调用方标识。</param>
/// <param name="CallChain">不含参数值的调用链。</param>
/// <param name="StartedUtc">开始时间。</param>
/// <param name="ElapsedMilliseconds">执行耗时（毫秒）。</param>
/// <param name="Succeeded">是否成功。</param>
/// <param name="ErrorCode">失败时的稳定错误码。</param>
/// <param name="StatementsExecuted">本次调用及其下游累计执行语句数增量。</param>
/// <param name="ResultRows">本次调用及其下游累计结果行数增量。</param>
public sealed record RoutineInvocationRecord(
    long Sequence,
    string Kind,
    string Name,
    string Caller,
    string CallChain,
    DateTimeOffset StartedUtc,
    double ElapsedMilliseconds,
    bool Succeeded,
    string? ErrorCode,
    int StatementsExecuted,
    int ResultRows);

/// <summary>过程与触发器累计指标快照。</summary>
/// <param name="ProcedureExecutions">过程调用次数。</param>
/// <param name="ProcedureFailures">过程失败次数。</param>
/// <param name="ProcedureElapsedMilliseconds">过程累计耗时。</param>
/// <param name="TriggerExecutions">触发器调用次数。</param>
/// <param name="TriggerFailures">触发器失败次数。</param>
/// <param name="TriggerElapsedMilliseconds">触发器累计耗时。</param>
public sealed record RoutineMetricsSnapshot(
    long ProcedureExecutions,
    long ProcedureFailures,
    double ProcedureElapsedMilliseconds,
    long TriggerExecutions,
    long TriggerFailures,
    double TriggerElapsedMilliseconds);

/// <summary>保存有界调用审计并提供无锁累计指标。</summary>
public sealed class RoutineDiagnostics
{
    private const int MaxAuditRecords = 256;
    private readonly object _sync = new();
    private readonly Queue<RoutineInvocationRecord> _audit = new();
    private long _sequence;
    private long _procedureExecutions;
    private long _procedureFailures;
    private long _procedureElapsedTicks;
    private long _triggerExecutions;
    private long _triggerFailures;
    private long _triggerElapsedTicks;

    /// <summary>返回最近的脱敏调用审计，按序号升序。</summary>
    /// <returns>不可变语义的审计快照。</returns>
    public IReadOnlyList<RoutineInvocationRecord> SnapshotAudit()
    {
        lock (_sync)
            return _audit.ToArray();
    }

    /// <summary>返回累计调用、失败和耗时指标。</summary>
    /// <returns>指标快照。</returns>
    public RoutineMetricsSnapshot GetMetrics()
        => new(
            Interlocked.Read(ref _procedureExecutions),
            Interlocked.Read(ref _procedureFailures),
            TimeSpan.FromTicks(Interlocked.Read(ref _procedureElapsedTicks)).TotalMilliseconds,
            Interlocked.Read(ref _triggerExecutions),
            Interlocked.Read(ref _triggerFailures),
            TimeSpan.FromTicks(Interlocked.Read(ref _triggerElapsedTicks)).TotalMilliseconds);

    internal long Record(
        string kind,
        string name,
        string caller,
        string callChain,
        DateTimeOffset startedUtc,
        TimeSpan elapsed,
        bool succeeded,
        string? errorCode,
        int statementsExecuted,
        int resultRows)
    {
        if (string.Equals(kind, "procedure", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _procedureExecutions);
            if (!succeeded)
                Interlocked.Increment(ref _procedureFailures);
            Interlocked.Add(ref _procedureElapsedTicks, elapsed.Ticks);
        }
        else
        {
            Interlocked.Increment(ref _triggerExecutions);
            if (!succeeded)
                Interlocked.Increment(ref _triggerFailures);
            Interlocked.Add(ref _triggerElapsedTicks, elapsed.Ticks);
        }

        long sequence = Interlocked.Increment(ref _sequence);
        var record = new RoutineInvocationRecord(
            sequence,
            kind,
            name,
            caller,
            callChain,
            startedUtc,
            elapsed.TotalMilliseconds,
            succeeded,
            errorCode,
            statementsExecuted,
            resultRows);
        lock (_sync)
        {
            while (_audit.Count >= MaxAuditRecords)
                _audit.Dequeue();
            _audit.Enqueue(record);
        }
        return sequence;
    }

    internal void MarkTriggerTransactionFailure(
        IReadOnlyList<long> auditSequences,
        string errorCode)
    {
        ArgumentNullException.ThrowIfNull(auditSequences);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (auditSequences.Count == 0)
            return;

        var sequenceSet = auditSequences.ToHashSet();
        Interlocked.Add(ref _triggerFailures, sequenceSet.Count);
        lock (_sync)
        {
            var records = _audit.ToArray();
            _audit.Clear();
            foreach (var record in records)
            {
                _audit.Enqueue(
                    sequenceSet.Contains(record.Sequence)
                    && string.Equals(record.Kind, "trigger", StringComparison.Ordinal)
                    && record.Succeeded
                        ? record with { Succeeded = false, ErrorCode = errorCode }
                        : record);
            }
        }
    }
}
