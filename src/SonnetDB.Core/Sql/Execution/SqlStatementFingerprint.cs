using System.Security.Cryptography;
using System.Text;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>生成不含参数值和行内容的 SQL 结构 fingerprint。</summary>
internal static class SqlStatementFingerprint
{
    internal static string Create(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var builder = new StringBuilder(256);
        AppendStatement(builder, statement);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendStatement(StringBuilder builder, SqlStatement statement)
    {
        builder.Append(statement.GetType().Name).Append('|');
        switch (statement)
        {
            case SelectStatement select:
                builder.Append(select.Measurement).Append('|')
                    .Append(select.TableAlias).Append('|')
                    .Append(select.JoinClauses.Count).Append('|')
                    .Append(select.FromSubquery is not null).Append('|')
                    .Append(select.Projections.Count).Append('|')
                    .Append(select.GroupBy.Count).Append('|')
                    .Append(select.OrderByList.Count).Append('|')
                    .Append(select.Pagination is not null).Append('|')
                    .Append(select.Distinct).Append('|')
                    .Append(select.UnionStatements.Count).Append('|')
                    .Append(select.TableValuedFunction is not null).Append('|')
                    .Append(select.GraphTable is not null).Append('|');
                if (select.FromSubquery is not null)
                    AppendStatement(builder, select.FromSubquery);
                AppendExpression(builder, select.TableValuedFunction);
                AppendExpression(builder, select.Where);
                AppendExpression(builder, select.Having);
                foreach (SqlExpression groupBy in select.GroupBy)
                    AppendExpression(builder, groupBy);
                foreach (OrderBySpec orderBy in select.OrderByList)
                {
                    builder.Append(orderBy.Direction).Append('|');
                    AppendExpression(builder, orderBy.Expression);
                }
                if (select.Pagination is { } pagination)
                {
                    AppendExpression(builder, pagination.OffsetExpression);
                    AppendExpression(builder, pagination.FetchExpression);
                }
                foreach (JoinClause join in select.JoinClauses)
                {
                    builder.Append(join.TableName).Append(':').Append(join.Alias).Append(':').Append(join.Kind).Append('|');
                    AppendExpression(builder, join.On);
                    if (join.Subquery is not null)
                        AppendStatement(builder, join.Subquery);
                }
                foreach (SelectItem projection in select.Projections)
                {
                    builder.Append(projection.Alias).Append('|');
                    AppendExpression(builder, projection.Expression);
                }
                if (select.GraphTable is not null)
                    AppendGraphTable(builder, select.GraphTable);
                foreach (SelectStatement union in select.UnionStatements)
                    AppendStatement(builder, union);
                break;
            default:
                builder.Append(statement.GetType().Name);
                break;
        }
    }

    private static void AppendExpression(StringBuilder builder, SqlExpression? expression)
    {
        if (expression is null)
        {
            builder.Append("null|");
            return;
        }

        builder.Append(expression.GetType().Name).Append('|');
        switch (expression)
        {
            case IdentifierExpression identifier:
                builder.Append(identifier.Qualifier).Append(':').Append(identifier.Name).Append('|');
                break;
            case BinaryExpression binary:
                builder.Append(binary.Operator).Append('|');
                AppendExpression(builder, binary.Left);
                AppendExpression(builder, binary.Right);
                break;
            case UnaryExpression unary:
                builder.Append(unary.Operator).Append('|');
                AppendExpression(builder, unary.Operand);
                break;
            case FunctionCallExpression function:
                builder.Append(function.Name).Append(':').Append(function.Arguments.Count).Append(':').Append(function.IsStar).Append('|');
                foreach (SqlExpression argument in function.Arguments)
                    AppendExpression(builder, argument);
                break;
            case InExpression inExpression:
                builder.Append(inExpression.Negated).Append(':').Append(inExpression.Values.Count).Append(':').Append(inExpression.Subquery is not null).Append('|');
                AppendExpression(builder, inExpression.Value);
                if (inExpression.Subquery is not null)
                    AppendStatement(builder, inExpression.Subquery);
                break;
            case IsNullExpression isNull:
                builder.Append(isNull.Negated).Append('|');
                AppendExpression(builder, isNull.Operand);
                break;
            case ExistsExpression exists:
                AppendStatement(builder, exists.Select);
                break;
            case SubqueryExpression subquery:
                AppendStatement(builder, subquery.Select);
                break;
            case NamedArgumentExpression named:
                builder.Append(named.Name).Append('|');
                AppendExpression(builder, named.Value);
                break;
            case CaseExpression caseExpression:
                builder.Append(caseExpression.WhenClauses.Count).Append('|');
                foreach (CaseWhenClause clause in caseExpression.WhenClauses)
                {
                    AppendExpression(builder, clause.Condition);
                    AppendExpression(builder, clause.Result);
                }
                AppendExpression(builder, caseExpression.Else);
                break;
            case VectorLiteralExpression vector:
                builder.Append(vector.Components.Count).Append('|');
                break;
            case GeoPointLiteralExpression:
                builder.Append("GeoPointLiteralExpression|");
                break;
            case DurationLiteralExpression:
                builder.Append("DurationLiteralExpression|");
                break;
            case LiteralExpression literal:
                // Literal values are deliberately omitted; the kind still separates NULL,
                // numeric, boolean and string expression shapes.
                builder.Append(literal.Kind).Append('|');
                break;
            case StarExpression:
            case DefaultValueExpression:
                builder.Append(expression.GetType().Name).Append('|');
                break;
            case ParameterExpression:
                builder.Append("ParameterExpression|");
                break;
        }
    }

    private static void AppendGraphTable(StringBuilder builder, GraphTableSource source)
    {
        builder.Append(source.GraphName).Append('|')
            .Append(source.LeftVertex.Variable).Append(':').Append(source.LeftVertex.Label).Append('|')
            .Append(source.Edge.Variable).Append(':').Append(source.Edge.Label).Append('|')
            .Append(source.RightVertex.Variable).Append(':').Append(source.RightVertex.Label).Append('|')
            .Append(source.Direction).Append('|');
        AppendExpression(builder, source.Predicate);
        builder.Append(source.Columns.Count).Append('|');
        foreach (SelectItem column in source.Columns)
        {
            builder.Append(column.Alias).Append('|');
            AppendExpression(builder, column.Expression);
        }

        if (source.Path is { } path)
        {
            builder.Append(path.Variable).Append('|')
                .Append(path.MinDepth).Append('|')
                .Append(path.MaxDepth).Append('|')
                .Append(path.Uniqueness).Append('|')
                .Append(path.IsAnyShortest).Append('|');
        }
        else
        {
            builder.Append("no-path|");
        }
    }
}
