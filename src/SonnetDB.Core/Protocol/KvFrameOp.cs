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

    /// <summary>原子条件写入（Always / NX / XX）。</summary>
    SetConditional = 4,

    /// <summary>原子读取旧记录并写入新值。</summary>
    GetAndSet = 5,

    /// <summary>原子读取并删除记录。</summary>
    GetAndDelete = 6,

    /// <summary>按版本比较并交换。</summary>
    CompareAndSet = 7,

    /// <summary>更新绝对 UTC 过期时间。</summary>
    Expire = 8,

    /// <summary>移除过期时间。</summary>
    Persist = 9,

    /// <summary>读取剩余 TTL。</summary>
    GetTimeToLive = 10,
}
