using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace SonnetDB.EntityFrameworkCore.Metadata.Conventions.Internal;

/// <summary>
/// 构建 SonnetDB Provider 使用的关系型约定集合。
/// </summary>
public sealed class SonnetDbConventionSetBuilder : RelationalConventionSetBuilder
{
    /// <summary>
    /// 创建 SonnetDB 关系型约定集合构建器。
    /// </summary>
    /// <param name="dependencies">Provider 约定集合依赖。</param>
    /// <param name="relationalDependencies">关系型约定集合依赖。</param>
    public SonnetDbConventionSetBuilder(
        ProviderConventionSetBuilderDependencies dependencies,
        RelationalConventionSetBuilderDependencies relationalDependencies)
        : base(dependencies, relationalDependencies)
    {
    }
}
