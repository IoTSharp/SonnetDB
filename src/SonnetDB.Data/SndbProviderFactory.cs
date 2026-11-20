using System.Data.Common;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB 的 <see cref="DbProviderFactory"/> 实现，便于在通用 ADO.NET 代码（如 Dapper）中以工厂模式获取连接 / 命令 / 参数。
/// </summary>
public sealed class SndbProviderFactory : DbProviderFactory
{
    /// <summary>共享单例。</summary>
    public static readonly SndbProviderFactory Instance = new();

    private SndbProviderFactory() { }

    /// <inheritdoc />
    public override DbConnection CreateConnection() => new SndbConnection();

    /// <inheritdoc />
    public override DbCommand CreateCommand() => new SndbCommand();

    /// <inheritdoc />
    public override DbDataAdapter CreateDataAdapter() => new SndbDataAdapter();

    /// <inheritdoc />
    public override bool CanCreateDataAdapter => true;

    /// <inheritdoc />
    public override DbParameter CreateParameter() => new SndbParameter();

    /// <inheritdoc />
    public override DbConnectionStringBuilder CreateConnectionStringBuilder()
        => new SndbConnectionStringBuilder();
}
