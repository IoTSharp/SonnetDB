using System.Net;
using System.Text;
using System.Text.Json;
using SonnetDB.Cli;
using SonnetDB.Engine;
using SonnetDB.SemanticContent;
using Xunit;

namespace SonnetDB.Core.Tests.Cli;

public sealed class RagOnlineCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-rag-online-" + Guid.NewGuid().ToString("N"));
    private readonly string _keyVariable = "SNDB_TEST_KEY_" + Guid.NewGuid().ToString("N");
    private string DatabasePath => Path.Combine(_root, "database");
    private string AuditPath => Path.Combine(_root, "audit.jsonl");
    private static EmbeddingProfile Profile => new("online-contract-v1", "openai", "fixture-only", "1", 2,
        supportedModalities: [SemanticContentModality.Text],
        dataEgressPolicy: new(SemanticDataEgressMode.ConfiguredProvider, "https://embedding.example/v1/"));

    public RagOnlineCommandTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(_keyVariable, "secret-test-key");
    }

    [Fact]
    public void Ingest_OnlineProfile_PublishesAndAuditsWithoutSecrets()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("https://embedding.example/v1/embeddings", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("secret-test-key", request.Headers.Authorization.Parameter);
            using var requestJson = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("float", requestJson.RootElement.GetProperty("encoding_format").GetString());
            Assert.Equal("private input", requestJson.RootElement.GetProperty("input").GetString());
            using var audit = new StreamReader(new FileStream(AuditPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            Assert.Contains("started", await audit.ReadToEndAsync(token));
            return Success();
        });
        Assert.Equal(0, Run(handler, Bundle("private input")));
        Assert.Equal(1, handler.Calls);
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = DatabasePath });
        using var active = database.Generations.AcquireActive("docs");
        Assert.Equal(1, active.Generation.Revision);
        string audit = File.ReadAllText(AuditPath);
        Assert.Contains("succeeded", audit);
        Assert.DoesNotContain("private input", audit);
        Assert.DoesNotContain("secret-test-key", audit);
    }

    [Fact]
    public void Ingest_DryRun_ValidatesPolicyWithoutNetworkDatabaseOrAudit()
    {
        Environment.SetEnvironmentVariable(_keyVariable, null);
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not send"));
        Assert.Equal(0, Run(handler, Bundle("alpha"), dryRun: true));
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(DatabasePath));
        Assert.False(File.Exists(AuditPath));
    }

    [Theory]
    [InlineData("https://other.example/v1/")]
    [InlineData("https://embedding.example/other/")]
    [InlineData("https://user@embedding.example/v1/")]
    public void Ingest_MismatchedPolicy_RejectsBeforeOpeningDatabase(string target)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Success()));
        var bundle = Bundle("alpha") with { Profile = Profile with { DataEgressPolicy = new(SemanticDataEgressMode.ConfiguredProvider, target) } };
        Assert.Throws<CliUsageException>(() => Run(handler, bundle));
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(DatabasePath));
    }

    [Theory]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[{\"index\":2,\"embedding\":[1,0]}]}")]
    [InlineData("{\"model\":\"other\",\"data\":[{\"index\":0,\"embedding\":[1,0]}]}")]
    public void Ingest_InvalidProtocol_DoesNotRetryOrPublish(string payload)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) }));
        Assert.Throws<InvalidOperationException>(() => Run(handler, Bundle("alpha")));
        Assert.Equal(1, handler.Calls);
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = DatabasePath });
        Assert.Throws<SonnetDB.Generations.DatabaseGenerationException>(() => database.Generations.AcquireActive("docs"));
    }

    [Fact]
    public void Ingest_ProviderFailure_PreservesOldGenerationAndResumesFrozenTask()
    {
        using (var handler = new Handler((_, _) => Task.FromResult(Success())))
            Assert.Equal(0, Run(handler, Bundle("old text")));
        using (var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("private failure") })))
        {
            var error = Assert.Throws<HttpRequestException>(() => Run(handler, Bundle("new text")));
            Assert.DoesNotContain("private failure", error.Message);
            Assert.Equal(3, handler.Calls);
        }
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = DatabasePath }))
        using (var active = database.Generations.AcquireActive("docs"))
            Assert.Equal(1, active.Generation.Revision);
        using (var handler = new Handler((_, _) => Task.FromResult(Success())))
            Assert.Equal(0, Run(handler, new() { Profile = Profile }, resume: true));
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = DatabasePath });
        using var newActive = reopened.Generations.AcquireActive("docs");
        Assert.Equal(2, newActive.Generation.Revision);
    }

    [Fact]
    public void Ingest_CancelledHttpCall_LeavesTaskResumable()
    {
        using (var handler = new Handler(async (_, token) => { await Task.Delay(TimeSpan.FromSeconds(5), token); return Success(); }))
            Assert.ThrowsAny<OperationCanceledException>(() => Run(handler, Bundle("alpha"), timeout: "1"));
        using var retry = new Handler((_, _) => Task.FromResult(Success()));
        Assert.Equal(0, Run(retry, new() { Profile = Profile }, resume: true));
        Assert.Contains("cancelled", File.ReadAllText(AuditPath));
    }

    private int Run(Handler handler, RagCliBundle bundle, bool dryRun = false, bool resume = false, string timeout = "30")
    {
        string input = Path.Combine(_root, "bundle.json");
        File.WriteAllText(input, JsonSerializer.Serialize(bundle, RagCliJsonContext.Default.RagCliBundle));
        var args = new List<string> { "rag", resume ? "resume" : "ingest", "--input", input, "--path", DatabasePath,
            "--stream", "docs", "--endpoint", "https://embedding.example/v1/", "--api-key-env", _keyVariable,
            "--allow-egress", "--audit", AuditPath, "--timeout", timeout };
        if (!resume) args.Add(dryRun ? "--dry-run" : "--replace-snapshot");
        return new RagCommandRunner(new StringWriter(), () => handler).Run(args);
    }

    private static RagCliBundle Bundle(string text)
    {
        var chunked = RagTextChunker.Chunk("manual", text);
        return new()
        {
            Profile = Profile,
            Snapshot = new([new("manual", new("manuals", "manual", chunked.ContentHash), chunked.ContentHash,
                "text/plain", SemanticContentModality.Text, Encoding.UTF8.GetByteCount(text), embeddingProfileId: Profile.Id) { Chunks = chunked.Chunks }]),
        };
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"model\":\"fixture-only\",\"data\":[{\"index\":0,\"embedding\":[1,0]}]}") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_keyVariable, null);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
