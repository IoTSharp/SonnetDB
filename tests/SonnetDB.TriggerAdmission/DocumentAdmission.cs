using System.Text.Json;
using SonnetDB.Documents;

namespace SonnetDB.TriggerAdmission;

/// <summary>
/// Document model admission baseline.  The scenarios intentionally use only
/// the public collection manager/store API so the evidence remains portable.
/// </summary>
internal static class DocumentAdmission
{
    internal static void Run(AdmissionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string databasePath = context.NewDatabasePath("document");
        string documentsPath = Path.Combine(databasePath, "documents");
        int itemCount = context.Quick ? 24 : 128;

        using (var manager = new DocumentCollectionManager(
            documentsPath,
            AdmissionContext.Options(databasePath).Kv))
        {
            manager.Create(DocumentCollectionSchema.Create(
                "events",
                indexes: [new DocumentPathIndexDefinition("category", "$.category")]));
            DocumentCollectionStore store = manager.Open("events");

            context.Measure(
                "Document",
                "insert-query",
                databasePath,
                itemCount,
                itemCount,
                () =>
                {
                    var requests = new List<DocumentWriteRequest>(itemCount);
                    for (int index = 0; index < itemCount; index++)
                    {
                        context.Check();
                        requests.Add(new DocumentWriteRequest(
                            $"event-{index:D4}",
                            $"{{\"category\":\"cat-{index % 4}\",\"value\":{index},\"tags\":[\"baseline\"]}}"));
                    }

                    DocumentWriteResult written = store.InsertMany(requests, ordered: true);
                    AdmissionContext.Require(
                        written.Committed && !written.HasErrors && written.Inserted == itemCount,
                        "Document insert baseline did not commit all rows.");

                    var query = new DocumentQuery(
                        new DocumentFieldFilter(
                            DocumentFieldRef.JsonPath("$.category"),
                            DocumentFilterOperator.Equal,
                            "cat-1"),
                        Limit: itemCount,
                        Skip: 0);
                    DocumentQueryResult result = DocumentQueryPlanner.Execute(store, store.Schema, query);
                    int expected = itemCount / 4;
                    AdmissionContext.Require(
                        result.Items.Count == expected && result.MatchedCount == expected,
                        "Document query baseline returned an unexpected row count.");
                    return new Dictionary<string, double>
                    {
                        ["inserted"] = written.Inserted,
                        ["matched"] = result.MatchedCount,
                        ["index"] = result.AccessPath is "document_index" or "document_index_prefix" ? 1 : 0,
                    };
                },
                "insert bounded documents, then query an indexed category path");

            context.Measure(
                "Document",
                "update-delete",
                databasePath,
                itemCount,
                itemCount,
                () =>
                {
                    var filter = new DocumentFieldFilter(
                        DocumentFieldRef.JsonPath("$.category"),
                        DocumentFilterOperator.Equal,
                        "cat-2");
                    var update = new DocumentUpdate(
                        Inc: new Dictionary<string, JsonElement>
                        {
                            ["$.value"] = JsonNumber(10),
                        });
                    DocumentWriteResult updated = store.UpdateManyWrite(filter, update);
                    int expected = itemCount / 4;
                    AdmissionContext.Require(
                        updated.Committed && !updated.HasErrors && updated.Modified == expected,
                        "Document update baseline did not modify all matching rows.");

                    int deleted = store.DeleteMany(["event-0000", "event-0001"]);
                    AdmissionContext.Require(deleted == 2, "Document delete baseline removed an unexpected count.");
                    return new Dictionary<string, double>
                    {
                        ["updated"] = updated.Modified,
                        ["deleted"] = deleted,
                        ["remaining"] = store.Count(),
                    };
                },
                "update indexed matches and delete two known IDs");

            context.Measure(
                "Document",
                "change-feed",
                databasePath,
                itemCount,
                itemCount,
                () =>
                {
                    const string requestId = "admission-doc-bulk";
                    var bulk = store.BulkWrite(
                        [new DocumentBulkWriteOperation(
                            DocumentBulkWriteOperationType.InsertOne,
                            Id: "bulk-event",
                            Json: "{\"category\":\"bulk\",\"value\":99}")],
                        requestId: requestId);
                    var replay = store.BulkWrite(
                        [new DocumentBulkWriteOperation(
                            DocumentBulkWriteOperationType.InsertOne,
                            Id: "bulk-event",
                            Json: "{\"category\":\"bulk\",\"value\":99}")],
                        requestId: requestId);
                    AdmissionContext.Require(bulk.Committed && bulk.Inserted == 1 && replay.Replayed && replay.Inserted == 1,
                        "Document bulk idempotency replay did not return its durable result.");
                    DocumentChangeFeedPage page = store.ReadChangeFeed(0, Math.Min(1000, itemCount * 2));
                    DocumentChangeFeedEntry metadata = page.Changes.Single(change => change.DocumentId == "bulk-event");
                    AdmissionContext.Require(metadata.Cause == DocumentChangeCauses.Bulk
                        && metadata.RequestId == requestId
                        && metadata.OperationIndex == 0,
                        "Document bulk change feed lost native batch metadata.");
                    return new Dictionary<string, double>
                    {
                        ["entries"] = page.Changes.Count,
                        ["latestSequence"] = page.LatestSequence,
                        ["hasMore"] = page.HasMore ? 1 : 0,
                    };
                },
                "read the bounded persisted change feed from sequence zero");
        }

        context.Measure(
            "Document",
            "reopen",
            databasePath,
            itemCount,
            itemCount,
            () =>
            {
                using var reopened = new DocumentCollectionManager(
                    documentsPath,
                    AdmissionContext.Options(databasePath).Kv);
                DocumentCollectionStore store = reopened.Open("events");
                int count = store.Count();
                AdmissionContext.Require(count == itemCount - 1, "Document reopen did not recover the expected count.");
                DocumentRow? row = store.Get("event-0002");
                AdmissionContext.Require(row is not null, "Document reopen lost a known document.");
                return new Dictionary<string, double>
                {
                    ["count"] = count,
                    ["recovered"] = row is null ? 0 : 1,
                    ["bulkReplay"] = reopened.Open("events").Get("bulk-event") is not null ? 1 : 0,
                };
            },
            "reopen the collection and verify persisted count and row");
    }

    private static JsonElement JsonNumber(int value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return document.RootElement.Clone();
    }
}
