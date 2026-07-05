namespace SonnetDB.Protocol;

/// <summary>
/// 二进制帧的目标 service。全部 7 个 service 编号现在保留，
/// 后续 PR（#237~#240）逐个挂载 opcode，不再重编号。
/// </summary>
public enum FrameService : byte
{
    /// <summary>消息队列（SonnetMQ）。</summary>
    Mq = 1,

    /// <summary>时序（measurement 批量写等，#237 预留）。</summary>
    Tsdb = 2,

    /// <summary>SQL / 关系查询（流式列式结果集，#238）。</summary>
    Sql = 3,

    /// <summary>向量检索（#239 预留）。</summary>
    Vector = 4,

    /// <summary>KV keyspace（#240 预留）。</summary>
    Kv = 5,

    /// <summary>对象存储（#240 预留）。</summary>
    Object = 6,

    /// <summary>文档集合（#240 预留）。</summary>
    Doc = 7,
}
