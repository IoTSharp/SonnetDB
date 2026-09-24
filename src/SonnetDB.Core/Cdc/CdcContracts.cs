namespace SonnetDB.Cdc;

/// <summary>
/// CDC 事件的写入操作类型。
/// </summary>
public enum CdcOperation
{
    /// <summary>新增实体。</summary>
    Insert = 0,

    /// <summary>修改实体。</summary>
    Update = 1,

    /// <summary>删除实体。</summary>
    Delete = 2,
}

/// <summary>
/// CDC 消费位点。位点在同一分区内单调递增。
/// </summary>
/// <param name="Partition">分区编号。</param>
/// <param name="Offset">分区内的非负偏移量。</param>
public readonly record struct CdcCheckpoint(long Partition, long Offset);

/// <summary>
/// CDC 事件的版本化元数据。
/// </summary>
/// <param name="ContractVersion">线上的 CDC 合同版本。</param>
/// <param name="Schema">实体 schema 的稳定名称。</param>
/// <param name="SchemaVersion">实体 schema 的版本。</param>
/// <param name="Operation">变更操作。</param>
/// <param name="Checkpoint">可用于恢复消费的分区位点。</param>
public sealed record CdcEventMetadata(
    int ContractVersion,
    string Schema,
    int SchemaVersion,
    CdcOperation Operation,
    CdcCheckpoint Checkpoint);

/// <summary>
/// 一条不可变、可跨进程传输的版本化 CDC 事件。
/// </summary>
/// <param name="EventId">事件的稳定幂等标识。</param>
/// <param name="Source">产生事件的数据库或连接标识。</param>
/// <param name="Entity">实体集合、表或 measurement 的稳定名称。</param>
/// <param name="Key">被变更实体的业务键。</param>
/// <param name="Sequence">源实体内单调递增的变更序号。</param>
/// <param name="OccurredAtUtc">事件发生时间，必须为 UTC。</param>
/// <param name="Metadata">版本、schema、操作和恢复位点元数据。</param>
/// <param name="BeforeJson">变更前的 JSON 值；插入时为空。</param>
/// <param name="AfterJson">变更后的 JSON 值；删除时为空。</param>
public sealed record CdcEvent(
    string EventId,
    string Source,
    string Entity,
    string Key,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    CdcEventMetadata Metadata,
    string? BeforeJson,
    string? AfterJson);

/// <summary>
/// CDC 合同格式错误。
/// </summary>
public class CdcFormatException : FormatException
{
    /// <summary>
    /// 使用指定消息创建格式错误。
    /// </summary>
    /// <param name="message">错误描述。</param>
    public CdcFormatException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 使用指定消息和内部异常创建格式错误。
    /// </summary>
    /// <param name="message">错误描述。</param>
    /// <param name="innerException">底层错误。</param>
    public CdcFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// CDC 合同版本不受当前实现支持。
/// </summary>
public sealed class CdcUnsupportedVersionException : CdcFormatException
{
    /// <summary>
    /// 创建版本错误。
    /// </summary>
    /// <param name="version">收到的版本号。</param>
    public CdcUnsupportedVersionException(int version)
        : base($"不支持 CDC 合同版本 {version}。当前支持版本为 {CdcEventCodec.CurrentContractVersion}。")
        => Version = version;

    /// <summary>收到的版本号。</summary>
    public int Version { get; }
}

/// <summary>
/// CDC 实体 schema 版本不受当前实现支持。
/// </summary>
public sealed class CdcUnsupportedSchemaVersionException : CdcFormatException
{
    /// <summary>
    /// 创建 schema 版本错误。
    /// </summary>
    /// <param name="schema">schema 名称。</param>
    /// <param name="version">收到的 schema 版本号。</param>
    public CdcUnsupportedSchemaVersionException(string schema, int version)
        : base($"不支持 CDC schema '{schema}' 的版本 {version}。当前支持版本为 {CdcEventCodec.CurrentSchemaVersion}。")
    {
        Schema = schema;
        Version = version;
    }

    /// <summary>收到的 schema 名称。</summary>
    public string Schema { get; }

    /// <summary>收到的 schema 版本号。</summary>
    public int Version { get; }
}
