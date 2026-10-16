using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class RelationalSelectExecutor
{
    /// <summary>UInt64 位运算以 DECIMAL(20,0) 返回，并合并两个输入的 NULL 声明。</summary>
    private static SelectColumnInfo? InferUInt64BitwiseColumnInfo(SqlExpression expression, IReadOnlyList<RelColumn> columns)
    {
        if (expression is not FunctionCallExpression function
            || !(function.Name.Equals("bit_and_uint64", StringComparison.OrdinalIgnoreCase)
                || function.Name.Equals("bit_or_uint64", StringComparison.OrdinalIgnoreCase))
            || function.Arguments.Count != 2
            || function.IsStar
            || FunctionRegistry.GetFunctionKind(function.Name) != FunctionKind.Scalar
            || UserFunctionRegistry.Current?.TryGetScalar(function.Name, out _) == true)
            return null;

        var left = InferSelectColumnInfo(function.Arguments[0], columns);
        var right = InferSelectColumnInfo(function.Arguments[1], columns);
        bool? nullable = left.IsNullable == true || right.IsNullable == true ? true
            : left.IsNullable == false && right.IsNullable == false ? false : null;
        return new SelectColumnInfo(TableColumnType.Decimal, nullable)
        {
            NumericPrecision = 20,
            NumericScale = 0,
        };
    }
}
