using System.Data.Common;
using System.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>
/// SonnetDB 数据库创建器。嵌入式模式下数据库等价于连接字符串中的数据目录。
/// </summary>
public sealed class SonnetDbDatabaseCreator : RelationalDatabaseCreator
{
    private readonly IRelationalConnection _connection;

    /// <summary>
    /// 创建 SonnetDB 数据库创建器。
    /// </summary>
    /// <param name="dependencies">关系型数据库创建器依赖。</param>
    public SonnetDbDatabaseCreator(RelationalDatabaseCreatorDependencies dependencies)
        : base(dependencies)
    {
        _connection = dependencies.Connection;
    }

    /// <inheritdoc />
    public override bool Exists()
    {
        try
        {
            var connection = _connection.DbConnection;
            var wasClosed = connection.State == ConnectionState.Closed;
            if (wasClosed)
            {
                connection.Open();
                connection.Close();
            }

            return true;
        }
        catch (DbException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override void Create()
    {
        var connection = _connection.DbConnection;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            connection.Open();
            connection.Close();
        }
    }

    /// <inheritdoc />
    public override void Delete()
    {
        if (!string.IsNullOrWhiteSpace(_connection.ConnectionString))
        {
            var connection = _connection.DbConnection;
            var dataSource = connection.DataSource;
            if (!string.IsNullOrWhiteSpace(dataSource) && Directory.Exists(dataSource))
            {
                Directory.Delete(dataSource, recursive: true);
            }
        }
    }

    /// <inheritdoc />
    public override bool HasTables()
    {
        var connection = _connection.DbConnection;
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SHOW TABLES";
        using var reader = command.ExecuteReader();
        var hasTables = reader.Read();
        if (wasClosed)
        {
            connection.Close();
        }

        return hasTables;
    }
}
