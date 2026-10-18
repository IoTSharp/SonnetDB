using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Data;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>同名 TEMP 的两个连接通过同一个持久过程读取各自会话数据。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ConnectionSession_SameTempName_StoredProcedureReadsOwningSession(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string suffix = Guid.NewGuid().ToString("N")[..10];
        string temporary = "shared_temp_" + suffix;
        string procedure = "read_temp_" + suffix;
        string connectionString = BuildSessionConnectionString(protocol, suffix);
        await using var first = new SndbConnection(connectionString);
        await using var second = new SndbConnection(connectionString);
        await first.OpenAsync(deadline.Token);
        await second.OpenAsync(deadline.Token);

        await ExecuteNonQueryAsync(first,
            $"CREATE TEMP TABLE {temporary}(Id INT,Value INT,PRIMARY KEY(Id))", null, deadline.Token);
        await ExecuteNonQueryAsync(second,
            $"CREATE TEMP TABLE {temporary}(Id INT,Value INT,PRIMARY KEY(Id))", null, deadline.Token);
        await ExecuteNonQueryAsync(first,
            $"INSERT INTO {temporary}(Id,Value) VALUES(1,11)", null, deadline.Token);
        await ExecuteNonQueryAsync(second,
            $"INSERT INTO {temporary}(Id,Value) VALUES(1,22)", null, deadline.Token);
        await ExecuteNonQueryAsync(first,
            $"CREATE PROCEDURE {procedure}() LANGUAGE SQL AS BEGIN SELECT Value FROM {temporary} WHERE Id=1; END",
            null, deadline.Token);

        Assert.Equal(11L, await ExecuteProcedureScalarAsync(first, procedure, deadline.Token));
        Assert.Equal(22L, await ExecuteProcedureScalarAsync(second, procedure, deadline.Token));
    }

    /// <summary>过程输出类型失败只撤销当前 CALL 的 TEMP 写，外层事务较早持久写仍可提交。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ConnectionSession_ProcedureOutputFailure_RollsBackCurrentTempWriteOnly(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string suffix = Guid.NewGuid().ToString("N")[..10];
        string temporary = "call_temp_" + suffix;
        string durable = "call_durable_" + suffix;
        string procedure = "write_temp_" + suffix;
        string connectionString = BuildSessionConnectionString(protocol, suffix);
        await using var owner = new SndbConnection(connectionString);
        await using var observer = new SndbConnection(connectionString);
        await owner.OpenAsync(deadline.Token);
        await observer.OpenAsync(deadline.Token);
        await ExecuteNonQueryAsync(owner,
            $"CREATE TEMP TABLE {temporary}(Id INT,PRIMARY KEY(Id))", null, deadline.Token);
        await ExecuteNonQueryAsync(owner,
            $"CREATE TABLE {durable}(Id INT,PRIMARY KEY(Id))", null, deadline.Token);
        await ExecuteNonQueryAsync(owner, $"""
            CREATE PROCEDURE {procedure}(IN row_id INT,OUT result STRING) LANGUAGE SQL AS BEGIN
              INSERT INTO {temporary}(Id) VALUES(@row_id);
              SET @result='done';
            END
            """, null, deadline.Token);

        await using (var transaction = await owner.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token))
        {
            await ExecuteNonQueryAsync(owner,
                $"INSERT INTO {durable}(Id) VALUES(1)", transaction, deadline.Token);
            await using var command = owner.CreateCommand();
            command.CommandText = procedure;
            command.CommandType = CommandType.StoredProcedure;
            command.Transaction = transaction;
            command.Parameters.Add(new SndbParameter("row_id", 7L));
            command.Parameters.Add(new SndbParameter("result", 0L)
            {
                Direction = ParameterDirection.Output,
                DbType = DbType.Int64,
            });
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await command.ExecuteNonQueryAsync(deadline.Token));
            Assert.Equal(0L, await ScalarInt64Async(owner,
                $"SELECT COUNT(*) FROM {temporary}", transaction, deadline.Token));
            Assert.Equal(1L, await ScalarInt64Async(owner,
                $"SELECT COUNT(*) FROM {durable}", transaction, deadline.Token));
            Assert.Equal(0L, await ScalarInt64Async(observer,
                $"SELECT COUNT(*) FROM {durable}", null, deadline.Token));
            await transaction.CommitAsync(deadline.Token);
        }
        Assert.Equal(1L, await ScalarInt64Async(observer,
            $"SELECT COUNT(*) FROM {durable}", null, deadline.Token));
        Assert.Equal(0L, await ScalarInt64Async(owner,
            $"SELECT COUNT(*) FROM {temporary}", null, deadline.Token));
    }

    /// <summary>旧服务端没有连接会话能力时，在发送 TEMP SQL 前稳定拒绝且不回落或重试业务请求。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ConnectionSession_UnsupportedServer_RejectsBeforeTempSqlDispatch(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _disableConnectionSessions = true;
        _requests.Clear();
        await using var connection = new SndbConnection(ConnectionString(
            protocol == "rest" ? _http11Url : _frameH2Url, protocol));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMP TABLE unsupported_temp(Id INT,PRIMARY KEY(Id))";

        var error = await Assert.ThrowsAsync<SndbServerException>(async () =>
            await command.ExecuteNonQueryAsync(deadline.Token));

        Assert.Equal("connection_session_not_supported", error.Error);
        Assert.Single(_requests, request => request.Path == $"/v1/db/{DatabaseName}/sql/sessions");
        Assert.DoesNotContain(_requests, request => request.Path == $"/v1/db/{DatabaseName}/sql"
            || request.Path == "/v1/frame");
    }

    /// <summary>畸形超大创建响应在 16 KiB 预算前拒绝，不发送或重放 TEMP SQL。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ConnectionSession_OversizedCreateResponse_IsRejectedWithoutTempDispatch(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _oversizeConnectionSessionResponse = true;
        _requests.Clear();
        await using var connection = new SndbConnection(ConnectionString(
            protocol == "rest" ? _http11Url : _frameH2Url, protocol));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMP TABLE oversized_temp(Id INT,PRIMARY KEY(Id))";

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await command.ExecuteNonQueryAsync(deadline.Token));

        Assert.Contains("超过字节预算", error.Message, StringComparison.Ordinal);
        Assert.Single(_requests, request => request.Path == $"/v1/db/{DatabaseName}/sql/sessions");
        Assert.DoesNotContain(_requests, request => request.Path == $"/v1/db/{DatabaseName}/sql"
            || request.Path == "/v1/frame");
    }

    /// <summary>三种 ADO 路径共享连接级 TEMP、事务和关闭语义，并实跑一万行集合更新。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ConnectionSession_TenThousandRowTempCtas_CommitRollbackAndCloseAreIsolated(
        string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string suffix = Guid.NewGuid().ToString("N")[..10];
        string target = "session_target_" + suffix;
        string staged = "session_stage_" + suffix;
        string procedure = "session_proc_" + suffix;
        string? embeddedRoot = protocol == "embedded"
            ? Path.Combine(_dataRoot!, "embedded-session-" + suffix)
            : null;
        string connectionString = protocol == "embedded"
            ? $"Data Source={embeddedRoot}"
            : ConnectionString(protocol == "rest" ? _http11Url : _frameH2Url, protocol);

        await using var owner = new SndbConnection(connectionString);
        await using var observer = new SndbConnection(connectionString);
        await owner.OpenAsync(deadline.Token);
        await observer.OpenAsync(deadline.Token);

        await ExecuteNonQueryAsync(owner,
            $"CREATE TABLE {target}(Id INT,Value INT,PRIMARY KEY(Id))", null, deadline.Token);
        await InsertBatchesAsync(owner, target, 0, transaction: null, deadline.Token);
        await ExecuteNonQueryAsync(owner,
            $"CREATE TEMP TABLE {staged} AS SELECT Id,Value FROM {target} WHERE Id<0", null, deadline.Token);
        await InsertBatchesAsync(owner, staged, 5, transaction: null, deadline.Token);

        Assert.Contains(owner.GetSchema("Tables").Rows.Cast<DataRow>(),
            row => string.Equals((string)row["TABLE_NAME"], staged, StringComparison.Ordinal));
        Assert.DoesNotContain(observer.GetSchema("Tables").Rows.Cast<DataRow>(),
            row => string.Equals((string)row["TABLE_NAME"], staged, StringComparison.Ordinal));
        Assert.Equal(10_000L, await ScalarInt64Async(owner,
            $"SELECT COUNT(*) FROM {staged}", null, deadline.Token));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await ScalarInt64Async(observer, $"SELECT COUNT(*) FROM {staged}", null, deadline.Token));
        await ExecuteNonQueryAsync(owner,
            $"CREATE PROCEDURE {procedure}() LANGUAGE SQL AS BEGIN SELECT COUNT(*) AS total FROM {staged}; END",
            null, deadline.Token);
        Assert.Equal(10_000L, await ExecuteProcedureScalarAsync(owner, procedure, deadline.Token));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await ExecuteProcedureScalarAsync(observer, procedure, deadline.Token));

        await using (var rollback = await owner.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token))
        {
            Assert.Equal(10_000, await ExecuteNonQueryAsync(owner,
                $"UPDATE {target} AS T SET Value=S.Value FROM {staged} AS S WHERE T.Id=S.Id",
                rollback, deadline.Token));
            Assert.Equal(50_000L, await ScalarInt64Async(owner,
                $"SELECT SUM(Value) FROM {target}", rollback, deadline.Token));
            Assert.Equal(0L, await ScalarInt64Async(observer,
                $"SELECT SUM(Value) FROM {target}", null, deadline.Token));
            await rollback.RollbackAsync(deadline.Token);
        }
        Assert.Equal(0L, await ScalarInt64Async(observer,
            $"SELECT SUM(Value) FROM {target}", null, deadline.Token));
        Assert.Equal(10_000L, await ScalarInt64Async(owner,
            $"SELECT COUNT(*) FROM {staged}", null, deadline.Token));

        await using (var commit = await owner.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token))
        {
            Assert.Equal(10_000, await ExecuteNonQueryAsync(owner,
                $"UPDATE {target} AS T SET Value=S.Value FROM {staged} AS S WHERE T.Id=S.Id",
                commit, deadline.Token));
            await commit.CommitAsync(deadline.Token);
        }
        Assert.Equal(50_000L, await ScalarInt64Async(observer,
            $"SELECT SUM(Value) FROM {target}", null, deadline.Token));
        Assert.Equal(10_000L, await ScalarInt64Async(owner,
            $"SELECT COUNT(*) FROM {staged}", null, deadline.Token));

        owner.Close();
        if (protocol != "embedded")
            Assert.Equal(0, _app!.Services.GetRequiredService<SqlConnectionSessions>().ActiveCount);
        await owner.OpenAsync(deadline.Token);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await ScalarInt64Async(owner, $"SELECT COUNT(*) FROM {staged}", null, deadline.Token));

        if (protocol == "frame-http2")
        {
            Assert.Contains(_requests, request => request.Path == "/v1/frame" && request.Protocol == "HTTP/2");
            Assert.Contains(_requests, request => request.Path.EndsWith("/sql/sessions", StringComparison.Ordinal)
                && request.Protocol == "HTTP/2");
        }
    }

    /// <summary>过期、认证变化和错库均拒绝复用同一连接 ID，且过期请求不会自动重建或执行 TEMP。</summary>
    [Fact]
    public async Task ConnectionSession_ExpiredWrongPrincipalOrDatabase_IsRejectedWithoutReplay()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string id = await CreateRawConnectionSessionAsync(_http11Url, AdminToken, DatabaseName, deadline.Token);
        var registry = _app!.Services.GetRequiredService<TsdbRegistry>();
        Assert.True(registry.TryGet(DatabaseName, out var database));
        var sessions = _app.Services.GetRequiredService<SqlConnectionSessions>();

        using (var lease = await sessions.AcquireAsync(
            id, database, DatabaseName, "Bearer " + AdminToken, deadline.Token))
        {
            Assert.NotNull(lease);
            lease!.Session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        }

        using var expired = await SendRawSqlAsync(
            _http11Url, AdminToken, DatabaseName, id,
            "CREATE TEMP TABLE must_not_exist(Id INT,PRIMARY KEY(Id))", deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Contains("connection_session_missing", await expired.Content.ReadAsStringAsync(deadline.Token),
            StringComparison.Ordinal);
        Assert.Equal(0, sessions.ActiveCount);
        Assert.Null(database.Tables.Catalog.TryGet("must_not_exist"));

        string active = await CreateRawConnectionSessionAsync(_http11Url, AdminToken, DatabaseName, deadline.Token);
        using var wrongPrincipal = await SendRawSqlAsync(
            _http11Url, "ado-h2-other", DatabaseName, active, "SELECT 1", deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, wrongPrincipal.StatusCode);

        const string otherDatabase = "ado_h2_other";
        _ = registry.TryCreate(otherDatabase, out _);
        using var wrongDatabase = await SendRawSqlAsync(
            _http11Url, AdminToken, otherDatabase, active, "SELECT 1", deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, wrongDatabase.StatusCode);
        Assert.Equal(1, sessions.ActiveCount);
    }

    /// <summary>事务 ID 绑定 connection A 后，不能携带 connection B 的独立 header 执行。</summary>
    [Fact]
    public async Task ConnectionSession_TransactionIdCannotBeReboundToAnotherConnection()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string first = await CreateRawConnectionSessionAsync(_http11Url, AdminToken, DatabaseName, deadline.Token);
        string second = await CreateRawConnectionSessionAsync(_http11Url, AdminToken, DatabaseName, deadline.Token);
        string transaction = await CreateRawTransactionAsync(
            _http11Url, AdminToken, DatabaseName, first, deadline.Token);

        using var wrong = await SendRawTransactionSqlAsync(
            _http11Url, AdminToken, DatabaseName, transaction, second, "SELECT 1", deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Contains("transaction_missing", await wrong.Content.ReadAsStringAsync(deadline.Token),
            StringComparison.Ordinal);

        using var rollback = await SendRawTransactionActionAsync(
            _http11Url, AdminToken, DatabaseName, transaction, first, "rollback", deadline.Token);
        Assert.True(rollback.IsSuccessStatusCode);
    }

    private static async Task InsertBatchesAsync(
        SndbConnection connection,
        string table,
        long value,
        SndbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int batch = 0; batch < 10; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(60))
                throw new TimeoutException("一万行 ADO 夹具写入超过 60 秒预算。");
            var sql = new StringBuilder($"INSERT INTO {table}(Id,Value) VALUES ");
            for (int index = 1; index <= 1_000; index++)
            {
                if (index != 1) sql.Append(',');
                sql.Append('(').Append(batch * 1_000 + index).Append(',').Append(value).Append(')');
            }
            Assert.Equal(1_000, await ExecuteNonQueryAsync(
                connection, sql.ToString(), transaction, cancellationToken));
        }
    }

    private static async Task<int> ExecuteNonQueryAsync(
        SndbConnection connection,
        string sql,
        SndbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarInt64Async(
        SndbConnection connection,
        string sql,
        SndbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ExecuteProcedureScalarAsync(
        SndbConnection connection,
        string procedure,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> CreateRawConnectionSessionAsync(
        string baseUrl,
        string token,
        string database,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.PostAsync(
            $"/v1/db/{database}/sql/sessions", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidDataException("连接会话响应缺少 id。");
    }

    private static async Task<string> CreateRawTransactionAsync(
        string baseUrl,
        string token,
        string database,
        string connectionSessionId,
        CancellationToken cancellationToken)
    {
        using var http = CreateRawHttpClient(baseUrl, token);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/db/{database}/sql/transactions");
        request.Headers.Add(SqlConnectionSessions.HeaderName, connectionSessionId);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidDataException("事务响应缺少 id。");
    }

    private static async Task<HttpResponseMessage> SendRawTransactionSqlAsync(
        string baseUrl,
        string token,
        string database,
        string transactionId,
        string connectionSessionId,
        string sql,
        CancellationToken cancellationToken)
    {
        var http = CreateRawHttpClient(baseUrl, token);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/db/{database}/sql/transactions/{transactionId}/sql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { sql }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SqlConnectionSessions.HeaderName, connectionSessionId);
        HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        http.Dispose();
        return response;
    }

    private static async Task<HttpResponseMessage> SendRawTransactionActionAsync(
        string baseUrl,
        string token,
        string database,
        string transactionId,
        string connectionSessionId,
        string action,
        CancellationToken cancellationToken)
    {
        var http = CreateRawHttpClient(baseUrl, token);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/db/{database}/sql/transactions/{transactionId}/{action}");
        request.Headers.Add(SqlConnectionSessions.HeaderName, connectionSessionId);
        HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        http.Dispose();
        return response;
    }

    private string BuildSessionConnectionString(string protocol, string suffix)
        => protocol == "embedded"
            ? $"Data Source={Path.Combine(_dataRoot!, "embedded-session-combo-" + suffix)}"
            : ConnectionString(protocol == "rest" ? _http11Url : _frameH2Url, protocol);

    private static HttpClient CreateRawHttpClient(string baseUrl, string token)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<HttpResponseMessage> SendRawSqlAsync(
        string baseUrl,
        string token,
        string database,
        string sessionId,
        string sql,
        CancellationToken cancellationToken)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/db/{database}/sql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { sql }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SqlConnectionSessions.HeaderName, sessionId);
        HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        http.Dispose();
        return response;
    }
}
