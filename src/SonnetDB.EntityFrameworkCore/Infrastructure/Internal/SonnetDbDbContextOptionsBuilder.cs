using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SonnetDB.EntityFrameworkCore.Infrastructure.Internal;

/// <summary>
/// SonnetDB Provider 专用的关系型选项构建器类型。
/// </summary>
public sealed class SonnetDbDbContextOptionsBuilder
    : RelationalDbContextOptionsBuilder<SonnetDbDbContextOptionsBuilder, SonnetDbOptionsExtension>
{
    /// <summary>
    /// 创建 SonnetDB Provider 选项构建器。
    /// </summary>
    /// <param name="optionsBuilder">EF Core 选项构建器。</param>
    public SonnetDbDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
        : base(optionsBuilder)
    {
    }
}
