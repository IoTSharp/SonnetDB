using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class RelationalSelectExecutor
{
    private static SelectColumnInfo? InferConcatDeclaration(SqlExpression expression)
    {
        if (expression is not FunctionCallExpression function
            || !function.Name.Equals("concat", StringComparison.OrdinalIgnoreCase)
            || function.IsStar || function.Arguments.Count == 0
            || FunctionRegistry.GetFunctionKind(function.Name) != FunctionKind.Scalar
            || UserFunctionRegistry.Current?.TryGetScalar(function.Name, out _) == true)
            return null;

        // 内置 CONCAT 将 NULL 作为空文本，始终返回非空字符串；不执行参数或猜测最大长度。
        return new SelectColumnInfo(TableColumnType.String, false);
    }
}
