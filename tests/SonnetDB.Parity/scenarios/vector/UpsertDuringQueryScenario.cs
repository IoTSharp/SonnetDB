using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Vector;

/// <summary>
/// 查询期间并发 upsert 场景。
/// </summary>
public sealed class UpsertDuringQueryScenario : VectorScenarioBase
{
    /// <inheritdoc />
    public override string Name => "upsert_during_query";

    /// <inheritdoc />
    public override Capability Required => Capability.Vector;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunVectorAsync(IVectorOps ops, ScenarioContext ctx)
    {
        const int Dimension = 16;
        string collection = Collection(ctx, "upsert");
        var initial = BuildRecords(500, Dimension);
        var extra = BuildRecords(100, Dimension)
            .Select(static r => r with { Id = r.Id + 10_000 })
            .ToArray();
        float[] query = extra[0].Vector;

        await ops.ResetCollectionAsync(collection, Dimension, ctx.Cancellation).ConfigureAwait(false);
        await ops.UpsertAsync(collection, initial, ctx.Cancellation).ConfigureAwait(false);

        var queryTask = Task.Run(async () =>
        {
            int nonEmpty = 0;
            for (int i = 0; i < 20; i++)
            {
                var hits = await ops.SearchAsync(collection, query, 10, null, ctx.Cancellation).ConfigureAwait(false);
                if (hits.Count > 0)
                    nonEmpty++;
            }
            return nonEmpty;
        }, ctx.Cancellation);

        await ops.UpsertAsync(collection, extra, ctx.Cancellation).ConfigureAwait(false);
        int nonEmptyQueries = await queryTask.ConfigureAwait(false);
        var finalHits = await ops.SearchAsync(collection, query, 10, null, ctx.Cancellation).ConfigureAwait(false);
        bool upsertVisible = finalHits.Any(h => h.Id == extra[0].Id);

        var result = MetricRow((long)nonEmptyQueries, upsertVisible ? 1L : 0L);
        result.Metrics["non_empty_queries"] = nonEmptyQueries;
        result.Pass = nonEmptyQueries > 0 && upsertVisible;
        return result;
    }
}
