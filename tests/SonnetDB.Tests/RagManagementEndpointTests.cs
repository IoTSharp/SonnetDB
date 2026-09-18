using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Engine;
using SonnetDB.Generations;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Protocol;
using SonnetDB.SemanticContent;
using SonnetDB.SemanticSearch;
using Xunit;

namespace SonnetDB.Tests;

public sealed class RagManagementEndpointTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-rag-management-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private readonly FixtureProvider _provider = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private Tsdb _database = null!;
    private ServerOptions _options = null!;
    private const string Base = "/v1/db/one/semantic/rag/";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _options = new ServerOptions
        {
            DataRoot = Path.Combine(_root, "data"),
            Tokens = new() { ["admin"] = ServerRoles.Admin, ["reader"] = ServerRoles.ReadOnly, ["writer"] = ServerRoles.ReadWrite },
        };
        _options.Copilot.Docs.RagProfile = new()
        {
            Id = "builtin-v1",
            Provider = "builtin",
            Model = "builtin-hash",
            Revision = "1",
            Dimensions = 384,
            SupportedModalities = [SemanticContentModality.Text, SemanticContentModality.Document],
        };
        _options.Copilot.Embedding.ApiKey = "DO_NOT_EXPOSE_KEY";
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), JsonSerializer.Serialize(new { SonnetDBServer = _options }));
        _app = Program.BuildApp(["--contentRoot", _root, "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0"], services =>
        {
            services.AddSingleton<IEmbeddingProvider>(_provider);
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(_options));
        });
        await _app.StartAsync(_deadline.Token);
        var url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(20) };
        _app.Services.GetRequiredService<TsdbRegistry>().TryCreate("one", out _database);
        As("admin");
    }

    [Fact]
    public async Task ManagementEndpoints_RequireDatabaseAdmin_AndExposeOnlySafeProfile()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(Base + "profiles", _deadline.Token)).StatusCode);
        As("writer");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(Base + "status?stream=docs", _deadline.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("rebuild", Request(0))).StatusCode);
        As("reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(Base + "audit", _deadline.Token)).StatusCode);
        As("admin");
        string body = await _client.GetStringAsync(Base + "profiles", _deadline.Token);
        Assert.Contains("builtin-v1", body);
        Assert.DoesNotContain("DO_NOT_EXPOSE_KEY", body);
        Assert.DoesNotContain("endpoint", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localModelPath", body, StringComparison.OrdinalIgnoreCase);
        var empty = await Status();
        Assert.Equal(0, empty.ActiveRevision);
        Assert.Null(empty.Pending);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Rebuild_ConfiguredProfile_PublishesNewGenerationWithAuditAndCas()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.Conflict, (await Post("rebuild", Request(0))).StatusCode);
        Assert.Equal(0, _provider.Calls);
        Assert.Equal(HttpStatusCode.OK, (await Post("rebuild", Request(1))).StatusCode);
        Assert.Equal(2, (await Status()).ActiveRevision);
        Assert.Equal(1, _provider.Calls);
        var audit = await _client.GetFromJsonAsync(Base + "audit?limit=1", ServerJsonContext.Default.RagManagementAuditPage, _deadline.Token);
        Assert.Single(audit!.Entries);
        Assert.Equal("succeeded", audit.Entries[0].Status);
        Assert.NotNull(audit.ContinuationToken);
        string body = JsonSerializer.Serialize(audit, ServerJsonContext.Default.RagManagementAuditPage);
        Assert.DoesNotContain("PRIVATE_BODY", body);
        Assert.DoesNotContain("PRIVATE_SOURCE", body);
        Assert.DoesNotContain("DO_NOT_EXPOSE_KEY", body);
        var second = await _client.GetFromJsonAsync(Base + "audit?limit=1&continuationToken=" + Uri.EscapeDataString(audit.ContinuationToken),
            ServerJsonContext.Default.RagManagementAuditPage, _deadline.Token);
        Assert.Equal("failed", Assert.Single(second!.Entries).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(Base + "audit?limit=201", _deadline.Token)).StatusCode);
    }

    [Fact]
    public async Task Rebuild_Failure_LeavesPendingForIdentityBoundResumeAndDiscard()
    {
        await Seed();
        _provider.Fail = true;
        using var failed = await Post("rebuild", Request(1));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain("PRIVATE_BODY", await failed.Content.ReadAsStringAsync(_deadline.Token));
        var pending = await Status();
        Assert.Equal(1, pending.ActiveRevision);
        Assert.NotNull(pending.Pending);
        Assert.Equal(HttpStatusCode.Conflict, (await Post("discard", Request(1) with { PendingGenerationId = Guid.NewGuid().ToString("N") })).StatusCode);
        _provider.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await Post("resume", Request(1) with { PendingGenerationId = pending.Pending.GenerationId })).StatusCode);
        Assert.Equal(2, (await Status()).ActiveRevision);
        Assert.Null((await Status()).Pending);
        _provider.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post("rebuild", Request(2))).StatusCode);
        var discard = await Status();
        Assert.Equal(HttpStatusCode.OK, (await Post("discard", Request(2) with { PendingGenerationId = discard.Pending!.GenerationId })).StatusCode);
        Assert.Null((await Status()).Pending);
        Assert.Equal(2, (await Status()).ActiveRevision);
    }

    [Fact]
    public async Task Cleanup_LeasedGeneration_DefersUntilLeaseRelease()
    {
        await Seed();
        using (var lease = _database.Generations.AcquireActive("docs"))
        {
            Assert.Equal(HttpStatusCode.OK, (await Post("rebuild", Request(1))).StatusCode);
            var cleanup = await Post("cleanup", Request(2) with { RetiredBeforeUtc = DateTimeOffset.UtcNow });
            var result = await cleanup.Content.ReadFromJsonAsync(ServerJsonContext.Default.RagManagementResult, _deadline.Token);
            Assert.Contains(1L, result!.DeferredRevisions);
            Assert.Empty(result.RemovedRevisions);
        }
        var response = await Post("cleanup", Request(2) with { RetiredBeforeUtc = DateTimeOffset.UtcNow });
        var removed = await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.RagManagementResult, _deadline.Token);
        Assert.Contains(1L, removed!.RemovedRevisions);
        Assert.Equal(2, (await Status()).ActiveRevision);
    }

    [Fact]
    public async Task Requests_RejectMissingCasUnknownProviderFieldsAndUnconfiguredProfile()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("rebuild", Request(1) with { ExpectedRevision = null })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post("rebuild", Request(1) with { ProfileId = "untrusted" })).StatusCode);
        using var body = new StringContent("{\"stream\":\"docs\",\"expectedRevision\":1,\"profileId\":\"builtin-v1\",\"endpoint\":\"https://untrusted.example/\"}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(Base + "rebuild", body, _deadline.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("cleanup", Request(1))).StatusCode);
        Assert.Equal(0, _provider.Calls);
        Assert.Equal(1, (await Status()).ActiveRevision);
    }

    [Fact]
    public async Task InternalResources_RejectRestAndFrameWindowsAliases()
    {
        await Seed();
        using var lease = _database.Generations.AcquireActive("docs");
        string resource = lease.GetRequiredResource(RagIngestionWriter.ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection).Name;
        As("writer");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/v1/db/one/kv/RAG-INGESTION-JOBS./get",
            new KvGetRequest("docs"), ServerJsonContext.Default.KvGetRequest, _deadline.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/v1/db/one/documents/{resource.ToUpperInvariant()}./find",
            new DocumentFindRequest(), ServerJsonContext.Default.DocumentFindRequest, _deadline.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/v1/db/one/kv/__RAG_MANAGEMENT_AUDIT./get",
            new KvGetRequest("forged"), ServerJsonContext.Default.KvGetRequest, _deadline.Token)).StatusCode);
        var frame = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodePutRequest(frame, 1, "one", resource.ToUpperInvariant() + ".", "snapshot"u8, "{}"u8);
        using var content = new ByteArrayContent(frame.WrittenMemory.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        var response = await _client.PostAsync("/v1/frame", content, _deadline.Token);
        var sequence = new ReadOnlySequence<byte>(await response.Content.ReadAsByteArrayAsync(_deadline.Token));
        Assert.True(FrameCodec.TryReadFrame(ref sequence, out FrameHeader header, out _));
        Assert.True(header.IsError);
        As("admin");
        Assert.Equal(1, (await Status()).ActiveRevision);
    }

    [Fact]
    public async Task Rebuild_NewConfiguredProfile_ChangesSpaceWithoutAcceptingArbitraryProfile()
    {
        await Seed();
        _options.Copilot.Docs.RagProfile!.Id = "builtin-v2";
        Assert.Equal(HttpStatusCode.Conflict, (await Post("rebuild", Request(1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("rebuild", Request(1) with { ProfileId = "builtin-v2" })).StatusCode);
        var status = await Status();
        Assert.Equal(2, status.ActiveRevision);
        Assert.Equal("builtin-v2", status.ActiveProfileId);
    }

    [Fact]
    public async Task Rebuild_InitialAuditStorageFailure_PreventsProviderAndMutation()
    {
        await Seed();
        // 在本测试拥有的数据库目录内用文件占据审计目录，模拟真实存储故障。
        string auditPath = Path.Combine(_database.Keyspaces.KeyspacesDirectory, "__rag_management_audit");
        File.WriteAllText(auditPath, "unavailable");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post("rebuild", Request(1))).StatusCode);
        Assert.Equal(0, _provider.Calls);
        Assert.Equal(1, (await Status()).ActiveRevision);
        Assert.Null((await Status()).Pending);
    }

    [Fact]
    public async Task Rebuild_CompletionAuditFailure_DoesNotLabelPublishedMutationFailed()
    {
        await Seed();
        _provider.BeforeReturn = () => _database.Keyspaces.Open("__rag_management_audit").Dispose();
        using var response = await Post("rebuild", Request(1));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("rag_audit_completion_failed", await response.Content.ReadAsStringAsync(_deadline.Token));
        Assert.Equal(2, (await Status()).ActiveRevision);
        var audit = await _client.GetFromJsonAsync(Base + "audit", ServerJsonContext.Default.RagManagementAuditPage, _deadline.Token);
        Assert.Equal("started", Assert.Single(audit!.Entries).Status);
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task Management_WithoutConfiguredProvider_CanReadAndDiscardPending()
    {
        await Seed();
        _provider.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post("rebuild", Request(1))).StatusCode);
        _options.Copilot.Docs.RagProfile = null;
        var status = await Status();
        Assert.NotNull(status.Pending);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync(Base + "profiles", _deadline.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("discard", Request(1) with { PendingGenerationId = status.Pending.GenerationId })).StatusCode);
        Assert.Null((await Status()).Pending);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(Base + "audit", _deadline.Token)).StatusCode);
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task Rebuild_ClientCancellation_PropagatesToProviderAndKeepsActive()
    {
        await Seed();
        _provider.BlockUntilCancelled = true;
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        Task<HttpResponseMessage> response = _client.PostAsJsonAsync(Base + "rebuild", Request(1),
            ServerJsonContext.Default.RagManagementRequest, requestDeadline.Token);
        await _provider.Started.Task.WaitAsync(_deadline.Token);
        requestDeadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await response);
        await _provider.Cancelled.Task.WaitAsync(_deadline.Token);
        var status = await Status();
        Assert.Equal(1, status.ActiveRevision);
        Assert.NotNull(status.Pending);
        Assert.Equal(1, _provider.Calls);
    }

    private async Task Seed()
    {
        var snapshot = RagTextChunker.Chunk("private", "PRIVATE_BODY");
        var profile = _options.Copilot.Docs.RagProfile!.ToProfile();
        await new RagIngestionWriter(_database, "docs", profile).WriteAsync(new([
            new("private", new("documents", "PRIVATE_SOURCE", "v1"), snapshot.ContentHash, "text/plain", SemanticContentModality.Document,
                12, "PRIVATE_SOURCE", profile.Id) { Chunks = snapshot.Chunks }]), (_, _) => ValueTask.FromResult(Vector()), _deadline.Token);
    }
    private Task<HttpResponseMessage> Post(string operation, RagManagementRequest request)
        => _client.PostAsJsonAsync(Base + operation, request, ServerJsonContext.Default.RagManagementRequest, _deadline.Token);
    private async Task<RagManagementStatus> Status() => (await _client.GetFromJsonAsync(Base + "status?stream=docs",
        ServerJsonContext.Default.RagManagementStatus, _deadline.Token))!;
    private static RagManagementRequest Request(long revision) => new() { Stream = "docs", ExpectedRevision = revision, ProfileId = "builtin-v1" };
    private void As(string role) => _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", role);
    private static float[] Vector() { var vector = new float[384]; vector[0] = 1; return vector; }
    private sealed class FixtureProvider : IEmbeddingProvider
    {
        internal bool Fail { get; set; }
        internal bool BlockUntilCancelled { get; set; }
        internal Action? BeforeReturn { get; set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public async ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            if (Fail) throw new ArgumentException("PRIVATE_BODY provider failed");
            Started.TrySetResult();
            if (BlockUntilCancelled)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            BeforeReturn?.Invoke();
            return Vector();
        }
    }
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (_app is not null) { await _app.StopAsync(stop.Token); await _app.DisposeAsync(); }
        _deadline.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
