using SonnetDB.Exceptions;
using SonnetDB.FullText;

namespace SonnetDB.Documents;

public sealed partial class DocumentCollectionStore
{
    /// <summary>
    /// 在同次集合读取视图内，先执行结构过滤，再执行全文查询并返回全部命中。
    /// 扫描、文本和 posting 均有预算；超过预算时抛错，不返回候选窗口或部分总数。
    /// 此视图只覆盖本次调用，不为后续分页保留快照。
    /// </summary>
    /// <param name="index">全文索引声明。</param>
    /// <param name="field">索引字段或星号。</param>
    /// <param name="queryText">非空查询文本，最多 4096 字符。</param>
    /// <param name="filter">可选结构过滤条件。</param>
    /// <param name="mode">精确或模糊模式。</param>
    /// <param name="queryKind">全部、任意或短语组合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="maxDocuments">最多扫描文档数，过滤掉的文档也计费。</param>
    /// <param name="maxDocumentBytes">扫描的 ID 和 JSON 的 UTF-16 字节预算，不是进程 heap 上限。</param>
    /// <param name="maxPostingVisits">最多 posting 检查次数。</param>
    /// <returns>完整命中及同次读取的过滤文档，用于精确总数和 facet。</returns>
    public DocumentFullTextCompleteSearchResult SearchFullTextComplete(
        DocumentFullTextIndex index,
        string field,
        string queryText,
        DocumentFilter? filter = null,
        FullTextSearchMode mode = FullTextSearchMode.Exact,
        FullTextQueryKind queryKind = FullTextQueryKind.All,
        CancellationToken cancellationToken = default,
        int maxDocuments = 100_000,
        long maxDocumentBytes = 64L * 1024 * 1024,
        long maxPostingVisits = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDocuments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPostingVisits);
        if (queryText.Length > 4096)
            throw new FullTextSearchBudgetExceededException("全文查询超过 4096 字符预算。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new FullTextSearchBudgetExceededException("全文查询超过 10 秒执行预算。");
        }

        bool entered = false;
        try
        {
            // 有界等待集合租约，并在每个短等待后响应取消。
            for (int attempt = 0; attempt < 200 && !entered; attempt++)
            {
                CheckBudget();
                entered = Monitor.TryEnter(_sync, 50);
            }
            CheckBudget();
            if (!entered)
                throw new FullTextSearchBudgetExceededException("全文查询等待集合锁超过预算。");

            var rows = new Dictionary<string, DocumentRow>(StringComparer.Ordinal);
            int scanned = 0;
            long bytes = 0;
            string? afterId = null;
            long visibleAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int batch = 0; batch <= maxDocuments / 64; batch++)
            {
                CheckBudget();
                int take = Math.Min(64, maxDocuments - scanned + 1);
                IReadOnlyList<DocumentRow> page = afterId is null
                    ? ScanRowsLocked(take)
                    : ScanRowsAfterLocked(afterId, take);
                foreach (DocumentRow row in page)
                {
                    CheckBudget();
                    if (++scanned > maxDocuments)
                        throw new FullTextSearchBudgetExceededException($"全文查询超过 {maxDocuments} 文档扫描预算。");
                    bytes += ((long)row.Id.Length + row.Json.Length) * sizeof(char);
                    if (bytes > maxDocumentBytes)
                        throw new FullTextSearchBudgetExceededException($"全文查询超过 {maxDocumentBytes} 文本文档字节预算。");
                    if (!IsExpired(row, _schema.Indexes, visibleAt)
                        && (filter is null || DocumentQueryPlanner.Matches(filter, row)))
                        rows.Add(row.Id, row);
                }
                if (page.Count < take)
                    break;
                afterId = page[^1].Id;
            }

            CheckBudget();
            DocumentFullTextIndexStore store = OpenFullTextStoreLocked(index, rebuildIfMissing: false);
            DocumentFullTextFilteredSearchResult search = store.SearchFiltered(
                field, queryText, rows.Count, rows.Keys.ToHashSet(StringComparer.Ordinal),
                maxPostingVisits, linked.Token, mode, queryKind);
            CheckBudget();
            if (search.PostingBudgetExceeded)
                throw new FullTextSearchBudgetExceededException($"全文查询超过 {maxPostingVisits} posting 检查预算。");
            return new DocumentFullTextCompleteSearchResult(search.Hits, rows);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new FullTextSearchBudgetExceededException("全文查询超过 10 秒执行预算。");
        }
        finally
        {
            if (entered)
                Monitor.Exit(_sync);
        }
    }
}

/// <summary>一次有界全文查询的完整过滤结果，不跨调用保留读取快照。</summary>
/// <param name="Hits">全部文本及结构过滤命中，按分数降序、ID 升序排列。</param>
/// <param name="Rows">同次读取的结构过滤文档，供命中投影与精确 facet 计算。</param>
public sealed record DocumentFullTextCompleteSearchResult(
    IReadOnlyList<DocumentFullTextSearchHit> Hits,
    IReadOnlyDictionary<string, DocumentRow> Rows);
