using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Document;

/// <summary>aggregation group/count/average 语义场景。</summary>
public sealed class DocumentAggregationScenario : DocumentScenarioBase
{
    /// <inheritdoc />
    public override string Name => "document_aggregation_group_average";

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(IDocumentOps ops, ScenarioContext context)
    {
        string collection = Collection(context, "aggregation");
        await ops.ResetCollectionAsync(collection, context.Cancellation).ConfigureAwait(false);
        await ops.InsertManyAsync(collection,
        [
            new("a", """{"site":"east","score":10}"""),
            new("b", """{"site":"east","score":30}"""),
            new("c", """{"site":"west","score":20}"""),
            new("d", """{"site":"west","score":40}"""),
        ], context.Cancellation).ConfigureAwait(false);
        var aggregates = await ops.AggregateAsync(collection, "$.site", "$.score", context.Cancellation).ConfigureAwait(false);
        var result = Rows(["site", "count", "average"], aggregates
            .Select(static row => new object?[] { row.Group, row.Count, row.Average })
            .ToArray());
        result.Pass = aggregates.Count == 2 && aggregates.All(static row => row.Count == 2);
        return result;
    }
}
