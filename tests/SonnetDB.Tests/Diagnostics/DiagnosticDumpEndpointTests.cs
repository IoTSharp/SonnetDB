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
using SonnetDB.Copilot;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests.Diagnostics;

/// <summary>
/// M17 #96：Diagnostic Dump 管理员权限与 metadata 边界端到端测试。
/// </summary>
public sealed class DiagnosticDumpEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "admin-dump-token";
    private const string ReadOnlyToken = "readonly-dump-token";
    private const string DatabaseName = "dump_metadata";
    private const string SensitiveValue = "must-not-appear-in-diagnostic-dump";

    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-diagnostic-dump-" + Guid.NewGuid().ToString("N"));
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
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;
        options.Observability.DiagnosticDump.Enabled = true;

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var admin = CreateClient(AdminToken);
        await CreateDatabaseAsync(admin, DatabaseName);
        await ExecuteSqlAsync(admin, DatabaseName, "CREATE MEASUREMENT dump_probe (value FIELD STRING)");
        await ExecuteSqlAsync(
            admin,
            DatabaseName,
            $"INSERT INTO dump_probe(value, time) VALUES ('{SensitiveValue}', 1776480000000)");
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
    public async Task Dump_WithAdminToken_ReturnsMetadataWithoutUserData()
    {
        var tracker = _app!.Services.GetRequiredService<CopilotInFlightTracker>();
        using var inFlight = tracker.Enter();
        using var admin = CreateClient(AdminToken);

        var response = await admin.GetAsync("/v1/diagnostics/dump");
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        var dump = JsonSerializer.Deserialize(body, ServerJsonContext.Default.DiagnosticDumpResponse);

        Assert.NotNull(dump);
        Assert.Equal(Environment.ProcessId, dump.Process.ProcessId);
        Assert.True(dump.Process.UptimeMs >= 0);
        Assert.True(dump.Process.WorkingSetBytes > 0);
        Assert.True(dump.Gc.TotalMemoryBytes > 0);
        Assert.True(dump.ThreadPool.MaxWorkerThreads > 0);
        Assert.Equal(1, dump.Copilot.InFlightSessions);

        var database = Assert.Single(dump.Databases, item => item.Name == DatabaseName);
        Assert.True(database.MemTablePointCount >= 1);
        Assert.True(database.MemTableEstimatedBytes > 0);
        Assert.True(database.SegmentCount >= 0);
        Assert.True(database.PendingFlushTasks >= 0);
        Assert.True(database.PendingCompactionTasks >= 0);
        Assert.NotEmpty(database.WalFiles);
        Assert.All(database.WalFiles, file =>
        {
            Assert.False(Path.IsPathRooted(file.FileName));
            Assert.DoesNotContain(Path.DirectorySeparatorChar, file.FileName);
            Assert.DoesNotContain(Path.AltDirectorySeparatorChar, file.FileName);
        });

        Assert.DoesNotContain(SensitiveValue, body, StringComparison.Ordinal);
        Assert.DoesNotContain("dump_probe", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dump_WithoutAdminToken_RejectsAnonymousAndReadOnlyCallers()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        using var readOnly = CreateClient(ReadOnlyToken);

        var anonymousResponse = await anonymous.GetAsync("/v1/diagnostics/dump");
        var readOnlyResponse = await readOnly.GetAsync("/v1/diagnostics/dump");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readOnlyResponse.StatusCode);
    }

    private HttpClient CreateClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
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
