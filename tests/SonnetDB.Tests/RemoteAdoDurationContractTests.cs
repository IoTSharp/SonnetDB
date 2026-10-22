using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SonnetDB.Data;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>时长在三条真实 ADO 路径、事务和完整重开后保持原生值。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Durations_NativeRangeNullTransactionsAndReopen_PreserveTicks(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string connectionString = IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "duration-embedded"));
        long[] ticks = [long.MinValue, -1, 0, 972000000001, long.MaxValue];
        await using (var connection = new SndbConnection(connectionString))
        {
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE durations(id INT, elapsed DURATION NULL, PRIMARY KEY(id))";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT elapsed, CAST(NULL AS DURATION) FROM durations";
            using (var empty = command.ExecuteReader())
            {
                Assert.Equal(typeof(TimeSpan), empty.GetFieldType(0));
                Assert.Equal(typeof(TimeSpan), empty.GetFieldType(1));
                Assert.False(empty.Read());
            }
            command.Parameters.Add(new SndbParameter("id", 0L));
            command.Parameters.Add(new SndbParameter("elapsed", TimeSpan.Zero));
            await using (var transaction = await connection.BeginTransactionAsync(deadline.Token))
            {
                command.Transaction = transaction;
                for (int index = 0; index < ticks.Length; index++)
                {
                    command.Parameters[0].Value = (long)index;
                    command.Parameters[1].Value = TimeSpan.FromTicks(ticks[index]);
                    command.CommandText = "INSERT INTO durations(id,elapsed) VALUES(@id,@elapsed) RETURNING elapsed";
                    Assert.Equal(TimeSpan.FromTicks(ticks[index]), await command.ExecuteScalarAsync(deadline.Token));
                    command.CommandText = "SELECT elapsed FROM durations WHERE elapsed=@elapsed";
                    Assert.Equal(TimeSpan.FromTicks(ticks[index]), await command.ExecuteScalarAsync(deadline.Token));
                }
                await transaction.CommitAsync(deadline.Token);
                command.Transaction = null;
            }
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO durations(id,elapsed) VALUES(5,NULL),(6,CAST(1 AS DURATION))";
            Assert.Equal(2, command.ExecuteNonQuery());
            await using (var transaction = await connection.BeginTransactionAsync(deadline.Token))
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM durations WHERE id=0 RETURNING elapsed";
                Assert.Equal(TimeSpan.MinValue, await command.ExecuteScalarAsync(deadline.Token));
                await transaction.RollbackAsync(deadline.Token);
                command.Transaction = null;
            }
            command.CommandText = "SELECT CAST('2026-10-16T03:00:00.0000001Z' AS DATETIME)-CAST('2026-10-15T00:00:00Z' AS DATETIME) AS delta";
            Assert.Equal(TimeSpan.FromTicks(972000000001), command.ExecuteScalar());
        }
        if (protocol != "embedded")
        {
            await ReopenHostAsync(deadline.Token);
            connectionString = IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "duration-embedded"));
        }
        await using var reopened = new SndbConnection(connectionString);
        await reopened.OpenAsync(deadline.Token);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT elapsed FROM durations ORDER BY id";
        await using var reader = await query.ExecuteReaderAsync(deadline.Token);
        Assert.Equal(typeof(TimeSpan), reader.GetFieldType(0));
        Assert.Equal(typeof(TimeSpan), reader.GetColumnSchema()[0].DataType);
        Assert.Equal(typeof(TimeSpan), reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.DataType]);
        foreach (long expected in ticks)
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal(TimeSpan.FromTicks(expected), reader.GetFieldValue<TimeSpan>(0));
        }
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.True(reader.IsDBNull(0));
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.Equal(TimeSpan.FromTicks(1), reader.GetValue(0));
        Assert.False(await reader.ReadAsync(deadline.Token));
        if (protocol != "embedded")
            Assert.Contains(_requests, request => request.ResultVersion == "7"
                && request.Protocol == (protocol == "rest" ? "HTTP/1.1" : "HTTP/2"));
    }

    /// <summary>仅明确的执行前不支持版本错误触发有界重试，写入只发生一次。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task DurationNegotiation_OldV4AndActualSqlError_ExecuteOnce(string protocol)
    {
        await using var connection = new SndbConnection(ConnectionString(protocol == "rest" ? _http11Url : _frameH2Url, protocol));
        connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE once_only(id INT, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        _rejectSqlV5 = true;
        _requests.Clear();
        command.CommandText = "INSERT INTO once_only(id) VALUES(1)";
        Assert.Equal(1, command.ExecuteNonQuery());
        Assert.Equal(new[] { "7", "6", "5", "4" }, _requests.Select(request => request.ResultVersion));
        command.CommandText = "SELECT COUNT(*) FROM once_only";
        Assert.Equal(1L, command.ExecuteScalar());
        _rejectSqlV5 = false;
        _requests.Clear();
        command.CommandText = "INSERT INTO once_only(id) VALUES(1)";
        Assert.Throws<SndbServerException>(() => command.ExecuteNonQuery());
        Assert.Equal("7", Assert.Single(_requests).ResultVersion);
        command.CommandText = "SELECT COUNT(*) FROM once_only";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    /// <summary>旧 REST 请求的 DML RETURNING 与过程拒绝必须先于持久写入。</summary>
    [Fact]
    public async Task LegacyDurationRest_ReturningAndSequenceProcedure_RejectBeforeMutation()
    {
        await using var connection = new SndbConnection(ConnectionString(_http11Url, "rest"));
        connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE legacy_duration(id INT, elapsed DURATION NULL, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE SEQUENCE duration_sequence START WITH 10";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE PROCEDURE legacy_duration_proc() LANGUAGE SQL AS BEGIN SELECT nextval('duration_sequence'); INSERT INTO legacy_duration(id,elapsed) VALUES(2,CAST(1 AS DURATION)); SELECT CAST(NULL AS DURATION); END";
        command.ExecuteNonQuery();
        using var http = new HttpClient { BaseAddress = new Uri(_http11Url), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        foreach (string sql in new[] { "INSERT INTO legacy_duration(id,elapsed) VALUES(1,CAST(-1 AS DURATION)) RETURNING elapsed",
            "CALL legacy_duration_proc()" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/db/{DatabaseName}/sql")
            { Content = new StringContent("{\"sql\":\"" + sql + "\"}", Encoding.UTF8, "application/json") };
            request.Headers.Add(SqlFrameCodec.ResultVersionHeader, "4");
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        command.CommandText = "SELECT COUNT(*) FROM legacy_duration";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT nextval('duration_sequence')";
        Assert.Equal(10L, command.ExecuteScalar());
    }
}
