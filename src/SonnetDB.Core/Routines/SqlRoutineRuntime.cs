using System.Diagnostics;
using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Routines;

internal static class SqlRoutineRuntime
{
    public static ProcedureDefinition CreateProcedure(Tsdb tsdb, CreateProcedureStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        EnsureOutsideTransaction("CREATE PROCEDURE");
        if (tsdb.Routines.TryGetProcedure(statement.Name) is not null)
            throw new InvalidOperationException($"procedure '{statement.Name}' 已存在。");

        ProcedureDefinition definition = ProcedureDefinition.Create(statement);
        if (definition.ProcedureDependencies.Contains(definition.Name, StringComparer.Ordinal))
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.RecursiveCall,
                $"procedure '{definition.Name}' 不能调用自身。");
        }
        ValidateProcedureDependencies(tsdb, definition);
        tsdb.Routines.Create(definition);
        return definition;
    }

    public static TriggerDefinition CreateTrigger(Tsdb tsdb, CreateTriggerStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        EnsureOutsideTransaction("CREATE TRIGGER");
        if (tsdb.Routines.TryGetTrigger(statement.Name) is not null)
            throw new InvalidOperationException($"trigger '{statement.Name}' 已存在。");

        TableSchema schema = tsdb.Tables.Catalog.TryGet(statement.TableName)
            ?? throw DependencyError($"trigger '{statement.Name}' 的目标关系表 '{statement.TableName}' 不存在。");
        TriggerDefinition definition = TriggerDefinition.Create(statement);
        ValidateTriggerDependencies(tsdb, definition, schema);
        tsdb.Routines.Create(definition);
        return definition;
    }

    public static RowsAffectedExecutionResult DropProcedure(Tsdb tsdb, DropProcedureStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        EnsureOutsideTransaction("DROP PROCEDURE");
        var callers = tsdb.Routines.FindProceduresCalling(statement.Name);
        if (callers.Count != 0)
        {
            throw DependencyError(
                $"无法删除 procedure '{statement.Name}'：procedure "
                + $"'{string.Join("', '", callers.Select(static value => value.Name))}' 仍调用它。");
        }

        bool removed = tsdb.Routines.DropProcedure(statement.Name);
        if (!removed && !statement.IfExists)
            throw new RoutineExecutionException(RoutineErrorCodes.ProcedureNotFound, $"procedure '{statement.Name}' 不存在。");
        return new RowsAffectedExecutionResult(statement.Name, removed ? 1 : 0, "drop_procedure");
    }

    public static RowsAffectedExecutionResult DropTrigger(Tsdb tsdb, DropTriggerStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        EnsureOutsideTransaction("DROP TRIGGER");
        bool removed = tsdb.Routines.DropTrigger(statement.Name);
        if (!removed && !statement.IfExists)
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.TriggerNotFound,
                $"trigger '{statement.Name}' 不存在。");
        }
        return new RowsAffectedExecutionResult(statement.Name, removed ? 1 : 0, "drop_trigger");
    }

    public static object? ExecuteCall(
        Tsdb tsdb,
        string? databaseName,
        CallProcedureStatement statement,
        IControlPlane? controlPlane,
        SqlTransactionContext? transaction)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ProcedureDefinition definition = tsdb.Routines.TryGetProcedure(statement.Name)
            ?? throw new RoutineExecutionException(
                RoutineErrorCodes.ProcedureNotFound,
                $"procedure '{statement.Name}' 不存在。");
        RoutineExecutionContext context = RoutineExecutionContext.Current
            ?? throw new InvalidOperationException("过程运行时缺少 SQL 执行上下文。");
        bool requiresWrite = RequiresWrite(tsdb, definition);
        if (requiresWrite && !context.Options.CanWrite)
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.Forbidden,
                $"调用 procedure '{definition.Name}' 需要当前数据库写权限。");
        }

        SqlParameters parameters = BindArguments(definition, statement.Arguments);
        SqlTransactionContext? effectiveTransaction = transaction;
        bool ownsTransaction = false;
        if (requiresWrite && effectiveTransaction is null)
        {
            effectiveTransaction = new SqlTransactionContext();
            ownsTransaction = true;
        }
        SqlTransactionContext.Savepoint? savepoint = effectiveTransaction?.CreateSavepoint();

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        long startedTimestamp = Stopwatch.GetTimestamp();
        int initialStatements = context.StatementsExecuted;
        int initialRows = context.ResultRows;
        bool succeeded = false;
        string? errorCode = null;
        string callChain = string.Empty;
        try
        {
            using var callScope = context.EnterProcedure(definition.Name);
            callChain = context.CallChain;
            object? lastResult = null;
            foreach (var bodyStatement in definition.Statements)
            {
                context.ConsumeStatement();
                SqlStatement bound = SqlParameterBinder.Bind(bodyStatement, parameters);
                lastResult = SqlExecutor.ExecuteStatement(
                    tsdb,
                    databaseName,
                    bound,
                    controlPlane,
                    effectiveTransaction);
                if (lastResult is SelectExecutionResult select)
                    context.AddResultRows(select.Rows.Count);
            }

            if (ownsTransaction)
                TableSqlExecutor.CommitTransaction(tsdb, effectiveTransaction!);
            succeeded = true;
            return lastResult;
        }
        catch (Exception exception)
        {
            var routineException = NormalizeExecutionException(definition.Name, exception);
            errorCode = routineException.Code;
            if (effectiveTransaction is not null && savepoint is not null)
            {
                tsdb.Routines.Diagnostics.MarkTriggerTransactionFailure(
                    effectiveTransaction.SnapshotTriggerAuditSequencesSince(savepoint),
                    routineException.Code);
                if (!ownsTransaction)
                    effectiveTransaction.RollbackTo(savepoint);
            }
            throw routineException;
        }
        finally
        {
            tsdb.Routines.Diagnostics.Record(
                "procedure",
                definition.Name,
                context.Options.Caller,
                string.IsNullOrEmpty(callChain) ? "procedure:" + definition.Name : callChain,
                startedUtc,
                Stopwatch.GetElapsedTime(startedTimestamp),
                succeeded,
                errorCode,
                context.StatementsExecuted - initialStatements,
                context.ResultRows - initialRows);
        }
    }

    public static IReadOnlyList<long> FireTriggers(
        Tsdb tsdb,
        string? databaseName,
        SqlTriggerEvent triggerEvent,
        IReadOnlyList<TableRowChange> changes,
        IControlPlane? controlPlane,
        SqlTransactionContext transaction)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(transaction);
        if (changes.Count == 0)
            return [];

        RoutineExecutionContext context = RoutineExecutionContext.Current
            ?? throw new InvalidOperationException("触发器运行时缺少 SQL 执行上下文。");
        IReadOnlyList<TriggerDefinition> triggers = tsdb.Routines.FindTriggers(changes[0].Schema.Name, triggerEvent);
        if (triggers.Count == 0)
            return [];

        var successfulAuditSequences = new List<long>();
        try
        {
            foreach (var change in changes)
            {
                var rowContext = new RoutineRowContext(change.Schema, change.OldValues, change.NewValues);
                foreach (var trigger in triggers)
                {
                    DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
                    long startedTimestamp = Stopwatch.GetTimestamp();
                    int initialStatements = context.StatementsExecuted;
                    int initialRows = context.ResultRows;
                    bool actionExecuted = false;
                    bool succeeded = false;
                    string? errorCode = null;
                    string callChain = string.Empty;
                    try
                    {
                        using var triggerScope = context.EnterTrigger(trigger.Name);
                        callChain = context.CallChain;
                        if (trigger.When is not null)
                        {
                            SqlExpression boundWhen = RoutineRowBinder.BindExpression(trigger.When, rowContext);
                            if (!RoutineExpressionEvaluator.EvaluateWhen(boundWhen))
                            {
                                succeeded = true;
                                continue;
                            }
                        }

                        actionExecuted = true;
                        foreach (var bodyStatement in trigger.Statements)
                        {
                            context.ConsumeStatement();
                            SqlStatement bound = RoutineRowBinder.Bind(bodyStatement, rowContext);
                            _ = SqlExecutor.ExecuteStatement(
                                tsdb,
                                databaseName,
                                bound,
                                controlPlane,
                                transaction);
                        }
                        succeeded = true;
                    }
                    catch (Exception exception)
                    {
                        var routineException = NormalizeTriggerException(trigger.Name, exception);
                        errorCode = routineException.Code;
                        throw routineException;
                    }
                    finally
                    {
                        long auditSequence = tsdb.Routines.Diagnostics.Record(
                            "trigger",
                            trigger.Name,
                            context.Options.Caller,
                            string.IsNullOrEmpty(callChain) ? "trigger:" + trigger.Name : callChain,
                            startedUtc,
                            Stopwatch.GetElapsedTime(startedTimestamp),
                            succeeded,
                            errorCode,
                            context.StatementsExecuted - initialStatements,
                            context.ResultRows - initialRows);
                        if (succeeded && actionExecuted)
                            successfulAuditSequences.Add(auditSequence);
                    }
                }
            }
        }
        catch (RoutineExecutionException exception)
        {
            tsdb.Routines.Diagnostics.MarkTriggerTransactionFailure(
                successfulAuditSequences,
                exception.Code);
            throw;
        }
        return successfulAuditSequences;
    }

    public static void EnsureNoDependents(Tsdb tsdb, string objectName, string operation)
    {
        var procedures = tsdb.Routines.FindProceduresDependingOnObject(objectName);
        var triggers = tsdb.Routines.FindTriggersDependingOnObject(objectName);
        if (procedures.Count == 0 && triggers.Count == 0)
            return;
        string dependents = string.Join(
            "', '",
            procedures.Select(static value => "procedure:" + value.Name)
                .Concat(triggers.Select(static value => "trigger:" + value.Name))
                .OrderBy(static value => value, StringComparer.Ordinal));
        throw DependencyError(
            $"无法执行 {operation}：'{dependents}' 依赖对象 '{objectName}'。");
    }

    private static void ValidateProcedureDependencies(Tsdb tsdb, ProcedureDefinition definition)
    {
        ValidateBodyObjects(tsdb, definition.Statements, definition.Name, "procedure");
        foreach (string dependency in definition.ProcedureDependencies)
        {
            if (string.Equals(dependency, definition.Name, StringComparison.Ordinal))
                throw new RoutineExecutionException(RoutineErrorCodes.RecursiveCall, $"procedure '{definition.Name}' 不能调用自身。");
            if (tsdb.Routines.TryGetProcedure(dependency) is null)
                throw DependencyError($"procedure '{definition.Name}' 调用了不存在的 procedure '{dependency}'。");
        }
    }

    private static void ValidateTriggerDependencies(
        Tsdb tsdb,
        TriggerDefinition definition,
        TableSchema targetSchema)
    {
        ValidateBodyObjects(tsdb, definition.Statements, definition.Name, "trigger");
        foreach (string columnName in definition.RowColumns)
        {
            if (targetSchema.TryGetColumn(columnName) is null)
                throw DependencyError($"trigger '{definition.Name}' 引用了目标表中不存在的列 '{columnName}'。");
        }
        if (definition.When is not null)
            ValidateWhenExpression(definition.When, targetSchema, definition.Name);
    }

    private static void ValidateBodyObjects(
        Tsdb tsdb,
        IReadOnlyList<SqlStatement> statements,
        string ownerName,
        string ownerKind)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case SelectStatement select:
                    ValidateSelectSources(tsdb, select, ownerName, ownerKind);
                    break;
                case InsertStatement insert:
                    EnsureRelationTable(tsdb, ownerKind, ownerName, insert.Measurement);
                    break;
                case UpdateStatement update:
                    EnsureRelationTable(tsdb, ownerKind, ownerName, update.TableName);
                    break;
                case DeleteStatement delete:
                    EnsureRelationTable(tsdb, ownerKind, ownerName, delete.Measurement);
                    break;
            }
        }
    }

    private static void ValidateSelectSources(
        Tsdb tsdb,
        SelectStatement select,
        string ownerName,
        string ownerKind)
    {
        if (select.FromSubquery is not null)
        {
            ValidateSelectSources(tsdb, select.FromSubquery, ownerName, ownerKind);
        }
        else if (select.GraphTable is { } graphTable)
        {
            if (!SqlExecutor.IsKnownGraphSource(tsdb, graphTable.GraphName))
            {
                throw DependencyError(
                    $"{ownerKind} '{ownerName}' 引用了不存在的 graph '{graphTable.GraphName}'。");
            }
        }
        else if (!string.IsNullOrEmpty(select.Measurement)
                 && !string.Equals(select.Measurement, "__json_file__", StringComparison.Ordinal)
                 && !SqlExecutor.IsKnownViewSource(tsdb, select.Measurement))
        {
            throw DependencyError(
                $"{ownerKind} '{ownerName}' 引用了不存在的数据源 '{select.Measurement}'。");
        }

        foreach (var projection in select.Projections)
            ValidateSelectExpressionSources(tsdb, projection.Expression, ownerName, ownerKind);
        if (select.Where is not null)
            ValidateSelectExpressionSources(tsdb, select.Where, ownerName, ownerKind);
        foreach (var expression in select.GroupBy)
            ValidateSelectExpressionSources(tsdb, expression, ownerName, ownerKind);
        if (select.Having is not null)
            ValidateSelectExpressionSources(tsdb, select.Having, ownerName, ownerKind);
        foreach (var orderBy in select.OrderByList)
            ValidateSelectExpressionSources(tsdb, orderBy.Expression, ownerName, ownerKind);
        if (select.Pagination is { } pagination)
        {
            ValidateSelectExpressionSources(tsdb, pagination.OffsetExpression, ownerName, ownerKind);
            if (pagination.FetchExpression is not null)
                ValidateSelectExpressionSources(tsdb, pagination.FetchExpression, ownerName, ownerKind);
        }
        if (select.TableValuedFunction is not null)
            ValidateSelectExpressionSources(tsdb, select.TableValuedFunction, ownerName, ownerKind);
        if (select.GraphTable is { } graphSource)
        {
            if (graphSource.Predicate is not null)
                ValidateSelectExpressionSources(tsdb, graphSource.Predicate, ownerName, ownerKind);
            foreach (var column in graphSource.Columns)
                ValidateSelectExpressionSources(tsdb, column.Expression, ownerName, ownerKind);
        }
        foreach (var join in select.JoinClauses)
        {
            if (join.Subquery is null)
            {
                if (!SqlExecutor.IsKnownViewSource(tsdb, join.TableName))
                {
                    throw DependencyError(
                        $"{ownerKind} '{ownerName}' 引用了不存在的数据源 '{join.TableName}'。");
                }
            }
            else
            {
                ValidateSelectSources(tsdb, join.Subquery, ownerName, ownerKind);
            }
            ValidateSelectExpressionSources(tsdb, join.On, ownerName, ownerKind);
        }
        foreach (var union in select.UnionStatements)
            ValidateSelectSources(tsdb, union, ownerName, ownerKind);
    }

    private static void ValidateSelectExpressionSources(
        Tsdb tsdb,
        SqlExpression expression,
        string ownerName,
        string ownerKind)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                ValidateSelectExpressionSources(tsdb, binary.Left, ownerName, ownerKind);
                ValidateSelectExpressionSources(tsdb, binary.Right, ownerName, ownerKind);
                break;
            case UnaryExpression unary:
                ValidateSelectExpressionSources(tsdb, unary.Operand, ownerName, ownerKind);
                break;
            case IsNullExpression isNull:
                ValidateSelectExpressionSources(tsdb, isNull.Operand, ownerName, ownerKind);
                break;
            case InExpression @in:
                ValidateSelectExpressionSources(tsdb, @in.Value, ownerName, ownerKind);
                foreach (var value in @in.Values)
                    ValidateSelectExpressionSources(tsdb, value, ownerName, ownerKind);
                if (@in.Subquery is not null)
                    ValidateSelectSources(tsdb, @in.Subquery, ownerName, ownerKind);
                break;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    ValidateSelectExpressionSources(tsdb, argument, ownerName, ownerKind);
                break;
            case NamedArgumentExpression named:
                ValidateSelectExpressionSources(tsdb, named.Value, ownerName, ownerKind);
                break;
            case CaseExpression @case:
                foreach (var clause in @case.WhenClauses)
                {
                    ValidateSelectExpressionSources(tsdb, clause.Condition, ownerName, ownerKind);
                    ValidateSelectExpressionSources(tsdb, clause.Result, ownerName, ownerKind);
                }
                if (@case.Else is not null)
                    ValidateSelectExpressionSources(tsdb, @case.Else, ownerName, ownerKind);
                break;
            case SubqueryExpression subquery:
                ValidateSelectSources(tsdb, subquery.Select, ownerName, ownerKind);
                break;
            case ExistsExpression exists:
                ValidateSelectSources(tsdb, exists.Select, ownerName, ownerKind);
                break;
        }
    }

    private static void EnsureRelationTable(Tsdb tsdb, string ownerKind, string ownerName, string target)
    {
        if (tsdb.Tables.Catalog.TryGet(target) is null)
        {
            throw DependencyError(
                $"{ownerKind} '{ownerName}' 的写语句目标 '{target}' 必须是已存在的关系表；"
                + "首版不允许对 measurement 或 document collection 执行例程事务写入。");
        }
    }

    private static void ValidateWhenExpression(SqlExpression expression, TableSchema schema, string triggerName)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                if (identifier.Qualifier is null
                    || (!string.Equals(identifier.Qualifier, "OLD", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(identifier.Qualifier, "NEW", StringComparison.OrdinalIgnoreCase)))
                {
                    throw DependencyError($"trigger '{triggerName}' 的 WHEN 标识符必须显式使用 OLD.column 或 NEW.column。");
                }
                if (schema.TryGetColumn(identifier.Name) is null)
                    throw DependencyError($"trigger '{triggerName}' 的 WHEN 引用了未知列 '{identifier.Name}'。");
                break;
            case BinaryExpression binary:
                ValidateWhenExpression(binary.Left, schema, triggerName);
                ValidateWhenExpression(binary.Right, schema, triggerName);
                break;
            case UnaryExpression unary:
                ValidateWhenExpression(unary.Operand, schema, triggerName);
                break;
            case IsNullExpression isNull:
                ValidateWhenExpression(isNull.Operand, schema, triggerName);
                break;
            case InExpression @in:
                ValidateWhenExpression(@in.Value, schema, triggerName);
                foreach (var value in @in.Values)
                    ValidateWhenExpression(value, schema, triggerName);
                if (@in.Subquery is not null)
                    throw DependencyError($"trigger '{triggerName}' 的 WHEN 不允许子查询。");
                break;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    ValidateWhenExpression(argument, schema, triggerName);
                break;
            case CaseExpression @case:
                foreach (var clause in @case.WhenClauses)
                {
                    ValidateWhenExpression(clause.Condition, schema, triggerName);
                    ValidateWhenExpression(clause.Result, schema, triggerName);
                }
                if (@case.Else is not null)
                    ValidateWhenExpression(@case.Else, schema, triggerName);
                break;
            case SubqueryExpression or ExistsExpression or ParameterExpression:
                throw DependencyError($"trigger '{triggerName}' 的 WHEN 不允许子查询或参数。");
        }
    }

    private static SqlParameters BindArguments(
        ProcedureDefinition definition,
        IReadOnlyList<SqlExpression> arguments)
    {
        if (arguments.Count != definition.Parameters.Count)
        {
            throw new RoutineExecutionException(
                RoutineErrorCodes.InvalidArguments,
                $"procedure '{definition.Name}' 需要 {definition.Parameters.Count} 个参数，实际收到 {arguments.Count} 个。");
        }

        var result = new SqlParameters();
        for (int index = 0; index < arguments.Count; index++)
        {
            object? value;
            try
            {
                value = ConvertArgument(
                    RoutineExpressionEvaluator.Evaluate(arguments[index]),
                    definition.Parameters[index].DataType);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
            {
                throw new RoutineExecutionException(
                    RoutineErrorCodes.InvalidArguments,
                    $"procedure '{definition.Name}' 参数 '{definition.Parameters[index].Name}' 类型不匹配。",
                    exception);
            }
            result.AddNamed(definition.Parameters[index].Name, value);
        }
        return result;
    }

    private static object? ConvertArgument(object? value, SqlProcedureParameterType dataType)
    {
        if (value is null)
            return null;
        return dataType switch
        {
            SqlProcedureParameterType.Int64 when IsNumeric(value) => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            SqlProcedureParameterType.Float64 when IsNumeric(value) => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            SqlProcedureParameterType.Boolean when value is bool boolean => boolean,
            SqlProcedureParameterType.String when value is string text => text,
            _ => throw new InvalidOperationException($"值类型 {value.GetType().Name} 与参数类型 {dataType} 不兼容。"),
        };
    }

    private static bool IsNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    internal static bool RequiresWrite(Tsdb tsdb, ProcedureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(definition);
        return RequiresWriteCore(tsdb, definition, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool RequiresWriteCore(
        Tsdb tsdb,
        ProcedureDefinition definition,
        HashSet<string> visited)
    {
        if (!visited.Add(definition.Name))
            return false;
        if (definition.RequiresWrite)
            return true;
        foreach (string dependency in definition.ProcedureDependencies)
        {
            var called = tsdb.Routines.TryGetProcedure(dependency);
            if (called is not null && RequiresWriteCore(tsdb, called, visited))
                return true;
        }
        return false;
    }

    private static RoutineExecutionException NormalizeExecutionException(string name, Exception exception)
        => exception as RoutineExecutionException
           ?? new RoutineExecutionException(
               RoutineErrorCodes.ExecutionFailed,
               $"procedure '{name}' 执行失败：{exception.Message}",
               exception);

    private static RoutineExecutionException NormalizeTriggerException(string name, Exception exception)
        => exception as RoutineExecutionException
           ?? new RoutineExecutionException(
               RoutineErrorCodes.ExecutionFailed,
               $"trigger '{name}' 执行失败：{exception.Message}",
               exception);

    private static RoutineExecutionException DependencyError(string message)
        => new(RoutineErrorCodes.Dependency, message);

    private static void EnsureOutsideTransaction(string operation)
    {
        if (SqlTransactionContext.Current is not null)
            throw new InvalidOperationException($"{operation} 不能在活动轻事务内执行。");
    }
}
