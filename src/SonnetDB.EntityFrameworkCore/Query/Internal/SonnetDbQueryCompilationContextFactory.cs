using Microsoft.EntityFrameworkCore.Query;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// 创建 SonnetDB 关系型查询编译上下文。
/// </summary>
public sealed class SonnetDbQueryCompilationContextFactory : IQueryCompilationContextFactory
{
    private readonly QueryCompilationContextDependencies _dependencies;
    private readonly RelationalQueryCompilationContextDependencies _relationalDependencies;

    /// <summary>
    /// 创建 SonnetDB 查询编译上下文工厂。
    /// </summary>
    /// <param name="dependencies">查询编译上下文依赖。</param>
    /// <param name="relationalDependencies">关系型查询编译上下文依赖。</param>
    public SonnetDbQueryCompilationContextFactory(
        QueryCompilationContextDependencies dependencies,
        RelationalQueryCompilationContextDependencies relationalDependencies)
    {
        _dependencies = dependencies;
        _relationalDependencies = relationalDependencies;
    }

    /// <inheritdoc />
    public QueryCompilationContext Create(bool async)
        => new RelationalQueryCompilationContext(_dependencies, _relationalDependencies, async);

    /// <inheritdoc />
    public QueryCompilationContext CreatePrecompiled(bool async)
        => new RelationalQueryCompilationContext(_dependencies, _relationalDependencies, async);
}
