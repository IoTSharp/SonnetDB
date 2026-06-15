using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.FullText;

/// <summary>
/// 中文分词正确性场景。
/// </summary>
public sealed class CjkTokenizeCorrectnessScenario : FullTextScenarioBase
{
    /// <inheritdoc />
    public override string Name => "cjk_tokenize_correctness";

    /// <inheritdoc />
    public override Capability Required => Capability.Fulltext | Capability.FulltextCjk;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunFullTextAsync(IFullTextOps ops, ScenarioContext ctx)
    {
        string index = Index(ctx, "cjk");
        await ops.ResetIndexAsync(index, new FullTextIndexOptions("cjk", ["category"]), ctx.Cancellation).ConfigureAwait(false);
        await ops.UpsertAsync(index, BuildChineseDocuments(), ctx.Cancellation).ConfigureAwait(false);

        var hits = await ops.SearchAsync(index, new FullTextSearchRequest("水泵 报警", 10), ctx.Cancellation).ConfigureAwait(false);
        bool containsExpected = hits.Any(static h => h.Id == "cjk-1");
        var result = MetricRow(containsExpected ? 1L : 0L, (long)hits.Count);
        result.Pass = containsExpected;
        return result;
    }
}
