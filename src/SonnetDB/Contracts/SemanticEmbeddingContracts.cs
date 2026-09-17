using SonnetDB.SemanticContent;

namespace SonnetDB.Contracts;

/// <summary>对 Object Bucket 中固定版本内容生成向量的请求。</summary>
/// <param name="Object">包含版本或 ETag 的对象引用；不接受远程 URL。</param>
public sealed record ObjectEmbeddingRequest(SemanticObjectReference Object);

/// <summary>对象 embedding 结果，不自动建立检索索引。</summary>
/// <param name="Object">本次实际读取的对象版本。</param>
/// <param name="Provider">执行 provider。</param>
/// <param name="Profile">输出向量空间。</param>
/// <param name="Embedding">本次向量。</param>
public sealed record ObjectEmbeddingResponse(SemanticObjectReference Object, string Provider,
    string Profile, float[] Embedding);

/// <summary>脱敏的 provider 调用记录；不保存文本、图片、向量、对象路径或异常消息。</summary>
/// <param name="CallId">调用标识。</param>
/// <param name="StartedUtc">开始时间。</param>
/// <param name="Principal">调用主体；后台任务标记为 background。</param>
/// <param name="Provider">provider 标识。</param>
/// <param name="Profile">向量空间。</param>
/// <param name="InputKind">text、image 或 object。</param>
/// <param name="InputBytes">输入字节数。</param>
/// <param name="ObjectIdentityHash">对象版本身份摘要，不包含内容。</param>
/// <param name="EgressMode">生效的外发模式。</param>
/// <param name="IsLocal">provider 是否保证本地执行。</param>
/// <param name="Status">started、succeeded、failed、denied、cancelled 或 timed_out。</param>
/// <param name="DurationMilliseconds">执行耗时。</param>
/// <param name="ErrorCode">稳定错误码，不记录 provider 异常消息。</param>
public sealed record SemanticEmbeddingAuditEntry(Guid CallId, DateTimeOffset StartedUtc,
    string Principal, string Provider, string Profile, string InputKind, int InputBytes,
    string? ObjectIdentityHash, SemanticDataEgressMode EgressMode, bool IsLocal,
    string Status, long DurationMilliseconds, string? ErrorCode);

/// <summary>有界的调用审计页，按开始时间倒序返回。</summary>
/// <param name="Entries">本页记录。</param>
/// <param name="ContinuationToken">后续页令牌；为空表示本次扫描没有后续记录。</param>
public sealed record SemanticEmbeddingAuditPage(IReadOnlyList<SemanticEmbeddingAuditEntry> Entries,
    string? ContinuationToken);
