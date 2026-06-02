using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

public sealed class SchemaAndMaintenanceEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "admin-test-token";
    private const string ReadOnlyToken = "ro-test-token";

    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-schema-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
                [ReadOnlyToken] = ServerRoles.ReadOnly,
            },
        };

        _app = Program.BuildApp(["--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0"], options);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();
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
    public async Task Schema_ReturnsMultimodelObjectsIndexesAndBackupStatus()
    {
        using var admin = CreateClient(AdminToken);
        const string dbName = "mm_schema";
        await CreateDatabaseAsync(admin, dbName);
        await SeedMultimodelCatalogAsync(admin, dbName);

        var response = await admin.GetAsync($"/v1/db/{dbName}/schema");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var measurements = root.GetProperty("measurements");
        Assert.Equal("metrics", measurements[0].GetProperty("name").GetString());
        var embedding = measurements[0].GetProperty("columns")
            .EnumerateArray()
            .Single(column => column.GetProperty("name").GetString() == "embedding");
        Assert.Equal(3, embedding.GetProperty("vectorDimension").GetInt32());
        Assert.Equal("Hnsw", embedding.GetProperty("vectorIndex").GetProperty("kind").GetString());

        var table = Assert.Single(root.GetProperty("tables").EnumerateArray());
        Assert.Equal("devices", table.GetProperty("name").GetString());
        Assert.Equal("idx_devices_site", Assert.Single(table.GetProperty("indexes").EnumerateArray()).GetProperty("name").GetString());

        var collection = Assert.Single(root.GetProperty("documentCollections").EnumerateArray());
        Assert.Equal("docs", collection.GetProperty("name").GetString());
        Assert.Equal("idx_docs_site", Assert.Single(collection.GetProperty("jsonIndexes").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("ft_docs_body", Assert.Single(collection.GetProperty("fullTextIndexes").EnumerateArray()).GetProperty("name").GetString());

        var indexes = root.GetProperty("indexes").EnumerateArray().ToArray();
        Assert.Contains(indexes, index => index.GetProperty("id").GetString() == "table:devices:idx_devices_site");
        Assert.Contains(indexes, index => index.GetProperty("id").GetString() == "document:docs:ft_docs_body");
        Assert.Contains(indexes, index => index.GetProperty("id").GetString() == "measurement:metrics:embedding");

        var backupStatus = root.GetProperty("backupStatus");
        Assert.True(backupStatus.GetProperty("backupCapable").GetBoolean());
        Assert.True(backupStatus.GetProperty("totalBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task Maintenance_HealthCheckAndRebuildIndex_Work()
    {
        using var admin = CreateClient(AdminToken);
        const string dbName = "mm_maint";
        await CreateDatabaseAsync(admin, dbName);
        await SeedMultimodelCatalogAsync(admin, dbName);

        var health = await admin.PostAsync($"/v1/db/{dbName}/maintenance",
            JsonContent.Create(new MaintenanceRequest("health_check"), ServerJsonContext.Default.MaintenanceRequest));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var document = JsonDocument.Parse(await health.Content.ReadAsStringAsync()))
        {
            Assert.Equal("health_check", document.RootElement.GetProperty("operation").GetString());
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        }

        var rebuild = await admin.PostAsync($"/v1/db/{dbName}/maintenance",
            JsonContent.Create(
                new MaintenanceRequest(
                    "rebuild_index",
                    TargetModel: "table",
                    TargetOwner: "devices",
                    TargetName: "idx_devices_site"),
                ServerJsonContext.Default.MaintenanceRequest));
        Assert.Equal(HttpStatusCode.OK, rebuild.StatusCode);
        using (var document = JsonDocument.Parse(await rebuild.Content.ReadAsStringAsync()))
        {
            Assert.Equal("rebuild_index", document.RootElement.GetProperty("operation").GetString());
            Assert.False(document.RootElement.GetProperty("index").GetProperty("planned").GetBoolean());
        }

        var vector = await admin.PostAsync($"/v1/db/{dbName}/maintenance",
            JsonContent.Create(
                new MaintenanceRequest(
                    "rebuild_index",
                    TargetModel: "measurement",
                    TargetOwner: "metrics",
                    TargetName: "embedding"),
                ServerJsonContext.Default.MaintenanceRequest));
        Assert.Equal(HttpStatusCode.OK, vector.StatusCode);
        using (var document = JsonDocument.Parse(await vector.Content.ReadAsStringAsync()))
        {
            Assert.True(document.RootElement.GetProperty("index").GetProperty("planned").GetBoolean());
            Assert.Equal("planned", document.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Maintenance_QualityAnalysis_ReturnsIndexLifecycleSummary()
    {
        using var admin = CreateClient(AdminToken);
        const string dbName = "mm_quality";
        await CreateDatabaseAsync(admin, dbName);
        await SeedMultimodelCatalogAsync(admin, dbName);

        var response = await admin.PostAsync($"/v1/db/{dbName}/maintenance",
            JsonContent.Create(new MaintenanceRequest("quality_analysis"), ServerJsonContext.Default.MaintenanceRequest));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("quality_analysis", root.GetProperty("operation").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());

        var quality = root.GetProperty("qualityAnalysis");
        Assert.True(quality.GetProperty("totalIndexes").GetInt32() >= 3);
        Assert.True(quality.GetProperty("rebuildableIndexes").GetInt32() >= 3);
        Assert.True(quality.GetProperty("plannedIndexes").GetInt32() >= 1);

        var indexes = quality.GetProperty("indexes").EnumerateArray().ToArray();
        Assert.Contains(indexes, index => index.GetProperty("id").GetString() == "table:devices:idx_devices_site");
        Assert.Contains(indexes, index => index.GetProperty("id").GetString() == "document:docs:ft_docs_body");
        Assert.Contains(indexes, index => index.GetProperty("id").GetString() == "measurement:metrics:embedding");
    }

    [Fact]
    public async Task Maintenance_BackupPathOperations_RequireServerAdmin()
    {
        using var admin = CreateClient(AdminToken);
        using var readOnly = CreateClient(ReadOnlyToken);
        const string dbName = "mm_backup_perm";
        await CreateDatabaseAsync(admin, dbName);

        var response = await readOnly.PostAsync($"/v1/db/{dbName}/maintenance",
            JsonContent.Create(
                new MaintenanceRequest("backup_verify", BackupDirectory: Path.Combine(Path.GetTempPath(), "missing-backup")),
                ServerJsonContext.Default.MaintenanceRequest));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private HttpClient CreateClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task CreateDatabaseAsync(HttpClient client, string databaseName)
    {
        var response = await client.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(databaseName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task SeedMultimodelCatalogAsync(HttpClient client, string dbName)
    {
        await ExecuteSqlAsync(client, dbName,
            "CREATE MEASUREMENT metrics (host TAG, value FIELD FLOAT, embedding FIELD VECTOR(3) WITH INDEX hnsw(m=4, ef=8))");
        await ExecuteSqlAsync(client, dbName,
            "INSERT INTO metrics (time, host, value, embedding) VALUES (1000, 'h1', 1.5, [1, 0, 0])");
        await ExecuteSqlAsync(client, dbName,
            "CREATE TABLE devices (id INT, site STRING, enabled BOOL, PRIMARY KEY (id))");
        await ExecuteSqlAsync(client, dbName,
            "CREATE INDEX idx_devices_site ON devices (site)");
        await ExecuteSqlAsync(client, dbName,
            "CREATE DOCUMENT COLLECTION docs");
        await ExecuteSqlAsync(client, dbName,
            """INSERT INTO docs (id, document) VALUES ('d1', '{"site":"north","body":"pump alarm"}')""");
        await ExecuteSqlAsync(client, dbName,
            "CREATE JSON INDEX idx_docs_site ON docs ('$.site')");
        await ExecuteSqlAsync(client, dbName,
            "CREATE FULLTEXT INDEX ft_docs_body ON docs ('$.body') USING unicode");
    }

    private static async Task ExecuteSqlAsync(HttpClient client, string db, string sql)
    {
        var response = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"SQL 失败：{(int)response.StatusCode} {text}");
    }
}
