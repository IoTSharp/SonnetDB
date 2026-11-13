using System.Collections;
using SonnetDB.Data.FullText;
using SonnetDB.Documents;
using SonnetDB.FullText;

namespace SonnetDB.Core.Tests.Documents;

public sealed class FullTextFacetCancellationTests
{
    [Fact]
    public void FacetObservesCancellationInsideTheDocumentTraversalAndRecovers()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new FullTextSearchBudget(cancellationToken: cancellation.Token);
        DocumentFullTextSearchHit[] hits = [new("a", 1), new("b", 1), new("c", 1)];
        var values = new Dictionary<string, DocumentRow>(StringComparer.Ordinal)
        {
            ["a"] = new("a", "{\"site\":\"north\"}", 1),
            ["b"] = new("b", "{\"site\":\"south\"}", 1),
            ["c"] = new("c", "{\"site\":\"north\"}", 1),
        };
        var rows = new CancellingRows(values, cancellation);
        Assert.Throws<OperationCanceledException>(() => SndbFullTextClient.BuildFacets(
            [new SndbFullTextFacetRequest("$.site")], hits, rows, budget));
        Assert.Equal(2, rows.ReadCount);
        using var recovered = new FullTextSearchBudget();
        var result = SndbFullTextClient.BuildFacets([new SndbFullTextFacetRequest("$.site")], hits, values, recovered);
        Assert.Equal(new[] { new SndbFullTextFacetBucket("north", 2), new SndbFullTextFacetBucket("south", 1) }, Assert.Single(result).Buckets);
    }

    private sealed class CancellingRows(IReadOnlyDictionary<string, DocumentRow> rows, CancellationTokenSource cancellation)
        : IReadOnlyDictionary<string, DocumentRow>
    {
        public int ReadCount { get; private set; }
        public int Count => rows.Count;
        public IEnumerable<string> Keys => rows.Keys;
        public IEnumerable<DocumentRow> Values => rows.Values;
        public DocumentRow this[string key] => rows[key];
        public bool ContainsKey(string key) => rows.ContainsKey(key);
        public IEnumerator<KeyValuePair<string, DocumentRow>> GetEnumerator() => rows.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool TryGetValue(string key, out DocumentRow value)
        {
            if (++ReadCount == 2)
                cancellation.Cancel();
            return rows.TryGetValue(key, out value!);
        }
    }
}
