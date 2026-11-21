using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Query;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>提供 SonnetDB 普通查询和发布时预编译查询的关系型编译上下文。</summary>
public sealed class SonnetDbQueryCompilationContext : RelationalQueryCompilationContext
{
    /// <summary>创建普通运行时查询上下文，不启用发布时预编译模式。</summary>
    /// <param name="dependencies">查询编译依赖。</param>
    /// <param name="relationalDependencies">关系型查询编译依赖。</param>
    /// <param name="async">是否编译异步查询。</param>
    public SonnetDbQueryCompilationContext(
        QueryCompilationContextDependencies dependencies,
        RelationalQueryCompilationContextDependencies relationalDependencies,
        bool async)
        : base(dependencies, relationalDependencies, async)
    {
    }

    /// <summary>创建显式指定发布时预编译模式的上下文；该 EF 接口仍为实验能力。</summary>
    /// <param name="dependencies">查询编译依赖。</param>
    /// <param name="relationalDependencies">关系型查询编译依赖。</param>
    /// <param name="async">是否编译异步查询。</param>
    /// <param name="precompiling">是否生成发布时预编译查询。</param>
    [Experimental("EF9100")]
    public SonnetDbQueryCompilationContext(
        QueryCompilationContextDependencies dependencies,
        RelationalQueryCompilationContextDependencies relationalDependencies,
        bool async,
        bool precompiling)
        : base(dependencies, relationalDependencies, async, precompiling)
    {
    }

    /// <summary>允许 EF 生成 SonnetDB 预编译查询及其提升常量。</summary>
    public override bool SupportsPrecompiledQuery => true;
}
