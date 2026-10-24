using System.Data;
using SonnetDB.Data;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>
/// 嵌入式 ADO.NET 双连接隔离合同：未提交写不可见、读己之写、隔离级别回报及冲突后整笔重试。
/// </summary>
public sealed class SndbIsolationAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-acid-i-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(IsolationLevel.Unspecified)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public void ReadCommitted_TwoConnections_HidesUncommittedWritesAndReadsOwnWrites(IsolationLevel requested)
    {
        using var writer = Open();
        using var observer = Open();
        Execute(writer, "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))");
        Execute(writer, "INSERT INTO items(id,value) VALUES(1,10)");

        using var transaction = writer.BeginTransaction(requested);
        Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
        Execute(writer, "INSERT INTO items(id,value) VALUES(2,20)", transaction);
        Execute(writer, "UPDATE items SET value = 11 WHERE id = 1", transaction);

        // A second connection only sees the last committed state.
        Assert.Equal(1L, Scalar(observer, "SELECT COUNT(*) FROM items"));
        Assert.Equal(10L, Scalar(observer, "SELECT value FROM items WHERE id = 1"));
        // The transaction sees both of its buffered mutations.
        Assert.Equal(2L, Scalar(writer, "SELECT COUNT(*) FROM items", transaction));
        Assert.Equal(11L, Scalar(writer, "SELECT value FROM items WHERE id = 1", transaction));

        transaction.Commit();
        Assert.Equal(2L, Scalar(observer, "SELECT COUNT(*) FROM items"));
        Assert.Equal(11L, Scalar(observer, "SELECT value FROM items WHERE id = 1"));
    }

    [Fact]
    public void Serializable_ConflictEndsTransactionAndAllowsWholeRetry()
    {
        using var first = Open();
        using var second = Open();
        Execute(first, "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))");
        Execute(first, "INSERT INTO items(id,value) VALUES(1,10)");

        using var stale = first.BeginTransaction(IsolationLevel.Serializable);
        Assert.Equal(10L, Scalar(first, "SELECT value FROM items WHERE id = 1", stale));
        Execute(second, "UPDATE items SET value = 20 WHERE id = 1");
        Assert.Equal(20L, Scalar(first, "SELECT value FROM items WHERE id = 1", stale));

        var error = Assert.Throws<TableConstraintException>(() => stale.Commit());
        Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
        Assert.Throws<InvalidOperationException>(() => stale.Commit());

        // A conflict ends the old transaction; the caller must begin again and reread.
        using var retry = first.BeginTransaction(IsolationLevel.Serializable);
        Assert.Equal(20L, Scalar(first, "SELECT value FROM items WHERE id = 1", retry));
        Execute(first, "UPDATE items SET value = 21 WHERE id = 1", retry);
        retry.Commit();
        Assert.Equal(21L, Scalar(first, "SELECT value FROM items WHERE id = 1"));
    }

    [Fact]
    public async Task Serializable_AsyncConflictEndsTransactionAndAllowsWholeRetry()
    {
        await using var first = new SndbConnection($"Data Source={_root}");
        await using var second = new SndbConnection($"Data Source={_root}");
        await first.OpenAsync();
        await second.OpenAsync();
        await ExecuteAsync(first, "CREATE TABLE async_items(id INT,value INT,PRIMARY KEY(id))");
        await ExecuteAsync(first, "INSERT INTO async_items(id,value) VALUES(1,10)");

        await using var stale = await first.BeginTransactionAsync(IsolationLevel.Serializable);
        Assert.Equal(10L, await ScalarAsync(first, "SELECT value FROM async_items WHERE id = 1", stale));
        await ExecuteAsync(second, "UPDATE async_items SET value = 20 WHERE id = 1");
        Assert.Equal(20L, await ScalarAsync(first, "SELECT value FROM async_items WHERE id = 1", stale));

        var error = await Assert.ThrowsAsync<TableConstraintException>(() => stale.CommitAsync());
        Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale.CommitAsync());

        await using var retry = await first.BeginTransactionAsync(IsolationLevel.Serializable);
        Assert.Equal(20L, await ScalarAsync(first, "SELECT value FROM async_items WHERE id = 1", retry));
        await ExecuteAsync(first, "UPDATE async_items SET value = 21 WHERE id = 1", retry);
        await retry.CommitAsync();
        Assert.Equal(21L, await ScalarAsync(first, "SELECT value FROM async_items WHERE id = 1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SndbConnection Open()
    {
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        return connection;
    }

    private static void Execute(SndbConnection connection, string sql, SndbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SndbConnection connection, string sql, SndbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return Assert.IsType<long>(command.ExecuteScalar());
    }

    private static async Task ExecuteAsync(SndbConnection connection, string sql, SndbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SndbConnection connection, string sql,
        SndbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }
}
