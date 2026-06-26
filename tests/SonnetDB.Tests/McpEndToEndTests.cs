using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 端到端测试：启动真实 SonnetDB Server，并通过官方 MCP client 调用 `/mcp/{db}`。
/// </summary>
public sealed class McpEndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private const string _adminToken = "mcp-admin-token";
    private const string _readOnlyToken = "mcp-readonly-token";
    private const string _dbName = "mcp_e2e";
    private const string _hiddenDbName = "mcp_hidden";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
                [_readOnlyToken] = ServerRoles.ReadOnly,
            },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var admin = CreateHttpClient(_adminToken);
        await CreateDatabaseAsync(admin, _dbName);
        await CreateDatabaseAsync(admin, _hiddenDbName);
        await ExecuteSqlAsync(admin, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT, temp FIELD INT)");
        await ExecuteSqlAsync(admin,
            "INSERT INTO cpu (time, host, usage, temp) VALUES " +
            "(1000, 'h1', 0.5, 11), " +
            "(2000, 'h1', 0.7, 12), " +
            "(3000, 'h1', 0.9, 13)");
        await ExecuteSqlAsync(admin, "CREATE MEASUREMENT mem (host TAG, used FIELD INT)");

        var registry = _app!.Services.GetRequiredService<TsdbRegistry>();
        Assert.True(registry.TryGet(_dbName, out var tsdb));
        _ = tsdb.FlushNow();
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ListTools_ReturnsReadOnlyMcpTools()
    {
        await using var client = await CreateMcpClientAsync();

        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray();

        Assert.Contains("describe_measurement", names);
        Assert.Contains("explain_sql", names);
        Assert.Contains("list_databases", names);
        Assert.Contains("list_measurements", names);
        Assert.Contains("query_sql", names);
        Assert.Contains("sample_rows", names);
    }

    [Fact]
    public async Task QuerySql_WithoutLimit_AppliesRowCapAndMarksTruncated()
    {
        await using var client = await CreateMcpClientAsync();

        var result = await client.CallToolAsync(
            "query_sql",
            new Dictionary<string, object?>
            {
                ["sql"] = "SELECT time, usage FROM cpu WHERE host = 'h1'",
                ["maxRows"] = 2,
            });

        Assert.False(result.IsError.GetValueOrDefault());
        Assert.True(result.StructuredContent.HasValue);

        var structured = result.StructuredContent.Value;
        Assert.Equal(_dbName, structured.GetProperty("database").GetString());
        Assert.Equal("select", structured.GetProperty("statementType").GetString());
        Assert.Equal(2, structured.GetProperty("returnedRows").GetInt32());
        Assert.True(structured.GetProperty("truncated").GetBoolean());

        var rows = structured.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(1000L, rows[0][0].GetProperty("integerValue").GetInt64());
        Assert.Equal(0.5, rows[0][1].GetProperty("doubleValue").GetDouble());
    }

    [Fact]
    public async Task ListMeasurements_AndDescribeMeasurement_ReturnStructuredResults()
    {
        await using var client = await CreateMcpClientAsync();

        var measurements = await client.CallToolAsync(
            "list_measurements",
            new Dictionary<string, object?> { ["maxRows"] = 10 });
        Assert.False(measurements.IsError.GetValueOrDefault());
        var measurementNames = measurements.StructuredContent!.Value.GetProperty("measurements")
            .EnumerateArray()
            .Select(static element => element.GetString())
            .ToArray();
        Assert.Equal(new[] { "cpu", "mem" }, measurementNames);

        var describe = await client.CallToolAsync(
            "describe_measurement",
            new Dictionary<string, object?> { ["name"] = "cpu" });
        Assert.False(describe.IsError.GetValueOrDefault());

        var columns = describe.StructuredContent!.Value.GetProperty("columns");
        Assert.Equal(3, columns.GetArrayLength());
        Assert.Equal("host", columns[0].GetProperty("name").GetString());
        Assert.Equal("tag", columns[0].GetProperty("columnType").GetString());
        Assert.Equal("usage", columns[1].GetProperty("name").GetString());
        Assert.Equal("float64", columns[1].GetProperty("dataType").GetString());
    }

    [Fact]
    public async Task ListDatabases_WithDynamicUserGrant_ReturnsOnlyVisibleDatabases()
    {
        using var admin = CreateHttpClient(_adminToken);
        await ExecuteSqlAsync(admin, "CREATE USER dbreader WITH PASSWORD 'p'");
        await ExecuteSqlAsync(admin, $"GRANT READ ON DATABASE {_dbName} TO dbreader");

        var token = await LoginAsync("dbreader", "p");

        await using var client = await CreateMcpClientAsync(token);
        var result = await client.CallToolAsync("list_databases", new Dictionary<string, object?>());

        Assert.False(result.IsError.GetValueOrDefault());
        var databases = result.StructuredContent!.Value.GetProperty("databases")
            .EnumerateArray()
            .Select(static element => element.GetString())
            .ToArray();
        Assert.Equal(new[] { _dbName }, databases);
        Assert.Equal(_dbName, result.StructuredContent!.Value.GetProperty("currentDatabase").GetString());
    }

    [Fact]
    public async Task SampleRows_WithLimit_ReturnsStructuredRows()
    {
        await using var client = await CreateMcpClientAsync();

        var result = await client.CallToolAsync(
            "sample_rows",
            new Dictionary<string, object?>
            {
                ["measurement"] = "cpu",
                ["n"] = 2,
            });

        Assert.False(result.IsError.GetValueOrDefault());
        var structured = result.StructuredContent!.Value;
        Assert.Equal(_dbName, structured.GetProperty("database").GetString());
        Assert.Equal("cpu", structured.GetProperty("measurement").GetString());
        Assert.Equal(2, structured.GetProperty("requestedRows").GetInt32());
        Assert.Equal(2, structured.GetProperty("returnedRows").GetInt32());
        Assert.True(structured.GetProperty("truncated").GetBoolean());

        var columns = structured.GetProperty("columns").EnumerateArray().Select(static x => x.GetString()).ToArray();
        Assert.Equal(new[] { "time", "host", "usage", "temp" }, columns);
        Assert.Equal(2, structured.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task ExplainSql_WithTimeAndTagFilter_ReturnsEstimatedScanStats()
    {
        await using var client = await CreateMcpClientAsync();

        var result = await client.CallToolAsync(
            "explain_sql",
            new Dictionary<string, object?>
            {
                ["sql"] = "SELECT usage FROM cpu WHERE host = 'h1' AND time >= 1000 AND time <= 2000",
            });

        Assert.False(result.IsError.GetValueOrDefault());
        var structured = result.StructuredContent!.Value;
        Assert.Equal(_dbName, structured.GetProperty("database").GetString());
        Assert.Equal("select", structured.GetProperty("statementType").GetString());
        Assert.Equal("cpu", structured.GetProperty("measurement").GetString());
        Assert.Equal(1, structured.GetProperty("matchedSeriesCount").GetInt32());
        Assert.Equal(1, structured.GetProperty("estimatedSegmentCount").GetInt32());
        Assert.Equal(1, structured.GetProperty("estimatedBlockCount").GetInt32());
        Assert.Equal(2, structured.GetProperty("estimatedScannedRows").GetInt64());
        Assert.Equal(0, structured.GetProperty("estimatedMemTableRows").GetInt64());
        Assert.Equal(2, structured.GetProperty("estimatedSegmentRows").GetInt64());
        Assert.True(structured.GetProperty("hasTimeFilter").GetBoolean());
        Assert.Equal(1, structured.GetProperty("tagFilterCount").GetInt32());
    }

    [Fact]
    public async Task ListMeasurements_AfterSchemaChange_WithinCacheWindow_ReturnsCachedSnapshot()
    {
        await using var client = await CreateMcpClientAsync();

        var first = await client.CallToolAsync(
            "list_measurements",
            new Dictionary<string, object?> { ["maxRows"] = 10 });
        Assert.False(first.IsError.GetValueOrDefault());

        using var admin = CreateHttpClient(_adminToken);
        await ExecuteSqlAsync(admin, "CREATE MEASUREMENT disk (host TAG, used FIELD INT)");

        var second = await client.CallToolAsync(
            "list_measurements",
            new Dictionary<string, object?> { ["maxRows"] = 10 });
        Assert.False(second.IsError.GetValueOrDefault());

        var measurements = second.StructuredContent!.Value.GetProperty("measurements")
            .EnumerateArray()
            .Select(static element => element.GetString())
            .ToArray();
        Assert.Equal(new[] { "cpu", "mem" }, measurements);
    }

    [Fact]
    public async Task Resources_ReturnMeasurementSchemaAndDatabaseStats()
    {
        await using var client = await CreateMcpClientAsync();

        var resources = await client.ListResourcesAsync();
        Assert.Contains(resources, static resource => resource.Uri == "sonnetdb://schema/measurements");
        Assert.Contains(resources, static resource => resource.Uri == "sonnetdb://stats/database");

        var templates = await client.ListResourceTemplatesAsync();
        Assert.Contains(templates, static template => template.UriTemplate == "sonnetdb://schema/measurement/{name}");

        var measurements = await client.ReadResourceAsync("sonnetdb://schema/measurements");
        var measurementsText = Assert.IsType<TextResourceContents>(measurements.Contents[0]).Text;
        using (var doc = JsonDocument.Parse(measurementsText))
        {
            var names = doc.RootElement.GetProperty("measurements")
                .EnumerateArray()
                .Select(static element => element.GetString())
                .ToArray();
            Assert.Equal(new[] { "cpu", "mem" }, names);
        }

        var schema = await client.ReadResourceAsync("sonnetdb://schema/measurement/cpu");
        var schemaText = Assert.IsType<TextResourceContents>(schema.Contents[0]).Text;
        using (var doc = JsonDocument.Parse(schemaText))
        {
            Assert.Equal("cpu", doc.RootElement.GetProperty("measurement").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("columns").GetArrayLength());
        }

        var stats = await client.ReadResourceAsync("sonnetdb://stats/database");
        var statsText = Assert.IsType<TextResourceContents>(stats.Contents[0]).Text;
        using var statsDoc = JsonDocument.Parse(statsText);
        Assert.Equal(_dbName, statsDoc.RootElement.GetProperty("database").GetString());
        Assert.Equal(2, statsDoc.RootElement.GetProperty("measurementCount").GetInt32());
        Assert.True(statsDoc.RootElement.GetProperty("segmentCount").GetInt32() >= 0);
    }

    [Fact]
    public async Task DynamicUser_WithoutGrant_CannotAccessMcpEndpoint()
    {
        using var admin = CreateHttpClient(_adminToken);
        await ExecuteSqlAsync(admin, "CREATE USER nogrant WITH PASSWORD 'p'");

        var token = await LoginAsync("nogrant", "p");

        using var client = CreateHttpClient(token);
        var response = await client.GetAsync($"/mcp/{_dbName}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DynamicUser_WithReadGrant_CanUseMcpTools()
    {
        using var admin = CreateHttpClient(_adminToken);
        await ExecuteSqlAsync(admin, "CREATE USER reader WITH PASSWORD 'p'");
        await ExecuteSqlAsync(admin, $"GRANT READ ON DATABASE {_dbName} TO reader");

        var token = await LoginAsync("reader", "p");

        await using var client = await CreateMcpClientAsync(token);
        var result = await client.CallToolAsync(
            "list_measurements",
            new Dictionary<string, object?>
            {
                ["maxRows"] = 10,
            });

        Assert.False(result.IsError.GetValueOrDefault());
        var measurements = result.StructuredContent!.Value.GetProperty("measurements")
            .EnumerateArray()
            .Select(static element => element.GetString())
            .ToArray();
        Assert.Equal(new[] { "cpu", "mem" }, measurements);
    }

    private HttpClient CreateHttpClient(string? token = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<McpClient> CreateMcpClientAsync(string? token = null)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(new Uri(_baseUrl), $"/mcp/{_dbName}"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {token ?? _readOnlyToken}",
                },
            },
            LoggerFactory.Create(static _ => { }));

        return await McpClient.CreateAsync(transport);
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        using var client = CreateHttpClient();
        var response = await client.PostAsync(
            "/v1/auth/login",
            JsonContent.Create(new LoginRequest(username, password), ServerJsonContext.Default.LoginRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"登录失败：{(int)response.StatusCode} {body}");

        var login = JsonSerializer.Deserialize(body, ServerJsonContext.Default.LoginResponse);
        Assert.NotNull(login);
        return login!.Token;
    }

    private static async Task CreateDatabaseAsync(HttpClient client, string databaseName)
    {
        var response = await client.PostAsync(
            "/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(databaseName), ServerJsonContext.Default.CreateDatabaseRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"创建数据库失败：{(int)response.StatusCode} {body}");
    }

    private async Task ExecuteSqlAsync(HttpClient client, string sql)
    {
        var response = await client.PostAsync(
            $"/v1/db/{_dbName}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"执行 SQL 失败：{(int)response.StatusCode} {body}");
    }
}
