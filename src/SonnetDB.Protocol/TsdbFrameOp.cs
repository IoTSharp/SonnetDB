namespace SonnetDB.Protocol;

/// <summary>
/// 时序 service（<see cref="FrameService.Tsdb"/>）的 opcode。
/// </summary>
public enum TsdbFrameOp : byte
{
    /// <summary>
    /// 列式批量写（#237）。请求：db, measurement, flushMode, 列式块序列
    /// （每块 = 一组 tag + 时间戳列 + 若干字段列，对齐 IoTDB Tablet / PG COPY BINARY 的列式批思路）；
    /// 响应：written（成功写入点数）。
    /// </summary>
    WriteColumnar = 1,
}
