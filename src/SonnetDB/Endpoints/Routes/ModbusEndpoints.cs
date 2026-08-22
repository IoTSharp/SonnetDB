using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Modbus;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapModbusEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var service = app.Services.GetRequiredService<ModbusEndpointWriteService>();
        var runtimeOptions = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.Modbus;

        app.MapGet("/v1/db/{db}/modbus", async (HttpContext context, string db) =>
        {
            if (!TryResolveDatabase(context, registry, db, out Tsdb database))
                return;
            DatabasePermission permission = DatabaseAccessEvaluator.GetEffectivePermission(context, grants, db);
            if (!await TryRequireDatabasePermissionAsync(context, db, permission, DatabasePermission.Read).ConfigureAwait(false))
                return;

            ModbusOverviewResponse response = BuildModbusOverview(database, runtimeOptions.Enabled);
            await Results.Json(response, ServerJsonContext.Default.ModbusOverviewResponse)
                .ExecuteAsync(context).ConfigureAwait(false);
        });

        app.MapGet("/v1/db/{db}/modbus/writes", async (HttpContext context, string db) =>
        {
            if (!TryResolveDatabase(context, registry, db, out _))
                return;
            DatabasePermission permission = DatabaseAccessEvaluator.GetEffectivePermission(context, grants, db);
            if (!await TryRequireDatabasePermissionAsync(context, db, permission, DatabasePermission.Admin).ConfigureAwait(false))
                return;

            int limit = ParseModbusLimit(context);
            string state = context.Request.Query["state"].ToString().Trim();
            IReadOnlyList<ModbusEndpointWriteResponse> items = service.ListLatest(db, limit)
                .Where(entry => MatchesModbusWriteState(entry.State, state))
                .Select(MapEndpointWrite)
                .ToArray();
            await Results.Json(
                    new ModbusEndpointWriteListResponse(items),
                    ServerJsonContext.Default.ModbusEndpointWriteListResponse)
                .ExecuteAsync(context).ConfigureAwait(false);
        });

        app.MapGet("/v1/db/{db}/modbus/write-audit", async (HttpContext context, string db) =>
        {
            if (!TryResolveDatabase(context, registry, db, out _))
                return;
            DatabasePermission permission = DatabaseAccessEvaluator.GetEffectivePermission(context, grants, db);
            if (!await TryRequireDatabasePermissionAsync(context, db, permission, DatabasePermission.Admin).ConfigureAwait(false))
                return;

            IReadOnlyList<ModbusEndpointWriteResponse> items = service.ListAudit(db, ParseModbusLimit(context))
                .Reverse()
                .Select(MapEndpointWrite)
                .ToArray();
            await Results.Json(
                    new ModbusEndpointWriteListResponse(items),
                    ServerJsonContext.Default.ModbusEndpointWriteListResponse)
                .ExecuteAsync(context).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/modbus/writes/{requestId:guid}/approve", async (
            HttpContext context,
            string db,
            Guid requestId) =>
        {
            if (!TryResolveDatabase(context, registry, db, out Tsdb database))
                return;
            DatabasePermission permission = DatabaseAccessEvaluator.GetEffectivePermission(context, grants, db);
            if (!DatabaseAccessEvaluator.HasPermission(permission, DatabasePermission.Write)
                || !DatabaseAccessEvaluator.HasPermission(permission, DatabasePermission.Admin))
            {
                await WriteSimpleErrorAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "forbidden",
                    $"当前凭据对数据库 '{db}' 没有 endpoint 写审批所需的 Write 与 Admin 权限。")
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                ModbusEndpointWriteEvent result = service.Approve(
                    database,
                    db,
                    requestId,
                    ResolveModbusApprovalPrincipal(context),
                    canWrite: true,
                    canApprove: true);
                await Results.Json(MapEndpointWrite(result), ServerJsonContext.Default.ModbusEndpointWriteResponse)
                    .ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (ModbusEndpointWriteException exception)
            {
                await WriteModbusGovernanceErrorAsync(context, exception).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/modbus/writes/{requestId:guid}/reject", async (
            HttpContext context,
            string db,
            Guid requestId) =>
        {
            if (!TryResolveDatabase(context, registry, db, out _))
                return;
            DatabasePermission permission = DatabaseAccessEvaluator.GetEffectivePermission(context, grants, db);
            if (!await TryRequireDatabasePermissionAsync(context, db, permission, DatabasePermission.Admin).ConfigureAwait(false))
                return;

            ModbusEndpointWriteDecisionRequest? request = context.Request.ContentLength is null or 0
                ? null
                : await ReadJsonAsync(context, ServerJsonContext.Default.ModbusEndpointWriteDecisionRequest)
                    .ConfigureAwait(false);
            try
            {
                ModbusEndpointWriteEvent result = service.Reject(
                    db,
                    requestId,
                    ResolveModbusApprovalPrincipal(context),
                    request?.Reason,
                    canApprove: true);
                await Results.Json(MapEndpointWrite(result), ServerJsonContext.Default.ModbusEndpointWriteResponse)
                    .ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (ModbusEndpointWriteException exception)
            {
                await WriteModbusGovernanceErrorAsync(context, exception).ConfigureAwait(false);
            }
        });
    }

    private static ModbusOverviewResponse BuildModbusOverview(Tsdb database, bool runtimeEnabled)
    {
        ModbusCatalogSnapshot snapshot = database.Modbus.Catalog.CaptureSnapshot();
        ModbusSourceResponse[] sources = snapshot.Sources.Values
            .OrderBy(static source => source.Name, StringComparer.Ordinal)
            .Select(source =>
            {
                ModbusSourceRuntimeStatus status = database.Modbus.GetSourceRuntimeStatus(source.Name);
                return new ModbusSourceResponse(
                    source.Name,
                    source.Host,
                    source.Port,
                    source.UnitId,
                    source.Enabled,
                    status.RuntimeEnabled,
                    status.Health.ToString().ToLowerInvariant(),
                    status.LastErrorCode,
                    snapshot.Revision);
            })
            .ToArray();
        ModbusEndpointResponse[] endpoints = snapshot.Endpoints.Values
            .OrderBy(static endpoint => endpoint.Name, StringComparer.Ordinal)
            .Select(endpoint =>
            {
                ModbusEndpointRuntimeStatus status = database.Modbus.GetEndpointRuntimeStatus(endpoint.Name);
                return new ModbusEndpointResponse(
                    endpoint.Name,
                    endpoint.BindAddress,
                    endpoint.Port,
                    endpoint.UnitId,
                    endpoint.WritePolicy.ToString().ToUpperInvariant(),
                    endpoint.AllowedClientNetworks ?? [],
                    endpoint.MaxConnections,
                    endpoint.Enabled,
                    status.RuntimeEnabled,
                    status.Health.ToString().ToLowerInvariant(),
                    status.LastErrorCode,
                    snapshot.Revision);
            })
            .ToArray();
        ModbusBindingResponse[] bindings = snapshot.Bindings.Values
            .OrderBy(static binding => binding.TableName, StringComparer.Ordinal)
            .Select(binding => new ModbusBindingResponse(
                binding.TableName,
                binding.Direction == ModbusMappingDirection.SourceToTable ? "FROM" : "EXPOSE",
                binding.TargetName,
                binding.RowKey,
                binding.TableMode.ToString().ToUpperInvariant(),
                FormatModbusApprovedWriteAction(binding.ApprovedWriteAction),
                binding.Columns.Select(static mapping => new ModbusMappingResponse(
                    mapping.ColumnName,
                    FormatModbusArea(mapping.Area),
                    mapping.DeclaredAddress,
                    mapping.PduAddress,
                    mapping.RegisterCount,
                    mapping.ValueType == ModbusValueType.String
                        ? $"STRING({mapping.StringLength})"
                        : mapping.ValueType.ToString().ToUpperInvariant(),
                    FormatModbusAccess(mapping.Access))).ToArray()))
            .ToArray();
        return new ModbusOverviewResponse(runtimeEnabled, sources, endpoints, bindings);
    }

    private static ModbusEndpointWriteResponse MapEndpointWrite(ModbusEndpointWriteEvent entry)
        => new(
            entry.RequestId,
            entry.OccurredAtUtc,
            entry.EventType,
            entry.State,
            entry.Principal,
            entry.Endpoint,
            entry.RemoteEndpoint,
            entry.UnitId,
            entry.TransactionId,
            $"0x{entry.FunctionCode:X2}",
            FormatModbusArea(entry.Area),
            entry.DeclaredAddress,
            entry.PduAddress,
            entry.RawValues.Select(static value => $"0x{value:X4}").ToArray(),
            entry.DecodedValue,
            entry.Table,
            entry.Column,
            entry.RowKey,
            entry.CatalogRevision,
            FormatModbusApprovedWriteAction(entry.ApprovedAction),
            entry.ExpiresAtUtc,
            entry.ErrorCode,
            entry.Reason);

    private static int ParseModbusLimit(HttpContext context)
        => int.TryParse(context.Request.Query["limit"], out int limit)
            ? Math.Clamp(limit, 1, 2_000)
            : 200;

    private static bool MatchesModbusWriteState(string currentState, string requestedState)
        => requestedState.Length == 0
           || string.Equals(currentState, requestedState, StringComparison.OrdinalIgnoreCase)
           || (string.Equals(requestedState, "pending", StringComparison.OrdinalIgnoreCase)
               && currentState is "staged" or "applying");

    private static string ResolveModbusApprovalPrincipal(HttpContext context)
    {
        if (BearerAuthMiddleware.GetUser(context) is { } user)
            return user.UserName;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
        return "credential:" + Convert.ToHexString(digest.AsSpan(0, 16));
    }

    private static string FormatModbusArea(ModbusRegisterArea area) => area switch
    {
        ModbusRegisterArea.Coil => "COIL",
        ModbusRegisterArea.DiscreteInput => "DISCRETE_INPUT",
        ModbusRegisterArea.InputRegister => "INPUT_REGISTER",
        ModbusRegisterArea.HoldingRegister => "HOLDING_REGISTER",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "未知的 Modbus 地址空间。"),
    };

    private static string FormatModbusAccess(ModbusAccessMode access) => access switch
    {
        ModbusAccessMode.Read => "READ",
        ModbusAccessMode.Write => "WRITE",
        ModbusAccessMode.ReadWrite => "READ_WRITE",
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, "未知的 Modbus 访问模式。"),
    };

    private static string FormatModbusApprovedWriteAction(ModbusApprovedWriteAction action) => action switch
    {
        ModbusApprovedWriteAction.StageOnly => "STAGE_ONLY",
        ModbusApprovedWriteAction.UpdateTable => "UPDATE_TABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知的 Modbus 审批动作。"),
    };

    private static Task WriteModbusGovernanceErrorAsync(
        HttpContext context,
        ModbusEndpointWriteException exception)
    {
        int statusCode = exception.Code switch
        {
            "forbidden" => StatusCodes.Status403Forbidden,
            "endpoint_write_request_not_found" => StatusCodes.Status404NotFound,
            "endpoint_write_audit_unavailable" => StatusCodes.Status503ServiceUnavailable,
            "endpoint_write_apply_failed" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status409Conflict,
        };
        return WriteSimpleErrorAsync(context, statusCode, exception.Code, exception.Message);
    }
}
