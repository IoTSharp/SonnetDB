using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Modbus;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests.Modbus;

public sealed class ModbusMasterRuntimeTests
{
    [Fact]
    public async Task Runtime_WhenGloballyDisabled_DoesNotConnectForPollingOrSelect()
    {
        await using var server = new ModbusTestServer();
        server.Start();
        string root = CreateRoot();
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("disabled", out Tsdb database));
            CreateSourceAndTable(database, server.Port, retryCount: 0);
            var metrics = new ServerMetrics();
            var service = CreateService(registry, metrics, enabled: false);

            await service.StartAsync(CancellationToken.None);
            _ = SqlExecutor.Execute(database, "SELECT holding_a FROM samples");
            await Task.Delay(150);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, server.ConnectionCount);
            Assert.Equal(0, metrics.ModbusPolls);
            var shown = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(database, "SHOW MODBUS SOURCES"));
            IReadOnlyList<object?> row = Assert.Single(shown.Rows);
            Assert.False(Assert.IsType<bool>(row[10]));
            Assert.Equal("disabled", row[11]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Runtime_WithFourAreas_BatchesWritesReconnectsAndPublishesMetrics()
    {
        await using var server = new ModbusTestServer { DropRequestsRemaining = 2 };
        server.SetValue(ModbusRegisterArea.Coil, 0, 1);
        server.SetValue(ModbusRegisterArea.DiscreteInput, 0, 0);
        server.SetValue(ModbusRegisterArea.InputRegister, 0, 25);
        server.SetValue(ModbusRegisterArea.HoldingRegister, 0, 100);
        server.SetValue(ModbusRegisterArea.HoldingRegister, 1, 200);
        server.Start();

        string root = CreateRoot();
        try
        {
            using var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("factory", out Tsdb database));
            CreateSourceAndTable(database, server.Port, retryCount: 1);
            var metrics = new ServerMetrics();
            var service = CreateService(registry, metrics, enabled: true);

            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => database.Tables.Open("samples").RowCount > 0,
                TimeSpan.FromSeconds(5));

            var runningSources = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(database, "SHOW MODBUS SOURCES"));
            IReadOnlyList<object?> runningSourceRow = Assert.Single(runningSources.Rows);
            Assert.True(Assert.IsType<bool>(runningSourceRow[10]));
            Assert.Equal("healthy", runningSourceRow[11]);
            Assert.NotNull(runningSourceRow[12]);
            await service.StopAsync(CancellationToken.None);

            var row = Assert.Single(database.Tables.Open("samples").Scan(limit: 1));
            Assert.IsType<DateTime>(row.Values[0]);
            Assert.True(Assert.IsType<bool>(row.Values[1]));
            Assert.False(Assert.IsType<bool>(row.Values[2]));
            Assert.Equal(25L, row.Values[3]);
            Assert.Equal(100L, row.Values[4]);
            Assert.Equal(200L, row.Values[5]);

            Assert.True(server.ConnectionCount >= 2);
            Assert.Contains(server.Requests, request => request is { FunctionCode: 0x03, StartAddress: 0, Count: 2 });
            Assert.Contains(server.Requests, request => request.FunctionCode == 0x01);
            Assert.Contains(server.Requests, request => request.FunctionCode == 0x02);
            Assert.Contains(server.Requests, request => request.FunctionCode == 0x04);
            Assert.True(metrics.ModbusPolls >= 2);
            Assert.True(metrics.ModbusPollFailures >= 1);
            Assert.True(metrics.ModbusReadBatches >= 4);
            Assert.True(metrics.ModbusRowsWritten >= 1);
            Assert.True(metrics.ModbusReconnects >= 1);

            string prometheus = PrometheusFormatter.Render(metrics, registry);
            Assert.Contains("sonnetdb_modbus_master_polls_total", prometheus, StringComparison.Ordinal);
            Assert.Contains("sonnetdb_modbus_master_reconnects_total", prometheus, StringComparison.Ordinal);

            var shown = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(database, "SHOW MODBUS SOURCES"));
            IReadOnlyList<object?> sourceRow = Assert.Single(shown.Rows);
            Assert.False(Assert.IsType<bool>(sourceRow[10]));
            Assert.Equal("disabled", sourceRow[11]);
            Assert.NotNull(sourceRow[12]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TcpClient_WithSlowResponse_HonorsTimeoutAndCallerCancellation()
    {
        await using var server = new ModbusTestServer { ResponseDelay = TimeSpan.FromSeconds(5) };
        server.Start();
        var batch = new ModbusReadBatch(ModbusRegisterArea.HoldingRegister, 0, 1);
        var timeoutSource = Source(server.Port, timeoutMilliseconds: 50, retryCount: 0);
        await using (var client = new ModbusTcpMasterClient())
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                client.ReadAsync(timeoutSource, batch, CancellationToken.None));
        }

        var cancellationSource = Source(server.Port, timeoutMilliseconds: 5_000, retryCount: 0);
        await using (var client = new ModbusTcpMasterClient())
        using (var cancellation = new CancellationTokenSource(50))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.ReadAsync(cancellationSource, batch, cancellation.Token));
        }
    }

    [Fact]
    public async Task TcpClient_WriteAsync_UsesStandardWriteFunctionsAndUpdatesDeviceValues()
    {
        await using var server = new ModbusTestServer();
        server.Start();
        ModbusSourceDefinition source = Source(server.Port, timeoutMilliseconds: 1_000, retryCount: 0);
        await using var client = new ModbusTcpMasterClient();

        await client.WriteAsync(
            source,
            new ModbusWritePayload(ModbusRegisterArea.Coil, 3, [(ushort)1]),
            CancellationToken.None);
        await client.WriteAsync(
            source,
            new ModbusWritePayload(ModbusRegisterArea.HoldingRegister, 7, [(ushort)0x1234]),
            CancellationToken.None);
        await client.WriteAsync(
            source,
            new ModbusWritePayload(
                ModbusRegisterArea.HoldingRegister,
                9,
                [(ushort)0x1111, (ushort)0x2222]),
            CancellationToken.None);

        Assert.Equal((ushort)1, server.GetValue(ModbusRegisterArea.Coil, 3));
        Assert.Equal((ushort)0x1234, server.GetValue(ModbusRegisterArea.HoldingRegister, 7));
        Assert.Equal((ushort)0x1111, server.GetValue(ModbusRegisterArea.HoldingRegister, 9));
        Assert.Equal((ushort)0x2222, server.GetValue(ModbusRegisterArea.HoldingRegister, 10));
        Assert.Contains(server.Requests, request => request is { FunctionCode: 0x05, StartAddress: 3, Count: 1 });
        Assert.Contains(server.Requests, request => request is { FunctionCode: 0x06, StartAddress: 7, Count: 1 });
        Assert.Contains(server.Requests, request => request is { FunctionCode: 0x10, StartAddress: 9, Count: 2 });
    }

    [Fact]
    public async Task TcpClient_WriteAsync_WithDeviceExceptionOrMismatchedEcho_RejectsResponse()
    {
        await using var exceptionServer = new ModbusTestServer { WriteExceptionCode = 0x02 };
        exceptionServer.Start();
        await using (var client = new ModbusTcpMasterClient())
        {
            ModbusProtocolException exception = await Assert.ThrowsAsync<ModbusProtocolException>(() =>
                client.WriteAsync(
                    Source(exceptionServer.Port, timeoutMilliseconds: 1_000, retryCount: 0),
                    new ModbusWritePayload(ModbusRegisterArea.HoldingRegister, 0, [(ushort)7]),
                    CancellationToken.None));
            Assert.Equal("device_exception_02", exception.ErrorCode);
        }

        await using var mismatchServer = new ModbusTestServer { WriteResponseAddressOverride = 5 };
        mismatchServer.Start();
        await using (var client = new ModbusTcpMasterClient())
        {
            ModbusProtocolException mismatch = await Assert.ThrowsAsync<ModbusProtocolException>(() =>
                client.WriteAsync(
                    Source(mismatchServer.Port, timeoutMilliseconds: 1_000, retryCount: 0),
                    new ModbusWritePayload(ModbusRegisterArea.Coil, 0, [(ushort)1]),
                    CancellationToken.None));
            Assert.Equal("address_mismatch", mismatch.ErrorCode);
        }
    }

    [Fact]
    public async Task TcpClient_WriteAsync_WithSlowResponse_HonorsTimeout()
    {
        await using var server = new ModbusTestServer { ResponseDelay = TimeSpan.FromSeconds(5) };
        server.Start();
        await using var client = new ModbusTcpMasterClient();

        await Assert.ThrowsAsync<TimeoutException>(() => client.WriteAsync(
            Source(server.Port, timeoutMilliseconds: 50, retryCount: 0),
            new ModbusWritePayload(ModbusRegisterArea.HoldingRegister, 0, [(ushort)7]),
            CancellationToken.None));
    }

    private static ModbusMasterService CreateService(
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
                RetryBaseDelayMilliseconds = 10,
                MaxRetryDelayMilliseconds = 20,
                ReconnectBaseDelayMilliseconds = 20,
                MaxReconnectDelayMilliseconds = 40,
            },
        };
        return new ModbusMasterService(
            registry,
            metrics,
            new ModbusSourceOperationCoordinator(),
            Options.Create(options),
            NullLogger<ModbusMasterService>.Instance);
    }

    private static void CreateSourceAndTable(Tsdb database, int port, int retryCount)
    {
        _ = SqlExecutor.Execute(database, $"""
            CREATE MODBUS SOURCE plc
            WITH (
                TRANSPORT TCP,
                ENDPOINT '127.0.0.1:{port}',
                UNIT_ID 1,
                POLL_INTERVAL '25ms',
                TIMEOUT '200ms',
                RETRY {retryCount},
                ADDRESSING MODICON,
                BYTE_ORDER BIG_ENDIAN,
                WORD_ORDER BIG_ENDIAN,
                ENABLED TRUE
            )
            """);
        _ = SqlExecutor.Execute(database, """
            CREATE TABLE samples (
                sample_time DATETIME SAMPLE_TIME,
                coil_value BOOL FROM MODBUS COIL(1) AS BIT,
                discrete_value BOOL FROM MODBUS DISCRETE_INPUT(10001) AS BIT,
                input_value INT FROM MODBUS INPUT_REGISTER(30001) AS UINT16,
                holding_a INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                holding_b INT FROM MODBUS HOLDING_REGISTER(40002) AS UINT16,
                PRIMARY KEY (sample_time)
            )
            USING MODBUS SOURCE plc
            WITH (TABLE_MODE HISTORY, ON_ERROR KEEP_LAST)
            """);
    }

    private static ModbusSourceDefinition Source(
        int port,
        int timeoutMilliseconds,
        int retryCount)
        => new(
            "plc",
            "127.0.0.1",
            port,
            TimeoutMilliseconds: timeoutMilliseconds,
            RetryCount: retryCount,
            Enabled: true);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(20, cancellation.Token);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-modbus-runtime-" + Guid.NewGuid().ToString("N"));
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
