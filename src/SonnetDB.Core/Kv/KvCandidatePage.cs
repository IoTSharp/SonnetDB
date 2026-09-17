namespace SonnetDB.Kv;

/// <summary>物理候选受限的范围页；continuation 包含被跳过的墓碑与过期键。</summary>
internal sealed record KvCandidatePage(
    IReadOnlyList<KvEntry> Entries,
    byte[]? ContinuationKey,
    bool HasMore,
    int CandidatesVisited);
