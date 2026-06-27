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

    private HttpClient CreateClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
