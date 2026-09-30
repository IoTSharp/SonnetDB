using SonnetDB.Catalog;
using SonnetDB.Diagnostics;
using SonnetDB.Storage.Format;

namespace SonnetDB.Engine;

/// <summary>measurement schema 变更或拒绝的审计类型。</summary>
public enum MeasurementSchemaAuditKind
{
    /// <summary>新建 measurement。</summary>
    Created,

    /// <summary>扩列或兼容类型提升。</summary>
    Evolved,

    /// <summary>删除 measurement。</summary>
    Dropped,

    /// <summary>策略或额度拒绝自动 schema 变更。</summary>
    Rejected,
}

/// <summary>不含写入值的 measurement schema 审计记录。</summary>
/// <param name="Sequence">进程内单调审计序号。</param>
/// <param name="TimestampUtc">事件发生时间。</param>
/// <param name="Measurement">measurement 名称。</param>
/// <param name="Kind">变更或拒绝类型。</param>
/// <param name="Revision">事件完成后的 schema revision；拒绝时保持原 revision。</param>
/// <param name="AddedColumns">本次新增列数。</param>
/// <param name="PromotedColumns">本次 INT64 到 FLOAT64 提升列数。</param>
/// <param name="ReasonCode">拒绝时的稳定原因码；成功时为 null。</param>
public sealed record MeasurementSchemaAuditEvent(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string Measurement,
    MeasurementSchemaAuditKind Kind,
    string Revision,
    int AddedColumns,
    int PromotedColumns,
    string? ReasonCode);

/// <summary>measurement schema 变更累计指标快照。</summary>
/// <param name="CreatedMeasurements">本实例成功创建的 measurement 数。</param>
/// <param name="AddedColumns">已有 measurement 成功扩列的列数。</param>
/// <param name="PromotedColumns">成功从 INT64 提升到 FLOAT64 的列数。</param>
/// <param name="RejectedChanges">策略、额度或列冲突拒绝的变更次数。</param>
public sealed record MeasurementSchemaMetricsSnapshot(
    long CreatedMeasurements,
    long AddedColumns,
    long PromotedColumns,
    long RejectedChanges);

/// <summary>提供进程内有界审计、变更通知与低基数累计指标；审计记录不持久化。</summary>
public sealed class MeasurementSchemaDiagnostics
{
    private const int MaxAuditRecords = 256;
    private readonly object _sync = new();
    private readonly Queue<MeasurementSchemaAuditEvent> _audit = new();
    private long _sequence;
    private long _createdMeasurements;
    private long _addedColumns;
    private long _promotedColumns;
    private long _rejectedChanges;

    /// <summary>成功持久化并发布 schema 后触发；处理器异常不会改变提交结果。</summary>
    public event EventHandler<MeasurementSchemaAuditEvent>? SchemaChanged;

    /// <summary>schema 成功变更或被拒绝时触发；处理器异常不影响写入结果。</summary>
    public event EventHandler<MeasurementSchemaAuditEvent>? AuditEvent;

    /// <summary>返回当前实例的 schema 累计指标。</summary>
    /// <returns>线程安全的指标快照。</returns>
    public MeasurementSchemaMetricsSnapshot GetMetrics()
        => new(
            Interlocked.Read(ref _createdMeasurements),
            Interlocked.Read(ref _addedColumns),
            Interlocked.Read(ref _promotedColumns),
            Interlocked.Read(ref _rejectedChanges));

    /// <summary>返回最近的有界审计记录，按序号升序。</summary>
    /// <param name="afterSequence">仅返回该序号之后的记录。</param>
    /// <returns>进程内最近最多 256 条记录；重启后清空，更早记录可能已淘汰。</returns>
    public IReadOnlyList<MeasurementSchemaAuditEvent> SnapshotAudit(long afterSequence = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        lock (_sync)
            return _audit.Where(record => record.Sequence > afterSequence).ToArray();
    }

    internal void RecordPublished(
        IReadOnlyList<MeasurementSchema> before,
        IReadOnlyList<MeasurementSchema> after,
        string revision)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(revision);

        var previous = before.ToDictionary(static schema => schema.Name, StringComparer.Ordinal);
        var current = after.ToDictionary(static schema => schema.Name, StringComparer.Ordinal);
        var events = new List<MeasurementSchemaAuditEvent>();

        foreach (MeasurementSchema schema in after)
        {
            if (!previous.TryGetValue(schema.Name, out MeasurementSchema? oldSchema))
            {
                Interlocked.Increment(ref _createdMeasurements);
                events.Add(AppendAudit(schema.Name, MeasurementSchemaAuditKind.Created, revision,
                    schema.Columns.Count, 0, null));
                continue;
            }

            int added = 0;
            int promoted = 0;
            foreach (MeasurementColumn column in schema.Columns)
            {
                MeasurementColumn? oldColumn = oldSchema.TryGetColumn(column.Name);
                if (oldColumn is null)
                    added++;
                else if (oldColumn.DataType == FieldType.Int64 && column.DataType == FieldType.Float64)
                    promoted++;
            }

            if (added == 0 && promoted == 0)
                continue;

            Interlocked.Add(ref _addedColumns, added);
            Interlocked.Add(ref _promotedColumns, promoted);
            if (added > 0)
                SonnetDbMeter.SchemaColumnsAdded.Add(added);
            if (promoted > 0)
                SonnetDbMeter.SchemaColumnsPromoted.Add(promoted);
            events.Add(AppendAudit(schema.Name, MeasurementSchemaAuditKind.Evolved, revision,
                added, promoted, null));
        }

        foreach (MeasurementSchema oldSchema in before)
        {
            if (!current.ContainsKey(oldSchema.Name))
                events.Add(AppendAudit(oldSchema.Name, MeasurementSchemaAuditKind.Dropped, revision, 0, 0, null));
        }

        foreach (MeasurementSchemaAuditEvent auditEvent in events)
        {
            Notify(AuditEvent, auditEvent);
            Notify(SchemaChanged, auditEvent);
        }
    }

    internal void RecordRejected(string measurement, string reasonCode, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(measurement);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentNullException.ThrowIfNull(revision);
        Interlocked.Increment(ref _rejectedChanges);
        SonnetDbMeter.SchemaChangesRejected.Add(1);
        MeasurementSchemaAuditEvent auditEvent = AppendAudit(
            measurement, MeasurementSchemaAuditKind.Rejected, revision, 0, 0, reasonCode);
        Notify(AuditEvent, auditEvent);
    }

    private void Notify(EventHandler<MeasurementSchemaAuditEvent>? handlers, MeasurementSchemaAuditEvent auditEvent)
    {
        if (handlers is null)
            return;
        foreach (EventHandler<MeasurementSchemaAuditEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, auditEvent);
            }
            catch
            {
                // 观察者不能把已经持久化的 schema 提交或正常拒绝变成失败。
            }
        }
    }

    private MeasurementSchemaAuditEvent AppendAudit(
        string measurement,
        MeasurementSchemaAuditKind kind,
        string revision,
        int addedColumns,
        int promotedColumns,
        string? reasonCode)
    {
        lock (_sync)
        {
            var record = new MeasurementSchemaAuditEvent(
                ++_sequence,
                DateTimeOffset.UtcNow,
                measurement,
                kind,
                revision,
                addedColumns,
                promotedColumns,
                reasonCode);
            if (_audit.Count >= MaxAuditRecords)
                _audit.Dequeue();
            _audit.Enqueue(record);
            return record;
        }
    }
}
