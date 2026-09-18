using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;

namespace SonnetDB.Core.Tests.FullText;

public sealed class FullTextSettingsDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-fulltext-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SchemaCodec_RoundTripsFullTextSettings()
    {
        var settings = new DocumentFullTextIndexSettings(
            SearchableFields: ["$.body"],
            FilterableFields: ["$.tenant"],
            SortableFields: ["$.rank"],
            Synonyms: new Dictionary<string, string> { ["db"] = "database" },
            StopWords: ["the"],
            TypoPolicy: new DocumentFullTextTypoPolicy(true, 0, 1, 1));
        DocumentCollectionSchema schema = DocumentCollectionSchema.Create(
            "docs",
            fullTextIndexes:
            [
                new DocumentFullTextIndexDefinition("ft", ["$.body", "$.tenant", "$.rank"], Settings: settings),
            ]);
        string path = Path.Combine(_root, "documents.docschema");
        Directory.CreateDirectory(_root);

        DocumentCollectionSchemaCodec.Save(path, [schema]);
        DocumentCollectionSchema loaded = Assert.Single(DocumentCollectionSchemaCodec.Load(path));
        DocumentFullTextIndex index = Assert.Single(loaded.FullTextIndexes);

        Assert.Equal(["$.body"], index.Settings.SearchableFields);
        Assert.Equal(["$.tenant"], index.Settings.FilterableFields);
        Assert.Equal(["$.rank"], index.Settings.SortableFields);
        Assert.Equal("database", index.Settings.Synonyms!["db"]);
        Assert.Equal(["the"], index.Settings.StopWords);
        Assert.Equal(1, index.Settings.TypoPolicy!.LongTokenMaxEdits);
    }

    [Fact]
    public void AnalyzeAndSearch_ApplySynonymAndStopWordSettings()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var settings = new DocumentFullTextIndexSettings(
            Synonyms: new Dictionary<string, string> { ["db"] = "database" },
            StopWords: ["the"]);
        DocumentCollectionSchema schema = DocumentCollectionSchema.Create(
            "docs",
            fullTextIndexes: [new DocumentFullTextIndexDefinition("ft", ["$.body"], Settings: settings)]);
        database.Documents.Create(schema);
        DocumentCollectionStore collection = database.Documents.Open("docs");
        Assert.True(collection.Insert("1", "{\"body\":\"the db\"}").Committed);
        DocumentFullTextIndex index = Assert.Single(schema.FullTextIndexes);

        IReadOnlyList<SonnetDB.FullText.Tokenization.Token> tokens = database.Documents.AnalyzeFullText("docs", "ft", "the db");
        Assert.Equal(["database"], tokens.Select(static token => token.Text));
        Assert.Single(collection.SearchFullText(index, "$.body", "database", 10));
        Assert.Empty(collection.SearchFullText(index, "$.body", "the", 10));
    }

    [Fact]
    public void ExplainAndRebuildCancellation_ExposeDiagnostics()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        DocumentCollectionSchema schema = DocumentCollectionSchema.Create(
            "docs",
            fullTextIndexes: [new DocumentFullTextIndexDefinition("ft", ["$.body"])]);
        database.Documents.Create(schema);
        DocumentCollectionStore collection = database.Documents.Open("docs");
        Assert.True(collection.Insert("1", "{\"body\":\"alpha beta\"}").Committed);

        DocumentFullTextRelevanceExplanation explanation = database.Documents.ExplainFullText("docs", "ft", "$.body", "alpha", "1");
        Assert.Equal("1", explanation.DocumentId);
        Assert.True(explanation.Score > 0);
        Assert.Contains(explanation.Contributions, static item => item.Matched);

        DocumentFullTextIndex index = Assert.Single(schema.FullTextIndexes);
        string indexDirectory = Path.Combine(_root, "standalone");
        DocumentFullTextIndexStore store = DocumentFullTextIndexStore.Open(indexDirectory, index);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Rebuild(
            [new DocumentRow("1", "{\"body\":\"alpha\"}", 1)],
            cancellation.Token));
        Assert.Equal("cancelled", store.RebuildProgress.State);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
