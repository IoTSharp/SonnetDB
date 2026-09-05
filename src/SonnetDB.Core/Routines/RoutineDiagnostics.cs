using System.Text.Json;

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
    int ResultRows)
{
    /// <summary>执行完成、待提交、已提交、已回滚或执行失败；不把待提交动作报告为成功。</summary>
    public string Outcome { get; init; } = Succeeded ? "completed" : "failed";
}

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

    /// <summary>按类型、名称和序号过滤最近的有界审计记录。</summary>
    /// <param name="kind">可选的 procedure 或 trigger 类型。</param>
    /// <param name="name">可选的定义名称。</param>
    /// <param name="afterSequence">只返回此序号之后的记录。</param>
    /// <returns>按序号升序的脱敏记录；较旧记录可能已被有界队列淘汰。</returns>
    public IReadOnlyList<RoutineInvocationRecord> SnapshotAudit(string? kind, string? name, long afterSequence = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        lock (_sync)
            return _audit.Where(record => record.Sequence > afterSequence
                    && (kind is null || string.Equals(kind, record.Kind, StringComparison.Ordinal))
                    && (name is null || string.Equals(name, record.Name, StringComparison.Ordinal)))
                .ToArray();
    }

    /// <summary>以 AOT 兼容 JSON 导出最近的脱敏审计快照；不关闭调用方的流。</summary>
    /// <param name="destination">可写目标流。</param>
    /// <param name="kind">可选例程类型。</param>
    /// <param name="name">可选定义名称。</param>
    /// <param name="afterSequence">只导出此序号之后的记录。</param>
    public void ExportAudit(Stream destination, string? kind = null, string? name = null, long afterSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var records = SnapshotAudit(kind, name, afterSequence);
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteString("schema", "sonnetdb-routine-audit-v1");
        writer.WriteNumber("capacity", MaxAuditRecords);
        writer.WriteStartArray("records");
        foreach (var record in records)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", record.Sequence);
            writer.WriteString("kind", record.Kind);
            writer.WriteString("name", record.Name);
            writer.WriteString("caller", record.Caller);
            writer.WriteString("callChain", record.CallChain);
            writer.WriteString("startedUtc", record.StartedUtc);
            writer.WriteNumber("elapsedMilliseconds", record.ElapsedMilliseconds);
            writer.WriteBoolean("succeeded", record.Succeeded);
            writer.WriteString("outcome", record.Outcome);
            writer.WriteString("errorCode", record.ErrorCode);
            writer.WriteNumber("statementsExecuted", record.StatementsExecuted);
            writer.WriteNumber("resultRows", record.ResultRows);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
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
        int resultRows,
        bool pendingCommit = false)
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

        lock (_sync)
        {
            long sequence = ++_sequence;
            var record = new RoutineInvocationRecord(
                sequence,
                kind,
                name,
                caller,
                callChain,
                startedUtc,
                elapsed.TotalMilliseconds,
                succeeded && !pendingCommit,
                errorCode,
                statementsExecuted,
                resultRows)
            {
                Outcome = pendingCommit ? "pending" : succeeded ? "completed"
                    : errorCode == SonnetDB.Exceptions.RoutineErrorCodes.CommitUnknown ? "unknown" : "failed",
            };
            if (_audit.Count >= MaxAuditRecords)
                _audit.Dequeue();
            _audit.Enqueue(record);
            return sequence;
        }
    }

    internal void CompleteTransaction(
        IReadOnlyList<(long Sequence, string Kind)> invocations,
        bool committed,
        string? errorCode)
    {
        if (invocations.Count == 0)
            return;

        var sequenceSet = invocations.Select(static invocation => invocation.Sequence).ToHashSet();
        if (!committed)
        {
            Interlocked.Add(ref _triggerFailures, invocations.Count(static invocation => invocation.Kind == "trigger"));
            Interlocked.Add(ref _procedureFailures, invocations.Count(static invocation => invocation.Kind == "procedure"));
        }
        lock (_sync)
        {
            var records = _audit.ToArray();
            _audit.Clear();
            foreach (var record in records)
            {
                _audit.Enqueue(
                    sequenceSet.Contains(record.Sequence)
                    && record.Outcome == "pending"
                        ? record with
                        {
                            Succeeded = committed,
                            ErrorCode = errorCode,
                            Outcome = committed ? "committed" : errorCode == SonnetDB.Exceptions.RoutineErrorCodes.CommitUnknown
                                ? "unknown" : "rolled_back"
                        }
                        : record);
            }
        }
    }
}
