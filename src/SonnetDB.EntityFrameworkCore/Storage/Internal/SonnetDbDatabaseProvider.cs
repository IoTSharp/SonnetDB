using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.EntityFrameworkCore.Infrastructure.Internal;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>
/// 向 EF Core 标识当前上下文是否配置了 SonnetDB Provider。
/// </summary>
public sealed class SonnetDbDatabaseProvider : IDatabaseProvider
{
    /// <inheritdoc />
    public string Name => "SonnetDB.EntityFrameworkCore";

    /// <inheritdoc />
    public string? Version => typeof(SonnetDbDatabaseProvider).Assembly.GetName().Version?.ToString();

    /// <inheritdoc />
    public bool IsConfigured(IDbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Extensions.OfType<SonnetDbOptionsExtension>().Any();
    }
}
