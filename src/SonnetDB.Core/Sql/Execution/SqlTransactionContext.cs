using SonnetDB.Tables;
using SonnetDB.Kv;
using SonnetDB.Routines;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// SQL 轻事务上下文。当前聚焦关系表小批量 DML，在 COMMIT 时按表原子提交。
/// </summary>
/// <remarks>
/// <para>
/// 隔离级别：<b>读已提交 + 本事务 read-your-writes</b>（#218）。事务内的关系表 SELECT
/// 会在已提交基线之上叠加本事务尚未提交的缓冲 insert/update/delete（见 <see cref="Current"/> /
/// <see cref="TryGetBufferedMutations"/>），因此能看到自身写入；对其他并发写入仍是读已提交
/// （不做快照，不加读锁）。measurement / document 写入在事务内被拒绝（#199），故 read-your-writes
/// 只覆盖关系表。
/// </para>
/// </remarks>
public sealed class SqlTransactionContext
{
    private static readonly AsyncLocal<SqlTransactionContext?> _current = new();

    private readonly Dictionary<string, MutationBuffer> _tableMutations = new(StringComparer.Ordinal);
    private readonly List<Action> _undo = [];
    private readonly Dictionary<string, long> _autoIncrementReservationGenerations = new(StringComparer.Ordinal);
    private readonly List<(long Sequence, string Kind)> _routineInvocations = [];
    private bool _completed;

    /// <summary>事务是否已经提交或回滚。</summary>
    public bool IsCompleted => _completed;

    /// <summary>当前语句执行作用域内的活动轻事务（基于 <see cref="AsyncLocal{T}"/>）；无事务时为 <c>null</c>。</summary>
    public static SqlTransactionContext? Current => _current.Value;

    /// <summary>把 <paramref name="transaction"/> 设为当前执行作用域的 ambient 轻事务，返回作用域释放器。</summary>
    public static AmbientScope EnterScope(SqlTransactionContext? transaction)
        => new(transaction);

    /// <summary>
    /// 按主键归并同一事务内连续的 INSERT、UPDATE、DELETE，保存最终净变化和首次并发版本。
    /// </summary>
    internal void AddOrMergeTableMutation(TableSchema schema, TableRowMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(mutation);
        ThrowIfCompleted();

        if (!_tableMutations.TryGetValue(schema.Name, out var buffer))
        {
            buffer = new MutationBuffer();
            _tableMutations.Add(schema.Name, buffer);
        }

        byte[] mutationKey = GetMutationPrimaryKey(schema, mutation);
        if (buffer.ByKey.TryGetValue(mutationKey, out var node))
        {
            var previous = node.Value.Mutation;
            bool previousIsInsert = previous.PrimaryKeyValues is null;
            bool previousIsDelete = previous.PrimaryKeyValues is not null && previous.NewValues is null;
            bool mutationIsInsert = mutation.PrimaryKeyValues is null;
            bool mutationIsDelete = mutation.PrimaryKeyValues is not null && mutation.NewValues is null;
            if (previousIsInsert && mutationIsDelete)
            {
                // 本事务先插入再删除同一主键时没有净变更。
                var next = node.Next;
                buffer.Rows.Remove(node);
                buffer.ByKey.Remove(mutationKey);
                // 重复 INSERT 仍交由提交阶段拒绝；删除首条时保留其后同键操作。
                for (var candidate = buffer.DuplicateKeys.Contains(mutationKey) ? next : null;
                     candidate is not null; candidate = candidate.Next)
                {
                    if (KvKeyComparer.Instance.Equals(candidate.Value.Key, mutationKey))
                    {
                        buffer.ByKey[mutationKey] = candidate;
                        break;
                    }
                }
                _undo.Add(() =>
                {
                    if (next is null) buffer.Rows.AddLast(node);
                    else buffer.Rows.AddBefore(next, node);
                    buffer.ByKey[mutationKey] = node;
                });
                return;
            }

            if (previousIsDelete && mutationIsInsert)
            {
                // 删除后重新插入视为替换：校验原行版本，但采用新 INSERT 初始化后的完整行值。
                Replace(new TableRowMutation(
                    previous.PrimaryKeyValues,
                    mutation.NewValues,
                    previous.ExpectedRowVersion) { ExpectedRowState = previous.ExpectedRowState });
                return;
            }

            if (mutationIsInsert)
            {
                // INSERT 接在 INSERT/UPDATE 后仍是重复主键操作，保留两条 mutation 交由 COMMIT 报错。
                Append();
                return;
            }

            // INSERT→UPDATE、UPDATE→UPDATE、UPDATE→DELETE 都保留首次操作的并发基线。
            Replace(new TableRowMutation(
                previous.PrimaryKeyValues,
                mutation.NewValues,
                previous.ExpectedRowVersion) { ExpectedRowState = previous.ExpectedRowState });
            return;

            void Replace(TableRowMutation replacement)
            {
                node.Value = (node.Value.Key, replacement);
                _undo.Add(() => node.Value = (node.Value.Key, previous));
            }
        }

        Append();

        void Append()
        {
            var appended = buffer.Rows.AddLast((mutationKey, mutation));
            bool indexed = buffer.ByKey.TryAdd(mutationKey, appended);
            if (!indexed) buffer.DuplicateKeys.Add(mutationKey);
            _undo.Add(() =>
            {
                buffer.Rows.Remove(appended);
                if (indexed) buffer.ByKey.Remove(mutationKey);
            });
        }
    }

    /// <summary>
    /// 获取 mutation 的规范主键字节；INSERT 从新行取键，其余操作使用显式主键值。
    /// </summary>
    private static byte[] GetMutationPrimaryKey(TableSchema schema, TableRowMutation mutation)
    {
        if (mutation.PrimaryKeyValues is not null)
            return TableKeyCodec.EncodePrimaryKeyValues(schema, mutation.PrimaryKeyValues);
        if (mutation.NewValues is not null)
            return TableKeyCodec.EncodePrimaryKey(schema, mutation.NewValues);
        throw new InvalidOperationException("关系表 mutation 缺少主键和新行值。");
    }

    /// <summary>
    /// 读取某表本事务已缓冲、尚未提交的变更序列（按加入顺序）。供 read-your-writes 叠加使用（#218）。
    /// 无缓冲变更时返回 <c>false</c>。
    /// </summary>
    internal bool TryGetBufferedMutations(string tableName, out IReadOnlyList<TableRowMutation> mutations)
    {
        if (!_completed && _tableMutations.TryGetValue(tableName, out var buffer) && buffer.Rows.Count > 0)
        {
            mutations = buffer.Snapshot();
            return true;
        }

        mutations = [];
        return false;
    }

    internal IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> SnapshotTableMutations()
        => _tableMutations.ToDictionary(
            static p => p.Key,
            static p => p.Value.Snapshot(),
            StringComparer.Ordinal);

    internal void RecordAutoIncrementReservation(string tableName, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ThrowIfCompleted();

        if (_autoIncrementReservationGenerations.TryGetValue(tableName, out long existing)
            && existing != generation)
        {
            throw new InvalidOperationException(
                $"table '{tableName}' 在 AUTO_INCREMENT 值预留期间执行了 TRUNCATE；当前事务不能跨 generation 继续写入。");
        }

        _autoIncrementReservationGenerations[tableName] = generation;
    }

    internal IReadOnlyDictionary<string, long> SnapshotAutoIncrementReservationGenerations()
        => _autoIncrementReservationGenerations.ToDictionary(StringComparer.Ordinal);

    internal void AddRoutineInvocation(long sequence, string kind)
    {
        ThrowIfCompleted();
        _routineInvocations.Add((sequence, kind));
    }

    internal void ResolveRoutineInvocations(RoutineDiagnostics diagnostics, bool committed,
        string? errorCode = null, Savepoint? savepoint = null)
    {
        int start = savepoint?.RoutineInvocationCount ?? 0;
        if (start >= _routineInvocations.Count) return;
        diagnostics.CompleteTransaction(_routineInvocations.GetRange(start, _routineInvocations.Count - start),
            committed, errorCode);
        _routineInvocations.RemoveRange(start, _routineInvocations.Count - start);
    }

    internal Savepoint CreateSavepoint()
        => new(_undo.Count,
            _autoIncrementReservationGenerations.ToDictionary(StringComparer.Ordinal),
            _routineInvocations.Count);

    internal void RollbackTo(Savepoint savepoint)
    {
        ArgumentNullException.ThrowIfNull(savepoint);
        ThrowIfCompleted();
        for (int index = _undo.Count - 1; index >= savepoint.UndoCount; index--)
            _undo[index]();
        _undo.RemoveRange(savepoint.UndoCount, _undo.Count - savepoint.UndoCount);
        _autoIncrementReservationGenerations.Clear();
        foreach (var pair in savepoint.AutoIncrementReservationGenerations)
            _autoIncrementReservationGenerations.Add(pair.Key, pair.Value);
        if (_routineInvocations.Count > savepoint.RoutineInvocationCount)
        {
            throw new InvalidOperationException("回滚保存点前必须结算例程审计。");
        }
    }

    internal sealed record Savepoint(
        int UndoCount,
        IReadOnlyDictionary<string, long> AutoIncrementReservationGenerations,
        int RoutineInvocationCount);

    internal void MarkCompleted()
    {
        ThrowIfCompleted();
        _completed = true;
        _undo.Clear();
        _tableMutations.Clear();
        _autoIncrementReservationGenerations.Clear();
        _routineInvocations.Clear();
    }

    private sealed class MutationBuffer
    {
        internal LinkedList<(byte[] Key, TableRowMutation Mutation)> Rows { get; } = new();
        internal Dictionary<byte[], LinkedListNode<(byte[] Key, TableRowMutation Mutation)>> ByKey { get; } =
            new(KvKeyComparer.Instance);
        internal HashSet<byte[]> DuplicateKeys { get; } = new(KvKeyComparer.Instance);

        internal IReadOnlyList<TableRowMutation> Snapshot()
            => Rows.Select(static row => row.Mutation).ToArray();
    }

    internal void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("轻事务已结束。");
    }

    /// <summary>用于在 <c>using</c> 块中临时设置 ambient 轻事务上下文。</summary>
    public readonly struct AmbientScope : IDisposable
    {
        private readonly SqlTransactionContext? _previous;

        internal AmbientScope(SqlTransactionContext? transaction)
        {
            _previous = _current.Value;
            _current.Value = transaction;
        }

        /// <summary>恢复进入前的 ambient 轻事务上下文。</summary>
        public void Dispose()
            => _current.Value = _previous;
    }
}
