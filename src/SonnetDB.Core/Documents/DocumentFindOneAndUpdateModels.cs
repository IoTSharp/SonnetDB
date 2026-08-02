namespace SonnetDB.Documents;

/// <summary>
/// 原子查找并更新操作返回文档的模式。
/// </summary>
public enum DocumentReturnDocument
{
    /// <summary>返回更新前文档；upsert 时为空。</summary>
    Before,

    /// <summary>返回更新后或 upsert 新建的文档。</summary>
    After,
}

/// <summary>
/// 原子查找并更新单条文档的结果。
/// </summary>
/// <param name="Document">按请求选择返回的 before/after 文档。</param>
/// <param name="WriteResult">统一文档写入结果。</param>
public sealed record DocumentFindOneAndUpdateResult(
    DocumentRow? Document,
    DocumentWriteResult WriteResult);
