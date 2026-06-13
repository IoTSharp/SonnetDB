using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SonnetDB.EntityFrameworkCore.Infrastructure.Internal;

namespace SonnetDB.EntityFrameworkCore.Extensions;

/// <summary>
/// 提供 SonnetDB Entity Framework Core Provider 的 <see cref="DbContextOptionsBuilder"/> 扩展方法。
/// </summary>
public static class SonnetDbDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// 配置当前 <see cref="DbContext"/> 使用 SonnetDB 数据库。
    /// </summary>
    /// <param name="optionsBuilder">EF Core 选项构建器。</param>
    /// <param name="connectionString">SonnetDB ADO.NET 连接字符串。</param>
    /// <param name="sonnetDbOptionsAction">可选的 SonnetDB Provider 关系型选项配置。</param>
    /// <returns>传入的 <see cref="DbContextOptionsBuilder"/>，用于链式调用。</returns>
    public static DbContextOptionsBuilder UseSonnetDB(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<SonnetDbDbContextOptionsBuilder>? sonnetDbOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var extension = optionsBuilder.Options.FindExtension<SonnetDbOptionsExtension>()
            ?? new SonnetDbOptionsExtension();
        extension = (SonnetDbOptionsExtension)extension.WithConnectionString(connectionString);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        sonnetDbOptionsAction?.Invoke(new SonnetDbDbContextOptionsBuilder(optionsBuilder));

        return optionsBuilder;
    }

    /// <summary>
    /// 配置当前 <see cref="DbContext"/> 使用已有的 SonnetDB 数据库连接。
    /// </summary>
    /// <param name="optionsBuilder">EF Core 选项构建器。</param>
    /// <param name="connection">已有的 SonnetDB ADO.NET 连接。</param>
    /// <param name="sonnetDbOptionsAction">可选的 SonnetDB Provider 关系型选项配置。</param>
    /// <returns>传入的 <see cref="DbContextOptionsBuilder"/>，用于链式调用。</returns>
    public static DbContextOptionsBuilder UseSonnetDB(
        this DbContextOptionsBuilder optionsBuilder,
        DbConnection connection,
        Action<SonnetDbDbContextOptionsBuilder>? sonnetDbOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);

        var extension = optionsBuilder.Options.FindExtension<SonnetDbOptionsExtension>()
            ?? new SonnetDbOptionsExtension();
        extension = (SonnetDbOptionsExtension)extension.WithConnection(connection);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        sonnetDbOptionsAction?.Invoke(new SonnetDbDbContextOptionsBuilder(optionsBuilder));

        return optionsBuilder;
    }

    /// <summary>
    /// 配置当前 <see cref="DbContext"/> 使用 SonnetDB 数据库。
    /// </summary>
    /// <typeparam name="TContext">DbContext 派生类型。</typeparam>
    /// <param name="optionsBuilder">EF Core 选项构建器。</param>
    /// <param name="connectionString">SonnetDB ADO.NET 连接字符串。</param>
    /// <param name="sonnetDbOptionsAction">可选的 SonnetDB Provider 关系型选项配置。</param>
    /// <returns>传入的 <see cref="DbContextOptionsBuilder{TContext}"/>，用于链式调用。</returns>
    public static DbContextOptionsBuilder<TContext> UseSonnetDB<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        Action<SonnetDbDbContextOptionsBuilder>? sonnetDbOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseSonnetDB((DbContextOptionsBuilder)optionsBuilder, connectionString, sonnetDbOptionsAction);

    /// <summary>
    /// 配置当前 <see cref="DbContext"/> 使用已有的 SonnetDB 数据库连接。
    /// </summary>
    /// <typeparam name="TContext">DbContext 派生类型。</typeparam>
    /// <param name="optionsBuilder">EF Core 选项构建器。</param>
    /// <param name="connection">已有的 SonnetDB ADO.NET 连接。</param>
    /// <param name="sonnetDbOptionsAction">可选的 SonnetDB Provider 关系型选项配置。</param>
    /// <returns>传入的 <see cref="DbContextOptionsBuilder{TContext}"/>，用于链式调用。</returns>
    public static DbContextOptionsBuilder<TContext> UseSonnetDB<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        DbConnection connection,
        Action<SonnetDbDbContextOptionsBuilder>? sonnetDbOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseSonnetDB((DbContextOptionsBuilder)optionsBuilder, connection, sonnetDbOptionsAction);
}
