using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data;
using SonnetDB.Data.Remote;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>GH-Issue #196：真实网络和嵌入式的 measurement 参数化 JOIN 合同。</summary>
public sealed class RemoteMeasurementJoinContractTests : IAsyncLifetime
{
    private const string AdminToken = "measurement-join-admin";
    private const string DatabaseName = "measurement_join";
    private const string Projection = "SELECT a.time AS observed_at, a.\"Host\" AS host, b.\"Id\" AS device_id, a.\"Value\" AS reading ";
    private const string From = "FROM \"sonnet_metric\" AS a INNER JOIN \"sonnet_device\" AS b ON a.\"Host\" = b.\"Name\" ";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-measurement-join-remote-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private readonly ConcurrentQueue<ObservedRequest> _requests = new();
    private WebApplication? _app;
    private string _restUrl = string.Empty;
    private string _frameUrl = string.Empty;

    /// <summary>创建真实 HTTP/1.1、HTTP/2 端口和独立嵌入式数据副本。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_root);
            _app = TestServerHost.Build(new ServerOptions
            {
                DataRoot = Path.Combine(_root, "server"),
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
            }, extraArgs:
            [
                "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
                "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
            ]);
            _app.Use(async (context, next) =>
            {
                _requests.Enqueue(new ObservedRequest(context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
                await next(context);
            });
            await _app.StartAsync(_deadline.Token);
            var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未提供监听地址。");
            Assert.Equal(2, addresses.Addresses.Count);
            foreach (string address in addresses.Addresses)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                try
                {
                    using var response = await probe.SendAsync(request, _deadline.Token);
                    if (response.IsSuccessStatusCode) _restUrl = address;
                    else _frameUrl = address;
                }
                catch (HttpRequestException) { _frameUrl = address; }
            }
            Assert.NotEmpty(_restUrl);
            Assert.NotEmpty(_frameUrl);
            using var http = new HttpClient { BaseAddress = new Uri(_restUrl), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            using var create = await http.PostAsync("/v1/db",
                JsonContent.Create(new CreateDatabaseRequest(DatabaseName), ServerJsonContext.Default.CreateDatabaseRequest), _deadline.Token);
            create.EnsureSuccessStatusCode();
            string[] statements =
            [
                "CREATE MEASUREMENT sonnet_metric (Host TAG, Value FIELD FLOAT, Rank FIELD INT)",
                "CREATE TABLE sonnet_device (Id INT, Name STRING, PRIMARY KEY (Id))",
                "CREATE DOCUMENT COLLECTION sonnet_docs",
                "INSERT INTO sonnet_device (Id, Name) VALUES (1, 'alpha'), (2, 'beta')",
                "INSERT INTO sonnet_metric (time, Host, Value, Rank) VALUES (1000, 'alpha', 1.5, 30), (2000, 'alpha', 2.5, 10), (3000, 'alpha', 3.5, 40), (1500, 'beta', 8.5, 20), (4000, 'orphan', 9.5, 50)",
            ];
            foreach (string mode in new[] { "embedded", "rest" })
            {
                using var connection = await OpenAsync(mode);
                foreach (string statement in statements)
                {
                    _deadline.Token.ThrowIfCancellationRequested();
                    using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    command.CommandTimeout = 5;
                    await command.ExecuteNonQueryAsync(_deadline.Token);
                }
            }
            _requests.Clear();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>有界停止宿主并仅删除当前测试唯一拥有的数据目录。</summary>
    public async Task DisposeAsync()
    {
        try
        {
            WebApplication? app = _app;
            _app = null;
            if (app is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await app.StopAsync(stop.Token); }
                finally { await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            }
        }
        finally
        {
            _deadline.Dispose();
            string root = Path.GetFullPath(_root);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.StartsWith(temporaryRoot, root, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("sndb-measurement-join-remote-", Path.GetFileName(root), StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>同步/异步、无参/WHERE 参数、多行 FIELD 投影与分页跨入口返回一致结果。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task Execute_ParameterizedAndLiteralMeasurementJoin_MatchesEveryTransport(string mode, bool asynchronous)
    {
        using var connection = await OpenAsync(mode);
        await AssertQueryAsync(connection, mode, asynchronous, Projection + From + "ORDER BY a.time",
            [[1000L, "alpha", 1L, 1.5], [1500L, "beta", 2L, 8.5], [2000L, "alpha", 1L, 2.5], [3000L, "alpha", 1L, 3.5]]);
        await AssertQueryAsync(connection, mode, asynchronous,
            Projection + From + "WHERE a.time >= @begin AND b.\"Id\" = @device_id ORDER BY a.time",
            [[2000L, "alpha", 1L, 2.5], [3000L, "alpha", 1L, 3.5]], [("begin", 1500L), ("device_id", 1L)]);
        await AssertQueryAsync(connection, mode, asynchronous,
            "SELECT @marker AS marker, a.time AS observed_at " + From + "WHERE b.\"Id\" = @device_id ORDER BY a.time OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            [["quoted ' marker", 2000L]], [("marker", "quoted ' marker"), ("device_id", 1L), ("skip", 1L), ("take", 1L)]);
        await AssertQueryAsync(connection, mode, asynchronous,
            "SELECT a.time AS observed_at " + From + "WHERE a.time >= @begin ORDER BY a.Rank ASC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            [[1500L], [1000L]], [("begin", 0L), ("skip", 1L), ("take", 2L)]);
        await AssertQueryAsync(connection, mode, asynchronous,
            "SELECT a.time AS observed_at " + From + "WHERE a.time >= @begin ORDER BY a.Rank DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            [[1000L], [1500L]], [("begin", 0L), ("skip", 1L), ("take", 2L)]);
        await AssertQueryAsync(connection, mode, asynchronous,
            "SELECT a.time AS observed_at " + From + "WHERE a.time >= @begin ORDER BY b.Id ASC, a.Rank DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            [[1000L], [2000L]], [("begin", 0L), ("skip", 1L), ("take", 2L)]);
        await AssertQueryAsync(connection, mode, asynchronous,
            "SELECT a.time AS observed_at " + From + "WHERE a.time >= @begin ORDER BY b.Id DESC, a.Rank ASC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            [[2000L], [1000L]], [("begin", 0L), ("skip", 1L), ("take", 2L)]);
    }

    /// <summary>ON 参数和其他已声明不支持的形状得到稳定错误，合法查询仍可复用连接。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_UnsupportedMeasurementJoinShapes_ReturnsStableDiagnosticAndRecovers(string mode)
    {
        using var connection = await OpenAsync(mode);
        (string Sql, string Diagnostic)[] invalid =
        [
            ("SELECT a.time FROM sonnet_metric a LEFT JOIN sonnet_device b ON a.Host = b.Name", "当前仅支持关系表 FROM"),
            ("SELECT a.time FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = b.Name INNER JOIN sonnet_device c ON a.Host = c.Name", "仅支持一个关系维表"),
            ("SELECT a.time FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = b.Name AND b.Id = @device_id", "仅支持 ON 中一个等值条件"),
            ("SELECT a.time FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = @name", "等值两侧必须都是列引用"),
            ("SELECT a.time FROM sonnet_metric a INNER JOIN sonnet_docs b ON a.Host = b.id", "JOIN 右侧必须是关系表"),
            ("SELECT b.Id FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = b.Name GROUP BY b.Id", "不支持 GROUP BY"),
            ("SELECT COUNT(*) FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = b.Name", "不支持聚合函数"),
            ("SELECT COUNT(*) FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = b.Name WHERE a.time > 9999", "不支持聚合函数"),
            ("EXPLAIN SELECT COUNT(*) FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = b.Name WHERE a.time > 9999", "不支持聚合函数"),
        ];
        foreach (var item in invalid)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            string? previousMessage = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                _requests.Clear();
                using var command = connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = item.Sql;
                command.Parameters.AddWithValue("device_id", 1L);
                command.Parameters.AddWithValue("name", "alpha");
                Exception error;
                if (mode == "embedded")
                    error = await Assert.ThrowsAsync<InvalidOperationException>(async () => { using var reader = await command.ExecuteReaderAsync(_deadline.Token); });
                else
                {
                    var remote = await Assert.ThrowsAsync<SndbServerException>(async () => { using var reader = await command.ExecuteReaderAsync(_deadline.Token); });
                    Assert.Equal("sql_error", remote.Error);
                    error = remote;
                }
                Assert.Contains(item.Diagnostic, error.Message, StringComparison.Ordinal);
                if (previousMessage is not null) Assert.Equal(previousMessage, error.Message);
                previousMessage = error.Message;
                AssertTransport(mode);
            }
        }
        await AssertQueryAsync(connection, mode, true, Projection + From + "WHERE b.Id = 2", [[1500L, "beta", 2L, 8.5]]);
    }

    /// <summary>缺失参数由驱动绑定阶段拒绝，不发送可提前判断必败的 SQL。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_MissingWhereParameter_RejectsBeforeNetworkRequest(string mode)
    {
        using var connection = await OpenAsync(mode);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = Projection + From + "WHERE a.time >= @begin AND b.Id = @device_id";
        command.Parameters.AddWithValue("begin", 0L);
        _requests.Clear();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => { using var reader = await command.ExecuteReaderAsync(_deadline.Token); });
        Assert.Contains("device_id", error.Message, StringComparison.Ordinal);
        Assert.Empty(_requests);
    }

    /// <summary>Provider 可在生成前使用本地 SDK 能力排除已知不支持的模型/谓词组合。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task GetSchema_MeasurementJoinCapabilities_AreLocalAndModelSpecific(string mode)
    {
        using var connection = await OpenAsync(mode);
        _requests.Clear();
        DataRow capabilities = Assert.Single(connection.GetSchema("DataSourceInformation").Rows.Cast<DataRow>());
        Assert.Equal("INNER", capabilities["MeasurementJoinKinds"]);
        Assert.Equal(1, capabilities["MeasurementJoinMaxTables"]);
        Assert.Equal("measurement_tag_equals_relation_column", capabilities["MeasurementJoinOnPredicate"]);
        Assert.Equal("projection,where,pagination", capabilities["MeasurementJoinParameterLocations"]);
        Assert.False((bool)capabilities["MeasurementJoinSupportsAggregates"]);
        Assert.True((bool)capabilities["MeasurementJoinSupportsPagination"]);
        Assert.Empty(_requests);
    }

    /// <summary>轻事务可读取当前已提交 measurement 与关系表，远程事务读使用 REST 控制面。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_TransactionRead_PreservesMeasurementJoinResults(string mode)
    {
        using var connection = await OpenAsync(mode);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = Projection + From + "WHERE b.Id = @device_id ORDER BY a.time";
        command.Parameters.AddWithValue("device_id", 1L);
        _requests.Clear();
        using (var reader = await command.ExecuteReaderAsync(_deadline.Token))
            await AssertRowsAsync(reader, true, [[1000L, "alpha", 1L, 1.5], [2000L, "alpha", 1L, 2.5], [3000L, "alpha", 1L, 3.5]]);
        if (mode == "embedded") Assert.Empty(_requests);
        else
        {
            Assert.NotEmpty(_requests);
            Assert.DoesNotContain(_requests, request => request.Path == "/v1/frame");
            Assert.All(_requests, request => Assert.Equal(mode == "frame-http2" ? "HTTP/2" : "HTTP/1.1", request.Protocol));
        }
        transaction.Rollback();
    }

    private async Task AssertQueryAsync(SndbConnection connection, string mode, bool asynchronous, string sql,
        object?[][] expected, (string Name, object Value)[]? parameters = null)
    {
        _deadline.Token.ThrowIfCancellationRequested();
        _requests.Clear();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 5;
        if (parameters is not null)
        {
            Assert.InRange(parameters.Length, 0, 4);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        using var reader = asynchronous ? await command.ExecuteReaderAsync(_deadline.Token) : command.ExecuteReader();
        await AssertRowsAsync(reader, asynchronous, expected);
        AssertTransport(mode);
    }

    private async Task AssertRowsAsync(DbDataReader reader, bool asynchronous, object?[][] expected)
    {
        Assert.InRange(expected.Length, 1, 8);
        Assert.Equal(expected[0].Length, reader.FieldCount);
        foreach (object?[] expectedRow in expected)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            Assert.True(asynchronous ? await reader.ReadAsync(_deadline.Token) : reader.Read());
            for (int column = 0; column < expectedRow.Length; column++)
            {
                Assert.Equal(expectedRow[column], reader.GetValue(column));
                Assert.Equal(expectedRow[column]!.GetType(), reader.GetFieldType(column));
            }
        }
        Assert.False(asynchronous ? await reader.ReadAsync(_deadline.Token) : reader.Read());
    }

    private void AssertTransport(string mode)
    {
        if (mode == "embedded") { Assert.Empty(_requests); return; }
        var request = Assert.Single(_requests);
        Assert.Equal(mode == "frame-http2" ? "/v1/frame" : $"/v1/db/{DatabaseName}/sql", request.Path);
        Assert.Equal(mode == "frame-http2" ? "HTTP/2" : "HTTP/1.1", request.Protocol);
    }

    private async Task<SndbConnection> OpenAsync(string mode)
    {
        _deadline.Token.ThrowIfCancellationRequested();
        string source = mode == "embedded" ? Path.Combine(_root, "embedded")
            : $"sonnetdb+http://{new Uri(mode == "frame-http2" ? _frameUrl : _restUrl).Authority}/{DatabaseName}";
        var connection = new SndbConnection(mode == "embedded" ? $"Data Source={source};Timeout=5"
            : $"Data Source={source};Token={AdminToken};Protocol={mode};Timeout=5");
        try { await connection.OpenAsync(_deadline.Token); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private sealed record ObservedRequest(string Path, string Protocol);
}
