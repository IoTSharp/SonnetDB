using System.Collections.Generic;

namespace SonnetDB.FullText.Index;

/// <summary>
/// 全文索引接口。
/// </summary>
public interface IFullTextIndex
{
    /// <summary>
    /// 写入或更新一个文档。
    /// </summary>
    void Index(Document document);

    /// <summary>
    /// 删除一个文档。
    /// </summary>
    /// <returns>是否实际删除了一个文档。</returns>
    bool Delete(DocumentId id);

    /// <summary>
    /// 当前可见文档总数（不含 tombstone）。
    /// </summary>
    int DocumentCount { get; }

    /// <summary>
    /// 执行检索。
    /// </summary>
    /// <param name="query">已解析的查询。</param>
    /// <param name="topK">返回前 K 条命中。</param>
    IReadOnlyList<SearchHit> Search(Query.Query query, int topK);
}
