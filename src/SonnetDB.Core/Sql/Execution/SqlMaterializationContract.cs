using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Views;

namespace SonnetDB.Sql.Execution;

/// <summary>在执行前验证显式物化预算当前支持的关系查询、直接 measurement 查询和关系 DML 范围。</summary>
internal static class SqlMaterializationContract
{
    internal static void Validate(Tsdb tsdb, SqlStatement statement)
    {
        switch (statement)
        {
            case SelectStatement select:
                ValidateQuery(select, new HashSet<string>(StringComparer.OrdinalIgnoreCase), allowMeasurement: true);
                return;
            case ExplainStatement { Statement: SelectStatement explainSelect }:
                ValidateQuery(explainSelect, new HashSet<string>(StringComparer.OrdinalIgnoreCase), allowMeasurement: true);
                return;
            case InsertStatement insert:
                ValidateInsert(insert);
                return;
            case UpdateStatement update:
                ValidateUpdate(update);
                return;
            case DeleteStatement delete:
                ValidateDelete(delete);
                return;
            default:
                throw new NotSupportedException(
                    "SQL 物化预算仅支持关系 SELECT、EXPLAIN SELECT 和关系表 DML；例程、DDL 与事务控制须使用未启用该预算的调用。");
        }

        void ValidateInsert(InsertStatement insert)
        {
            ValidateTableTarget(insert.Measurement);
            foreach (IReadOnlyList<SqlExpression> row in insert.Rows)
                foreach (SqlExpression expression in row)
                    ValidateDmlExpression(expression, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (insert.OnConflict is { } conflict)
            {
                foreach (UpdateAssignment assignment in conflict.UpdateAssignments)
                    ValidateDmlExpression(assignment.Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                ValidateDmlExpression(conflict.UpdateWhere, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            if (insert.Query is { } query)
                ValidateQuery(query, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        void ValidateUpdate(UpdateStatement update)
        {
            ValidateTableTarget(update.TableName);
            foreach (UpdateAssignment assignment in update.Assignments)
                ValidateDmlExpression(assignment.Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            ValidateDmlExpression(update.Where, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (JoinClause join in update.FromClauses)
            {
                if (join.Subquery is { } subquery)
                    ValidateQuery(subquery, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                else
                    ValidateTableTarget(join.TableName);
                ValidateDmlExpression(join.On, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        void ValidateDelete(DeleteStatement delete)
        {
            ValidateTableTarget(delete.Measurement);
            ValidateDmlExpression(delete.Where, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        void ValidateTableTarget(string name)
        {
            if (tsdb.Tables.Catalog.TryGet(name) is null)
                throw new NotSupportedException(
                    "SQL 物化预算仅支持关系表、关系表视图、CTE 和常量查询源；DML 目标必须是关系表。");
        }

        void ValidateDmlExpression(SqlExpression? expression, HashSet<string> inheritedCtes)
        {
            switch (expression)
            {
                case SubqueryExpression subquery:
                    ValidateQuery(subquery.Select, inheritedCtes);
                    break;
                case ExistsExpression exists:
                    ValidateQuery(exists.Select, inheritedCtes);
                    break;
                case InExpression membership:
                    ValidateDmlExpression(membership.Value, inheritedCtes);
                    foreach (SqlExpression value in membership.Values)
                        ValidateDmlExpression(value, inheritedCtes);
                    if (membership.Subquery is { } membershipQuery)
                        ValidateQuery(membershipQuery, inheritedCtes);
                    break;
                case BinaryExpression binary:
                    ValidateDmlExpression(binary.Left, inheritedCtes);
                    ValidateDmlExpression(binary.Right, inheritedCtes);
                    break;
                case UnaryExpression unary:
                    ValidateDmlExpression(unary.Operand, inheritedCtes);
                    break;
                case CastExpression cast:
                    ValidateDmlExpression(cast.Operand, inheritedCtes);
                    break;
                case IsNullExpression isNull:
                    ValidateDmlExpression(isNull.Operand, inheritedCtes);
                    break;
                case NamedArgumentExpression named:
                    ValidateDmlExpression(named.Value, inheritedCtes);
                    break;
                case CaseExpression conditional:
                    foreach (CaseWhenClause clause in conditional.WhenClauses)
                    {
                        ValidateDmlExpression(clause.Condition, inheritedCtes);
                        ValidateDmlExpression(clause.Result, inheritedCtes);
                    }
                    ValidateDmlExpression(conditional.Else, inheritedCtes);
                    break;
                case FunctionCallExpression function:
                    if (tsdb.Functions.TryGetScalar(function.Name, out _)
                        || tsdb.Functions.TryGetAggregate(function.Name, out _)
                        || tsdb.Functions.TryGetWindow(function.Name, out _))
                    {
                        throw new NotSupportedException("SQL 物化预算不支持用户自定义函数回调。");
                    }
                    foreach (SqlExpression argument in function.Arguments)
                        ValidateDmlExpression(argument, inheritedCtes);
                    if (function.Over is { } window)
                    {
                        foreach (SqlExpression partition in window.PartitionBy)
                            ValidateDmlExpression(partition, inheritedCtes);
                        foreach (OrderBySpec order in window.OrderBy)
                            ValidateDmlExpression(order.Expression, inheritedCtes);
                    }
                    break;
                case VectorLiteralExpression or GeoPointLiteralExpression:
                    throw new NotSupportedException("SQL 物化预算只支持标准 SQL 标量、字符串和二进制值。");
                case DefaultValueExpression or null or LiteralExpression or DurationLiteralExpression
                    or IdentifierExpression or StarExpression:
                    break;
                default:
                    throw new NotSupportedException("SQL 物化预算只支持已经绑定的只读标量表达式。");
            }
        }

        void ValidateQuery(SelectStatement query, HashSet<string> inheritedCtes, bool allowMeasurement = false)
        {
            if (!query.IsRecursive)
                query = CommonTableExpressionExpander.Expand(query);
            if (tsdb.Views.Catalog.Count != 0)
                query = ViewExpander.Expand(tsdb.Views.Catalog, query);
            if (query.TableValuedFunction is not null || query.GraphTable is not null)
                throw new NotSupportedException("SQL 物化预算不支持表值函数或 Graph 查询源。");

            HashSet<string> ctes = inheritedCtes;
            if (query.CommonTableExpressions.Count != 0)
            {
                ctes = new HashSet<string>(inheritedCtes, StringComparer.OrdinalIgnoreCase);
                foreach (CommonTableExpression definition in query.CommonTableExpressions)
                    ctes.Add(definition.Name);
                foreach (CommonTableExpression definition in query.CommonTableExpressions)
                    ValidateQuery(definition.Query, ctes);
            }
            if (query.FromSubquery is { } from)
                ValidateQuery(from, ctes);
            else if (!string.IsNullOrEmpty(query.Measurement))
            {
                if (tsdb.Tables.Catalog.TryGet(query.Measurement) is null
                    && tsdb.Measurements.TryGet(query.Measurement) is not null)
                {
                    if (!allowMeasurement)
                        throw new NotSupportedException("SQL 物化预算尚不支持包含 measurement 的嵌套查询或集合运算。");
                }
                else
                    ValidateTable(query.Measurement, ctes);
            }
            foreach (JoinClause join in query.JoinClauses)
            {
                if (join.Subquery is { } subquery)
                    ValidateQuery(subquery, ctes);
                else
                    ValidateTable(join.TableName, ctes);
                ValidateExpression(join.On, ctes);
            }
            foreach (SqlSetOperation operation in query.SetOperationList)
                ValidateQuery(operation.Query, ctes);
            foreach (SelectItem projection in query.Projections)
                ValidateExpression(projection.Expression, ctes);
            ValidateExpression(query.Where, ctes);
            ValidateExpression(query.Having, ctes);
            foreach (SqlExpression expression in query.GroupBy)
                ValidateExpression(expression, ctes);
            foreach (OrderBySpec order in query.OrderByList)
                ValidateExpression(order.Expression, ctes);
            if (query.Pagination is { } pagination)
            {
                ValidateExpression(pagination.OffsetExpression, ctes);
                ValidateExpression(pagination.FetchExpression, ctes);
            }
            if (query.FromSubquery is null && tsdb.Tables.Catalog.TryGet(query.Measurement) is null
                && tsdb.Measurements.TryGet(query.Measurement) is { } measurementSchema)
                SelectExecutor.ValidateMaterializationSupported(measurementSchema, query);
        }

        void ValidateTable(string name, HashSet<string> ctes)
        {
            if (tsdb.Tables.Catalog.TryGet(name) is null && !ctes.Contains(name)
                && RecursiveCteScope.Find(name) is null)
            {
                throw new NotSupportedException("SQL 物化预算仅支持关系表、关系表视图、CTE 和常量查询源。");
            }
        }

        void ValidateExpression(SqlExpression? expression, HashSet<string> ctes)
        {
            switch (expression)
            {
                case SubqueryExpression subquery:
                    ValidateQuery(subquery.Select, ctes);
                    break;
                case ExistsExpression exists:
                    ValidateQuery(exists.Select, ctes);
                    break;
                case InExpression membership:
                    ValidateExpression(membership.Value, ctes);
                    foreach (SqlExpression value in membership.Values)
                        ValidateExpression(value, ctes);
                    if (membership.Subquery is { } membershipQuery)
                        ValidateQuery(membershipQuery, ctes);
                    break;
                case BinaryExpression binary:
                    ValidateExpression(binary.Left, ctes);
                    ValidateExpression(binary.Right, ctes);
                    break;
                case UnaryExpression unary:
                    ValidateExpression(unary.Operand, ctes);
                    break;
                case CastExpression cast:
                    ValidateExpression(cast.Operand, ctes);
                    break;
                case IsNullExpression isNull:
                    ValidateExpression(isNull.Operand, ctes);
                    break;
                case NamedArgumentExpression named:
                    ValidateExpression(named.Value, ctes);
                    break;
                case CaseExpression conditional:
                    foreach (CaseWhenClause clause in conditional.WhenClauses)
                    {
                        ValidateExpression(clause.Condition, ctes);
                        ValidateExpression(clause.Result, ctes);
                    }
                    ValidateExpression(conditional.Else, ctes);
                    break;
                case FunctionCallExpression function:
                    if (tsdb.Functions.TryGetScalar(function.Name, out _)
                        || tsdb.Functions.TryGetAggregate(function.Name, out _)
                        || tsdb.Functions.TryGetWindow(function.Name, out _))
                    {
                        throw new NotSupportedException("SQL 物化预算不支持用户自定义函数回调。");
                    }
                    foreach (SqlExpression argument in function.Arguments)
                        ValidateExpression(argument, ctes);
                    if (function.Over is { } window)
                    {
                        foreach (SqlExpression partition in window.PartitionBy)
                            ValidateExpression(partition, ctes);
                        foreach (OrderBySpec order in window.OrderBy)
                            ValidateExpression(order.Expression, ctes);
                    }
                    break;
                case VectorLiteralExpression or GeoPointLiteralExpression:
                    throw new NotSupportedException("SQL 物化预算只支持标准 SQL 标量、字符串和二进制值。");
                case null or LiteralExpression or DurationLiteralExpression or IdentifierExpression or StarExpression:
                    break;
                default:
                    throw new NotSupportedException("SQL 物化预算只支持已经绑定的只读标量表达式。");
            }
        }
    }
}
