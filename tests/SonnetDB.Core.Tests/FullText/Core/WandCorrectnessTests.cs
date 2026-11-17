using SonnetDB.Documents;
using SonnetDB.FullText;
using SonnetDB.FullText.Index;
using SonnetDB.FullText.Query;
using SonnetDB.FullText.Scoring;
using SonnetDB.FullText.Storage;
using SonnetDB.FullText.Tokenizers.Unicode;
using Xunit;

namespace SonnetDB.Core.Tests.FullText;

/// <summary>
/// 覆盖受限 Block-Max WAND、Top-K 边界和全文预算失败合同。
/// </summary>
public sealed class WandCorrectnessTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-fulltext-wand-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Wand_matches_independent_full_scoring_reference_for_multiple_top_k_values()
    {
        Document[] documents =
        [
            new Document(new DocumentId("doc-00")).Set("body", "alpha beta"),
            new Document(new DocumentId("doc-01")).Set("body", "alpha alpha gamma"),
            new Document(new DocumentId("doc-02")).Set("body", "beta gamma"),
            new Document(new DocumentId("doc-03")).Set("body", "alpha delta epsilon"),
            new Document(new DocumentId("doc-04")).Set("body", "gamma gamma gamma"),
            new Document(new DocumentId("doc-05")).Set("body", "zeta"),
        ];
        OrQuery query = new(
        [
            new TermQuery("body", "alpha"),
            new TermQuery("body", "beta"),
            new TermQuery("body", "gamma"),
        ]);

        PersistentFullTextIndex persistent = OpenPersistent();
        persistent.IndexMany(documents);

        var reference = new InMemoryFullTextIndex(new UnicodeTokenizer());
        foreach (Document document in documents)
            reference.Index(document);

        foreach (int topK in new[] { 1, 2, 3, 100 })
        {
            IReadOnlyList<SearchHit> actual = persistent.Search(query, topK);
            IReadOnlyList<SearchHit> expected = reference.Search(query, topK);

            Assert.Equal(expected.Select(static hit => hit.DocumentId.Value), actual.Select(static hit => hit.DocumentId.Value));
            Assert.Equal(expected.Select(static hit => hit.Score), actual.Select(static hit => hit.Score));
        }
    }

    [Fact]
    public void Wand_preserves_document_id_order_for_equal_scores_and_top_k_edges()
    {
        PersistentFullTextIndex persistent = OpenPersistent();
        persistent.IndexMany(
        [
            new Document(new DocumentId("z-doc")).Set("body", "alpha"),
            new Document(new DocumentId("a-doc")).Set("body", "alpha"),
            new Document(new DocumentId("missing-doc")).Set("body", "omega"),
        ]);

        OrQuery query = new([new TermQuery("body", "alpha"), new TermQuery("body", "missing")]);

        Assert.Empty(persistent.Search(query, topK: 0));

        IReadOnlyList<SearchHit> first = persistent.Search(query, topK: 1);
        Assert.Single(first);
        Assert.Equal("a-doc", first[0].DocumentId.Value);

        IReadOnlyList<SearchHit> all = persistent.Search(query, topK: 100);
        Assert.Equal(new[] { "a-doc", "z-doc" }, all.Select(static hit => hit.DocumentId.Value));
    }

    [Fact]
    public void Filtered_search_returns_no_partial_hits_when_posting_budget_is_exceeded()
    {
        DocumentFullTextIndexStore store = OpenDocumentStore();
        var rows = Enumerable.Range(0, 8)
            .Select(static ordinal => new DocumentRow(
                $"doc-{ordinal:D2}",
                "{\"body\":\"alpha beta\"}",
                Version: 1))
            .ToArray();
        store.UpsertMany(rows);

        DocumentFullTextFilteredSearchResult result = store.SearchFiltered(
            "$.body",
            "alpha",
            topK: 5,
            rows.Select(static row => row.Id).ToHashSet(StringComparer.Ordinal),
            maxPostingVisits: 1,
            CancellationToken.None);

        Assert.True(result.PostingBudgetExceeded);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Filtered_wand_respects_allowed_ids_and_returns_stable_top_k()
    {
        DocumentFullTextIndexStore store = OpenDocumentStore();
        store.UpsertMany(
        [
            new DocumentRow("allowed-alpha", "{\"body\":\"alpha\"}", Version: 1),
            new DocumentRow("allowed-beta", "{\"body\":\"beta\"}", Version: 1),
            new DocumentRow("denied-alpha", "{\"body\":\"alpha\"}", Version: 1),
        ]);

        DocumentFullTextFilteredSearchResult result = store.SearchFiltered(
            "$.body",
            "alpha beta",
            topK: 10,
            new HashSet<string>(StringComparer.Ordinal) { "allowed-alpha", "allowed-beta", "denied-alpha" },
            maxPostingVisits: 100,
            CancellationToken.None,
            FullTextSearchMode.Exact,
            FullTextQueryKind.Any);

        Assert.False(result.PostingBudgetExceeded);
        Assert.Equal(3, result.Hits.Count);
        Assert.Equal(
            new[] { "allowed-beta", "allowed-alpha", "denied-alpha" },
            result.Hits.Select(static hit => hit.DocumentId));

        DocumentFullTextFilteredSearchResult restricted = store.SearchFiltered(
            "$.body",
            "alpha beta",
            topK: 10,
            new HashSet<string>(StringComparer.Ordinal) { "allowed-beta" },
            maxPostingVisits: 100,
            CancellationToken.None,
            FullTextSearchMode.Exact,
            FullTextQueryKind.Any);

        Assert.Equal(new[] { "allowed-beta" }, restricted.Hits.Select(static hit => hit.DocumentId));
    }

    [Fact]
    public void Filtered_search_honors_pre_cancel_without_returning_partial_results()
    {
        DocumentFullTextIndexStore store = OpenDocumentStore();
        store.Upsert(new DocumentRow("doc-1", "{\"body\":\"alpha\"}", Version: 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => store.SearchFiltered(
            "$.body",
            "alpha",
            topK: 5,
            new HashSet<string>(StringComparer.Ordinal) { "doc-1" },
            maxPostingVisits: 100,
            cancellation.Token));
    }

    [Fact]
    public void Invalid_bm25_parameters_fall_back_to_full_scoring_without_pruning()
    {
        Document[] documents =
        [
            new Document(new DocumentId("doc-a")).Set("body", "alpha beta"),
            new Document(new DocumentId("doc-b")).Set("body", "alpha alpha"),
            new Document(new DocumentId("doc-c")).Set("body", "beta"),
        ];
        OrQuery query = new([new TermQuery("body", "alpha"), new TermQuery("body", "beta")]);
        Bm25Parameters invalid = new(1.2, 2.0);

        PersistentFullTextIndex persistent = PersistentFullTextIndex.Open(
            Path.Combine(_directory, "invalid-bm25"),
            new UnicodeTokenizer(),
            invalid,
            new PersistentIndexOptions { EnableBackgroundMerge = false });
        persistent.IndexMany(documents);

        var reference = new InMemoryFullTextIndex(new UnicodeTokenizer(), invalid);
        foreach (Document document in documents)
            reference.Index(document);

        IReadOnlyList<SearchHit> actual = persistent.Search(query, topK: 3);
        IReadOnlyList<SearchHit> expected = reference.Search(query, topK: 3);

        Assert.Equal(expected.Select(static hit => hit.DocumentId.Value), actual.Select(static hit => hit.DocumentId.Value));
        for (int index = 0; index < expected.Count; index++)
            Assert.Equal(expected[index].Score, actual[index].Score, precision: 12);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private PersistentFullTextIndex OpenPersistent()
        => PersistentFullTextIndex.Open(
            Path.Combine(_directory, "persistent"),
            new UnicodeTokenizer(),
            options: new PersistentIndexOptions { EnableBackgroundMerge = false });

    private DocumentFullTextIndexStore OpenDocumentStore()
        => DocumentFullTextIndexStore.Open(
            Path.Combine(_directory, "documents"),
            new DocumentFullTextIndex(
                "ft_docs",
                ["$.body"],
                "unicode",
                DateTime.UtcNow.Ticks));
}
