using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Modbus;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests.Modbus;

/// <summary>
/// 验证 Modbus TCP slave endpoint 的门禁、四区读取与网络访问边界。
/// </summary>
public sealed class ModbusSlaveRuntimeTests
{
    [Fact]
    public async Task Runtime_WhenGloballyDisabled_DoesNotListen()
    {
        int port = GetFreeTcpPort();
        string root = CreateRoot();
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("disabled", out Tsdb database));
            CreateEndpointAndTable(database, port, enabled: true);
            var service = CreateService(registry, new ServerMetrics(), enabled: false);

            await service.StartAsync(CancellationToken.None);
            await Task.Delay(100);
            Assert.False(await CanConnectAsync(port));
            Assert.Equal(
                ModbusEndpointRuntimeHealth.Disabled,
                database.Modbus.GetEndpointRuntimeStatus("shadow").Health);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Runtime_WhenEndpointDisabled_DoesNotListen()
    {
        int port = GetFreeTcpPort();
        string root = CreateRoot();
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("endpoint_disabled", out Tsdb database));
            CreateEndpointAndTable(database, port, enabled: false);
            var service = CreateService(registry, new ServerMetrics(), enabled: true);

            await service.StartAsync(CancellationToken.None);
            await Task.Delay(100);
            Assert.False(await CanConnectAsync(port));
            Assert.Equal(
                ModbusEndpointRuntimeHealth.Disabled,
                database.Modbus.GetEndpointRuntimeStatus("shadow").Health);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Runtime_WhenBindFails_PublishesDegradedStatusWithoutCatalogMutation()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        string root = CreateRoot();
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("bind_failure", out Tsdb database));
            CreateEndpointAndTable(database, port, enabled: true);
            long revision = database.Modbus.Revision;
            var service = CreateService(registry, new ServerMetrics(), enabled: true);

            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => database.Modbus.GetEndpointRuntimeStatus("shadow").Health
                      == ModbusEndpointRuntimeHealth.Degraded,
                TimeSpan.FromSeconds(5));

            ModbusEndpointRuntimeStatus status = database.Modbus.GetEndpointRuntimeStatus("shadow");
            Assert.True(status.RuntimeEnabled);
            Assert.Equal(ModbusErrorCodes.EndpointBind, status.LastErrorCode);
            Assert.Equal(revision, database.Modbus.Revision);

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            occupied.Stop();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Runtime_WithFourAreas_ReadsFixedRowAndPublishesMetrics()
    {
        int port = GetFreeTcpPort();
        string root = CreateRoot();
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("factory", out Tsdb database));
            CreateEndpointAndTable(database, port, enabled: true);
            CreateAdditionalEndpointTable(database);
            var metrics = new ServerMetrics();
            var service = CreateService(registry, metrics, enabled: true);

            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => database.Modbus.GetEndpointRuntimeStatus("shadow").Health
                      == ModbusEndpointRuntimeHealth.Listening,
                TimeSpan.FromSeconds(5));

            var source = Source(port, unitId: 7);
            await using (var client = new ModbusTcpMasterClient())
            {
                ushort[] coils = await client.ReadAsync(
                    source,
                    new ModbusReadBatch(ModbusRegisterArea.Coil, 0, 2),
                    CancellationToken.None);
                ushort[] discreteInputs = await client.ReadAsync(
                    source,
                    new ModbusReadBatch(ModbusRegisterArea.DiscreteInput, 0, 1),
                    CancellationToken.None);
                ushort[] holdingRegisters = await client.ReadAsync(
                    source,
                    new ModbusReadBatch(ModbusRegisterArea.HoldingRegister, 0, 2),
                    CancellationToken.None);
                ushort[] inputRegisters = await client.ReadAsync(
                    source,
                    new ModbusReadBatch(ModbusRegisterArea.InputRegister, 0, 1),
                    CancellationToken.None);
                ushort[] inputBits = await client.ReadAsync(
                    source,
                    new ModbusReadBatch(ModbusRegisterArea.InputRegister, 2, 1),
                    CancellationToken.None);

                Assert.Equal([(ushort)1, (ushort)1], coils);
                Assert.Equal([(ushort)0], discreteInputs);
                Assert.Equal([(ushort)0x5678, (ushort)0x1234], holdingRegisters);
                Assert.Equal([(ushort)123], inputRegisters);
                Assert.Equal([(ushort)0x0800], inputBits);
            }

            Assert.Equal(5, metrics.ModbusSlaveReadRequests);
            Assert.Equal(0, metrics.ModbusSlaveReadFailures);
            Assert.Equal(1, metrics.ModbusSlaveConnections);
            await WaitUntilAsync(
                () => metrics.ModbusSlaveActiveConnections == 0,
                TimeSpan.FromSeconds(2));
            var shown = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(database, "SHOW MODBUS ENDPOINTS"));
            IReadOnlyList<object?> endpointRow = Assert.Single(shown.Rows);
            Assert.True(Assert.IsType<bool>(endpointRow[10]));
            Assert.Equal("listening", endpointRow[11]);

            string prometheus = PrometheusFormatter.Render(metrics, registry);
            Assert.Contains("sonnetdb_modbus_slave_connections_total 1", prometheus, StringComparison.Ordinal);
            Assert.Contains("sonnetdb_modbus_slave_read_requests_total 5", prometheus, StringComparison.Ordinal);

            await service.StopAsync(CancellationToken.None);
            Assert.Equal(
                ModbusEndpointRuntimeHealth.Disabled,
                database.Modbus.GetEndpointRuntimeStatus("shadow").Health);
            Assert.False(await CanConnectAsync(port));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Runtime_InvalidUnitAddressQuantityAndWrite_ReturnsProtocolExceptions()
    {
        int port = GetFreeTcpPort();
        string root = CreateRoot();
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("protocol", out Tsdb database));
            CreateEndpointAndTable(database, port, enabled: true);
            var metrics = new ServerMetrics();
            var service = CreateService(registry, metrics, enabled: true);
            await service.StartAsync(CancellationToken.None);
            await WaitUntilListeningAsync(database);

            await using (var client = new ModbusTcpMasterClient())
            {
                await AssertProtocolExceptionAsync(
                    "device_exception_0b",
                    () => client.ReadAsync(
                        Source(port, unitId: 8),
                        new ModbusReadBatch(ModbusRegisterArea.Coil, 0, 1),
                        CancellationToken.None));
                await AssertProtocolExceptionAsync(
                    "device_exception_02",
                    () => client.ReadAsync(
                        Source(port, unitId: 7),
                        new ModbusReadBatch(ModbusRegisterArea.Coil, 10, 1),
                        CancellationToken.None));
                await AssertProtocolExceptionAsync(
                    "device_exception_03",
                    () => client.ReadAsync(
                        Source(port, unitId: 7),
                        new ModbusReadBatch(ModbusRegisterArea.Coil, 0, 0),
                        CancellationToken.None));
                Assert.True(database.Tables.Open("shadow_values").DeleteByPrimaryKey([1L]));
                await AssertProtocolExceptionAsync(
                    "device_exception_04",
                    () => client.ReadAsync(
                        Source(port, unitId: 7),
                        new ModbusReadBatch(ModbusRegisterArea.Coil, 0, 1),
                        CancellationToken.None));
                await AssertProtocolExceptionAsync(
                    "device_exception_01",
                    () => client.WriteAsync(
                        Source(port, unitId: 7),
                        new ModbusWritePayload(ModbusRegisterArea.HoldingRegister, 0, [(ushort)1]),
                        CancellationToken.None));
            }

            Assert.Equal(4, metrics.ModbusSlaveReadRequests);
            Assert.Equal(4, metrics.ModbusSlaveReadFailures);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Runtime_AllowlistExcludesLoopback_RejectsConnection()
    {
        int port = GetFreeTcpPort();
        string root = CreateRoot();
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("allowlist", out Tsdb database));
            CreateEndpointAndTable(
                database,
                port,
                enabled: true,
                allowlist: "'192.0.2.0/24'");
            var metrics = new ServerMetrics();
            var service = CreateService(registry, metrics, enabled: true);
            await service.StartAsync(CancellationToken.None);
            await WaitUntilListeningAsync(database);

            await using var client = new ModbusTcpMasterClient();
            await Assert.ThrowsAnyAsync<IOException>(() => client.ReadAsync(
                Source(port, unitId: 7),
                new ModbusReadBatch(ModbusRegisterArea.Coil, 0, 1),
                CancellationToken.None));
            await WaitUntilAsync(
                () => metrics.ModbusSlaveConnectionRejections == 1,
                TimeSpan.FromSeconds(2));
            Assert.Equal(0, metrics.ModbusSlaveConnections);

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Runtime_MaxConnectionsRejectsExcessAndReleasesSlot()
    {
        int port = GetFreeTcpPort();
        string root = CreateRoot();
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("connections", out Tsdb database));
            CreateEndpointAndTable(database, port, enabled: true, maxConnections: 1);
            var metrics = new ServerMetrics();
            var service = CreateService(registry, metrics, enabled: true);
            await service.StartAsync(CancellationToken.None);
            await WaitUntilListeningAsync(database);

            using var first = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, port);
            await WaitUntilAsync(
                () => metrics.ModbusSlaveActiveConnections == 1,
                TimeSpan.FromSeconds(2));

            await using (var rejected = new ModbusTcpMasterClient())
            {
                await Assert.ThrowsAnyAsync<IOException>(() => rejected.ReadAsync(
                    Source(port, unitId: 7),
                    new ModbusReadBatch(ModbusRegisterArea.Coil, 0, 1),
                    CancellationToken.None));
            }
            await WaitUntilAsync(
                () => metrics.ModbusSlaveConnectionRejections == 1,
                TimeSpan.FromSeconds(2));

            first.Dispose();
            await WaitUntilAsync(
                () => metrics.ModbusSlaveActiveConnections == 0,
                TimeSpan.FromSeconds(2));
            await using (var accepted = new ModbusTcpMasterClient())
            {
                ushort[] values = await accepted.ReadAsync(
                    Source(port, unitId: 7),
                    new ModbusReadBatch(ModbusRegisterArea.Coil, 0, 1),
                    CancellationToken.None);
                Assert.Equal([(ushort)1], values);
            }

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ClientAllowlist_WithExactAndCidr_MatchesAddressFamilies()
    {
        var allowlist = new ModbusClientAllowlist([
            "192.168.10.5",
            "10.20.0.0/16",
            "2001:db8::/32",
        ]);

        Assert.True(allowlist.IsAllowed(IPAddress.Parse("192.168.10.5")));
        Assert.True(allowlist.IsAllowed(IPAddress.Parse("10.20.255.1")));
        Assert.True(allowlist.IsAllowed(IPAddress.Parse("2001:db8::42")));
        Assert.False(allowlist.IsAllowed(IPAddress.Parse("10.21.0.1")));
        Assert.False(allowlist.IsAllowed(IPAddress.IPv6Loopback));
        Assert.True(new ModbusClientAllowlist([]).IsAllowed(IPAddress.Loopback));
        Assert.False(new ModbusClientAllowlist([]).IsAllowed(IPAddress.Parse("192.0.2.1")));
    }

    private static async Task AssertProtocolExceptionAsync(string errorCode, Func<Task> action)
    {
        ModbusProtocolException exception = await Assert.ThrowsAsync<ModbusProtocolException>(action);
        Assert.Equal(errorCode, exception.ErrorCode);
    }

    private static ModbusSlaveService CreateService(
        TsdbRegistry registry,
        ServerMetrics metrics,
        bool enabled)
    {
        var options = new ServerOptions
        {
            Modbus = new ModbusRuntimeOptions
            {
                Enabled = enabled,
                DiscoveryIntervalMilliseconds = 20,
            },
        };
        return new ModbusSlaveService(
            registry,
            metrics,
            Options.Create(options),
            NullLogger<ModbusSlaveService>.Instance);
    }

    private static void CreateEndpointAndTable(
        Tsdb database,
        int port,
        bool enabled,
        string allowlist = "'127.0.0.0/8'",
        int maxConnections = 4)
    {
        _ = SqlExecutor.Execute(database, $"""
            CREATE MODBUS ENDPOINT shadow
            WITH (
                BIND '127.0.0.1:{port}',
                UNIT_ID 7,
                ADDRESSING ZERO_BASED,
                BYTE_ORDER BIG_ENDIAN,
                WORD_ORDER BIG_ENDIAN,
                ALLOWLIST ({allowlist}),
                MAX_CONNECTIONS {maxConnections},
                WRITE_POLICY REJECT,
                ENABLED {(enabled ? "TRUE" : "FALSE")}
            )
            """);
        _ = SqlExecutor.Execute(database, """
            CREATE TABLE shadow_values (
                id INT NOT NULL,
                coil_value BOOL EXPOSE AS MODBUS COIL(0) AS BIT ACCESS READ,
                discrete_value BOOL EXPOSE AS MODBUS DISCRETE_INPUT(0) AS BIT ACCESS READ,
                holding_value INT EXPOSE AS MODBUS HOLDING_REGISTER(0, 2)
                    AS UINT32 WORD_ORDER LITTLE_ENDIAN ACCESS READ,
                input_value FLOAT EXPOSE AS MODBUS INPUT_REGISTER(0)
                    AS INT16 SCALE 0.1 ACCESS READ,
                input_bit BOOL EXPOSE AS MODBUS INPUT_REGISTER(2).BIT(3)
                    AS BIT BYTE_ORDER LITTLE_ENDIAN ACCESS READ,
                PRIMARY KEY (id)
            )
            USING MODBUS ENDPOINT shadow
            WITH (ROW KEY 1, ON_EXTERNAL_WRITE STAGE_ONLY)
            """);
        database.Tables.Open("shadow_values").Insert([
            1L,
            true,
            false,
            0x1234_5678L,
            12.3d,
            true,
        ]);
    }

    private static ModbusSourceDefinition Source(int port, byte unitId)
        => new(
            "test-client",
            "127.0.0.1",
            port,
            UnitId: unitId,
            TimeoutMilliseconds: 1_000,
            RetryCount: 0,
            Enabled: true);

    private static void CreateAdditionalEndpointTable(Tsdb database)
    {
        _ = SqlExecutor.Execute(database, """
            CREATE TABLE shadow_extra (
                id INT NOT NULL,
                second_coil BOOL EXPOSE AS MODBUS COIL(1) AS BIT ACCESS READ,
                PRIMARY KEY (id)
            )
            USING MODBUS ENDPOINT shadow
            WITH (ROW KEY 2, ON_EXTERNAL_WRITE STAGE_ONLY)
            """);
        database.Tables.Open("shadow_extra").Insert([2L, true]);
    }

    private static async Task WaitUntilListeningAsync(Tsdb database)
        => await WaitUntilAsync(
            () => database.Modbus.GetEndpointRuntimeStatus("shadow").Health
                  == ModbusEndpointRuntimeHealth.Listening,
            TimeSpan.FromSeconds(5));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(20, cancellation.Token);
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        using var client = new TcpClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellation.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-modbus-slave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
