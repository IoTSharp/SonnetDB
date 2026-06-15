using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.FullText;

/// <summary>
/// typo tolerant 查询场景。
/// </summary>
public sealed class TypoTolerantQueryScenario : FullTextScenarioBase
{
    /// <inheritdoc />
    public override string Name => "typo_tolerant_query";

    /// <inheritdoc />
    public override Capability Required => Capability.Fulltext | Capability.FulltextTypoTolerant;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunFullTextAsync(IFullTextOps ops, ScenarioContext ctx)
    {
        string index = Index(ctx, "typo");
        await ops.ResetIndexAsync(index, new FullTextIndexOptions("unicode", ["category"]), ctx.Cancellation).ConfigureAwait(false);
        await ops.UpsertAsync(index,
        [
            new("typo-1", "pump alarm", "pump alarm pressure station north", "pump", ["north"]),
            new("typo-2", "fan normal", "fan airflow normal station south", "fan", ["south"]),
        ], ctx.Cancellation).ConfigureAwait(false);

        var hits = await ops.SearchAsync(index, new FullTextSearchRequest("pmp alrm", 5, TypoTolerant: true), ctx.Cancellation).ConfigureAwait(false);
        bool found = hits.Any(static h => h.Id == "typo-1");
        var result = MetricRow(found ? 1L : 0L, (long)hits.Count);
        result.Pass = found;
        return result;
    }
}
