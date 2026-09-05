using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

/// <summary>
/// 端到端测试：启动 Kestrel（随机端口）+ HttpClient 调用 + 校验 ndjson / JSON 响应。
/// 不使用 WebApplicationFactory，因为它对 AOT 友好的 Slim builder 启动模型支持有限。
/// </summary>
public sealed class ServerEndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-test-token";
    private const string _readWriteToken = "rw-test-token";
    private const string _readOnlyToken = "ro-test-token";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
                [_readWriteToken] = ServerRoles.ReadWrite,
                [_readOnlyToken] = ServerRoles.ReadOnly,
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private HttpClient CreateClient(string? token = _adminToken)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Healthz_ReturnsOk_WithoutAuth()
    {
        using var client = CreateClient(token: null);
        var resp = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
    }

    [Fact]
    public async Task HealthzLiveAndReady_ReturnStandardChecks_WithoutAuth()
    {
        using var client = CreateClient(token: null);

        var live = await client.GetAsync("/healthz/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using (var liveDocument = JsonDocument.Parse(await live.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Healthy", liveDocument.RootElement.GetProperty("status").GetString());
            Assert.Empty(liveDocument.RootElement.GetProperty("entries").EnumerateObject());
        }

        var ready = await client.GetAsync("/healthz/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var readyDocument = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        var entries = readyDocument.RootElement.GetProperty("entries");
        Assert.Equal("Healthy", entries.GetProperty("relational_table_warmup").GetProperty("status").GetString());
        Assert.Equal("Healthy", entries.GetProperty("segment_store_writable").GetProperty("status").GetString());
        Assert.Equal("Healthy", entries.GetProperty("wal_writable").GetProperty("status").GetString());
        Assert.Equal("Degraded", entries.GetProperty("copilot_provider_reachable").GetProperty("status").GetString());
        Assert.Equal("Healthy", entries.GetProperty("copilot_embedding_provider_reachable").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Metrics_ReturnsPrometheusText_WithoutAuth()
    {
        using var client = CreateClient(token: null);
        var resp = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("sonnetdb_uptime_seconds", body);
        Assert.Contains("sonnetdb_databases", body);
    }

    [Fact]
    public async Task Sql_RequiresAuth()
    {
        using var client = CreateClient(token: null);
        var resp = await client.PostAsync("/v1/db/test/sql", JsonContent.Create(new SqlRequest("SELECT 1"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CreateDatabase_RequiresAdmin()
    {
        using var client = CreateClient(_readWriteToken);
        var resp = await client.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest("denied"), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task FullFlow_Create_Insert_Select_Drop()
    {
        using var admin = CreateClient(_adminToken);
        using var ro = CreateClient(_readOnlyToken);

        // 1) CREATE DATABASE
        var dbName = "flowtest";
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // 2) CREATE MEASUREMENT + INSERT (admin)
        await ExecuteSqlAsync(admin, dbName, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        await ExecuteSqlAsync(admin, dbName, "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 0.5), (2000, 'h1', 0.7)");

        // 3) SELECT (readonly)
        var (meta, rows, end) = await ExecuteSelectAsync(ro, dbName, "SELECT time, usage FROM cpu WHERE host = 'h1'");
        Assert.Equal(new[] { "time", "usage" }, meta);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, end.RowCount);

        // 4) readonly 不能 INSERT
        var ins = await ExecuteRawAsync(ro, dbName, "INSERT INTO cpu (time, host, usage) VALUES (3000, 'h2', 0.9)");
        Assert.Contains("forbidden", ins);

        // 5) DROP DATABASE
        var drop = await admin.DeleteAsync($"/v1/db/{dbName}");
        Assert.Equal(HttpStatusCode.OK, drop.StatusCode);
    }

    [Fact]
    public async Task Sql_GeoPointColumn_RendersGeoJsonPointInNdjson()
    {
        using var admin = CreateClient(_adminToken);
        var dbName = "geosql";
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        await ExecuteSqlAsync(admin, dbName, "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");
        await ExecuteSqlAsync(admin, dbName,
            "INSERT INTO vehicle (time, device, position) VALUES (1000, 'car-1', POINT(39.9042, 116.4074))");

        var (_, rows, _) = await ExecuteSelectAsync(admin, dbName, "SELECT position FROM vehicle");
        var point = Assert.Single(rows)[0];
        Assert.Equal("Point", point.GetProperty("type").GetString());
        var coordinates = point.GetProperty("coordinates");
        Assert.Equal(116.4074, coordinates[0].GetDouble(), 6);
        Assert.Equal(39.9042, coordinates[1].GetDouble(), 6);
    }

    [Fact]
    public async Task Sql_ExplainSelect_ReturnsKeyValuePlanRows()
    {
        using var admin = CreateClient(_adminToken);
        var dbName = "explainsql";
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        await ExecuteSqlAsync(admin, dbName, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        await ExecuteSqlAsync(admin, dbName,
            "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 0.5), (2000, 'h1', 0.7), (3000, 'h2', 0.9)");

        var (columns, rows, _) = await ExecuteSelectAsync(admin, dbName,
            "EXPLAIN SELECT usage FROM cpu WHERE host = 'h1' AND time >= 1000 AND time <= 2000");

        Assert.Equal(new[] { "key", "value" }, columns);

        var values = rows.ToDictionary(
            row => row[0].GetString()!,
            row => row[1],
            StringComparer.Ordinal);

        Assert.Equal(dbName, values["database"].GetString());
        Assert.Equal("select", values["statement_type"].GetString());
        Assert.Equal("cpu", values["measurement"].GetString());
        Assert.Equal(1, values["matched_series_count"].GetInt32());
        Assert.Equal(0, values["estimated_segment_count"].GetInt32());
        Assert.Equal(0, values["estimated_block_count"].GetInt32());
        Assert.Equal(2, values["estimated_scanned_rows"].GetInt64());
        Assert.Equal(2, values["estimated_memtable_rows"].GetInt64());
        Assert.Equal(0, values["estimated_segment_rows"].GetInt64());
        Assert.True(values["has_time_filter"].GetBoolean());
        Assert.Equal(1, values["tag_filter_count"].GetInt32());
    }

    [Fact]
    public async Task Sql_RelationalTableFlow_WorksOverHttp()
    {
        using var admin = CreateClient(_adminToken);
        using var ro = CreateClient(_readOnlyToken);
        var dbName = "tableflow";
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        await ExecuteSqlAsync(admin, dbName,
            "CREATE TABLE devices (id INT, name STRING NOT NULL, enabled BOOL, PRIMARY KEY (id))");
        await ExecuteSqlAsync(admin, dbName,
            "INSERT INTO devices (id, name, enabled) VALUES (1, 'pump', TRUE), (2, 'fan', FALSE)");
        await ExecuteSqlAsync(admin, dbName,
            "UPDATE devices SET name = 'pump-2' WHERE id = 1");

        var (columns, rows, end) = await ExecuteSelectAsync(ro, dbName,
            "SELECT id, name FROM devices WHERE enabled = TRUE ORDER BY id");
        Assert.Equal(new[] { "id", "name" }, columns);
        Assert.Single(rows);
        Assert.Equal(1L, rows[0][0].GetInt64());
        Assert.Equal("pump-2", rows[0][1].GetString());
        Assert.Equal(1, end.RowCount);

        var show = await ExecuteSelectAsync(ro, dbName, "SHOW TABLES");
        Assert.Equal("devices", Assert.Single(show.Rows)[0].GetString());

        var forbidden = await ExecuteRawAsync(ro, dbName, "DELETE FROM devices WHERE id = 1");
        Assert.Contains("forbidden", forbidden, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sql_RestParameters_BindSingleAndBatchRequests()
    {
        using var admin = CreateClient(_adminToken);
        var dbName = "restparams";
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        await ExecuteSqlAsync(admin, dbName,
            "CREATE TABLE devices (id INT, name STRING, score FLOAT, enabled BOOL, PRIMARY KEY (id))");

        await ExecuteSqlAsync(admin, dbName, new SqlRequest(
            "INSERT INTO devices (id, name, score, enabled) VALUES (@id, @name, @score, @enabled)",
            new Dictionary<string, JsonElementValue>
            {
                ["id"] = new(ScalarKind.Integer, IntegerValue: 1),
                ["name"] = new(ScalarKind.String, StringValue: "pump' OR '1'='1"),
                ["score"] = new(ScalarKind.Double, DoubleValue: 7.5),
                ["enabled"] = new(ScalarKind.Boolean, BooleanValue: true),
            }));

        var (columns, rows, end) = await ExecuteSelectAsync(admin, dbName, new SqlRequest(
            "SELECT id, name, score FROM devices WHERE name = @name AND enabled = @enabled",
            new Dictionary<string, JsonElementValue>
            {
                ["name"] = new(ScalarKind.String, StringValue: "pump' OR '1'='1"),
                ["enabled"] = new(ScalarKind.Boolean, BooleanValue: true),
            }));

        Assert.Equal(new[] { "id", "name", "score" }, columns);
        Assert.Single(rows);
        Assert.Equal(1L, rows[0][0].GetInt64());
        Assert.Equal("pump' OR '1'='1", rows[0][1].GetString());
        Assert.Equal(7.5, rows[0][2].GetDouble());
        Assert.Equal(1, end.RowCount);

        var batch = await admin.PostAsync($"/v1/db/{dbName}/sql/batch",
            JsonContent.Create(new SqlBatchRequest([
                new SqlRequest("BEGIN"),
                new SqlRequest(
                    "UPDATE devices SET score = @score WHERE id = @id",
                    new Dictionary<string, JsonElementValue>
                    {
                        ["score"] = new(ScalarKind.Double, DoubleValue: 8.25),
                        ["id"] = new(ScalarKind.Integer, IntegerValue: 1),
                    }),
                new SqlRequest("COMMIT"),
            ]), ServerJsonContext.Default.SqlBatchRequest));
        var batchText = await batch.Content.ReadAsStringAsync();
        Assert.True(batch.IsSuccessStatusCode, batchText);
        Assert.DoesNotContain("\"error\"", batchText);

        var (_, updatedRows, _) = await ExecuteSelectAsync(admin, dbName, "SELECT score FROM devices WHERE id = 1");
        Assert.Equal(8.25, Assert.Single(updatedRows)[0].GetDouble());
    }

    /// <summary>同一 HTTP 批次提交的两条关系写入在新请求及 Server 重启后均可见。</summary>
    [Fact]
    public async Task SqlBatch_CommitTwoWrites_RemainsVisibleAfterServerRestart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var admin = CreateClient(_adminToken);
        admin.Timeout = TimeSpan.FromSeconds(10);
        const string database = "batchcommit";
        await CreateBatchTransactionDatabaseAsync(admin, database, deadline.Token);

        var records = await PostTransactionBatchAsync(admin, database,
        [
            "BEGIN",
            "INSERT INTO batch_rows (id, name) VALUES (1, 'first')",
            "INSERT INTO batch_rows (id, name) VALUES (2, 'second')",
            "COMMIT",
        ], deadline.Token);
        Assert.Equal(4, records.Length);
        Assert.All(records, record => Assert.Equal("end", record.GetProperty("type").GetString()));
        Assert.Equal(1, records[1].GetProperty("recordsAffected").GetInt32());
        Assert.Equal(1, records[2].GetProperty("recordsAffected").GetInt32());
        await AssertBatchTransactionRowsAsync(admin, database, [1L, 2L], deadline.Token);

        await RestartBatchTransactionServerAsync(deadline.Token);
        using var reopened = CreateClient(_readOnlyToken);
        reopened.Timeout = TimeSpan.FromSeconds(10);
        await AssertBatchTransactionRowsAsync(reopened, database, [1L, 2L], deadline.Token);
    }

    /// <summary>显式回滚不留下持久化行，Server 重启后仍为空表。</summary>
    [Fact]
    public async Task SqlBatch_RollbackTwoWrites_RemainsEmptyAfterServerRestart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var admin = CreateClient(_adminToken);
        admin.Timeout = TimeSpan.FromSeconds(10);
        const string database = "batchrollback";
        await CreateBatchTransactionDatabaseAsync(admin, database, deadline.Token);

        var records = await PostTransactionBatchAsync(admin, database,
        [
            "BEGIN",
            "INSERT INTO batch_rows (id, name) VALUES (1, 'first')",
            "INSERT INTO batch_rows (id, name) VALUES (2, 'second')",
            "ROLLBACK",
        ], deadline.Token);
        Assert.Equal(4, records.Length);
        Assert.All(records, record => Assert.Equal("end", record.GetProperty("type").GetString()));
        await AssertBatchTransactionRowsAsync(admin, database, [], deadline.Token);

        await RestartBatchTransactionServerAsync(deadline.Token);
        using var reopened = CreateClient(_readOnlyToken);
        reopened.Timeout = TimeSpan.FromSeconds(10);
        await AssertBatchTransactionRowsAsync(reopened, database, [], deadline.Token);
    }

    /// <summary>批次中途错误丢弃未提交前缀，并且不执行后续提交或写入。</summary>
    [Fact]
    public async Task SqlBatch_StatementError_DoesNotCommitPrefixOrExecuteRemainingStatements()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var admin = CreateClient(_adminToken);
        admin.Timeout = TimeSpan.FromSeconds(10);
        const string database = "batchfailure";
        await CreateBatchTransactionDatabaseAsync(admin, database, deadline.Token);

        var records = await PostTransactionBatchAsync(admin, database,
        [
            "BEGIN",
            "INSERT INTO batch_rows (id, name) VALUES (1, 'uncommitted')",
            "INSERT INTO missing_batch_table (id) VALUES (2)",
            "COMMIT",
            "INSERT INTO batch_rows (id, name) VALUES (99, 'must not execute')",
        ], deadline.Token);
        Assert.Equal(3, records.Length);
        Assert.Equal("end", records[0].GetProperty("type").GetString());
        Assert.Equal("end", records[1].GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(records[2].GetProperty("message").GetString()));
        Assert.False(records[2].TryGetProperty("recordsAffected", out _));
        await AssertBatchTransactionRowsAsync(admin, database, [], deadline.Token);

        await RestartBatchTransactionServerAsync(deadline.Token);
        using var reopened = CreateClient(_readOnlyToken);
        reopened.Timeout = TimeSpan.FromSeconds(10);
        await AssertBatchTransactionRowsAsync(reopened, database, [], deadline.Token);
    }

    private static async Task CreateBatchTransactionDatabaseAsync(HttpClient client, string database, CancellationToken cancellationToken)
    {
        using var createContent = JsonContent.Create(new CreateDatabaseRequest(database), ServerJsonContext.Default.CreateDatabaseRequest);
        using var create = await client.PostAsync("/v1/db", createContent, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var tableContent = JsonContent.Create(
            new SqlRequest("CREATE TABLE batch_rows (id INT, name STRING, PRIMARY KEY (id))"), ServerJsonContext.Default.SqlRequest);
        var records = await PostTransactionRecordsAsync(client, $"/v1/db/{database}/sql", tableContent, cancellationToken);
        Assert.Equal("end", Assert.Single(records).GetProperty("type").GetString());
    }

    private static async Task<JsonElement[]> PostTransactionBatchAsync(
        HttpClient client, string database, string[] statements, CancellationToken cancellationToken)
    {
        Assert.InRange(statements.Length, 1, 8);
        using var content = JsonContent.Create(
            new SqlBatchRequest(statements.Select(static sql => new SqlRequest(sql)).ToArray()),
            ServerJsonContext.Default.SqlBatchRequest);
        return await PostTransactionRecordsAsync(client, $"/v1/db/{database}/sql/batch", content, cancellationToken);
    }

    private static async Task<JsonElement[]> PostTransactionRecordsAsync(
        HttpClient client, string path, HttpContent content, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(path, content, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        string[] lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.InRange(lines.Length, 1, 32);
        return lines.Select(static line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }

    private static async Task AssertBatchTransactionRowsAsync(
        HttpClient client, string database, long[] expectedIds, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(new SqlRequest("SELECT id FROM batch_rows ORDER BY id"), ServerJsonContext.Default.SqlRequest);
        var records = await PostTransactionRecordsAsync(client, $"/v1/db/{database}/sql", content, cancellationToken);
        Assert.Equal("meta", records[0].GetProperty("type").GetString());
        Assert.Equal("end", records[^1].GetProperty("type").GetString());
        Assert.Equal(expectedIds.Length, records[^1].GetProperty("rowCount").GetInt32());
        Assert.Equal(expectedIds, records.Where(static record => record.ValueKind == JsonValueKind.Array)
            .Select(static record => record[0].GetInt64()).ToArray());
    }

    private async Task RestartBatchTransactionServerAsync(CancellationToken cancellationToken)
    {
        Assert.NotNull(_app);
        var options = _app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value;
        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync().AsTask().WaitAsync(cancellationToken);
        _app = TestServerHost.Build(options);
        await _app.StartAsync(cancellationToken);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _baseUrl = Assert.Single(Assert.IsAssignableFrom<IServerAddressesFeature>(addresses).Addresses);
    }

    [Fact]
    public async Task GeoTrajectory_ReturnsFeatureCollectionAndLineString()
    {
        using var admin = CreateClient(_adminToken);
        var dbName = "geotest";
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        await ExecuteSqlAsync(admin, dbName, "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");
        await ExecuteSqlAsync(admin, dbName,
            "INSERT INTO vehicle (time, device, position) VALUES " +
            "(1000, 'car-1', POINT(39.9042, 116.4074)), " +
            "(2000, 'car-1', POINT(31.2304, 121.4737)), " +
            "(3000, 'car-2', POINT(22.5431, 114.0579))");

        var points = await admin.GetAsync($"/v1/db/{dbName}/geo/vehicle/trajectory?device=car-1&from=1000&to=2000");
        Assert.Equal(HttpStatusCode.OK, points.StatusCode);
        using (var doc = JsonDocument.Parse(await points.Content.ReadAsStringAsync()))
        {
            Assert.Equal("FeatureCollection", doc.RootElement.GetProperty("type").GetString());
            var features = doc.RootElement.GetProperty("features");
            Assert.Equal(2, features.GetArrayLength());
            var coordinates = features[0].GetProperty("geometry").GetProperty("coordinates");
            Assert.Equal(116.4074, coordinates[0].GetDouble(), 6);
            Assert.Equal(39.9042, coordinates[1].GetDouble(), 6);
            Assert.Equal("car-1", features[0].GetProperty("properties").GetProperty("device").GetString());
        }

        var line = await admin.GetAsync($"/v1/db/{dbName}/geo/vehicle/trajectory?device=car-1&format=linestring");
        Assert.Equal(HttpStatusCode.OK, line.StatusCode);
        using (var doc = JsonDocument.Parse(await line.Content.ReadAsStringAsync()))
        {
            var feature = doc.RootElement.GetProperty("features")[0];
            Assert.Equal("LineString", feature.GetProperty("geometry").GetProperty("type").GetString());
            Assert.Equal(2, feature.GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
        }
    }

    [Fact]
    public async Task UnknownDatabase_Returns404()
    {
        using var client = CreateClient(_adminToken);
        var resp = await client.PostAsync("/v1/db/nonexistent/sql",
            JsonContent.Create(new SqlRequest("SELECT 1"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static async Task ExecuteSqlAsync(HttpClient client, string db, string sql)
        => await ExecuteSqlAsync(client, db, new SqlRequest(sql));

    private static async Task ExecuteSqlAsync(HttpClient client, string db, SqlRequest request)
    {
        var resp = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(request, ServerJsonContext.Default.SqlRequest));
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"SQL 失败：{(int)resp.StatusCode} {text}");
    }

    private static async Task<string> ExecuteRawAsync(HttpClient client, string db, string sql)
    {
        var resp = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        return await resp.Content.ReadAsStringAsync();
    }

    private static async Task<(string[] Columns, List<JsonElement> Rows, ResultEnd End)> ExecuteSelectAsync(
        HttpClient client, string db, string sql)
        => await ExecuteSelectAsync(client, db, new SqlRequest(sql));

    private static async Task<(string[] Columns, List<JsonElement> Rows, ResultEnd End)> ExecuteSelectAsync(
        HttpClient client, string db, SqlRequest request)
    {
        var resp = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(request, ServerJsonContext.Default.SqlRequest));
        Assert.True(resp.IsSuccessStatusCode, $"SELECT 失败：{(int)resp.StatusCode}");
        var text = await resp.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, $"ndjson 至少含 meta + end，实际 {lines.Length} 行：{text}");

        // 第一行 meta
        using var metaDoc = JsonDocument.Parse(lines[0]);
        var columns = metaDoc.RootElement.GetProperty("columns").EnumerateArray().Select(e => e.GetString()!).ToArray();

        // 中间是行
        var rows = new List<JsonElement>();
        for (int i = 1; i < lines.Length - 1; i++)
        {
            using var doc = JsonDocument.Parse(lines[i]);
            rows.Add(doc.RootElement.Clone());
        }

        // 最后一行 end
        var end = JsonSerializer.Deserialize(lines[^1], ServerJsonContext.Default.ResultEnd)!;
        Assert.Equal("end", end.Type);
        return (columns, rows, end);
    }
}
