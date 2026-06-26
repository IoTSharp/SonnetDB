using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Catalog;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Hosting;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests;

public sealed class DocsIngestorTests
{
    [Fact]
    public async Task IngestAsync_WithReservedCharactersInMarkdownHeading_PreservesExactSectionForNewSchema()
    {
        var dataRoot = CreateTempDirectory("sndb-docs-ingest-data-");
        var docsRoot = CreateTempDirectory("sndb-docs-ingest-docs-");

        try
        {
            File.WriteAllText(
                Path.Combine(docsRoot, "bulk-ingest.md"),
                """
                # Bulk Ingest

                ### `onerror=skip`

                当坏行出现时可以选择跳过。
                """);

            await using var app = CreateApp(dataRoot, docsRoot);
            var ingestor = app.Services.GetRequiredService<DocsIngestor>();
            var search = app.Services.GetRequiredService<DocsSearchService>();

            var stats = await ingestor.IngestAsync([docsRoot], force: true, dryRun: false);

            Assert.Equal(1, stats.IndexedFiles);
            Assert.Equal(1, stats.WrittenChunks);

            var schema = ingestor.GetKnowledgeDb().Measurements.TryGet(DocsIngestor.DocsMeasurementName);
            Assert.NotNull(schema);
            Assert.Equal(MeasurementColumnRole.Tag, schema!.TryGetColumn("source")!.Role);
            Assert.Equal(MeasurementColumnRole.Field, schema.TryGetColumn("section")!.Role);
            Assert.Equal(MeasurementColumnRole.Field, schema.TryGetColumn("title")!.Role);

            var hits = await search.SearchAsync("skip", 5);
            var hit = Assert.Single(hits);
            Assert.Equal("Bulk Ingest", hit.Title);
            Assert.Equal("Bulk Ingest / `onerror=skip`", hit.Section);
        }
        finally
        {
            DeleteDirectory(docsRoot);
            DeleteDirectory(dataRoot);
        }
    }

    [Fact]
    public async Task IngestAsync_WithLegacyDocsSchema_DoesNotThrowOnReservedCharacters()
    {
        var dataRoot = CreateTempDirectory("sndb-docs-legacy-data-");
        var docsRoot = CreateTempDirectory("sndb-docs-legacy-docs-");

        try
        {
            File.WriteAllText(
                Path.Combine(docsRoot, "bulk-ingest.md"),
                """
                # Bulk Ingest

                ### `onerror=skip`

                旧 schema 下也不应该因为 section 标题中的等号而失败。
                """);

            await using var app = CreateApp(dataRoot, docsRoot);
            var registry = app.Services.GetRequiredService<TsdbRegistry>();
            registry.TryCreate(DocsIngestor.CopilotDatabaseName, out var database);
            SqlExecutor.ExecuteStatement(
                database,
                new CreateMeasurementStatement(
                    DocsIngestor.DocsMeasurementName,
                    [
                        new ColumnDefinition("source", ColumnKind.Tag, SqlDataType.String),
                        new ColumnDefinition("section", ColumnKind.Tag, SqlDataType.String),
                        new ColumnDefinition("title", ColumnKind.Tag, SqlDataType.String),
                        new ColumnDefinition("content", ColumnKind.Field, SqlDataType.String),
                        new ColumnDefinition("embedding", ColumnKind.Field, SqlDataType.Vector, DocsIngestor.ExpectedEmbeddingDimensions),
                    ]));

            var ingestor = app.Services.GetRequiredService<DocsIngestor>();
            var search = app.Services.GetRequiredService<DocsSearchService>();

            var exception = await Record.ExceptionAsync(() => ingestor.IngestAsync([docsRoot], force: true, dryRun: false));
            Assert.Null(exception);

            var hits = await search.SearchAsync("skip", 5);
            var hit = Assert.Single(hits);
            Assert.Contains("onerror", hit.Section, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(docsRoot);
            DeleteDirectory(dataRoot);
        }
    }

    private static WebApplication CreateApp(string dataRoot, string docsRoot)
    {
        var options = new ServerOptions
        {
            DataRoot = dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
        };
        options.Copilot.Enabled = true;
        options.Copilot.Docs.Roots = [docsRoot];
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Embedding.Provider = "builtin";
        options.Copilot.Chat.Provider = "openai";
        options.Copilot.Chat.Endpoint = "https://chat.example/v1/";
        options.Copilot.Chat.ApiKey = "chat-key";
        options.Copilot.Chat.Model = "chat-model";

        return TestServerHost.Build(
            options,
            services =>
            {
                services.AddSingleton<IEmbeddingProvider, FakeEmbeddingProvider>();
                services.AddSingleton<IChatProvider, FakeChatProvider>();
            });
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            var embedding = new float[DocsIngestor.ExpectedEmbeddingDimensions];
            embedding[0] = 1.0f;
            embedding[1] = text.Length;
            embedding[2] = text.Contains("skip", StringComparison.OrdinalIgnoreCase) ? 3.0f : 0.5f;
            return ValueTask.FromResult(embedding);
        }
    }

    private sealed class FakeChatProvider : IChatProvider
    {
        public ValueTask<string> CompleteAsync(
            IReadOnlyList<AiMessage> messages,
            string? modelOverride = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult("unused");
    }
}
