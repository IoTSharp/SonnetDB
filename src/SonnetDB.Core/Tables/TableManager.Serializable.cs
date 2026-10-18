using System.Diagnostics;
using SonnetDB.Exceptions;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    private const int MaximumSerializableMetadataItems = 16_384;

    internal void ExpandSerializableCascades(SqlTransactionContext transaction)
    {
        transaction.CheckCommitDeadline();
        if (transaction.SnapshotTableMutations().Count == 0) return;
        ValidateSerializableMetadataBudget(transaction);
        ExpandDeferredCascades(transaction);
    }

    private IReadOnlyList<TableSchema> ValidateSerializableMetadataBudget(SqlTransactionContext transaction)
    {
        transaction.CheckCommitDeadline();
        if (_catalog.Count > MaximumSerializableMetadataItems)
            throw new NotSupportedException("Serializable 提交最多检查 16384 张表的外键元数据。");
        var schemas = _catalog.Snapshot();
        int foreignKeys = 0;
        foreach (var schema in schemas)
        {
            transaction.CheckCommitDeadline();
            foreach (var _ in schema.ForeignKeys)
            {
                transaction.CheckCommitDeadline();
                if (++foreignKeys > MaximumSerializableMetadataItems)
                    throw new NotSupportedException("Serializable 提交最多检查 16384 项外键定义。");
            }
        }
        return schemas;
    }

    internal int ApplySerializableTransaction(SqlTransactionContext transaction,
        IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> mutations)
    {
        // 调用方持有 manager 锁；触发器与级联已完成，固定实际读、写和外键校验依赖。
        transaction.CheckCommitDeadline();
        long started = Stopwatch.GetTimestamp();
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name)
        {
            transaction.CheckCommitDeadline();
            names.Add(name);
            if (names.Count > SqlTransactionContext.MaximumSerializableTables)
                throw new NotSupportedException("Serializable 提交最多锁定 4096 张实际依赖表。");
        }
        foreach (var read in transaction.SerializableReads) Add(read.Schema.Name);
        int mutationRows = 0;
        foreach (var (name, rows) in mutations)
        {
            Add(name);
            if (rows.Count > 100_000 - mutationRows)
                throw new NotSupportedException("Serializable 提交最多包含 100000 项行变更。");
            mutationRows += rows.Count;
        }
        if (mutations.Count != 0)
        {
            foreach (var schema in ValidateSerializableMetadataBudget(transaction))
            {
                transaction.CheckCommitDeadline();
                foreach (var foreignKey in schema.ForeignKeys)
                {
                    transaction.CheckCommitDeadline();
                    if (mutations.ContainsKey(schema.Name) || mutations.ContainsKey(foreignKey.PrincipalTable))
                    {
                        Add(schema.Name);
                        Add(foreignKey.PrincipalTable);
                    }
                }
            }
        }
        string[] orderedNames = names.Order(StringComparer.Ordinal).ToArray();
        var stores = new List<TableStore>(orderedNames.Length);
        try
        {
            foreach (string name in orderedNames)
            {
                transaction.CheckCommitDeadline();
                var schema = _catalog.TryGet(name);
                if (schema is null) throw SerializationConflict(name);
                TableStore store = OpenStoreLocked(schema);
                transaction.CheckCommitDeadline();
                bool taken = false;
                for (int attempt = 0; attempt < 2_401 && !taken; attempt++)
                {
                    transaction.CheckCommitDeadline();
                    int remaining = 120_000 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (remaining <= 0) break;
                    Monitor.TryEnter(store.SynchronizationRoot, Math.Min(50, remaining), ref taken);
                }
                if (!taken)
                    throw new RoutineExecutionException(RoutineErrorCodes.CommitTimeout,
                        "可串行化事务提交等待依赖表锁超时，尚未开始持久化。");
                stores.Add(store);
                transaction.CheckCommitDeadline();
            }
            ValidateSerializableReadsLocked(transaction);
            transaction.EndDeferredExecution();
            // 公共 TableStore 写入只取得 store 锁；从校验至完整 apply 不释放这些锁。
            return ApplyTransaction(mutations);
        }
        finally
        {
            for (int index = stores.Count - 1; index >= 0; index--)
                Monitor.Exit(stores[index].SynchronizationRoot);
        }
    }

    private void ValidateSerializableReadsLocked(SqlTransactionContext transaction)
    {
        foreach (var read in transaction.SerializableReads)
        {
            transaction.CheckCommitDeadline();
            if (_catalog.TryGet(read.Schema.Name) is not { } schema
                || !ReferenceEquals(schema, read.Schema)
                || !_stores.TryGetValue(read.Schema.Name, out var store)
                || !store.MatchesSerializableRead(read, transaction.SerializableExpectedSequence(read)))
                throw SerializationConflict(read.Schema.Name);
        }
    }

    private static TableConstraintException SerializationConflict(string name)
        => new(TableConstraintException.SerializationConflict, name, null,
            $"table '{name}' 在首次读取后发生变化，事务已拒绝提交。", "commit", hint: null);
}
