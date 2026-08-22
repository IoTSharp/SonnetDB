using System.Buffers.Binary;
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
/// 验证 endpoint 外部写的真实 TCP staging、审批、失效和失败关闭不变量。
/// </summary>
public sealed class ModbusEndpointWriteRuntimeTests
{
    [Fact]
    public async Task StagedWrite_ApproveUpdateTable_PersistsCompleteAuditTrail()
    {
        await using var environment = await EndpointEnvironment.StartAsync();

        await environment.WriteHoldingAsync(42);

        Assert.Equal(10L, environment.ReadSetpoint());
        ModbusEndpointWriteEvent pending = Assert.Single(environment.Service.ListLatest("factory", 20));
        Assert.Equal("staged", pending.State);
        Assert.Equal("shadow_values", pending.Table);
        Assert.Equal("setpoint", pending.Column);
        Assert.Equal("42", pending.DecodedValue);
        Assert.Equal([(ushort)42], pending.RawValues);

        ModbusEndpointWriteEvent applied = environment.Service.Approve(
            environment.Database,
            "factory",
            pending.RequestId,
            "operator",
            canWrite: true,
            canApprove: true);

        Assert.Equal("applied", applied.State);
        Assert.Equal(42L, environment.ReadSetpoint());
        Assert.Equal(
            ["staged", "approval_started", "applied"],
            environment.Service.ListAudit("factory", 20).Select(static entry => entry.EventType).ToArray());

        var reopened = new FileModbusEndpointWriteStore(environment.SystemDirectory);
        Assert.Equal("applied", reopened.TryGetLatest(pending.RequestId)?.State);
    }

    [Fact]
    public async Task StagedWrite_CoilAndMultipleRegisters_UsesStandardFunctions()
    {
        await using var environment = await EndpointEnvironment.StartAsync();

        await environment.WriteCoilAsync(false);
        ModbusEndpointWriteEvent coil = environment.Service.ListLatest("factory", 20)
            .Single(static entry => entry.State == "staged");
        Assert.Equal(0x05, coil.FunctionCode);
        Assert.Equal("FALSE", coil.DecodedValue);
        _ = environment.Service.Approve(
            environment.Database,
            "factory",
            coil.RequestId,
            "operator",
            canWrite: true,
            canApprove: true);
        Assert.False(environment.ReadCoil());

        await environment.WriteHoldingAsync([(ushort)0x0001, (ushort)0x0002], startAddress: 1);
        ModbusEndpointWriteEvent registers = environment.Service.ListLatest("factory", 20)
            .Single(static entry => entry.State == "staged");
        Assert.Equal(0x10, registers.FunctionCode);
        Assert.Equal("65538", registers.DecodedValue);
        _ = environment.Service.Approve(
            environment.Database,
            "factory",
            registers.RequestId,
            "operator",
            canWrite: true,
            canApprove: true);
        Assert.Equal(65_538L, environment.ReadWideValue());

        await environment.WriteHoldingAsync([(ushort)0, (ushort)0], startAddress: 3);
        ModbusEndpointWriteEvent emptyString = environment.Service.ListLatest("factory", 20)
            .Single(static entry => entry.State == "staged");
        Assert.Equal(0x10, emptyString.FunctionCode);
        Assert.Equal(string.Empty, emptyString.DecodedValue);
        _ = environment.Service.Approve(
            environment.Database,
            "factory",
            emptyString.RequestId,
            "operator",
            canWrite: true,
            canApprove: true);
        Assert.Equal(string.Empty, environment.ReadTextValue());

        await environment.WriteMultipleCoilsAsync(true);
        ModbusEndpointWriteEvent multipleCoils = environment.Service.ListLatest("factory", 20)
            .Single(static entry => entry.State == "staged");
        Assert.Equal(0x0F, multipleCoils.FunctionCode);
        Assert.Equal("TRUE", multipleCoils.DecodedValue);
        _ = environment.Service.Approve(
            environment.Database,
            "factory",
            multipleCoils.RequestId,
            "operator",
            canWrite: true,
            canApprove: true);
        Assert.True(environment.ReadCoil());
    }

    [Fact]
    public async Task StagedWrite_StageOnlyApproval_DoesNotChangeTable()
    {
        await using var environment = await EndpointEnvironment.StartAsync(approvedAction: "STAGE_ONLY");
        await environment.WriteHoldingAsync(45);
        ModbusEndpointWriteEvent pending = Assert.Single(environment.Service.ListLatest("factory", 20));

        ModbusEndpointWriteEvent approved = environment.Service.Approve(
            environment.Database,
            "factory",
            pending.RequestId,
            "operator",
            canWrite: true,
            canApprove: true);

        Assert.Equal("approved", approved.State);
        Assert.Equal(10L, environment.ReadSetpoint());
        Assert.Equal(
            ["staged", "approved"],
            environment.Service.ListAudit("factory", 20).Select(static entry => entry.EventType).ToArray());
    }

    [Fact]
    public async Task ApprovalAuditFailure_AfterTableCommit_RecoversWithoutApplyingTwice()
    {
        var store = new FailingNthEndpointWriteStore(failingAppend: 3);
        await using var environment = await EndpointEnvironment.StartAsync(store: store);
        await environment.WriteHoldingAsync(43);
        Guid requestId = environment.Service.ListLatest("factory", 20).Single().RequestId;

        ModbusEndpointWriteException exception = Assert.Throws<ModbusEndpointWriteException>(() =>
            environment.Service.Approve(
                environment.Database,
                "factory",
                requestId,
                "operator",
                canWrite: true,
                canApprove: true));

        Assert.Equal("endpoint_write_audit_unavailable", exception.Code);
        Assert.Equal(43L, environment.ReadSetpoint());
        Assert.Equal(2L, environment.ReadRowVersion());
        Assert.Equal("applying", environment.Service.ListLatest("factory", 20).Single().State);

        ModbusEndpointWriteEvent recovered = environment.Service.Approve(
            environment.Database,
            "factory",
            requestId,
            "recovery-operator",
            canWrite: true,
            canApprove: true);

        Assert.Equal("applied", recovered.State);
        Assert.Equal(43L, environment.ReadSetpoint());
        Assert.Equal(2L, environment.ReadRowVersion());
        Assert.Equal(
            ["staged", "approval_started", "applied"],
            environment.Service.ListAudit("factory", 20).Select(static entry => entry.EventType).ToArray());
    }

    [Fact]
    public async Task StagedWrite_QueueAtCapacity_ReturnsServerBusyWithoutChangingTable()
    {
        await using var environment = await EndpointEnvironment.StartAsync(maxPendingWrites: 1);
        await environment.WriteHoldingAsync(45);

        ModbusProtocolException exception = await Assert.ThrowsAsync<ModbusProtocolException>(
            () => environment.WriteHoldingAsync(46));

        Assert.Equal("device_exception_06", exception.ErrorCode);
        Assert.Equal(10L, environment.ReadSetpoint());
        ModbusEndpointWriteEvent rejected = environment.Service.ListAudit("factory", 20)
            .Single(static entry => entry.ErrorCode == "endpoint_write_queue_full");
        Assert.Equal("protocol_rejected", rejected.EventType);
    }

    [Fact]
    public async Task StagedWrite_TargetRowChanges_InvalidatesRequestWithoutApplyingValue()
    {
        await using var environment = await EndpointEnvironment.StartAsync();
        await environment.WriteHoldingAsync(55);
        ModbusEndpointWriteEvent pending = Assert.Single(environment.Service.ListLatest("factory", 20));

        _ = SqlExecutor.Execute(
            environment.Database,
            "UPDATE shadow_values SET coil_value = FALSE WHERE id = 1");

        ModbusEndpointWriteException exception = Assert.Throws<ModbusEndpointWriteException>(() =>
            environment.Service.Approve(
                environment.Database,
                "factory",
                pending.RequestId,
                "operator",
                canWrite: true,
                canApprove: true));

        Assert.Equal("endpoint_write_binding_changed", exception.Code);
        Assert.Equal(10L, environment.ReadSetpoint());
        Assert.Equal("invalidated", environment.Service.ListLatest("factory", 20).Single().State);
    }

    [Fact]
    public async Task StagedWrite_ExpiresAndRejectsLateApproval()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-22T00:00:00Z"));
        await using var environment = await EndpointEnvironment.StartAsync(timeProvider: time, lifetimeSeconds: 30);
        await environment.WriteHoldingAsync(66);
        Guid requestId = environment.Service.ListLatest("factory", 20).Single().RequestId;

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("expired", environment.Service.ListLatest("factory", 20).Single().State);
        ModbusEndpointWriteException exception = Assert.Throws<ModbusEndpointWriteException>(() =>
            environment.Service.Approve(
                environment.Database,
                "factory",
                requestId,
                "operator",
                canWrite: true,
                canApprove: true));

        Assert.Equal("endpoint_write_request_not_pending", exception.Code);
        Assert.Equal(10L, environment.ReadSetpoint());
    }

    [Fact]
    public async Task StagedWrite_Reject_DoesNotChangeTable()
    {
        await using var environment = await EndpointEnvironment.StartAsync();
        await environment.WriteHoldingAsync(77);
        Guid requestId = environment.Service.ListLatest("factory", 20).Single().RequestId;

        ModbusEndpointWriteEvent rejected = environment.Service.Reject(
            "factory",
            requestId,
            "operator",
            "maintenance window",
            canApprove: true);

        Assert.Equal("rejected", rejected.State);
        Assert.Equal("maintenance window", rejected.Reason);
        Assert.Equal(10L, environment.ReadSetpoint());
    }

    [Fact]
    public async Task RejectPolicy_ReturnsIllegalFunctionAndAuditsRejection()
    {
        await using var environment = await EndpointEnvironment.StartAsync(writePolicy: "REJECT");

        ModbusProtocolException exception = await Assert.ThrowsAsync<ModbusProtocolException>(
            () => environment.WriteHoldingAsync(88));

        Assert.Equal("device_exception_01", exception.ErrorCode);
        ModbusEndpointWriteEvent rejected = Assert.Single(environment.Service.ListAudit("factory", 20));
        Assert.Equal("protocol_rejected", rejected.EventType);
        Assert.Equal("endpoint_write_policy_reject", rejected.ErrorCode);
        Assert.Equal(10L, environment.ReadSetpoint());
    }

    [Fact]
    public async Task StagingAuditUnavailable_ReturnsServerFailureAndDoesNotChangeTable()
    {
        await using var environment = await EndpointEnvironment.StartAsync(
            store: new FailingEndpointWriteStore());

        ModbusProtocolException exception = await Assert.ThrowsAsync<ModbusProtocolException>(
            () => environment.WriteHoldingAsync(99));

        Assert.Equal("device_exception_04", exception.ErrorCode);
        Assert.Equal(10L, environment.ReadSetpoint());
        Assert.Equal(1, environment.Metrics.ModbusSlaveWriteRequests);
        Assert.Equal(1, environment.Metrics.ModbusSlaveWriteFailures);
    }

    private sealed class EndpointEnvironment : IAsyncDisposable
    {
        private readonly string _root;
        private readonly TsdbRegistry _registry;
        private readonly ModbusSlaveService _slave;
        private readonly int _port;

        private EndpointEnvironment(
            string root,
            string systemDirectory,
            TsdbRegistry registry,
            Tsdb database,
            ModbusSlaveService slave,
            ModbusEndpointWriteService service,
            ServerMetrics metrics,
            int port)
        {
            _root = root;
            SystemDirectory = systemDirectory;
            _registry = registry;
            Database = database;
            _slave = slave;
            Service = service;
            Metrics = metrics;
            _port = port;
        }

        internal string SystemDirectory { get; }

        internal Tsdb Database { get; }

        internal ModbusEndpointWriteService Service { get; }

        internal ServerMetrics Metrics { get; }

        internal static async Task<EndpointEnvironment> StartAsync(
            string writePolicy = "STAGED",
            string approvedAction = "UPDATE_TABLE",
            IModbusEndpointWriteStore? store = null,
            TimeProvider? timeProvider = null,
            int lifetimeSeconds = 900,
            int maxPendingWrites = 4_096)
        {
            string root = Path.Combine(Path.GetTempPath(), "sonnetdb-endpoint-write-" + Guid.NewGuid().ToString("N"));
            string systemDirectory = Path.Combine(root, ".system");
            Directory.CreateDirectory(systemDirectory);
            int port = GetFreeTcpPort();
            var registry = new TsdbRegistry(root);
            Assert.True(registry.TryCreate("factory", out Tsdb database));
            CreateSchema(database, port, writePolicy, approvedAction);
            var options = new ServerOptions
            {
                Modbus = new ModbusRuntimeOptions
                {
                    Enabled = true,
                    DiscoveryIntervalMilliseconds = 20,
                    EndpointWriteRequestLifetimeSeconds = lifetimeSeconds,
                    MaxPendingEndpointWrites = maxPendingWrites,
                },
            };
            var metrics = new ServerMetrics();
            var service = new ModbusEndpointWriteService(
                store ?? new FileModbusEndpointWriteStore(systemDirectory),
                Options.Create(options),
                timeProvider ?? TimeProvider.System);
            var slave = new ModbusSlaveService(
                registry,
                metrics,
                service,
                Options.Create(options),
                NullLogger<ModbusSlaveService>.Instance);
            await slave.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => database.Modbus.GetEndpointRuntimeStatus("shadow").Health
                      == ModbusEndpointRuntimeHealth.Listening,
                TimeSpan.FromSeconds(5));
            return new EndpointEnvironment(root, systemDirectory, registry, database, slave, service, metrics, port);
        }

        internal async Task WriteHoldingAsync(ushort value)
            => await WriteHoldingAsync([value], startAddress: 0);

        internal async Task WriteHoldingAsync(IReadOnlyList<ushort> values, ushort startAddress)
        {
            var source = new ModbusSourceDefinition(
                "client",
                "127.0.0.1",
                _port,
                UnitId: 7,
                TimeoutMilliseconds: 1_000,
                RetryCount: 0,
                Enabled: true);
            await using var client = new ModbusTcpMasterClient();
            await client.WriteAsync(
                source,
                new ModbusWritePayload(ModbusRegisterArea.HoldingRegister, startAddress, values),
                CancellationToken.None);
        }

        internal async Task WriteCoilAsync(bool value)
        {
            var source = new ModbusSourceDefinition(
                "client",
                "127.0.0.1",
                _port,
                UnitId: 7,
                TimeoutMilliseconds: 1_000,
                RetryCount: 0,
                Enabled: true);
            await using var client = new ModbusTcpMasterClient();
            await client.WriteAsync(
                source,
                new ModbusWritePayload(ModbusRegisterArea.Coil, 0, [value ? (ushort)1 : (ushort)0]),
                CancellationToken.None);
        }

        internal async Task WriteMultipleCoilsAsync(params bool[] values)
        {
            int byteCount = (values.Length + 7) / 8;
            var request = new byte[13 + byteCount];
            BinaryPrimitives.WriteUInt16BigEndian(request, 31);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), checked((ushort)(7 + byteCount)));
            request[6] = 7;
            request[7] = 0x0F;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), checked((ushort)values.Length));
            request[12] = checked((byte)byteCount);
            for (int index = 0; index < values.Length; index++)
            {
                if (values[index])
                    request[13 + (index / 8)] |= checked((byte)(1 << (index % 8)));
            }

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, _port);
            await using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(request);
            var response = new byte[12];
            await stream.ReadExactlyAsync(response);
            Assert.Equal((byte)0x0F, response[7]);
            Assert.Equal(request.AsSpan(8, 4).ToArray(), response.AsSpan(8, 4).ToArray());
        }

        internal long ReadSetpoint()
            => Convert.ToInt64(Database.Tables.Open("shadow_values").GetByPrimaryKey([1L])!.Values[2]);

        internal bool ReadCoil()
            => Convert.ToBoolean(Database.Tables.Open("shadow_values").GetByPrimaryKey([1L])!.Values[1]);

        internal long ReadWideValue()
            => Convert.ToInt64(Database.Tables.Open("shadow_values").GetByPrimaryKey([1L])!.Values[3]);

        internal string ReadTextValue()
            => Convert.ToString(Database.Tables.Open("shadow_values").GetByPrimaryKey([1L])!.Values[4])!;

        internal long ReadRowVersion()
            => Convert.ToInt64(Database.Tables.Open("shadow_values").GetByPrimaryKey([1L])!.Values[5]);

        public async ValueTask DisposeAsync()
        {
            await _slave.StopAsync(CancellationToken.None);
            _slave.Dispose();
            _registry.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void CreateSchema(
            Tsdb database,
            int port,
            string writePolicy,
            string approvedAction)
        {
            _ = SqlExecutor.Execute(database, $"""
                CREATE MODBUS ENDPOINT shadow
                WITH (
                    BIND '127.0.0.1:{port}',
                    UNIT_ID 7,
                    ADDRESSING ZERO_BASED,
                    BYTE_ORDER BIG_ENDIAN,
                    WORD_ORDER BIG_ENDIAN,
                    ALLOWLIST ('127.0.0.0/8'),
                    MAX_CONNECTIONS 4,
                    WRITE_POLICY {writePolicy},
                    ENABLED TRUE
                )
                """);
            _ = SqlExecutor.Execute(database, $"""
                CREATE TABLE shadow_values (
                    id INT NOT NULL,
                    coil_value BOOL EXPOSE AS MODBUS COIL(0) AS BIT ACCESS READ_WRITE,
                    setpoint INT EXPOSE AS MODBUS HOLDING_REGISTER(0) AS UINT16 ACCESS READ_WRITE,
                    wide_value INT EXPOSE AS MODBUS HOLDING_REGISTER(1, 2) AS UINT32 ACCESS READ_WRITE,
                    text_value STRING EXPOSE AS MODBUS HOLDING_REGISTER(3, 2) AS STRING(4) ACCESS READ_WRITE,
                    version INT ROWVERSION,
                    PRIMARY KEY (id)
                )
                USING MODBUS ENDPOINT shadow
                WITH (ROW KEY 1, ON_EXTERNAL_WRITE {approvedAction})
                """);
            database.Tables.Open("shadow_values").Insert([1L, true, 10L, 0L, "AB", 1L]);
        }
    }

    private sealed class FailingNthEndpointWriteStore(int failingAppend) : IModbusEndpointWriteStore
    {
        private readonly List<ModbusEndpointWriteEvent> _events = [];
        private readonly Dictionary<Guid, ModbusEndpointWriteEvent> _latest = [];
        private int _appendCount;

        public void Append(ModbusEndpointWriteEvent entry)
        {
            _appendCount++;
            if (_appendCount == failingAppend)
                throw new IOException("Injected persistence failure.");
            _events.Add(entry);
            _latest[entry.RequestId] = entry;
        }

        public ModbusEndpointWriteEvent? TryGetLatest(Guid requestId) => _latest.GetValueOrDefault(requestId);

        public IReadOnlyList<ModbusEndpointWriteEvent> ListLatest(string database, int maxEntries)
            => _latest.Values
                .Where(entry => string.Equals(entry.Database, database, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static entry => entry.OccurredAtUtc)
                .Take(maxEntries)
                .ToArray();

        public IReadOnlyList<ModbusEndpointWriteEvent> ListEvents(string database, int maxEntries)
            => _events
                .Where(entry => string.Equals(entry.Database, database, StringComparison.OrdinalIgnoreCase))
                .TakeLast(maxEntries)
                .ToArray();
    }

    private sealed class FailingEndpointWriteStore : IModbusEndpointWriteStore
    {
        public void Append(ModbusEndpointWriteEvent entry) => throw new IOException("Injected persistence failure.");

        public ModbusEndpointWriteEvent? TryGetLatest(Guid requestId) => null;

        public IReadOnlyList<ModbusEndpointWriteEvent> ListLatest(string database, int maxEntries) => [];

        public IReadOnlyList<ModbusEndpointWriteEvent> ListEvents(string database, int maxEntries) => [];
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(20, cancellation.Token);
    }
}
