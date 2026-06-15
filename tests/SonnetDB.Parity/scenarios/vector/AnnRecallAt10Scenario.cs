using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Vector;

/// <summary>
/// ANN recall@10 场景。
/// </summary>
public sealed class AnnRecallAt10Scenario : VectorScenarioBase
{
    /// <inheritdoc />
    public override string Name => "ann_recall_at_10";

    /// <inheritdoc />
    public override Capability Required => Capability.Vector;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunVectorAsync(IVectorOps ops, ScenarioContext ctx)
    {
        int count = EnvInt("PARITY_VECTOR_RECORDS", 2_000);
        const int Dimension = 16;
        string collection = Collection(ctx, "ann");
        var records = BuildRecords(count, Dimension);
        float[] query = records[count / 3].Vector;

        await ops.ResetCollectionAsync(collection, Dimension, ctx.Cancellation).ConfigureAwait(false);
        await ops.UpsertAsync(collection, records, ctx.Cancellation).ConfigureAwait(false);
        var hits = await ops.SearchAsync(collection, query, 10, null, ctx.Cancellation).ConfigureAwait(false);

        var expected = ExactTopK(records, query, 10);
        int matched = hits.Select(static h => h.Id).Count(expected.Contains);
        double recall = matched / 10d;
        var result = MetricRow((long)Math.Round(recall * 1000d), (long)hits.Count);
        result.Metrics["recall_at_10"] = recall;
        result.Metrics["records"] = count;
        result.Pass = hits.Count == 10 && recall >= 0.8d;
        return result;
    }
}
