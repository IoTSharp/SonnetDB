using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.FullText;

/// <summary>
/// 全文索引写入吞吐冒烟场景。默认轻量规模，可通过 <c>PARITY_FULLTEXT_DOCUMENTS=1000000</c> 跑完整 1M 文档。
/// </summary>
public sealed class IndexOneMillionDocumentsScenario : FullTextScenarioBase
{
    /// <inheritdoc />
    public override string Name => "index_1m_documents";

    /// <inheritdoc />
    public override Capability Required => Capability.Fulltext;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunFullTextAsync(IFullTextOps ops, ScenarioContext ctx)
    {
        string index = Index(ctx, "index");
        int count = EnvInt("PARITY_FULLTEXT_DOCUMENTS", 5_000);
        var documents = BuildEnglishDocuments(count);

        await ops.ResetIndexAsync(index, new FullTextIndexOptions("unicode", ["category"]), ctx.Cancellation).ConfigureAwait(false);
        var started = DateTimeOffset.UtcNow;
        await ops.UpsertAsync(index, documents, ctx.Cancellation).ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - started;
        long actual = await ops.CountDocumentsAsync(index, ctx.Cancellation).ConfigureAwait(false);

        var result = MetricRow(actual);
        result.Metrics["documents"] = count;
        result.Metrics["elapsed_ms"] = elapsed.TotalMilliseconds;
        result.Metrics["documents_per_second"] = elapsed.TotalSeconds <= 0 ? 0 : count / elapsed.TotalSeconds;
        result.Metrics["full_1m_enabled"] = count >= 1_000_000;
        result.Pass = actual == count;
        return result;
    }
}
