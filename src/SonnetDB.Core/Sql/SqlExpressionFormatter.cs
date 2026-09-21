using System.Globalization;
using System.Text;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql;

internal static class SqlExpressionFormatter
{
    public static string Format(SqlExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var builder = new StringBuilder();
        Append(builder, expression);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, SqlExpression expression)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                AppendLiteral(builder, literal);
                return;
            case DurationLiteralExpression duration:
                builder.Append(duration.Milliseconds.ToString(CultureInfo.InvariantCulture));
                return;
            case CastExpression cast:
                builder.Append("CAST(");
                Append(builder, cast.Operand);
                builder.Append(" AS ").Append(DataTypeText(cast.TargetType)).Append(')');
                return;
            case IdentifierExpression identifier:
                if (!string.IsNullOrWhiteSpace(identifier.Qualifier))
                {
                    AppendIdentifier(builder, identifier.Qualifier);
                    builder.Append('.');
                }
                AppendIdentifier(builder, identifier.Name);
                return;
            case BinaryExpression binary:
                builder.Append('(');
                Append(builder, binary.Left);
                builder.Append(' ').Append(OperatorText(binary.Operator)).Append(' ');
                Append(builder, binary.Right);
                builder.Append(')');
                return;
            case UnaryExpression unary:
                builder.Append('(').Append(unary.Operator == SqlUnaryOperator.Not ? "NOT " : "-");
                Append(builder, unary.Operand);
                builder.Append(')');
                return;
            case IsNullExpression isNull:
                builder.Append('(');
                Append(builder, isNull.Operand);
                builder.Append(isNull.Negated ? " IS NOT NULL)" : " IS NULL)");
                return;
            case InExpression { Subquery: null } inExpression:
                builder.Append('(');
                Append(builder, inExpression.Value);
                builder.Append(inExpression.Negated ? " NOT IN (" : " IN (");
                for (var i = 0; i < inExpression.Values.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    Append(builder, inExpression.Values[i]);
                }
                builder.Append("))");
                return;
            case FunctionCallExpression { IsStar: false } function:
                AppendIdentifier(builder, function.Name);
                builder.Append('(');
                if (function.IsDistinct)
                    builder.Append("DISTINCT ");
                for (var i = 0; i < function.Arguments.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    Append(builder, function.Arguments[i]);
                }
                builder.Append(')');
                if (function.Over is { } over)
                {
                    builder.Append(" OVER (");
                    if (over.PartitionBy.Count != 0)
                    {
                        builder.Append("PARTITION BY ");
                        for (var i = 0; i < over.PartitionBy.Count; i++)
                        {
                            if (i > 0)
                                builder.Append(", ");
                            Append(builder, over.PartitionBy[i]);
                        }
                    }
                    if (over.OrderBy.Count != 0)
                    {
                        if (over.PartitionBy.Count != 0)
                            builder.Append(' ');
                        builder.Append("ORDER BY ");
                        for (var i = 0; i < over.OrderBy.Count; i++)
                        {
                            if (i > 0)
                                builder.Append(", ");
                            Append(builder, over.OrderBy[i].Expression);
                            if (over.OrderBy[i].Direction == SortDirection.Descending)
                                builder.Append(" DESC");
                        }
                    }
                    builder.Append(')');
                }
                return;
            case CaseExpression caseExpression:
                builder.Append("(CASE");
                foreach (var clause in caseExpression.WhenClauses)
                {
                    builder.Append(" WHEN ");
                    Append(builder, clause.Condition);
                    builder.Append(" THEN ");
                    Append(builder, clause.Result);
                }
                if (caseExpression.Else is not null)
                {
                    builder.Append(" ELSE ");
                    Append(builder, caseExpression.Else);
                }
                builder.Append(" END)");
                return;
            default:
                throw new NotSupportedException(
                    $"CHECK 约束暂不支持表达式节点 '{expression.GetType().Name}'。");
        }
    }

    private static void AppendLiteral(StringBuilder builder, LiteralExpression literal)
    {
        switch (literal.Kind)
        {
            case SqlLiteralKind.Null:
                builder.Append("NULL");
                break;
            case SqlLiteralKind.Boolean:
                builder.Append(literal.BooleanValue ? "TRUE" : "FALSE");
                break;
            case SqlLiteralKind.Integer:
                builder.Append(literal.IntegerValue.ToString(CultureInfo.InvariantCulture));
                break;
            case SqlLiteralKind.Float:
                builder.Append(literal.FloatValue.ToString("R", CultureInfo.InvariantCulture));
                break;
            case SqlLiteralKind.String:
                builder.Append('\'')
                    .Append((literal.StringValue ?? string.Empty).Replace("'", "''", StringComparison.Ordinal))
                    .Append('\'');
                break;
            default:
                throw new NotSupportedException($"CHECK 约束暂不支持字面量 '{literal.Kind}'。");
        }
    }

    private static void AppendIdentifier(StringBuilder builder, string identifier)
        => builder.Append('"')
            .Append(identifier.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Append('"');

    private static string OperatorText(SqlBinaryOperator value) => value switch
    {
        SqlBinaryOperator.Or => "OR",
        SqlBinaryOperator.And => "AND",
        SqlBinaryOperator.Equal => "=",
        SqlBinaryOperator.NotEqual => "!=",
        SqlBinaryOperator.LessThan => "<",
        SqlBinaryOperator.LessThanOrEqual => "<=",
        SqlBinaryOperator.GreaterThan => ">",
        SqlBinaryOperator.GreaterThanOrEqual => ">=",
        SqlBinaryOperator.Like => "LIKE",
        SqlBinaryOperator.NotLike => "NOT LIKE",
        SqlBinaryOperator.Regex => "=~",
        SqlBinaryOperator.NotRegex => "!~",
        SqlBinaryOperator.Add => "+",
        SqlBinaryOperator.Subtract => "-",
        SqlBinaryOperator.Multiply => "*",
        SqlBinaryOperator.Divide => "/",
        SqlBinaryOperator.Modulo => "%",
        SqlBinaryOperator.BitwiseAnd => "&",
        SqlBinaryOperator.BitwiseOr => "|",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static string DataTypeText(SqlDataType value) => value switch
    {
        SqlDataType.Float64 => "FLOAT",
        SqlDataType.Int64 => "INT",
        SqlDataType.Boolean => "BOOL",
        SqlDataType.String => "STRING",
        SqlDataType.Vector => "VECTOR",
        SqlDataType.GeoPoint => "GEOPOINT",
        SqlDataType.DateTime => "DATETIME",
        SqlDataType.Time => "TIME",
        SqlDataType.Blob => "BLOB",
        SqlDataType.Json => "JSON",
        _ => value.ToString().ToUpperInvariant(),
    };
}
