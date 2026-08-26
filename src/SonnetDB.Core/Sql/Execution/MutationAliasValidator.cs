using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 校验单目标表 UPDATE 顶层表达式中的列限定符。
/// </summary>
internal static class MutationAliasValidator
{
    /// <summary>
    /// 确保限定列只引用 UPDATE 声明的目标表别名。
    /// </summary>
    /// <param name="statement">待执行的 UPDATE 语句。</param>
    public static void Validate(UpdateStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var expectedQualifier = statement.TableAlias ?? statement.TableName;
        foreach (var assignment in statement.Assignments)
        {
            ValidateExpression(assignment.Value, expectedQualifier);
        }

        ValidateExpression(statement.Where, expectedQualifier);
    }

    private static void ValidateExpression(SqlExpression expression, string expectedQualifier)
    {
        switch (expression)
        {
            case IdentifierExpression { Qualifier: not null } identifier
                when !string.Equals(identifier.Qualifier, expectedQualifier, StringComparison.OrdinalIgnoreCase):
                throw new InvalidOperationException(
                    $"限定列名 '{identifier.Qualifier}.{identifier.Name}' 引用了未知别名 '{identifier.Qualifier}'；"
                    + $"当前 UPDATE 目标只声明了限定符 '{expectedQualifier}'。");
            case IdentifierExpression:
                return;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    ValidateExpression(argument, expectedQualifier);
                return;
            case NamedArgumentExpression named:
                ValidateExpression(named.Value, expectedQualifier);
                return;
            case UnaryExpression unary:
                ValidateExpression(unary.Operand, expectedQualifier);
                return;
            case BinaryExpression binary:
                ValidateExpression(binary.Left, expectedQualifier);
                ValidateExpression(binary.Right, expectedQualifier);
                return;
            case IsNullExpression isNull:
                ValidateExpression(isNull.Operand, expectedQualifier);
                return;
            case InExpression inExpression:
                ValidateExpression(inExpression.Value, expectedQualifier);
                foreach (var value in inExpression.Values)
                    ValidateExpression(value, expectedQualifier);
                return;
            case CaseExpression caseExpression:
                foreach (var clause in caseExpression.WhenClauses)
                {
                    ValidateExpression(clause.Condition, expectedQualifier);
                    ValidateExpression(clause.Result, expectedQualifier);
                }

                if (caseExpression.Else is not null)
                    ValidateExpression(caseExpression.Else, expectedQualifier);
                return;
            // 子查询拥有独立作用域；外层相关引用由 TableInSubqueryExecutor 校验并拒绝。
            case SubqueryExpression or ExistsExpression:
                return;
        }
    }
}
