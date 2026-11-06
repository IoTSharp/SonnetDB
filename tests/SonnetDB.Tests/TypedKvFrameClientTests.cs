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
using Xunit;

namespace SonnetDB.Tests;

/// <summary>独立 typed KV Client → 真实 HTTP/2 Frame → Server → 持久化 keyspace。</summary>
public sealed class TypedKvFrameClientTests : IAsyncLifetime
{
    private const string Database = "typed_kv";
    private const string Token = "typed-kv-admin";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-typed-kv-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Path, string Protocol)> _requests = new();
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;
    private Uri _address = null!;
    private int _abortNextFrame;
    private int _holdNextFrame;

    public async Task InitializeAsync()
    {
        await StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = _address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var content = new StringContent("{\"name\":\"" + Database + "\"}", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("v1/db", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        _requests.Clear();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task TypedKv_BinaryExpiryAndPaging_PreserveValuesAcrossTwoReopens()
    {
        var expiry = new DateTimeOffset(2030, 11, 6, 1, 2, 3, TimeSpan.FromHours(8)).AddTicks(7);
        long version;
        using (var client = CreateClient())
        {
            Assert.Null(await client.GetKvAsync(Database, "Cache", new byte[] { 0, 0 }));
            version = await client.PutKvAsync(Database, "Cache", new byte[] { 0, 0 }, new byte[] { 0, 128, 255 }, expiry);
            await client.PutKvAsync(Database, "Cache", new byte[] { 0, 1 }, ReadOnlyMemory<byte>.Empty);
            await client.PutKvAsync(Database, "Cache", new byte[] { 0, 2 }, new byte[] { 42 });
            await client.PutKvAsync(Database, "Cache", new byte[] { 1, 0 }, new byte[] { 99 });
        }
        for (int reopen = 0; reopen < 2; reopen++)
        {
            await StopAsync();
            await StartAsync();
            using var client = CreateClient();
            var value = await client.GetKvAsync(Database, "Cache", new byte[] { 0, 0 });
            Assert.NotNull(value);
            Assert.Equal(new byte[] { 0, 128, 255 }, value.Value);
            Assert.Equal(version, value.Version);
            Assert.Equal(expiry.ToUniversalTime(), value.ExpiresAtUtc);
            var first = await client.ScanKvPageAsync(Database, "Cache", new byte[] { 0 }, limit: 2);
            Assert.Equal(2, first.Count);
            Assert.Equal(new byte[] { 0, 0 }, first[0].Key);
            Assert.Equal(new byte[] { 0, 1 }, first[1].Key);
            Assert.Empty(first[1].Value);
            var second = await client.ScanKvPageAsync(Database, "Cache", new byte[] { 0 }, first[^1].Key, limit: 2);
            Assert.Equal(new byte[] { 0, 2 }, Assert.Single(second).Key);
            Assert.Empty(await client.ScanKvPageAsync(Database, "Cache", new byte[] { 0 }, second[^1].Key, limit: 2));
        }
        AssertFrameOnly();
    }

    [Theory]
    [InlineData("readonly", "forbidden")]
    [InlineData(null, "http_error")]
    public async Task PutKv_Unauthorized_DoesNotWrite(string? token, string expectedCode)
    {
        using var client = CreateClient(token);
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => client.PutKvAsync(Database, "Cache", new byte[] { 1 }, new byte[] { 42 }));
        Assert.Equal(expectedCode, error.Code);
        using var admin = CreateClient();
        Assert.Null(await admin.GetKvAsync(Database, "Cache", new byte[] { 1 }));
        AssertFrameOnly();
    }

    [Fact]
    public async Task PutKv_DisconnectAfterCommit_ReportsUnknownAndDoesNotReplay()
    {
        using var client = CreateClient();
        long original = await client.PutKvAsync(Database, "Cache", new byte[] { 1 }, new byte[] { 1 });
        _requests.Clear();
        Interlocked.Exchange(ref _abortNextFrame, 1);
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => client.PutKvAsync(Database, "Cache", new byte[] { 1 }, new byte[] { 2 }));
        Assert.True(error.OutcomeUnknown);
        Assert.Single(_requests);
        var committed = await client.GetKvAsync(Database, "Cache", new byte[] { 1 });
        Assert.NotNull(committed);
        Assert.Equal(new byte[] { 2 }, committed.Value);
        Assert.Equal(original + 1, committed.Version);
        Assert.Equal(2, _requests.Count);
        AssertFrameOnly();
    }

    [Fact]
    public async Task TypedKv_CancelInFlight_AbortsAndClientRemainsUsable()
    {
        using var client = CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Interlocked.Exchange(ref _holdNextFrame, 1);
        var pending = client.GetKvAsync(Database, "Cache", new byte[] { 1 }, cancellationToken: cancellation.Token);
        await _held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<SndbFrameCanceledException>(() => pending);
        Assert.True(error.OutcomeUnknown);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await _canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await client.GetKvAsync(Database, "Cache", new byte[] { 1 }));
        Assert.Equal(2, _requests.Count);
        AssertFrameOnly();
    }

    [Fact]
    public async Task GetKv_ResponseBudget_RejectsAndReleasesResponse()
    {
        using var admin = CreateClient();
        byte[] expected = new byte[256];
        expected[255] = 255;
        await admin.PutKvAsync(Database, "Cache", new byte[] { 1 }, expected);
        using var small = new SndbFrameClient(_address, Token, maxPayloadBytes: 32, timeout: TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => small.GetKvAsync(Database, "Cache", new byte[] { 1 }));
        Assert.Equal("frame_budget", error.Code);
        var actual = await admin.GetKvAsync(Database, "Cache", new byte[] { 1 });
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value);
        AssertFrameOnly();
    }

    private SndbFrameClient CreateClient(string? token = Token)
        => new(_address, token, timeout: TimeSpan.FromSeconds(10));

    private void AssertFrameOnly()
    {
        Assert.NotEmpty(_requests);
        Assert.All(_requests, request =>
        {
            Assert.Equal("/v1/frame", request.Path);
            Assert.Equal("HTTP/2", request.Protocol);
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
            _requests.Enqueue((context.Request.Path.Value!, context.Request.Protocol));
            if (Interlocked.Exchange(ref _holdNextFrame, 0) == 1)
            {
                _held.TrySetResult();
                try { await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { _canceled.TrySetResult(); }
                return;
            }
            if (Interlocked.Exchange(ref _abortNextFrame, 0) == 1)
            {
                Stream original = context.Response.Body;
                await using var buffer = new MemoryStream();
                try { context.Response.Body = buffer; await next(context); context.Abort(); }
                finally { context.Response.Body = original; }
            }
            else await next(context);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _app.StartAsync(deadline.Token);
        _address = new Uri(Assert.Single(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
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
}
