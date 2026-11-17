using SonnetDB.Documents;
using SonnetDB.FullText;
using SonnetDB.FullText.Index;
using SonnetDB.FullText.Query;
using SonnetDB.FullText.Scoring;
using SonnetDB.FullText.Storage;
using SonnetDB.FullText.Tokenizers.Unicode;
using Xunit;
using FullTextQuery = SonnetDB.FullText.Query.Query;

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

    [Fact]
    public void Filtered_wand_and_fallback_match_oracle_after_random_multi_segment_updates_and_tombstones()
    {
        const int documentCount = 48;
        string[] vocabulary = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta"];
        var random = new Random(0x5EED_2026);
        var persistent = OpenPersistent();
        var reference = new InMemoryFullTextIndex(new UnicodeTokenizer());
        var liveDocuments = new Dictionary<string, Document>(StringComparer.Ordinal);
        var allDocumentIds = new HashSet<string>(StringComparer.Ordinal);
        var staleDocumentIds = new HashSet<string>(StringComparer.Ordinal);

        for (int batchStart = 0; batchStart < documentCount; batchStart += 8)
        {
            var batch = new List<Document>(8);
            for (int ordinal = batchStart; ordinal < batchStart + 8; ordinal++)
            {
                Document document = CreateRandomDocument($"doc-{ordinal:D2}", vocabulary, random);
                batch.Add(document);
                liveDocuments[document.Id.Value] = document;
                allDocumentIds.Add(document.Id.Value);
                reference.Index(document);
            }

            persistent.IndexMany(batch);
        }

        for (int operation = 0; operation < 18; operation++)
        {
            string id = $"doc-{random.Next(documentCount):D2}";
            if (operation % 3 == 0)
            {
                persistent.Delete(new DocumentId(id));
                reference.Delete(new DocumentId(id));
                if (liveDocuments.Remove(id))
                {
                    staleDocumentIds.Add(id);
                }
            }
            else
            {
                Document replacement = CreateRandomDocument(id, vocabulary, random);
                persistent.Index(replacement);
                reference.Index(replacement);
                liveDocuments[id] = replacement;
                allDocumentIds.Add(id);
                staleDocumentIds.Remove(id);
            }
        }

        PersistentFullTextIndex reopened = PersistentFullTextIndex.Open(
            Path.Combine(_directory, "persistent"),
            new UnicodeTokenizer(),
            options: new PersistentIndexOptions { EnableBackgroundMerge = false });

        for (int queryIndex = 0; queryIndex < 36; queryIndex++)
        {
            int firstTermIndex = random.Next(vocabulary.Length);
            int secondTermIndex = random.Next(vocabulary.Length - 1);
            if (secondTermIndex >= firstTermIndex)
            {
                secondTermIndex++;
            }

            string firstTerm = vocabulary[firstTermIndex];
            string secondTerm = vocabulary[secondTermIndex];

            FullTextQuery query = (queryIndex % 3) switch
            {
                0 => new OrQuery([new TermQuery("body", firstTerm), new TermQuery("body", secondTerm)]),
                1 => new TermQuery("body", firstTerm),
                _ => new AndQuery([new TermQuery("body", firstTerm), new TermQuery("body", secondTerm)]),
            };
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            switch (queryIndex % 4)
            {
                case 0:
                    allowed.UnionWith(allDocumentIds);
                    break;
                case 1:
                    foreach (string id in liveDocuments.Keys)
                    {
                        if (random.Next(3) != 0)
                        {
                            allowed.Add(id);
                        }
                    }
                    allowed.Add($"unknown-{queryIndex:D2}");
                    break;
                case 2:
                    allowed.UnionWith(staleDocumentIds);
                    allowed.Add($"unknown-{queryIndex:D2}");
                    break;
            }

            int topK = (queryIndex % 5) switch
            {
                0 => 0,
                1 => 1,
                2 => 2,
                3 => 7,
                _ => 100,
            };

            IReadOnlyList<SearchHit> expectedAll = reference.Search(query, topK: documentCount);
            IReadOnlyList<SearchHit> actualAll = reopened.Search(query, topK: documentCount);
            Assert.Equal(expectedAll.Select(static hit => hit.DocumentId.Value), actualAll.Select(static hit => hit.DocumentId.Value));
            Assert.Equal(expectedAll.Select(static hit => hit.Score), actualAll.Select(static hit => hit.Score));

            IReadOnlyList<SearchHit> expected = expectedAll
                .Where(hit => allowed.Contains(hit.DocumentId.Value))
                .Take(topK)
                .ToArray();
            PersistentFullTextFilteredSearchResult actual = reopened.SearchFiltered(
                query,
                topK,
                allowed,
                maxPostingVisits: 20_000,
                CancellationToken.None);

            Assert.False(actual.PostingBudgetExceeded);
            Assert.Equal(expected.Select(static hit => hit.DocumentId.Value), actual.Hits.Select(static hit => hit.DocumentId.Value));
            Assert.Equal(expected.Select(static hit => hit.Score), actual.Hits.Select(static hit => hit.Score));
        }
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

    private static Document CreateRandomDocument(string id, IReadOnlyList<string> vocabulary, Random random)
    {
        int length = random.Next(1, 9);
        var terms = new string[length];
        for (int index = 0; index < terms.Length; index++)
        {
            terms[index] = vocabulary[random.Next(vocabulary.Count)];
        }

        return new Document(new DocumentId(id)).Set("body", string.Join(' ', terms));
    }

    private DocumentFullTextIndexStore OpenDocumentStore()
        => DocumentFullTextIndexStore.Open(
            Path.Combine(_directory, "documents"),
            new DocumentFullTextIndex(
                "ft_docs",
                ["$.body"],
                "unicode",
                DateTime.UtcNow.Ticks));
}
