using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 校验 UPDATE 顶层表达式中的列限定符。
/// </summary>
internal static class MutationAliasValidator
{
    /// <summary>
    /// 确保限定列只引用 UPDATE 声明的目标表或联接来源别名。
    /// </summary>
    /// <param name="statement">待执行的 UPDATE 语句。</param>
    public static void Validate(UpdateStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var allowedQualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            statement.TableAlias ?? statement.TableName,
        };
        foreach (var from in statement.FromClauses)
        {
            if (!allowedQualifiers.Add(from.Alias))
            {
                throw new InvalidOperationException($"UPDATE 联接别名 '{from.Alias}' 重复。");
            }
        }

        foreach (var assignment in statement.Assignments)
        {
            ValidateExpression(assignment.Value, allowedQualifiers);
        }

        ValidateExpression(statement.Where, allowedQualifiers);
        foreach (var from in statement.FromClauses)
            ValidateExpression(from.On, allowedQualifiers);
    }

    private static void ValidateExpression(SqlExpression expression, IReadOnlySet<string> allowedQualifiers)
    {
        switch (expression)
        {
            case IdentifierExpression { Qualifier: not null } identifier
                when !allowedQualifiers.Contains(identifier.Qualifier):
                throw new InvalidOperationException(
                    $"限定列名 '{identifier.Qualifier}.{identifier.Name}' 引用了未知别名 '{identifier.Qualifier}'；"
                    + $"当前 UPDATE 语句未声明该联接来源。");
            case IdentifierExpression:
                return;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    ValidateExpression(argument, allowedQualifiers);
                return;
            case NamedArgumentExpression named:
                ValidateExpression(named.Value, allowedQualifiers);
                return;
            case UnaryExpression unary:
                ValidateExpression(unary.Operand, allowedQualifiers);
                return;
            case BinaryExpression binary:
                ValidateExpression(binary.Left, allowedQualifiers);
                ValidateExpression(binary.Right, allowedQualifiers);
                return;
            case IsNullExpression isNull:
                ValidateExpression(isNull.Operand, allowedQualifiers);
                return;
            case InExpression inExpression:
                ValidateExpression(inExpression.Value, allowedQualifiers);
                foreach (var value in inExpression.Values)
                    ValidateExpression(value, allowedQualifiers);
                return;
            case CaseExpression caseExpression:
                foreach (var clause in caseExpression.WhenClauses)
                {
                    ValidateExpression(clause.Condition, allowedQualifiers);
                    ValidateExpression(clause.Result, allowedQualifiers);
                }

                if (caseExpression.Else is not null)
                    ValidateExpression(caseExpression.Else, allowedQualifiers);
                return;
            // 子查询拥有独立作用域；外层相关引用由 TableInSubqueryExecutor 校验并拒绝。
            case SubqueryExpression or ExistsExpression:
                return;
        }
    }
}
