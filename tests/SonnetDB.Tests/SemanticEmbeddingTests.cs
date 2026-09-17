using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;
using SonnetDB.Protocol;
using SonnetDB.SemanticContent;
using SonnetDB.SemanticSearch;
using Xunit;

namespace SonnetDB.Tests;

public sealed class SemanticEmbeddingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SonnetDB.Embedding." + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(45));
    private readonly Tsdb _db;

    public SemanticEmbeddingTests() => _db = Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root, "db") });

    [Theory]
    [InlineData(SemanticDataEgressMode.LocalOnly, null)]
    [InlineData(SemanticDataEgressMode.ConfiguredProvider, "different-provider")]
    [InlineData(SemanticDataEgressMode.ExternalProvider, "different-target")]
    public async Task EmbedText_DisallowedExternalProvider_DeniesBeforeInvocation(SemanticDataEgressMode mode, string? target)
    {
        var provider = new RecordingProvider(local: false);
        var service = CreateService(provider, new SemanticDataEgressPolicy(mode, target));
        await Assert.ThrowsAsync<SemanticEmbeddingPolicyException>(() => service.EmbedTextAsync(_db, "secret-input", _deadline.Token));
        Assert.Equal(0, provider.Calls);
        var entry = Assert.Single(ReadAudit());
        Assert.Equal("denied", entry.Status);
        Assert.Equal("semantic_egress_denied", entry.ErrorCode);
        Assert.DoesNotContain("secret-input", JsonSerializer.Serialize(entry, ServerJsonContext.Default.SemanticEmbeddingAuditEntry));
    }

    [Theory]
    [InlineData(SemanticDataEgressMode.ConfiguredProvider, "recording")]
    [InlineData(SemanticDataEgressMode.ExternalProvider, "embedding-target")]
    public async Task EmbedText_ExplicitProviderOrTarget_AllowsAuditedInvocation(SemanticDataEgressMode mode, string target)
    {
        var provider = new RecordingProvider(local: false);
        float[] vector = await CreateService(provider, new SemanticDataEgressPolicy(mode, target))
            .EmbedTextAsync(_db, "hello", _deadline.Token);
        Assert.Equal(new[] { 1f, 0f }, vector);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("succeeded", Assert.Single(ReadAudit()).Status);
    }

    [Fact]
    public async Task EmbedText_AuditStorageUnavailable_DoesNotInvokeProvider()
    {
        var provider = new RecordingProvider();
        _db.Keyspaces.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => CreateService(provider).EmbedTextAsync(_db, "hello", _deadline.Token));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task EmbedText_WeakWalConfigurationAndSyncFailure_DoesNotInvokeProvider()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root, "weak-wal"),
            Kv = new SonnetDB.Kv.KvOptions { SyncWalOnEveryWrite = false } });
        var audit = new SemanticEmbeddingAuditStore(db);
        audit.WalSyncForTest = () => throw new IOException("injected sync failure");
        var provider = new RecordingProvider();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => CreateService(provider).EmbedTextAsync(db, "hello", _deadline.Token));
            Assert.Equal(0, provider.Calls);
        }
        finally { audit.WalSyncForTest = null; }
    }

    [Fact]
    public async Task EmbedText_ProviderFailure_PersistsSafeFailureWithoutExceptionPayload()
    {
        var provider = new RecordingProvider { Handler = _ => ValueTask.FromException<float[]>(new IOException("secret-input secret-key")) };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(provider)
            .EmbedTextAsync(_db, "secret-input", _deadline.Token));
        Assert.DoesNotContain("secret", failure.ToString());
        var entry = Assert.Single(ReadAudit());
        Assert.Equal("failed", entry.Status);
        Assert.Equal("semantic_provider_failed", entry.ErrorCode);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(entry, ServerJsonContext.Default.SemanticEmbeddingAuditEntry));
    }

    [Fact]
    public async Task EmbedText_Cancellation_PersistsCancelledState()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingProvider { Handler = async token =>
        {
            entered.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            return [1f, 0f];
        } };
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        Task<float[]> call = CreateService(provider).EmbedTextAsync(_db, "secret-cancelled", cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal("cancelled", Assert.Single(ReadAudit()).Status);
    }

    [Fact]
    public async Task EmbedText_Deadline_PersistsTimeoutState()
    {
        var provider = new RecordingProvider { Handler = async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            return [1f, 0f];
        } };
        await Assert.ThrowsAsync<TimeoutException>(() => CreateService(provider, timeoutSeconds: 1)
            .EmbedTextAsync(_db, "hello", _deadline.Token));
        Assert.Equal("timed_out", Assert.Single(ReadAudit()).Status);
    }

    [Fact]
    public async Task EmbedObject_FixedTextVersion_UsesExistingTextEncoderAndRedactsPath()
    {
        var source = await PutObjectAsync("private-name.txt", "text/plain; charset=utf-8", "hello");
        var provider = new RecordingProvider();
        ObjectEmbeddingResponse result = await CreateService(provider).EmbedObjectAsync(_db,
            new SemanticObjectReference(source.Bucket, source.Key, source.VersionId, source.ETag), _deadline.Token);
        Assert.Equal(source.VersionId, result.Object.VersionId);
        Assert.Equal("hello", provider.Text);
        var audit = Assert.Single(ReadAudit());
        Assert.Equal("object", audit.InputKind);
        Assert.NotNull(audit.ObjectIdentityHash);
        Assert.DoesNotContain(source.Key, JsonSerializer.Serialize(audit, ServerJsonContext.Default.SemanticEmbeddingAuditEntry));
    }

    [Fact]
    public async Task EmbedObject_WrongETagOrUnsupportedType_DoesNotInvokeProvider()
    {
        var source = await PutObjectAsync("document.pdf", "application/pdf", "not-a-pdf");
        var provider = new RecordingProvider();
        var service = CreateService(provider);
        await Assert.ThrowsAsync<SemanticObjectVersionException>(() => service.EmbedObjectAsync(_db,
            new SemanticObjectReference(source.Bucket, source.Key, source.VersionId, "wrong"), _deadline.Token));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.EmbedObjectAsync(_db,
            new SemanticObjectReference(source.Bucket, source.Key, source.VersionId), _deadline.Token));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task EmbedObject_ImageVersion_ReusesImageEncoder()
    {
        var source = await PutObjectAsync("image.png", "image/png", "fake-image");
        var provider = new RecordingProvider();
        await CreateService(provider).EmbedObjectAsync(_db,
            new SemanticObjectReference(source.Bucket, source.Key, eTag: source.ETag), _deadline.Token);
        Assert.Equal(1, provider.ImageCalls);
        Assert.Equal("succeeded", Assert.Single(ReadAudit()).Status);
    }

    [Fact]
    public async Task EmbedObject_TooLargeOrUnversioned_DoesNotInvokeProvider()
    {
        var source = await PutObjectAsync("text.txt", "text/plain", "too-long");
        var provider = new RecordingProvider();
        var options = Options.Create(new ServerOptions { SemanticSearch = new SemanticSearchOptions
            { Enabled = true, MaxObjectEmbeddingBytes = 2 } });
        var service = new SemanticEmbeddingService(provider, new MultimodalObjectEmbeddingProvider(provider), options);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.EmbedObjectAsync(_db,
            new SemanticObjectReference(source.Bucket, source.Key, source.VersionId), _deadline.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EmbedObjectAsync(_db,
            new SemanticObjectReference(source.Bucket, source.Key), _deadline.Token));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Audit_RestartAndBoundedPagination_PreservesCallsAndCursor()
    {
        string path = Path.Combine(_root, "recovery");
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = path }))
        {
            var service = CreateService(new RecordingProvider());
            for (int index = 0; index < 3; index++)
                await service.EmbedTextAsync(db, "hello", _deadline.Token);
        }
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = path });
        var store = new SemanticEmbeddingAuditStore(reopened);
        var first = store.Read(1, null, _deadline.Token);
        var second = store.Read(2, first.ContinuationToken, _deadline.Token);
        Assert.Single(first.Entries);
        Assert.NotNull(first.ContinuationToken);
        Assert.Equal(2, second.Entries.Count);
        Assert.DoesNotContain(second.Entries, entry => entry.CallId == first.Entries[0].CallId);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Read(201, null, _deadline.Token));
        Assert.Throws<ArgumentException>(() => store.Read(1, "invalid", _deadline.Token));
    }

    [Fact]
    public async Task Endpoints_ObjectCallAndAudit_EnforceAuthAndPageBounds()
    {
        var options = new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            Tokens = new Dictionary<string, string> { ["admin"] = ServerRoles.Admin, ["reader"] = ServerRoles.ReadOnly,
                ["writer"] = ServerRoles.ReadWrite },
            SemanticSearch = new SemanticSearchOptions { Enabled = true, Backend = "managed", Dimensions = 2 },
        };
        await using var app = TestServerHost.Build(options, services =>
            services.AddSingleton<IMultimodalEmbeddingProvider>(new RecordingProvider()));
        await app.StartAsync(_deadline.Token);
        try
        {
            string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
            var registry = app.Services.GetRequiredService<TsdbRegistry>();
            Assert.True(registry.TryCreate("one", out var db));
            Assert.True(registry.TryCreate("two", out var other));
            var store = new SndbObjectStore(db);
            store.CreateBucket("texts");
            using var input = new MemoryStream("hello"u8.ToArray());
            var source = await store.PutObjectAsync("texts", "note.txt", input, "text/plain", cancellationToken: _deadline.Token);
            var request = new ObjectEmbeddingRequest(new SemanticObjectReference("texts", "note.txt", source.VersionId));
            const string endpoint = "/v1/db/one/semantic/embeddings/object";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(endpoint, request,
                ServerJsonContext.Default.ObjectEmbeddingRequest, _deadline.Token)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "reader");
            var response = await client.PostAsJsonAsync(endpoint, request, ServerJsonContext.Default.ObjectEmbeddingRequest, _deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.ObjectEmbeddingResponse, _deadline.Token);
            Assert.Equal(source.VersionId, result!.Object.VersionId);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/db/one/semantic/embeddings/audit", _deadline.Token)).StatusCode);
            string auditKeyspace = SemanticEmbeddingAuditStore.KeyspaceName;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
                $"/v1/db/one/kv/{auditKeyspace}/scan", new KvScanCursorRequest(),
                ServerJsonContext.Default.KvScanCursorRequest, _deadline.Token)).StatusCode);
            var listResponse = await client.PostAsync("/v1/db/one/kv/keyspaces", null, _deadline.Token);
            var keyspaces = await listResponse.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvKeyspaceListResponse, _deadline.Token);
            Assert.DoesNotContain(auditKeyspace, keyspaces!.Keyspaces);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "writer");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
                $"/v1/db/one/kv/{auditKeyspace}/remove-prefix", new KvPrefixRequest("", null),
                ServerJsonContext.Default.KvPrefixRequest, _deadline.Token)).StatusCode);
            var frame = new ArrayBufferWriter<byte>();
            KvFrameCodec.EncodePutRequest(frame, 1, "one", auditKeyspace.ToUpperInvariant() + ".", "forged"u8, "{}"u8);
            using var frameContent = new ByteArrayContent(frame.WrittenMemory.ToArray());
            frameContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
            var frameResponse = await client.PostAsync("/v1/frame", frameContent, _deadline.Token);
            Assert.Equal(HttpStatusCode.OK, frameResponse.StatusCode);
            var frameBody = new ReadOnlySequence<byte>(await frameResponse.Content.ReadAsByteArrayAsync(_deadline.Token));
            Assert.True(FrameCodec.TryReadFrame(ref frameBody, out FrameHeader frameHeader, out _));
            Assert.True(frameHeader.IsError);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
                $"/v1/db/one/kv/{auditKeyspace}/get", new KvGetRequest("forged"),
                ServerJsonContext.Default.KvGetRequest, _deadline.Token)).StatusCode);
            var page = await client.GetFromJsonAsync("/v1/db/one/semantic/embeddings/audit?limit=1",
                ServerJsonContext.Default.SemanticEmbeddingAuditPage, _deadline.Token);
            Assert.Single(page!.Entries);
            Assert.Empty(new SemanticEmbeddingAuditStore(other).Read(10, null, _deadline.Token).Entries);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/db/one/semantic/embeddings/audit?limit=201", _deadline.Token)).StatusCode);
        }
        finally { await app.StopAsync(_deadline.Token); }
    }

    private SemanticEmbeddingService CreateService(RecordingProvider provider, SemanticDataEgressPolicy? policy = null,
        int timeoutSeconds = 10) => new(provider, new MultimodalObjectEmbeddingProvider(provider),
        Options.Create(new ServerOptions { SemanticSearch = new SemanticSearchOptions
            { Enabled = true, DataEgressPolicy = policy ?? new(), EmbeddingTimeoutSeconds = timeoutSeconds } }));

    private IReadOnlyList<SemanticEmbeddingAuditEntry> ReadAudit()
        => new SemanticEmbeddingAuditStore(_db).Read(100, null, _deadline.Token).Entries;

    private async Task<SndbObjectInfo> PutObjectAsync(string key, string type, string text)
    {
        var store = new SndbObjectStore(_db);
        store.CreateBucket("objects");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await store.PutObjectAsync("objects", key, input, type, cancellationToken: _deadline.Token);
    }

    public void Dispose()
    {
        _db.Dispose();
        _deadline.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingProvider(bool local = true) : IMultimodalEmbeddingProvider
    {
        public MultimodalEmbeddingProviderInfo Info { get; } = new("recording", "test-profile", 2, true)
            { IsLocal = local, Target = "embedding-target" };
        public int Calls { get; private set; }
        public int ImageCalls { get; private set; }
        public string? Text { get; private set; }
        public Func<CancellationToken, ValueTask<float[]>>? Handler { get; init; }

        public ValueTask<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls++;
            Text = text;
            return Handler?.Invoke(cancellationToken) ?? ValueTask.FromResult(new[] { 1f, 0f });
        }

        public ValueTask<float[]> EmbedImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
        {
            Calls++;
            ImageCalls++;
            return Handler?.Invoke(cancellationToken) ?? ValueTask.FromResult(new[] { 1f, 0f });
        }
    }
}
