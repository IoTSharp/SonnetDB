using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SonnetDB.Studio.Tests;

public sealed class StudioCopilotBrokerTests
{
    private const string TrustedOrigin = "https://studio.example.test:7443";
    private const string PublicOrigin = "https://runtime.example.test";
    private const string PublicToken = "unique-public-runtime-token";
    private const string ChatBody = """
        {"contractVersion":"m27-browser-direct-v1","runId":"run-1","request":{"db":"test","messages":[{"role":"user","content":"show tables"}],"mode":"read-only"}}
        """;
    private const string Events = """
        {"contractVersion":"m27-browser-direct-v1","runId":"run-1","sequence":1,"cursor":"c1","event":{"type":"answer","answer":"done"}}

        """;

    [Theory]
    [InlineData("http://runtime.example.test/")]
    [InlineData("https://user:secret@runtime.example.test/")]
    [InlineData("https://runtime.example.test/?token=secret")]
    [InlineData("https://runtime.example.test/#secret")]
    [InlineData("https://other.example.test/")]
    public void Options_WithUnapprovedOrUnsafeTarget_RejectsConfiguration(string address)
        => Assert.Throws<ArgumentException>(() => StudioCopilotOptions.Create(address, [PublicOrigin]));

    [Theory]
    [InlineData(0)]
    [InlineData(7201)]
    public void Options_WithUnboundedLifetime_RejectsConfiguration(int seconds)
        => Assert.Throws<ArgumentException>(() => StudioCopilotOptions.Create(PublicOrigin, [PublicOrigin], seconds));

    [Fact]
    public void Options_WithChangedPublicTarget_UsesIsolatedCredentialNames()
    {
        var first = StudioCopilotOptions.Create(PublicOrigin + "/tenant-a", [PublicOrigin]);
        var equivalent = StudioCopilotOptions.Create(PublicOrigin + "/tenant-a/", [PublicOrigin]);
        var otherPath = StudioCopilotOptions.Create(PublicOrigin + "/tenant-b", [PublicOrigin]);
        var otherOrigin = StudioCopilotOptions.Create("https://other.example.test", ["https://other.example.test"]);
        Assert.Equal(first.CredentialTarget, equivalent.CredentialTarget);
        Assert.NotEqual(first.CredentialTarget, otherPath.CredentialTarget);
        Assert.NotEqual(first.CredentialTarget, otherOrigin.CredentialTarget);
    }

    [Fact]
    public void WindowsCredential_WithUniqueTarget_RoundTripsAcrossInstancesAndDeletes()
    {
        string target = "SonnetDB/Studio/Copilot/test-" + Guid.NewGuid().ToString("N");
        var store = new StudioCopilotWindowsCredentialStore(target);
        try
        {
            Assert.Null(store.Read());
            var value = new StudioCopilotCredential(PublicToken, DateTimeOffset.UtcNow.AddMinutes(10));
            store.Write(value);
            Assert.Equal(value, new StudioCopilotWindowsCredentialStore(target).Read());
            store.Delete();
            Assert.Null(new StudioCopilotWindowsCredentialStore(target).Read());
            store.Delete();
        }
        finally { store.Delete(); }
    }

    [Fact]
    public async Task LoopbackBridge_WithWindowsCredential_ConnectsStreamsContinuesAndDisconnects()
    {
        var store = new StudioCopilotWindowsCredentialStore("SonnetDB/Studio/Copilot/test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new RecordingProvider();
            await using var fixture = await BridgeFixture.StartAsync(store, provider);
            using var status = await fixture.SendAsync(HttpMethod.Get, "copilot/status");
            Assert.False((await ReadJsonAsync(status)).GetProperty("connected").GetBoolean());
            using var connected = await fixture.SendAsync(HttpMethod.Post, "copilot/connect");
            string statusBody = await connected.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
            using var connectedStatus = JsonDocument.Parse(statusBody);
            Assert.True(connectedStatus.RootElement.GetProperty("connected").GetBoolean());
            Assert.DoesNotContain(PublicToken, statusBody, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Host.Token, statusBody, StringComparison.Ordinal);
            Assert.Equal(PublicToken, store.Read()!.AccessToken);

            using var readiness = await fixture.SendAsync(HttpMethod.Get, "copilot/readiness");
            Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
            Assert.Equal("ready", (await ReadJsonAsync(readiness)).GetProperty("status").GetString());
            Assert.Contains(StudioCopilotBroker.ContractHeader, string.Join(',', readiness.Headers.GetValues("Access-Control-Expose-Headers")), StringComparison.OrdinalIgnoreCase);

            using var chat = await fixture.SendAsync(HttpMethod.Post, "copilot/chat", ChatBody);
            Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
            Assert.Equal(Events, await chat.Content.ReadAsStringAsync());
            const string nextBody = """
                {"contractVersion":"m27-browser-direct-v1","runId":"run-1","request":{"messages":[{"role":"user","content":"continue"}]},"continuation":{"previousCursor":"c1","toolCallId":"t1","toolName":"list_tables","toolResult":"[]"}}
                """;
            using var continuation = await fixture.SendAsync(HttpMethod.Post, "copilot/continue", nextBody);
            Assert.Equal(HttpStatusCode.OK, continuation.StatusCode);
            Assert.Equal(Events, await continuation.Content.ReadAsStringAsync());
            var calls = provider.Requests.ToArray();
            Assert.Equal(4, calls.Length);
            Assert.All(calls, call =>
            {
                Assert.Equal("Bearer " + PublicToken, call.Authorization);
                Assert.Equal(PublicOrigin, call.Uri.GetLeftPart(UriPartial.Authority));
                Assert.False(call.Headers.Contains("X-SonnetDB-Studio-Bridge-Token", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(fixture.Host.Token, call.Body, StringComparison.Ordinal);
            });
            Assert.Equal("/tenant/v1/copilot/readiness", calls[0].Uri.AbsolutePath);
            Assert.Equal("/tenant/v1/copilot/chat/stream", calls[2].Uri.AbsolutePath);
            using var forwarded = JsonDocument.Parse(calls[3].Body);
            Assert.Equal("c1", forwarded.RootElement.GetProperty("continuation").GetProperty("previousCursor").GetString());
            Assert.Equal("read-only", forwarded.RootElement.GetProperty("request").GetProperty("mode").GetString());

            using var disconnected = await fixture.SendAsync(HttpMethod.Post, "copilot/disconnect");
            Assert.False((await ReadJsonAsync(disconnected)).GetProperty("connected").GetBoolean());
            Assert.Null(store.Read());
            using var unavailable = await fixture.SendAsync(HttpMethod.Post, "copilot/chat", ChatBody);
            Assert.Equal(HttpStatusCode.Unauthorized, unavailable.StatusCode);
            Assert.Equal(4, provider.Requests.Count);
        }
        finally { store.Delete(); }
    }

    [Theory]
    [InlineData("copilot/status?url=https://evil.example.test", null, false)]
    [InlineData("copilot/connect", "{\"accessToken\":\"database-token\"}", false)]
    [InlineData("copilot/disconnect", "{}", false)]
    [InlineData("copilot/status", null, true)]
    public async Task LoopbackBridge_WithCredentialOrUrlInjection_RejectsBeforePublicRequest(string route, string? body, bool authorization)
    {
        using var provider = new RecordingProvider();
        await using var fixture = await BridgeFixture.StartAsync(new MemoryCredentialStore(), provider);
        using var request = fixture.Request(route.Contains("status", StringComparison.Ordinal) ? HttpMethod.Get : HttpMethod.Post, route, body);
        if (authorization) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "database-token");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(provider.Requests);
        Assert.DoesNotContain("database-token", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.example.test", true, HttpStatusCode.Forbidden)]
    [InlineData(null, true, HttpStatusCode.Forbidden)]
    [InlineData(TrustedOrigin, false, HttpStatusCode.Unauthorized)]
    public async Task LoopbackBridge_WithoutTrustedRenderer_RejectsBeforePublicRequest(string? origin, bool token, HttpStatusCode expected)
    {
        using var provider = new RecordingProvider();
        await using var fixture = await BridgeFixture.StartAsync(new MemoryCredentialStore(), provider);
        using var request = fixture.Request(HttpMethod.Post, "copilot/connect");
        request.Headers.Remove("Origin");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (!token) request.Headers.Remove("X-SonnetDB-Studio-Bridge-Token");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(provider.Requests);
    }

    [Theory]
    [InlineData("\"mode\":42,")]
    [InlineData("\"mode\":\"read-write\",")]
    [InlineData("\"cursor\":\"server-relay-only\",")]
    [InlineData("\"authorization\":\"database-token\",")]
    [InlineData("\"messages\":[],")]
    public async Task LoopbackChat_WithInvalidRequestFields_RejectsBeforePublicRequest(string field)
    {
        using var provider = new RecordingProvider();
        await using var fixture = await BridgeFixture.StartAsync(ConnectedStore(), provider);
        string body = ChatBody.Replace("\"db\":\"test\",", field, StringComparison.Ordinal);
        using var response = await fixture.SendAsync(HttpMethod.Post, "copilot/chat", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task LoopbackChat_WithOversizedBody_RejectsBeforePublicRequest()
    {
        using var provider = new RecordingProvider();
        await using var fixture = await BridgeFixture.StartAsync(ConnectedStore(), provider);
        using var response = await fixture.SendAsync(HttpMethod.Post, "copilot/chat", new string(' ', 256 * 1024 + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(provider.Requests);
    }

    [Theory]
    [InlineData(302, true)]
    [InlineData(401, true)]
    [InlineData(200, false)]
    public async Task LoopbackConnect_WithRedirectErrorOrWrongContract_DoesNotPersistOrExposeSecret(int statusCode, bool version)
    {
        using var provider = new RecordingProvider((_, _) => Task.FromResult(Response((HttpStatusCode)statusCode, PublicToken, "application/json", version)));
        var store = new MemoryCredentialStore();
        await using var fixture = await BridgeFixture.StartAsync(store, provider);
        using var response = await fixture.SendAsync(HttpMethod.Post, "copilot/connect");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(PublicToken, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(store.Read());
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task LoopbackConnect_WithCanceledPrompt_DoesNotContactProviderOrPersist()
    {
        using var provider = new RecordingProvider();
        var store = new MemoryCredentialStore();
        await using var fixture = await BridgeFixture.StartAsync(store, provider, (_, _) => Task.FromResult<string?>(null));
        using var response = await fixture.SendAsync(HttpMethod.Post, "copilot/connect");
        Assert.True((await ReadJsonAsync(response)).GetProperty("canceled").GetBoolean());
        Assert.Null(store.Read());
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task LoopbackDisconnect_WithLatePrompt_CancelsConnectAndPreventsCredentialResurrection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new RecordingProvider();
        var store = new MemoryCredentialStore();
        await using var fixture = await BridgeFixture.StartAsync(store, provider, (_, _) => { entered.TrySetResult(); return prompt.Task; });
        var connecting = fixture.SendAsync(HttpMethod.Post, "copilot/connect");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var disconnected = await fixture.SendAsync(HttpMethod.Post, "copilot/disconnect");
            using var failedConnect = await connecting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failedConnect.StatusCode);
            prompt.TrySetResult(PublicToken);
            using var status = await fixture.SendAsync(HttpMethod.Get, "copilot/status");
            Assert.False((await ReadJsonAsync(status)).GetProperty("connected").GetBoolean());
            Assert.Null(store.Read());
            Assert.Empty(provider.Requests);
        }
        finally { prompt.TrySetResult(null); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoopbackDisconnect_WithLateReadinessSuccess_DoesNotRestoreCredential(bool useWindowsStore)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IStudioCopilotCredentialStore store = useWindowsStore
            ? new StudioCopilotWindowsCredentialStore("SonnetDB/Studio/Copilot/test-" + Guid.NewGuid().ToString("N"))
            : new MemoryCredentialStore();
        using var provider = new RecordingProvider(async (_, _) =>
        {
            entered.TrySetResult();
            // 模拟忽略取消但在八秒内返回的公网响应，确认取消后不会保存迟到凭据。
            await release.Task.WaitAsync(TimeSpan.FromSeconds(8));
            return Response(HttpStatusCode.OK, "{\"status\":\"ready\"}", "application/json");
        });
        try
        {
            await using var fixture = await BridgeFixture.StartAsync(store, provider);
            var connecting = fixture.SendAsync(HttpMethod.Post, "copilot/connect");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var disconnected = await fixture.SendAsync(HttpMethod.Post, "copilot/disconnect");
                Assert.False((await ReadJsonAsync(disconnected)).GetProperty("connected").GetBoolean());
                release.TrySetResult();
                using var failedConnect = await connecting.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, failedConnect.StatusCode);
                Assert.Null(store.Read());
                using var status = await fixture.SendAsync(HttpMethod.Get, "copilot/status");
                Assert.False((await ReadJsonAsync(status)).GetProperty("connected").GetBoolean());
                Assert.Single(provider.Requests);
            }
            finally { release.TrySetResult(); }
        }
        finally { store.Delete(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoopbackStream_AfterFirstChunkDisconnectOrCallerAbort_CancelsUpstreamRead(bool abortCaller)
    {
        using var upstream = new CancelableChunkStream(Encoding.UTF8.GetBytes(Events));
        using var provider = new RecordingProvider((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(upstream) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            response.Headers.Add(StudioCopilotBroker.ContractHeader, StudioCopilotBroker.ContractVersion);
            return Task.FromResult(response);
        });
        await using var fixture = await BridgeFixture.StartAsync(ConnectedStore(), provider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = fixture.Request(HttpMethod.Post, "copilot/chat", ChatBody);
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var incoming = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] first = new byte[Encoding.UTF8.GetByteCount(Events)];
        await incoming.ReadExactlyAsync(first, timeout.Token);
        Assert.Equal(Events, Encoding.UTF8.GetString(first));
        await upstream.SecondReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] remaining = new byte[256];
        var pendingRead = incoming.ReadAsync(remaining, timeout.Token).AsTask();
        if (abortCaller)
        {
            timeout.Cancel();
            response.Dispose();
        }
        else
        {
            using var disconnected = await fixture.SendAsync(HttpMethod.Post, "copilot/disconnect");
            Assert.False((await ReadJsonAsync(disconnected)).GetProperty("connected").GetBoolean());
        }
        await upstream.ReadCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Record.ExceptionAsync(async () => { await pendingRead.WaitAsync(TimeSpan.FromSeconds(5)); });
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(0, upstream.LateChunksWritten);
        Assert.Equal(2, upstream.ReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoopbackStream_WhenDisconnectOrCallerCancels_PropagatesCancellationToProvider(bool cancelCaller)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new RecordingProvider(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(8), token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            return Response(HttpStatusCode.OK, Events, "application/x-ndjson");
        });
        await using var fixture = await BridgeFixture.StartAsync(ConnectedStore(), provider);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = fixture.Request(HttpMethod.Post, "copilot/chat", ChatBody);
        var running = fixture.Client.SendAsync(request, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller) caller.Cancel();
        else { using var disconnected = await fixture.SendAsync(HttpMethod.Post, "copilot/disconnect"); }
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        else { using var response = await running; Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); }
    }

    [Fact]
    public async Task LoopbackReadiness_WithExpiredCredential_DeletesItWithoutPublicRequest()
    {
        using var provider = new RecordingProvider();
        var store = new MemoryCredentialStore { Credential = new StudioCopilotCredential(PublicToken, DateTimeOffset.UtcNow.AddSeconds(-1)) };
        await using var fixture = await BridgeFixture.StartAsync(store, provider);
        using var response = await fixture.SendAsync(HttpMethod.Get, "copilot/readiness");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(store.Read());
        Assert.Empty(provider.Requests);
    }

    [Theory]
    [InlineData("text/html", 32)]
    [InlineData("application/x-ndjson", 4 * 1024 * 1024 + 1)]
    public async Task LoopbackStream_WithInvalidTypeOrOversizedResponse_RejectsPublicResponse(string mediaType, int bytes)
    {
        using var provider = new RecordingProvider((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new string('x', bytes), mediaType)));
        await using var fixture = await BridgeFixture.StartAsync(ConnectedStore(), provider);
        using var response = await fixture.SendAsync(HttpMethod.Post, "copilot/chat", ChatBody);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(new string('x', 32), await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoopbackStream_WithEventStream_PreservesVersionedEvents()
    {
        const string events = "data: {\"contractVersion\":\"m27-browser-direct-v1\",\"runId\":\"run-1\",\"sequence\":1,\"cursor\":\"c1\",\"event\":{\"type\":\"answer\",\"answer\":\"done\"}}\n\n";
        using var provider = new RecordingProvider((_, _) => Task.FromResult(Response(HttpStatusCode.OK, events, "text/event-stream")));
        await using var fixture = await BridgeFixture.StartAsync(ConnectedStore(), provider);
        using var response = await fixture.SendAsync(HttpMethod.Post, "copilot/chat", ChatBody);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(StudioCopilotBroker.ContractVersion, Assert.Single(response.Headers.GetValues(StudioCopilotBroker.ContractHeader)));
        Assert.Equal(events, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LoopbackHost_WhenDisposedDuringConnect_CancelsPromptWithinDeadline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new RecordingProvider();
        var fixture = await BridgeFixture.StartAsync(new MemoryCredentialStore(), provider, async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(8), token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            return PublicToken;
        });
        var connecting = fixture.SendAsync(HttpMethod.Post, "copilot/connect");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.StopHostAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var response = await connecting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static MemoryCredentialStore ConnectedStore() => new()
    { Credential = new StudioCopilotCredential(PublicToken, DateTimeOffset.UtcNow.AddMinutes(10)) };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage Response(HttpStatusCode code, string body, string mediaType, bool version = true)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        if (version) response.Headers.Add(StudioCopilotBroker.ContractHeader, StudioCopilotBroker.ContractVersion);
        if (code == HttpStatusCode.Redirect) response.Headers.Location = new Uri("https://unapproved.example.test/");
        return response;
    }

    private sealed record PublicCall(Uri Uri, string? Authorization, string Headers, string Body);

    private sealed class RecordingProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null) : HttpMessageHandler
    {
        public ConcurrentQueue<PublicCall> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new PublicCall(request.RequestUri!, request.Headers.Authorization?.ToString(), request.Headers.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond is null
                ? request.Method == HttpMethod.Get
                    ? Response(HttpStatusCode.OK, "{\"status\":\"ready\"}", "application/json")
                    : Response(HttpStatusCode.OK, Events, "application/x-ndjson")
                : await respond(request, cancellationToken);
        }
    }

    private sealed class MemoryCredentialStore : IStudioCopilotCredentialStore
    {
        public StudioCopilotCredential? Credential { get; set; }
        public StudioCopilotCredential? Read() => Credential;
        public void Write(StudioCopilotCredential credential) => Credential = credential;
        public void Delete() => Credential = null;
    }

    private sealed class CancelableChunkStream(byte[] first) : Stream
    {
        private int _readCount;
        private int _lateChunksWritten;
        public TaskCompletionSource SecondReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount => Volatile.Read(ref _readCount);
        public int LateChunksWritten => Volatile.Read(ref _lateChunksWritten);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _readCount);
            if (call == 1) { first.AsMemory().CopyTo(buffer); return first.Length; }
            if (call > 2) return 0;
            SecondReadEntered.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken); }
            catch (OperationCanceledException) { ReadCanceled.TrySetResult(); throw; }
            Interlocked.Increment(ref _lateChunksWritten);
            buffer.Span[0] = (byte)'!';
            return 1;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BridgeFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private bool _disposed;
        private bool _hostStopped;
        private BridgeFixture(string directory, StudioBridgeHost host)
        {
            _directory = directory;
            Host = host;
            Client = new HttpClient { BaseAddress = new Uri(host.EndpointUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
        }
        public StudioBridgeHost Host { get; }
        public HttpClient Client { get; }

        public static async Task<BridgeFixture> StartAsync(IStudioCopilotCredentialStore store, HttpMessageHandler handler,
            Func<string, CancellationToken, Task<string?>>? prompt = null)
        {
            string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sonnetdb-studio-copilot-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var options = new StudioHostOptions(TrustedOrigin + "/admin", "/admin/app/studio", 1280, 800, true, port,
                Path.Combine(directory, "data"), "http://127.0.0.1:5080", Path.Combine(directory, "connections.json"), null, false, false);
            var broker = new StudioCopilotBroker(StudioCopilotOptions.Create(PublicOrigin + "/tenant/", [PublicOrigin]), store,
                prompt ?? ((_, _) => Task.FromResult<string?>(PublicToken)), handler);
            var fixture = new BridgeFixture(directory, new StudioBridgeHost(options, broker));
            try
            {
                using var start = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await fixture.Host.StartAsync(start.Token);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public HttpRequestMessage Request(HttpMethod method, string path, string? body = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Origin", TrustedOrigin);
            request.Headers.Add("X-SonnetDB-Studio-Bridge-Token", Host.Token);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null)
        {
            using var request = Request(method, path, body);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await StopHostAsync(); }
            finally
            {
                Client.Dispose();
                string temp = Path.GetFullPath(Path.GetTempPath());
                if (Path.GetFullPath(_directory).StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(_directory).StartsWith("sonnetdb-studio-copilot-", StringComparison.Ordinal))
                    Directory.Delete(_directory, recursive: true);
            }
        }

        public ValueTask StopHostAsync()
        {
            if (_hostStopped) return ValueTask.CompletedTask;
            _hostStopped = true;
            return Host.DisposeAsync();
        }
    }
}
