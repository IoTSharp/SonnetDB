using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.FullText;

/// <summary>
/// 查询期间增量更新场景。
/// </summary>
public sealed class IncrementalUpdateDuringQueryScenario : FullTextScenarioBase
{
    /// <inheritdoc />
    public override string Name => "incremental_update_during_query";

    /// <inheritdoc />
    public override Capability Required => Capability.Fulltext;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunFullTextAsync(IFullTextOps ops, ScenarioContext ctx)
    {
        string index = Index(ctx, "update");
        var documents = BuildEnglishDocuments(300).ToList();
        await ops.ResetIndexAsync(index, new FullTextIndexOptions("unicode", ["category"]), ctx.Cancellation).ConfigureAwait(false);
        await ops.UpsertAsync(index, documents, ctx.Cancellation).ConfigureAwait(false);

        var before = await ops.SearchAsync(index, new FullTextSearchRequest("brandnew alarm", 5), ctx.Cancellation).ConfigureAwait(false);
        var update = new FullTextDocument("doc-new", "pump critical", "brandnew alarm pressure pump urgent", "pump", ["north", "critical"]);
        await ops.UpsertAsync(index, [update], ctx.Cancellation).ConfigureAwait(false);
        await ops.DeleteDocumentAsync(index, documents[0].Id, ctx.Cancellation).ConfigureAwait(false);
        var after = await ops.SearchAsync(index, new FullTextSearchRequest("brandnew alarm", 5), ctx.Cancellation).ConfigureAwait(false);
        long count = await ops.CountDocumentsAsync(index, ctx.Cancellation).ConfigureAwait(false);

        bool foundNew = after.Any(static h => h.Id == "doc-new");
        var result = MetricRow((long)before.Count, foundNew ? 1L : 0L, count);
        result.Pass = before.Count == 0 && foundNew && count == documents.Count;
        return result;
    }
}
