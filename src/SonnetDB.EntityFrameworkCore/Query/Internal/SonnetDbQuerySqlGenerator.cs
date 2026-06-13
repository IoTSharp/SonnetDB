using Microsoft.EntityFrameworkCore.Query;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// SonnetDB 基础查询 SQL 生成器。
/// </summary>
public sealed class SonnetDbQuerySqlGenerator : QuerySqlGenerator
{
    /// <summary>
    /// 创建 SonnetDB 查询 SQL 生成器。
    /// </summary>
    /// <param name="dependencies">查询 SQL 生成器依赖。</param>
    public SonnetDbQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
        : base(dependencies)
    {
    }
}
