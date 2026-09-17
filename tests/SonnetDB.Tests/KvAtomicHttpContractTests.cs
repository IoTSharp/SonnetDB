using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using SonnetDB.Kv;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>通过真实宿主校验原始 REST 与 Frame 的 KV 参数、权限和错误合同。</summary>
public sealed class KvAtomicHttpContractTests(KvAtomicHttpContractFixture fixture)
    : IClassFixture<KvAtomicHttpContractFixture>
{
    private const string DatabaseName = KvAtomicHttpContractFixture.DatabaseName;
    private const string FrameContentType = "application/x-sonnetdb-frame";

    /// <summary>无效参数稳定返回 bad_request，保留请求关联头且不写入或消耗版本。</summary>
    [Theory]
    [InlineData("set-conditional", "{")]
    [InlineData("set-conditional", "null")]
    [InlineData("set-conditional", "{\"key\":\"record\",\"value\":null,\"condition\":0}")]
    [InlineData("set-conditional", "{\"key\":\"record\",\"value\":\"Ag==\",\"condition\":99}")]
    [InlineData("set-conditional", "{\"key\":\"record\",\"value\":\"Ag==\",\"condition\":0,\"expiresAtUtc\":\"2099-01-01T00:00:00+08:00\"}")]
    [InlineData("set-conditional", "{\"key\":\"\",\"value\":\"Ag==\",\"condition\":0}")]
    [InlineData("cas", "{\"key\":\"record\",\"value\":\"Ag==\",\"expectedVersion\":-1}")]
    [InlineData("set", "{\"key\":\"record\",\"value\":null}")]
    [InlineData("expire", "{\"key\":\"record\",\"expiresAtUtc\":\"2099-01-01T00:00:00+08:00\"}")]
    [InlineData("persist", "{\"key\":\"\"}")]
    [InlineData("set-conditional", "{\"key\":\"record\",\"value\":\"Ag==\"}")]
    public async Task AtomicRest_InvalidBody_ReturnsBadRequestWithoutMutation(string action, string json)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = fixture.CreateClient();
        string keyspace = NewKeyspace();
        long version = await SeedAsync(client, keyspace, deadline.Token);
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(Route(keyspace, action), body, deadline.Token);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "bad_request", deadline.Token, contractHeaders: true);
        await AssertBaselineAsync(client, keyspace, version, deadline.Token);

        using var replacementBody = JsonContent.Create(new KvSetRequest("record", [3], null), ServerJsonContext.Default.KvSetRequest);
        using var replacement = await client.PostAsync(Route(keyspace, "get-and-set"), replacementBody, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        var result = await replacement.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvExchangeResponse, deadline.Token);
        Assert.NotNull(result);
        Assert.Equal(version + 1, result.MutationVersion);
    }

    /// <summary>REST 和 Frame 对缺失数据库保留同一稳定错误码。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicWrite_MissingDatabase_ReturnsDbNotFound(bool frame)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = fixture.CreateClient();
        using var response = await PostWriteAsync(client, frame, "missing_database", NewKeyspace(), deadline.Token);
        if (frame)
            await AssertFrameErrorAsync(response, "db_not_found", deadline.Token);
        else
            await AssertErrorAsync(response, HttpStatusCode.NotFound, "db_not_found", deadline.Token, contractHeaders: true);
    }

    /// <summary>匿名原子写入被 HTTP 鉴权拒绝，已有记录保持原版本。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicWrite_Anonymous_ReturnsUnauthorizedWithoutMutation(bool frame)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var admin = fixture.CreateClient();
        using var anonymous = fixture.CreateClient(token: null);
        string keyspace = NewKeyspace();
        long version = await SeedAsync(admin, keyspace, deadline.Token);
        using var response = await PostWriteAsync(anonymous, frame, DatabaseName, keyspace, deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertBaselineAsync(admin, keyspace, version, deadline.Token);
    }

    /// <summary>只读令牌可读取已有记录，但两个传输的原子写均返回 forbidden。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicWrite_ReadOnly_ReturnsForbiddenWithoutMutation(bool frame)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var admin = fixture.CreateClient();
        using var readOnly = fixture.CreateClient(KvAtomicHttpContractFixture.ReadOnlyToken);
        string keyspace = NewKeyspace();
        long version = await SeedAsync(admin, keyspace, deadline.Token);
        await AssertBaselineAsync(readOnly, keyspace, version, deadline.Token);
        using var response = await PostWriteAsync(readOnly, frame, DatabaseName, keyspace, deadline.Token);
        if (frame)
            await AssertFrameErrorAsync(response, "forbidden", deadline.Token);
        else
            await AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden", deadline.Token, contractHeaders: true);
        await AssertBaselineAsync(admin, keyspace, version, deadline.Token);
    }

    /// <summary>不支持的帧版本或操作码稳定返回对应错误，原子写不执行。</summary>
    [Theory]
    [InlineData(false, "unsupported_op")]
    [InlineData(true, "unsupported_version")]
    public async Task AtomicFrame_UnsupportedEnvelope_ReturnsStableErrorWithoutMutation(bool versionError, string code)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = fixture.CreateClient();
        string keyspace = NewKeyspace();
        long version = await SeedAsync(client, keyspace, deadline.Token);
        byte[] request = EncodeWrite(DatabaseName, keyspace);
        Assert.True(FrameHeader.TryRead(request, out var header));
        new FrameHeader(header.PayloadLength, versionError ? (byte)2 : header.Version,
            header.Service, versionError ? header.Op : byte.MaxValue, header.Flags, header.StreamId).Write(request);
        using var body = FrameBody(request);
        using var response = await client.PostAsync("/v1/frame", body, deadline.Token);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, code, deadline.Token, contractHeaders: false);
        await AssertBaselineAsync(client, keyspace, version, deadline.Token);
    }

    /// <summary>空字节数组在跨协议交换时仍然存在，重复删除才返回缺失。</summary>
    [Fact]
    public async Task AtomicWrite_EmptyValueAcrossRestAndFrame_PreservesFoundAndPreviousVersion()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = fixture.CreateClient();
        string keyspace = NewKeyspace();
        using var body = JsonContent.Create(new KvConditionalSetRequest("record", [], KvSetCondition.IfNotExists, null),
            ServerJsonContext.Default.KvConditionalSetRequest);
        using var created = await client.PostAsync(Route(keyspace, "set-conditional"), body, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        AssertContractHeaders(created);
        var set = await created.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvConditionalSetResponse, deadline.Token);
        Assert.NotNull(set);
        Assert.True(set.Applied);
        Assert.Equal(set.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture), set.VersionText);

        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeAtomicWriteRequest(writer, 17, KvFrameOp.GetAndSet, DatabaseName, keyspace, "record"u8, []);
        using var frameBody = FrameBody(writer.WrittenMemory.ToArray());
        using var exchanged = await client.PostAsync("/v1/frame", frameBody, deadline.Token);
        var frame = await ReadSingleFrameAsync(exchanged, deadline.Token);
        Assert.False(frame.Header.IsError);
        var exchange = KvFrameCodec.DecodeExchangeResponse(frame.Payload);
        Assert.NotNull(exchange.PreviousEntry);
        Assert.Empty(exchange.PreviousEntry.Value.ToArray());
        Assert.Equal(set.Version, exchange.PreviousEntry.Version);
        Assert.True(exchange.MutationVersion > set.Version);

        using var deleteBody = JsonContent.Create(new KvDeleteRequest("record"), ServerJsonContext.Default.KvDeleteRequest);
        using var deleted = await client.PostAsync(Route(keyspace, "get-and-delete"), deleteBody, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        AssertContractHeaders(deleted);
        var result = await deleted.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvExchangeResponse, deadline.Token);
        Assert.NotNull(result);
        Assert.True(result.Previous.Found);
        Assert.NotNull(result.Previous.Value);
        Assert.Empty(result.Previous.Value);
        Assert.Equal(exchange.MutationVersion, result.Previous.Version);
        Assert.Equal(result.Previous.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture), result.PreviousVersionText);
        Assert.Equal(result.MutationVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture), result.MutationVersionText);

        using var againBody = JsonContent.Create(new KvDeleteRequest("record"), ServerJsonContext.Default.KvDeleteRequest);
        using var again = await client.PostAsync(Route(keyspace, "get-and-delete"), againBody, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var missing = await again.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvExchangeResponse, deadline.Token);
        Assert.NotNull(missing);
        Assert.False(missing.Previous.Found);
        Assert.Null(missing.Previous.Value);
        Assert.Null(missing.MutationVersion);
    }

    private static string NewKeyspace() => "contract_" + Guid.NewGuid().ToString("N");

    private static string Route(string keyspace, string action) => $"/v1/db/{DatabaseName}/kv/{keyspace}/{action}";

    private static async Task<long> SeedAsync(HttpClient client, string keyspace, CancellationToken cancellationToken)
    {
        using var body = JsonContent.Create(new KvSetRequest("record", [1], null), ServerJsonContext.Default.KvSetRequest);
        using var response = await client.PostAsync(Route(keyspace, "set"), body, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvSetResponse, cancellationToken);
        Assert.NotNull(value);
        return value.Version;
    }

    private static async Task AssertBaselineAsync(HttpClient client, string keyspace, long version, CancellationToken cancellationToken)
    {
        using var body = JsonContent.Create(new KvGetRequest("record"), ServerJsonContext.Default.KvGetRequest);
        using var response = await client.PostAsync(Route(keyspace, "get"), body, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvValueResponse, cancellationToken);
        Assert.NotNull(value);
        Assert.True(value.Found);
        Assert.Equal(new byte[] { 1 }, value.Value);
        Assert.Equal(version, value.Version);
    }

    private static async Task<HttpResponseMessage> PostWriteAsync(
        HttpClient client, bool frame, string database, string keyspace, CancellationToken cancellationToken)
    {
        using HttpContent body = frame
            ? FrameBody(EncodeWrite(database, keyspace))
            : JsonContent.Create(new KvConditionalSetRequest("record", [2], KvSetCondition.Always, null),
                ServerJsonContext.Default.KvConditionalSetRequest);
        return await client.PostAsync(frame ? "/v1/frame" : $"/v1/db/{database}/kv/{keyspace}/set-conditional", body, cancellationToken);
    }

    private static byte[] EncodeWrite(string database, string keyspace)
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeAtomicWriteRequest(writer, 17, KvFrameOp.SetConditional, database, keyspace, "record"u8, [2]);
        return writer.WrittenMemory.ToArray();
    }

    private static ByteArrayContent FrameBody(byte[] bytes)
    {
        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
        return body;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code,
        CancellationToken cancellationToken, bool contractHeaders)
    {
        Assert.Equal(status, response.StatusCode);
        if (contractHeaders)
            AssertContractHeaders(response);
        var error = await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.ErrorResponse, cancellationToken);
        Assert.NotNull(error);
        Assert.Equal(code, error.Error);
        Assert.NotEmpty(error.Message);
    }

    private static void AssertContractHeaders(HttpResponseMessage response)
    {
        Assert.Equal("1", Assert.Single(response.Headers.GetValues("X-SonnetDB-Contract-Version")));
        Assert.NotEmpty(Assert.Single(response.Headers.GetValues("X-Request-ID")));
    }

    private static async Task AssertFrameErrorAsync(HttpResponseMessage response, string expectedCode, CancellationToken cancellationToken)
    {
        var frame = await ReadSingleFrameAsync(response, cancellationToken);
        Assert.True(frame.Header.IsError);
        Assert.True(frame.Header.IsResponse);
        Assert.Equal(17u, frame.Header.StreamId);
        (string code, string message) = FrameCodec.ReadErrorPayload(frame.Payload);
        Assert.Equal(expectedCode, code);
        Assert.NotEmpty(message);
    }

    private static async Task<(FrameHeader Header, byte[] Payload)> ReadSingleFrameAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FrameContentType, response.Content.Headers.ContentType?.MediaType);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var remaining = new ReadOnlySequence<byte>(bytes);
        Assert.True(FrameCodec.TryReadFrame(ref remaining, out var header, out var payload));
        Assert.Equal(0, remaining.Length);
        return (header, payload.ToArray());
    }
}

/// <summary>为原始 KV HTTP 合同测试维护独立的真实宿主与数据目录。</summary>
public sealed class KvAtomicHttpContractFixture : IAsyncLifetime
{
    /// <summary>本 fixture 的数据库名。</summary>
    public const string DatabaseName = "kv_atomic_contract";
    /// <summary>本 fixture 的只读令牌。</summary>
    public const string ReadOnlyToken = "kv-atomic-contract-readonly";
    private const string AdminToken = "kv-atomic-contract-admin";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-kv-http-contract-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private Uri? _baseAddress;

    /// <summary>以三十秒期限启动宿主并创建测试数据库。</summary>
    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(_root);
        try
        {
            _app = TestServerHost.Build(new ServerOptions
            {
                DataRoot = _root,
                AutoLoadExistingDatabases = true,
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string>
                {
                    [AdminToken] = ServerRoles.Admin,
                    [ReadOnlyToken] = ServerRoles.ReadOnly,
                },
            });
            await _app.StartAsync(deadline.Token);
            string address = Assert.Single(_app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses);
            _baseAddress = new Uri(address);
            using var client = CreateClient();
            using var body = JsonContent.Create(new CreateDatabaseRequest(DatabaseName), ServerJsonContext.Default.CreateDatabaseRequest);
            using var response = await client.PostAsync("/v1/db", body, deadline.Token);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>创建具有指定凭据的 HTTP 客户端；空令牌表示匿名。</summary>
    public HttpClient CreateClient(string? token = AdminToken)
    {
        var client = new HttpClient { BaseAddress = _baseAddress, Timeout = TimeSpan.FromSeconds(10) };
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>有界关闭宿主并核对路径归属后删除独占目录。</summary>
    public async Task DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        WebApplication? app = _app;
        _app = null;
        if (app is not null)
        {
            try
            {
                await app.StopAsync(deadline.Token);
            }
            finally
            {
                await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }

        string ownedRoot = Path.GetFullPath(_root);
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!ownedRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(ownedRoot).StartsWith("sonnetdb-kv-http-contract-", StringComparison.Ordinal))
            throw new InvalidOperationException("KV HTTP contract fixture cleanup path is not owned by this test.");
        if (Directory.Exists(ownedRoot))
            Directory.Delete(ownedRoot, recursive: true);
    }
}
