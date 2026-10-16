namespace SonnetDB.Protocol;

/// <summary>
/// 二进制帧的目标 service。既有 1~7 编号保持不变；后续服务仅向末尾扩展。
/// </summary>
public enum FrameService : byte
{
    /// <summary>消息队列（SonnetMQ）。</summary>
    Mq = 1,

    /// <summary>时序（measurement 列式批量写，#237）。</summary>
    Tsdb = 2,

    /// <summary>SQL / 关系查询（流式列式结果集，#238）。</summary>
    Sql = 3,

    /// <summary>向量检索（#239）。</summary>
    Vector = 4,

    /// <summary>KV keyspace（#240）。</summary>
    Kv = 5,

    /// <summary>对象存储（#240）。</summary>
    Object = 6,

    /// <summary>文档集合（#240）。</summary>
    Doc = 7,

    /// <summary>原生属性图（M40 #351）。</summary>
    Graph = 8,
}
