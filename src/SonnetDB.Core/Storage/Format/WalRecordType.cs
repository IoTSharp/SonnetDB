namespace SonnetDB.Storage.Format;

/// <summary>
/// WAL 记录类型，用于区分 WAL 日志中不同操作的条目。
/// </summary>
public enum WalRecordType : byte
{
    /// <summary>未知记录类型（占位，不应出现在有效 WAL 中）。</summary>
    Unknown = 0,

    /// <summary>数据写入记录（包含一个时序数据点）。</summary>
    WritePoint = 1,

    /// <summary>检查点记录（标记 Flush 完成后的截断位置）。</summary>
    Checkpoint = 2,

    /// <summary>序列目录更新记录（新建 SeriesId 映射）。</summary>
    CreateSeries = 3,

    /// <summary>WAL 截断记录（标记该位置之前的记录已全部落盘）。</summary>
    Truncate = 4,

    /// <summary>删除记录（声明某 (SeriesId, FieldName) 在时间窗内的数据已被墓碑标记）。</summary>
    Delete = 5,

    /// <summary>显式 WAL v2 中的单个多字段 Point；一个载荷 CRC 覆盖全部字段。</summary>
    AtomicPoint = 6,
}
