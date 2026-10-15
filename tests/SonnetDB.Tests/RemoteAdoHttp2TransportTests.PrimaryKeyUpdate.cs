using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>嵌入式及真实 REST/h2c 的同步异步参数改键、约束回滚、事务和完整重开一致。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task PrimaryKeyUpdate_ParametersTransactionsAndReopen_PreserveRowsAndIndexes(
        string protocol, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string embeddedRoot = Path.Combine(_dataRoot!, "embedded-pk312");
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            foreach (string sql in new[]
            {
                "CREATE TABLE Items(Id INT,Name STRING,GroupId INT,PRIMARY KEY(Id),CHECK(Id>0))",
                "CREATE UNIQUE INDEX ux_name ON Items(Name)",
                "CREATE INDEX ix_group ON Items(GroupId)",
                "INSERT INTO Items(Id,Name,GroupId) VALUES(1,'a',9),(2,'b',9)",
            })
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
            command.CommandText = "UPDATE \"Items\" SET \"Id\"=@newId WHERE \"Id\"=@oldId";
            var target = new SndbParameter("newId", 101L);
            command.Parameters.Add(target);
            command.Parameters.Add(new SndbParameter("oldId", 1L));
            Assert.Equal(1, asynchronous
                ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM Items WHERE Id=1";
            Assert.Equal(0L, await command.ExecuteScalarAsync(deadline.Token));
            command.CommandText = "SELECT Name FROM Items WHERE Id=101";
            Assert.Equal("a", asynchronous
                ? await command.ExecuteScalarAsync(deadline.Token) : command.ExecuteScalar());

            foreach (object invalid in new object[] { 2L, DBNull.Value, -1L })
            {
                command.CommandText = "UPDATE Items SET Id=@newId WHERE Id=101";
                target.Value = invalid;
                command.Parameters.Add(target);
                Exception error = asynchronous
                    ? await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteNonQueryAsync(deadline.Token))
                    : Assert.ThrowsAny<Exception>(() => command.ExecuteNonQuery());
                if (protocol == "embedded") Assert.IsAssignableFrom<InvalidOperationException>(error);
                else Assert.IsType<SndbServerException>(error);
                Assert.Contains(invalid is DBNull ? "NULL" : invalid.Equals(2L) ? "主键" : "CHECK", error.Message);
                command.Parameters.Clear();
                command.CommandText = "SELECT Id FROM Items WHERE Name='a'";
                Assert.Equal(101L, await command.ExecuteScalarAsync(deadline.Token));
            }

            foreach (bool commit in new[] { false, true })
            {
                await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
                command.Transaction = transaction;
                command.CommandText = "UPDATE Items SET Id=@newId WHERE Id=101";
                target.Value = 201L;
                command.Parameters.Add(target);
                Assert.Equal(1, asynchronous
                    ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
                command.Parameters.Clear();
                command.CommandText = "UPDATE Items SET Id=301 WHERE Id=201";
                Assert.Equal(1, asynchronous
                    ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery());
                command.CommandText = "SELECT COUNT(*) FROM Items WHERE Id IN (101,201)";
                Assert.Equal(0L, await command.ExecuteScalarAsync(deadline.Token));
                command.CommandText = "SELECT Name FROM Items WHERE Id=301";
                Assert.Equal("a", await command.ExecuteScalarAsync(deadline.Token));
                if (commit) await transaction.CommitAsync(deadline.Token);
                else await transaction.RollbackAsync(deadline.Token);
                command.Transaction = null;
            }
        }
        if (protocol != "embedded") await ReopenHostAsync(deadline.Token);
        await using var reopened = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot));
        await reopened.OpenAsync(deadline.Token);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT Id FROM Items WHERE Name='a'";
        Assert.Equal(301L, await query.ExecuteScalarAsync(deadline.Token));
        query.CommandText = "SELECT Id FROM Items WHERE GroupId=9 ORDER BY Id";
        await using (var reader = await query.ExecuteReaderAsync(deadline.Token))
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal(301L, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        query.CommandText = "SELECT COUNT(*) FROM Items WHERE Id IN (1,101,201)";
        Assert.Equal(0L, await query.ExecuteScalarAsync(deadline.Token));
        if (protocol != "embedded")
            Assert.Contains(_requests, request => request.Protocol == (protocol == "rest" ? "HTTP/1.1" : "HTTP/2")
                && request.Path == (protocol == "rest" ? $"/v1/db/{DatabaseName}/sql" : "/v1/frame"));
    }
}
