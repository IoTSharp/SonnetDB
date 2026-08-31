namespace SonnetDB.Kv;

/// <summary>
/// KV 原子取值并变更操作的结果。
/// </summary>
/// <param name="PreviousEntry">变更前可见记录的副本；key 不存在或已过期时为 <see langword="null"/>。</param>
/// <param name="MutationVersion">
/// 本次 set 或 delete 提交的单调版本号；get-and-delete 未找到可见 key 时为 <see langword="null"/>。
/// </param>
public sealed record KvExchangeResult(KvEntry? PreviousEntry, long? MutationVersion);
