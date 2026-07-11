using System.Text.Json;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Document;

/// <summary>CRUD、filter、projection 与 sort 语义场景。</summary>
public sealed class DocumentCrudQueryScenario : DocumentScenarioBase
{
    /// <inheritdoc />
    public override string Name => "document_crud_filter_projection_sort";

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(IDocumentOps ops, ScenarioContext context)
    {
        string collection = Collection(context, "crud");
        await ops.ResetCollectionAsync(collection, context.Cancellation).ConfigureAwait(false);
        await ops.InsertManyAsync(collection,
        [
            new("dev-1", """{"name":"pump-a","site":"east","score":10}"""),
            new("dev-2", """{"name":"pump-b","site":"west","score":20}"""),
            new("dev-3", """{"name":"fan-c","site":"east","score":30}"""),
            new("dev-4", """{"name":"gateway-d","site":"west","score":40}"""),
        ], context.Cancellation).ConfigureAwait(false);

        var documents = await ops.FindAsync(collection, new DocumentParityQuery(
            new DocumentParityPredicate("$.score", DocumentParityOperator.GreaterThanOrEqual, 20L),
            ["$.name", "$.site", "$.score"],
            "$.score",
            Descending: true,
            Limit: 3), context.Cancellation).ConfigureAwait(false);
        int deleted = await ops.DeleteAsync(collection,
            new DocumentParityPredicate("_id", DocumentParityOperator.Equal, "dev-4"),
            many: false,
            context.Cancellation).ConfigureAwait(false);
        long remaining = await ops.CountAsync(collection, context.Cancellation).ConfigureAwait(false);

        var rows = documents.Select(Parse).ToArray();
        var result = Rows(["id", "name", "site", "score"], rows);
        result.Pass = rows.Length == 3 && deleted == 1 && remaining == 3;
        result.Metrics["deleted"] = deleted;
        result.Metrics["remaining"] = remaining;
        return result;
    }

    private static object?[] Parse(DocumentParityRecord record)
    {
        using var document = JsonDocument.Parse(record.Json);
        var root = document.RootElement;
        return [record.Id, root.GetProperty("name").GetString(), root.GetProperty("site").GetString(), root.GetProperty("score").GetInt64()];
    }
}
