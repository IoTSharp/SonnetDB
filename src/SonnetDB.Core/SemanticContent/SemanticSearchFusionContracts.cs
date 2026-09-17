namespace SonnetDB.SemanticContent;

/// <summary>候选列表的融合算法。</summary>
public enum SemanticSearchFusionMode
{
    /// <summary>按各路去重后的名次计算加权倒数排名融合。</summary>
    ReciprocalRank = 0,
    /// <summary>各路分数归一化到零至一后计算加权和。</summary>
    NormalizedScore = 1,
}

/// <summary>融合、重排和持久 RAG 查询的硬预算；耗尽时拒绝返回部分结果。</summary>
public sealed record SemanticSearchFusionOptions
{
    /// <summary>创建使用默认预算的融合选项。</summary>
    public SemanticSearchFusionOptions() { }
    /// <summary>结果条数，范围一至一千。</summary>
    public int TopK { get; init; } = 10;
    /// <summary>每路允许的候选数，范围一至一万。</summary>
    public int MaxCandidatesPerSource { get; init; } = 100;
    /// <summary>各路候选条目总数上限，范围一至十万，包含重复条目。</summary>
    public int MaxTotalCandidates { get; init; } = 1_600;
    /// <summary>候选和查询的 UTF-16 字符总预算，范围一至六千四百万。</summary>
    public int MaxTextCharacters { get; init; } = 4 * 1024 * 1024;
    /// <summary>重排候选数上限；重排启用时必须大于等于 TopK。</summary>
    public int MaxRerankCandidates { get; init; } = 100;
    /// <summary>融合算法，默认倒数排名融合。</summary>
    public SemanticSearchFusionMode Mode { get; init; }
    /// <summary>倒数排名常数，范围一至一百万。</summary>
    public int RankConstant { get; init; } = 60;
    /// <summary>是否仅保留每个 ContentId 的最高分分块，默认保留不同分块。</summary>
    public bool DeduplicateByContent { get; init; }
    /// <summary>完整调用的取消期限，范围大于零至五分钟；扩展必须遵守取消令牌。</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>持久 RAG 精确向量路径最多检查的文档数，范围一至十万。</summary>
    public int MaxScannedDocuments { get; init; } = 10_000;
    /// <summary>持久 RAG 扫描的 JSON UTF-16 字符总预算，在反序列化前检查。</summary>
    public int MaxScannedJsonCharacters { get; init; } = 16 * 1024 * 1024;
    /// <summary>已发布 RAG 快照 JSON 的字节预算，在解析 profile 前检查。</summary>
    public int MaxSnapshotBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>持久 RAG 全文路径最多检查的 posting 次数，范围一至一千万。</summary>
    public long MaxPostingVisits { get; init; } = 100_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(TopK, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TopK, 1_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCandidatesPerSource, TopK);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxCandidatesPerSource, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalCandidates, MaxCandidatesPerSource);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxTotalCandidates, 100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRerankCandidates, TopK);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRerankCandidates, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTextCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxTextCharacters, 64 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(RankConstant, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RankConstant, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxScannedDocuments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxScannedDocuments, 100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxScannedJsonCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxScannedJsonCharacters, 128 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSnapshotBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSnapshotBytes, 64 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPostingVisits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPostingVisits, 10_000_000);
        if (!Enum.IsDefined(Mode) || MaxDuration <= TimeSpan.Zero || MaxDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(MaxDuration));
    }
}

/// <summary>已授权的检索候选；各路同一 Id 必须携带一致的正文和元数据。</summary>
/// <param name="Id">稳定分块标识，采用序数比较。</param>
/// <param name="ContentId">稳定内容标识。</param>
/// <param name="Text">交给重排器的正文。</param>
/// <param name="Score">有限原始分数，越大越相关。</param>
/// <param name="Source">可选来源。</param>
/// <param name="Section">可选章节。</param>
public sealed record SemanticSearchCandidate(string Id, string ContentId, string Text, double Score, string? Source = null, string? Section = null);

/// <summary>一个已授权的独立召回通道。</summary>
/// <param name="Candidates">无须预先排序的有限候选列表。</param>
/// <param name="Weight">通道权重，有限且大于零、最多一千。</param>
public sealed record SemanticSearchSource(IReadOnlyList<SemanticSearchCandidate> Candidates, double Weight = 1);

/// <summary>融合或重排后的不可变检索命中。</summary>
/// <param name="Id">稳定分块标识。</param>
/// <param name="ContentId">稳定内容标识。</param>
/// <param name="Text">原始授权候选正文。</param>
/// <param name="Source">可选来源。</param>
/// <param name="Section">可选章节。</param>
/// <param name="FusionScore">重排前融合分数。</param>
/// <param name="Score">最终分数，越大越相关。</param>
public sealed record SemanticSearchHit(string Id, string ContentId, string Text, string? Source, string? Section, double FusionScore, double Score);

/// <summary>重排器只能修改候选分数，不能替换内容。</summary>
/// <param name="Id">传入重排窗口中的候选标识。</param>
/// <param name="Score">有限重排分数，越大越相关。</param>
public sealed record SemanticSearchRerankScore(string Id, double Score);

/// <summary>由宿主显式注入的重排扩展；必须遵守取消并自行落实模型外发策略和审计。</summary>
public interface ISemanticSearchReranker
{
    /// <summary>为窗口内每个候选返回且仅返回一个有限分数；不得新增或漏掉标识。</summary>
    /// <param name="query">查询文本。</param>
    /// <param name="candidates">已授权、去重且按融合分数排序的只读窗口。</param>
    /// <param name="cancellationToken">包括完整查询期限的取消令牌。</param>
    /// <returns>窗口内全部候选的分数。</returns>
    ValueTask<IReadOnlyList<SemanticSearchRerankScore>> RerankAsync(string query, IReadOnlyList<SemanticSearchHit> candidates, CancellationToken cancellationToken);
}
