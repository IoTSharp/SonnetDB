using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>集中分发内建表值函数的显式物化准入，避免注册回调替换已验证的内核。</summary>
internal static class SqlTableFunctionMaterialization
{
    internal static void ValidateScalarArgumentCount(FunctionCallExpression call)
    {
        if (FunctionRegistry.TryGetScalar(call.Name, out IScalarFunction? function)
            && (call.Arguments.Count < function.MinArgumentCount || call.Arguments.Count > function.MaxArgumentCount))
            throw new NotSupportedException($"SQL 物化预算要求标量函数 '{call.Name}' 使用合法参数个数。");
    }

    internal static void Validate(Tsdb tsdb, SelectStatement statement)
    {
        FunctionCallExpression call = statement.TableValuedFunction
            ?? throw new NotSupportedException("SQL 物化预算需要直接内建表值函数来源。");
        if (tsdb.Functions.TryGetTableValuedFunction(call.Name, out _))
            throw new NotSupportedException("SQL 物化预算不支持用户注册的表值函数回调。");

        switch (call.Name.ToLowerInvariant())
        {
            case "knn":
                TableValuedFunctionExecutor.ValidateMaterializationSupported(tsdb, statement);
                break;
            case "vector_search":
                DocumentVectorSearchExecutor.ValidateMaterializationSupported(tsdb, statement);
                break;
            case "json_each":
            case "json_table":
                JsonFileSqlExecutor.ValidateMaterializationSupported(tsdb, statement);
                break;
            default:
                throw new NotSupportedException("SQL 物化预算尚不支持该表值函数查询源。");
        }
    }
}
