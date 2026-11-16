namespace SonnetDB.Engine;

/// <summary>
/// 新写入在同一 (series, field, timestamp) 上发生重复时的内存层处理策略。
/// </summary>
/// <remarks>
/// 默认 <see cref="KeepAll"/> 保持现有语义。<see cref="KeepFirst"/> 与
/// <see cref="KeepLast"/> 仅约束当前 MemTable 及其 WAL 回放；已经落盘的旧 Segment
/// 不会被静默重写。需要清理历史重复点时，应使用单独的迁移/重写流程。
/// </remarks>
public enum DuplicateTimestampPolicy
{
    /// <summary>保留所有重复点（默认，兼容现有行为）。</summary>
    KeepAll = 0,

    /// <summary>同一活跃 MemTable 中保留最先写入的点，后续重复写仍可进入 WAL。</summary>
    KeepFirst = 1,

    /// <summary>同一活跃 MemTable 中使用最后写入的值替换先前重复点，点数保持不变。</summary>
    KeepLast = 2,
}
