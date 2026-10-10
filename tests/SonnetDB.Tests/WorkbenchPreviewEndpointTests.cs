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
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>真实 Kestrel 上验证 Preview SQL 白名单、授权和正常停止后的持久化。</summary>
public sealed class WorkbenchPreviewEndpointTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-p03-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await StartAsync();
        using var created = await _client.PostAsJsonAsync("/v1/db", new CreateDatabaseRequest("PreviewDb"), ServerJsonContext.Default.CreateDatabaseRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain("\"type\":\"error\"", await SqlAsync("CREATE TABLE \"DeviceID_Main\" (\"DeviceID\" INT, \"MixedCaseName\" STRING, PRIMARY KEY (\"DeviceID\"))"));
    }

    private async Task StartAsync()
    {
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { ["p03-admin-fixture"] = ServerRoles.Admin, ["p03-readonly-fixture"] = ServerRoles.ReadOnly },
        });
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        _client = new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()), Timeout = TimeSpan.FromSeconds(40) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "p03-admin-fixture");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await _app.StopAsync(timeout.Token);
        await _app.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<string> SqlAsync(string sql, string? action = null, IReadOnlyDictionary<string, JsonElementValue>? parameters = null, string profile = "preview-1")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/db/PreviewDb/sql")
        {
            Content = JsonContent.Create(new SqlRequest(sql, parameters), ServerJsonContext.Default.SqlRequest),
        };
        if (action is not null)
        {
            request.Headers.Add("X-SonnetDB-Workbench-Profile", profile);
            request.Headers.Add("X-SonnetDB-Workbench-Action", action);
        }
        using var response = await _client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    [Theory]
    [InlineData("DELETE FROM \"DeviceID_Main\"")]
    [InlineData("INSERT INTO \"DeviceID_Main\" VALUES (1, 'blocked')")]
    [InlineData("SHOW USERS")]
    [InlineData("SHOW TOKENS")]
    [InlineData("EXPLAIN DELETE FROM \"DeviceID_Main\"")]
    [InlineData("SELECT 1; DELETE FROM \"DeviceID_Main\"")]
    public async Task PreviewRead_WithOutOfScopeSql_DoesNotExecute(string sql)
    {
        var response = await SqlAsync(sql, "sql.read");
        Assert.Contains("error", response);
        Assert.DoesNotContain("\"recordsAffected\":1", response);
    }

    [Fact]
    public async Task PreviewRead_WithUnknownProfile_RejectsEvenAdmin()
    {
        Assert.Contains("preview_action_denied", await SqlAsync("SHOW TABLES", "sql.read", profile: "preview-unknown"));
    }

    [Fact]
    public async Task Access_WithReadOnlyHttpRole_ReportsReadWithoutWrite()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "p03-readonly-fixture");
        using var response = await _client.GetAsync("/v1/db/PreviewDb/access");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("readonly", body.RootElement.GetProperty("httpRole").GetString());
        Assert.True(body.RootElement.GetProperty("canRead").GetBoolean());
        Assert.False(body.RootElement.GetProperty("canWrite").GetBoolean());
        var denied = await SqlAsync("INSERT INTO \"DeviceID_Main\" (\"DeviceID\", \"MixedCaseName\") VALUES (@id, @name)", "relation.insert.one", Parameters(1));
        Assert.Contains("forbidden", denied);
    }

    [Fact]
    public async Task PreviewInsert_WithOneParameterizedRow_PreservesNamesAndSurvivesNormalRestart()
    {
        var inserted = await SqlAsync("INSERT INTO \"DeviceID_Main\" (\"DeviceID\", \"MixedCaseName\") VALUES (@id, @name)", "relation.insert.one", Parameters(1));
        Assert.Contains("\"recordsAffected\":1", inserted);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            await _app.StopAsync(timeout.Token);
        await _app.DisposeAsync();
        _client.Dispose();
        await StartAsync();
        var read = await SqlAsync("SELECT * FROM \"DeviceID_Main\"", "sql.read");
        Assert.Contains("DeviceID", read);
        Assert.Contains("MixedCaseName", read);
        Assert.Contains("P03 stored value", read);
    }

    [Theory]
    [InlineData("INSERT INTO \"DeviceID_Main\" (\"DeviceID\", \"MixedCaseName\") VALUES (1, 'literal')")]
    [InlineData("INSERT INTO \"DeviceID_Main\" (\"DeviceID\", \"MixedCaseName\") VALUES (@id, @name), (@id, @name)")]
    [InlineData("INSERT INTO \"DeviceID_Main\" (\"DeviceID\", \"MixedCaseName\") SELECT * FROM \"DeviceID_Main\"")]
    [InlineData("UPDATE \"DeviceID_Main\" SET \"MixedCaseName\" = 'changed' WHERE \"DeviceID\" = 1")]
    public async Task PreviewInsert_WithOtherMutation_Rejects(string sql)
    {
        Assert.Contains("preview_action_denied", await SqlAsync(sql, "relation.insert.one", Parameters(1)));
    }

    [Fact]
    public async Task PreviewRead_WithOversizedInput_RejectsBeforeExecution()
    {
        Assert.Contains("bad_request", await SqlAsync("SELECT '" + new string('x', 70_000) + "'", "sql.read"));
    }

    [Fact]
    public async Task PreviewRead_WithMoreThanWindowRows_ReportsTruncation()
    {
        var values = string.Join(',', Enumerable.Range(1, 1001).Select(static id => $"({id}, 'window')"));
        Assert.Contains("\"recordsAffected\":1001", await SqlAsync("INSERT INTO \"DeviceID_Main\" (\"DeviceID\", \"MixedCaseName\") VALUES " + values));
        var result = await SqlAsync("SELECT * FROM \"DeviceID_Main\"", "sql.read");
        Assert.Contains("\"rowCount\":1000", result);
        Assert.Contains("\"truncated\":true", result);
    }

    [Fact]
    public async Task PreviewSchema_WithTooManyColumns_RejectsWithoutStorageStatistics()
    {
        var columns = string.Join(',', Enumerable.Range(1, 1001).Select(static id => $"c{id} STRING"));
        Assert.DoesNotContain("\"type\":\"error\"", await SqlAsync("CREATE TABLE Wide (" + columns + ")"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/db/PreviewDb/schema");
        request.Headers.Add("X-SonnetDB-Workbench-Profile", "preview-1");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("preview_action_denied", await SqlAsync("SHOW TABLES", "sql.read"));
    }

    [Fact]
    public async Task PreviewSchema_WithSmallCatalog_ReturnsOriginalNamesWithoutBackupOrIndexStatistics()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/db/PreviewDb/schema");
        request.Headers.Add("X-SonnetDB-Workbench-Profile", "preview-1");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DeviceID_Main", json.RootElement.GetProperty("tables")[0].GetProperty("name").GetString());
        Assert.True(!json.RootElement.TryGetProperty("backupStatus", out var backup) || backup.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task PreviewRead_WithUnsupportedMeasurementAggregate_RejectsBeforeUnbudgetedExecution()
    {
        Assert.DoesNotContain("\"type\":\"error\"", await SqlAsync("CREATE MEASUREMENT Samples (DeviceID TAG, Reading FIELD FLOAT)"));
        var response = await SqlAsync("SELECT SUM(Reading) FROM Samples", "sql.read");
        Assert.Contains("\"error\":\"sql_error\"", response);
        Assert.DoesNotContain("\"type\":\"end\"", response);
    }

    [Fact]
    public async Task PreviewRead_WithOversizedMaterializedValue_RejectsWithoutReturningValue()
    {
        var parameters = Parameters(1);
        parameters["name"] = new(ScalarKind.String, StringValue: new string('x', 2_100_000));
        Assert.Contains("\"recordsAffected\":1", await SqlAsync("INSERT INTO \"DeviceID_Main\" (\"DeviceID\", \"MixedCaseName\") VALUES (@id, @name)", parameters: parameters));
        var response = await SqlAsync("SELECT * FROM \"DeviceID_Main\"", "sql.read");
        Assert.Contains("\"error\":\"sql_error\"", response);
        Assert.True(response.Length < 10000);
    }

    private static Dictionary<string, JsonElementValue> Parameters(long id) => new()
    {
        ["id"] = new(ScalarKind.Integer, IntegerValue: id),
        ["name"] = new(ScalarKind.String, StringValue: "P03 stored value"),
    };
}
