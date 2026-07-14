using System.Runtime.CompilerServices;
using SonnetDB.Documents;

namespace SonnetDB.Data.Documents;

/// <summary>
/// 按只读快照逐页读取 Document Store 查询结果的客户端游标。
/// </summary>
public sealed class SndbDocumentCursor
{
    private readonly SndbDocumentClient _client;
    private readonly string _collection;
    private readonly SndbDocumentFindOptions _options;
    private int _readInProgress;
    private bool _started;

    internal SndbDocumentCursor(
        SndbDocumentClient client,
        string collection,
        SndbDocumentFindOptions options)
    {
        _client = client;
        _collection = collection;
        _options = options;
    }

    /// <summary>最近一次成功读取的页；尚未读取时为 null。</summary>
    public SndbDocumentPage? Current { get; private set; }

    /// <summary>游标是否已读取到末尾。</summary>
    public bool IsExhausted { get; private set; }

    /// <summary>下一页 continuation token；首次读取前为查询选项中的 token。</summary>
    public string? ContinuationToken => _started ? Current?.ContinuationToken : _options.ContinuationToken;

    /// <summary>
    /// 读取下一页。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取到非空页时返回 <c>true</c>；游标结束时返回 <c>false</c>。</returns>
    public async Task<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _readInProgress, 1) != 0)
        {
            throw new DocumentCursorException(
                DocumentCursorErrorCodes.ConcurrentRead,
                "同一个 document cursor 不能并发读取。");
        }

        try
        {
            while (!IsExhausted)
            {
                var options = _started
                    ? _options with { ContinuationToken = Current?.ContinuationToken, Skip = 0 }
                    : _options;

                Current = await _client.FindPageAsync(_collection, options, cancellationToken).ConfigureAwait(false);
                _started = true;
                IsExhausted = !Current.HasMore;
                if (Current.Documents.Count > 0)
                    return true;
            }

            return false;
        }
        finally
        {
            Volatile.Write(ref _readInProgress, 0);
        }
    }

    /// <summary>
    /// 从游标当前位置开始异步枚举剩余文档，不重放 <see cref="Current"/> 中已经读取的页。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按查询排序逐条返回的异步序列。</returns>
    public async IAsyncEnumerable<SndbDocument> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in Current!.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return document;
            }
        }
    }
}
