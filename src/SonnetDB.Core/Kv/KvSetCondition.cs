namespace SonnetDB.Kv;

/// <summary>
/// KV <c>Set</c> 操作的存在性条件。
/// </summary>
public enum KvSetCondition
{
    /// <summary>无论 key 是否存在都写入。</summary>
    Always = 0,

    /// <summary>仅当 key 不存在或已过期时写入，对应 NX 语义。</summary>
    IfNotExists = 1,

    /// <summary>仅当 key 存在且未过期时写入，对应 XX 语义。</summary>
    IfExists = 2,
}
