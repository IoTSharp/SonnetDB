namespace SonnetDB.FullText.Index;

/// <summary>
/// 单条命中：文档主键 + 评分。
/// </summary>
/// <param name="DocumentId">命中文档主键。</param>
/// <param name="Score">BM25 评分；越大越相关。</param>
public readonly record struct SearchHit(DocumentId DocumentId, double Score);
