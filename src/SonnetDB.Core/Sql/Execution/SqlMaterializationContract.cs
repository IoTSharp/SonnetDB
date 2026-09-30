using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Views;

namespace SonnetDB.Sql.Execution;

/// <summary>在执行前验证显式物化预算当前支持的只读关系查询范围。</summary>
internal static class SqlMaterializationContract
{
    internal static void Validate(Tsdb tsdb, SqlStatement statement)
    {
        SelectStatement select = statement switch
        {
            SelectStatement query => query,
            ExplainStatement { Statement: SelectStatement query } => query,
            _ => throw new NotSupportedException(
                "SQL 物化预算仅支持关系 SELECT 和 EXPLAIN SELECT；写入、RETURNING、例程和事务控制须使用未启用该预算的调用。"),
        };
        ValidateQuery(select, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        void ValidateQuery(SelectStatement query, HashSet<string> inheritedCtes)
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
                ValidateTable(query.Measurement, ctes);
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
