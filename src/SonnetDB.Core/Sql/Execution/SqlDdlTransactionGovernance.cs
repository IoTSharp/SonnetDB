using SonnetDB.Engine;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>在私有关系 workspace 执行前复用原数据库的名称、依赖与 UDF 治理。</summary>
internal static class SqlDdlTransactionGovernance
{
    internal static void Validate(Tsdb source, SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(statement);
        if (statement is CreateTableStatement create)
        {
            SqlSourceNameResolver.EnsureAvailable(source, create.Name, SqlSourceKind.Table);
            if (source.Graphs.Catalog.TryGet(create.Name) is not null
                || source.Graphs.PropertyGraphs.TryGet(create.Name) is not null)
                throw new InvalidOperationException($"无法创建 table '{create.Name}'：同名 graph 已存在。");
        }

        if (TryMutationTarget(statement, out string? tableName, out string operation))
        {
            source.Modbus.EnsureTableCanMutate(tableName, operation);
            IReadOnlyList<SonnetDB.Graphs.PropertyGraphDefinition> graphs =
                source.Graphs.PropertyGraphs.FindDependents(tableName);
            if (graphs.Count != 0)
                throw new InvalidOperationException(
                    $"无法执行 {operation}：property graph '{string.Join("', '", graphs.Select(static graph => graph.Name))}' 依赖 table '{tableName}'。");
            SqlRoutineRuntime.EnsureNoDependents(source, tableName, operation);
            var views = source.Views.FindDependents(tableName);
            var materialized = source.MaterializedViews.FindDependents(tableName);
            if (views.Count != 0 || materialized.Count != 0)
            {
                string dependents = string.Join("', '", views.Select(static view => view.Name)
                    .Concat(materialized.Select(static view => view.Name)).Order(StringComparer.Ordinal));
                throw new InvalidOperationException(
                    $"无法执行 {operation}：view/materialized view '{dependents}' 依赖对象 '{tableName}'。");
            }
        }

        foreach (SqlExpression expression in Expressions(statement))
        {
            if (ContainsUserScalarFunction(source, expression))
                throw new NotSupportedException(
                    "关系 DDL 事务 workspace 暂不执行 UDF 默认值或 CHECK；请改用内置确定性表达式。");
        }
    }

    private static bool TryMutationTarget(SqlStatement statement, out string tableName, out string operation)
    {
        (tableName, operation) = statement switch
        {
            DropTableStatement drop => (drop.Name, "DROP TABLE"),
            AlterTableAddColumnStatement alter => (alter.TableName, "ALTER TABLE"),
            AlterTableAlterPrimaryKeyStatement alter => (alter.TableName, "ALTER TABLE"),
            AlterTableAutoIncrementStatement alter => (alter.TableName, "ALTER TABLE"),
            AlterTableAlterColumnStatement alter => (alter.TableName, "ALTER TABLE"),
            AlterTableAddForeignKeyStatement alter => (alter.TableName, "ALTER TABLE"),
            AlterTableAddCheckConstraintStatement alter => (alter.TableName, "ALTER TABLE"),
            AlterTableDropColumnStatement alter => (alter.TableName, "ALTER TABLE"),
            AlterTableDropConstraintStatement alter => (alter.TableName, "ALTER TABLE"),
            AlterTableRenameColumnStatement alter => (alter.TableName, "ALTER TABLE"),
            AlterTableRenameTableStatement alter => (alter.OldTableName, "ALTER TABLE RENAME"),
            _ => (string.Empty, string.Empty),
        };
        return tableName.Length != 0;
    }

    private static IEnumerable<SqlExpression> Expressions(SqlStatement statement)
    {
        if (statement is CreateTableStatement create)
        {
            foreach (TableColumnDefinition column in create.Columns)
                if (column.DefaultExpression is not null) yield return column.DefaultExpression;
            foreach (TableCheckConstraintClause check in create.CheckConstraintClauses)
                yield return check.Expression;
        }
        else if (statement is AlterTableAddColumnStatement { DefaultExpression: { } add })
            yield return add;
        else if (statement is AlterTableAlterColumnStatement { DefaultExpression: { } alter })
            yield return alter;
        else if (statement is AlterTableAddCheckConstraintStatement check)
            yield return check.Expression;
    }

    private static bool ContainsUserScalarFunction(Tsdb source, SqlExpression expression)
    {
        switch (expression)
        {
            case FunctionCallExpression function:
                return source.Functions.TryGetScalar(function.Name, out _)
                    || function.Arguments.Any(argument => ContainsUserScalarFunction(source, argument));
            case UnaryExpression unary:
                return ContainsUserScalarFunction(source, unary.Operand);
            case CastExpression cast:
                return ContainsUserScalarFunction(source, cast.Operand);
            case BinaryExpression binary:
                return ContainsUserScalarFunction(source, binary.Left)
                    || ContainsUserScalarFunction(source, binary.Right);
            case CaseExpression @case:
                return @case.WhenClauses.Any(clause =>
                        ContainsUserScalarFunction(source, clause.Condition)
                        || ContainsUserScalarFunction(source, clause.Result))
                    || @case.Else is not null && ContainsUserScalarFunction(source, @case.Else);
            case IsNullExpression isNull:
                return ContainsUserScalarFunction(source, isNull.Operand);
            case InExpression @in:
                return ContainsUserScalarFunction(source, @in.Value)
                    || @in.Values.Any(value => ContainsUserScalarFunction(source, value));
            default:
                return false;
        }
    }
}
