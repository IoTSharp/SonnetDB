using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>关系表原子替换：完整新行计划、真实删除阶段、真实插入阶段共用一个事务。</summary>
internal static class SqlReplaceExecutor
{
    internal static InsertExecutionResult Execute(
        Tsdb database, string? databaseName, InsertStatement statement, TableSchema schema,
        IControlPlane? controlPlane, SqlTransactionContext? transaction)
    {
        IReadOnlyList<TriggerDefinition> insertTriggers = database.Routines.FindTriggers(schema.Name, SqlTriggerEvent.Insert);
        IReadOnlyList<TriggerDefinition> deleteTriggers = database.Routines.FindTriggers(schema.Name, SqlTriggerEvent.Delete);
        using var transitionBudget = new TriggerTransitionBudget(enabled: true);
        bool ownsTransaction = transaction is null;
        SqlTransactionContext effective = transaction ?? new SqlTransactionContext();
        SqlTransactionContext.Savepoint savepoint = effective.CreateSavepoint();
        using var scope = SqlTransactionContext.EnterScope(effective);
        try
        {
            return TableLockOperation.Run(database.Tables.RowLocks, () =>
            {
                // 冲突集合与删除阶段不能被并发写入插入幻影；先取锁再执行 DEFAULT 或触发器。
                TableLockOperation.Require(database.Tables.RowLocks, [TableRowLockManager.Resource.DatabaseExclusive]);
                var plan = TableSqlExecutor.PrepareReplacement(database, effective, statement, schema,
                    insertTriggers, transitionBudget, transitionBudget);
                IReadOnlyList<TableRowChange> deleted = TableSqlExecutor.QueueReplacementDeletes(database, effective, plan);
                if (deleted.Count != 0)
                    SqlRoutineRuntime.FireTriggers(database, databaseName, deleteTriggers, deleted, controlPlane, effective);
                InsertExecutionResult result = TableSqlExecutor.QueueReplacementInserts(effective, plan, out var inserted);
                SqlRoutineRuntime.FireTriggers(database, databaseName, insertTriggers, inserted, controlPlane, effective);
                RoutineExecutionContext.Current?.CheckCancellation();
                if (ownsTransaction)
                    TableSqlExecutor.CommitTransaction(database, effective);
                return result;
            });
        }
        catch (Exception exception)
        {
            effective.ResolveRoutineInvocations(database.Routines.Diagnostics, committed: false,
                SqlRoutineRuntime.GetErrorCode(exception), savepoint);
            if (!effective.IsCompleted)
            {
                if (ownsTransaction)
                    effective.MarkCompleted();
                else
                    effective.RollbackTo(savepoint);
            }
            if ((insertTriggers.Count != 0 || deleteTriggers.Count != 0) && exception is not RoutineExecutionException)
                throw new RoutineExecutionException(SqlRoutineRuntime.GetErrorCode(exception),
                    $"REPLACE 触发器事务失败：{exception.Message}", exception);
            throw;
        }
    }
}
