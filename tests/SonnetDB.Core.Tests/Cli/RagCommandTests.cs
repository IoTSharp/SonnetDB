using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Cli;
using SonnetDB.Engine;
using SonnetDB.Generations;
using SonnetDB.SemanticContent;
using Xunit;

namespace SonnetDB.Core.Tests.Cli;

public sealed class RagCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-rag-cli-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "database");
    private static EmbeddingProfile Profile => new("offline-test-v1", "fixture", "unit-test-only", "1", 2,
        supportedModalities: [SemanticContentModality.Text]);

    public RagCommandTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Ingest_DryRun_ValidatesWithoutCreatingDatabase()
    {
        string input = WriteBundle(Bundle("alpha"));
        var result = Run("rag", "ingest", "--input", input, "--path", DatabasePath, "--stream", "docs", "--dry-run");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.False(Directory.Exists(DatabasePath));
        using var report = JsonDocument.Parse(result.Output);
        Assert.Equal("validated", report.RootElement.GetProperty("status").GetString());
        Assert.True(report.RootElement.GetProperty("targetNotRead").GetBoolean());
        Assert.Equal(1, report.RootElement.GetProperty("validatedChunks").GetInt32());
    }

    [Fact]
    public void Ingest_CompleteSnapshot_PublishesSearchableDataAndIdempotentReplay()
    {
        string input = WriteBundle(Bundle("alpha"));
        var result = Ingest(input);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = DatabasePath }))
        using (var lease = database.Generations.AcquireActive("docs"))
        {
            var collection = database.Documents.Open(lease.GetRequiredResource(
                RagIngestionWriter.ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection).Name);
            Assert.Single(collection.SearchFullText(collection.Schema.TryGetFullTextIndex(RagIngestionWriter.FullTextIndexName)!, "$.text", "alpha", 10));
            Assert.Equal(1, lease.Generation.Revision);
        }
        var replay = Ingest(input);
        Assert.Equal(0, replay.Code);
        using var report = JsonDocument.Parse(replay.Output);
        Assert.Equal(1, report.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(0, report.RootElement.GetProperty("embeddedChunks").GetInt32());
    }

    [Fact]
    public void Ingest_WithoutReplaceFlag_RejectsBeforeOpeningDatabase()
    {
        string input = WriteBundle(Bundle("alpha"));
        var result = Run("rag", "ingest", "--input", input, "--path", DatabasePath, "--stream", "docs");
        Assert.Equal(ExitCodes.InvalidArguments, result.Code);
        Assert.Contains("--replace-snapshot", result.Error);
        Assert.False(Directory.Exists(DatabasePath));
    }

    [Fact]
    public void Ingest_VectorBoundToDifferentText_RejectsBeforeOpeningDatabase()
    {
        RagCliBundle bundle = Bundle("alpha");
        string input = WriteBundle(bundle with { Vectors = [bundle.Vectors[0] with { TextSha256 = new string('0', 64) }] });
        var result = Ingest(input);
        Assert.NotEqual(0, result.Code);
        Assert.Contains("SHA-256", result.Error);
        Assert.False(Directory.Exists(DatabasePath));
    }

    [Fact]
    public void Ingest_DryRunWithUnknownManifestVersion_RejectsBeforeOpeningDatabase()
    {
        RagCliBundle bundle = Bundle("alpha");
        string input = WriteBundle(bundle with
        {
            Snapshot = new([bundle.Snapshot!.Manifests[0] with { SchemaVersion = 2 }]),
        });
        var result = Run("rag", "ingest", "--input", input, "--path", DatabasePath, "--stream", "docs", "--dry-run");
        Assert.NotEqual(0, result.Code);
        Assert.False(Directory.Exists(DatabasePath));
    }

    [Fact]
    public void Ingest_DryRunWithInvalidNormalizedVector_RejectsBeforeOpeningDatabase()
    {
        RagCliBundle bundle = Bundle("alpha");
        string input = WriteBundle(bundle with
        {
            Profile = bundle.Profile with { Normalization = EmbeddingNormalization.L2 },
            Vectors = [bundle.Vectors[0] with { Values = [2, 0] }],
        });
        var result = Run("rag", "ingest", "--input", input, "--path", DatabasePath, "--stream", "docs", "--dry-run");
        Assert.NotEqual(0, result.Code);
        Assert.Contains("L2", result.Error);
        Assert.False(Directory.Exists(DatabasePath));
    }

    [Fact]
    public async Task Resume_AfterWriterFailure_UsesPersistedSnapshotAndPrecomputedVectors()
    {
        RagCliBundle bundle = Bundle("alpha", "bravo");
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = DatabasePath }))
        {
            var writer = new RagIngestionWriter(database, "docs", Profile,
                new RagIngestionWriterOptions { MaxEmbeddingAttempts = 1, MaxDuration = TimeSpan.FromSeconds(30) });
            int calls = 0;
            await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(bundle.Snapshot!, (_, _) =>
            {
                if (++calls == 2) throw new IOException("test interruption");
                return ValueTask.FromResult(new float[] { 1, 0 });
            }).AsTask());
        }
        // Resume 不依赖输入 snapshot，也无需为已完成的 alpha 提供向量。
        string input = WriteBundle(bundle with { Snapshot = null, Vectors = [bundle.Vectors[1]] });
        var result = Run("rag", "resume", "--input", input, "--path", DatabasePath, "--stream", "docs");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var report = JsonDocument.Parse(result.Output);
        Assert.Equal(1, report.RootElement.GetProperty("embeddedChunks").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("reusedChunks").GetInt32());
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = DatabasePath });
        using var lease = reopened.Generations.AcquireActive("docs");
        var collection = reopened.Documents.Open(lease.GetRequiredResource(
            RagIngestionWriter.ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection).Name);
        Assert.Single(collection.SearchFullText(collection.Schema.TryGetFullTextIndex(RagIngestionWriter.FullTextIndexName)!, "$.text", "alpha", 10));
        Assert.Single(collection.SearchFullText(collection.Schema.TryGetFullTextIndex(RagIngestionWriter.FullTextIndexName)!, "$.text", "bravo", 10));
    }

    [Fact]
    public void Ingest_EmptyCompleteSnapshot_RemovesOldSearchResults()
    {
        Assert.Equal(0, Ingest(WriteBundle(Bundle("alpha"))).Code);
        Assert.Equal(0, Ingest(WriteBundle(Bundle())).Code);
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = DatabasePath });
        using var lease = database.Generations.AcquireActive("docs");
        var collection = database.Documents.Open(lease.GetRequiredResource(
            RagIngestionWriter.ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection).Name);
        Assert.Empty(collection.SearchFullText(collection.Schema.TryGetFullTextIndex(RagIngestionWriter.FullTextIndexName)!, "$.text", "alpha", 10));
        Assert.Equal(2, lease.Generation.Revision);
    }

    [Theory]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "601")]
    [InlineData("--unknown", "value")]
    public void Ingest_InvalidOptions_RejectsBeforeReadingInput(string name, string value)
    {
        var result = Run("rag", "ingest", "--input", "missing", "--path", DatabasePath, "--stream", "docs", "--dry-run", name, value);
        Assert.Equal(ExitCodes.InvalidArguments, result.Code);
        Assert.False(Directory.Exists(DatabasePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ingest_OverlongStream_RejectsBeforeCreatingDatabase(bool dryRun)
    {
        string input = WriteBundle(Bundle("alpha"));
        var result = Run("rag", "ingest", "--input", input, "--path", DatabasePath,
            "--stream", new string('é', 2049), dryRun ? "--dry-run" : "--replace-snapshot");
        Assert.Equal(ExitCodes.InvalidArguments, result.Code);
        Assert.Contains("4096", result.Error);
        Assert.False(Directory.Exists(DatabasePath));
    }

    private (int Code, string Output, string Error) Ingest(string input)
        => Run("rag", "ingest", "--input", input, "--path", DatabasePath, "--stream", "docs", "--replace-snapshot");

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = new CliApplication(new StringReader(string.Empty), output, error).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    private string WriteBundle(RagCliBundle bundle)
    {
        string path = Path.Combine(_root, "bundle.json");
        File.WriteAllText(path, JsonSerializer.Serialize(bundle, RagCliJsonContext.Default.RagCliBundle));
        return path;
    }

    private static RagCliBundle Bundle(params string[] texts)
    {
        var manifests = new List<SemanticContentManifest>();
        var vectors = new List<RagCliVector>();
        foreach (string text in texts)
        {
            RagTextSnapshot chunked = RagTextChunker.Chunk(text, text);
            manifests.Add(new(text, new("source", text, chunked.ContentHash), chunked.ContentHash, "text/plain", SemanticContentModality.Text,
                Encoding.UTF8.GetByteCount(text), embeddingProfileId: Profile.Id)
            { Chunks = chunked.Chunks });
            foreach (SemanticContentChunk chunk in chunked.Chunks)
                vectors.Add(new(chunk.Id, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Text))), [1, 0]));
        }
        return new() { Profile = Profile, Snapshot = new(manifests), Vectors = vectors };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
