using System.Text;
using SonnetDB.Kv;

namespace SonnetDB.Documents;

/// <summary>按调用方原始 JSON 字节预算读取的文档页。</summary>
/// <param name="Documents">当前可见的独立文档结果。</param>
/// <param name="Truncated">是否还有未检查的文档。</param>
/// <param name="NextId">下一页使用的文档 ID，不跳过未读取的超限文档。</param>
public sealed record DocumentBoundedReadPage(IReadOnlyList<DocumentRow> Documents, bool Truncated, string? NextId);

public sealed partial class DocumentCollectionStore
{
    /// <summary>在读取或解码原始 JSON 前检查单文档字节预算；不执行 TTL 删除。</summary>
    /// <param name="id">文档标识符。</param>
    /// <param name="maxDocumentBytes">单文档原始 JSON 最大字节数。</param>
    /// <param name="cancellationToken">调用取消令牌。</param>
    /// <returns>可见文档；不存在或过期时为空。</returns>
    public DocumentRow? GetBounded(string id, int maxDocumentBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_sync)
        {
            var entry = _keyspace.GetEntryBounded(DocumentIndexCodec.EncodeDocumentKey(id), maxDocumentBytes, cancellationToken);
            if (entry is null)
                return null;
            var row = new DocumentRow(id, Encoding.UTF8.GetString(entry.Value.Span), entry.Version);
            return _schema.Indexes.Any(static index => index.IsTtl) && IsExpired(row, _schema.Indexes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ? null : row;
        }
    }

    /// <summary>按页和单文档预算读取，不预物化 limit+1 文档，也不执行 TTL 删除。</summary>
    /// <param name="afterId">上一页文档 ID。</param>
    /// <param name="maxRows">最多检查和返回的文档条数。</param>
    /// <param name="maxDocumentBytes">单文档原始 JSON 字节上限。</param>
    /// <param name="maxPageBytes">本页原始 key/JSON 总字节上限。</param>
    /// <param name="cancellationToken">调用取消令牌。</param>
    /// <returns>包含真实继续位置的预算页。</returns>
    public DocumentBoundedReadPage ScanAfterBounded(string? afterId, int maxRows, int maxDocumentBytes, int maxPageBytes, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            byte[] after = afterId is null ? [] : DocumentIndexCodec.EncodeDocumentKey(afterId);
            KvBoundedReadPage page = _keyspace.ScanPrefixBounded(new byte[] { (byte)'d' }, after, maxRows,
                maxDocumentBytes, maxPageBytes, cancellationToken);
            var documents = new List<DocumentRow>(page.Entries.Count);
            foreach (var entry in page.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = DocumentIndexCodec.DecodeIdFromDocumentKey(entry.Key);
                var row = new DocumentRow(id, Encoding.UTF8.GetString(entry.Value.Span), entry.Version);
                if (!_schema.Indexes.Any(static index => index.IsTtl) || !IsExpired(row, _schema.Indexes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                    documents.Add(row);
            }
            return new(documents, page.Truncated,
                page.NextKey is null ? null : DocumentIndexCodec.DecodeIdFromDocumentKey(page.NextKey));
        }
    }
}
