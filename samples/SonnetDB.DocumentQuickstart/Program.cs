using SonnetDB.Data;
using SonnetDB.Data.Documents;

const string Collection = "device_profiles";
string connectionString = Environment.GetEnvironmentVariable("SONNETDB_CONNECTION")
    ?? "Data Source=./document-quickstart-data";

await SndbResourceInitializer.EnsureDatabaseAsync(connectionString, "Document Quickstart");
using var documents = new SndbDocumentClient(connectionString);
await documents.CreateCollectionAsync(Collection, ifNotExists: true);

var activate = new SndbDocumentUpdateBuilder()
    .Set("$.status", "online")
    .Multiply("$.score", 2)
    .Build();
var requestId = $"quickstart-{Guid.NewGuid():N}";
SndbDocumentBulkWriteOperation[] operations =
[
    SndbDocumentBulkWrites.ReplaceOne(
        "device-001",
        """{"site":"east","status":"new","score":21,"tags":["pump","critical"]}""",
        upsert: true),
    SndbDocumentBulkWrites.ReplaceOne(
        "device-002",
        """{"site":"west","status":"idle","score":9,"tags":["meter"]}""",
        upsert: true),
    SndbDocumentBulkWrites.UpdateOne(
        SndbDocumentFilters.Equal("$.site", "east"),
        activate),
    SndbDocumentBulkWrites.DeleteMany(
        SndbDocumentFilters.Equal("$.status", "retired")),
];

var first = await documents.BulkWriteAsync(Collection, operations, ordered: true, requestId);
var retry = await documents.BulkWriteAsync(Collection, operations, ordered: true, requestId);
Console.WriteLine($"bulk committed={first.Committed}, replayed-on-retry={retry.Replayed}, items={first.Items.Count}");

var updated = await documents.FindOneAndUpdateAsync(
    Collection,
    new SndbDocumentFindOneAndUpdateOptions(
        new SndbDocumentUpdateBuilder().AddToSet("$.tags", "inspected").Build(),
        Id: "device-001",
        ReturnDocument: SndbDocumentReturnDocument.After));
Console.WriteLine($"findOneAndUpdate={updated.Document?.Json}");

var filter = new SndbDocumentFilterBuilder()
    .Equal("$.status", "online")
    .GreaterThanOrEqual("$.score", 20)
    .Build();
var projection = new SndbDocumentProjectionBuilder()
    .Include("_id")
    .Include("$.site", "site")
    .Include("$.score", "score")
    .Build();
var sort = new SndbDocumentSortBuilder()
    .Descending("$.score")
    .Build();

var cursor = documents.FindCursor(
    Collection,
    new SndbDocumentFindOptions(
        Filter: filter,
        Projection: projection,
        Sort: sort,
        Limit: 100));

await foreach (var document in cursor.ReadAllAsync())
    Console.WriteLine($"{document.Id}: {document.Json}");
