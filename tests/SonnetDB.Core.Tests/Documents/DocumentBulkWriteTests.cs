using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentBulkWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-document-bulk-write-" + Guid.NewGuid().ToString("N"));

    public DocumentBulkWriteTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void BulkWrite_MixedSequentialOperations_CommitsAsOneKvVersion()
    {
        using var db = Open();
        var store = CreateCollection(db);
        long before = store.LastVersion;

        var result = store.BulkWrite(
        [
            new(DocumentBulkWriteOperationType.InsertOne, "dev-1", """{"count":1}"""),
            new(
                DocumentBulkWriteOperationType.UpdateOne,
                Id: "dev-1",
                Update: new DocumentUpdate(Inc: new Dictionary<string, JsonElement>
                {
                    ["$.count"] = JsonValue("2"),
                })),
            new(DocumentBulkWriteOperationType.DeleteOne, Id: "dev-1"),
        ], requestId: "mixed-one-version");

        Assert.True(result.Committed);
        Assert.False(result.Replayed);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, result.Modified);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(before + 1, store.LastVersion);
        Assert.Null(store.Get("dev-1"));
        Assert.Equal(3, store.LatestChangeSequence);
        Assert.All(store.ReadChangeFeed(0, 10).Changes, change => Assert.Equal(store.LastVersion, change.DocumentVersion));
    }

    [Fact]
    public void BulkWrite_OrderedFailure_CommitsNoDocumentsAndMarksOtherItemsNotAttempted()
    {
        using var db = Open();
        var store = CreateCollection(db);
        store.Insert("existing", """{"site":"north"}""");

        var result = store.BulkWrite(
        [
            new(DocumentBulkWriteOperationType.InsertOne, "dev-1", """{"site":"east"}"""),
            new(DocumentBulkWriteOperationType.InsertOne, "existing", """{"site":"duplicate"}"""),
            new(DocumentBulkWriteOperationType.InsertOne, "dev-2", """{"site":"west"}"""),
        ], ordered: true);

        Assert.False(result.Committed);
        Assert.Equal(0, result.Inserted);
        Assert.Equal(DocumentBulkWriteItemStatuses.NotAttempted, result.Items[0].Status);
        Assert.Equal(DocumentBulkWriteItemStatuses.Failed, result.Items[1].Status);
        Assert.Equal(DocumentBulkWriteItemStatuses.NotAttempted, result.Items[2].Status);
        Assert.Null(store.Get("dev-1"));
        Assert.Null(store.Get("dev-2"));
        Assert.Equal("north", JsonDocument.Parse(store.Get("existing")!.Json).RootElement.GetProperty("site").GetString());
    }

    [Fact]
    public void BulkWrite_UnorderedUniqueConflict_CommitsValidSubsetAtomically()
    {
        using var db = Open();
        var store = CreateCollection(db, new DocumentPathIndexDefinition("uq_site", "$.site", IsUnique: true));
        long before = store.LastVersion;

        var result = store.BulkWrite(
        [
            new(DocumentBulkWriteOperationType.InsertOne, "a", """{"site":"north"}"""),
            new(DocumentBulkWriteOperationType.InsertOne, "b", """{"site":"north"}"""),
            new(DocumentBulkWriteOperationType.InsertOne, "c", """{"site":"south"}"""),
        ], ordered: false);

        Assert.True(result.Committed);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(DocumentWriteErrorCodes.DuplicateKey, Assert.Single(result.Errors).Code);
        Assert.Equal(before + 1, store.LastVersion);
        Assert.NotNull(store.Get("a"));
        Assert.Null(store.Get("b"));
        Assert.NotNull(store.Get("c"));
        Assert.True(store.VerifyIndexConsistency().IsConsistent);
    }

    [Fact]
    public void BulkWrite_UpdateManyCreatesBatchUniqueConflict_RejectsWholeOrderedRequest()
    {
        using var db = Open();
        var store = CreateCollection(db, new DocumentPathIndexDefinition("uq_site", "$.site", IsUnique: true));
        store.InsertMany(
        [
            new DocumentWriteRequest("a", """{"site":"north","kind":"pump"}"""),
            new DocumentWriteRequest("b", """{"site":"south","kind":"pump"}"""),
        ]);

        var result = store.BulkWrite(
        [
            new(
                DocumentBulkWriteOperationType.UpdateMany,
                Filter: new DocumentFieldFilter(
                    DocumentFieldRef.JsonPath("$.kind"),
                    DocumentFilterOperator.Equal,
                    "pump"),
                Update: new DocumentUpdate(Set: new Dictionary<string, JsonElement>
                {
                    ["$.site"] = JsonValue("\"same\""),
                })),
        ]);

        Assert.False(result.Committed);
        Assert.Equal(DocumentWriteErrorCodes.DuplicateKey, Assert.Single(result.Errors).Code);
        Assert.Contains("north", store.Get("a")!.Json, StringComparison.Ordinal);
        Assert.Contains("south", store.Get("b")!.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void BulkWrite_IdempotencyReplaySurvivesReopenAndRejectsFingerprintMismatch()
    {
        using (var db = Open())
        {
            var store = CreateCollection(db);
            var first = store.BulkWrite(
                [new(DocumentBulkWriteOperationType.InsertOne, "a", """{"value":1}""")],
                requestId: "request-42");
            Assert.False(first.Replayed);
        }

        using (var reopened = Open())
        {
            var store = reopened.Documents.Open("docs");
            long beforeReplay = store.LastVersion;
            var replay = store.BulkWrite(
                [new(DocumentBulkWriteOperationType.InsertOne, "a", """{"value":1}""")],
                requestId: "request-42");
            Assert.True(replay.Replayed);
            Assert.Equal(1, replay.Inserted);
            Assert.Equal(beforeReplay, store.LastVersion);

            var mismatch = store.BulkWrite(
                [new(DocumentBulkWriteOperationType.InsertOne, "b", """{"value":2}""")],
                requestId: "request-42");
            Assert.False(mismatch.Committed);
            Assert.Equal(DocumentWriteErrorCodes.IdempotencyConflict, Assert.Single(mismatch.Errors).Code);
            Assert.Null(store.Get("b"));
        }
    }

    [Fact]
    public void BulkWrite_EmptyOperations_RejectsRequest()
    {
        using var db = Open();
        var store = CreateCollection(db);

        var error = Assert.Throws<ArgumentException>(() => store.BulkWrite([]));

        Assert.Contains("不能为空", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BulkWrite_IrrelevantOperationFields_ReturnsValidationErrors()
    {
        using var db = Open();
        var store = CreateCollection(db);

        var result = store.BulkWrite(
        [
            new(
                DocumentBulkWriteOperationType.InsertOne,
                "a",
                """{"value":1}""",
                Filter: new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.Equal, "a")),
            new(
                DocumentBulkWriteOperationType.UpdateOne,
                "b",
                """{"ignored":true}""",
                Update: new DocumentUpdate(Set: new Dictionary<string, JsonElement>
                {
                    ["$.value"] = JsonValue("2"),
                })),
            new(
                DocumentBulkWriteOperationType.DeleteOne,
                "c",
                Upsert: true),
            new(
                DocumentBulkWriteOperationType.ReplaceOne,
                "d",
                "{}",
                Upsert: true,
                UpsertId: "ignored"),
        ], ordered: false);

        Assert.True(result.Committed);
        Assert.Equal(4, result.Errors.Count);
        Assert.All(result.Errors, error => Assert.Equal(DocumentWriteErrorCodes.ValidationFailed, error.Code));
        Assert.All(result.Items, item => Assert.Equal(DocumentBulkWriteItemStatuses.Failed, item.Status));
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void BulkWrite_InvalidFilterOnEmptyCollection_ReturnsValidationError()
    {
        using var db = Open();
        var store = CreateCollection(db);

        var result = store.BulkWrite(
        [
            new(
                DocumentBulkWriteOperationType.DeleteMany,
                Filter: new DocumentAndFilter([])),
        ]);

        Assert.False(result.Committed);
        Assert.Equal(DocumentWriteErrorCodes.ValidationFailed, Assert.Single(result.Errors).Code);
        Assert.Equal(DocumentBulkWriteItemStatuses.Failed, Assert.Single(result.Items).Status);
    }

    [Fact]
    public void BulkWrite_DelimiterCollisionPayload_RejectsIdempotencyMismatch()
    {
        using var db = Open();
        var store = CreateCollection(db);
        const string requestId = "length-prefixed-fingerprint";

        var first = store.BulkWrite(
            [new(DocumentBulkWriteOperationType.InsertOne, "a|b", "c")],
            requestId: requestId);
        var mismatch = store.BulkWrite(
            [new(DocumentBulkWriteOperationType.InsertOne, "a", "b|c")],
            requestId: requestId);

        Assert.False(first.Committed);
        Assert.Equal(DocumentWriteErrorCodes.ValidationFailed, Assert.Single(first.Errors).Code);
        Assert.False(mismatch.Committed);
        Assert.Equal(DocumentWriteErrorCodes.IdempotencyConflict, Assert.Single(mismatch.Errors).Code);
    }

    [Fact]
    public void BulkWrite_OperationAndCanonicalPayloadBudgets_ReturnBatchTooLarge()
    {
        using var db = Open();
        var store = CreateCollection(db);
        DocumentBulkWriteOperation[] tooMany = Enumerable.Range(0, 1001)
            .Select(index => new DocumentBulkWriteOperation(
                DocumentBulkWriteOperationType.InsertOne,
                $"id-{index}",
                "{}"))
            .ToArray();

        var countResult = store.BulkWrite(tooMany);
        var payloadResult = store.BulkWrite(
            [new(
                DocumentBulkWriteOperationType.InsertOne,
                "large",
                new string('x', (16 * 1024 * 1024) + 1))]);

        Assert.False(countResult.Committed);
        Assert.Equal(DocumentWriteErrorCodes.BatchTooLarge, Assert.Single(countResult.Errors).Code);
        Assert.False(payloadResult.Committed);
        Assert.Equal(DocumentWriteErrorCodes.BatchTooLarge, Assert.Single(payloadResult.Errors).Code);
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void BulkWrite_AtomicWalBudgetRejectsBatch_ReturnsBatchTooLargeWithoutMutation()
    {
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = new KvOptions
            {
                MaxWalBytes = 32 * 1024,
                MaxOverlayEntries = int.MaxValue,
            },
        });
        var store = CreateCollection(db);
        string json = "{\"payload\":\"" + new string('x', 24 * 1024) + "\"}";

        var result = store.BulkWrite(
            [new(DocumentBulkWriteOperationType.InsertOne, "large", json)]);

        Assert.False(result.Committed);
        Assert.Equal(DocumentWriteErrorCodes.BatchTooLarge, Assert.Single(result.Errors).Code);
        Assert.Null(store.Get("large"));
    }

    [Fact]
    public void BulkWrite_UnorderedInvalidPayloads_ReturnItemErrorsAndCommitValidSubset()
    {
        using var db = Open();
        var store = CreateCollection(db);
        store.Upsert("target", """{"items":1}""");

        var result = store.BulkWrite(
        [
            new(DocumentBulkWriteOperationType.InsertOne, "bad-json", "{"),
            new(
                DocumentBulkWriteOperationType.UpdateOne,
                "target",
                Update: new DocumentUpdate(Pop: new Dictionary<string, JsonElement>
                {
                    ["$.items"] = JsonValue("1"),
                })),
            new(DocumentBulkWriteOperationType.InsertOne, "good", """{"value":3}"""),
        ], ordered: false);

        Assert.True(result.Committed);
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, error => Assert.Equal(DocumentWriteErrorCodes.ValidationFailed, error.Code));
        Assert.Equal(
            [
                DocumentBulkWriteItemStatuses.Failed,
                DocumentBulkWriteItemStatuses.Failed,
                DocumentBulkWriteItemStatuses.Succeeded,
            ],
            result.Items.Select(static item => item.Status).ToArray());
        Assert.NotNull(store.Get("good"));
        Assert.Equal("""{"items":1}""", store.Get("target")!.Json);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static DocumentCollectionStore CreateCollection(
        Tsdb db,
        params DocumentPathIndexDefinition[] indexes)
    {
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        foreach (var index in indexes)
            db.Documents.CreateIndex("docs", index);
        return db.Documents.Open("docs");
    }

    private static JsonElement JsonValue(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
