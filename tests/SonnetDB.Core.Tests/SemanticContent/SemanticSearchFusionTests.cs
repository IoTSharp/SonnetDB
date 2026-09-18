using System.Text.Json;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class SemanticSearchFusionTests
{
    private static SemanticSearchCandidate Hit(string id, double score, string? contentId = null)
        => new(id, contentId ?? id, "text-" + id, score);
    private static SemanticSearchFusionOptions Options(int k = 10) => new() { TopK = k };

    [Fact]
    public void JsonRoundTrip_OptionsAndHits_UsesGeneratedMetadata()
    {
        var options = Options(2) with { Mode = SemanticSearchFusionMode.NormalizedScore, MaxDuration = TimeSpan.FromSeconds(3) };
        string json = JsonSerializer.Serialize(options, SemanticContentJsonContext.Default.SemanticSearchFusionOptions);
        Assert.Equal(options, JsonSerializer.Deserialize(json, SemanticContentJsonContext.Default.SemanticSearchFusionOptions));
        IReadOnlyList<SemanticSearchHit> hits = [new("a", "content", "text", "source", "section", 0.5, 0.75)];
        json = JsonSerializer.Serialize(hits, SemanticContentJsonContext.Default.IReadOnlyListSemanticSearchHit);
        Assert.Equal(hits, JsonSerializer.Deserialize(json, SemanticContentJsonContext.Default.IReadOnlyListSemanticSearchHit));
    }

    [Fact]
    public async Task FuseAsync_RepeatedHitsAcrossSources_CountsOnlyBestRankPerSource()
    {
        IReadOnlyList<SemanticSearchHit> result = await SemanticSearchFusion.FuseAsync("q",
            [new([Hit("b", 7), Hit("a", 9), Hit("a", 8)]), new([Hit("b", 2), Hit("a", 1)])]);
        Assert.Equal(["a", "b"], result.Select(h => h.Id));
        Assert.Equal(1d / 61 + 1d / 62, result[0].Score, 12);
        Assert.Equal(result[0].Score, result[1].Score);
    }

    [Fact]
    public async Task FuseAsync_TiedInputPermutation_UsesOrdinalIdentity()
    {
        var first = await SemanticSearchFusion.FuseAsync("q", [new([Hit("z", 1), Hit("a", 1)])]);
        var second = await SemanticSearchFusion.FuseAsync("q", [new([Hit("a", 1), Hit("z", 1)])]);
        Assert.Equal(first, second);
        Assert.Equal(["a", "z"], first.Select(h => h.Id));
    }

    [Fact]
    public async Task FuseAsync_NormalizedExtremeAndConstantScores_ReturnsFiniteWeightedScores()
    {
        var result = await SemanticSearchFusion.FuseAsync("q",
            [new([Hit("a", double.MaxValue), Hit("b", -double.MaxValue)]), new([Hit("a", -4), Hit("b", -4)], 2)],
            Options() with { Mode = SemanticSearchFusionMode.NormalizedScore });
        Assert.Equal(3, result[0].Score);
        Assert.Equal(2, result[1].Score);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task FuseAsync_NonFiniteScore_RejectsBeforeReranking(double score)
        => await Assert.ThrowsAsync<ArgumentException>(() => SemanticSearchFusion.FuseAsync("q", [new([Hit("a", score)])]).AsTask());

    [Fact]
    public async Task FuseAsync_DuplicateIdentityWithDifferentBody_Rejects()
        => await Assert.ThrowsAsync<ArgumentException>(() => SemanticSearchFusion.FuseAsync("q",
            [new([Hit("a", 1)]), new([Hit("a", 1) with { Text = "different" }])]).AsTask());

    [Fact]
    public async Task FuseAsync_OptionalContentDedup_ChoosesBestChunkBeforeWindow()
    {
        var result = await SemanticSearchFusion.FuseAsync("q",
            [new([Hit("a1", 10, "a"), Hit("a2", 9, "a"), Hit("b", 8)])],
            Options(2) with { DeduplicateByContent = true });
        Assert.Equal(["a1", "b"], result.Select(h => h.Id));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("nan")]
    public async Task FuseAsync_InvalidRerankerResults_Rejects(string fault)
    {
        var reranker = new Reranker((_, _, _) => ValueTask.FromResult<IReadOnlyList<SemanticSearchRerankScore>>(fault switch
        {
            "unknown" => [new("a", 1), new("evil", 2)],
            "duplicate" => [new("a", 1), new("a", 2)],
            "missing" => [new("a", 1)],
            _ => [new("a", 1), new("b", double.NaN)],
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SemanticSearchFusion.FuseAsync("q",
            [new([Hit("a", 2), Hit("b", 1)])], Options(2), reranker).AsTask());
    }

    [Fact]
    public async Task FuseAsync_RerankerWindow_PreservesContentAndFusionScore()
    {
        var reranker = new Reranker((_, candidates, _) =>
        {
            Assert.Equal(2, candidates.Count);
            Assert.IsAssignableFrom<System.Collections.ObjectModel.ReadOnlyCollection<SemanticSearchHit>>(candidates);
            return ValueTask.FromResult<IReadOnlyList<SemanticSearchRerankScore>>([new("b", 20), new("a", 10)]);
        });
        var result = await SemanticSearchFusion.FuseAsync("q", [new([Hit("a", 3), Hit("b", 2), Hit("c", 1)])],
            Options(1) with { MaxRerankCandidates = 2 }, reranker);
        Assert.Equal("b", Assert.Single(result).Id);
        Assert.Equal("text-b", result[0].Text);
        Assert.Equal(1d / 62, result[0].FusionScore);
        Assert.Equal(20, result[0].Score);
    }

    [Fact]
    public async Task FuseAsync_BudgetsOrCancellation_RejectsWithoutPartialResults()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SemanticSearchFusion.FuseAsync("q",
            [new([Hit("a", 1), Hit("b", 2)])], Options(1) with { MaxCandidatesPerSource = 1 }).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => SemanticSearchFusion.FuseAsync("q",
            [new([Hit("a", 1)])], Options(1) with { MaxTextCharacters = 1 }).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SemanticSearchFusion.FuseAsync("q",
            [], cancellationToken: new CancellationToken(true)).AsTask());
        var slow = new Reranker(async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return [];
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SemanticSearchFusion.FuseAsync("q",
            [new([Hit("a", 1)])], Options(1) with { MaxDuration = TimeSpan.FromMilliseconds(30) }, slow).AsTask());
    }

    [Fact]
    public async Task FuseAsync_EmptySources_ReturnsEmptyWithoutCallingHook()
        => Assert.Empty(await SemanticSearchFusion.FuseAsync("q", [], reranker: new Reranker((_, _, _) => throw new Exception("must not call"))));

    [Fact]
    public async Task FuseAsync_CustomLists_UsesBoundedIndexAccessInsteadOfUntrustedEnumerators()
    {
        var candidates = new IndexOnlyList<SemanticSearchCandidate>([Hit("a", 1)]);
        var sources = new IndexOnlyList<SemanticSearchSource>([new(candidates)]);
        var reranker = new Reranker((_, _, _) => ValueTask.FromResult<IReadOnlyList<SemanticSearchRerankScore>>(
            new IndexOnlyList<SemanticSearchRerankScore>([new("a", 2)])));
        Assert.Equal("a", Assert.Single(await SemanticSearchFusion.FuseAsync("q", sources, Options(1), reranker)).Id);
    }

    private sealed class IndexOnlyList<T>(T[] values) : IReadOnlyList<T>
    {
        public int Count => values.Length;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Do not execute caller enumerators.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class Reranker(Func<string, IReadOnlyList<SemanticSearchHit>, CancellationToken, ValueTask<IReadOnlyList<SemanticSearchRerankScore>>> run) : ISemanticSearchReranker
    {
        public ValueTask<IReadOnlyList<SemanticSearchRerankScore>> RerankAsync(string query, IReadOnlyList<SemanticSearchHit> candidates, CancellationToken cancellationToken)
            => run(query, candidates, cancellationToken);
    }
}
