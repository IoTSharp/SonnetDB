using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SonnetDB.Data;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>无日期的诊断及 schema 查询也使用明确的 v4 meta，并保持真实 h2c。</summary>
    [Fact]
    public async Task DateTimeCapability_ShowDescribeExplain_RemainUsableOverRealHttp2()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE date_introspection(id INT, created DATETIME NULL, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        _requests.Clear();
        foreach (string sql in new[] { "SHOW TABLES", "DESCRIBE TABLE date_introspection", "EXPLAIN SELECT created FROM date_introspection" })
        {
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(reader.FieldCount > 0);
            Assert.True(await reader.ReadAsync());
            while (await reader.ReadAsync()) { }
        }
        Assert.Equal(3, _requests.Count);
        Assert.All(_requests, request =>
        {
            Assert.Equal("/v1/frame", request.Path);
            Assert.Equal("HTTP/2", request.Protocol);
            Assert.Equal("6", request.ResultVersion);
        });
    }

    /// <summary>日期函数投影必须保留本地钟面、UTC 与无时区值的原生类型及 Kind。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task DateTimeFunctions_NativeValuesAndKinds_PreserveWallClock(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol,
            Path.Combine(_dataRoot!, "date-native-embedded")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        var utc = new DateTime(2024, 2, 28, 16, 30, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        (string Sql, DateTime Expected)[] cases =
        [
            ("CAST('2024-02-28T16:30:00Z' AS DATETIME)", utc),
            ("TO_LOCAL_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME))", local),
            ("TO_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME))", unspecified),
            ("DATE_ONLY(TO_LOCAL_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME)))", local.Date),
            ("DATE_ONLY(TO_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME)))", unspecified.Date),
            ("DATE_ADD_DATETIME(DATE_ONLY(TO_LOCAL_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME))),1,'day')", local.Date.AddDays(1)),
            ("DATE_ADD_DATETIME(TO_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME)),1,'tick')", unspecified.AddTicks(1)),
            ("COALESCE(NULL,DATE_ONLY(TO_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME))))", unspecified.Date),
            ("TO_UTC_DATETIME(TO_LOCAL_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME)))", utc),
            ("DATE_ONLY(DATE_ADD_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME),8,'hour'))", utc.AddHours(8).Date),
            ("DATE_ADD(TO_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME)),1,'day')", unspecified.AddDays(1)),
        ];
        var failures = new List<string>();
        foreach (var item in cases)
        {
            command.CommandText = "SELECT " + item.Sql + " AS value";
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            var before = reader.GetFieldType(0);
            Assert.True(await reader.ReadAsync(deadline.Token));
            object raw = reader.GetValue(0);
            if (before != typeof(DateTime) || reader.GetFieldType(0) != typeof(DateTime)
                || raw is not DateTime actual || actual.Ticks != item.Expected.Ticks || actual.Kind != item.Expected.Kind)
            {
                failures.Add($"{protocol}: {item.Sql}; before={before.Name}; value={raw:O}; runtime={raw.GetType().Name}; "
                    + $"kind={(raw is DateTime date ? date.Kind : null)}; expected={item.Expected:O}/{item.Expected.Kind}");
            }
        }
        foreach (bool utcClock in new[] { false, true })
        {
            DateTime beforeClock = utcClock ? DateTime.UtcNow : DateTime.Now;
            command.CommandText = utcClock
                ? "SELECT CURRENT_UTC_DATETIME(), DATE_ONLY(CURRENT_UTC_DATETIME())"
                : "SELECT CURRENT_DATETIME(), DATE_ONLY(CURRENT_DATETIME())";
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            var declared = new[] { reader.GetFieldType(0), reader.GetFieldType(1) };
            Assert.True(await reader.ReadAsync(deadline.Token));
            DateTime afterClock = utcClock ? DateTime.UtcNow : DateTime.Now;
            for (int ordinal = 0; ordinal < 2; ordinal++)
            {
                object raw = reader.GetValue(ordinal);
                DateTime lower = ordinal == 0 ? beforeClock.AddSeconds(-1) : beforeClock.Date;
                DateTime upper = ordinal == 0 ? afterClock.AddSeconds(1) : afterClock.Date;
                DateTimeKind kind = utcClock ? DateTimeKind.Utc : DateTimeKind.Local;
                if (declared[ordinal] != typeof(DateTime) || raw is not DateTime actual || actual.Kind != kind
                    || actual.Ticks < lower.Ticks || actual.Ticks > upper.Ticks)
                    failures.Add($"{protocol}: clock utc={utcClock} ordinal={ordinal}; declared={declared[ordinal].Name}; "
                        + $"value={raw:O}; runtime={raw.GetType().Name}; kind={(raw is DateTime date ? date.Kind : null)}; expected={lower:O}..{upper:O}/{kind}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        if (protocol != "embedded")
            Assert.Contains(_requests, request => request.ResultVersion == SqlFrameCodec.NativeDateTimeOffsetResultVersion
                && request.Protocol == (protocol == "rest" ? "HTTP/1.1" : "HTTP/2"));
    }

    /// <summary>日期函数的 NULL 和零行声明不依赖读取首行猜测类型。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task DateTimeFunctions_NullAndEmpty_DeclareNativeDateTime(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol,
            Path.Combine(_dataRoot!, "date-empty-embedded")));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE date_metadata(id INT, created DATETIME NULL, PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        string projection = "SELECT created, DATE_ONLY(created), DATE_ADD_DATETIME(created,1,'day'), "
            + "DATE_ONLY(DATE_ADD_DATETIME(created,1,'day')), COALESCE(NULL,DATE_ONLY(created)) FROM date_metadata";
        for (int phase = 0; phase < 2; phase++)
        {
            command.CommandText = projection;
            await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
            {
                for (int ordinal = 0; ordinal < 5; ordinal++)
                {
                    Assert.Equal(typeof(DateTime), reader.GetFieldType(ordinal));
                    Assert.Equal(typeof(DateTime), reader.GetColumnSchema()[ordinal].DataType);
                    Assert.True(reader.GetColumnSchema()[ordinal].AllowDBNull);
                    Assert.Equal(typeof(DateTime), reader.GetSchemaTable()!.Rows[ordinal][SchemaTableColumn.DataType]);
                }
                Assert.Equal(phase == 1, await reader.ReadAsync(deadline.Token));
                if (phase == 1)
                    for (int ordinal = 0; ordinal < 5; ordinal++) Assert.True(reader.IsDBNull(ordinal));
            }
            if (phase == 0)
            {
                command.CommandText = "INSERT INTO date_metadata(id,created) VALUES(1,NULL)";
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
        }
    }

    /// <summary>既有参数和持久 DATETIME 保持 Unix 毫秒时刻合同，单独于表达式传输验证。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task DateTimeParameters_ExistingInstantBindingAndStorage_AreExplicit(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string connectionString = IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "date-storage-embedded"));
        var inputs = new[]
        {
            new DateTime(2024, 2, 29, 0, 30, 0, DateTimeKind.Utc).AddTicks(12345),
            new DateTime(2024, 2, 29, 0, 30, 0, DateTimeKind.Local).AddTicks(12345),
            new DateTime(2024, 2, 29, 0, 30, 0, DateTimeKind.Unspecified).AddTicks(12345),
        };
        DateTime[] expected = inputs.Select(value => DateTimeOffset.FromUnixTimeMilliseconds(
            new DateTimeOffset(value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value)
                .ToUnixTimeMilliseconds()).UtcDateTime).ToArray();
        await using (var connection = new SndbConnection(connectionString))
        {
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE date_storage(id INT, created DATETIME NULL, PRIMARY KEY(id))";
            await command.ExecuteNonQueryAsync(deadline.Token);
            for (int index = 0; index < inputs.Length; index++)
            {
                command.Parameters.Clear();
                command.Parameters.Add(new SndbParameter("@stamp", inputs[index]));
                command.CommandText = "SELECT @stamp";
                Assert.Equal(new DateTimeOffset(expected[index]).ToUnixTimeMilliseconds(),
                    Assert.IsType<long>(await command.ExecuteScalarAsync(deadline.Token)));
                command.CommandText = $"INSERT INTO date_storage(id,created) VALUES({index + 1},@stamp)";
                Assert.Equal(1, await command.ExecuteNonQueryAsync(deadline.Token));
            }
        }
        if (protocol != "embedded")
        {
            await ReopenHostAsync(deadline.Token);
            connectionString = IntegralConnectionString(protocol, string.Empty);
        }
        await using var reopened = new SndbConnection(connectionString);
        await reopened.OpenAsync(deadline.Token);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT created FROM date_storage ORDER BY id";
        await using var reader = await query.ExecuteReaderAsync(deadline.Token);
        Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
        foreach (var instant in expected)
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            var actual = Assert.IsType<DateTime>(reader.GetValue(0));
            Assert.Equal(instant.Ticks, actual.Ticks);
            Assert.Equal(DateTimeKind.Utc, actual.Kind);
        }
        Assert.False(await reader.ReadAsync(deadline.Token));
    }

    /// <summary>v1–v3 保留旧 UTC 时间戳，只有 v4 明确确认新的墙钟/Kind 合同。</summary>
    [Fact]
    public async Task DateTimeFrames_OldAndNewResults_AreExplicitlyDifferent()
    {
        const string sql = "SELECT DATE_ONLY(TO_LOCAL_DATETIME(CAST('2024-02-28T16:30:00Z' AS DATETIME)))";
        DateTime expected = new DateTime(2024, 2, 28, 16, 30, 0, DateTimeKind.Utc).ToLocalTime().Date;
        foreach (string? version in new string?[] { null, "1", "2", "3", "4" })
        {
            var frames = await SendSqlFrameAsync(sql, version);
            SqlFrameCodec.DecodeQueryMetaFrameWithInfo(frames[0].Payload, out bool nativeDateTime);
            Assert.Equal(version == "4", nativeDateTime);
            var actual = Assert.IsType<DateTime>(SqlFrameCodec.DecodeQueryRowsFrame(frames[1].Payload, nativeDateTime)[0][0]);
            Assert.Equal(nativeDateTime ? expected.Ticks : expected.ToUniversalTime().Ticks, actual.Ticks);
            Assert.Equal(nativeDateTime ? DateTimeKind.Local : DateTimeKind.Utc, actual.Kind);
        }
    }

    /// <summary>新客户端面对旧 v3 服务只在明确的执行前拒绝后回退，结果仍遵循旧 UTC 合同。</summary>
    [Fact]
    public async Task DateTimeFrames_OldServerFallback_DoesNotClaimNativeContract()
    {
        _rejectSqlV4 = true;
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        _requests.Clear();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TO_DATETIME(CAST('2024-02-29T00:30:00Z' AS DATETIME))";
        var actual = Assert.IsType<DateTime>(await command.ExecuteScalarAsync());
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
        Assert.Equal(new DateTime(2024, 2, 29, 0, 30, 0).Ticks, actual.Ticks);
        Assert.Equal(new[] { "6", "5", "4", "3" }, _requests.Select(request => request.ResultVersion).ToArray());
    }

    /// <summary>REST 旧客户端继续接收 ISO 字符串；显式 v4 接收 ticks/Kind；未知版本在写入前拒绝。</summary>
    [Fact]
    public async Task DateTimeRest_NegotiationAndBatch_KeepLegacyAndRejectUnknownBeforeWrite()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_http11Url), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        foreach (string? version in new string?[] { null, "4" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/db/{DatabaseName}/sql/batch")
            {
                Content = new StringContent("{\"statements\":[{\"sql\":\"SELECT TO_DATETIME(CAST('2024-02-29T00:00:00Z' AS DATETIME))\"},{\"sql\":\"SELECT DATE_ONLY(CAST('2024-02-29T12:30:00Z' AS DATETIME))\"}]}", Encoding.UTF8, "application/json"),
            };
            if (version is not null) request.Headers.Add(SqlFrameCodec.ResultVersionHeader, version);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(version == "4", response.Headers.Contains(SqlFrameCodec.ResultVersionHeader));
            var rows = (await response.Content.ReadAsStringAsync()).Split('\n').Where(line => line.StartsWith('[')).ToArray();
            Assert.Equal(2, rows.Length);
            for (int index = 0; index < rows.Length; index++)
            {
                using var json = JsonDocument.Parse(rows[index]);
                var value = json.RootElement[0];
                Assert.Equal(version == "4" ? JsonValueKind.Object : JsonValueKind.String, value.ValueKind);
                if (version == "4")
                {
                    Assert.Equal(new DateTime(2024, 2, 29).Ticks, value.GetProperty("ticks").GetInt64());
                    Assert.Equal(index, value.GetProperty("kind").GetInt32());
                }
            }
        }
        using var invalid = new HttpRequestMessage(HttpMethod.Post, $"v1/db/{DatabaseName}/sql")
        {
            Content = new StringContent("{\"sql\":\"CREATE TABLE should_not_exist(id INT, PRIMARY KEY(id))\"}", Encoding.UTF8, "application/json"),
        };
        invalid.Headers.Add(SqlFrameCodec.ResultVersionHeader, "99");
        using var rejected = await http.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await using var connection = new SndbConnection(ConnectionString(_http11Url, "rest"));
        connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM should_not_exist";
        await Assert.ThrowsAsync<SndbServerException>(async () => await command.ExecuteReaderAsync());
    }

    /// <summary>旧 REST 忽略协商头时沿用原解析合同，写语句只发送一次。</summary>
    [Fact]
    public async Task DateTimeRest_OldServerWithoutAcknowledgement_DoesNotReplayWrites()
    {
        _suppressSqlResultVersion = true;
        await using var connection = new SndbConnection(ConnectionString(_http11Url, "rest"));
        connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE legacy_date_write(id INT, PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync();
        _requests.Clear();
        command.CommandText = "INSERT INTO legacy_date_write(id) VALUES(1)";
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.Single(_requests);
        command.CommandText = "SELECT CAST('2024-02-29T00:00:00Z' AS DATETIME)";
        var actual = Assert.IsType<DateTime>(await command.ExecuteScalarAsync());
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
    }

    /// <summary>会话事务结果也要原生传输，日期列/参数函数仍遵循已有 UTC 毫秒输入合同。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task DateTimeTransactions_ColumnsAndParameters_PreserveExpressionAndStorageContracts(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol, Path.Combine(_dataRoot!, "date-tx-embedded")));
        connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE date_tx(id INT, created DATETIME NULL, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
        command.Transaction = transaction;
        var input = new DateTime(2024, 2, 29, 0, 30, 0, DateTimeKind.Local);
        command.Parameters.Add(new SndbParameter("@date", input));
        command.CommandText = "INSERT INTO date_tx(id,created) VALUES(1,@date)";
        Assert.Equal(1, await command.ExecuteNonQueryAsync(deadline.Token));
        command.CommandText = "SELECT created, DATE_ONLY(created), DATE_ONLY(@date), "
            + "DATE_ONLY(TO_LOCAL_DATETIME(created)), TO_DATETIME(created) FROM date_tx";
        using (var reader = command.ExecuteReader())
        {
            DateTime utc = input.ToUniversalTime();
            var expected = new[] { utc, utc.Date, utc.Date, input.Date, DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) };
            for (int index = 0; index < expected.Length; index++) Assert.Equal(typeof(DateTime), reader.GetFieldType(index));
            Assert.True(reader.Read());
            for (int index = 0; index < expected.Length; index++)
            {
                var actual = Assert.IsType<DateTime>(reader.GetValue(index));
                Assert.Equal(expected[index].Ticks, actual.Ticks);
                Assert.Equal(expected[index].Kind, actual.Kind);
            }
        }
        await transaction.RollbackAsync(deadline.Token);
    }

    /// <summary>旧无会话服务的批次预览保留日期值及零行声明，明确区别于默认会话事务。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task DateTimeTransactions_LegacyBatchPreview_PreservesNativeValuesAndEmptyDeclarations(string protocol)
    {
        _disableTransactionSessions = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = new SndbConnection(ConnectionString(protocol == "rest" ? _http11Url : _frameH2Url, protocol));
        connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE legacy_date_tx(id INT, created DATETIME NULL, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
        command.Transaction = transaction;
        command.CommandText = "SELECT DATE_ONLY(created) FROM legacy_date_tx";
        using (var empty = command.ExecuteReader())
        {
            Assert.Equal(typeof(DateTime), empty.GetFieldType(0));
            Assert.True(empty.GetColumnSchema()[0].AllowDBNull);
            Assert.False(empty.Read());
        }
        command.CommandText = "SELECT TO_DATETIME(CAST('2024-02-29T00:00:00Z' AS DATETIME))";
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
        {
            Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
            Assert.True(await reader.ReadAsync(deadline.Token));
            var actual = Assert.IsType<DateTime>(reader.GetValue(0));
            Assert.Equal(new DateTime(2024, 2, 29).Ticks, actual.Ticks);
            Assert.Equal(DateTimeKind.Unspecified, actual.Kind);
        }
        Assert.Contains(_requests, request => request.Path.EndsWith("/sql/batch", StringComparison.Ordinal)
            && request.ResultVersion == "6");
        transaction.Rollback();
    }
}
