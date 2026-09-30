using System.Text.Json.Serialization;

namespace SonnetDB.Cdc;

/// <summary>本地 CDC 接收端的快照与增量阶段。</summary>
public enum CdcSnapshotPhase
{
    /// <summary>正在接收固定读视图的快照页。</summary>
    Snapshot = 0,

    /// <summary>快照完整，正在接收快照位点之后的增量。</summary>
    Incremental = 1,
}

/// <summary>固定读视图和增量流共享的快照身份。</summary>
/// <param name="SnapshotId">固定读视图的稳定标识；恢复时必须使用同一标识。</param>
/// <param name="Source">源数据库或连接的稳定标识。</param>
/// <param name="Entity">源集合或表的稳定名称。</param>
/// <param name="Schema">源实体 schema 的稳定名称。</param>
/// <param name="SchemaVersion">源实体 schema 的版本。</param>
/// <param name="Checkpoint">快照读视图包含的最后一个分区位点。</param>
/// <param name="RowCount">固定读视图中必须接收的总行数。</param>
public sealed record CdcSnapshotDescriptor(
    string SnapshotId,
    string Source,
    string Entity,
    string Schema,
    int SchemaVersion,
    CdcCheckpoint Checkpoint,
    long RowCount);

/// <summary>本地快照或物化结果中的一行。</summary>
/// <param name="Key">实体的稳定业务键。</param>
/// <param name="ValueJson">实体完整的 JSON 值。</param>
public sealed record CdcSnapshotRow(string Key, string ValueJson);

/// <summary>本地快照接收端的容量和批次限制。</summary>
public sealed record CdcSnapshotReplicaOptions
{
    /// <summary>允许保留的最大物化行数。</summary>
    public int MaxRows { get; init; } = 10_000;

    /// <summary>完整状态文件允许占用的最大字节数。</summary>
    public int MaxBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>单次快照写入、增量应用或物化读取的最大条数。</summary>
    public int MaxBatchRows { get; init; } = 1_000;

    /// <summary>单次批次的最大 UTF-8 字节数；行计入键和值，增量计入完整事件。</summary>
    public int MaxBatchBytes { get; init; } = 4 * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRows, 100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBytes, 256 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchRows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchRows, MaxRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchBytes, MaxBytes);
    }
}

/// <summary>持久化快照接收端的一致恢复状态。</summary>
/// <param name="Descriptor">固定读视图身份。</param>
/// <param name="Phase">当前接收阶段。</param>
/// <param name="SnapshotRowsCopied">已持久化的快照行数，也是下一页的起始序号。</param>
/// <param name="LastSnapshotKey">已持久化快照中的最后一个键。</param>
/// <param name="AppliedCheckpoint">快照边界或最后一个已应用的增量位点。</param>
/// <param name="MaterializedRows">当前物化行数。</param>
public sealed record CdcSnapshotReplicaState(
    CdcSnapshotDescriptor Descriptor,
    CdcSnapshotPhase Phase,
    long SnapshotRowsCopied,
    string? LastSnapshotKey,
    CdcCheckpoint AppliedCheckpoint,
    int MaterializedRows);

/// <summary>一次有界物化读取的结果。</summary>
/// <param name="Rows">按键的序数顺序排列的行。</param>
/// <param name="NextKey">继续读取时使用的排他起始键。</param>
/// <param name="HasMore">是否仍有后续行。</param>
/// <param name="EncodedBytes">返回行的键和值的 UTF-8 总字节数。</param>
/// <param name="AppliedCheckpoint">本批次读取对应的增量位点。</param>
public sealed record CdcSnapshotRowBatch(
    IReadOnlyList<CdcSnapshotRow> Rows,
    string? NextKey,
    bool HasMore,
    int EncodedBytes,
    CdcCheckpoint AppliedCheckpoint);

internal sealed record CdcSnapshotReplicaDocument(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] CdcSnapshotDescriptor Descriptor,
    [property: JsonRequired] CdcSnapshotPhase Phase,
    [property: JsonRequired] long SnapshotRowsCopied,
    [property: JsonRequired] string? LastSnapshotKey,
    [property: JsonRequired] long AppliedOffset,
    [property: JsonRequired] string? LastEventHash,
    [property: JsonRequired] Dictionary<string, string> Rows);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CdcSnapshotReplicaDocument))]
[JsonSerializable(typeof(CdcSnapshotDescriptor))]
[JsonSerializable(typeof(CdcSnapshotRow))]
internal sealed partial class CdcSnapshotReplicaJsonContext : JsonSerializerContext;
