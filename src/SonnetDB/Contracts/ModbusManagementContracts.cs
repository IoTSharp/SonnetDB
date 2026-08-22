namespace SonnetDB.Contracts;

/// <summary>
/// Modbus 管理面的数据库级概览。
/// </summary>
/// <param name="RuntimeEnabled">服务端 Modbus 全局门禁是否启用。</param>
/// <param name="Sources">当前数据库的 master source。</param>
/// <param name="Endpoints">当前数据库的 slave endpoint。</param>
/// <param name="Bindings">关系表与 Modbus 对象的映射摘要。</param>
public sealed record ModbusOverviewResponse(
    bool RuntimeEnabled,
    IReadOnlyList<ModbusSourceResponse> Sources,
    IReadOnlyList<ModbusEndpointResponse> Endpoints,
    IReadOnlyList<ModbusBindingResponse> Bindings);

/// <summary>
/// Modbus master source 的稳定管理视图。
/// </summary>
public sealed record ModbusSourceResponse(
    string Name,
    string Host,
    int Port,
    int UnitId,
    bool ConfiguredEnabled,
    bool RuntimeEnabled,
    string Health,
    string? LastErrorCode,
    long CatalogRevision);

/// <summary>
/// Modbus slave endpoint 的稳定管理视图。
/// </summary>
public sealed record ModbusEndpointResponse(
    string Name,
    string BindAddress,
    int Port,
    int UnitId,
    string WritePolicy,
    IReadOnlyList<string> Allowlist,
    int MaxConnections,
    bool ConfiguredEnabled,
    bool RuntimeEnabled,
    string Health,
    string? LastErrorCode,
    long CatalogRevision);

/// <summary>
/// 一张关系表的 Modbus 映射摘要。
/// </summary>
public sealed record ModbusBindingResponse(
    string Table,
    string Direction,
    string Target,
    long? RowKey,
    string TableMode,
    string ApprovedWriteAction,
    IReadOnlyList<ModbusMappingResponse> Mappings);

/// <summary>
/// 单个关系列的 Modbus 地址映射。
/// </summary>
public sealed record ModbusMappingResponse(
    string Column,
    string Area,
    int DeclaredAddress,
    int PduAddress,
    int RegisterCount,
    string WireType,
    string Access);

/// <summary>
/// Endpoint 外部写请求的公开治理视图。
/// </summary>
public sealed record ModbusEndpointWriteResponse(
    Guid RequestId,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string State,
    string Principal,
    string Endpoint,
    string RemoteEndpoint,
    int UnitId,
    int TransactionId,
    string FunctionCode,
    string Area,
    int DeclaredAddress,
    int PduAddress,
    IReadOnlyList<string> RawValues,
    string? DecodedValue,
    string? Table,
    string? Column,
    long? RowKey,
    long CatalogRevision,
    string ApprovedWriteAction,
    DateTimeOffset? ExpiresAtUtc,
    string? ErrorCode,
    string? Reason);

/// <summary>
/// Endpoint 外部写请求列表。
/// </summary>
/// <param name="Items">按最近事件时间倒序排列的请求或审计事件。</param>
public sealed record ModbusEndpointWriteListResponse(
    IReadOnlyList<ModbusEndpointWriteResponse> Items);

/// <summary>
/// 拒绝 endpoint 外部写时附带的可选操作说明。
/// </summary>
/// <param name="Reason">不超过 512 个字符的拒绝原因。</param>
public sealed record ModbusEndpointWriteDecisionRequest(string? Reason = null);
