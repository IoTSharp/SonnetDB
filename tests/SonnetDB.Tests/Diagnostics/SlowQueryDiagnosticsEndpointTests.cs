using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests.Diagnostics;

/// <summary>
/// M17 #95 慢查询与 Top-N 诊断端点端到端测试。
/// </summary>
public sealed class SlowQueryDiagnosticsEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "admin-diagnostics-token";
    private const string VisibleDatabase = "diag_visible";
    private const string HiddenDatabase = "diag_hidden";

    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };
        options.Observability.SlowQueryLog.ThresholdMs = 0;
        options.Observability.SlowQueryLog.Capacity = 32;
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;

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
    public async Task Diagnostics_WithSlowQueries_ReturnsDetailsTopNAndPermissionFilteredData()
    {
        using var admin = CreateClient(AdminToken);
        await CreateDatabaseAsync(admin, VisibleDatabase);
        await CreateDatabaseAsync(admin, HiddenDatabase);
        await ExecuteSqlAsync(admin, VisibleDatabase, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");
        await ExecuteSqlAsync(admin, HiddenDatabase, "CREATE MEASUREMENT secrets (host TAG, value FIELD FLOAT)");
        await ExecuteSqlAsync(admin, VisibleDatabase, "SELECT * FROM cpu WHERE time >= 1000");
        await ExecuteSqlAsync(admin, VisibleDatabase, "select * from cpu where time >= 9000 -- same shape");
        await ExecuteSqlAsync(admin, HiddenDatabase, "SELECT * FROM secrets");
        await ExecuteSqlAsync(admin, VisibleDatabase,
            "CREATE TABLE diagnostics_audits (id INT, idempotency_key STRING, status STRING, PRIMARY KEY (id))");
        await ExecuteSqlAsync(admin, VisibleDatabase,
            "CREATE UNIQUE INDEX ux_diagnostics_audits_key ON diagnostics_audits (idempotency_key)");
        await ExecuteSqlAsync(admin, VisibleDatabase,
            "INSERT INTO diagnostics_audits (id, idempotency_key, status) VALUES (1, 'key-1', 'ready')");
        await ExecuteSqlAsync(admin, VisibleDatabase,
            "SELECT id FROM diagnostics_audits WHERE idempotency_key = 'key-1' AND status = 'ready'");

        var slowResponse = await admin.GetFromJsonAsync(
            $"/v1/diagnostics/slow-queries?database={VisibleDatabase}&limit=32",
            ServerJsonContext.Default.SlowQueryListResponse);
        Assert.NotNull(slowResponse);
        Assert.True(slowResponse.Enabled);
        Assert.Equal(32, slowResponse.Capacity);
        Assert.DoesNotContain(slowResponse.Items, static item => item.Database != VisibleDatabase);
        Assert.Contains(slowResponse.Items, static item => item.NormalizedSql.Contains("time >= ?", StringComparison.OrdinalIgnoreCase));
        var indexed = Assert.Single(
            slowResponse.Items,
            static item => item.NormalizedSql.Contains("FROM diagnostics_audits", StringComparison.Ordinal));
        Assert.Equal("secondary_index", indexed.AccessPath);
        Assert.Equal("ux_diagnostics_audits_key", indexed.IndexName);
        Assert.Equal(1, indexed.CandidateRows);
        Assert.Equal(1, indexed.ExaminedRows);
        Assert.True(indexed.AllocatedBytes >= 0);
        Assert.True(indexed.QueueWaitMs >= 0);

        var topResponse = await admin.GetFromJsonAsync(
            $"/v1/diagnostics/top-queries?database={VisibleDatabase}&limit=20",
            ServerJsonContext.Default.TopQueryListResponse);
        Assert.NotNull(topResponse);
        Assert.Equal(1024, topResponse.AggregateCapacity);
        var selectGroup = Assert.Single(
            topResponse.Items,
            static item => item.NormalizedSql.Contains("time >= ?", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, selectGroup.Count);
        Assert.Equal(2, selectGroup.LifetimeCount);
        Assert.True(selectGroup.P95Ms >= selectGroup.P50Ms);
        Assert.True(topResponse.LifetimeSampleCount >= topResponse.SampleCount);

        await ExecuteSqlAsync(admin, VisibleDatabase, "CREATE USER diag_reader WITH PASSWORD 'p'");
        await ExecuteSqlAsync(admin, VisibleDatabase, $"GRANT READ ON DATABASE {VisibleDatabase} TO diag_reader");
        var userToken = await LoginAsync("diag_reader", "p");
        using var reader = CreateClient(userToken);

        var visibleOnly = await reader.GetFromJsonAsync(
            "/v1/diagnostics/slow-queries?limit=32",
            ServerJsonContext.Default.SlowQueryListResponse);
        Assert.NotNull(visibleOnly);
        Assert.NotEmpty(visibleOnly.Items);
        Assert.All(visibleOnly.Items, static item => Assert.Equal(VisibleDatabase, item.Database));

        var forbidden = await reader.GetAsync($"/v1/diagnostics/slow-queries?database={HiddenDatabase}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Metrics_WithSqlQuery_UsesOnlyBoundedLabels()
    {
        var measurements = new ConcurrentQueue<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SonnetDB.Server"
                && instrument.Name.StartsWith("sonnetdb.sql.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.Start();

        using var admin = CreateClient(AdminToken);
        await CreateDatabaseAsync(admin, VisibleDatabase);
        await ExecuteSqlAsync(admin, VisibleDatabase, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");
        await ExecuteSqlAsync(admin, VisibleDatabase, "SELECT * FROM cpu WHERE time >= 123456");

        (string Name, KeyValuePair<string, object?>[] Tags)[] snapshot = measurements.ToArray();
        Assert.Contains(snapshot, static value => value.Name == "sonnetdb.sql.query.count");
        Assert.All(snapshot, static measurement =>
        {
            Assert.All(measurement.Tags, static tag =>
                Assert.Contains(tag.Key, new[] { "outcome", "access.path", "fallback.reason" }));
            Assert.DoesNotContain(measurement.Tags, static tag =>
                tag.Key.Contains("fingerprint", StringComparison.OrdinalIgnoreCase)
                || tag.Key.Contains("index", StringComparison.OrdinalIgnoreCase)
                || tag.Key.Contains("database", StringComparison.OrdinalIgnoreCase)
                || tag.Key.Contains("parameter", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task TopQueries_BelowSlowThreshold_AggregatesWithoutSlowSample()
    {
        var options = _app!.Services.GetRequiredService<IOptions<ServerOptions>>();
        options.Value.Observability.SlowQueryLog.ThresholdMs = int.MaxValue;

        using var admin = CreateClient(AdminToken);
        await CreateDatabaseAsync(admin, VisibleDatabase);
        await ExecuteSqlAsync(admin, VisibleDatabase, "CREATE MEASUREMENT quiet_cpu (value FIELD FLOAT)");
        await ExecuteSqlAsync(admin, VisibleDatabase, "SELECT * FROM quiet_cpu WHERE time >= 24680");

        var slowResponse = await admin.GetFromJsonAsync(
            $"/v1/diagnostics/slow-queries?database={VisibleDatabase}&limit=32",
            ServerJsonContext.Default.SlowQueryListResponse);
        Assert.NotNull(slowResponse);
        Assert.DoesNotContain(slowResponse.Items,
            static item => item.NormalizedSql.Contains("quiet_cpu", StringComparison.Ordinal));

        var topResponse = await admin.GetFromJsonAsync(
            $"/v1/diagnostics/top-queries?database={VisibleDatabase}&limit=32",
            ServerJsonContext.Default.TopQueryListResponse);
        Assert.NotNull(topResponse);
        Assert.Contains(topResponse.Items,
            static item => item.NormalizedSql.Contains("quiet_cpu", StringComparison.Ordinal));
    }

    private HttpClient CreateClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        var response = await client.PostAsync(
            "/v1/auth/login",
            JsonContent.Create(new LoginRequest(username, password), ServerJsonContext.Default.LoginRequest));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.LoginResponse);
        return login?.Token ?? throw new InvalidOperationException("登录响应缺少 token。");
    }

    private static async Task CreateDatabaseAsync(HttpClient client, string database)
    {
        var response = await client.PostAsync(
            "/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(database), ServerJsonContext.Default.CreateDatabaseRequest));
        response.EnsureSuccessStatusCode();
    }

    private static async Task ExecuteSqlAsync(HttpClient client, string database, string sql)
    {
        var response = await client.PostAsync(
            $"/v1/db/{database}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"SQL 失败：{(int)response.StatusCode} {body}");
    }
}
