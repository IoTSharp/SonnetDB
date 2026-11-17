using SonnetDB.FullText.Index;
using Xunit;

namespace SonnetDB.Core.Tests.FullText;

public sealed class TopKSearchHitHeapTests
{
    [Fact]
    public void Keeps_best_hits_and_preserves_score_then_id_order()
    {
        var heap = new TopKSearchHitHeap(2);

        heap.Add(new SearchHit(new DocumentId("z"), 1.0));
        heap.Add(new SearchHit(new DocumentId("b"), 2.0));
        heap.Add(new SearchHit(new DocumentId("a"), 2.0));
        heap.Add(new SearchHit(new DocumentId("c"), 0.1));

        IReadOnlyList<SearchHit> hits = heap.ToSortedList();

        Assert.Collection(
            hits,
            first =>
            {
                Assert.Equal("a", first.DocumentId.Value);
                Assert.Equal(2.0, first.Score);
            },
            second =>
            {
                Assert.Equal("b", second.DocumentId.Value);
                Assert.Equal(2.0, second.Score);
            });
    }

    [Fact]
    public void Extreme_capacity_does_not_preallocate_the_entire_query_limit()
    {
        var heap = new TopKSearchHitHeap(int.MaxValue);

        heap.Add(new SearchHit(new DocumentId("doc"), 1.0));

        Assert.Single(heap.ToSortedList());
    }
}
