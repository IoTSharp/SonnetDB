using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Document;

/// <summary>并发写、重开恢复与索引一致性场景。</summary>
public sealed class DocumentConcurrentRecoveryScenario : DocumentScenarioBase
{
    /// <inheritdoc />
    public override string Name => "document_concurrent_write_recovery_index_consistency";

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(IDocumentOps ops, ScenarioContext context)
    {
        const int writers = 8;
        const int perWriter = 20;
        string collection = Collection(context, "recovery");
        await ops.ResetCollectionAsync(collection, context.Cancellation).ConfigureAwait(false);
        await ops.CreateIndexAsync(collection, new DocumentParityIndex("idx_site", "$.site"), context.Cancellation).ConfigureAwait(false);

        var tasks = Enumerable.Range(0, writers).Select(async writer =>
        {
            for (int item = 0; item < perWriter; item++)
            {
                string id = $"w{writer:D2}-{item:D3}";
                bool inserted = await ops.TryInsertAsync(collection,
                    new DocumentParityRecord(id, $$"""{"site":"{{(item % 2 == 0 ? "east" : "west")}}","value":{{item}}}"""),
                    context.Cancellation).ConfigureAwait(false);
                if (!inserted)
                    throw new InvalidOperationException($"Concurrent insert unexpectedly rejected id '{id}'.");
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        await ops.RestartAsync(context.Cancellation).ConfigureAwait(false);

        long count = await ops.CountAsync(collection, context.Cancellation).ConfigureAwait(false);
        var state = await ops.VerifyIndexAsync(collection, "idx_site", context.Cancellation).ConfigureAwait(false);
        int east = (await ops.FindAsync(collection, new DocumentParityQuery(
            new DocumentParityPredicate("$.site", DocumentParityOperator.Equal, "east")),
            context.Cancellation).ConfigureAwait(false)).Count;
        var result = Rows(["documents", "east", "index_entries", "consistent"],
            [count, (long)east, state.IndexedDocumentCount, state.IsConsistent ? 1L : 0L]);
        result.Pass = count == writers * perWriter && east == writers * perWriter / 2 && state.IsConsistent;
        return result;
    }
}
