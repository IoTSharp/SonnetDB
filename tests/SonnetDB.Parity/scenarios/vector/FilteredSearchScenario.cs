using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Vector;

/// <summary>
/// 带 payload/tag 过滤的向量检索场景。
/// </summary>
public sealed class FilteredSearchScenario : VectorScenarioBase
{
    /// <inheritdoc />
    public override string Name => "filtered_search";

    /// <inheritdoc />
    public override Capability Required => Capability.Vector | Capability.HnswFiltered;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunVectorAsync(IVectorOps ops, ScenarioContext ctx)
    {
        const int Dimension = 16;
        var records = BuildRecords(1_000, Dimension);
        string collection = Collection(ctx, "filtered");
        float[] query = records[10].Vector;

        await ops.ResetCollectionAsync(collection, Dimension, ctx.Cancellation).ConfigureAwait(false);
        await ops.UpsertAsync(collection, records, ctx.Cancellation).ConfigureAwait(false);
        var hits = await ops.SearchAsync(collection, query, 10, "hot", ctx.Cancellation).ConfigureAwait(false);

        bool allHot = hits.All(static h => h.Category == "hot");
        var expected = ExactTopK(records, query, 10, "hot");
        int matched = hits.Select(static h => h.Id).Count(expected.Contains);
        var result = MetricRow(allHot ? 1L : 0L, (long)matched);
        result.Metrics["filtered_matches"] = matched;
        result.Pass = allHot && hits.Count == 10 && matched >= 8;
        return result;
    }
}
