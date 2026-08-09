using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Modbus;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests.Modbus;

/// <summary>
/// 验证受限 Modbus SQL 写的真实 REST、TCP、权限、确认和持久审计边界。
/// </summary>
public sealed class ModbusWriteRuntimeTests
{
    /// <summary>验证 dry-run、preview、一次性确认、凭据绑定、权限和审计结果。</summary>
    [Fact]
    public async Task RestSql_DryRunPreviewConfirm_EnforcesTokenPermissionAndAuditContract()
    {
        await using WriteRuntimeEnvironment environment = await WriteRuntimeEnvironment.CreateAsync();
        int initialWrites = environment.DeviceWriteCount;

        SqlResult dryRun = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "WRITE MODBUS controls SET setpoint = 42 DRY RUN");
        Assert.Equal("dry_run", dryRun.SingleValue("mode").GetString());
        Assert.Equal("validated", dryRun.SingleValue("result").GetString());
        Assert.Equal(JsonValueKind.Null, dryRun.SingleValue("confirmation_token").ValueKind);
        Assert.Equal(initialWrites, environment.DeviceWriteCount);

        SqlResult preview = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "WRITE MODBUS controls SET setpoint = 42 PREVIEW");
        string token = Assert.IsType<string>(preview.SingleValue("confirmation_token").GetString());
        Assert.Equal("0x06", preview.SingleValue("function_code").GetString());
        Assert.Equal("0x002A", preview.SingleValue("encoded_values").GetString());
        Assert.Equal(initialWrites, environment.DeviceWriteCount);

        using (HttpResponseMessage otherCredential = await environment.ExecuteAsync(
                   WriteRuntimeEnvironment.SecondAdminToken,
                   $"WRITE MODBUS controls SET setpoint = 42 CONFIRM '{token}'"))
        {
            string body = await otherCredential.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, otherCredential.StatusCode);
            Assert.Contains("modbus_write_confirmation_mismatch", body, StringComparison.Ordinal);
        }

        SqlResult confirmed = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            $"WRITE MODBUS controls SET setpoint = 42 CONFIRM '{token}'");
        Assert.Equal("succeeded", confirmed.SingleValue("result").GetString());
        Assert.Equal((ushort)42, environment.Device.GetValue(ModbusRegisterArea.HoldingRegister, 0));
        Assert.Equal(42L, (await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "SELECT setpoint FROM controls")).SingleValue("setpoint").GetInt64());

        using (HttpResponseMessage replay = await environment.ExecuteAsync(
                   WriteRuntimeEnvironment.AdminToken,
                   $"WRITE MODBUS controls SET setpoint = 42 CONFIRM '{token}'"))
        {
            string body = await replay.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.Contains("modbus_write_confirmation_invalid", body, StringComparison.Ordinal);
        }

        using (HttpResponseMessage writer = await environment.ExecuteAsync(
                   WriteRuntimeEnvironment.WriterToken,
                   "WRITE MODBUS controls SET setpoint = 43 PREVIEW"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, writer.StatusCode);
        }

        SqlResult valuePreview = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "WRITE MODBUS controls SET setpoint = 45 PREVIEW");
        string valueToken = Assert.IsType<string>(valuePreview.SingleValue("confirmation_token").GetString());
        int writesBeforeValueMismatch = environment.DeviceWriteCount;
        using (HttpResponseMessage valueMismatch = await environment.ExecuteAsync(
                   WriteRuntimeEnvironment.AdminToken,
                   $"WRITE MODBUS controls SET setpoint = 46 CONFIRM '{valueToken}'"))
        {
            string body = await valueMismatch.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, valueMismatch.StatusCode);
            Assert.Contains("modbus_write_confirmation_mismatch", body, StringComparison.Ordinal);
        }
        Assert.Equal(writesBeforeValueMismatch, environment.DeviceWriteCount);

        SqlResult stalePreview = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "WRITE MODBUS controls SET setpoint = 43 PREVIEW");
        string staleToken = Assert.IsType<string>(stalePreview.SingleValue("confirmation_token").GetString());
        await environment.ExecuteSuccessAsync(
            WriteRuntimeEnvironment.AdminToken,
            "UPDATE controls SET note = 'changed' WHERE id = 0");
        int writesBeforeStaleConfirm = environment.DeviceWriteCount;
        using (HttpResponseMessage stale = await environment.ExecuteAsync(
                   WriteRuntimeEnvironment.AdminToken,
                   $"WRITE MODBUS controls SET setpoint = 43 CONFIRM '{staleToken}'"))
        {
            string body = await stale.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.Contains("modbus_write_confirmation_mismatch", body, StringComparison.Ordinal);
        }
        Assert.Equal(writesBeforeStaleConfirm, environment.DeviceWriteCount);

        SqlResult catalogPreview = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "WRITE MODBUS controls SET setpoint = 44 PREVIEW");
        string catalogToken = Assert.IsType<string>(catalogPreview.SingleValue("confirmation_token").GetString());
        await environment.ExecuteSuccessAsync(
            WriteRuntimeEnvironment.AdminToken,
            """
            CREATE MODBUS SOURCE standby
            WITH (
                ENDPOINT '127.0.0.1:65000',
                BYTE_ORDER BIG_ENDIAN,
                WORD_ORDER BIG_ENDIAN,
                ENABLED FALSE
            )
            """);
        int writesBeforeCatalogConfirm = environment.DeviceWriteCount;
        using (HttpResponseMessage catalogChanged = await environment.ExecuteAsync(
                   WriteRuntimeEnvironment.AdminToken,
                   $"WRITE MODBUS controls SET setpoint = 44 CONFIRM '{catalogToken}'"))
        {
            string body = await catalogChanged.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, catalogChanged.StatusCode);
            Assert.Contains("modbus_write_catalog_changed", body, StringComparison.Ordinal);
        }
        Assert.Equal(writesBeforeCatalogConfirm, environment.DeviceWriteCount);

        SqlResult audit = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "SHOW MODBUS WRITE AUDIT");
        Assert.DoesNotContain("normalized_value", audit.Columns);
        Assert.DoesNotContain("encoded_values", audit.Columns);
        Assert.DoesNotContain("confirmation_token", audit.Columns);
        Assert.Contains(audit.Rows, row =>
            audit.Value(row, "event_type").GetString() == "confirm"
            && audit.Value(row, "result").GetString() == "remote_succeeded");
        Assert.Contains(audit.Rows, row =>
            audit.Value(row, "result").GetString() == "failed"
            && audit.Value(row, "error_code").GetString() == "modbus_write_catalog_changed");

        using HttpResponseMessage writerAudit = await environment.ExecuteAsync(
            WriteRuntimeEnvironment.WriterToken,
            "SHOW MODBUS WRITE AUDIT");
        Assert.Equal(HttpStatusCode.Forbidden, writerAudit.StatusCode);
    }

    /// <summary>验证设备异常响应不会改写 LATEST 行或产生远端成功审计。</summary>
    [Fact]
    public async Task RestSql_ConfirmWithDeviceException_DoesNotChangeLatestRowOrReportSuccess()
    {
        await using WriteRuntimeEnvironment environment = await WriteRuntimeEnvironment.CreateAsync();
        SqlResult preview = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "WRITE MODBUS controls SET setpoint = 99 PREVIEW");
        string token = Assert.IsType<string>(preview.SingleValue("confirmation_token").GetString());
        environment.Device.WriteExceptionCode = 0x02;

        using (HttpResponseMessage failed = await environment.ExecuteAsync(
                   WriteRuntimeEnvironment.AdminToken,
                   $"WRITE MODBUS controls SET setpoint = 99 CONFIRM '{token}'"))
        {
            string body = await failed.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
            Assert.Contains("device_exception_02", body, StringComparison.Ordinal);
        }

        Assert.Equal((ushort)7, environment.Device.GetValue(ModbusRegisterArea.HoldingRegister, 0));
        Assert.Equal(7L, (await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "SELECT setpoint FROM controls")).SingleValue("setpoint").GetInt64());

        SqlResult audit = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "SHOW MODBUS WRITE AUDIT");
        Assert.Contains(audit.Rows, row =>
            audit.Value(row, "result").GetString() == "failed"
            && audit.Value(row, "error_code").GetString() == "device_exception_02");
        Assert.DoesNotContain(audit.Rows, row =>
            audit.Value(row, "result").GetString() == "remote_succeeded");
    }

    /// <summary>验证 started 审计无法落盘时不会发出设备写请求。</summary>
    [Fact]
    public async Task RestSql_WhenStartedAuditCannotPersist_FailsClosedBeforeDeviceWrite()
    {
        var auditStore = new FailingModbusWriteAuditStore(failOnAppend: 2);
        await using WriteRuntimeEnvironment environment = await WriteRuntimeEnvironment.CreateAsync(auditStore);
        SqlResult preview = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "WRITE MODBUS controls SET setpoint = 88 PREVIEW");
        string token = Assert.IsType<string>(preview.SingleValue("confirmation_token").GetString());
        int writesBeforeConfirm = environment.DeviceWriteCount;

        using (HttpResponseMessage failed = await environment.ExecuteAsync(
                   WriteRuntimeEnvironment.AdminToken,
                   $"WRITE MODBUS controls SET setpoint = 88 CONFIRM '{token}'"))
        {
            string body = await failed.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Contains("modbus_write_audit_unavailable", body, StringComparison.Ordinal);
        }

        Assert.Equal(writesBeforeConfirm, environment.DeviceWriteCount);
        Assert.Equal((ushort)7, environment.Device.GetValue(ModbusRegisterArea.HoldingRegister, 0));
        Assert.Equal(7L, (await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "SELECT setpoint FROM controls")).SingleValue("setpoint").GetInt64());
    }

    /// <summary>验证一次性确认令牌超过五分钟后失效并写入失败审计。</summary>
    [Fact]
    public async Task RestSql_ConfirmAfterTokenExpiry_RejectsBeforeDeviceWriteAndAuditsFailure()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 9, 1, 2, 3, TimeSpan.Zero));
        await using WriteRuntimeEnvironment environment = await WriteRuntimeEnvironment.CreateAsync(
            timeProvider: timeProvider);
        SqlResult preview = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "WRITE MODBUS controls SET setpoint = 77 PREVIEW");
        string token = Assert.IsType<string>(preview.SingleValue("confirmation_token").GetString());
        int writesBeforeConfirm = environment.DeviceWriteCount;
        timeProvider.Advance(TimeSpan.FromMinutes(6));

        using (HttpResponseMessage expired = await environment.ExecuteAsync(
                   WriteRuntimeEnvironment.AdminToken,
                   $"WRITE MODBUS controls SET setpoint = 77 CONFIRM '{token}'"))
        {
            string body = await expired.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
            Assert.Contains("modbus_write_confirmation_expired", body, StringComparison.Ordinal);
        }

        Assert.Equal(writesBeforeConfirm, environment.DeviceWriteCount);
        SqlResult audit = await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "SHOW MODBUS WRITE AUDIT");
        Assert.Contains(audit.Rows, row =>
            audit.Value(row, "result").GetString() == "failed"
            && audit.Value(row, "error_code").GetString() == "modbus_write_confirmation_expired");
    }

    /// <summary>验证确认在等待 source 互斥锁时取消会消费令牌、记录失败且不联网。</summary>
    [Fact]
    public async Task Service_ConfirmCancelledWhileWaitingForSourceLock_AuditsFailureWithoutDeviceWrite()
    {
        await using WriteRuntimeEnvironment environment = await WriteRuntimeEnvironment.CreateAsync();
        var service = environment.Services.GetRequiredService<ModbusWriteService>();
        var coordinator = environment.Services.GetRequiredService<ModbusSourceOperationCoordinator>();
        var registry = environment.Services.GetRequiredService<TsdbRegistry>();
        Assert.True(registry.TryGet("factory", out Tsdb database));

        var previewStatement = Assert.IsType<WriteModbusStatement>(SqlParser.Parse(
            "WRITE MODBUS controls SET setpoint = 78 PREVIEW"));
        SelectExecutionResult preview = await service.ExecuteAsync(
            database,
            "factory",
            previewStatement,
            "test-principal",
            canWrite: true,
            canControl: true,
            CancellationToken.None);
        string token = Assert.IsType<string>(Assert.Single(preview.Rows)[14]);
        int writesBeforeConfirm = environment.DeviceWriteCount;

        await using ModbusSourceOperationCoordinator.Lease heldLease =
            await coordinator.AcquireAsync("factory", "plc", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var confirmStatement = Assert.IsType<WriteModbusStatement>(SqlParser.Parse(
            $"WRITE MODBUS controls SET setpoint = 78 CONFIRM '{token}'"));
        Task<SelectExecutionResult> confirmTask = service.ExecuteAsync(
            database,
            "factory",
            confirmStatement,
            "test-principal",
            canWrite: true,
            canControl: true,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmTask);
        Assert.Equal(writesBeforeConfirm, environment.DeviceWriteCount);
        SelectExecutionResult audit = service.ShowAudit("factory");
        Assert.Contains(audit.Rows, row =>
            row[13] as string == "failed"
            && row[14] as string == "modbus_write_cancelled");
    }

    /// <summary>验证损坏的 Modbus 审计只关闭控制写，不影响无关 SQL 查询。</summary>
    [Fact]
    public async Task RestSql_WithCorruptedModbusAudit_KeepsOrdinarySqlAvailableAndFailsWriteClosed()
    {
        await using WriteRuntimeEnvironment environment = await WriteRuntimeEnvironment.CreateAsync(
            corruptAuditFile: true);

        Assert.Equal(7L, (await environment.ExecuteSelectAsync(
            WriteRuntimeEnvironment.AdminToken,
            "SELECT setpoint FROM controls")).SingleValue("setpoint").GetInt64());

        using HttpResponseMessage failed = await environment.ExecuteAsync(
            WriteRuntimeEnvironment.AdminToken,
            "WRITE MODBUS controls SET setpoint = 8 DRY RUN");
        string body = await failed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Contains("modbus_write_audit_unavailable", body, StringComparison.Ordinal);
        Assert.Equal(0, environment.DeviceWriteCount);
    }

    private sealed class WriteRuntimeEnvironment : IAsyncDisposable
    {
        internal const string AdminToken = "modbus-admin-token";
        internal const string SecondAdminToken = "modbus-second-admin-token";
        internal const string WriterToken = "modbus-writer-token";
        private const string DatabaseName = "factory";
        private readonly WebApplication _app;
        private readonly string _dataRoot;
        private readonly string _baseUrl;

        private WriteRuntimeEnvironment(
            WebApplication app,
            ModbusTestServer device,
            string dataRoot,
            string baseUrl)
        {
            _app = app;
            Device = device;
            _dataRoot = dataRoot;
            _baseUrl = baseUrl;
        }

        internal ModbusTestServer Device { get; }

        internal IServiceProvider Services => _app.Services;

        internal int DeviceWriteCount => Device.Requests.Count(static request =>
            request.FunctionCode is 0x05 or 0x06 or 0x10);

        internal static async Task<WriteRuntimeEnvironment> CreateAsync(
            IModbusWriteAuditStore? auditStore = null,
            TimeProvider? timeProvider = null,
            bool corruptAuditFile = false)
        {
            var device = new ModbusTestServer();
            device.SetValue(ModbusRegisterArea.Coil, 0, 0);
            device.SetValue(ModbusRegisterArea.HoldingRegister, 0, 7);
            device.Start();

            string dataRoot = Path.Combine(
                Path.GetTempPath(),
                "sndb-modbus-write-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            PrepareDatabase(dataRoot, device.Port);
            if (corruptAuditFile)
            {
                string systemDirectory = Path.Combine(dataRoot, ".system");
                Directory.CreateDirectory(systemDirectory);
                File.WriteAllText(Path.Combine(systemDirectory, "modbus-write-audit.ndjson"), "{broken\n");
            }

            var options = new ServerOptions
            {
                DataRoot = dataRoot,
                AutoLoadExistingDatabases = true,
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string>
                {
                    [AdminToken] = ServerRoles.Admin,
                    [SecondAdminToken] = ServerRoles.Admin,
                    [WriterToken] = ServerRoles.ReadWrite,
                },
                Modbus = new ModbusRuntimeOptions
                {
                    Enabled = true,
                    DiscoveryIntervalMilliseconds = 20,
                    RetryBaseDelayMilliseconds = 10,
                    MaxRetryDelayMilliseconds = 20,
                    ReconnectBaseDelayMilliseconds = 20,
                    MaxReconnectDelayMilliseconds = 40,
                },
            };

            Action<IServiceCollection>? configureServices = null;
            if (auditStore is not null || timeProvider is not null)
            {
                configureServices = services =>
                {
                    if (auditStore is not null)
                    {
                        services.RemoveAll<IModbusWriteAuditStore>();
                        services.AddSingleton(auditStore);
                    }
                    if (timeProvider is not null)
                    {
                        services.RemoveAll<TimeProvider>();
                        services.AddSingleton(timeProvider);
                    }
                };
            }

            WebApplication app = TestServerHost.Build(options, configureServices);
            await app.StartAsync();
            string baseUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            var environment = new WriteRuntimeEnvironment(app, device, dataRoot, baseUrl);
            TsdbRegistry registry = app.Services.GetRequiredService<TsdbRegistry>();
            await WaitUntilAsync(
                () => registry.TryGet(DatabaseName, out Tsdb database)
                      && database.Tables.Open("controls").RowCount == 1,
                TimeSpan.FromSeconds(5));
            return environment;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            await Device.DisposeAsync();
            TryDeleteDirectory(_dataRoot);
        }

        internal async Task<HttpResponseMessage> ExecuteAsync(string token, string sql)
        {
            using var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.PostAsync(
                $"/v1/db/{DatabaseName}/sql",
                JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        }

        internal async Task ExecuteSuccessAsync(string token, string sql)
        {
            using HttpResponseMessage response = await ExecuteAsync(token, sql);
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"SQL 失败：{(int)response.StatusCode} {body}");
        }

        internal async Task<SqlResult> ExecuteSelectAsync(string token, string sql)
        {
            using HttpResponseMessage response = await ExecuteAsync(token, sql);
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"SQL 失败：{(int)response.StatusCode} {body}");
            string[] lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.Length >= 2, body);

            using JsonDocument metaDocument = JsonDocument.Parse(lines[0]);
            string[] columns = metaDocument.RootElement.GetProperty("columns")
                .EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray();
            var rows = new List<JsonElement>();
            for (int index = 1; index < lines.Length - 1; index++)
            {
                using JsonDocument rowDocument = JsonDocument.Parse(lines[index]);
                rows.Add(rowDocument.RootElement.Clone());
            }

            return new SqlResult(columns, rows);
        }

        private static void PrepareDatabase(string root, int port)
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate(DatabaseName, out Tsdb database));
            _ = SqlExecutor.Execute(database, $"""
                CREATE MODBUS SOURCE plc
                WITH (
                    TRANSPORT TCP,
                    ENDPOINT '127.0.0.1:{port}',
                    UNIT_ID 1,
                    POLL_INTERVAL '60s',
                    TIMEOUT '500ms',
                    RETRY 0,
                    ADDRESSING MODICON,
                    BYTE_ORDER BIG_ENDIAN,
                    WORD_ORDER BIG_ENDIAN,
                    ENABLED TRUE
                )
                """);
            _ = SqlExecutor.Execute(database, """
                CREATE TABLE controls (
                    id INT NOT NULL,
                    note STRING NULL,
                    enabled BOOL FROM MODBUS COIL(1) AS BIT ACCESS READ_WRITE,
                    setpoint INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16 ACCESS READ_WRITE,
                    PRIMARY KEY (id)
                )
                USING MODBUS SOURCE plc
                WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
                """);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (!condition())
                await Task.Delay(20, cancellation.Token);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // Test process cleanup will remove delayed Windows handles.
            }
            catch (UnauthorizedAccessException)
            {
                // Test process cleanup will remove delayed Windows handles.
            }
        }
    }

    private sealed record SqlResult(IReadOnlyList<string> Columns, IReadOnlyList<JsonElement> Rows)
    {
        internal JsonElement SingleValue(string column)
            => Value(Assert.Single(Rows), column);

        internal JsonElement Value(JsonElement row, string column)
        {
            int ordinal = -1;
            for (int index = 0; index < Columns.Count; index++)
            {
                if (string.Equals(Columns[index], column, StringComparison.Ordinal))
                {
                    ordinal = index;
                    break;
                }
            }
            Assert.True(ordinal >= 0, $"结果中缺少列 '{column}'。");
            return row[ordinal];
        }
    }

    private sealed class FailingModbusWriteAuditStore(int failOnAppend) : IModbusWriteAuditStore
    {
        private readonly List<ModbusWriteAuditEntry> _entries = [];
        private int _appendCount;

        public void Append(ModbusWriteAuditEntry entry)
        {
            if (Interlocked.Increment(ref _appendCount) == failOnAppend)
                throw new IOException("Injected audit persistence failure.");
            lock (_entries)
                _entries.Add(entry);
        }

        public IReadOnlyList<ModbusWriteAuditEntry> List(string database, int maxEntries)
        {
            lock (_entries)
            {
                return _entries
                    .Where(entry => string.Equals(entry.Database, database, StringComparison.Ordinal))
                    .TakeLast(maxEntries)
                    .ToArray();
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
