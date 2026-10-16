namespace SonnetDB.Tables;

public sealed partial class TableStore
{
    internal TableRowLockManager? RowLocks { get; init; }
    internal object RowLockIdentity { get; init; } = new();

    internal long RowLockSequence => _keyspace.LastSequence;

    private T RunRowLocked<T>(Func<T> action)
        => RowLocks is null ? action() : TableLockOperation.Run(RowLocks, action);

    private void RunRowLocked(Action action)
    {
        if (RowLocks is null) action();
        else TableLockOperation.Run(RowLocks, action);
    }

    private void RequireRowWrites(IEnumerable<TableMutationOperation> operations)
    {
        if (RowLocks is null) return;
        var resources = new List<TableRowLockManager.Resource>();
        foreach (var operation in operations)
        {
            if (operation.OldRow is not null)
                resources.Add(TableRowLockManager.Resource.Row(RowLockIdentity, operation.OldRow.PrimaryKey.Span));
            if (operation.NewRow is not null)
                resources.Add(TableRowLockManager.Resource.Row(RowLockIdentity, operation.NewRow.PrimaryKey.Span));
        }
        TableLockOperation.Require(RowLocks, resources.Distinct().ToArray());
    }

    private void RequireMutationRows(TableSchema schema, IReadOnlyList<TableRowMutation> mutations)
    {
        if (RowLocks is null) return;
        var resources = new List<TableRowLockManager.Resource>();
        foreach (var mutation in mutations)
        {
            resources.Add(TableRowLockManager.Resource.Row(RowLockIdentity, TableRowIdentity.TargetKey(schema, mutation)));
            if (mutation.NewValues is not null)
                resources.Add(TableRowLockManager.Resource.Row(RowLockIdentity, TableRowIdentity.CurrentKey(schema, mutation)));
        }
        TableLockOperation.Require(RowLocks, resources.Distinct().ToArray());
    }

    private void RequireIdentityWrite()
    {
        if (RowLocks is not null)
            TableLockOperation.Require(RowLocks, [new TableRowLockManager.Resource(RowLockIdentity, "identity")], retain: false);
    }

    // 正常预演只修改私有数组；多表调用方先完成所有表的预演和取锁。
    // 终止性自增溢出另保留旧合同，不复用此前已预留的号码。
    internal IReadOnlyList<TableRowMutation> PrepareLockingMutations(IReadOnlyList<TableRowMutation> mutations)
    {
        lock (_sync)
        {
            var schema = _schema;
            var prepared = new List<TableRowMutation>(mutations.Count);
            long current = schema.AutoIncrementColumn is null ? 0 : ReadAutoIncrementStateLocked();
            bool changed = false;
            foreach (var mutation in mutations)
            {
                if (mutation.NewValues is null) { prepared.Add(mutation); continue; }
                var values = mutation.NewValues.ToArray();
                DecimalDeclarationValidator.NormalizeForWrite(schema, values);
                if (schema.AutoIncrementColumn is { } column)
                {
                    if (values.Length != schema.Columns.Count)
                        throw new ArgumentException("行值数量必须与表 schema 列数量一致。", nameof(mutations));
                    PreviewAutoIncrementValue(column, values, mutation.PrimaryKeyValues is null, ref current, ref changed);
                }
                prepared.Add(TableRowIdentity.PrepareInsert(schema, mutation with { NewValues = values }));
            }
            RequireMutationRows(schema, prepared);
            if (changed) RequireIdentityWrite();
            return prepared;
        }
    }

    private byte[][] PrepareInsertRowLocks(TableSchema schema, IReadOnlyList<object?[]> rows)
    {
        long current = schema.AutoIncrementColumn is null ? 0 : ReadAutoIncrementStateLocked();
        bool changed = false;
        var keys = new byte[rows.Count][];
        for (int index = 0; index < rows.Count; index++)
        {
            var values = rows[index];
            if (values.Length != schema.Columns.Count)
                throw new ArgumentException("行值数量必须与表 schema 列数量一致。", nameof(rows));
            if (schema.AutoIncrementColumn is { } column)
                PreviewAutoIncrementValue(column, values, true, ref current, ref changed);
            keys[index] = TableRowIdentity.InsertKey(schema, values);
        }
        if (RowLocks is not null)
            TableLockOperation.Require(RowLocks, keys.Select(key => TableRowLockManager.Resource.Row(RowLockIdentity, key)).ToArray());
        if (changed) RequireIdentityWrite();
        return keys;
    }

    private void PreviewAutoIncrementValue(TableColumn column, object?[] values, bool allocateMissing,
        ref long current, ref bool changed)
    {
        try { ApplyAutoIncrementValue(column, values, allocateMissing, ref current, ref changed); }
        catch (OverflowException)
        {
            // 保留既有“溢出前已预留号码不复用”合同。此分支终止整个操作，写高水位后
            // 立即抛原异常，不再申请任何行锁或重试行写入；identity 竞争仍先在锁外等待。
            WriteAutoIncrementStateLocked(current, changed);
            throw;
        }
    }
}
