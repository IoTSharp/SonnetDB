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
    {
        var resp = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
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
    {
        var resp = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
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
