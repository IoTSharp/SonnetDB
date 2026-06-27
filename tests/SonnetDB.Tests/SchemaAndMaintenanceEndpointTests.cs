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
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Sql.Execution;
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

        _app = TestServerHost.Build(options);
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
        var tableIndexes = table.GetProperty("indexes").EnumerateArray().ToArray();
        Assert.Contains(tableIndexes, index => index.GetProperty("name").GetString() == "idx_devices_site");
        var tableJsonIndex = tableIndexes.Single(index => index.GetProperty("name").GetString() == "idx_devices_metadata_site");
        Assert.Equal("$.site", tableJsonIndex.GetProperty("jsonPath").GetString());

        var collection = Assert.Single(root.GetProperty("documentCollections").EnumerateArray());
        Assert.Equal("docs", collection.GetProperty("name").GetString());
        var documentIndex = Assert.Single(collection.GetProperty("jsonIndexes").EnumerateArray());
        Assert.Equal("idx_docs_site", documentIndex.GetProperty("name").GetString());
        Assert.Equal("$.site", documentIndex.GetProperty("path").GetString());
        Assert.Equal("$.site", Assert.Single(documentIndex.GetProperty("paths").EnumerateArray()).GetString());
        Assert.False(documentIndex.GetProperty("isUnique").GetBoolean());
        Assert.Equal("ft_docs_body", Assert.Single(collection.GetProperty("fullTextIndexes").EnumerateArray()).GetProperty("name").GetString());

        var indexes = root.GetProperty("indexes").EnumerateArray().ToArray();
        Assert.Contains(indexes, index => index.GetProperty("id").GetString() == "table:devices:idx_devices_site");
        Assert.Contains(indexes, index =>
            index.GetProperty("id").GetString() == "table:devices:idx_devices_metadata_site"
            && index.GetProperty("kind").GetString() == "json_path");
        Assert.Contains(indexes, index =>
            index.GetProperty("id").GetString() == "document:docs:idx_docs_site"
            && index.GetProperty("kind").GetString() == "document");
        Assert.Contains(indexes, index => index.GetProperty("id").GetString() == "document:docs:ft_docs_body");
        var vectorIndex = indexes.Single(index => index.GetProperty("id").GetString() == "measurement:metrics:embedding");
        Assert.Equal("planned", vectorIndex.GetProperty("state").GetString());

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
            Assert.Equal("secondary", document.RootElement.GetProperty("index").GetProperty("kind").GetString());
            Assert.Equal("sync", document.RootElement.GetProperty("index").GetProperty("mode").GetString());
        }

        var tableJsonRebuild = await admin.PostAsync($"/v1/db/{dbName}/maintenance",
            JsonContent.Create(
                new MaintenanceRequest(
                    "rebuild_index",
                    TargetModel: "table",
                    TargetOwner: "devices",
                    TargetName: "idx_devices_metadata_site"),
                ServerJsonContext.Default.MaintenanceRequest));
        Assert.Equal(HttpStatusCode.OK, tableJsonRebuild.StatusCode);
        using (var document = JsonDocument.Parse(await tableJsonRebuild.Content.ReadAsStringAsync()))
        {
            var index = document.RootElement.GetProperty("index");
            Assert.Equal("table", index.GetProperty("model").GetString());
            Assert.Equal("json_path", index.GetProperty("kind").GetString());
            Assert.Equal("sync", index.GetProperty("mode").GetString());
            Assert.False(index.GetProperty("planned").GetBoolean());
        }

        var documentJsonRebuild = await admin.PostAsync($"/v1/db/{dbName}/maintenance",
            JsonContent.Create(
                new MaintenanceRequest(
                    "rebuild_index",
                    TargetModel: "document_json",
                    TargetOwner: "docs",
                    TargetName: "idx_docs_site"),
                ServerJsonContext.Default.MaintenanceRequest));
        Assert.Equal(HttpStatusCode.OK, documentJsonRebuild.StatusCode);
        using (var document = JsonDocument.Parse(await documentJsonRebuild.Content.ReadAsStringAsync()))
        {
            var index = document.RootElement.GetProperty("index");
            Assert.Equal("document", index.GetProperty("model").GetString());
            Assert.Equal("document", index.GetProperty("kind").GetString());
            Assert.Equal("sync", index.GetProperty("mode").GetString());
            Assert.False(index.GetProperty("planned").GetBoolean());
        }

        var fullTextRebuild = await admin.PostAsync($"/v1/db/{dbName}/maintenance",
            JsonContent.Create(
                new MaintenanceRequest(
                    "rebuild_index",
                    TargetModel: "document_fulltext",
                    TargetOwner: "docs",
                    TargetName: "ft_docs_body"),
                ServerJsonContext.Default.MaintenanceRequest));
        Assert.Equal(HttpStatusCode.OK, fullTextRebuild.StatusCode);
        using (var document = JsonDocument.Parse(await fullTextRebuild.Content.ReadAsStringAsync()))
        {
            var index = document.RootElement.GetProperty("index");
            Assert.Equal("document", index.GetProperty("model").GetString());
            Assert.Equal("fulltext", index.GetProperty("kind").GetString());
            Assert.Equal("sync_touch", index.GetProperty("mode").GetString());
            Assert.Equal(1, index.GetProperty("documentCount").GetInt64());
            Assert.False(index.GetProperty("planned").GetBoolean());
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
            Assert.Equal("planned", document.RootElement.GetProperty("status").GetString());
            var index = document.RootElement.GetProperty("index");
            Assert.True(index.GetProperty("planned").GetBoolean());
            Assert.Equal("planned", index.GetProperty("mode").GetString());
            Assert.Equal("vector:Hnsw", index.GetProperty("kind").GetString());
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

    [Fact]
    public async Task Maintenance_RestoreDryRun_UsesBackupManifestWithoutCopyingFiles()
    {
        using var admin = CreateClient(AdminToken);
        const string dbName = "mm_restore_dry_run";
        await CreateDatabaseAsync(admin, dbName);

        string sourcePath = Path.Combine(_dataRoot!, "backup-source", dbName);
        string backupPath = Path.Combine(_dataRoot!, "backups", dbName);
        string restoreTarget = Path.Combine(_dataRoot!, "restored", dbName);
        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = sourcePath,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Retention = new RetentionPolicy { Enabled = false },
        }))
        {
            SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, site STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "CREATE INDEX idx_devices_site ON devices (site)");
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION docs");
            SqlExecutor.Execute(db, """INSERT INTO docs (id, document) VALUES ('d1', '{"body":"pump alarm"}')""");
            SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_docs_body ON docs ('$.body') USING unicode");
            _ = new SonnetDB.Backup.BackupService().Create(db, new SonnetDB.Backup.BackupCreateOptions
            {
                DestinationDirectory = backupPath,
            });
        }

        var response = await admin.PostAsync($"/v1/db/{dbName}/maintenance",
            JsonContent.Create(
                new MaintenanceRequest(
                    "restore_dry_run",
                    BackupDirectory: backupPath,
                    RestoreTargetDirectory: restoreTarget),
                ServerJsonContext.Default.MaintenanceRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("restore_dry_run", root.GetProperty("operation").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(Directory.Exists(restoreTarget));

        var dryRun = root.GetProperty("restoreDryRun");
        Assert.True(dryRun.GetProperty("isValid").GetBoolean());
        Assert.True(dryRun.GetProperty("fileCount").GetInt32() > 0);
        Assert.True(dryRun.GetProperty("indexCount").GetInt32() >= 2);
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
            "CREATE TABLE devices (id INT, site STRING, enabled BOOL, metadata JSON, PRIMARY KEY (id))");
        await ExecuteSqlAsync(client, dbName,
            """INSERT INTO devices (id, site, enabled, metadata) VALUES (1, 'north', true, '{"site":"north","rack":"r1"}')""");
        await ExecuteSqlAsync(client, dbName,
            "CREATE INDEX idx_devices_site ON devices (site)");
        await ExecuteSqlAsync(client, dbName,
            "CREATE JSON INDEX idx_devices_metadata_site ON devices (metadata, '$.site')");
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
