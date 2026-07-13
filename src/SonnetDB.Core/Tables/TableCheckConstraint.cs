using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Tables;

/// <summary>
/// 关系表检查约束的持久化声明。
/// </summary>
/// <param name="Name">约束名；空值由 schema 生成稳定名称。</param>
/// <param name="ExpressionSql">不含外围 <c>CHECK (...)</c> 的表达式文本。</param>
public sealed record TableCheckConstraintDefinition(string Name, string ExpressionSql);

/// <summary>
/// 已解析并可执行的关系表检查约束。
/// </summary>
/// <param name="Name">约束名。</param>
/// <param name="ExpressionSql">规范化表达式文本。</param>
/// <param name="Expression">已解析表达式 AST。</param>
public sealed record TableCheckConstraint(
    string Name,
    string ExpressionSql,
    SqlExpression Expression)
{
    internal static TableCheckConstraint Create(
        string tableName,
        IReadOnlySet<string> columnNames,
        string name,
        string expressionSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(columnNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(expressionSql);

        var expression = SqlParser.ParsePredicate(expressionSql);
        ValidateExpression(tableName, columnNames, expression);
        return new TableCheckConstraint(
            name,
            SqlExpressionFormatter.Format(expression),
            expression);
    }

    internal bool ReferencesColumn(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        return ReferencesColumn(Expression, columnName);
    }

    private static void ValidateExpression(
        string tableName,
        IReadOnlySet<string> columnNames,
        SqlExpression expression)
    {
        switch (expression)
        {
            case LiteralExpression:
                return;
            case IdentifierExpression identifier:
                if (!string.IsNullOrWhiteSpace(identifier.Qualifier))
                {
                    throw new ArgumentException(
                        $"CHECK 约束不允许限定列名 '{identifier.Qualifier}.{identifier.Name}'。",
                        nameof(expression));
                }
                if (!columnNames.Contains(identifier.Name))
                {
                    throw new ArgumentException(
                        $"关系表 '{tableName}' 的 CHECK 约束引用了未知列 '{identifier.Name}'。",
                        nameof(expression));
                }
                return;
            case BinaryExpression binary:
                ValidateExpression(tableName, columnNames, binary.Left);
                ValidateExpression(tableName, columnNames, binary.Right);
                return;
            case UnaryExpression unary:
                ValidateExpression(tableName, columnNames, unary.Operand);
                return;
            case IsNullExpression isNull:
                ValidateExpression(tableName, columnNames, isNull.Operand);
                return;
            case InExpression { Subquery: null } inExpression:
                ValidateExpression(tableName, columnNames, inExpression.Value);
                foreach (var value in inExpression.Values)
                    ValidateExpression(tableName, columnNames, value);
                return;
            case FunctionCallExpression { IsStar: false } function:
                if (!IsSupportedScalarFunction(function))
                {
                    throw new NotSupportedException(
                        $"CHECK 约束不支持函数 '{function.Name}' 或其参数形式。");
                }
                foreach (var argument in function.Arguments)
                    ValidateExpression(tableName, columnNames, argument);
                return;
            case CaseExpression caseExpression:
                foreach (var clause in caseExpression.WhenClauses)
                {
                    ValidateExpression(tableName, columnNames, clause.Condition);
                    ValidateExpression(tableName, columnNames, clause.Result);
                }
                if (caseExpression.Else is not null)
                    ValidateExpression(tableName, columnNames, caseExpression.Else);
                return;
            default:
                throw new NotSupportedException(
                    $"CHECK 约束暂不支持表达式节点 '{expression.GetType().Name}'。");
        }
    }

    private static bool ReferencesColumn(SqlExpression expression, string columnName) => expression switch
    {
        IdentifierExpression identifier => string.Equals(identifier.Name, columnName, StringComparison.Ordinal),
        BinaryExpression binary => ReferencesColumn(binary.Left, columnName)
            || ReferencesColumn(binary.Right, columnName),
        UnaryExpression unary => ReferencesColumn(unary.Operand, columnName),
        IsNullExpression isNull => ReferencesColumn(isNull.Operand, columnName),
        InExpression inExpression => ReferencesColumn(inExpression.Value, columnName)
            || inExpression.Values.Any(value => ReferencesColumn(value, columnName)),
        FunctionCallExpression function => function.Arguments.Any(argument => ReferencesColumn(argument, columnName)),
        CaseExpression caseExpression => caseExpression.WhenClauses.Any(clause =>
                ReferencesColumn(clause.Condition, columnName)
                || ReferencesColumn(clause.Result, columnName))
            || (caseExpression.Else is not null && ReferencesColumn(caseExpression.Else, columnName)),
        _ => false
    };

    private static bool IsSupportedScalarFunction(FunctionCallExpression function)
        => (string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
                && function.Arguments.Count == 2
                && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String })
            || ((string.Equals(function.Name, "lower", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(function.Name, "upper", StringComparison.OrdinalIgnoreCase))
                && function.Arguments.Count == 1)
            || (string.Equals(function.Name, "coalesce", StringComparison.OrdinalIgnoreCase)
                && function.Arguments.Count > 0);
}
