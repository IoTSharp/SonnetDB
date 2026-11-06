using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Client;
using SonnetDB.Configuration;
using SonnetDB.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.Tests;

/// <summary>轻量 Client 的原子读取删除经真实 h2c 产品 Server 验证，不替换产品端点。</summary>
public sealed class KvGetAndDeleteFrameClientTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string Database = "typed_getdelete";
    private const string Keyspace = "Cache";
    private const string Token = "typed-getdelete-admin";
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sndb-kv-getdelete-" + Guid.NewGuid().ToString("N")));
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();
    private readonly TaskCompletionSource _getAndDeletePairArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;
    private Uri _address = null!;
    private bool _ownsRoot;
    private int _synchronizeGetAndDeletePair;
    private int _getAndDeletePairArrivals;

    /// <summary>创建独占数据目录、真实HTTP/2宿主及业务数据库。</summary>
    public async Task InitializeAsync()
    {
        Assert.False(Directory.Exists(_root));
        Directory.CreateDirectory(_root);
        _ownsRoot = true;
        await StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new HttpClient
        {
            BaseAddress = _address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var content = new StringContent("{\"name\":\"" + Database + "\"}", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("v1/db", content, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        _requests.Clear();
    }

    /// <summary>关闭本夹具宿主并仅回收已验证归属的独占目录。</summary>
    public async Task DisposeAsync()
    {
        try { await StopAsync(); }
        finally
        {
            if (_ownsRoot && Directory.Exists(_root))
            {
                string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Assert.Equal(expectedParent, Path.GetDirectoryName(_root));
                Assert.StartsWith("sndb-kv-getdelete-", Path.GetFileName(_root), StringComparison.Ordinal);
                Directory.Delete(_root, recursive: true);
                _ownsRoot = false;
            }
        }
    }

    /// <summary>返回旧二进制值、旧版本与完整UTC ticks，删除及缺失状态跨两次真实重开保持。</summary>
    [Fact]
    public async Task GetAndDeleteKv_BinaryExpiryAndVersions_DeletesAcrossTwoServerReopens()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        byte[] key = [0, 128, 255];
        byte[] value = [0, 128, 255, 0, 13];
        var expiry = new DateTimeOffset(2031, 11, 7, 1, 2, 3, TimeSpan.FromHours(5.5)).AddTicks(1234567);
        using (var client = CreateClient())
        {
            long original = await client.PutKvAsync(Database, Keyspace, key, value, expiry, cancellationToken: token);
            var result = await client.GetAndDeleteKvAsync(Database, Keyspace, key, streamId: 37, cancellationToken: token);
            Assert.NotNull(result.PreviousValue);
            Assert.Equal(value, result.PreviousValue.Value);
            Assert.Equal(original, result.PreviousValue.Version);
            Assert.NotNull(result.PreviousValue.ExpiresAtUtc);
            Assert.Equal(expiry.UtcTicks, result.PreviousValue.ExpiresAtUtc.Value.UtcTicks);
            Assert.Equal(TimeSpan.Zero, result.PreviousValue.ExpiresAtUtc.Value.Offset);
            Assert.Equal(original + 1, result.MutationVersion);
            Assert.Null(await client.GetKvAsync(Database, Keyspace, key, cancellationToken: token));
            Assert.Empty(await client.ScanKvPageAsync(Database, Keyspace, key, limit: 8, cancellationToken: token));
            var missing = await client.GetAndDeleteKvAsync(Database, Keyspace, key, cancellationToken: token);
            Assert.Null(missing.PreviousValue);
            Assert.Null(missing.MutationVersion);
        }
        Assert.Contains(_requests, request => request.Operation == (byte)KvFrameOp.GetAndDelete && request.StreamId == 37);
        await AssertAbsentAfterServerReopenAsync(key, token);
        await AssertAbsentAfterServerReopenAsync(key, token);
        AssertNativeFrameRequests();
    }

    /// <summary>已存在的空值与缺失记录不同，缺失删除不分配变更版本。</summary>
    [Fact]
    public async Task GetAndDeleteKv_EmptyValueAndMissing_HaveDistinctResultsAndVersions()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var client = CreateClient();
        byte[] key = [0];
        long version = await client.PutKvAsync(Database, Keyspace, key, ReadOnlyMemory<byte>.Empty, cancellationToken: token);
        var empty = await client.GetAndDeleteKvAsync(Database, Keyspace, key, cancellationToken: token);
        Assert.NotNull(empty.PreviousValue);
        Assert.Empty(empty.PreviousValue.Value);
        Assert.Equal(version, empty.PreviousValue.Version);
        Assert.Null(empty.PreviousValue.ExpiresAtUtc);
        Assert.Equal(version + 1, empty.MutationVersion);
        var missing = await client.GetAndDeleteKvAsync(Database, Keyspace, key, cancellationToken: token);
        Assert.Null(missing.PreviousValue);
        Assert.Null(missing.MutationVersion);
        long after = await client.PutKvAsync(Database, Keyspace, new byte[] { 1 }, new byte[] { 9 }, cancellationToken: token);
        Assert.Equal(empty.MutationVersion!.Value + 1, after);
        Assert.Null(await client.GetKvAsync(Database, Keyspace, key, cancellationToken: token));
        Assert.Empty(await client.ScanKvPageAsync(Database, Keyspace, key, limit: 8, cancellationToken: token));
        AssertNativeFrameRequests();
    }

    /// <summary>两个独立HTTP客户端并发消费同键，恰好一方取得完整旧记录且只提交一次删除。</summary>
    [Fact]
    public async Task GetAndDeleteKv_TwoIndependentClients_ConsumeThePreviousRecordExactlyOnce()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var admin = CreateClient();
        byte[] key = [1, 0];
        byte[] bytes = [0, 254, 255];
        long version = await admin.PutKvAsync(Database, Keyspace, key, bytes, cancellationToken: token);
        _requests.Clear();
        using var first = CreateClient();
        using var second = CreateClient();
        Interlocked.Exchange(ref _synchronizeGetAndDeletePair, 1);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<KvDataFrameExchangeResult> ConsumeAsync(SndbFrameClient contender, uint streamId)
        {
            await start.Task.WaitAsync(token);
            return await contender.GetAndDeleteKvAsync(Database, Keyspace, key, streamId, token);
        }
        Task<KvDataFrameExchangeResult> one = ConsumeAsync(first, 10);
        Task<KvDataFrameExchangeResult> two = ConsumeAsync(second, 11);
        start.SetResult();
        var results = await Task.WhenAll(one, two).WaitAsync(token);
        Interlocked.Exchange(ref _synchronizeGetAndDeletePair, 0);
        Assert.True(_getAndDeletePairArrived.Task.IsCompletedSuccessfully);
        Assert.Equal(2, Volatile.Read(ref _getAndDeletePairArrivals));
        Assert.Equal(2, _requests.Count);
        Assert.All(_requests, request => Assert.Equal((byte)KvFrameOp.GetAndDelete, request.Operation));
        var winner = Assert.Single(results, result => result.PreviousValue is not null);
        var loser = Assert.Single(results, result => result.PreviousValue is null);
        output.WriteLine("Both real op6 requests arrived before product dispatch; requests=2, previous-record winners=1, missing-record results=1.");
        Assert.Equal(bytes, winner.PreviousValue!.Value);
        Assert.Equal(version, winner.PreviousValue.Version);
        Assert.Equal(version + 1, winner.MutationVersion);
        Assert.Null(loser.MutationVersion);
        Assert.Null(await admin.GetKvAsync(Database, Keyspace, key, cancellationToken: token));
        Assert.Empty(await admin.ScanKvPageAsync(Database, Keyspace, key, limit: 8, cancellationToken: token));
        Assert.Equal(version + 2, await admin.PutKvAsync(Database, Keyspace, new byte[] { 2 }, new byte[] { 7 }, cancellationToken: token));
        AssertNativeFrameRequests();
    }

    /// <summary>删除已提交但大旧值超过客户端响应预算时结果未知，只发一次请求，独立核对后重开仍缺失。</summary>
    [Fact]
    public async Task GetAndDeleteKv_ResponseBudget_ReportsUnknownWithoutReplayingCommittedDeletion()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        byte[] key = [3];
        using (var admin = CreateClient())
        {
            var value = new byte[256];
            value[255] = 255;
            long version = await admin.PutKvAsync(Database, Keyspace, key, value, cancellationToken: token);
            _requests.Clear();
            using var small = new SndbFrameClient(_address, Token, maxPayloadBytes: 32, timeout: TimeSpan.FromSeconds(10));
            var failure = await Assert.ThrowsAsync<SndbFrameException>(() => small.GetAndDeleteKvAsync(Database, Keyspace, key, cancellationToken: token));
            Assert.Equal("frame_budget", failure.Code);
            Assert.True(failure.OutcomeUnknown);
            var sent = Assert.Single(_requests);
            Assert.Equal((byte)KvFrameOp.GetAndDelete, sent.Operation);
            using var reconciliation = CreateClient();
            Assert.Null(await reconciliation.GetKvAsync(Database, Keyspace, key, cancellationToken: token));
            Assert.Empty(await reconciliation.ScanKvPageAsync(Database, Keyspace, key, limit: 8, cancellationToken: token));
            Assert.Equal(version + 2, await reconciliation.PutKvAsync(Database, Keyspace, new byte[] { 4 }, new byte[] { 1 }, cancellationToken: token));
        }
        await AssertAbsentAfterServerReopenAsync(key, token);
        AssertNativeFrameRequests();
    }

    /// <summary>拒绝未认证或只读身份的原子删除，既有值与版本均保持。</summary>
    [Theory]
    [InlineData("readonly", "forbidden")]
    [InlineData(null, "http_error")]
    public async Task GetAndDeleteKv_Unauthorized_DoesNotConsumeTheExistingRecord(string? tokenValue, string expectedCode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var admin = CreateClient();
        byte[] key = [5];
        long version = await admin.PutKvAsync(Database, Keyspace, key, new byte[] { 42 }, cancellationToken: token);
        _requests.Clear();
        using var denied = CreateClient(tokenValue);
        var failure = await Assert.ThrowsAsync<SndbFrameException>(() => denied.GetAndDeleteKvAsync(Database, Keyspace, key, cancellationToken: token));
        Assert.Equal(expectedCode, failure.Code);
        if (tokenValue is null)
        {
            Assert.Contains("HTTP 401", failure.Message, StringComparison.Ordinal);
            Assert.Empty(_requests);
            output.WriteLine("Anonymous GetAndDelete received HTTP 401 before the post-authentication observer; observed product Frame requests=0.");
        }
        else
        {
            Assert.Equal("readonly", tokenValue);
            Assert.Equal((byte)KvFrameOp.GetAndDelete, Assert.Single(_requests).Operation);
            output.WriteLine("Authenticated readonly GetAndDelete reached the product Frame endpoint exactly once and returned forbidden.");
        }
        var actual = await admin.GetKvAsync(Database, Keyspace, key, cancellationToken: token);
        Assert.NotNull(actual);
        Assert.Equal(new byte[] { 42 }, actual.Value);
        Assert.Equal(version, actual.Version);
        AssertNativeFrameRequests();
    }

    /// <summary>预取消在编码/发送前终止，服务器不收到删除请求，记录保持可读。</summary>
    [Fact]
    public async Task GetAndDeleteKv_PreCanceled_DoesNotSendOrDelete()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var client = CreateClient();
        byte[] key = [6];
        long version = await client.PutKvAsync(Database, Keyspace, key, new byte[] { 9 }, cancellationToken: deadline.Token);
        _requests.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAndDeleteKvAsync(Database, Keyspace, key, cancellationToken: canceled.Token));
        Assert.Empty(_requests);
        var actual = await client.GetKvAsync(Database, Keyspace, key, cancellationToken: deadline.Token);
        Assert.NotNull(actual);
        Assert.Equal(new byte[] { 9 }, actual.Value);
        Assert.Equal(version, actual.Version);
        AssertNativeFrameRequests();
    }

    private SndbFrameClient CreateClient(string? token = Token)
        => new(_address, token, timeout: TimeSpan.FromSeconds(10));

    private async Task AssertAbsentAfterServerReopenAsync(byte[] key, CancellationToken token)
    {
        await StopAsync();
        await StartAsync();
        using var reopened = CreateClient();
        Assert.Null(await reopened.GetKvAsync(Database, Keyspace, key, cancellationToken: token));
        Assert.Empty(await reopened.ScanKvPageAsync(Database, Keyspace, key, limit: 8, cancellationToken: token));
        var missing = await reopened.GetAndDeleteKvAsync(Database, Keyspace, key, cancellationToken: token);
        Assert.Null(missing.PreviousValue);
        Assert.Null(missing.MutationVersion);
    }

    private void AssertNativeFrameRequests()
    {
        Assert.NotEmpty(_requests);
        Assert.All(_requests, request =>
        {
            Assert.Equal("/v1/frame", request.Path);
            Assert.Equal("HTTP/2", request.Protocol);
            Assert.Equal("application/x-sonnetdb-frame", request.ContentType);
            Assert.Equal((byte)FrameService.Kv, request.Service);
        });
    }

    private async Task StartAsync()
    {
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin, ["readonly"] = ServerRoles.ReadOnly }
        }, extraArgs: ["--Kestrel:Endpoints:Http:Protocols=Http2"]);
        _app.Use(async (context, next) =>
        {
            byte service = 0;
            byte operation = 0;
            uint streamId = 0;
            if (context.Request.Path == "/v1/frame")
            {
                context.Request.EnableBuffering(bufferThreshold: 4096, bufferLimit: 4096);
                var headerBytes = new byte[FrameHeader.Size];
                await context.Request.Body.ReadExactlyAsync(headerBytes, context.RequestAborted);
                Assert.True(FrameHeader.TryRead(headerBytes, out var header));
                context.Request.Body.Position = 0;
                service = header.Service;
                operation = header.Op;
                streamId = header.StreamId;
            }
            if (_requests.Count >= 64)
                throw new InvalidOperationException("GetAndDelete fixture request budget exceeded.");
            _requests.Enqueue(new CapturedRequest(context.Request.Path.Value!, context.Request.Protocol,
                context.Request.ContentType, service, operation, streamId));
            output.WriteLine($"{context.Request.Protocol} {context.Request.Path} content={context.Request.ContentType} service={service} op={operation} stream={streamId}");
            if (service == (byte)FrameService.Kv && operation == (byte)KvFrameOp.GetAndDelete
                && Volatile.Read(ref _synchronizeGetAndDeletePair) == 1)
            {
                int arrived = Interlocked.Increment(ref _getAndDeletePairArrivals);
                if (arrived > 2)
                    throw new InvalidOperationException("The concurrent GetAndDelete fixture accepts exactly two requests.");
                output.WriteLine($"Concurrent op6 arrived: count={arrived}, stream={streamId}.");
                if (arrived == 2)
                    _getAndDeletePairArrived.TrySetResult();
                await _getAndDeletePairArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), context.RequestAborted);
            }
            await next(context);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _app.StartAsync(deadline.Token);
            _address = new Uri(Assert.Single(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
            output.WriteLine($"Product Server started: address={_address} dataRoot={_root}");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    private async Task StopAsync()
    {
        if (_app is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _app.StopAsync(deadline.Token); }
        finally
        {
            await _app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            _app = null;
        }
    }

    private sealed record CapturedRequest(string Path, string Protocol, string? ContentType, byte Service, byte Operation, uint StreamId);
}
