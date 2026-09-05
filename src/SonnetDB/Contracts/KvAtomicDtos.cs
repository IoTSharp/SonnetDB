using SonnetDB.Kv;
using System.Globalization;
using System.Text.Json.Serialization;

namespace SonnetDB.Contracts;

/// <summary>KV 原子条件写入请求；合同版本为 v1。</summary>
/// <param name="Key">包含命名空间前缀的完整 key。</param>
/// <param name="Value">原始值字节，可为空数组，不能为 null。</param>
/// <param name="Condition">0 为无条件，1 为 NX，2 为 XX。</param>
/// <param name="ExpiresAtUtc">新值的 UTC 过期时间；为空表示移除 TTL。</param>
public sealed record KvConditionalSetRequest(string Key, byte[] Value, [property: JsonRequired] KvSetCondition Condition, DateTimeOffset? ExpiresAtUtc);

/// <summary>条件写结果；条件不满足时不写入，版本为空。</summary>
/// <param name="Applied">是否写入。</param>
/// <param name="Version">成功写入的版本。</param>
public sealed record KvConditionalSetResponse(bool Applied, long? Version)
{
    /// <summary>完整十进制版本字符串，供无法精确表示 64 位整数的客户端使用。</summary>
    public string? VersionText => Version?.ToString(CultureInfo.InvariantCulture);
}

/// <summary>原子交换或删除结果，保留旧值的存在性、版本和 TTL。</summary>
/// <param name="Previous">操作前可见值；Found 区分缺失和空值。</param>
/// <param name="MutationVersion">本次变更版本；删除缺失 key 时为空。</param>
public sealed record KvExchangeResponse(KvValueResponse Previous, long? MutationVersion)
{
    /// <summary>旧值的完整十进制版本字符串；不存在旧值时为空。</summary>
    public string? PreviousVersionText => Previous.Version?.ToString(CultureInfo.InvariantCulture);

    /// <summary>本次变更的完整十进制版本字符串；未变更时为空。</summary>
    public string? MutationVersionText => MutationVersion?.ToString(CultureInfo.InvariantCulture);
}
