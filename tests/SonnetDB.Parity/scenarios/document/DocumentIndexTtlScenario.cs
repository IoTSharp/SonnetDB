using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Document;

/// <summary>unique 与 TTL index 语义场景。</summary>
public sealed class DocumentIndexTtlScenario : DocumentScenarioBase
{
    /// <inheritdoc />
    public override string Name => "document_index_unique_ttl";

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(IDocumentOps ops, ScenarioContext context)
    {
        string collection = Collection(context, "indexes");
        await ops.ResetCollectionAsync(collection, context.Cancellation).ConfigureAwait(false);
        await ops.CreateIndexAsync(collection, new DocumentParityIndex("ux_serial", "$.serial", Unique: true), context.Cancellation).ConfigureAwait(false);
        await ops.CreateIndexAsync(collection, new DocumentParityIndex("ttl_expires", "$.expiresAt", TtlSeconds: 1), context.Cancellation).ConfigureAwait(false);

        long expired = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        long future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        await ops.InsertManyAsync(collection,
        [
            new("stable", $$"""{"serial":"serial-1","expiresAt":{{future}}}"""),
            new("expired", $$"""{"serial":"serial-2","expiresAt":{{expired}}}"""),
        ], context.Cancellation).ConfigureAwait(false);
        bool duplicateAccepted = await ops.TryInsertAsync(collection,
            new DocumentParityRecord("duplicate", $$"""{"serial":"serial-1","expiresAt":{{future}}}"""),
            context.Cancellation).ConfigureAwait(false);

        long count = await WaitForCountAsync(ops, collection, expected: 1, context.Cancellation).ConfigureAwait(false);
        var state = await ops.VerifyIndexAsync(collection, "ux_serial", context.Cancellation).ConfigureAwait(false);
        var result = Rows(["duplicate_rejected", "ttl_remaining", "index_consistent"],
            [duplicateAccepted ? 0L : 1L, count, state.IsConsistent ? 1L : 0L]);
        result.Pass = !duplicateAccepted && count == 1 && state.IsConsistent;
        result.Metrics["ttl_wait_seconds"] = ops.BackendName == "mongodb" ? 10 : 0;
        return result;
    }

    private static async Task<long> WaitForCountAsync(IDocumentOps ops, string collection, long expected, CancellationToken ct)
    {
        long count = await ops.CountAsync(collection, ct).ConfigureAwait(false);
        for (int attempt = 0; count != expected && attempt < 40; attempt++)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            count = await ops.CountAsync(collection, ct).ConfigureAwait(false);
        }
        return count;
    }
}
