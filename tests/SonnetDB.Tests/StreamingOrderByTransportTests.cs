using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Model;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>通过 Kestrel 和内嵌 ADO.NET 验证完整排序消费、预览和传输中断。</summary>
public sealed class StreamingOrderByTransportTests : IAsyncLifetime
{
    private const string Token = "stream-order-admin";
    private const string Database = "stream_order";
    private const string SqlPath = "/v1/db/" + Database + "/sql";
    private const string Query = "SELECT time, host, value FROM order_samples ORDER BY time DESC";
    private const int SeedRows = 1_024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-order-transport-{Guid.NewGuid():N}");
    private WebApplication? _app;
    private Uri _rest = null!;
    private Uri _frame = null!;
    private Tsdb _database = null!;
    private ResponsePause? _nextResponsePause;

    /// <summary>启动独占 Kestrel 宿主，配置 HTTP/1.1 和 HTTP/2 实际监听地址。</summary>
    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            AllowAnonymousProbes = true,
            SqlExecution = new SqlExecutionResourceOptions { MaxResultRows = 4 },
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin },
        }, extraArgs:
            ["--Kestrel:Endpoints:OrderedH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:OrderedH2:Protocols=Http2"]);

        _app.Use(async (context, next) =>
        {
            ResponsePause? pause = _nextResponsePause;
            if (pause is null || context.Request.Path != pause.Path ||
                Interlocked.CompareExchange(ref _nextResponsePause, null, pause) != pause)
            {
                await next(context);
                return;
            }

            Stream body = context.Response.Body;
            using var registration = context.RequestAborted.Register(() => pause.Aborted.TrySetResult());
            using var pausedBody = new PausedResponseBody(body, pause, context.RequestAborted);
            context.Response.Body = pausedBody;
            try
            {
                await next(context);
            }
            finally
            {
                context.Response.Body = body;
                pause.Completed.TrySetResult();
            }
        });

        await _app.StartAsync(deadline.Token);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Assert.Equal(2, addresses.Addresses.Count);
        foreach (string address in addresses.Addresses)
        {
            var uri = new Uri(address);
            using var probe = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz")
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                using var response = await probe.SendAsync(request, deadline.Token);
                if (response.IsSuccessStatusCode)
                    _rest = uri;
                else
                    _frame = uri;
            }
            catch (HttpRequestException)
            {
                _frame = uri;
            }
        }
        Assert.NotNull(_rest);
        Assert.NotNull(_frame);

        using var client = CreateClient();
        using var created = await client.PostAsJsonAsync("/v1/db", new CreateDatabaseRequest(Database),
            ServerJsonContext.Default.CreateDatabaseRequest, deadline.Token);
        created.EnsureSuccessStatusCode();

        Assert.True(_app.Services.GetRequiredService<TsdbRegistry>().TryGet(Database, out var database));
        _database = Assert.IsType<Tsdb>(database);
        SqlExecutor.Execute(_database, "CREATE MEASUREMENT order_samples (host TAG, value FIELD INT)");
        Seed(_database, offset: 0, SeedRows, deadline.Token);
    }

    /// <summary>停止请求宿主并回收该用例独占的数据目录。</summary>
    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>REST 的正式查询返回全部行及成功终态，显式预览报告截断。</summary>
    [Theory]
    [InlineData(null, SeedRows)]
    [InlineData(3, 3)]
    public async Task RestNdjson_ReportsExactRowsAndTerminalRecord(int? preview, int expectedRows)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = CreateClient();
        using var request = CreateSqlRequest(Query, preview);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));

        using (var meta = JsonDocument.Parse(Assert.IsType<string>(await reader.ReadLineAsync(deadline.Token))))
        {
            Assert.Equal("meta", meta.RootElement.GetProperty("type").GetString());
            Assert.Equal(["time", "host", "value"],
                meta.RootElement.GetProperty("columns").EnumerateArray().Select(static name => name.GetString()));
        }

        var expected = ExpectedRows(_database);
        for (int i = 0; i < expectedRows; i++)
        {
            using var row = JsonDocument.Parse(Assert.IsType<string>(await reader.ReadLineAsync(deadline.Token)));
            Assert.Equal((long)expected[i][0]!, row.RootElement[0].GetInt64());
            Assert.Equal((string)expected[i][1]!, row.RootElement[1].GetString());
            Assert.Equal((long)expected[i][2]!, row.RootElement[2].GetInt64());
        }

        using var end = JsonDocument.Parse(Assert.IsType<string>(await reader.ReadLineAsync(deadline.Token)));
        Assert.Equal("end", end.RootElement.GetProperty("type").GetString());
        Assert.Equal(expectedRows, end.RootElement.GetProperty("rowCount").GetInt64());
        Assert.Equal(preview.HasValue, end.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Null(await reader.ReadLineAsync(deadline.Token));
    }

    /// <summary>ADO.NET 逐行返回完整结果和列名，并验证缓冲 SQL 基线的跨 series 稳定顺序。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("embedded")]
    public async Task SequentialAdoReader_ConsumesWholeOrderedResult(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string connectionString;
        if (protocol == "embedded")
        {
            string embeddedRoot = Path.Combine(_root, "embedded");
            using (Tsdb embedded = Tsdb.Open(new TsdbOptions { RootDirectory = embeddedRoot }))
            {
                SqlExecutor.Execute(embedded, "CREATE MEASUREMENT order_samples (host TAG, value FIELD INT)");
                Seed(embedded, offset: 0, SeedRows, deadline.Token);
            }
            connectionString = $"Data Source={embeddedRoot}";
        }
        else
        {
            connectionString = RemoteConnectionString(protocol);
        }

        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = Query;
        command.CommandTimeout = 10;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, deadline.Token);
        Assert.Equal(["time", "host", "value"], Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        var expected = ExpectedRows(_database);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal((long)expected[i][0]!, reader.GetInt64(0));
            Assert.Equal((string)expected[i][1]!, reader.GetString(1));
            Assert.Equal((long)expected[i][2]!, reader.GetInt64(2));
        }
        Assert.Equal(SeedRows, expected.Count);
        Assert.False(await reader.ReadAsync(deadline.Token));
        Assert.False(Assert.IsType<SndbDataReader>(reader).Truncated);
    }

    /// <summary>Workbench 按累计物化预算拒绝无界预览，同时允许普通查询读取全部行。</summary>
    [Fact]
    public async Task WorkbenchPreview_RejectsExcessButOrdinaryReaderConsumesAllRows()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        const int total = 10_002;
        Seed(_database, SeedRows, total - SeedRows, deadline.Token);
        using var client = CreateClient();
        using var preview = CreateSqlRequest(Query);
        preview.Headers.Add("X-SonnetDB-Workbench-Profile", "preview-1");
        preview.Headers.Add("X-SonnetDB-Workbench-Action", "sql.read");
        using var rejected = await client.SendAsync(preview, deadline.Token);
        using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal("sql_error", error.RootElement.GetProperty("error").GetString());
        Assert.Contains("累计物化", error.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);

        await using var connection = new SndbConnection(RemoteConnectionString("rest"));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = Query;
        command.CommandTimeout = 10;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, deadline.Token);
        for (int i = 0; i < total; i++)
            Assert.True(await reader.ReadAsync(deadline.Token));
        Assert.False(await reader.ReadAsync(deadline.Token));
        Assert.False(Assert.IsType<SndbDataReader>(reader).Truncated);
    }

    /// <summary>读取前缀后客户端主动断开，服务器退出发送并可处理下一次查询。</summary>
    [Fact]
    public async Task RestDisconnect_StopsPausedServerAndAllowsNextQuery()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pause = PauseNextResponse(SqlPath);
        using var client = CreateClient();
        using var request = CreateSqlRequest(Query);
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            Assert.Contains("\"type\":\"meta\"", await reader.ReadLineAsync(deadline.Token));
            Assert.StartsWith("[", await reader.ReadLineAsync(deadline.Token));
            await pause.Reached.Task.WaitAsync(deadline.Token);
        }

        await pause.Aborted.Task.WaitAsync(deadline.Token);
        await pause.Completed.Task.WaitAsync(deadline.Token);
        await AssertReusableAsync(client, deadline.Token);
    }

    /// <summary>服务器截止时间覆盖发送回压，已开始的响应不能产生成功终态。</summary>
    [Fact]
    public async Task RestServerDeadline_AbortsPausedOutputWithoutSuccessfulEnd()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = _app!.Services.GetRequiredService<IOptions<ServerOptions>>().Value.SqlExecution;
        options.StreamingQueryTimeoutMilliseconds = 3_000;
        var pause = PauseNextResponse(SqlPath);
        using var client = CreateClient();
        using var request = CreateSqlRequest(Query);
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            response.EnsureSuccessStatusCode();
            Assert.Equal("1", Assert.Single(response.Headers.GetValues(SqlFrameCodec.StreamingResultHeader)));
            Assert.Contains("\"type\":\"meta\"", await reader.ReadLineAsync(deadline.Token));
            Assert.StartsWith("[", await reader.ReadLineAsync(deadline.Token));
            await pause.Reached.Task.WaitAsync(deadline.Token);
            await pause.WriteCancelled.Task.WaitAsync(deadline.Token);
            bool ended = false;
            try
            {
                for (int i = 1; i <= SeedRows; i++)
                {
                    string? line = await reader.ReadLineAsync(deadline.Token);
                    if (line is null)
                    {
                        ended = true;
                        break;
                    }
                    Assert.StartsWith("[", line);
                }
            }
            catch (IOException)
            {
                // Kestrel 可以用连接重置结束尚未完整提交的 HTTP/1.1 响应。
                ended = true;
            }
            Assert.True(ended, "The partial response must end without a success record.");
        }

        await pause.Completed.Task.WaitAsync(deadline.Token);
        options.StreamingQueryTimeoutMilliseconds = 0;
        await AssertReusableAsync(client, deadline.Token);
    }

    /// <summary>实际网络读者的取消或读取超时结束后，主动释放读者会打断服务器发送。</summary>
    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    public async Task AdoInterruption_ReleasesPausedResponseAndAllowsNextQuery(string protocol, bool timeout)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        bool isRest = protocol == "rest";
        if (!isRest)
            Seed(_database, SeedRows, 2 * SqlFrameCodec.DefaultMaxChunkRows - SeedRows, deadline.Token);
        await using var connection = new SndbConnection(RemoteConnectionString(protocol));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = Query;
        command.CommandTimeout = timeout ? 1 : 10;
        var pause = PauseNextResponse(isRest ? SqlPath : "/v1/frame");
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, deadline.Token))
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            await pause.Reached.Task.WaitAsync(deadline.Token);
            using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            if (timeout)
            {
                await Assert.ThrowsAsync<TimeoutException>(async () =>
                {
                    for (int i = 0; i < SeedRows + 1 && await reader.ReadAsync(interrupt.Token); i++)
                    {
                        interrupt.Token.ThrowIfCancellationRequested();
                    }
                });
            }
            else
            {
                interrupt.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync(interrupt.Token));
            }
        }

        await pause.Aborted.Task.WaitAsync(deadline.Token);
        await pause.Completed.Task.WaitAsync(deadline.Token);
        using var client = CreateClient();
        await AssertReusableAsync(client, deadline.Token);
    }

    private ResponsePause PauseNextResponse(string path, int flushesBeforePause = 2)
    {
        var pause = new ResponsePause(path, flushesBeforePause);
        Assert.Null(Interlocked.CompareExchange(ref _nextResponsePause, pause, null));
        return pause;
    }

    private async Task AssertReusableAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = CreateSqlRequest(Query + " LIMIT 1");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Contains("\"rowCount\":1", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 })
        {
            BaseAddress = _rest,
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private string RemoteConnectionString(string protocol)
    {
        Uri endpoint = protocol == "rest" ? _rest : _frame;
        return $"Data Source=sonnetdb+http://{endpoint.Authority}/{Database};Token={Token};Protocol={protocol}";
    }

    private static HttpRequestMessage CreateSqlRequest(string sql, int? preview = null) =>
        new(HttpMethod.Post, SqlPath)
        {
            Content = JsonContent.Create(new SqlRequest(sql) { PreviewMaxRows = preview }, ServerJsonContext.Default.SqlRequest),
        };

    private static IReadOnlyList<IReadOnlyList<object?>> ExpectedRows(Tsdb database) =>
        Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, Query)).Rows;

    private static void Seed(Tsdb database, int offset, int count, CancellationToken cancellationToken)
    {
        const int batchSize = 1_024;
        var hosts = new Dictionary<string, string>[]
        {
            new() { ["host"] = "alpha" },
            new() { ["host"] = "beta" },
        };
        var points = new Point[Math.Min(count, batchSize)];
        for (int written = 0; written < count; written += points.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(points.Length, count - written);
            for (int index = 0; index < length; index++)
            {
                int row = offset + written + index;
                points[index] = Point.Create("order_samples", row / 2, hosts[row % 2], new Dictionary<string, FieldValue>
                {
                    ["value"] = FieldValue.FromLong(10L * row + 1),
                });
            }
            Assert.Equal(length, database.WriteMany(points.AsSpan(0, length), cancellationToken));
        }
    }

    private sealed class ResponsePause(PathString path, int flushesBeforePause)
    {
        internal PathString Path { get; } = path;
        internal int FlushesBeforePause { get; } = flushesBeforePause;
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WriteCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PausedResponseBody(Stream inner, ResponsePause pause, CancellationToken requestAborted) : Stream
    {
        private int _flushes;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await inner.FlushAsync(cancellationToken);
            _flushes++;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // 先送出协议元数据和至少一行，再等待真实连接断开。
            if (_flushes >= pause.FlushesBeforePause)
            {
                pause.Reached.TrySetResult();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, cancellationToken);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), deadline.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !requestAborted.IsCancellationRequested)
                {
                    pause.WriteCancelled.TrySetResult();
                    throw;
                }
                throw new TimeoutException("The paused Kestrel response did not observe request cancellation.");
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
