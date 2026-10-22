using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    /// <summary>在重插之前完成替换删除的外键动作，避免同主键净变更合并掩盖真实删除。</summary>
    internal void ApplyReplacementDeleteActions(
        SqlTransactionContext transaction, TableSchema principalSchema, IReadOnlyList<TableRow> deletedRows)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(principalSchema);
        ArgumentNullException.ThrowIfNull(deletedRows);
        if (deletedRows.Count == 0)
            return;
        ExecuteLocked(() =>
        {
            transaction.ValidateMutationSchemas(Catalog);
            IReadOnlyList<TableSchema> schemas = Catalog.Snapshot();
            var references = new Dictionary<string, List<CascadeForeignKeyLookup>>(StringComparer.Ordinal);
            foreach (TableSchema child in schemas)
            {
                foreach (TableForeignKey key in child.ForeignKeys)
                {
                    SqlExecutor.ThrowIfCancellationRequested();
                    TableSchema parent = Catalog.TryGet(key.PrincipalTable)
                        ?? throw new InvalidDataException($"外键 '{key.Name}' 的父表不存在。");
                    TableIndex? index = child.Indexes.FirstOrDefault(candidate => candidate.JsonPath is null
                        && candidate.Columns.SequenceEqual(key.Columns, StringComparer.Ordinal));
                    if (!references.TryGetValue(parent.Name, out var keys))
                        references.Add(parent.Name, keys = []);
                    keys.Add(new CascadeForeignKeyLookup(parent, child, key, index));
                }
            }

            var pending = new Queue<(TableSchema Schema, TableRow Row)>();
            var visited = new HashSet<(string Table, string Key)>();
            foreach (TableRow row in deletedRows)
                pending.Enqueue((principalSchema, row));
            using var budget = new TriggerTransitionBudget(enabled: true);
            int maximum = RoutineExecutionContext.Current?.Options.MaxTriggerTransitionRows
                ?? SqlExecutionOptions.Default.MaxTriggerTransitionRows;
            for (int processed = 0; processed < maximum && pending.Count != 0; processed++)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                var (parent, row) = pending.Dequeue();
                if (parent.PrimaryKey.Count == 0
                    || !visited.Add((parent.Name, Convert.ToHexString(row.PrimaryKey.Span)))
                    || !references.TryGetValue(parent.Name, out var lookups))
                    continue;
                IReadOnlyList<object?> parentKey = ExtractPrimaryKeyValues(parent, row);
                foreach (CascadeForeignKeyLookup lookup in lookups)
                {
                    var child = lookup.ChildSchema;
                    var key = lookup.ForeignKey;
                    IReadOnlyList<TableRow> children = TableSqlExecutor.ReplacementVisibleRows(child,
                        lookup.FindRows(OpenStoreLocked(child), parentKey, null, transaction), transaction);
                    foreach (TableRow childRow in children)
                    {
                        SqlExecutor.ThrowIfCancellationRequested();
                        IReadOnlyList<object?>? childKey = ExtractForeignKeyValues(child, childRow, key);
                        if (childKey is null || !ValuesEqual(childKey, parentKey))
                            continue;
                        if (key.OnDelete is not (ForeignKeyAction.Cascade or ForeignKeyAction.SetNull))
                            throw ForeignKeyDeleteViolation(child, key);
                        object?[]? replacement = null;
                        if (key.OnDelete == ForeignKeyAction.SetNull)
                        {
                            replacement = childRow.Values.ToArray();
                            foreach (string columnName in key.Columns)
                                replacement[child.TryGetColumn(columnName)!.Ordinal] = null;
                        }
                        budget.Add(childRow.Values, replacement);
                        SqlRowRetentionBudget.RetainForExecution(replacement is null ? childRow.Values : replacement);
                        transaction.AddOrMergeTableMutation(child, new TableRowMutation(
                            ExtractPrimaryKeyValues(child, childRow), replacement)
                        {
                            ExpectedRowState = TableRowCodec.Encode(child, childRow.Values),
                            EncodedRowIdentity = child.PrimaryKey.Count == 0 ? childRow.PrimaryKey.ToArray() : null,
                        });
                        if (replacement is null)
                            pending.Enqueue((child, childRow));
                    }
                }
            }
            if (pending.Count != 0)
                throw new RoutineExecutionException(RoutineErrorCodes.TransitionLimit,
                    "REPLACE 的外键删除动作超过行数预算，语句已拒绝。");
            return 0;
        });
    }
}
