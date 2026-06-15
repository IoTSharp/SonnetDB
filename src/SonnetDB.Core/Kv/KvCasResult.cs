namespace SonnetDB.Kv;

/// <summary>
/// KV 乐观锁比较并交换结果。
/// </summary>
/// <param name="Succeeded">比较版本匹配并完成写入时为 <see langword="true"/>。</param>
/// <param name="CurrentVersion">操作时观察到的当前版本；key 不存在时为 0。</param>
/// <param name="NewVersion">成功写入后的新版本；失败时为 <see langword="null"/>。</param>
public sealed record KvCasResult(bool Succeeded, long CurrentVersion, long? NewVersion);
