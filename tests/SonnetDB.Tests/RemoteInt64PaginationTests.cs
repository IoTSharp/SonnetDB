using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>三种连接方式同步/异步执行完整 Int64 分页，并保留空结果声明和参数身份。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task Pagination_Int64BoundariesParametersAndReopen_PreserveExactWindow(string protocol, bool useAsync)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string embeddedRoot = Path.Combine(_dataRoot!, "int64-pages");
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            if (useAsync) await connection.OpenAsync(deadline.Token);
            else connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText = "CREATE TABLE pages(id INT,label STRING,PRIMARY KEY(id))";
            await ExecuteAsync();
            command.CommandText = "INSERT INTO pages(id,label) VALUES(1,'c'),(2,'a'),(3,'b')";
            Assert.Equal(3, await ExecuteAsync());
            command.CommandText = "CREATE TABLE empty_pages(id INT,label STRING,PRIMARY KEY(id))";
            await ExecuteAsync();

            foreach (long boundary in new[] { 0L, 1L, 2147483647L, 2147483648L, long.MaxValue - 1, long.MaxValue })
            {
                await CheckAsync($"SELECT id FROM pages ORDER BY id LIMIT {boundary}", boundary == 0 ? [] : boundary == 1 ? [1L] : [1L, 2L, 3L]);
                await CheckAsync($"SELECT id FROM pages ORDER BY id LIMIT 9223372036854775807 OFFSET {boundary}", boundary == 0 ? [1L, 2L, 3L] : boundary == 1 ? [2L, 3L] : []);
                await CheckAsync($"SELECT id FROM pages ORDER BY label LIMIT 1 OFFSET {boundary}", boundary == 0 ? [2L] : boundary == 1 ? [3L] : []);
                await CheckAsync($"SELECT id FROM empty_pages LIMIT 1 OFFSET {boundary}", []);
            }
            await CheckAsync("SELECT id FROM pages LIMIT 0 OFFSET 9223372036854775807", []);
            await CheckAsync("SELECT id FROM pages WHERE id<0 LIMIT 9223372036854775807", []);

            var take = new SndbParameter("@take", long.MaxValue) { DbType = DbType.Int64 };
            var skip = new SndbParameter("@skip", long.MaxValue) { DbType = DbType.Int64 };
            command.Parameters.Add(take);
            command.Parameters.Add(skip);
            object expectedTake = take.Value!;
            object expectedSkip = skip.Value!;
            await CheckAsync("SELECT id FROM pages LIMIT @take OFFSET @skip", []);
            await CheckAsync("SELECT id FROM pages OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", []);
            Assert.Same(expectedTake, take.Value);
            Assert.Same(expectedSkip, skip.Value);
            Assert.Equal(DbType.Int64, take.DbType);
            Assert.Equal(DbType.Int64, skip.DbType);
            take.Value = 1L;
            skip.Value = 1L;
            await CheckAsync("SELECT id FROM pages ORDER BY id LIMIT @take OFFSET @skip", [2L]);
            foreach (long invalid in new[] { -1L, long.MinValue })
            {
                take.Value = invalid;
                skip.Value = 0L;
                command.CommandText = "SELECT id FROM empty_pages LIMIT @take OFFSET @skip";
                Assert.NotNull(await Record.ExceptionAsync(async () => { await using var rejected = await ReadAsync(); }));
                take.Value = 0L;
                skip.Value = invalid;
                Assert.NotNull(await Record.ExceptionAsync(async () => { await using var rejected = await ReadAsync(); }));
                Assert.Equal(invalid, skip.Value);
            }
            command.Parameters.Clear();
            foreach (string invalid in new[] { "-1", "9223372036854775808", "18446744073709551615", "1.5", "NULL", "'1'" })
            {
                command.CommandText = $"SELECT id FROM empty_pages LIMIT {invalid}";
                Assert.NotNull(await Record.ExceptionAsync(async () => { await using var rejected = await ReadAsync(); }));
                command.CommandText = $"SELECT id FROM empty_pages LIMIT 0 OFFSET {invalid}";
                Assert.NotNull(await Record.ExceptionAsync(async () => { await using var rejected = await ReadAsync(); }));
            }

            async Task CheckAsync(string sql, long[] expected)
            {
                command.CommandText = sql;
                await using var reader = await ReadAsync();
                Assert.Equal(typeof(long), reader.GetFieldType(0));
                Assert.Equal("id", reader.GetName(0));
                Assert.Equal(typeof(long), Assert.Single(reader.GetColumnSchema()).DataType);
                Assert.Equal(typeof(long), reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.DataType]);
                foreach (long id in expected)
                {
                    Assert.True(await MoveAsync(reader));
                    Assert.Equal(id, reader.GetInt64(0));
                }
                Assert.False(await MoveAsync(reader));
            }
            async Task<DbDataReader> ReadAsync()
                => useAsync ? await command.ExecuteReaderAsync(deadline.Token) : command.ExecuteReader();
            async Task<bool> MoveAsync(DbDataReader reader)
                => useAsync ? await reader.ReadAsync(deadline.Token) : reader.Read();
            async Task<int> ExecuteAsync()
                => useAsync ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery();
        }

        if (protocol != "embedded") await ReopenHostAsync(deadline.Token);
        await using (var reopened = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await reopened.OpenAsync(deadline.Token);
            await using var command = reopened.CreateCommand();
            command.CommandText = "SELECT id FROM pages ORDER BY id LIMIT 9223372036854775807 OFFSET 1";
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            foreach (long expected in new[] { 2L, 3L })
            {
                Assert.True(await reader.ReadAsync(deadline.Token));
                Assert.Equal(expected, reader.GetInt64(0));
            }
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        if (protocol != "embedded")
        {
            var requests = _requests.Where(request => request.Path == $"/v1/db/{DatabaseName}/sql" || request.Path == "/v1/frame").ToArray();
            Assert.NotEmpty(requests);
            Assert.All(requests, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
            Assert.Contains(requests, request => request.Path == $"/v1/db/{DatabaseName}/sql");
            if (protocol == "frame-http2") Assert.Contains(requests, request => request.Path == "/v1/frame");
        }
    }
}
