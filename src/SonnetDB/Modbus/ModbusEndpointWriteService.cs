using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Engine;
using SonnetDB.Tables;

namespace SonnetDB.Modbus;

internal sealed class ModbusEndpointWriteService
{
    private const int DefaultMaxPendingWrites = 4_096;
    private const int MaximumListEntries = 2_000;
    private static readonly TimeSpan DefaultRequestLifetime = TimeSpan.FromMinutes(15);
    private readonly object _sync = new();
    private readonly IModbusEndpointWriteStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxPendingWrites;
    private readonly TimeSpan _requestLifetime;

    internal ModbusEndpointWriteService(
        IModbusEndpointWriteStore store,
        IOptions<ServerOptions> options,
        TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
        ModbusRuntimeOptions modbus = options.Value.Modbus;
        _maxPendingWrites = modbus.MaxPendingEndpointWrites > 0
            ? Math.Min(modbus.MaxPendingEndpointWrites, 100_000)
            : DefaultMaxPendingWrites;
        _requestLifetime = modbus.EndpointWriteRequestLifetimeSeconds > 0
            ? TimeSpan.FromSeconds(Math.Min(modbus.EndpointWriteRequestLifetimeSeconds, 86_400))
            : DefaultRequestLifetime;
    }

    internal ModbusEndpointStageResult Stage(
        Tsdb database,
        string databaseName,
        ModbusEndpointDefinition endpoint,
        string remoteEndpoint,
        ushort transactionId,
        byte unitId,
        ModbusEndpointWriteCommand command)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteEndpoint);

        Guid requestId = Guid.NewGuid();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (endpoint.WritePolicy == ModbusEndpointWritePolicy.Reject)
        {
            return PersistProtocolRejection(
                database,
                databaseName,
                endpoint,
                remoteEndpoint,
                transactionId,
                unitId,
                command,
                requestId,
                now,
                "endpoint_write_policy_reject",
                ModbusTcpExceptionCodes.IllegalFunction);
        }

        ModbusEndpointWritePlan plan;
        try
        {
            plan = CreatePlan(database, endpoint, command);
        }
        catch (ModbusEndpointWriteException exception)
        {
            return PersistProtocolRejection(
                database,
                databaseName,
                endpoint,
                remoteEndpoint,
                transactionId,
                unitId,
                command,
                requestId,
                now,
                exception.Code,
                exception.ProtocolExceptionCode);
        }

        lock (_sync)
        {
            try
            {
                ExpirePendingLocked(databaseName, now);
                int pendingCount = _store.ListLatest(databaseName, int.MaxValue)
                    .Count(static entry => entry.State is "staged" or "applying");
                if (pendingCount >= _maxPendingWrites)
                {
                    return PersistProtocolRejectionLocked(
                        databaseName,
                        endpoint,
                        remoteEndpoint,
                        transactionId,
                        unitId,
                        command,
                        requestId,
                        now,
                        "endpoint_write_queue_full",
                        ModbusTcpExceptionCodes.ServerDeviceBusy,
                        plan);
                }

                DateTimeOffset expiresAt = now.Add(_requestLifetime);
                _store.Append(CreateEvent(
                    plan,
                    requestId,
                    now,
                    "staged",
                    "staged",
                    "external_modbus_client",
                    databaseName,
                    endpoint,
                    remoteEndpoint,
                    transactionId,
                    unitId,
                    command,
                    expiresAt,
                    errorCode: null,
                    reason: null));
                return new ModbusEndpointStageResult(true, 0, requestId);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return new ModbusEndpointStageResult(
                    false,
                    ModbusTcpExceptionCodes.ServerDeviceFailure,
                    requestId);
            }
        }
    }

    internal IReadOnlyList<ModbusEndpointWriteEvent> ListLatest(string databaseName, int maxEntries)
    {
        int limit = Math.Clamp(maxEntries, 1, MaximumListEntries);
        lock (_sync)
        {
            ExpirePendingLocked(databaseName, _timeProvider.GetUtcNow());
            return _store.ListLatest(databaseName, limit);
        }
    }

    internal IReadOnlyList<ModbusEndpointWriteEvent> ListAudit(string databaseName, int maxEntries)
    {
        int limit = Math.Clamp(maxEntries, 1, MaximumListEntries);
        lock (_sync)
        {
            ExpirePendingLocked(databaseName, _timeProvider.GetUtcNow());
            return _store.ListEvents(databaseName, limit);
        }
    }

    internal ModbusEndpointWriteEvent Approve(
        Tsdb database,
        string databaseName,
        Guid requestId,
        string principal,
        bool canWrite,
        bool canApprove)
    {
        if (!canWrite || !canApprove)
            throw new ModbusEndpointWriteException("forbidden", "批准 Modbus endpoint 外部写需要当前数据库的 Write 与 Admin 权限。");

        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            ExpirePendingLocked(databaseName, now);
            ModbusEndpointWriteEvent pending = RequireRequest(databaseName, requestId);
            if (string.Equals(pending.State, "applying", StringComparison.Ordinal))
                return ResumeApplying(database, pending, principal);
            EnsurePending(pending);
            ModbusEndpointWritePlan plan;
            try
            {
                plan = Revalidate(database, pending);
            }
            catch (ModbusEndpointWriteException exception)
            {
                ModbusEndpointWriteEvent invalidated = Transition(
                    pending,
                    now,
                    "invalidated",
                    "invalidated",
                    principal,
                    exception.Code,
                    exception.Message);
                AppendOrThrow(invalidated);
                throw;
            }

            if (plan.Binding.ApprovedWriteAction == ModbusApprovedWriteAction.StageOnly)
            {
                ModbusEndpointWriteEvent approved = Transition(
                    pending,
                    now,
                    "approved",
                    "approved",
                    principal,
                    errorCode: null,
                    reason: "STAGE_ONLY：审批完成，不更新关系表。");
                AppendOrThrow(approved);
                return approved;
            }

            ModbusEndpointWriteEvent applying = Transition(
                pending,
                now,
                "approval_started",
                "applying",
                principal,
                errorCode: null,
                reason: null);
            AppendOrThrow(applying);
            return ApplyAndPersistResult(database, plan, applying, principal);
        }
    }

    internal ModbusEndpointWriteEvent Reject(
        string databaseName,
        Guid requestId,
        string principal,
        string? reason,
        bool canApprove)
    {
        if (!canApprove)
            throw new ModbusEndpointWriteException("forbidden", "拒绝 Modbus endpoint 外部写需要当前数据库的 Admin 权限。");

        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            ExpirePendingLocked(databaseName, now);
            ModbusEndpointWriteEvent pending = RequirePending(databaseName, requestId);
            string? normalizedReason = string.IsNullOrWhiteSpace(reason)
                ? null
                : reason.Trim()[..Math.Min(reason.Trim().Length, 512)];
            ModbusEndpointWriteEvent rejected = Transition(
                pending,
                now,
                "rejected_by_approver",
                "rejected",
                principal,
                errorCode: null,
                normalizedReason);
            AppendOrThrow(rejected);
            return rejected;
        }
    }

    private ModbusEndpointStageResult PersistProtocolRejection(
        Tsdb database,
        string databaseName,
        ModbusEndpointDefinition endpoint,
        string remoteEndpoint,
        ushort transactionId,
        byte unitId,
        ModbusEndpointWriteCommand command,
        Guid requestId,
        DateTimeOffset now,
        string errorCode,
        byte protocolExceptionCode)
    {
        lock (_sync)
        {
            ModbusEndpointWritePlan? plan = null;
            try
            {
                plan = TryCreatePlan(database, endpoint, command);
                return PersistProtocolRejectionLocked(
                    databaseName,
                    endpoint,
                    remoteEndpoint,
                    transactionId,
                    unitId,
                    command,
                    requestId,
                    now,
                    errorCode,
                    protocolExceptionCode,
                    plan);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return new ModbusEndpointStageResult(false, ModbusTcpExceptionCodes.ServerDeviceFailure, requestId);
            }
        }
    }

    private ModbusEndpointStageResult PersistProtocolRejectionLocked(
        string databaseName,
        ModbusEndpointDefinition endpoint,
        string remoteEndpoint,
        ushort transactionId,
        byte unitId,
        ModbusEndpointWriteCommand command,
        Guid requestId,
        DateTimeOffset now,
        string errorCode,
        byte protocolExceptionCode,
        ModbusEndpointWritePlan? plan)
    {
        ModbusEndpointWriteEvent entry = plan is null
            ? new ModbusEndpointWriteEvent(
                Guid.NewGuid(),
                requestId,
                now,
                "protocol_rejected",
                "rejected",
                "external_modbus_client",
                databaseName,
                endpoint.Name,
                remoteEndpoint,
                unitId,
                transactionId,
                command.FunctionCode,
                command.Area,
                command.PduAddress,
                command.PduAddress,
                command.RawValues.ToArray(),
                null,
                null,
                null,
                null,
                null,
                null,
                Math.Max(0, plan?.CatalogRevision ?? 0),
                ModbusApprovedWriteAction.StageOnly,
                null,
                errorCode,
                null)
            : CreateEvent(
                plan,
                requestId,
                now,
                "protocol_rejected",
                "rejected",
                "external_modbus_client",
                databaseName,
                endpoint,
                remoteEndpoint,
                transactionId,
                unitId,
                command,
                expiresAt: null,
                errorCode,
                reason: null);
        _store.Append(entry);
        return new ModbusEndpointStageResult(false, protocolExceptionCode, requestId);
    }

    private static ModbusEndpointWritePlan? TryCreatePlan(
        Tsdb database,
        ModbusEndpointDefinition endpoint,
        ModbusEndpointWriteCommand command)
    {
        try
        {
            return CreatePlan(database, endpoint, command);
        }
        catch (Exception exception) when (exception is ModbusEndpointWriteException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
        {
            return null;
        }
    }

    private static ModbusEndpointWritePlan CreatePlan(
        Tsdb database,
        ModbusEndpointDefinition endpoint,
        ModbusEndpointWriteCommand command)
    {
        ModbusCatalogSnapshot snapshot = database.Modbus.Catalog.CaptureSnapshot();
        ModbusEndpointDefinition catalogEndpoint = snapshot.Endpoints.GetValueOrDefault(endpoint.Name)
            ?? throw new ModbusEndpointWriteException(
                "endpoint_write_endpoint_not_found",
                $"MODBUS ENDPOINT '{endpoint.Name}' 不存在。",
                protocolExceptionCode: ModbusTcpExceptionCodes.ServerDeviceFailure);
        if (catalogEndpoint != endpoint)
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_catalog_changed",
                "Modbus endpoint 定义已变化，请重新发送写请求。",
                protocolExceptionCode: ModbusTcpExceptionCodes.ServerDeviceFailure);
        }

        var matches = new List<(ModbusTableBinding Binding, ModbusColumnMapping Mapping)>();
        foreach (ModbusTableBinding binding in snapshot.Bindings.Values)
        {
            if (binding.Direction != ModbusMappingDirection.TableToEndpoint
                || !string.Equals(binding.TargetName, endpoint.Name, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (ModbusColumnMapping mapping in binding.Columns)
            {
                if (mapping.Area == command.Area
                    && mapping.PduAddress == command.PduAddress
                    && mapping.RegisterCount == command.RawValues.Count)
                {
                    matches.Add((binding, mapping));
                }
            }
        }

        if (matches.Count != 1)
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_mapping_not_found",
                "外部写必须精确命中一个可写列映射，不能跨列、部分覆盖或写入未映射地址。",
                protocolExceptionCode: ModbusTcpExceptionCodes.IllegalDataAddress);
        }

        (ModbusTableBinding targetBinding, ModbusColumnMapping targetMapping) = matches[0];
        if (targetMapping.Access == ModbusAccessMode.Read
            || targetMapping.Area is not ModbusRegisterArea.Coil and not ModbusRegisterArea.HoldingRegister
            || targetMapping.BitIndex is not null)
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_read_only",
                "目标映射只读，或属于不支持原子写入的输入区/寄存器 BIT。",
                protocolExceptionCode: ModbusTcpExceptionCodes.IllegalDataAddress);
        }

        if (targetBinding.RowKey is not { } rowKey)
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_row_key_missing",
                "Endpoint 映射表缺少固定 ROW KEY。",
                protocolExceptionCode: ModbusTcpExceptionCodes.ServerDeviceFailure);
        }

        object decoded;
        try
        {
            decoded = ModbusValueCodec.Decode(
                command.RawValues.ToArray(),
                targetMapping.Area,
                targetMapping.ValueType,
                targetMapping.StringLength,
                targetMapping.BitIndex ?? 0,
                targetMapping.ByteOrder,
                targetMapping.WordOrder,
                targetMapping.Scale,
                targetMapping.Offset);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or FormatException
                                          or InvalidDataException
                                          or OverflowException)
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_decode_failed",
                "外部写载荷无法按目标列的 wire type、字节序或缩放规则解码。",
                exception,
                ModbusTcpExceptionCodes.IllegalDataValue);
        }

        TableStore store = database.Tables.Open(targetBinding.TableName);
        TableRow row = store.GetByPrimaryKey([rowKey])
            ?? throw new ModbusEndpointWriteException(
                "endpoint_write_row_not_found",
                $"目标表 '{targetBinding.TableName}' 不存在固定 ROW KEY {rowKey}。",
                protocolExceptionCode: ModbusTcpExceptionCodes.ServerDeviceFailure);
        string fingerprint = FingerprintRow(store.Schema, row);
        (object?[] appliedValues, _) = BuildUpdatedValues(
            store.Schema,
            row,
            targetMapping.ColumnName,
            decoded);
        string appliedFingerprint = FingerprintRow(
            store.Schema,
            new TableRow(appliedValues, row.PrimaryKey));
        return new ModbusEndpointWritePlan(
            snapshot.Revision,
            targetBinding,
            targetMapping,
            decoded,
            fingerprint,
            appliedFingerprint);
    }

    private static ModbusEndpointWritePlan Revalidate(Tsdb database, ModbusEndpointWriteEvent pending)
    {
        ModbusEndpointWritePlan plan = RevalidateBinding(database, pending);
        if (!string.Equals(plan.RowFingerprint, pending.RowFingerprint, StringComparison.Ordinal)
            || !string.Equals(plan.AppliedRowFingerprint, pending.AppliedRowFingerprint, StringComparison.Ordinal))
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_binding_changed",
                "目标固定行或预期提交结果已变化，旧请求必须拒绝并重新发送。");
        }

        return plan;
    }

    private static ModbusEndpointWritePlan RevalidateBinding(
        Tsdb database,
        ModbusEndpointWriteEvent pending)
    {
        if (database.Modbus.Revision != pending.CatalogRevision)
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_catalog_changed",
                "Modbus catalog 已在 staging 后变化，旧请求必须拒绝并重新发送。");
        }

        ModbusEndpointDefinition endpoint = database.Modbus.Catalog.TryGetEndpoint(pending.Endpoint)
            ?? throw new ModbusEndpointWriteException(
                "endpoint_write_endpoint_not_found",
                $"MODBUS ENDPOINT '{pending.Endpoint}' 已不存在。");
        var command = new ModbusEndpointWriteCommand(
            pending.FunctionCode,
            pending.Area,
            pending.PduAddress,
            pending.RawValues);
        ModbusEndpointWritePlan plan = CreatePlan(database, endpoint, command);
        if (!string.Equals(plan.Binding.TableName, pending.Table, StringComparison.Ordinal)
            || !string.Equals(plan.Mapping.ColumnName, pending.Column, StringComparison.Ordinal)
            || plan.Binding.RowKey != pending.RowKey
            || plan.Binding.ApprovedWriteAction != pending.ApprovedAction)
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_binding_changed",
                "目标映射、固定行或获批动作已变化，旧请求必须拒绝并重新发送。");
        }

        return plan;
    }

    /// <summary>
    /// 恢复已持久化审批意图：若表事务已提交则只补终态审计，尚未提交则按原始行指纹执行一次。
    /// </summary>
    private ModbusEndpointWriteEvent ResumeApplying(
        Tsdb database,
        ModbusEndpointWriteEvent applying,
        string principal)
    {
        ModbusEndpointWritePlan plan;
        try
        {
            plan = RevalidateBinding(database, applying);
        }
        catch (ModbusEndpointWriteException exception)
        {
            ModbusEndpointWriteEvent invalidated = Transition(
                applying,
                _timeProvider.GetUtcNow(),
                "invalidated",
                "invalidated",
                principal,
                exception.Code,
                exception.Message);
            AppendOrThrow(invalidated);
            throw;
        }

        if (string.Equals(plan.RowFingerprint, applying.AppliedRowFingerprint, StringComparison.Ordinal))
        {
            ModbusEndpointWriteEvent recovered = Transition(
                applying,
                _timeProvider.GetUtcNow(),
                "applied",
                "applied",
                principal,
                errorCode: null,
                reason: "检测到关系表事务已提交，已补写审批终态审计。");
            AppendOrThrow(recovered);
            return recovered;
        }

        if (!string.Equals(plan.RowFingerprint, applying.RowFingerprint, StringComparison.Ordinal)
            || !string.Equals(plan.AppliedRowFingerprint, applying.AppliedRowFingerprint, StringComparison.Ordinal))
        {
            const string message = "审批恢复时目标行既不匹配提交前快照，也不匹配预期提交结果。";
            ModbusEndpointWriteEvent invalidated = Transition(
                applying,
                _timeProvider.GetUtcNow(),
                "invalidated",
                "invalidated",
                principal,
                "endpoint_write_recovery_conflict",
                message);
            AppendOrThrow(invalidated);
            throw new ModbusEndpointWriteException("endpoint_write_recovery_conflict", message);
        }

        return ApplyAndPersistResult(database, plan, applying, principal);
    }

    private static void ApplyToTable(
        Tsdb database,
        ModbusEndpointWritePlan plan,
        ModbusEndpointWriteEvent pending)
    {
        _ = database.ExecuteSchemaMutation(() =>
            database.Tables.ExecuteLocked(() =>
            {
                if (database.Modbus.Revision != pending.CatalogRevision)
                    throw new InvalidOperationException("审批期间 Modbus catalog 已变化。");

                TableStore store = database.Tables.Open(plan.Binding.TableName);
                TableSchema schema = store.Schema;
                TableRow row = store.GetByPrimaryKey([pending.RowKey!.Value])
                    ?? throw new InvalidOperationException("审批期间固定目标行已不存在。");
                if (!string.Equals(FingerprintRow(schema, row), pending.RowFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("审批期间固定目标行已变化。");

                (object?[] newValues, long? expectedRowVersion) = BuildUpdatedValues(
                    schema,
                    row,
                    plan.Mapping.ColumnName,
                    plan.DecodedValue);
                if (!string.Equals(
                        FingerprintRow(schema, new TableRow(newValues, row.PrimaryKey)),
                        plan.AppliedRowFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("审批期间预期提交结果已变化。");
                }

                object?[] primaryKeyValues = schema.PrimaryKey
                    .Select(name => row.Values[schema.TryGetColumn(name)!.Ordinal])
                    .ToArray();
                return database.Tables.ApplyTransaction(
                    new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
                    {
                        [schema.Name] = [new TableRowMutation(primaryKeyValues, newValues, expectedRowVersion)],
                    });
            }));
    }

    private ModbusEndpointWriteEvent ApplyAndPersistResult(
        Tsdb database,
        ModbusEndpointWritePlan plan,
        ModbusEndpointWriteEvent applying,
        string principal)
    {
        try
        {
            ApplyToTable(database, plan, applying);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or IOException
                                          or ObjectDisposedException
                                          or OverflowException)
        {
            ModbusEndpointWriteEvent failed = Transition(
                applying,
                _timeProvider.GetUtcNow(),
                "apply_failed",
                "failed",
                principal,
                "endpoint_write_apply_failed",
                exception.Message);
            AppendOrThrow(failed);
            throw new ModbusEndpointWriteException(
                "endpoint_write_apply_failed",
                "Modbus endpoint 外部写已批准，但关系表事务因约束、并发或存储错误而失败。",
                exception);
        }

        ModbusEndpointWriteEvent applied = Transition(
            applying,
            _timeProvider.GetUtcNow(),
            "applied",
            "applied",
            principal,
            errorCode: null,
            reason: null);
        AppendOrThrow(applied);
        return applied;
    }

    private static (object?[] Values, long? ExpectedRowVersion) BuildUpdatedValues(
        TableSchema schema,
        TableRow row,
        string columnName,
        object decodedValue)
    {
        TableColumn column = schema.TryGetColumn(columnName)
            ?? throw new InvalidOperationException($"列 '{columnName}' 已不存在。");
        if (column.IsRowVersion)
            throw new InvalidOperationException("Modbus endpoint 不能直接映射 ROWVERSION 列。");

        var newValues = row.Values.ToArray();
        newValues[column.Ordinal] = decodedValue;
        long? expectedRowVersion = null;
        if (schema.RowVersionColumn is { } rowVersionColumn)
        {
            expectedRowVersion = row.Values[rowVersionColumn.Ordinal] is null
                ? 0L
                : Convert.ToInt64(row.Values[rowVersionColumn.Ordinal], CultureInfo.InvariantCulture);
            newValues[rowVersionColumn.Ordinal] = checked(expectedRowVersion.Value + 1L);
        }

        return (newValues, expectedRowVersion);
    }

    private ModbusEndpointWriteEvent RequirePending(string databaseName, Guid requestId)
    {
        ModbusEndpointWriteEvent current = RequireRequest(databaseName, requestId);
        EnsurePending(current);
        return current;
    }

    private ModbusEndpointWriteEvent RequireRequest(string databaseName, Guid requestId)
    {
        ModbusEndpointWriteEvent current = _store.TryGetLatest(requestId)
            ?? throw new ModbusEndpointWriteException("endpoint_write_request_not_found", "待审批的 Modbus 写请求不存在。");
        if (!string.Equals(current.Database, databaseName, StringComparison.OrdinalIgnoreCase))
            throw new ModbusEndpointWriteException("endpoint_write_request_not_found", "待审批的 Modbus 写请求不存在。");

        return current;
    }

    private static void EnsurePending(ModbusEndpointWriteEvent current)
    {
        if (!string.Equals(current.State, "staged", StringComparison.Ordinal))
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_request_not_pending",
                $"Modbus 写请求当前状态为 '{current.State}'，不能重复审批。");
        }
    }

    private void ExpirePendingLocked(string databaseName, DateTimeOffset now)
    {
        ModbusEndpointWriteEvent[] expired = _store.ListLatest(databaseName, int.MaxValue)
            .Where(entry => string.Equals(entry.State, "staged", StringComparison.Ordinal)
                            && entry.ExpiresAtUtc <= now)
            .ToArray();
        foreach (ModbusEndpointWriteEvent entry in expired)
        {
            _store.Append(Transition(
                entry,
                now,
                "expired",
                "expired",
                "system",
                "endpoint_write_request_expired",
                "待审批请求已过期，必须由外部客户端重新发送。"));
        }
    }

    private void AppendOrThrow(ModbusEndpointWriteEvent entry)
    {
        try
        {
            _store.Append(entry);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            throw new ModbusEndpointWriteException(
                "endpoint_write_audit_unavailable",
                "Modbus endpoint 写治理审计无法持久化；操作已失败关闭。",
                exception);
        }
    }

    private static ModbusEndpointWriteEvent CreateEvent(
        ModbusEndpointWritePlan plan,
        Guid requestId,
        DateTimeOffset occurredAt,
        string eventType,
        string state,
        string principal,
        string databaseName,
        ModbusEndpointDefinition endpoint,
        string remoteEndpoint,
        ushort transactionId,
        byte unitId,
        ModbusEndpointWriteCommand command,
        DateTimeOffset? expiresAt,
        string? errorCode,
        string? reason)
        => new(
            Guid.NewGuid(),
            requestId,
            occurredAt,
            eventType,
            state,
            principal,
            databaseName,
            endpoint.Name,
            remoteEndpoint,
            unitId,
            transactionId,
            command.FunctionCode,
            command.Area,
            plan.Mapping.DeclaredAddress,
            command.PduAddress,
            command.RawValues.ToArray(),
            FormatValue(plan.DecodedValue),
            plan.Binding.TableName,
            plan.Mapping.ColumnName,
            plan.Binding.RowKey,
            plan.RowFingerprint,
            plan.AppliedRowFingerprint,
            plan.CatalogRevision,
            plan.Binding.ApprovedWriteAction,
            expiresAt,
            errorCode,
            reason);

    private static ModbusEndpointWriteEvent Transition(
        ModbusEndpointWriteEvent current,
        DateTimeOffset occurredAt,
        string eventType,
        string state,
        string principal,
        string? errorCode,
        string? reason)
        => current with
        {
            EventId = Guid.NewGuid(),
            OccurredAtUtc = occurredAt,
            EventType = eventType,
            State = state,
            Principal = principal,
            ErrorCode = errorCode,
            Reason = reason,
        };

    private static string FingerprintRow(TableSchema schema, TableRow row)
    {
        byte[] payload = TableRowCodec.Encode(schema, row.Values);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(row.PrimaryKey.Span);
        hash.AppendData(payload);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string FormatValue(object value) => value switch
    {
        bool boolean => boolean ? "TRUE" : "FALSE",
        double floating => floating.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool IsPersistenceFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException;

    private sealed record ModbusEndpointWritePlan(
        long CatalogRevision,
        ModbusTableBinding Binding,
        ModbusColumnMapping Mapping,
        object DecodedValue,
        string RowFingerprint,
        string AppliedRowFingerprint);
}

internal readonly record struct ModbusEndpointWriteCommand(
    byte FunctionCode,
    ModbusRegisterArea Area,
    ushort PduAddress,
    IReadOnlyList<ushort> RawValues);

internal readonly record struct ModbusEndpointStageResult(
    bool Succeeded,
    byte ExceptionCode,
    Guid RequestId);

internal sealed class ModbusEndpointWriteException : InvalidOperationException
{
    internal ModbusEndpointWriteException(
        string code,
        string message,
        Exception? innerException = null,
        byte protocolExceptionCode = ModbusTcpExceptionCodes.ServerDeviceFailure)
        : base(message, innerException)
    {
        Code = code;
        ProtocolExceptionCode = protocolExceptionCode;
    }

    internal string Code { get; }

    internal byte ProtocolExceptionCode { get; }
}
