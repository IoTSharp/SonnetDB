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

    /// <summary>
    /// 执行一条自动提交的写语句（INSERT / UPDATE / DELETE 或受支持的 DDL）。
    /// 响应为单个 committed 结果帧；RETURNING、事务控制和多结果语句显式拒绝。
    /// </summary>
    Execute = 2,

    /// <summary>类型化存储过程调用；命名参数、多个结果与完整输出终态，不回落文本 CALL。</summary>
    Procedure = 3,
}
