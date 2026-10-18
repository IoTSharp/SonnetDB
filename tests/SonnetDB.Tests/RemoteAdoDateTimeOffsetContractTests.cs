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
    /// <summary>原生时刻在三条真实路径、事务、空值及完整重开后保留全部 UTC ticks。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task NativeOffsets_TransactionsAndReopen_PreserveGenericReader(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string localRoot = Path.Combine(_dataRoot!, "native-offset-embedded");
        string connectionString = IntegralConnectionString(protocol, localRoot);
        long[] ticks = [0, 1, 639270596961230000, 639270596961230001, 3155378975999999999];
        await using (var connection = new SndbConnection(connectionString))
        {
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE native_offsets(id INT, Stamp DATETIMEOFFSET NULL, Legacy DATETIME NULL, PRIMARY KEY(id))";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT CURRENT_DATETIME_OFFSET(),DATE_ADD_DATETIME_OFFSET(NULL,1,'day')";
            using (var functions = command.ExecuteReader())
            {
                Assert.Equal(typeof(DateTimeOffset), functions.GetFieldType(0));
                Assert.Equal(typeof(DateTimeOffset), functions.GetFieldType(1));
                Assert.True(functions.Read());
                Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(functions.GetValue(0)).Offset);
                Assert.True(functions.IsDBNull(1));
            }
            command.CommandText = "SELECT CURRENT_DATETIME_OFFSET(),DATE_ADD_DATETIME_OFFSET(NULL,1,'day') FROM native_offsets WHERE FALSE";
            using (var functions = command.ExecuteReader())
            {
                Assert.Equal(typeof(DateTimeOffset), functions.GetFieldType(0));
                Assert.Equal(typeof(DateTimeOffset), functions.GetFieldType(1));
                Assert.False(functions.Read());
            }
            command.Parameters.Add(new SndbParameter("stamp", DBNull.Value) { DbType = DbType.DateTimeOffset });
            command.CommandText = "SELECT Stamp,@stamp FROM native_offsets";
            using (var empty = command.ExecuteReader())
            {
                Assert.Equal(typeof(DateTimeOffset), empty.GetFieldType(0));
                Assert.Equal(typeof(DateTimeOffset), empty.GetFieldType(1));
                Assert.False(empty.Read());
            }
            command.Parameters.Add(new SndbParameter("id", 0L));
            await using (var transaction = await connection.BeginTransactionAsync(deadline.Token))
            {
                command.Transaction = transaction;
                for (int index = 0; index < ticks.Length; index++)
                {
                    var value = new DateTimeOffset(ticks[index], TimeSpan.Zero);
                    command.Parameters[0].Value = value;
                    command.Parameters[1].Value = (long)index;
                    command.CommandText = "INSERT INTO native_offsets(id,Stamp) VALUES(@id,@stamp) RETURNING Stamp";
                    Assert.Equal(value, await command.ExecuteScalarAsync(deadline.Token));
                    command.CommandText = "SELECT Stamp FROM native_offsets WHERE Stamp=@stamp";
                    Assert.Equal(value, await command.ExecuteScalarAsync(deadline.Token));
                }
                command.Parameters[0].Value = DBNull.Value;
                command.CommandText = "SELECT Stamp,@stamp FROM native_offsets WHERE FALSE";
                using (var empty = command.ExecuteReader())
                {
                    Assert.Equal(typeof(DateTimeOffset), empty.GetFieldType(0));
                    Assert.Equal(typeof(DateTimeOffset), empty.GetFieldType(1));
                    Assert.False(empty.Read());
                }
                await transaction.CommitAsync(deadline.Token);
                command.Transaction = null;
            }
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO native_offsets(id,Stamp) VALUES(5,NULL)";
            Assert.Equal(1, command.ExecuteNonQuery());
            command.CommandText = "SELECT Stamp FROM native_offsets WHERE Stamp=Legacy LIMIT 0";
            Assert.ThrowsAny<Exception>(() => command.ExecuteReader());
            await using (var transaction = await connection.BeginTransactionAsync(deadline.Token))
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM native_offsets WHERE id=0 RETURNING Stamp";
                Assert.Equal(DateTimeOffset.MinValue, await command.ExecuteScalarAsync(deadline.Token));
                await transaction.RollbackAsync(deadline.Token);
                command.Transaction = null;
            }
        }
        if (protocol != "embedded")
        {
            await ReopenHostAsync(deadline.Token);
            connectionString = IntegralConnectionString(protocol, localRoot);
        }
        await using var reopened = new SndbConnection(connectionString);
        await reopened.OpenAsync(deadline.Token);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT Stamp FROM native_offsets ORDER BY id";
        await using var reader = await query.ExecuteReaderAsync(deadline.Token);
        Assert.Equal(typeof(DateTimeOffset), reader.GetFieldType(0));
        Assert.Equal(typeof(DateTimeOffset), reader.GetColumnSchema()[0].DataType);
        Assert.Equal(typeof(DateTimeOffset), reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.DataType]);
        foreach (long expected in ticks)
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            var value = Assert.IsType<DateTimeOffset>(reader.GetValue(0));
            Assert.Equal(expected, value.UtcTicks);
            Assert.Equal(TimeSpan.Zero, value.Offset);
            var values = new object[1];
            Assert.Equal(1, reader.GetValues(values));
            Assert.Equal(value, values[0]);
        }
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.True(reader.IsDBNull(0));
        Assert.False(await reader.ReadAsync(deadline.Token));
        if (protocol != "embedded")
            Assert.Contains(_requests, request => request.ResultVersion == "7"
                && request.Protocol == (protocol == "rest" ? "HTTP/1.1" : "HTTP/2"));
    }

    /// <summary>v5 执行前拒绝允许一次有界降级，显式旧 DATETIME 参数仍保持旧行为。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task NativeOffsetNegotiation_OldV5_ExecutesLegacyWriteOnce(string protocol)
    {
        await using var connection = new SndbConnection(ConnectionString(protocol == "rest" ? _http11Url : _frameH2Url, protocol));
        connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE legacy_offsets(id INT, Stamp DATETIME, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        var value = new DateTimeOffset(2026, 10, 8, 20, 34, 56, TimeSpan.FromHours(8)).AddTicks(1230000);
        command.Parameters.Add(new SndbParameter("stamp", value) { DbType = DbType.DateTime });
        _rejectSqlV6 = true;
        _requests.Clear();
        command.CommandText = "INSERT INTO legacy_offsets(id,Stamp) VALUES(1,@stamp)";
        Assert.Equal(1, command.ExecuteNonQuery());
        Assert.Equal(new[] { "7", "6", "5" }, _requests.Select(request => request.ResultVersion));
        command.CommandText = "SELECT Stamp FROM legacy_offsets WHERE Stamp=@stamp";
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds()).UtcDateTime, command.ExecuteScalar());
        _rejectSqlV6 = false;
        _requests.Clear();
        command.CommandText = "INSERT INTO legacy_offsets(id,Stamp) VALUES(1,@stamp)";
        Assert.Throws<SndbServerException>(() => command.ExecuteNonQuery());
        Assert.Equal("7", Assert.Single(_requests).ResultVersion);
    }

    /// <summary>旧协议看到原生声明时必须在 RETURNING、序列及过程副作用之前拒绝。</summary>
    [Fact]
    public async Task LegacyOffsetRest_EmptyReturningAndProcedure_RejectBeforeMutation()
    {
        await using var connection = new SndbConnection(ConnectionString(_http11Url, "rest"));
        connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE native_offsets(id INT, Stamp DATETIMEOFFSET NULL, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE SEQUENCE offset_sequence START WITH 10";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE PROCEDURE offset_proc() LANGUAGE SQL AS BEGIN SELECT nextval('offset_sequence'); INSERT INTO native_offsets(id,Stamp) VALUES(2,CAST(1 AS DATETIMEOFFSET)); SELECT CAST(NULL AS DATETIMEOFFSET); END";
        command.ExecuteNonQuery();
        using var http = new HttpClient { BaseAddress = new Uri(_http11Url), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        foreach (string sql in new[] { "SELECT Stamp FROM native_offsets WHERE FALSE",
            "INSERT INTO native_offsets(id,Stamp) VALUES(1,NULL) RETURNING Stamp", "CALL offset_proc()" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/db/{DatabaseName}/sql")
            { Content = new StringContent("{\"sql\":\"" + sql + "\"}", Encoding.UTF8, "application/json") };
            request.Headers.Add(SqlFrameCodec.ResultVersionHeader, "5");
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        command.CommandText = "SELECT COUNT(*) FROM native_offsets";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT nextval('offset_sequence')";
        Assert.Equal(10L, command.ExecuteScalar());
    }
}
