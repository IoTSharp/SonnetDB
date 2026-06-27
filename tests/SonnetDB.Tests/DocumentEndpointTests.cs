using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data;
using SonnetDB.Data.Documents;
using SonnetDB.Data.Remote;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

public sealed class DocumentEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "admin-document-token";
    private const string ReadOnlyToken = "readonly-document-token";
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-document-endpoint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
                [ReadOnlyToken] = ServerRoles.ReadOnly,
            },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsJsonAsync(
            "/v1/db",
            new CreateDatabaseRequest("docapi"),
            ServerJsonContext.Default.CreateDatabaseRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DocumentApi_HttpCrudEndpoints_Work()
    {
        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/devices",
            new DocumentCollectionCreateRequest(),
            ServerJsonContext.Default.DocumentCollectionCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var first = JsonDocument.Parse("""{"site":"north","kind":"pump"}""");
        var insert = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/devices/insert-one",
            new DocumentWriteItem("dev-1", first.RootElement.Clone()),
            ServerJsonContext.Default.DocumentWriteItem);
        Assert.Equal(HttpStatusCode.OK, insert.StatusCode);

        using var second = JsonDocument.Parse("""{"site":"south","kind":"fan"}""");
        var insertMany = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/devices/insert-many",
            new DocumentInsertManyRequest([
                new DocumentWriteItem("dev-2", second.RootElement.Clone()),
            ]),
            ServerJsonContext.Default.DocumentInsertManyRequest);
        Assert.Equal(HttpStatusCode.OK, insertMany.StatusCode);

        var find = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/devices/find",
            new DocumentFindRequest(Limit: 10),
            ServerJsonContext.Default.DocumentFindRequest);
        Assert.Equal(HttpStatusCode.OK, find.StatusCode);
        var findBody = await find.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentFindResponse);
        Assert.NotNull(findBody);
        Assert.Equal(["dev-1", "dev-2"], findBody!.Documents.Select(static x => x.Id).ToArray());

        var count = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/devices/count",
            new DocumentCountRequest(),
            ServerJsonContext.Default.DocumentCountRequest);
        Assert.Equal(HttpStatusCode.OK, count.StatusCode);
        var countBody = await count.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentCountResponse);
        Assert.Equal(2, countBody!.Count);

        var distinct = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/devices/distinct",
            new DocumentDistinctRequest("$.site"),
            ServerJsonContext.Default.DocumentDistinctRequest);
        Assert.Equal(HttpStatusCode.OK, distinct.StatusCode);
        var distinctBody = await distinct.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentDistinctResponse);
        Assert.Equal(["north", "south"], distinctBody!.Values.Select(static x => x.StringValue!).Order(StringComparer.Ordinal).ToArray());

        using var updated = JsonDocument.Parse("""{"site":"north","kind":"pump","status":"ok"}""");
        var update = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/devices/update-one",
            new DocumentUpdateOneRequest("dev-1", updated.RootElement.Clone()),
            ServerJsonContext.Default.DocumentUpdateOneRequest);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updateBody = await update.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentWriteResponse);
        Assert.Equal(1, updateBody!.Modified);

        var delete = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/devices/delete-one",
            new DocumentDeleteOneRequest("dev-2"),
            ServerJsonContext.Default.DocumentDeleteOneRequest);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var deleteBody = await delete.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentWriteResponse);
        Assert.Equal(1, deleteBody!.Deleted);
    }

    [Fact]
    public async Task DocumentApi_RemoteClientAndPermissions_Work()
    {
        var connectionString = new SndbConnectionStringBuilder
        {
            DataSource = $"sonnetdb+http://{new Uri(_baseUrl!).Authority}/docapi",
            Token = AdminToken,
            Timeout = 30,
        }.ConnectionString;
        using var client = new SndbDocumentClient(connectionString);

        Assert.Equal("created", await client.CreateCollectionAsync("clientdocs"));
        await client.InsertOneAsync("clientdocs", "a", """{"category":"alpha","score":1}""");
        await client.InsertOneAsync("clientdocs", "b", """{"category":"beta","score":2}""");

        var found = await client.FindOneAsync("clientdocs", "a");
        Assert.NotNull(found);
        Assert.Contains("\"alpha\"", found!.Json);
        Assert.Equal(2, await client.CountAsync("clientdocs"));

        var update = await client.UpdateOneAsync("clientdocs", "a", """{"category":"alpha","score":3}""");
        Assert.Equal(1, update.Modified);
        var duplicate = await client.InsertOneAsync("clientdocs", "a", """{"category":"duplicate"}""");
        Assert.True(duplicate.HasErrors);
        Assert.Equal("duplicate_key", Assert.Single(duplicate.Errors!).Code);
        var deleted = await client.DeleteManyAsync("clientdocs", ["b", "missing"]);
        Assert.Equal(1, deleted.Deleted);

        var readOnlyConnectionString = new SndbConnectionStringBuilder
        {
            DataSource = $"sonnetdb+http://{new Uri(_baseUrl!).Authority}/docapi",
            Token = ReadOnlyToken,
            Timeout = 30,
        }.ConnectionString;
        using var readOnly = new SndbDocumentClient(readOnlyConnectionString);

        Assert.Single(await readOnly.FindAsync("clientdocs"));
        var ex = await Assert.ThrowsAsync<SndbServerException>(() =>
            readOnly.InsertOneAsync("clientdocs", "blocked", """{"x":1}"""));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task DocumentApi_FindFilterProjectionSort_Work()
    {
        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/querydocs",
            new DocumentCollectionCreateRequest(),
            ServerJsonContext.Default.DocumentCollectionCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var d1 = JsonDocument.Parse("""{"site":"north","score":7,"tags":["hot","critical"],"metrics":{"temp":22},"nullable":null}""");
        using var d2 = JsonDocument.Parse("""{"site":"south","score":3,"tags":["cold"],"metrics":{"temp":18}}""");
        using var d3 = JsonDocument.Parse("""{"site":"north","score":9,"tags":["hot"],"metrics":{"temp":24}}""");
        var insert = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/querydocs/insert-many",
            new DocumentInsertManyRequest([
                new DocumentWriteItem("dev-1", d1.RootElement.Clone()),
                new DocumentWriteItem("dev-2", d2.RootElement.Clone()),
                new DocumentWriteItem("dev-3", d3.RootElement.Clone()),
            ]),
            ServerJsonContext.Default.DocumentInsertManyRequest);
        Assert.Equal(HttpStatusCode.OK, insert.StatusCode);

        using var north = JsonDocument.Parse("\"north\"");
        using var minScore = JsonDocument.Parse("5");
        using var hot = JsonDocument.Parse("\"hot\"");
        var find = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/querydocs/find",
            new DocumentFindRequest(
                Limit: 10,
                Filter: new DocumentFilterContract(And: [
                    new DocumentFilterContract("$.site", "eq", north.RootElement.Clone()),
                    new DocumentFilterContract("$.score", "gte", minScore.RootElement.Clone()),
                    new DocumentFilterContract("$.tags", "contains", hot.RootElement.Clone()),
                ]),
                Projection: [
                    new DocumentProjectionContract("_id", "_id"),
                    new DocumentProjectionContract("temp", "$.metrics.temp"),
                ],
                Sort: [new DocumentSortContract("$.score", Descending: true)]),
            ServerJsonContext.Default.DocumentFindRequest);
        Assert.Equal(HttpStatusCode.OK, find.StatusCode);

        var body = await find.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentFindResponse);
        Assert.NotNull(body);
        Assert.Equal(["dev-3", "dev-1"], body!.Documents.Select(static x => x.Id).ToArray());
        Assert.Equal("""{"_id":"dev-3","temp":24}""", body.Documents[0].Document.GetRawText());
        Assert.Equal("""{"_id":"dev-1","temp":22}""", body.Documents[1].Document.GetRawText());

        var exists = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/querydocs/find",
            new DocumentFindRequest(
                Filter: new DocumentFilterContract("$.nullable", "exists"),
                Projection: [new DocumentProjectionContract("_id", "_id")]),
            ServerJsonContext.Default.DocumentFindRequest);
        Assert.Equal(HttpStatusCode.OK, exists.StatusCode);
        var existsBody = await exists.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentFindResponse);
        Assert.Equal(["dev-1"], existsBody!.Documents.Select(static x => x.Id).ToArray());
    }

    [Fact]
    public async Task DocumentApi_FindCursorPagination_ReturnsContinuationToken()
    {
        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/pagedocs",
            new DocumentCollectionCreateRequest(),
            ServerJsonContext.Default.DocumentCollectionCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var d1 = JsonDocument.Parse("""{"site":"north","score":1}""");
        using var d2 = JsonDocument.Parse("""{"site":"north","score":2}""");
        using var d3 = JsonDocument.Parse("""{"site":"south","score":3}""");
        var insert = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/pagedocs/insert-many",
            new DocumentInsertManyRequest([
                new DocumentWriteItem("dev-1", d1.RootElement.Clone()),
                new DocumentWriteItem("dev-2", d2.RootElement.Clone()),
                new DocumentWriteItem("dev-3", d3.RootElement.Clone()),
            ]),
            ServerJsonContext.Default.DocumentInsertManyRequest);
        Assert.Equal(HttpStatusCode.OK, insert.StatusCode);

        var first = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/pagedocs/find",
            new DocumentFindRequest(Limit: 2),
            ServerJsonContext.Default.DocumentFindRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentFindResponse);
        Assert.NotNull(firstBody);
        Assert.Equal(["dev-1", "dev-2"], firstBody!.Documents.Select(static x => x.Id).ToArray());
        Assert.True(firstBody.HasMore);
        Assert.NotNull(firstBody.ContinuationToken);
        Assert.NotNull(firstBody.CursorExpiresAtUtc);

        var second = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/pagedocs/find",
            new DocumentFindRequest(Limit: 2, ContinuationToken: firstBody.ContinuationToken),
            ServerJsonContext.Default.DocumentFindRequest);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentFindResponse);
        Assert.Equal(["dev-3"], secondBody!.Documents.Select(static x => x.Id).ToArray());
        Assert.False(secondBody.HasMore);

        using var north = JsonDocument.Parse("\"north\"");
        var mismatched = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/pagedocs/find",
            new DocumentFindRequest(
                Limit: 2,
                Filter: new DocumentFilterContract("$.site", "eq", north.RootElement.Clone()),
                ContinuationToken: firstBody.ContinuationToken),
            ServerJsonContext.Default.DocumentFindRequest);
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
    }

    [Fact]
    public async Task DocumentApi_AggregateEndpoint_RunsPipeline()
    {
        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/aggdocs",
            new DocumentCollectionCreateRequest(),
            ServerJsonContext.Default.DocumentCollectionCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var d1 = JsonDocument.Parse("""{"site":"north","score":7,"kind":"pump"}""");
        using var d2 = JsonDocument.Parse("""{"site":"south","score":3,"kind":"fan"}""");
        using var d3 = JsonDocument.Parse("""{"site":"north","score":9,"kind":"pump"}""");
        var insert = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/aggdocs/insert-many",
            new DocumentInsertManyRequest([
                new DocumentWriteItem("dev-1", d1.RootElement.Clone()),
                new DocumentWriteItem("dev-2", d2.RootElement.Clone()),
                new DocumentWriteItem("dev-3", d3.RootElement.Clone()),
            ]),
            ServerJsonContext.Default.DocumentInsertManyRequest);
        Assert.Equal(HttpStatusCode.OK, insert.StatusCode);

        using var minScore = JsonDocument.Parse("5");
        var aggregate = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/aggdocs/aggregate",
            new DocumentAggregateRequest([
                new DocumentAggregateStageContract(Match: new DocumentFilterContract("$.score", "gte", minScore.RootElement.Clone())),
                new DocumentAggregateStageContract(Group: new DocumentAggregateGroupContract(
                    Keys: [new DocumentAggregateGroupKeyContract("site", "$.site")],
                    Accumulators: [
                        new DocumentAggregateAccumulatorContract("count", "count"),
                        new DocumentAggregateAccumulatorContract("total", "sum", "$.score"),
                        new DocumentAggregateAccumulatorContract("firstKind", "first", "$.kind"),
                    ])),
                new DocumentAggregateStageContract(Sort: [new DocumentSortContract("$.total", Descending: true)]),
            ]),
            ServerJsonContext.Default.DocumentAggregateRequest);

        Assert.Equal(HttpStatusCode.OK, aggregate.StatusCode);
        var body = await aggregate.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentAggregateResponse);
        Assert.NotNull(body);
        Assert.Equal(1, body!.Count);
        var row = Assert.Single(body.Documents);
        Assert.Equal("north", row.GetProperty("site").GetString());
        Assert.Equal(2, row.GetProperty("count").GetInt32());
        Assert.Equal(16, row.GetProperty("total").GetInt32());
        Assert.Equal("pump", row.GetProperty("firstKind").GetString());
    }

    [Fact]
    public async Task DocumentApi_UpdateOperators_MaintainJsonAndFullTextIndexes()
    {
        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/updateindexed",
            new DocumentCollectionCreateRequest(),
            ServerJsonContext.Default.DocumentCollectionCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var first = JsonDocument.Parse("""{"site":"north","message":"legacy alarm","tags":["hot"],"score":1}""");
        var insert = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/updateindexed/insert-one",
            new DocumentWriteItem("dev-1", first.RootElement.Clone()),
            ServerJsonContext.Default.DocumentWriteItem);
        Assert.Equal(HttpStatusCode.OK, insert.StatusCode);

        var jsonIndex = await admin.PostAsJsonAsync(
            "/v1/db/docapi/sql",
            new SqlRequest("CREATE JSON INDEX idx_update_site ON updateindexed ('$.site')"),
            ServerJsonContext.Default.SqlRequest);
        Assert.Equal(HttpStatusCode.OK, jsonIndex.StatusCode);

        var fullText = await admin.PostAsJsonAsync(
            "/v1/db/docapi/sql",
            new SqlRequest("CREATE FULLTEXT INDEX ft_update_message ON updateindexed ('$.message')"),
            ServerJsonContext.Default.SqlRequest);
        Assert.Equal(HttpStatusCode.OK, fullText.StatusCode);

        using var east = JsonDocument.Parse("\"east\"");
        using var message = JsonDocument.Parse("\"fresh pump event\"");
        using var inc = JsonDocument.Parse("4");
        using var addTag = JsonDocument.Parse("\"new\"");
        var update = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/updateindexed/update-one",
            new DocumentUpdateOneRequest(
                Id: "dev-1",
                Update: new DocumentUpdateContract(
                    Set: new Dictionary<string, JsonElement>
                    {
                        ["$.site"] = east.RootElement.Clone(),
                        ["$.message"] = message.RootElement.Clone(),
                    },
                    Inc: new Dictionary<string, JsonElement> { ["$.score"] = inc.RootElement.Clone() },
                    AddToSet: new Dictionary<string, JsonElement> { ["$.tags"] = addTag.RootElement.Clone() })),
            ServerJsonContext.Default.DocumentUpdateOneRequest);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updateBody = await update.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentWriteResponse);
        Assert.Equal(1, updateBody!.Matched);
        Assert.Equal(1, updateBody.Modified);

        var indexed = await admin.PostAsJsonAsync(
            "/v1/db/docapi/sql",
            new SqlRequest("""
                SELECT id, json_value(document, '$.score') AS score
                FROM updateindexed
                WHERE json_value(document, '$.site') = 'east'
                """),
            ServerJsonContext.Default.SqlRequest);
        Assert.Equal(HttpStatusCode.OK, indexed.StatusCode);
        var indexedRows = await ReadSqlRowsAsync(indexed);
        Assert.Single(indexedRows);
        Assert.Equal("dev-1", indexedRows[0][0].GetString());
        Assert.Equal(5, indexedRows[0][1].GetInt32());

        var oldSite = await admin.PostAsJsonAsync(
            "/v1/db/docapi/sql",
            new SqlRequest("SELECT id FROM updateindexed WHERE json_value(document, '$.site') = 'north'"),
            ServerJsonContext.Default.SqlRequest);
        Assert.Equal(HttpStatusCode.OK, oldSite.StatusCode);
        Assert.Empty(await ReadSqlRowsAsync(oldSite));

        var fullTextQuery = await admin.PostAsJsonAsync(
            "/v1/db/docapi/sql",
            new SqlRequest("""
                SELECT id
                FROM updateindexed
                WHERE match(ft_update_message, '$.message', 'fresh')
                """),
            ServerJsonContext.Default.SqlRequest);
        Assert.Equal(HttpStatusCode.OK, fullTextQuery.StatusCode);
        var fullTextRows = await ReadSqlRowsAsync(fullTextQuery);
        Assert.Single(fullTextRows);
        Assert.Equal("dev-1", fullTextRows[0][0].GetString());
    }

    [Fact]
    public async Task DocumentApi_BulkWriteOrderedDuplicate_ReturnsConflictAndRollsBack()
    {
        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/bulkdocs",
            new DocumentCollectionCreateRequest(),
            ServerJsonContext.Default.DocumentCollectionCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var existing = JsonDocument.Parse("""{"site":"north"}""");
        var seed = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/bulkdocs/insert-one",
            new DocumentWriteItem("existing", existing.RootElement.Clone()),
            ServerJsonContext.Default.DocumentWriteItem);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var first = JsonDocument.Parse("""{"site":"east"}""");
        using var duplicate = JsonDocument.Parse("""{"site":"duplicate"}""");
        var insert = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/bulkdocs/insert-many",
            new DocumentInsertManyRequest([
                new DocumentWriteItem("dev-1", first.RootElement.Clone()),
                new DocumentWriteItem("existing", duplicate.RootElement.Clone()),
            ]),
            ServerJsonContext.Default.DocumentInsertManyRequest);

        Assert.Equal(HttpStatusCode.Conflict, insert.StatusCode);
        var body = await insert.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentWriteResponse);
        Assert.Equal("duplicate_key", Assert.Single(body!.Errors!).Code);

        var find = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/bulkdocs/find",
            new DocumentFindRequest(Limit: 10),
            ServerJsonContext.Default.DocumentFindRequest);
        var findBody = await find.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentFindResponse);
        Assert.Equal(["existing"], findBody!.Documents.Select(static x => x.Id).ToArray());
    }

    [Fact]
    public async Task DocumentApi_BulkWriteUnorderedDuplicate_ReturnsMultiStatusAndCommitsValidItems()
    {
        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/unordereddocs",
            new DocumentCollectionCreateRequest(),
            ServerJsonContext.Default.DocumentCollectionCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var existing = JsonDocument.Parse("""{"site":"north"}""");
        var seed = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/unordereddocs/insert-one",
            new DocumentWriteItem("existing", existing.RootElement.Clone()),
            ServerJsonContext.Default.DocumentWriteItem);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var first = JsonDocument.Parse("""{"site":"east"}""");
        using var duplicate = JsonDocument.Parse("""{"site":"duplicate"}""");
        using var second = JsonDocument.Parse("""{"site":"west"}""");
        var insert = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/unordereddocs/insert-many",
            new DocumentInsertManyRequest([
                new DocumentWriteItem("dev-1", first.RootElement.Clone()),
                new DocumentWriteItem("existing", duplicate.RootElement.Clone()),
                new DocumentWriteItem("dev-2", second.RootElement.Clone()),
            ], Ordered: false),
            ServerJsonContext.Default.DocumentInsertManyRequest);

        Assert.Equal((HttpStatusCode)207, insert.StatusCode);
        var body = await insert.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentWriteResponse);
        Assert.Equal(2, body!.Inserted);
        Assert.Equal("duplicate_key", Assert.Single(body.Errors!).Code);

        var find = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/unordereddocs/find",
            new DocumentFindRequest(Limit: 10),
            ServerJsonContext.Default.DocumentFindRequest);
        var findBody = await find.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentFindResponse);
        Assert.Equal(["dev-1", "dev-2", "existing"], findBody!.Documents.Select(static x => x.Id).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task DocumentApi_Validator_ErrorAndWarnActions_Work()
    {
        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/validateddocs",
            new DocumentCollectionCreateRequest(),
            ServerJsonContext.Default.DocumentCollectionCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var set = await admin.PutAsJsonAsync(
            "/v1/db/docapi/documents/validateddocs/validator",
            new DocumentValidatorContract([
                new DocumentValidatorRuleContract("$.site", Required: true, Type: "string"),
                new DocumentValidatorRuleContract("$.score", Type: "number", Minimum: 0, Maximum: 10),
            ]),
            ServerJsonContext.Default.DocumentValidatorContract);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var setBody = await set.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentValidatorResponse);
        Assert.Equal("updated", setBody!.Status);
        Assert.Equal("error", setBody.Validator!.ValidationAction);

        using var invalid = JsonDocument.Parse("""{"site":"north","score":99}""");
        var rejected = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/validateddocs/insert-one",
            new DocumentWriteItem("bad", invalid.RootElement.Clone()),
            ServerJsonContext.Default.DocumentWriteItem);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentWriteResponse);
        var rejectedError = Assert.Single(rejectedBody!.Errors!);
        Assert.Equal("validation_failed", rejectedError.Code);
        Assert.Equal("error", rejectedError.Severity);

        var warn = await admin.PutAsJsonAsync(
            "/v1/db/docapi/documents/validateddocs/validator",
            new DocumentValidatorContract([
                new DocumentValidatorRuleContract("$.site", Required: true, Type: "string"),
            ], ValidationAction: "warn"),
            ServerJsonContext.Default.DocumentValidatorContract);
        Assert.Equal(HttpStatusCode.OK, warn.StatusCode);

        using var warningDoc = JsonDocument.Parse("""{"score":1}""");
        var warningWrite = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/validateddocs/insert-one",
            new DocumentWriteItem("warned", warningDoc.RootElement.Clone()),
            ServerJsonContext.Default.DocumentWriteItem);
        Assert.Equal(HttpStatusCode.OK, warningWrite.StatusCode);
        var warningBody = await warningWrite.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentWriteResponse);
        var warning = Assert.Single(warningBody!.Errors!);
        Assert.Equal("validation_failed", warning.Code);
        Assert.Equal("warning", warning.Severity);
        Assert.Equal(1, warningBody.Inserted);

        var drop = await admin.DeleteAsync("/v1/db/docapi/documents/validateddocs/validator");
        Assert.Equal(HttpStatusCode.OK, drop.StatusCode);

        var createWithValidator = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/createvalidateddocs",
            new DocumentCollectionCreateRequest(Validator: new DocumentValidatorContract([
                new DocumentValidatorRuleContract("$.site", Required: true, Type: "string"),
            ])),
            ServerJsonContext.Default.DocumentCollectionCreateRequest);
        Assert.Equal(HttpStatusCode.Created, createWithValidator.StatusCode);

        using var createdInvalid = JsonDocument.Parse("""{"score":2}""");
        var createdRejected = await admin.PostAsJsonAsync(
            "/v1/db/docapi/documents/createvalidateddocs/insert-one",
            new DocumentWriteItem("bad", createdInvalid.RootElement.Clone()),
            ServerJsonContext.Default.DocumentWriteItem);
        Assert.Equal(HttpStatusCode.BadRequest, createdRejected.StatusCode);
        var createdRejectedBody = await createdRejected.Content.ReadFromJsonAsync(ServerJsonContext.Default.DocumentWriteResponse);
        Assert.Equal("validation_failed", Assert.Single(createdRejectedBody!.Errors!).Code);
    }

    private HttpClient CreateClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<List<JsonElement>> ReadSqlRowsAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, text);

        var rows = new List<JsonElement>();
        for (int i = 1; i < lines.Length - 1; i++)
        {
            using var document = JsonDocument.Parse(lines[i]);
            rows.Add(document.RootElement.Clone());
        }

        return rows;
    }
}
