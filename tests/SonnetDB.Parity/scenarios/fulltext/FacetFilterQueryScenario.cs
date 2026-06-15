using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.FullText;

/// <summary>
/// 全文 + facet/filter 查询场景。
/// </summary>
public sealed class FacetFilterQueryScenario : FullTextScenarioBase
{
    /// <inheritdoc />
    public override string Name => "facet_filter_query";

    /// <inheritdoc />
    public override Capability Required => Capability.Fulltext | Capability.FulltextFacetFilter;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunFullTextAsync(IFullTextOps ops, ScenarioContext ctx)
    {
        string index = Index(ctx, "facet");
        var documents = BuildEnglishDocuments(500);
        await ops.ResetIndexAsync(index, new FullTextIndexOptions("unicode", ["category"]), ctx.Cancellation).ConfigureAwait(false);
        await ops.UpsertAsync(index, documents, ctx.Cancellation).ConfigureAwait(false);

        var hits = await ops.SearchAsync(index, new FullTextSearchRequest("station warning", 100, CategoryFilter: "pump"), ctx.Cancellation).ConfigureAwait(false);
        bool allPump = hits.Count > 0 && hits.All(static h => h.Category == "pump");
        var result = MetricRow((long)hits.Count, allPump ? 1L : 0L);
        result.Pass = allPump;
        return result;
    }
}
