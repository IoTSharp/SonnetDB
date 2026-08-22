using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Modbus;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests.Modbus;

/// <summary>
/// 验证 Modbus 管理 API 的公开合同、数据库权限与审批闭环。
/// </summary>
public sealed class ModbusManagementEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "modbus-admin-token";
    private const string ReadWriteToken = "modbus-rw-token";
    private const string ReadOnlyToken = "modbus-read-token";
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private Guid _requestId;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-modbus-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
                [ReadWriteToken] = ServerRoles.ReadWrite,
                [ReadOnlyToken] = ServerRoles.ReadOnly,
            },
        };
        _app = TestServerHost.Build(options);
        var registry = _app.Services.GetRequiredService<TsdbRegistry>();
        Assert.True(registry.TryCreate("factory", out var database));
        CreateSchema(database);
        var service = _app.Services.GetRequiredService<ModbusEndpointWriteService>();
        ModbusEndpointDefinition endpoint = database.Modbus.Catalog.TryGetEndpoint("shadow")!;
        ModbusEndpointStageResult staged = service.Stage(
            database,
            "factory",
            endpoint,
            "192.0.2.10:50200",
            transactionId: 17,
            unitId: 7,
            new ModbusEndpointWriteCommand(
                0x06,
                ModbusRegisterArea.HoldingRegister,
                0,
                [(ushort)42]));
        Assert.True(staged.Succeeded);
        _requestId = staged.RequestId;

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
            try { Directory.Delete(_dataRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task ManagementApi_ReadOverviewAdminQueueAndApprove_EnforcesPermissions()
    {
        using var readOnly = CreateClient(ReadOnlyToken);
        HttpResponseMessage overviewResponse = await readOnly.GetAsync("/v1/db/factory/modbus");
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        ModbusOverviewResponse? overview = await overviewResponse.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ModbusOverviewResponse);
        Assert.NotNull(overview);
        Assert.Single(overview!.Endpoints);
        ModbusBindingResponse binding = Assert.Single(overview.Bindings);
        Assert.False(overview.RuntimeEnabled);
        Assert.Equal("UPDATE_TABLE", binding.ApprovedWriteAction);
        Assert.Equal("READ_WRITE", Assert.Single(binding.Mappings).Access);

        HttpResponseMessage readQueue = await readOnly.GetAsync("/v1/db/factory/modbus/writes");
        Assert.Equal(HttpStatusCode.Forbidden, readQueue.StatusCode);

        using var readWrite = CreateClient(ReadWriteToken);
        HttpResponseMessage writeQueue = await readWrite.GetAsync("/v1/db/factory/modbus/writes");
        Assert.Equal(HttpStatusCode.Forbidden, writeQueue.StatusCode);
        HttpResponseMessage writeApproval = await readWrite.PostAsync(
            $"/v1/db/factory/modbus/writes/{_requestId:D}/approve",
            content: null);
        Assert.Equal(HttpStatusCode.Forbidden, writeApproval.StatusCode);

        using var admin = CreateClient(AdminToken);
        HttpResponseMessage pendingResponse = await admin.GetAsync("/v1/db/factory/modbus/writes?state=pending");
        Assert.Equal(HttpStatusCode.OK, pendingResponse.StatusCode);
        ModbusEndpointWriteListResponse? pending = await pendingResponse.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ModbusEndpointWriteListResponse);
        ModbusEndpointWriteResponse pendingWrite = Assert.Single(pending!.Items);
        Assert.Equal(_requestId, pendingWrite.RequestId);
        Assert.Equal("UPDATE_TABLE", pendingWrite.ApprovedWriteAction);

        HttpResponseMessage approveResponse = await admin.PostAsync(
            $"/v1/db/factory/modbus/writes/{_requestId:D}/approve",
            content: null);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        ModbusEndpointWriteResponse? approved = await approveResponse.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ModbusEndpointWriteResponse);
        Assert.Equal("applied", approved!.State);

        var registry = _app!.Services.GetRequiredService<TsdbRegistry>();
        Assert.True(registry.TryGet("factory", out var database));
        Assert.Equal(42L, Convert.ToInt64(database.Tables.Open("shadow_values").GetByPrimaryKey([1L])!.Values[1]));

        HttpResponseMessage auditResponse = await admin.GetAsync("/v1/db/factory/modbus/write-audit");
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        ModbusEndpointWriteListResponse? audit = await auditResponse.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ModbusEndpointWriteListResponse);
        Assert.Equal(["applied", "approval_started", "staged"], audit!.Items.Select(static item => item.EventType).ToArray());

        HttpResponseMessage sqlAuditResponse = await admin.PostAsync(
            "/v1/db/factory/sql",
            JsonContent.Create(
                new SqlRequest("SHOW MODBUS WRITE AUDIT"),
                ServerJsonContext.Default.SqlRequest));
        string sqlAudit = await sqlAuditResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, sqlAuditResponse.StatusCode);
        Assert.Contains("staged", sqlAudit, StringComparison.Ordinal);
        Assert.Contains("applied", sqlAudit, StringComparison.Ordinal);
        Assert.DoesNotContain("0x002A", sqlAudit, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient CreateClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!, UriKind.Absolute) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static void CreateSchema(SonnetDB.Engine.Tsdb database)
    {
        _ = SqlExecutor.Execute(database, """
            CREATE MODBUS ENDPOINT shadow
            WITH (
                BIND '127.0.0.1:1502',
                UNIT_ID 7,
                ADDRESSING ZERO_BASED,
                BYTE_ORDER BIG_ENDIAN,
                WORD_ORDER BIG_ENDIAN,
                ALLOWLIST ('127.0.0.0/8'),
                MAX_CONNECTIONS 4,
                WRITE_POLICY STAGED,
                ENABLED FALSE
            )
            """);
        _ = SqlExecutor.Execute(database, """
            CREATE TABLE shadow_values (
                id INT NOT NULL,
                setpoint INT EXPOSE AS MODBUS HOLDING_REGISTER(0) AS UINT16 ACCESS READ_WRITE,
                PRIMARY KEY (id)
            )
            USING MODBUS ENDPOINT shadow
            WITH (ROW KEY 1, ON_EXTERNAL_WRITE UPDATE_TABLE)
            """);
        database.Tables.Open("shadow_values").Insert([1L, 10L]);
    }
}
