using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.FullText;

/// <summary>
/// BM25 top-10 排序重合率场景。
/// </summary>
public sealed class Bm25RankingTop10OverlapScenario : FullTextScenarioBase
{
    /// <inheritdoc />
    public override string Name => "bm25_ranking_top10_overlap";

    /// <inheritdoc />
    public override Capability Required => Capability.Fulltext;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunFullTextAsync(IFullTextOps ops, ScenarioContext ctx)
    {
        string index = Index(ctx, "bm25");
        int count = EnvInt("PARITY_FULLTEXT_BM25_DOCUMENTS", 2_000);
        var documents = BuildEnglishDocuments(count);

        await ops.ResetIndexAsync(index, new FullTextIndexOptions("unicode", ["category"]), ctx.Cancellation).ConfigureAwait(false);
        await ops.UpsertAsync(index, documents, ctx.Cancellation).ConfigureAwait(false);
        var hits = await ops.SearchAsync(index, new FullTextSearchRequest("pump alarm pressure", 10), ctx.Cancellation).ConfigureAwait(false);

        var expected = documents
            .Where(static d => string.Equals(d.Category, "pump", StringComparison.Ordinal))
            .Take(10)
            .Select(static d => d.Id)
            .ToHashSet(StringComparer.Ordinal);
        int overlap = hits.Select(static h => h.Id).Count(expected.Contains);
        double ratio = overlap / 10d;

        var result = MetricRow((long)Math.Round(ratio * 1000d), (long)hits.Count);
        result.Metrics["top10_overlap"] = ratio;
        result.Metrics["documents"] = count;
        result.Pass = hits.Count == 10 && ratio >= 0.8d;
        return result;
    }
}
