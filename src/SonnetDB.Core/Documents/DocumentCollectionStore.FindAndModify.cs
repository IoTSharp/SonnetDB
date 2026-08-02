namespace SonnetDB.Documents;

public sealed partial class DocumentCollectionStore
{
    /// <summary>
    /// 原子查找第一条匹配文档并应用局部更新，同时返回更新前或更新后文档。
    /// </summary>
    /// <param name="filter">过滤条件；为空时匹配集合中的第一条文档。</param>
    /// <param name="update">局部更新操作符。</param>
    /// <param name="returnDocument">返回更新前或更新后文档。</param>
    /// <param name="upsert">未匹配时是否插入新文档。</param>
    /// <param name="upsertId">upsert 文档 ID；为空时尝试从过滤条件推断。</param>
    /// <returns>返回文档与统一写入结果。</returns>
    public DocumentFindOneAndUpdateResult FindOneAndUpdate(
        DocumentFilter? filter,
        DocumentUpdate update,
        DocumentReturnDocument returnDocument = DocumentReturnDocument.Before,
        bool upsert = false,
        string? upsertId = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!Enum.IsDefined(returnDocument))
            throw new ArgumentOutOfRangeException(nameof(returnDocument), returnDocument, "不支持的返回文档模式。");

        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            IReadOnlyList<DocumentRow> matches = FindMatchingRowsLocked(filter, limit: 1);
            DocumentRow? before = matches.Count == 0 ? null : matches[0];
            string? resolvedUpsertId = before is null && upsert
                ? upsertId ?? DocumentUpdateExecutor.TryInferUpsertId(filter)
                : null;

            DocumentWriteResult writeResult = before is null
                ? UpsertFromUpdateLocked(filter, update, upsert, resolvedUpsertId ?? upsertId)
                : ApplyUpdateRowsLocked([before], update);
            if (writeResult.HasErrors)
                return new DocumentFindOneAndUpdateResult(null, writeResult);

            if (returnDocument == DocumentReturnDocument.Before)
                return new DocumentFindOneAndUpdateResult(before, writeResult);

            string? resultId = before?.Id ?? resolvedUpsertId;
            DocumentRow? after = resultId is null
                ? null
                : TryGetByDocumentKeyLocked(DocumentIndexCodec.EncodeDocumentKey(resultId));
            return new DocumentFindOneAndUpdateResult(after, writeResult);
        }
    }
}
