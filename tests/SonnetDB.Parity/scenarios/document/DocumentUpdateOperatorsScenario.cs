using System.Text.Json;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Document;

/// <summary>局部更新操作符语义场景。</summary>
public sealed class DocumentUpdateOperatorsScenario : DocumentScenarioBase
{
    /// <inheritdoc />
    public override string Name => "document_update_operators";

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(IDocumentOps ops, ScenarioContext context)
    {
        string collection = Collection(context, "updates");
        await ops.ResetCollectionAsync(collection, context.Cancellation).ConfigureAwait(false);
        await ops.InsertManyAsync(collection,
            [new("dev-1", """{"name":"pump-a","score":10,"legacy":true,"tags":["alpha"]}""")],
            context.Cancellation).ConfigureAwait(false);
        var predicate = new DocumentParityPredicate("_id", DocumentParityOperator.Equal, "dev-1");
        int first = await ops.UpdateAsync(collection, predicate, new DocumentParityUpdate(
            Set: new Dictionary<string, object?> { ["$.status"] = "online" },
            Unset: ["$.legacy"],
            Increment: new Dictionary<string, object?> { ["$.score"] = 5L },
            Rename: new Dictionary<string, string> { ["$.name"] = "$.label" },
            Push: new Dictionary<string, object?> { ["$.tags"] = "beta" }),
            many: false,
            context.Cancellation).ConfigureAwait(false);
        int second = await ops.UpdateAsync(collection, predicate, new DocumentParityUpdate(
            AddToSet: new Dictionary<string, object?> { ["$.tags"] = "beta" }),
            many: false,
            context.Cancellation).ConfigureAwait(false);

        var record = AssertSingle(await ops.FindAsync(collection, new DocumentParityQuery(predicate), context.Cancellation).ConfigureAwait(false));
        using var document = JsonDocument.Parse(record.Json);
        var root = document.RootElement;
        string tags = string.Join(",", root.GetProperty("tags").EnumerateArray().Select(static value => value.GetString()));
        bool legacyMissing = !root.TryGetProperty("legacy", out _);
        var result = Rows(["label", "score", "status", "tags", "legacy_missing"],
            [root.GetProperty("label").GetString(), checked((long)root.GetProperty("score").GetDouble()), root.GetProperty("status").GetString(), tags, legacyMissing ? 1L : 0L]);
        result.Pass = first == 1 && second == 0 && legacyMissing;
        return result;
    }

    private static DocumentParityRecord AssertSingle(IReadOnlyList<DocumentParityRecord> rows)
        => rows.Count == 1 ? rows[0] : throw new InvalidOperationException($"Expected one document, got {rows.Count}.");
}
