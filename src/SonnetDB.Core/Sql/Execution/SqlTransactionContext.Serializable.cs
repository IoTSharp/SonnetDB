using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

public sealed partial class SqlTransactionContext
{
    internal const int MaximumSerializableTables = 4_096;
    private readonly Dictionary<string, SerializableTableRead> _serializableReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _serializableReservationAdvances = new(StringComparer.Ordinal);

    /// <summary>创建默认读已提交轻事务。</summary>
    public SqlTransactionContext() : this(SqlTransactionIsolationLevel.ReadCommitted)
    {
    }

    /// <summary>创建指定关系表隔离级别的轻事务。</summary>
    /// <param name="isolationLevel">读已提交或乐观可串行化隔离。</param>
    public SqlTransactionContext(SqlTransactionIsolationLevel isolationLevel)
    {
        if (isolationLevel is not SqlTransactionIsolationLevel.ReadCommitted
            and not SqlTransactionIsolationLevel.Serializable)
            throw new ArgumentOutOfRangeException(nameof(isolationLevel));
        IsolationLevel = isolationLevel;
    }

    /// <summary>本事务实际使用的关系表隔离级别。</summary>
    public SqlTransactionIsolationLevel IsolationLevel { get; }

    internal void RecordSerializableRead(TableStore store, TableSchema schema, long generation, long sequence)
    {
        if (SqlTemporaryTableIdentity.TryGet(schema, out _))
            return;
        if (IsolationLevel != SqlTransactionIsolationLevel.Serializable || _completed)
            return;
        if (_serializableReads.Count >= MaximumSerializableTables && !_serializableReads.ContainsKey(schema.Name))
            throw new NotSupportedException("Serializable 的读集最多包含 4096 张关系表。");
        // 必须保存首次读，不因后续 speculative reads 或保存点回滚而前移基线。
        _serializableReads.TryAdd(schema.Name, new SerializableTableRead(store, schema, generation, sequence));
    }

    internal IReadOnlyCollection<SerializableTableRead> SerializableReads => _serializableReads.Values;
    internal bool IsSerializableCommitInProgress
        => _commitInProgress && IsolationLevel == SqlTransactionIsolationLevel.Serializable;

    internal void RecordSerializableReservation(TableStore store, TableSchema schema, long generation,
        long beforeSequence, long afterSequence)
    {
        if (SqlTemporaryTableIdentity.TryGet(schema, out _))
            return;
        if (IsolationLevel != SqlTransactionIsolationLevel.Serializable || _completed || beforeSequence == afterSequence)
            return;
        RecordSerializableRead(store, schema, generation, beforeSequence);
        var read = _serializableReads[schema.Name];
        long advance = _serializableReservationAdvances.GetValueOrDefault(schema.Name);
        if (ReferenceEquals(read.Store, store) && ReferenceEquals(read.Schema, schema)
            && read.Generation == generation && read.Sequence + advance == beforeSequence)
            _serializableReservationAdvances[schema.Name] = checked(advance + afterSequence - beforeSequence);
        // 出现外部变化时不吸收该版本，保留首次基线使 COMMIT 拒绝。
    }

    internal long SerializableExpectedSequence(SerializableTableRead read)
        => checked(read.Sequence + _serializableReservationAdvances.GetValueOrDefault(read.Schema.Name));

    internal sealed record SerializableTableRead(TableStore Store, TableSchema Schema, long Generation, long Sequence);
}
