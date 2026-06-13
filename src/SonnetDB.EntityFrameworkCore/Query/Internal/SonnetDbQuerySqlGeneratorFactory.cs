using Microsoft.EntityFrameworkCore.Query;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// 创建 SonnetDB 查询 SQL 生成器。
/// </summary>
public sealed class SonnetDbQuerySqlGeneratorFactory : IQuerySqlGeneratorFactory
{
    private readonly QuerySqlGeneratorDependencies _dependencies;

    /// <summary>
    /// 创建 SonnetDB 查询 SQL 生成器工厂。
    /// </summary>
    /// <param name="dependencies">查询 SQL 生成器依赖。</param>
    public SonnetDbQuerySqlGeneratorFactory(QuerySqlGeneratorDependencies dependencies)
    {
        _dependencies = dependencies;
    }

    /// <inheritdoc />
    public QuerySqlGenerator Create()
        => new SonnetDbQuerySqlGenerator(_dependencies);
}
