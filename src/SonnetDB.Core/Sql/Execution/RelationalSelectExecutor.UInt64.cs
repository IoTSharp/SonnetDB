using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class RelationalSelectExecutor
{
    /// <summary>完整 UInt64 文本解析使用现有 DECIMAL(20,0) 声明；不推断同名用户函数。</summary>
    private static SelectColumnInfo? InferUInt64ParseColumnInfo(SqlExpression expression, IReadOnlyList<RelColumn> columns)
    {
        if (expression is not FunctionCallExpression function
            || !function.Name.Equals("parse_uint64", StringComparison.OrdinalIgnoreCase)
            || function.Arguments.Count != 1
            || function.IsStar
            || FunctionRegistry.GetFunctionKind(function.Name) != FunctionKind.Scalar
            || UserFunctionRegistry.Current?.TryGetScalar(function.Name, out _) == true)
            return null;

        var input = InferSelectColumnInfo(function.Arguments[0], columns);
        return new SelectColumnInfo(TableColumnType.Decimal, input.IsNullable)
        {
            NumericPrecision = 20,
            NumericScale = 0,
        };
    }
}
