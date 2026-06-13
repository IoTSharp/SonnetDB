using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.Data;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>
/// EF Core 使用的 SonnetDB 关系型连接。
/// </summary>
public sealed class SonnetDbRelationalConnection : RelationalConnection
{
    /// <summary>
    /// 创建 SonnetDB 关系型连接。
    /// </summary>
    /// <param name="dependencies">关系型连接依赖。</param>
    public SonnetDbRelationalConnection(RelationalConnectionDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    protected override DbConnection CreateDbConnection()
        => new SndbConnection(ConnectionString);
}
