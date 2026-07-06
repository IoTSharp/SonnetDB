namespace SonnetDB.Protocol;

/// <summary>
/// kv service（<see cref="FrameService.Kv"/>）的 opcode（M28 P5b #240）。
/// </summary>
public enum KvFrameOp : byte
{
    /// <summary>读取单个 key（含 version / 过期时间元数据）。</summary>
    Get = 1,

    /// <summary>写入或覆盖单个 key（可选过期时间）。</summary>
    Put = 2,

    /// <summary>按 key 前缀扫描（可选起始 key 之后分页）。</summary>
    Scan = 3,
}
