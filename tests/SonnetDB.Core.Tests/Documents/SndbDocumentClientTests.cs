using System.Text.Json;
using SonnetDB.Data;
using SonnetDB.Data.Documents;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class SndbDocumentClientTests : IDisposable
{
    private readonly string _root;

    public SndbDocumentClientTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-document-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task DocumentClient_EmbeddedCrudAndDistinct_RoundTrips()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        Assert.Equal("created", await client.CreateCollectionAsync("devices"));
        Assert.Equal("exists", await client.CreateCollectionAsync("devices"));

        var inserted = await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","kind":"pump","metrics":{"temp":21.5}}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"south","kind":"fan","metrics":{"temp":18}}"""),
        ]);
        Assert.Equal(2, inserted.Inserted);

        var one = await client.FindOneAsync("devices", "dev-1");
        Assert.NotNull(one);
        Assert.Equal("""{"site":"north","kind":"pump","metrics":{"temp":21.5}}""", one!.Json);
        Assert.True(one.Version > 0);

        var scan = await client.FindAsync("devices", new SndbDocumentFindOptions(Limit: 10));
        Assert.Equal(["dev-1", "dev-2"], scan.Select(static x => x.Id).ToArray());
        Assert.Equal(2, await client.CountAsync("devices"));

        var distinct = await client.DistinctAsync("devices", "$.site");
        Assert.Equal(["north", "south"], distinct.Values.Cast<string>().Order(StringComparer.Ordinal).ToArray());

        var updated = await client.UpdateOneAsync("devices", "dev-1", """{"site":"north","kind":"pump","metrics":{"temp":22}}""");
        Assert.Equal(1, updated.Matched);
        Assert.Equal(1, updated.Modified);
        Assert.Contains("\"temp\":22", (await client.FindOneAsync("devices", "dev-1"))!.Json);

        var deleted = await client.DeleteManyAsync("devices", ["dev-1", "missing"]);
        Assert.Equal(1, deleted.Deleted);
        Assert.Equal(["dev-2"], (await client.FindAsync("devices")).Select(static x => x.Id).ToArray());

        Assert.True(await client.DropCollectionAsync("devices"));
    }

    [Fact]
    public async Task DocumentClient_WithOpenAdoConnection_ReusesEmbeddedDatabase()
    {
        var connectionString = new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString;
        using var connection = new SndbConnection(connectionString);
        connection.Open();

        using var client = new SndbDocumentClient(connectionString);
        Assert.Equal("created", await client.CreateCollectionAsync("devices"));
        var inserted = await client.InsertOneAsync("devices", "dev-1", """{"site":"north"}""");
        Assert.Equal(1, inserted.Inserted);

        var document = await client.FindOneAsync("devices", "dev-1");
        Assert.NotNull(document);
        Assert.Equal("""{"site":"north"}""", document!.Json);
    }

    [Fact]
    public async Task DocumentClient_FindOptions_FilterProjectionSort_ReturnsProjectedDocuments()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","kind":"pump","score":7,"tags":["hot","critical"],"metrics":{"temp":22},"nullable":null}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"south","kind":"fan","score":3,"tags":["cold"],"metrics":{"temp":18}}"""),
            new KeyValuePair<string, string>("dev-3", """{"site":"north","kind":"pump","score":9,"tags":["hot"],"metrics":{"temp":24}}"""),
        ]);

        using var site = JsonDocument.Parse("\"north\"");
        using var minScore = JsonDocument.Parse("5");
        using var tag = JsonDocument.Parse("\"hot\"");
        var docs = await client.FindAsync("devices", new SndbDocumentFindOptions(
            Filter: new SndbDocumentFilter(And: [
                new SndbDocumentFilter("$.site", "eq", site.RootElement.Clone()),
                new SndbDocumentFilter("$.score", "gte", minScore.RootElement.Clone()),
                new SndbDocumentFilter("$.tags", "contains", tag.RootElement.Clone()),
            ]),
            Projection: [
                new SndbDocumentProjection("_id", "_id"),
                new SndbDocumentProjection("temp", "$.metrics.temp"),
            ],
            Sort: [new SndbDocumentSort("$.score", Descending: true)],
            Limit: 10));

        Assert.Equal(["dev-3", "dev-1"], docs.Select(static d => d.Id).ToArray());
        Assert.Equal("""{"_id":"dev-3","temp":24}""", docs[0].Json);
        Assert.Equal("""{"_id":"dev-1","temp":22}""", docs[1].Json);
    }

    [Fact]
    public async Task DocumentClient_FindPageAsync_WithCursor_ReturnsBatches()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","score":1}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"north","score":2}"""),
            new KeyValuePair<string, string>("dev-3", """{"site":"north","score":3}"""),
        ]);

        var first = await client.FindPageAsync("devices", new SndbDocumentFindOptions(Limit: 2));
        Assert.Equal(["dev-1", "dev-2"], first.Documents.Select(static doc => doc.Id).ToArray());
        Assert.True(first.HasMore);
        Assert.NotNull(first.ContinuationToken);
        Assert.NotNull(first.CursorExpiresAtUtc);

        var second = await client.FindPageAsync("devices", new SndbDocumentFindOptions(
            Limit: 2,
            ContinuationToken: first.ContinuationToken));
        Assert.Equal(["dev-3"], second.Documents.Select(static doc => doc.Id).ToArray());
        Assert.False(second.HasMore);
        Assert.Null(second.ContinuationToken);
    }

    [Fact]
    public async Task DocumentClient_FindPageAsync_WithChangedSnapshot_RejectsCursor()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","score":1}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"north","score":2}"""),
        ]);

        var first = await client.FindPageAsync("devices", new SndbDocumentFindOptions(Limit: 1));
        Assert.True(first.HasMore);

        await client.InsertOneAsync("devices", "dev-3", """{"site":"north","score":3}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FindPageAsync("devices", new SndbDocumentFindOptions(
                Limit: 1,
                ContinuationToken: first.ContinuationToken)));
        Assert.Contains("snapshot is stale", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentClient_FindOptions_ExistsDistinguishesNullFromMissing()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("null-value", """{"nullable":null}"""),
            new KeyValuePair<string, string>("missing", """{"other":1}"""),
        ]);

        var exists = await client.FindAsync("devices", new SndbDocumentFindOptions(
            Filter: new SndbDocumentFilter("$.nullable", "exists")));
        Assert.Equal(["null-value"], exists.Select(static d => d.Id).ToArray());

        using var nullValue = JsonDocument.Parse("null");
        var equalsNull = await client.FindAsync("devices", new SndbDocumentFindOptions(
            Filter: new SndbDocumentFilter("$.nullable", "eq", nullValue.RootElement.Clone())));
        Assert.Equal(["null-value"], equalsNull.Select(static d => d.Id).ToArray());
    }

    [Fact]
    public async Task DocumentClient_AggregatePipeline_GroupsUnwindsAndDistincts()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","kind":"pump","score":7,"tags":["hot","critical"]}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"south","kind":"fan","score":3,"tags":["cold"]}"""),
            new KeyValuePair<string, string>("dev-3", """{"site":"north","kind":"pump","score":9,"tags":["hot"]}"""),
        ]);

        using var minScore = JsonDocument.Parse("5");
        var grouped = await client.AggregateAsync("devices", [
            new SndbDocumentAggregateStage(Match: new SndbDocumentFilter("$.score", "gte", minScore.RootElement.Clone())),
            new SndbDocumentAggregateStage(Unwind: new SndbDocumentAggregateUnwind("$.tags", "tag")),
            new SndbDocumentAggregateStage(Group: new SndbDocumentAggregateGroup(
                Keys: [new SndbDocumentAggregateGroupKey("site", "$.site")],
                Accumulators: [
                    new SndbDocumentAggregateAccumulator("total", "sum", "$.score"),
                    new SndbDocumentAggregateAccumulator("avgScore", "avg", "$.score"),
                    new SndbDocumentAggregateAccumulator("rows", "count"),
                    new SndbDocumentAggregateAccumulator("tags", "distinct", "$.tag"),
                ])),
            new SndbDocumentAggregateStage(Sort: [new SndbDocumentSort("$.total", Descending: true)]),
        ]);

        Assert.Equal(1, grouped.Count);
        using (var doc = JsonDocument.Parse(Assert.Single(grouped.Documents)))
        {
            var root = doc.RootElement;
            Assert.Equal("north", root.GetProperty("site").GetString());
            Assert.Equal(23, root.GetProperty("total").GetInt32());
            Assert.Equal(3, root.GetProperty("rows").GetInt32());
            Assert.Equal(23d / 3d, root.GetProperty("avgScore").GetDouble(), precision: 9);
            Assert.Equal(["hot", "critical"], root.GetProperty("tags").EnumerateArray().Select(static x => x.GetString()!).ToArray());
        }

        var distinct = await client.AggregateAsync("devices", [
            new SndbDocumentAggregateStage(Distinct: new SndbDocumentAggregateDistinct("$.site", "site")),
            new SndbDocumentAggregateStage(Sort: [new SndbDocumentSort("$.site")]),
        ]);

        Assert.Equal(["north", "south"], distinct.Documents
            .Select(static json =>
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("site").GetString()!;
            })
            .ToArray());
    }

    [Fact]
    public async Task DocumentClient_UpdateOperators_ModifyDocumentsAndSupportUpsert()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","score":5,"min":10,"max":10,"name":"pump","tags":["hot"],"old":"legacy"}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"north","score":1,"tags":["cold"]}"""),
        ]);

        using var ok = JsonDocument.Parse("\"ok\"");
        using var unset = JsonDocument.Parse("true");
        using var inc = JsonDocument.Parse("2");
        using var min = JsonDocument.Parse("7");
        using var max = JsonDocument.Parse("12");
        using var pushed = JsonDocument.Parse("\"new\"");
        using var pulled = JsonDocument.Parse("\"hot\"");
        using var setValue = JsonDocument.Parse("\"new\"");
        using var currentDate = JsonDocument.Parse("true");

        var updated = await client.UpdateOneAsync(
            "devices",
            filter: null,
            new SndbDocumentUpdate(
                Set: new Dictionary<string, JsonElement> { ["$.status"] = ok.RootElement.Clone() },
                Unset: new Dictionary<string, JsonElement> { ["$.old"] = unset.RootElement.Clone() },
                Inc: new Dictionary<string, JsonElement> { ["$.score"] = inc.RootElement.Clone() },
                Min: new Dictionary<string, JsonElement> { ["$.min"] = min.RootElement.Clone() },
                Max: new Dictionary<string, JsonElement> { ["$.max"] = max.RootElement.Clone() },
                Rename: new Dictionary<string, string> { ["$.name"] = "$.kind" },
                Push: new Dictionary<string, JsonElement> { ["$.events"] = pushed.RootElement.Clone() },
                Pull: new Dictionary<string, JsonElement> { ["$.tags"] = pulled.RootElement.Clone() },
                AddToSet: new Dictionary<string, JsonElement> { ["$.labels"] = setValue.RootElement.Clone() },
                CurrentDate: new Dictionary<string, JsonElement> { ["$.updatedAt"] = currentDate.RootElement.Clone() }),
            id: "dev-1");

        Assert.Equal(1, updated.Matched);
        Assert.Equal(1, updated.Modified);
        var doc = await client.FindOneAsync("devices", "dev-1");
        Assert.NotNull(doc);
        using (var parsed = JsonDocument.Parse(doc!.Json))
        {
            var root = parsed.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.False(root.TryGetProperty("old", out _));
            Assert.Equal(7, root.GetProperty("score").GetInt32());
            Assert.Equal(7, root.GetProperty("min").GetInt32());
            Assert.Equal(12, root.GetProperty("max").GetInt32());
            Assert.Equal("pump", root.GetProperty("kind").GetString());
            Assert.Empty(root.GetProperty("tags").EnumerateArray());
            var events = root.GetProperty("events").EnumerateArray().Select(static x => x.GetString()).ToArray();
            var labels = root.GetProperty("labels").EnumerateArray().Select(static x => x.GetString()).ToArray();
            Assert.Equal("new", Assert.Single(events));
            Assert.Equal("new", Assert.Single(labels));
            Assert.True(DateTimeOffset.TryParse(root.GetProperty("updatedAt").GetString(), out _));
        }

        using var site = JsonDocument.Parse("\"north\"");
        using var one = JsonDocument.Parse("1");
        var multi = await client.UpdateManyAsync(
            "devices",
            new SndbDocumentFilter("$.site", "eq", site.RootElement.Clone()),
            new SndbDocumentUpdate(Inc: new Dictionary<string, JsonElement> { ["$.visits"] = one.RootElement.Clone() }));
        Assert.Equal(2, multi.Matched);
        Assert.Equal(2, multi.Modified);

        using var east = JsonDocument.Parse("\"east\"");
        using var created = JsonDocument.Parse("\"yes\"");
        var upsert = await client.UpdateOneAsync(
            "devices",
            new SndbDocumentFilter("$.site", "eq", east.RootElement.Clone()),
            new SndbDocumentUpdate(Set: new Dictionary<string, JsonElement> { ["$.created"] = created.RootElement.Clone() }),
            upsert: true,
            upsertId: "dev-3");
        Assert.Equal(1, upsert.Inserted);
        Assert.Equal("""{"site":"east","created":"yes"}""", (await client.FindOneAsync("devices", "dev-3"))!.Json);
    }

    [Fact]
    public async Task DocumentClient_UpdateOperators_WithConflictingPaths_Throws()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertOneAsync("devices", "dev-1", """{"metrics":{"temp":20}}""");
        using var value = JsonDocument.Parse("1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UpdateOneAsync(
                "devices",
                filter: null,
                new SndbDocumentUpdate(
                    Set: new Dictionary<string, JsonElement> { ["$.metrics"] = value.RootElement.Clone() },
                    Inc: new Dictionary<string, JsonElement> { ["$.metrics.temp"] = value.RootElement.Clone() }),
                id: "dev-1"));
        Assert.Contains("路径冲突", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentClient_InsertMany_OrderedDuplicate_RollsBackBatch()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertOneAsync("devices", "existing", """{"site":"north"}""");

        var result = await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"east"}"""),
            new KeyValuePair<string, string>("existing", """{"site":"duplicate"}"""),
        ]);

        Assert.True(result.HasErrors);
        Assert.Equal(SndbDocumentWriteErrorCodes.DuplicateKey, Assert.Single(result.Errors!).Code);
        Assert.Null(await client.FindOneAsync("devices", "dev-1"));
        Assert.Equal("""{"site":"north"}""", (await client.FindOneAsync("devices", "existing"))!.Json);
    }

    [Fact]
    public async Task DocumentClient_InsertMany_UnorderedDuplicate_CommitsValidItems()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertOneAsync("devices", "existing", """{"site":"north"}""");

        var result = await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"east"}"""),
            new KeyValuePair<string, string>("existing", """{"site":"duplicate"}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"west"}"""),
        ], ordered: false);

        Assert.Equal(2, result.Inserted);
        Assert.True(result.HasErrors);
        Assert.Equal(SndbDocumentWriteErrorCodes.DuplicateKey, Assert.Single(result.Errors!).Code);
        Assert.NotNull(await client.FindOneAsync("devices", "dev-1"));
        Assert.NotNull(await client.FindOneAsync("devices", "dev-2"));
        Assert.Equal("""{"site":"north"}""", (await client.FindOneAsync("devices", "existing"))!.Json);
    }

    [Fact]
    public async Task DocumentClient_UpdateMany_OrderedDuplicateId_RollsBackBatch()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north"}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"south"}"""),
        ]);

        var result = await client.UpdateManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"east"}"""),
            new KeyValuePair<string, string>("dev-1", """{"site":"duplicate"}"""),
        ]);

        Assert.True(result.HasErrors);
        Assert.Equal(SndbDocumentWriteErrorCodes.DuplicateKey, Assert.Single(result.Errors!).Code);
        Assert.Equal("""{"site":"north"}""", (await client.FindOneAsync("devices", "dev-1"))!.Json);
        Assert.Equal("""{"site":"south"}""", (await client.FindOneAsync("devices", "dev-2"))!.Json);
    }

    [Fact]
    public async Task DocumentClient_DeleteMany_UnorderedInvalidId_CommitsValidItems()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north"}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"south"}"""),
        ]);

        var result = await client.DeleteManyAsync("devices", ["dev-1", "", "dev-2"], ordered: false);

        Assert.Equal(2, result.Deleted);
        Assert.True(result.HasErrors);
        Assert.Equal(SndbDocumentWriteErrorCodes.ValidationFailed, Assert.Single(result.Errors!).Code);
        Assert.Empty(await client.FindAsync("devices"));
    }

    [Fact]
    public async Task DocumentClient_ValidatorError_RejectsOrderedAndAllowsUnorderedValidItems()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        var updated = await client.SetValidatorAsync("devices", new SndbDocumentValidator([
            new SndbDocumentValidatorRule("$.site", Required: true, Type: "string"),
            new SndbDocumentValidatorRule("$.score", Type: "number", Minimum: 0, Maximum: 10),
        ]));
        Assert.Equal("updated", updated.Status);
        Assert.Equal("error", updated.Validator!.ValidationAction);

        var ordered = await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","score":1}"""),
            new KeyValuePair<string, string>("bad", """{"site":"north","score":99}"""),
        ]);

        Assert.True(ordered.HasErrors);
        Assert.Equal(SndbDocumentWriteErrorCodes.ValidationFailed, Assert.Single(ordered.Errors!).Code);
        Assert.Null(await client.FindOneAsync("devices", "dev-1"));

        var unordered = await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","score":1}"""),
            new KeyValuePair<string, string>("bad", """{"site":"north","score":99}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"south","score":2}"""),
        ], ordered: false);

        Assert.Equal(2, unordered.Inserted);
        Assert.True(unordered.HasErrors);
        Assert.Equal(SndbDocumentWriteErrorCodes.ValidationFailed, Assert.Single(unordered.Errors!).Code);
        Assert.NotNull(await client.FindOneAsync("devices", "dev-1"));
        Assert.Null(await client.FindOneAsync("devices", "bad"));
        Assert.NotNull(await client.FindOneAsync("devices", "dev-2"));
    }

    [Fact]
    public async Task DocumentClient_ValidatorWarn_AllowsWriteAndReturnsWarning()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.SetValidatorAsync("devices", new SndbDocumentValidator([
            new SndbDocumentValidatorRule("$.site", Required: true, Type: "string"),
        ], ValidationAction: "warn"));

        var result = await client.InsertOneAsync("devices", "bad", """{"score":1}""");

        Assert.False(result.HasErrors);
        Assert.True(result.HasWarnings);
        var warning = Assert.Single(result.Errors!);
        Assert.Equal(SndbDocumentWriteErrorCodes.ValidationFailed, warning.Code);
        Assert.Equal(SndbDocumentWriteErrorSeverity.Warning, warning.Severity);
        Assert.NotNull(await client.FindOneAsync("devices", "bad"));

        Assert.True(await client.DropValidatorAsync("devices"));
        var afterDrop = await client.InsertOneAsync("devices", "free", """{"score":2}""");
        Assert.False(afterDrop.HasErrors);
    }
}
