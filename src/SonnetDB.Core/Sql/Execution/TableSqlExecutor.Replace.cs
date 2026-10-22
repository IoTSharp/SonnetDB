using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class TableSqlExecutor
{
    /// <summary>冻结完整新行和全部冲突旧行；一批输入自身的重复主键或唯一键明确拒绝。</summary>
    internal static ReplacementPlan PrepareReplacement(
        Tsdb database, SqlTransactionContext transaction, InsertStatement statement, TableSchema schema,
        IReadOnlyList<TriggerDefinition> insertTriggers, TriggerTransitionBudget insertBudget,
        TriggerTransitionBudget deleteBudget)
    {
        SqlDateTimeOffsetContract.Validate(database, statement);
        if (statement.OnConflict is not null || statement.Query is not null)
            throw new NotSupportedException("REPLACE 仅支持 VALUES 或 DEFAULT VALUES，不能与 ON CONFLICT 或 SELECT 混用。");
        TableColumn[] returning = BindReturningColumns(statement, schema);
        TableColumn[] bindings = BindInsertColumns(statement, schema);
        var defaults = BindInsertDefaults(schema, bindings);
        insertBudget.CheckRowCapacity(statement.Rows.Count);
        var valuesRows = new List<object?[]>(statement.Rows.Count);
        foreach (var expressions in statement.Rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            var values = new object?[schema.Columns.Count];
            for (int index = 0; index < bindings.Length; index++)
                values[bindings[index].Ordinal] = ConvertInsertValue(expressions[index], bindings[index], schema);
            ApplyInsertDefaults(defaults, values);
            ApplyInsertRowVersion(schema, values);
            valuesRows.Add(values);
        }

        TableStore store = database.Tables.Open(schema.Name);
        if (schema.AutoIncrementColumn is { } identity)
        {
            bool generated = valuesRows.Any(values => values[identity.Ordinal] is null);
            long generation = store.ApplyAutoIncrement(valuesRows);
            if (generated)
                transaction.RecordAutoIncrementReservation(schema.Name, generation);
        }
        if (schema.PrimaryKey.Count == 0)
            transaction.RecordAutoIncrementReservation(schema.Name, store.ReserveRowIdentityGeneration());

        var primaryKeys = new HashSet<string>(StringComparer.Ordinal);
        var uniqueKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (object?[] values in valuesRows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            SqlRowRetentionBudget.RetainForExecution(values);
            SqlRoutineRuntime.FireBeforeTriggers(database, insertTriggers, schema, null, values, transaction);
            DecimalDeclarationValidator.NormalizeForWrite(schema, values);
            ValidateRequiredColumns(schema, values);
            insertBudget.Add(null, values);
            if (schema.PrimaryKey.Count != 0
                && !primaryKeys.Add(Convert.ToHexString(TableKeyCodec.EncodePrimaryKey(schema, values))))
                throw new TableConstraintException(TableConstraintException.UniqueViolation, schema.Name,
                    "PRIMARY KEY", "REPLACE 批次输入包含重复主键，整批未替换。");
            foreach (TableIndex index in schema.Indexes)
            {
                if (!index.IsUnique)
                    continue;
                byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(index, values, schema, ReadOnlySpan<byte>.Empty);
                if (key is not null && !uniqueKeys.Add(Convert.ToHexString(key)))
                    throw new TableConstraintException(TableConstraintException.UniqueViolation, schema.Name,
                        index.Name, "REPLACE 批次输入包含重复唯一键，整批未替换。");
            }
        }

        var oldRows = new List<TableRow>();
        var seenOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (object?[] values in valuesRows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            IReadOnlyList<TableRow> candidates = ReplacementVisibleRows(schema,
                store.FindReplacementConflicts(values), transaction);
            foreach (TableRow row in candidates)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (!IsReplacementConflict(schema, values, row)
                    || !seenOwners.Add(Convert.ToHexString(row.PrimaryKey.Span)))
                    continue;
                deleteBudget.Add(row.Values, null);
                SqlRowRetentionBudget.RetainForExecution(row.Values);
                oldRows.Add(row);
            }
        }
        oldRows.Sort(static (left, right) => left.PrimaryKey.Span.SequenceCompareTo(right.PrimaryKey.Span));
        transaction.MarkReplacement(store.RowLockIdentity);
        return new ReplacementPlan(schema, valuesRows, oldRows, returning);
    }

    /// <summary>先缓冲全部冲突删除和外键动作，重插前的 DELETE 触发器可看到真实删除状态。</summary>
    internal static IReadOnlyList<TableRowChange> QueueReplacementDeletes(
        Tsdb database, SqlTransactionContext transaction, ReplacementPlan plan)
    {
        var changes = new List<TableRowChange>(plan.OldRows.Count);
        foreach (TableRow row in plan.OldRows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            transaction.AddOrMergeTableMutation(plan.Schema, new TableRowMutation(
                ExtractPrimaryKeyValues(plan.Schema, row.Values), NewValues: null,
                ExtractRowVersion(plan.Schema, row.Values))
            {
                ExpectedRowState = TableRowCodec.Encode(plan.Schema, row.Values),
                EncodedRowIdentity = plan.Schema.PrimaryKey.Count == 0 ? row.PrimaryKey.ToArray() : null,
            });
            changes.Add(new TableRowChange(plan.Schema, row.Values, NewValues: null));
        }
        database.Tables.ApplyReplacementDeleteActions(transaction, plan.Schema, plan.OldRows);
        return changes;
    }

    /// <summary>按完整 INSERT 新行进入同一事务；影响数按输入的新行计，不累加内部删除数。</summary>
    internal static InsertExecutionResult QueueReplacementInserts(
        SqlTransactionContext transaction, ReplacementPlan plan, out IReadOnlyList<TableRowChange> changes)
    {
        var inserted = new List<object?[]>(plan.Rows.Count);
        var rowChanges = new List<TableRowChange>(plan.Rows.Count);
        foreach (object?[] values in plan.Rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            object?[] owned = values.ToArray();
            transaction.AddOrMergeTableMutation(plan.Schema, new TableRowMutation(PrimaryKeyValues: null, owned));
            inserted.Add(owned);
            rowChanges.Add(new TableRowChange(plan.Schema, OldValues: null, owned));
        }
        changes = rowChanges;
        return CreateInsertResult(plan.Schema.Name, inserted.Count, plan.Returning, inserted);
    }

    internal static IReadOnlyList<TableRow> ReplacementVisibleRows(
        TableSchema schema, IReadOnlyList<TableRow> committed, SqlTransactionContext transaction)
        => transaction.TryGetBufferedMutations(schema, out var buffered)
            ? ApplyMutationOverlay(schema, committed, buffered) : committed;

    private static bool IsReplacementConflict(TableSchema schema, object?[] candidate, TableRow existing)
    {
        if (schema.PrimaryKey.Count != 0
            && TableKeyCodec.EncodePrimaryKey(schema, candidate).AsSpan().SequenceEqual(existing.PrimaryKey.Span))
            return true;
        foreach (TableIndex index in schema.Indexes)
        {
            if (!index.IsUnique)
                continue;
            byte[]? candidateKey = TableIndexCodec.TryEncodeIndexEntryKey(index, candidate, schema, ReadOnlySpan<byte>.Empty);
            if (candidateKey is null)
                continue;
            byte[]? existingKey = TableIndexCodec.TryEncodeIndexEntryKey(index, existing.Values, schema, existing.PrimaryKey.Span);
            if (existingKey is not null && candidateKey.AsSpan().SequenceEqual(existingKey))
                return true;
        }
        return false;
    }

    internal sealed record ReplacementPlan(
        TableSchema Schema, IReadOnlyList<object?[]> Rows, IReadOnlyList<TableRow> OldRows,
        IReadOnlyList<TableColumn> Returning);
}
