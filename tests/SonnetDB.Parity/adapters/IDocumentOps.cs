namespace SonnetDB.Parity.Adapters;

/// <summary>
/// Document Store 参考 parity 的最小语义契约。两端分别由 SonnetDB 自有 API 与 MongoDB 官方 Driver 实现。
/// </summary>
public interface IDocumentOps : IAsyncDisposable
{
    /// <summary>后端稳定名称。</summary>
    string BackendName { get; }

    /// <summary>重建空集合。</summary>
    Task ResetCollectionAsync(string collection, CancellationToken ct);

    /// <summary>批量插入文档。</summary>
    Task InsertManyAsync(string collection, IReadOnlyList<DocumentParityRecord> documents, CancellationToken ct);

    /// <summary>尝试插入单条文档，唯一键冲突时返回 false。</summary>
    Task<bool> TryInsertAsync(string collection, DocumentParityRecord document, CancellationToken ct);

    /// <summary>执行过滤、投影、排序和分页查询。</summary>
    Task<IReadOnlyList<DocumentParityRecord>> FindAsync(string collection, DocumentParityQuery query, CancellationToken ct);

    /// <summary>对匹配文档执行局部更新。</summary>
    Task<int> UpdateAsync(string collection, DocumentParityPredicate predicate, DocumentParityUpdate update, bool many, CancellationToken ct);

    /// <summary>删除匹配文档。</summary>
    Task<int> DeleteAsync(string collection, DocumentParityPredicate predicate, bool many, CancellationToken ct);

    /// <summary>创建单字段索引。</summary>
    Task CreateIndexAsync(string collection, DocumentParityIndex index, CancellationToken ct);

    /// <summary>按字段分组并计算 count 与 average。</summary>
    Task<IReadOnlyList<DocumentParityAggregateRow>> AggregateAsync(
        string collection,
        string groupPath,
        string averagePath,
        CancellationToken ct);

    /// <summary>返回集合文档数。</summary>
    Task<long> CountAsync(string collection, CancellationToken ct);

    /// <summary>关闭并重新打开当前后端连接或数据库实例。</summary>
    Task RestartAsync(CancellationToken ct);

    /// <summary>校验指定索引及其主数据的一致性。</summary>
    Task<DocumentParityIndexState> VerifyIndexAsync(string collection, string indexName, CancellationToken ct);
}

/// <summary>规范化文档。</summary>
public sealed record DocumentParityRecord(string Id, string Json);

/// <summary>规范化字段谓词。</summary>
public sealed record DocumentParityPredicate(string Path, DocumentParityOperator Operator, object? Value);

/// <summary>参考 parity 支持的比较操作符。</summary>
public enum DocumentParityOperator
{
    /// <summary>等于。</summary>
    Equal,
    /// <summary>大于等于。</summary>
    GreaterThanOrEqual,
}

/// <summary>规范化查询。</summary>
public sealed record DocumentParityQuery(
    DocumentParityPredicate? Predicate = null,
    IReadOnlyList<string>? Projection = null,
    string? SortPath = null,
    bool Descending = false,
    int? Limit = null);

/// <summary>规范化局部更新。</summary>
public sealed record DocumentParityUpdate(
    IReadOnlyDictionary<string, object?>? Set = null,
    IReadOnlyList<string>? Unset = null,
    IReadOnlyDictionary<string, object?>? Increment = null,
    IReadOnlyDictionary<string, string>? Rename = null,
    IReadOnlyDictionary<string, object?>? Push = null,
    IReadOnlyDictionary<string, object?>? AddToSet = null);

/// <summary>规范化单字段索引。</summary>
public sealed record DocumentParityIndex(string Name, string Path, bool Unique = false, long? TtlSeconds = null);

/// <summary>规范化分组聚合行。</summary>
public sealed record DocumentParityAggregateRow(string Group, long Count, double Average);

/// <summary>索引一致性结果。</summary>
public sealed record DocumentParityIndexState(bool IsConsistent, long DocumentCount, long IndexedDocumentCount);
