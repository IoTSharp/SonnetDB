using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Copilot;
using SonnetDB.Generations;
using SonnetDB.Hosting;
using SonnetDB.SemanticContent;
using Xunit;

namespace SonnetDB.Tests;

public sealed class CopilotRagMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-copilot-rag-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private string DocsPath => Path.Combine(_root, "docs");
    private string DataPath => Path.Combine(_root, "data");
    private static CopilotRagProfileOptions Profile => new()
    {
        Id = "builtin-contract-v1",
        Provider = "builtin",
        Model = "builtin-hash",
        Revision = "1",
        Dimensions = 384,
        SupportedModalities = [SemanticContentModality.Text, SemanticContentModality.Document],
    };

    public CopilotRagMigrationTests() => Directory.CreateDirectory(DocsPath);

    [Fact]
    public async Task IngestAsync_RagMigration_CanSwitchBackToUntouchedLegacyAndDeleteSnapshot()
    {
        var provider = new FixtureProvider();
        ServerOptions options = Options();
        await using var app = CreateApp(options, provider);
        var ingestor = app.Services.GetRequiredService<DocsIngestor>();
        var search = app.Services.GetRequiredService<DocsSearchService>();
        string file = Path.Combine(DocsPath, "manual.md");
        await File.WriteAllTextAsync(file, "# Manual\nlegacy original", _deadline.Token);
        await ingestor.IngestAsync([DocsPath], cancellationToken: _deadline.Token);
        options.Copilot.Docs.StorageMode = "rag";
        await File.WriteAllTextAsync(file, "# Manual\nnew generation", _deadline.Token);
        var first = await ingestor.IngestAsync([DocsPath], cancellationToken: _deadline.Token);
        Assert.Equal(1, first.IndexedFiles);
        Assert.Contains("new generation", Assert.Single(await search.SearchAsync("manual", 5, _deadline.Token)).Content);
        var repeat = await ingestor.IngestAsync([DocsPath], cancellationToken: _deadline.Token);
        Assert.Equal(0, repeat.WrittenChunks);
        Assert.Equal(1, repeat.SkippedFiles);
        Assert.Equal(1, (await ingestor.GetIndexStateAsync(_deadline.Token)).IndexedFiles);
        options.Copilot.Docs.StorageMode = "legacy";
        Assert.Contains("legacy original", Assert.Single(await search.SearchAsync("manual", 5, _deadline.Token)).Content);
        options.Copilot.Docs.StorageMode = "rag";
        File.Delete(file);
        var deleted = await ingestor.IngestAsync([DocsPath], cancellationToken: _deadline.Token);
        Assert.Equal(1, deleted.DeletedFiles);
        Assert.Empty(await search.SearchAsync("manual", 5, _deadline.Token));
        options.Copilot.Docs.StorageMode = "legacy";
        Assert.Single(await search.SearchAsync("manual", 5, _deadline.Token));
    }

    [Fact]
    public async Task IngestAsync_ProviderFailure_KeepsOldGenerationAndResumesAfterReopen()
    {
        string file = Path.Combine(DocsPath, "manual.md");
        ServerOptions options = Options();
        options.Copilot.Docs.StorageMode = "rag";
        var provider = new FixtureProvider();
        await using (var app = CreateApp(options, provider))
        {
            var ingestor = app.Services.GetRequiredService<DocsIngestor>();
            await File.WriteAllTextAsync(file, "old content", _deadline.Token);
            await ingestor.IngestAsync([DocsPath], cancellationToken: _deadline.Token);
            await File.WriteAllTextAsync(file, "failed content", _deadline.Token);
            provider.Fail = true;
            await Assert.ThrowsAsync<IOException>(() => ingestor.IngestAsync([DocsPath], cancellationToken: _deadline.Token));
            provider.Fail = false;
            Assert.Contains("old content", Assert.Single(await app.Services.GetRequiredService<DocsSearchService>()
                .SearchAsync("old", 5, _deadline.Token)).Content);
        }
        await using var reopened = CreateApp(options, provider);
        await reopened.Services.GetRequiredService<DocsIngestor>().IngestAsync([DocsPath], cancellationToken: _deadline.Token);
        Assert.Contains("failed content", Assert.Single(await reopened.Services.GetRequiredService<DocsSearchService>()
            .SearchAsync("new", 5, _deadline.Token)).Content);
    }

    [Fact]
    public async Task SearchAsync_UninitializedOrMismatchedProfile_FailsWithoutFallback()
    {
        var options = Options();
        options.Copilot.Docs.StorageMode = "rag";
        var provider = new FixtureProvider();
        await using var app = CreateApp(options, provider);
        var search = app.Services.GetRequiredService<DocsSearchService>();
        await Assert.ThrowsAsync<DatabaseGenerationException>(() => search.SearchAsync("query", 5, _deadline.Token));
        Assert.Equal(0, provider.Calls);
        await File.WriteAllTextAsync(Path.Combine(DocsPath, "manual.md"), "alpha", _deadline.Token);
        await app.Services.GetRequiredService<DocsIngestor>().IngestAsync([DocsPath], cancellationToken: _deadline.Token);
        options.Copilot.Docs.RagProfile!.Id = "changed-profile";
        int calls = provider.Calls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync("query", 5, _deadline.Token));
        Assert.Equal(calls, provider.Calls);
    }

    [Fact]
    public async Task IngestAsync_CancelledMigration_PreservesLegacyAndPublishedRag()
    {
        var options = Options();
        var provider = new FixtureProvider();
        await using var app = CreateApp(options, provider);
        var ingestor = app.Services.GetRequiredService<DocsIngestor>();
        string file = Path.Combine(DocsPath, "manual.md");
        await File.WriteAllTextAsync(file, "old content", _deadline.Token);
        await ingestor.IngestAsync([DocsPath], cancellationToken: _deadline.Token);
        options.Copilot.Docs.StorageMode = "rag";
        await ingestor.IngestAsync([DocsPath], cancellationToken: _deadline.Token);
        await File.WriteAllTextAsync(file, "pending content", _deadline.Token);
        provider.Cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingestor.IngestAsync([DocsPath], cancellationToken: _deadline.Token));
        provider.Cancel = false;
        Assert.Contains("old content", Assert.Single(await app.Services.GetRequiredService<DocsSearchService>()
            .SearchAsync("query", 5, _deadline.Token)).Content);
        options.Copilot.Docs.StorageMode = "legacy";
        Assert.Contains("old content", Assert.Single(await app.Services.GetRequiredService<DocsSearchService>()
            .SearchAsync("query", 5, _deadline.Token)).Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect, "private upstream error")]
    [InlineData(HttpStatusCode.OK, "{\"data\":[null]}")]
    [InlineData(HttpStatusCode.OK, "{\"model\":\"wrong\",\"data\":[{\"index\":0,\"embedding\":[1,0]}]}")]
    [InlineData(HttpStatusCode.OK, "{\"data\":[{\"index\":1,\"embedding\":[1,0]}]}")]
    public async Task EmbedGovernedAsync_UntrustedResponse_FailsWithSanitizedError(HttpStatusCode status, string payload)
    {
        var factory = new FixtureHttpFactory(status, payload);
        var provider = new OpenAICompatibleEmbeddingProvider(new()
        {
            Provider = "openai",
            Endpoint = "https://embedding.example/v1/",
            Model = "fixture",
            ApiKey = "secret",
        }, factory);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EmbedGovernedAsync("private input", _deadline.Token).AsTask());
        Assert.Equal("copilot-rag-embedding", factory.Name);
        Assert.DoesNotContain("private", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void Bind_RagProfile_PreservesExplicitModelAndEgressContract()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SonnetDBServer:Copilot:Docs:StorageMode"] = "rag",
            ["SonnetDBServer:Copilot:Docs:RagProfile:Id"] = "online-v1",
            ["SonnetDBServer:Copilot:Docs:RagProfile:Provider"] = "openai",
            ["SonnetDBServer:Copilot:Docs:RagProfile:Model"] = "fixture",
            ["SonnetDBServer:Copilot:Docs:RagProfile:Revision"] = "1",
            ["SonnetDBServer:Copilot:Docs:RagProfile:Dimensions"] = "2",
            ["SonnetDBServer:Copilot:Docs:RagProfile:SupportedModalities:0"] = "Text",
            ["SonnetDBServer:Copilot:Docs:RagProfile:SupportedModalities:1"] = "Document",
            ["SonnetDBServer:Copilot:Docs:RagProfile:DataEgressPolicy:Mode"] = "ConfiguredProvider",
            ["SonnetDBServer:Copilot:Docs:RagProfile:DataEgressPolicy:Target"] = "https://example.com/v1/",
        }).Build();
        using var configurationLifetime = configuration as IDisposable;
        var options = ServerOptionsBinder.Bind(configuration);
        Assert.Equal("rag", options.Copilot.Docs.StorageMode);
        var profile = options.Copilot.Docs.RagProfile!.ToProfile();
        Assert.Equal("fixture", profile.Model);
        Assert.Equal(2, profile.Dimensions);
        Assert.Equal(2, profile.SupportedModalities.Count);
        Assert.Equal(SemanticDataEgressMode.ConfiguredProvider, profile.DataEgressPolicy.Mode);
        Assert.Equal("https://example.com/v1/", profile.DataEgressPolicy.Target);
    }

    private ServerOptions Options()
    {
        var options = new ServerOptions { DataRoot = DataPath, AutoLoadExistingDatabases = true };
        options.Copilot.Docs.Roots = [DocsPath];
        options.Copilot.Docs.RagProfile = Profile;
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Embedding.Provider = "builtin";
        return options;
    }

    private WebApplication CreateApp(ServerOptions options, FixtureProvider provider)
    {
        string contentRoot = Path.Combine(_root, "host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"),
            System.Text.Json.JsonSerializer.Serialize(new { SonnetDBServer = options }));
        return Program.BuildApp(["--contentRoot", contentRoot, "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0"], services =>
        {
            services.AddSingleton<IEmbeddingProvider>(provider);
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        });
    }

    private sealed class FixtureProvider : IEmbeddingProvider
    {
        internal bool Fail { get; set; }
        internal bool Cancel { get; set; }
        internal int Calls { get; private set; }
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Fail) throw new IOException("test failure");
            if (Cancel) throw new OperationCanceledException("test cancellation");
            var vector = new float[384]; vector[0] = 1;
            return ValueTask.FromResult(vector);
        }
    }

    private sealed class FixtureHttpFactory(HttpStatusCode status, string payload) : IHttpClientFactory
    {
        internal string? Name { get; private set; }
        public HttpClient CreateClient(string name) { Name = name; return new(new ResponseHandler(status, payload)); }
    }

    private sealed class ResponseHandler(HttpStatusCode status, string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(payload) });
    }

    public void Dispose()
    {
        _deadline.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
