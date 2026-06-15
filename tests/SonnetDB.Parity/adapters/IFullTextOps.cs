namespace SonnetDB.Parity.Adapters;

/// <summary>
/// 全文检索支柱的语义操作集合。
/// </summary>
public interface IFullTextOps
{
    /// <summary>重建场景索引。</summary>
    Task ResetIndexAsync(string index, FullTextIndexOptions options, CancellationToken ct);

    /// <summary>批量 upsert 文档。</summary>
    Task UpsertAsync(string index, IReadOnlyList<FullTextDocument> documents, CancellationToken ct);

    /// <summary>删除指定文档。</summary>
    Task DeleteDocumentAsync(string index, string id, CancellationToken ct);

    /// <summary>执行全文检索。</summary>
    Task<IReadOnlyList<FullTextHit>> SearchAsync(string index, FullTextSearchRequest request, CancellationToken ct);

    /// <summary>返回索引中文档数量。</summary>
    Task<long> CountDocumentsAsync(string index, CancellationToken ct);
}

/// <summary>
/// 不支持全文检索能力的空操作对象。
/// </summary>
public sealed class UnsupportedFullTextOps : IFullTextOps
{
    /// <summary>共享实例。</summary>
    public static UnsupportedFullTextOps Instance { get; } = new();

    private UnsupportedFullTextOps() { }

    /// <inheritdoc />
    public Task ResetIndexAsync(string index, FullTextIndexOptions options, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task UpsertAsync(string index, IReadOnlyList<FullTextDocument> documents, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task DeleteDocumentAsync(string index, string id, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task<IReadOnlyList<FullTextHit>> SearchAsync(string index, FullTextSearchRequest request, CancellationToken ct)
        => Unsupported<IReadOnlyList<FullTextHit>>();

    /// <inheritdoc />
    public Task<long> CountDocumentsAsync(string index, CancellationToken ct) => Unsupported<long>();

    private static Task Unsupported()
        => throw new NotSupportedException("当前后端不支持全文检索操作。");

    private static Task<T> Unsupported<T>()
        => throw new NotSupportedException("当前后端不支持全文检索操作。");
}

/// <summary>
/// 全文索引选项。
/// </summary>
/// <param name="Tokenizer">SonnetDB tokenizer 名称。</param>
/// <param name="FilterableFields">可过滤字段。</param>
public sealed record FullTextIndexOptions(string Tokenizer, IReadOnlyList<string> FilterableFields);

/// <summary>
/// 规范化全文文档。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Title">标题。</param>
/// <param name="Body">正文。</param>
/// <param name="Category">分类。</param>
/// <param name="Tags">标签。</param>
public sealed record FullTextDocument(string Id, string Title, string Body, string Category, IReadOnlyList<string> Tags);

/// <summary>
/// 规范化全文查询请求。
/// </summary>
/// <param name="Query">查询文本。</param>
/// <param name="TopK">返回数量上限。</param>
/// <param name="CategoryFilter">可选分类过滤。</param>
/// <param name="TypoTolerant">是否请求 typo-tolerant 语义。</param>
public sealed record FullTextSearchRequest(string Query, int TopK, string? CategoryFilter = null, bool TypoTolerant = false);

/// <summary>
/// 规范化全文命中。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Score">相关性分数。</param>
/// <param name="Category">分类。</param>
public sealed record FullTextHit(string Id, double Score, string? Category);
