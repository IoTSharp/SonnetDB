using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace SonnetDB.EntityFrameworkCore.ValueGeneration.Internal;

/// <summary>
/// 为 SonnetDB 选择 EF Core 值生成器。
/// </summary>
public sealed class SonnetDbValueGeneratorSelector : RelationalValueGeneratorSelector
{
    /// <summary>
    /// 创建 SonnetDB 值生成器选择器。
    /// </summary>
    /// <param name="dependencies">EF Core 值生成器选择器依赖。</param>
    public SonnetDbValueGeneratorSelector(ValueGeneratorSelectorDependencies dependencies)
        : base(dependencies)
    {
    }
}
