using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Modbus;

internal sealed class ModbusWriteService
{
    private const int MaxPendingConfirmations = 1_024;
    private const int MaxAuditEntries = 200;
    private static readonly TimeSpan _confirmationLifetime = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyList<string> _resultColumns =
    [
        "operation_id", "mode", "source_name", "unit_id", "function_code",
        "table_name", "column_name", "declared_address", "pdu_address", "wire_type",
        "normalized_value", "encoded_values", "catalog_revision", "approval_id",
        "confirmation_token", "expires_at", "result",
    ];
    private static readonly IReadOnlyList<string> _auditColumns =
    [
        "event_id", "operation_id", "occurred_at", "principal", "database", "source_name",
        "table_name", "column_name", "unit_id", "function_code", "declared_address",
        "pdu_address", "event_type", "result", "error_code", "approval_id", "catalog_revision",
    ];

    private readonly object _confirmationSync = new();
    private readonly Dictionary<string, PendingConfirmation> _confirmations = new(StringComparer.Ordinal);
    private readonly IModbusWriteAuditStore _auditStore;
    private readonly ModbusSourceOperationCoordinator _operationCoordinator;
    private readonly ModbusRuntimeOptions _options;
    private readonly TimeProvider _timeProvider;

    internal ModbusWriteService(
        IModbusWriteAuditStore auditStore,
        ModbusSourceOperationCoordinator operationCoordinator,
        IOptions<ServerOptions> options,
        TimeProvider timeProvider)
    {
        _auditStore = auditStore;
        _operationCoordinator = operationCoordinator;
        _options = options.Value.Modbus;
        _timeProvider = timeProvider;
    }

    internal async Task<SelectExecutionResult> ExecuteAsync(
        Tsdb database,
        string databaseName,
        WriteModbusStatement statement,
        string principal,
        bool canWrite,
        bool canControl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        if (!canWrite)
            throw new ModbusWriteException("forbidden", "WRITE MODBUS 需要当前数据库的写权限。");
        if (!canControl)
            throw new ModbusWriteException("forbidden", "WRITE MODBUS 需要当前数据库的 Modbus 控制写权限（Admin）。");

        return statement.Mode == ModbusWriteMode.Confirm
            ? await ConfirmAsync(
                database,
                databaseName,
                statement,
                principal,
                cancellationToken).ConfigureAwait(false)
            : Preview(database, databaseName, statement, principal);
    }

    internal SelectExecutionResult ShowAudit(string databaseName)
    {
        IReadOnlyList<ModbusWriteAuditEntry> entries;
        try
        {
            entries = _auditStore.List(databaseName, MaxAuditEntries);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw AuditUnavailable(exception);
        }

        var rows = entries.Select(static entry => (IReadOnlyList<object?>)new object?[]
        {
            entry.EventId.ToString("D", CultureInfo.InvariantCulture),
            entry.OperationId.ToString("D", CultureInfo.InvariantCulture),
            entry.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
            entry.Principal,
            entry.Database,
            entry.Source,
            entry.Table,
            entry.Column,
            (long)entry.UnitId,
            $"0x{entry.FunctionCode:X2}",
            (long)entry.DeclaredAddress,
            (long)entry.PduAddress,
            entry.EventType,
            entry.Result,
            entry.ErrorCode,
            entry.ApprovalId?.ToString("D", CultureInfo.InvariantCulture),
            entry.CatalogRevision,
        }).ToArray();
        return new SelectExecutionResult(_auditColumns, rows);
    }

    private SelectExecutionResult Preview(
        Tsdb database,
        string databaseName,
        WriteModbusStatement statement,
        string principal)
    {
        ModbusWritePlan plan = CreatePlan(database, statement);
        Guid operationId = Guid.NewGuid();
        if (statement.Mode == ModbusWriteMode.DryRun)
        {
            AppendAudit(CreateAudit(
                plan,
                operationId,
                principal,
                databaseName,
                "dry_run",
                "validated",
                errorCode: null,
                approvalId: null));
            return BuildResult(plan, operationId, "dry_run", null, null, "validated");
        }

        EnsureRuntimeEnabled(plan.Source);
        Guid approvalId = Guid.NewGuid();
        DateTimeOffset expiresAt = _timeProvider.GetUtcNow().Add(_confirmationLifetime);
        string token;
        lock (_confirmationSync)
        {
            RemoveExpiredConfirmationsLocked(_timeProvider.GetUtcNow());
            if (_confirmations.Count >= MaxPendingConfirmations)
            {
                throw new ModbusWriteException(
                    "modbus_write_preview_capacity",
                    "待确认的 Modbus 写预览已达到服务端上限，请等待旧预览过期后重试。");
            }

            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var pending = new PendingConfirmation(
                operationId,
                approvalId,
                principal,
                databaseName,
                plan.Source.Name,
                plan.Binding.TableName,
                plan.Mapping.ColumnName,
                plan.CatalogRevision,
                plan.RowFingerprint,
                plan.EncodedValues.ToArray(),
                plan.Source.UnitId,
                new ModbusWritePayload(
                    plan.Mapping.Area,
                    plan.Mapping.PduAddress,
                    plan.EncodedValues).FunctionCode,
                plan.Mapping.DeclaredAddress,
                plan.Mapping.PduAddress,
                expiresAt);
            AppendAudit(CreateAudit(
                plan,
                operationId,
                principal,
                databaseName,
                "preview",
                "pending_confirmation",
                errorCode: null,
                approvalId));
            _confirmations.Add(token, pending);
        }

        return BuildResult(
            plan,
            operationId,
            "preview",
            approvalId,
            (token, expiresAt),
            "pending_confirmation");
    }

    private async Task<SelectExecutionResult> ConfirmAsync(
        Tsdb database,
        string databaseName,
        WriteModbusStatement statement,
        string principal,
        CancellationToken cancellationToken)
    {
        string token = RequireConfirmationToken(statement.ConfirmationToken);
        PendingConfirmation pending = ConsumeConfirmation(token, principal);
        ModbusWritePlan plan;
        try
        {
            if (pending.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                throw new ModbusWriteException(
                    "modbus_write_confirmation_expired",
                    "确认令牌已过期，请重新 PREVIEW。");
            }
            if (!string.Equals(pending.Database, databaseName, StringComparison.Ordinal))
                throw ConfirmationMismatch("数据库");
            if (database.Modbus.Revision != pending.CatalogRevision)
            {
                throw new ModbusWriteException(
                    "modbus_write_catalog_changed",
                    "Modbus catalog 已在预览后变化，旧确认令牌已失效，请重新 PREVIEW。");
            }

            plan = CreatePlan(database, statement);
            ValidateConfirmationContext(plan, pending);
            EnsureRuntimeEnabled(plan.Source);
        }
        catch (ModbusWriteException exception)
        {
            AppendAudit(CreateAudit(
                pending,
                principal,
                "confirm",
                "failed",
                exception.Code));
            throw;
        }

        ModbusSourceOperationCoordinator.Lease operationLease;
        try
        {
            operationLease = await _operationCoordinator.AcquireAsync(
                databaseName,
                plan.Source.Name,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AppendAudit(CreateAudit(
                pending,
                principal,
                "confirm",
                "failed",
                "modbus_write_cancelled"));
            throw;
        }

        await using (operationLease)
        {
            return await ExecuteConfirmedUnderLeaseAsync(
                database,
                databaseName,
                statement,
                principal,
                pending,
                plan,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SelectExecutionResult> ExecuteConfirmedUnderLeaseAsync(
        Tsdb database,
        string databaseName,
        WriteModbusStatement statement,
        string principal,
        PendingConfirmation pending,
        ModbusWritePlan plan,
        CancellationToken cancellationToken)
    {
        // 等待轮询让锁期间 catalog 或当前行仍可能被其它管理/DML 请求改变，联网前必须重新验证。
        try
        {
            plan = CreatePlan(database, statement);
            ValidateConfirmationContext(plan, pending);
            EnsureRuntimeEnabled(plan.Source);
        }
        catch (ModbusWriteException exception)
        {
            AppendAudit(CreateAudit(
                pending,
                principal,
                "confirm",
                "failed",
                exception.Code));
            throw;
        }
        AppendAudit(CreateAudit(
            plan,
            pending.OperationId,
            principal,
            databaseName,
            "confirm",
            "started",
            errorCode: null,
            pending.ApprovalId));

        try
        {
            await using var client = new ModbusTcpMasterClient();
            await client.WriteAsync(
                plan.Source,
                new ModbusWritePayload(plan.Mapping.Area, plan.Mapping.PduAddress, plan.EncodedValues),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            string errorCode = GetRemoteErrorCode(exception);
            AppendAudit(CreateAudit(
                plan,
                pending.OperationId,
                principal,
                databaseName,
                "confirm",
                "failed",
                errorCode,
                pending.ApprovalId));
            if (exception is OperationCanceledException)
                throw;
            throw new ModbusWriteException(errorCode, "Modbus 远端写入未收到成功确认，本地 LATEST 行未更新。", exception);
        }

        // 先持久化远端成功事实；只有该审计落盘后才允许更新本地镜像。
        AppendAudit(CreateAudit(
            plan,
            pending.OperationId,
            principal,
            databaseName,
            "confirm",
            "remote_succeeded",
            errorCode: null,
            pending.ApprovalId));

        try
        {
            UpdateLocalLatestRow(database, plan);
        }
        catch (Exception exception)
        {
            AppendAudit(CreateAudit(
                plan,
                pending.OperationId,
                principal,
                databaseName,
                "confirm",
                "local_failed",
                "modbus_write_local_update_failed",
                pending.ApprovalId));
            throw new ModbusWriteException(
                "modbus_write_local_update_failed",
                "设备已确认远端写入，但本地 LATEST 行发生冲突或约束失败，未报告本地成功。",
                exception);
        }

        return BuildResult(
            plan,
            pending.OperationId,
            "confirm",
            pending.ApprovalId,
            confirmation: null,
            "succeeded");
    }

    private ModbusWritePlan CreatePlan(Tsdb database, WriteModbusStatement statement)
    {
        ModbusCatalogSnapshot snapshot = database.Modbus.Catalog.CaptureSnapshot();
        ModbusTableBinding binding = snapshot.Bindings.GetValueOrDefault(statement.TableName)
            ?? throw new ModbusWriteException(
                "modbus_write_binding_not_found",
                $"table '{statement.TableName}' 不存在 Modbus 绑定。");
        if (binding.Direction != ModbusMappingDirection.SourceToTable)
            throw new ModbusWriteException("modbus_write_not_source", "WRITE MODBUS 只能写入 FROM MODBUS SOURCE 表。");
        if (binding.TableMode != ModbusTableMode.Latest)
            throw new ModbusWriteException("modbus_write_not_latest", "WRITE MODBUS 不允许修改 HISTORY 样本，只支持 LATEST 表。");

        ModbusColumnMapping mapping = binding.Columns.FirstOrDefault(candidate =>
                string.Equals(candidate.ColumnName, statement.ColumnName, StringComparison.Ordinal))
            ?? throw new ModbusWriteException(
                "modbus_write_column_not_mapped",
                $"列 '{statement.ColumnName}' 不是 table '{statement.TableName}' 的 Modbus 映射列。");
        if (mapping.Access == ModbusAccessMode.Read)
            throw new ModbusWriteException("modbus_write_read_only", $"列 '{mapping.ColumnName}' 声明为 ACCESS READ。");
        if (mapping.Area is not ModbusRegisterArea.Coil and not ModbusRegisterArea.HoldingRegister
            || mapping.BitIndex is not null)
        {
            throw new ModbusWriteException(
                "modbus_write_area_read_only",
                "受限远端写只支持完整 Coil 或 Holding Register 映射，不支持输入区或寄存器 BIT 读改写。");
        }
        if (mapping.Area == ModbusRegisterArea.HoldingRegister && mapping.RegisterCount > 123)
        {
            throw new ModbusWriteException(
                "modbus_write_too_large",
                "单次受限写最多支持 123 个 Holding Register；不会拆分为可能部分成功的多个请求。");
        }

        ModbusSourceDefinition source = snapshot.Sources.GetValueOrDefault(binding.TargetName)
            ?? throw new ModbusWriteException(
                "modbus_write_source_not_found",
                $"MODBUS SOURCE '{binding.TargetName}' 不存在。");
        TableStore store = database.Tables.Open(binding.TableName);
        IReadOnlyList<TableRow> rows = store.Scan(limit: 2);
        if (rows.Count != 1)
        {
            throw new ModbusWriteException(
                "modbus_write_row_not_single",
                $"WRITE MODBUS 必须命中一个当前逻辑行，table '{binding.TableName}' 当前命中 {rows.Count} 行。");
        }

        object value = EvaluateLiteral(statement.Value);
        var encoded = new ushort[mapping.RegisterCount];
        object normalizedValue;
        try
        {
            _ = ModbusValueCodec.Encode(
                value,
                encoded,
                mapping.Area,
                mapping.ValueType,
                mapping.StringLength,
                mapping.BitIndex ?? 0,
                mapping.ByteOrder,
                mapping.WordOrder,
                mapping.Scale,
                mapping.Offset);
            normalizedValue = ModbusValueCodec.Decode(
                encoded,
                mapping.Area,
                mapping.ValueType,
                mapping.StringLength,
                mapping.BitIndex ?? 0,
                mapping.ByteOrder,
                mapping.WordOrder,
                mapping.Scale,
                mapping.Offset);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or FormatException)
        {
            throw new ModbusWriteException(
                "modbus_write_value_invalid",
                $"值无法按列 '{mapping.ColumnName}' 的 Modbus wire type 无损编码。",
                exception);
        }
        string fingerprint = FingerprintRow(store.Schema, rows[0]);
        return new ModbusWritePlan(
            snapshot.Revision,
            source,
            binding,
            mapping,
            normalizedValue,
            encoded,
            fingerprint);
    }

    private void EnsureRuntimeEnabled(ModbusSourceDefinition source)
    {
        if (!_options.Enabled)
            throw new ModbusWriteException("modbus_write_runtime_disabled", "服务端 Modbus runtime 全局门禁未启用。");
        if (!source.Enabled)
            throw new ModbusWriteException("modbus_write_source_disabled", $"MODBUS SOURCE '{source.Name}' 未启用。");
    }

    private PendingConfirmation ConsumeConfirmation(string token, string principal)
    {
        lock (_confirmationSync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (!_confirmations.TryGetValue(token, out PendingConfirmation? pending))
                throw new ModbusWriteException("modbus_write_confirmation_invalid", "确认令牌无效、已使用或已被回收。");
            if (!string.Equals(pending.Principal, principal, StringComparison.Ordinal))
                throw ConfirmationMismatch("用户");

            _confirmations.Remove(token);
            RemoveExpiredConfirmationsLocked(now);
            return pending;
        }
    }

    private static void ValidateConfirmationContext(ModbusWritePlan plan, PendingConfirmation pending)
    {
        if (plan.CatalogRevision != pending.CatalogRevision)
            throw new ModbusWriteException("modbus_write_catalog_changed", "Modbus catalog 已变化，请重新 PREVIEW。");
        if (!string.Equals(plan.Source.Name, pending.Source, StringComparison.Ordinal)
            || !string.Equals(plan.Binding.TableName, pending.Table, StringComparison.Ordinal)
            || !string.Equals(plan.Mapping.ColumnName, pending.Column, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(plan.RowFingerprint),
                System.Text.Encoding.ASCII.GetBytes(pending.RowFingerprint))
            || !plan.EncodedValues.SequenceEqual(pending.EncodedValues))
        {
            throw ConfirmationMismatch("source、表、列、当前行或规范化值");
        }
    }

    private static void UpdateLocalLatestRow(Tsdb database, ModbusWritePlan plan)
    {
        _ = database.ExecuteSchemaMutation(() =>
            database.Tables.ExecuteLocked(() =>
            {
                if (database.Modbus.Revision != plan.CatalogRevision)
                    throw new InvalidOperationException("远端写入期间 Modbus catalog 已变化。");

                TableStore store = database.Tables.Open(plan.Binding.TableName);
                TableSchema schema = store.Schema;
                IReadOnlyList<TableRow> rows = store.Scan(limit: 2);
                if (rows.Count != 1 || !string.Equals(
                        FingerprintRow(schema, rows[0]),
                        plan.RowFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("预览后的当前逻辑行已变化。");
                }

                TableRow row = rows[0];
                TableColumn column = schema.TryGetColumn(plan.Mapping.ColumnName)
                    ?? throw new InvalidOperationException($"列 '{plan.Mapping.ColumnName}' 已不存在。");
                var newValues = row.Values.ToArray();
                newValues[column.Ordinal] = plan.NormalizedValue;
                long? expectedRowVersion = null;
                if (schema.RowVersionColumn is { } rowVersionColumn)
                {
                    expectedRowVersion = row.Values[rowVersionColumn.Ordinal] is null
                        ? 0L
                        : Convert.ToInt64(row.Values[rowVersionColumn.Ordinal], CultureInfo.InvariantCulture);
                    newValues[rowVersionColumn.Ordinal] = checked(expectedRowVersion.Value + 1L);
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

    private static object EvaluateLiteral(SqlExpression expression) => expression switch
    {
        LiteralExpression { Kind: SqlLiteralKind.Boolean } literal => literal.BooleanValue,
        LiteralExpression { Kind: SqlLiteralKind.Integer } literal => literal.IntegerValue,
        LiteralExpression { Kind: SqlLiteralKind.Float } literal => literal.FloatValue,
        LiteralExpression { Kind: SqlLiteralKind.String, StringValue: not null } literal => literal.StringValue,
        UnaryExpression
        {
            Operator: SqlUnaryOperator.Negate,
            Operand: LiteralExpression { Kind: SqlLiteralKind.Integer } literal,
        } => checked(-literal.IntegerValue),
        UnaryExpression
        {
            Operator: SqlUnaryOperator.Negate,
            Operand: LiteralExpression { Kind: SqlLiteralKind.Float } literal,
        } => -literal.FloatValue,
        ParameterExpression => throw new ModbusWriteException(
            "modbus_write_parameter_unbound",
            "WRITE MODBUS 的值参数尚未绑定。"),
        _ => throw new ModbusWriteException(
            "modbus_write_value_not_literal",
            "WRITE MODBUS 只接受一个已绑定的整数、浮点、布尔或字符串字面量。"),
    };

    private static string RequireConfirmationToken(SqlExpression? expression)
        => expression is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: { Length: > 0 } token }
            ? token
            : throw new ModbusWriteException(
                "modbus_write_confirmation_invalid",
                "CONFIRM 必须提供非空的一次性字符串令牌。");

    private void RemoveExpiredConfirmationsLocked(DateTimeOffset now)
    {
        string[] expired = _confirmations
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (string token in expired)
            _confirmations.Remove(token);
    }

    private void AppendAudit(ModbusWriteAuditEntry entry)
    {
        try
        {
            _auditStore.Append(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw AuditUnavailable(exception);
        }
    }

    private ModbusWriteAuditEntry CreateAudit(
        ModbusWritePlan plan,
        Guid operationId,
        string principal,
        string database,
        string eventType,
        string result,
        string? errorCode,
        Guid? approvalId)
        => new(
            Guid.NewGuid(),
            operationId,
            _timeProvider.GetUtcNow(),
            principal,
            database,
            plan.Source.Name,
            plan.Binding.TableName,
            plan.Mapping.ColumnName,
            plan.Source.UnitId,
            new ModbusWritePayload(plan.Mapping.Area, plan.Mapping.PduAddress, plan.EncodedValues).FunctionCode,
            plan.Mapping.DeclaredAddress,
            plan.Mapping.PduAddress,
            eventType,
            result,
            errorCode,
            approvalId,
            plan.CatalogRevision);

    private ModbusWriteAuditEntry CreateAudit(
        PendingConfirmation pending,
        string principal,
        string eventType,
        string result,
        string? errorCode)
        => new(
            Guid.NewGuid(),
            pending.OperationId,
            _timeProvider.GetUtcNow(),
            principal,
            pending.Database,
            pending.Source,
            pending.Table,
            pending.Column,
            pending.UnitId,
            pending.FunctionCode,
            pending.DeclaredAddress,
            pending.PduAddress,
            eventType,
            result,
            errorCode,
            pending.ApprovalId,
            pending.CatalogRevision);

    private static SelectExecutionResult BuildResult(
        ModbusWritePlan plan,
        Guid operationId,
        string mode,
        Guid? approvalId,
        (string Token, DateTimeOffset ExpiresAt)? confirmation,
        string result)
    {
        byte functionCode = new ModbusWritePayload(
            plan.Mapping.Area,
            plan.Mapping.PduAddress,
            plan.EncodedValues).FunctionCode;
        IReadOnlyList<object?> row = new object?[]
        {
            operationId.ToString("D", CultureInfo.InvariantCulture),
            mode,
            plan.Source.Name,
            (long)plan.Source.UnitId,
            $"0x{functionCode:X2}",
            plan.Binding.TableName,
            plan.Mapping.ColumnName,
            (long)plan.Mapping.DeclaredAddress,
            (long)plan.Mapping.PduAddress,
            FormatWireType(plan.Mapping),
            FormatValue(plan.NormalizedValue),
            string.Join(",", plan.EncodedValues.Select(static value => $"0x{value:X4}")),
            plan.CatalogRevision,
            approvalId?.ToString("D", CultureInfo.InvariantCulture),
            confirmation?.Token,
            confirmation?.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
            result,
        };
        return new SelectExecutionResult(_resultColumns, [row]);
    }

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

    private static string FormatWireType(ModbusColumnMapping mapping)
        => mapping.ValueType == ModbusValueType.String
            ? $"STRING({mapping.StringLength.ToString(CultureInfo.InvariantCulture)})"
            : mapping.ValueType.ToString().ToUpperInvariant();

    private static string GetRemoteErrorCode(Exception exception) => exception switch
    {
        TimeoutException => "modbus_write_timeout",
        ModbusProtocolException protocol => protocol.ErrorCode,
        SocketException => "modbus_write_connection_error",
        IOException => "modbus_write_connection_error",
        OperationCanceledException => "modbus_write_cancelled",
        _ => "modbus_write_remote_error",
    };

    private static ModbusWriteException AuditUnavailable(Exception exception)
        => new(
            "modbus_write_audit_unavailable",
            "Modbus 写审计无法持久化；操作已失败关闭。",
            exception);

    private static ModbusWriteException ConfirmationMismatch(string field)
        => new(
            "modbus_write_confirmation_mismatch",
            $"确认令牌绑定的{field}与当前请求不一致，请重新 PREVIEW。");

    private sealed record ModbusWritePlan(
        long CatalogRevision,
        ModbusSourceDefinition Source,
        ModbusTableBinding Binding,
        ModbusColumnMapping Mapping,
        object NormalizedValue,
        IReadOnlyList<ushort> EncodedValues,
        string RowFingerprint);

    private sealed record PendingConfirmation(
        Guid OperationId,
        Guid ApprovalId,
        string Principal,
        string Database,
        string Source,
        string Table,
        string Column,
        long CatalogRevision,
        string RowFingerprint,
        IReadOnlyList<ushort> EncodedValues,
        byte UnitId,
        byte FunctionCode,
        int DeclaredAddress,
        ushort PduAddress,
        DateTimeOffset ExpiresAt);
}

internal sealed class ModbusWriteException : InvalidOperationException
{
    internal ModbusWriteException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    internal string Code { get; }
}
