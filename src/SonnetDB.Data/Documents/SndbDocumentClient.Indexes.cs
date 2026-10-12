using SonnetDB.Documents;

namespace SonnetDB.Data.Documents;

public sealed partial class SndbDocumentClient
{
    /// <summary>幂等创建普通 JSON path 索引，已有同名索引必须具有相同定义。</summary>
    /// <param name="collection">集合名称。</param>
    /// <param name="indexName">索引名称。</param>
    /// <param name="paths">按顺序排列的 JSON path。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已有或创建的索引名称。</returns>
    public async Task<string> EnsureIndexAsync(string collection, string indexName, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCollection(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            throw new ArgumentException("索引必须包含至少一个 path。", nameof(paths));
        string[] normalized = paths.Select(path => JsonPath.Parse(path).Text).ToArray();
        if (_embedded is not null)
            return _embedded.Documents.EnsureIndex(collection, indexName, normalized).Name;
        using var response = await PostJsonAsync(CollectionActionUrl(collection, "indexes/ensure"),
            new DocumentIndexEnsureRequest(indexName, normalized), SndbDocumentClientJsonContext.Default.DocumentIndexEnsureRequest,
            cancellationToken).ConfigureAwait(false);
        DocumentIndexEnsureResponse body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentIndexEnsureResponse, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(body.Collection, collection, StringComparison.Ordinal) || !string.Equals(body.Index, indexName, StringComparison.Ordinal)
            || !string.Equals(body.Status, "ready", StringComparison.Ordinal)
            || body.Paths is null || !body.Paths.SequenceEqual(normalized, StringComparer.Ordinal))
            throw new InvalidDataException("Document 索引响应的集合、名称或 path 不匹配。");
        return body.Index;
    }
}

internal sealed record DocumentIndexEnsureRequest(string Name, IReadOnlyList<string> Paths);
internal sealed record DocumentIndexEnsureResponse(string Collection, string Index, string Status, IReadOnlyList<string>? Paths = null);
