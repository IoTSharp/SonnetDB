using System.Buffers;
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
    [Fact]
    public async Task PreciseDateTimes_FrameExecuteExistingKind12_AcceptsFullTicksAndTypedNullUnderV7()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString("frame-http2", Path.Combine(_dataRoot!, "unused-native-frame-date2")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE frame_precise(id INT,Stamp DATETIME2 NULL,PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        using var http = new HttpClient
        {
            BaseAddress = new Uri(_frameH2Url),
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        http.DefaultRequestHeaders.Add(SqlFrameCodec.ResultVersionHeader, "7");
        for (int index = 0; index < 2; index++)
        {
            var writer = new ArrayBufferWriter<byte>();
            SqlExecuteFrameCodec.EncodeRequest(writer, 1, DatabaseName,
                $"INSERT INTO frame_precise(id,Stamp) VALUES({index},@stamp)",
                [new("stamp", SqlFrameParameterKind.DateTime, index == 0 ? DateTime.UnixEpoch.AddTicks(1) : null)]);
            using var content = new ByteArrayContent(writer.WrittenMemory.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
            using var response = await http.PostAsync("v1/frame", content, deadline.Token);
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var frames = new ReadOnlySequence<byte>(await response.Content.ReadAsByteArrayAsync(deadline.Token));
            Assert.True(FrameCodec.TryReadFrame(ref frames, out var header, out var payload));
            Assert.False(header.IsError, header.IsError ? FrameCodec.ReadErrorPayload(payload.ToArray()).Message : string.Empty);
            Assert.Equal(1L, SqlExecuteFrameCodec.DecodeResponse(payload.ToArray()).RecordsAffected);
            Assert.Equal(0, frames.Length);
        }
        command.CommandText = "SELECT Stamp FROM frame_precise ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        AssertPreciseDateTimeDeclaration(reader, 0);
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.Equal(DateTime.UnixEpoch.AddTicks(1), reader.GetValue(0));
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.True(reader.IsDBNull(0));
        Assert.False(await reader.ReadAsync(deadline.Token));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task PreciseDateTimes_CancelDuringCapabilityProbe_DoesNotSendBusinessWrite(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "unused-cancel-date2")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE cancelled_dates(id INT,Stamp DATETIME2,PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "INSERT INTO cancelled_dates(id,Stamp) VALUES(1,@stamp)";
        command.Parameters.Add(new SndbParameter("stamp", DateTime.UnixEpoch.AddTicks(1)));
        _requests.Clear();
        _probeDateTime2Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task pending = command.ExecuteNonQueryAsync(cancellation.Token);
        await _probeDateTime2Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("7", Assert.Single(_requests).ResultVersion);
        _probeDateTime2Arrived = null;
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM cancelled_dates";
        Assert.Equal(0L, await command.ExecuteScalarAsync(deadline.Token));
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task PreciseDateTimes_ExplicitOffsetParameter_PreservesDateTime2AndRejectsLegacyLoss(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "offset-date2")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE offset_date2(id INT,Stamp DATETIME2 NULL,PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        var input = new DateTimeOffset(2026, 10, 8, 20, 34, 56, TimeSpan.FromHours(8)).AddTicks(1230001);
        command.Parameters.Add(new SndbParameter("stamp", input) { DbType = DbType.DateTime2 });
        command.CommandText = "INSERT INTO offset_date2(id,Stamp) VALUES(1,@stamp) RETURNING Stamp";
        DateTime precise = Assert.IsType<DateTime>(await command.ExecuteScalarAsync(deadline.Token));
        Assert.Equal(input.UtcTicks, precise.Ticks);
        Assert.Equal(DateTimeKind.Utc, precise.Kind);
        command.Parameters[0].DbType = DbType.DateTime;
        command.CommandText = "INSERT INTO offset_date2(id,Stamp) VALUES(2,@stamp)";
        await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteNonQueryAsync(deadline.Token));
        command.Parameters[0].DbType = DbType.DateTime2;
        command.Parameters[0].Value = DBNull.Value;
        command.CommandText = "SELECT @stamp";
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        AssertPreciseDateTimeDeclaration(reader, 0);
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.True(reader.IsDBNull(0));
        Assert.False(await reader.ReadAsync(deadline.Token));
    }

    [Fact]
    public async Task PreciseDateTimes_LegacyV6ReturningAndProcedure_RejectBeforeSequenceOrWrite()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString("rest", Path.Combine(_dataRoot!, "unused-legacy-date2")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        foreach (string sql in new[]
        {
            "CREATE TABLE date2_legacy_guard(id INT,Stamp DATETIME2 NULL,PRIMARY KEY(id))",
            "CREATE SEQUENCE date2_sequence START WITH 10",
            "CREATE PROCEDURE date2_proc() LANGUAGE SQL AS BEGIN SELECT nextval('date2_sequence'); INSERT INTO date2_legacy_guard(id,Stamp) VALUES(2,CAST(1 AS DATETIME2)); SELECT CAST(NULL AS DATETIME2); END",
        })
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(deadline.Token);
        }
        using var http = new HttpClient { BaseAddress = new Uri(_http11Url), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        foreach (string sql in new[]
        {
            "SELECT Stamp FROM date2_legacy_guard WHERE FALSE",
            "INSERT INTO date2_legacy_guard(id,Stamp) VALUES(1,NULL) RETURNING Stamp",
            "CALL date2_proc()",
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/db/{DatabaseName}/sql")
            { Content = new StringContent("{\"sql\":\"" + sql + "\"}", Encoding.UTF8, "application/json") };
            request.Headers.Add(SqlFrameCodec.ResultVersionHeader, "6");
            using var response = await http.SendAsync(request, deadline.Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        command.CommandText = "SELECT COUNT(*) FROM date2_legacy_guard";
        Assert.Equal(0L, await command.ExecuteScalarAsync(deadline.Token));
        command.CommandText = "SELECT nextval('date2_sequence')";
        Assert.Equal(10L, await command.ExecuteScalarAsync(deadline.Token));
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task PreciseDateTimes_ProcedureResult_DeclaresFullTicksBeforeFirstRead(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "procedure-dates")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE PROCEDURE precise_proc() LANGUAGE SQL AS BEGIN SELECT CAST(621355968000000001 AS DATETIME2) AS Stamp; END";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "precise_proc";
        command.CommandType = CommandType.StoredProcedure;
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        AssertPreciseDateTimeDeclaration(reader, 0);
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.Equal(DateTime.UnixEpoch.AddTicks(1), reader.GetValue(0));
        Assert.False(await reader.ReadAsync(deadline.Token));
    }

    [Theory]
    [InlineData("rest", false)]
    [InlineData("frame-http2", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", true)]
    public async Task PreciseDateTimes_OldOrUnacknowledgedCapability_RejectsBeforeBusinessWrite(string protocol, bool suppress)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "unused-v7")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE capability_dates(id INT,Stamp DATETIME2,PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.Parameters.Add(new SndbParameter("stamp", DateTime.UnixEpoch.AddTicks(1)));
        command.CommandText = "INSERT INTO capability_dates(id,Stamp) VALUES(1,@stamp)";
        _rejectSqlV7 = !suppress;
        _suppressSqlResultVersion = suppress;
        _requests.Clear();
        if (suppress)
            await Assert.ThrowsAsync<InvalidDataException>(() => command.ExecuteNonQueryAsync(deadline.Token));
        else
            Assert.Equal("unsupported_result_version", (await Assert.ThrowsAsync<SndbServerException>(
                () => command.ExecuteNonQueryAsync(deadline.Token))).Error);
        Assert.Equal("7", Assert.Single(_requests).ResultVersion);
        _rejectSqlV7 = false;
        _suppressSqlResultVersion = false;
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM capability_dates";
        Assert.Equal(0L, await command.ExecuteScalarAsync(deadline.Token));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task PreciseDateTimes_DisconnectAfterRealCommit_ReportsUnknownAndDoesNotResend(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string connectionString = IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "unused-abort"));
        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE aborted_dates(id INT,Stamp DATETIME2,PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.Parameters.Add(new SndbParameter("stamp", DateTime.UnixEpoch.AddTicks(1)));
        command.CommandText = "INSERT INTO aborted_dates(id,Stamp) VALUES(1,@stamp)";
        _abortSqlStatement = command.CommandText;
        _requests.Clear();
        SndbServerException failure = await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteNonQueryAsync(deadline.Token));
        Assert.Equal("sql_outcome_unknown", failure.Error);
        Assert.Equal(1, _abortedSqlWrites);
        Assert.Equal(2, _requests.Count);
        _abortSqlStatement = null;
        await using var independent = new SndbConnection(connectionString);
        await independent.OpenAsync(deadline.Token);
        await using var query = independent.CreateCommand();
        query.CommandText = "SELECT Stamp FROM aborted_dates";
        Assert.Equal(DateTime.UnixEpoch.AddTicks(1), await query.ExecuteScalarAsync(deadline.Token));
        query.CommandText = "SELECT COUNT(*) FROM aborted_dates";
        Assert.Equal(1L, await query.ExecuteScalarAsync(deadline.Token));
    }

    [Theory]
    [InlineData("embedded", DateTimeKind.Unspecified)]
    [InlineData("rest", DateTimeKind.Unspecified)]
    [InlineData("frame-http2", DateTimeKind.Unspecified)]
    [InlineData("embedded", DateTimeKind.Local)]
    [InlineData("rest", DateTimeKind.Local)]
    [InlineData("frame-http2", DateTimeKind.Local)]
    public async Task PreciseDateTimes_PositionalAndKind_NormalizeAtClientBoundary(string protocol, DateTimeKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "kind-dates")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        DateTime input = new DateTime(2026, 10, 8, 1, 2, 3, kind).AddTicks(4567890);
        command.Parameters.Add(new SndbParameter("stamp", input));
        command.CommandText = "SELECT ?";
        DateTime value = Assert.IsType<DateTime>(await command.ExecuteScalarAsync(deadline.Token));
        Assert.Equal(kind == DateTimeKind.Unspecified ? input.Ticks : input.ToUniversalTime().Ticks, value.Ticks);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task PreciseDateTimes_DefaultParametersMetadataAndReopen_PreserveFullTicks(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string localRoot = Path.Combine(_dataRoot!, "issue236-date2-embedded");
        string connectionString = IntegralConnectionString(protocol, localRoot);
        DateTime[] values =
        [
            DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
            DateTime.UnixEpoch.AddTicks(-1),
            DateTime.UnixEpoch.AddTicks(1),
            new DateTime(2026, 10, 8, 1, 2, 3, DateTimeKind.Utc).AddTicks(4_567_890),
            DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
        ];
        await using (var connection = new SndbConnection(connectionString))
        {
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE issue236_dates(id INT,Stamp DATETIME2 NULL,Legacy DATETIME NULL,PRIMARY KEY(id))";
            await command.ExecuteNonQueryAsync(deadline.Token);
            command.Parameters.Add(new SndbParameter("stamp", DBNull.Value) { DbType = DbType.DateTime2 });
            command.CommandText = "SELECT Stamp,@stamp FROM issue236_dates";
            await using (var empty = await command.ExecuteReaderAsync(deadline.Token))
            {
                AssertPreciseDateTimeDeclaration(empty, 0);
                AssertPreciseDateTimeDeclaration(empty, 1);
                Assert.False(await empty.ReadAsync(deadline.Token));
            }

            // No Scale=7 switch. The ordinary default DbType.Object must infer native DateTime2.
            command.Parameters[0].DbType = DbType.Object;
            command.Parameters.Add(new SndbParameter("id", 0L));
            await using (var transaction = await connection.BeginTransactionAsync(deadline.Token))
            {
                command.Transaction = transaction;
                for (int index = 0; index < values.Length; index++)
                {
                    command.Parameters[0].Value = values[index];
                    command.Parameters[1].Value = (long)index;
                    command.CommandText = "INSERT INTO issue236_dates(id,Stamp) VALUES(@id,@stamp) RETURNING Stamp";
                    DateTime returned = Assert.IsType<DateTime>(await command.ExecuteScalarAsync(deadline.Token));
                    Assert.Equal(values[index].Ticks, returned.Ticks);
                    Assert.Equal(DateTimeKind.Utc, returned.Kind);
                    command.CommandText = "SELECT Stamp FROM issue236_dates WHERE Stamp=@stamp";
                    Assert.Equal(values[index].Ticks,
                        Assert.IsType<DateTime>(await command.ExecuteScalarAsync(deadline.Token)).Ticks);
                }
                await transaction.CommitAsync(deadline.Token);
                command.Transaction = null;
            }
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO issue236_dates(id,Stamp) VALUES(5,NULL)";
            Assert.Equal(1, await command.ExecuteNonQueryAsync(deadline.Token));
        }

        if (protocol != "embedded")
        {
            await ReopenHostAsync(deadline.Token);
            connectionString = IntegralConnectionString(protocol, localRoot);
        }
        await using var reopened = new SndbConnection(connectionString);
        await reopened.OpenAsync(deadline.Token);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT Stamp FROM issue236_dates ORDER BY id";
        await using var reader = await query.ExecuteReaderAsync(deadline.Token);
        AssertPreciseDateTimeDeclaration(reader, 0);
        foreach (DateTime expected in values)
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            DateTime actual = Assert.IsType<DateTime>(reader.GetValue(0));
            Assert.Equal(expected.Ticks, actual.Ticks);
            Assert.Equal(DateTimeKind.Utc, actual.Kind);
            Assert.Equal(actual, reader.GetDateTime(0));
            object[] row = new object[1];
            Assert.Equal(1, reader.GetValues(row));
            Assert.Equal(actual, row[0]);
        }
        Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.True(reader.IsDBNull(0));
        Assert.False(await reader.ReadAsync(deadline.Token));
        if (protocol != "embedded")
            Assert.Contains(_requests, request => request.ResultVersion == "7"
                && request.Protocol == (protocol == "rest" ? "HTTP/1.1" : "HTTP/2"));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task PreciseDateTimes_MeasurementTimeParameterOrigin_SurvivesRealRemoteBoundary(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol,
            Path.Combine(_dataRoot!, "issue236-measurement-unused")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE MEASUREMENT issue236_clock(value FIELD INT)";
        await command.ExecuteNonQueryAsync(deadline.Token);
        DateTime value = new DateTime(2026, 10, 8, 1, 2, 3, DateTimeKind.Utc).AddTicks(4_567_890);
        long milliseconds = new DateTimeOffset(value).ToUnixTimeMilliseconds();
        command.Parameters.Add(new SndbParameter("stamp", value));
        command.CommandText = "SELECT time FROM issue236_clock WHERE time>=@stamp LIMIT 0";
        await using (var empty = await command.ExecuteReaderAsync(deadline.Token))
            Assert.False(await empty.ReadAsync(deadline.Token));
        command.CommandText = "INSERT INTO issue236_clock(time,value) VALUES(@stamp,1)";
        Assert.Equal(1, await command.ExecuteNonQueryAsync(deadline.Token));
        command.CommandText = "SELECT time FROM issue236_clock WHERE time=@stamp";
        Assert.Equal(milliseconds, await command.ExecuteScalarAsync(deadline.Token));
        command.Parameters.Clear();
        command.Parameters.Add(new SndbParameter("stamp", milliseconds));
        Assert.Equal(milliseconds, await command.ExecuteScalarAsync(deadline.Token));
        command.Parameters[0].Value = value;
        command.CommandText = "DELETE FROM issue236_clock WHERE time=@stamp";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.Parameters.Clear();
        command.CommandText = "SELECT time FROM issue236_clock";
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
            Assert.False(await reader.ReadAsync(deadline.Token));
        command.CommandText = "CREATE TABLE issue236_relation(id INT,time DATETIME2,PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.Parameters.Add(new SndbParameter("stamp", value));
        command.CommandText = "INSERT INTO issue236_relation(id,time) VALUES(1,@stamp)";
        Assert.Equal(1, await command.ExecuteNonQueryAsync(deadline.Token));
        command.CommandText = "SELECT time FROM issue236_relation WHERE time=@stamp";
        Assert.Equal(value, Assert.IsType<DateTime>(await command.ExecuteScalarAsync(deadline.Token)));
        Assert.Contains(_requests, request => request.Path.EndsWith("/sql", StringComparison.Ordinal)
            && request.Protocol == (protocol == "rest" ? "HTTP/1.1" : "HTTP/2"));
    }

    private static void AssertPreciseDateTimeDeclaration(DbDataReader reader, int ordinal)
    {
        Assert.Equal(typeof(DateTime), reader.GetFieldType(ordinal));
        Assert.Equal("DATETIME2", reader.GetDataTypeName(ordinal).ToUpperInvariant());
        Assert.Equal(typeof(DateTime), reader.GetColumnSchema()[ordinal].DataType);
        Assert.Equal(7, reader.GetColumnSchema()[ordinal].NumericScale);
        DataRow row = reader.GetSchemaTable()!.Rows[ordinal];
        Assert.Equal(typeof(DateTime), row[SchemaTableColumn.DataType]);
        Assert.Equal((int)DbType.DateTime2, row[SchemaTableColumn.ProviderType]);
    }
}
