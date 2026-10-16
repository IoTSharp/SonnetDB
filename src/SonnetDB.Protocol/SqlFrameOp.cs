namespace SonnetDB.Protocol;

/// <summary>
/// sql service（<see cref="FrameService.Sql"/>）的 opcode（M28 P5b #238）。
/// </summary>
public enum SqlFrameOp : byte
{
    /// <summary>
    /// 只读查询（SELECT / SHOW / DESCRIBE / EXPLAIN），响应为流式列式结果集：
    /// meta 帧 → 0..N 个 rows 帧 → end 帧，均回显请求 streamId。
    /// </summary>
    Query = 1,
}
