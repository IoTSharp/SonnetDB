namespace SonnetDB.Kv;

/// <summary>
/// KV 条件写入结果。
/// </summary>
/// <param name="Applied">存在性条件成立且写入已提交时为 <see langword="true"/>。</param>
/// <param name="Version">成功写入后的单调版本号；条件不成立时为 <see langword="null"/>。</param>
public sealed record KvSetResult(bool Applied, long? Version);
