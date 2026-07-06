namespace SonnetDB.Protocol;

/// <summary>
/// vector service（<see cref="FrameService.Vector"/>）的 opcode（M28 P5b #239）。
/// </summary>
public enum VectorFrameOp : byte
{
    /// <summary>
    /// measurement 向量列 KNN 检索。查询向量以紧凑二进制（f32 LE × 维度）传输，
    /// 响应为流式列式结果集（与 sql service 同一块布局）：meta 帧 → 0..N 个 rows 帧 → end 帧，
    /// 均回显请求 streamId，帧头 service/op 为 vector/search。
    /// </summary>
    Search = 1,
}
