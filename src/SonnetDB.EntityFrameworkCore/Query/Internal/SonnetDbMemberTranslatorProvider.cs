using Microsoft.EntityFrameworkCore.Query;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// SonnetDB 成员翻译器提供程序。
/// </summary>
public sealed class SonnetDbMemberTranslatorProvider : RelationalMemberTranslatorProvider
{
    /// <summary>
    /// 创建 SonnetDB 成员翻译器提供程序。
    /// </summary>
    /// <param name="dependencies">关系成员翻译器依赖。</param>
    public SonnetDbMemberTranslatorProvider(RelationalMemberTranslatorProviderDependencies dependencies)
        : base(dependencies)
    {
        AddTranslators([new SonnetDbDateTimeMemberTranslator(dependencies.SqlExpressionFactory)]);
    }
}
