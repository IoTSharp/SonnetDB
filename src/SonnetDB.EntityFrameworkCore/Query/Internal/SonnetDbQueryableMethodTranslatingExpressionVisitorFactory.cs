using Microsoft.EntityFrameworkCore.Query;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>创建支持 SonnetDB JSON 原子更新的关系型查询翻译器。</summary>
public sealed class SonnetDbQueryableMethodTranslatingExpressionVisitorFactory : IQueryableMethodTranslatingExpressionVisitorFactory
{
    private readonly QueryableMethodTranslatingExpressionVisitorDependencies _dependencies;
    private readonly RelationalQueryableMethodTranslatingExpressionVisitorDependencies _relationalDependencies;

    /// <summary>创建查询翻译器工厂。</summary>
    /// <param name="dependencies">查询翻译器依赖。</param>
    /// <param name="relationalDependencies">关系型查询翻译器依赖。</param>
    public SonnetDbQueryableMethodTranslatingExpressionVisitorFactory(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies)
    {
        _dependencies = dependencies;
        _relationalDependencies = relationalDependencies;
    }

    /// <inheritdoc />
    public QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new SonnetDbQueryableMethodTranslatingExpressionVisitor(_dependencies, _relationalDependencies,
            (RelationalQueryCompilationContext)queryCompilationContext);
}
